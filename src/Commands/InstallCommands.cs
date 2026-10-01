using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// 安装器：官方桌面端（NSIS 静默安装）与 CLI（npm 包 @deepseek-ai/dsh）。
    /// 全流程自带：源解析 → 完整性校验（SHA512 + 数字签名）→ 清残留 → 备份信息 →
    /// 静默安装 → 版本校验 → 启动；失败时给出明确原因与回滚方式，绝不谎报成功。
    /// </summary>
    public static class InstallCommands
    {
        const string CliPackage = "@deepseek-ai/dsh";
        const string PublisherHint = "Hangzhou DeepSeek Artificial Intelligence Co., Ltd.";

        public static void Register()
        {
            Registry.Add("install.desktop", L.T("安装/升级官方桌面端（下载→校验→清残留→静默安装→校验→启动）", "Install/upgrade the official desktop app (download → verify → kill leftovers → silent install → verify version → launch)"),
                "install desktop [--file <exe>] [--url <url>] [--sha512 <b64>] [--dry-run] [--yes] [--no-start]",
                RunDesktop, examples: new[] { "dsh-toolbox install.desktop --dry-run --json", "dsh-toolbox install.desktop --yes" });

            Registry.Add("install.cli", L.T("安装官方 CLI（npm 包 @deepseek-ai/dsh，用户级，不需要管理员）", "Install the official CLI (npm package @deepseek-ai/dsh, user-level, no admin needed)"),
                "install cli [--version <v>] [--file <tgz>] [--registry <url>] [--dry-run] [--yes]",
                RunCli, examples: new[] { "dsh-toolbox install.cli --dry-run --json", "dsh-toolbox install.cli --yes" });

            Registry.Add("install.verify", L.T("只做校验：对已有安装包验证 SHA512 与数字签名（不安装）", "Verify only: check SHA512 and the digital signature of an existing package (no install)"),
                L.T("install verify --file <exe> [--sha512 <b64>] [--publisher <名>]", "install verify --file <exe> [--sha512 <b64>] [--publisher <name>]"),
                RunVerify, examples: new[] { "dsh-toolbox install.verify --file x.exe --json" });
        }

        // ================================================================ install.desktop
        static int RunDesktop(Ctx ctx)
        {
            var desktop = HostCommands.DesktopInfo();
            string installed = H.S(desktop, "version");
            string installDir = H.S(desktop, "installDir");
            string exe = H.S(desktop, "exe");

            // 解析来源：本地文件 > 显式 URL > 官方 feed
            string file = ctx.Get("file");
            string url = ctx.Get("url");
            string sha = ctx.Get("sha512");
            long expectSize = ctx.Args.GetLong("size", 0);
            string version = ctx.Get("version");
            string latest = null;

            if (string.IsNullOrEmpty(file))
            {
                var feed = ReadFeed();
                if (url == null) url = H.S(feed, "url");
                if (sha == null) sha = H.S(feed, "sha512");
                latest = H.S(feed, "latest");
                if (expectSize == 0) expectSize = H.L(feed, "size");
                if (string.IsNullOrEmpty(version)) version = latest;
                if (string.IsNullOrEmpty(url))
                    throw new ToolException("E_NO_SOURCE", L.T("无法从官方源获取安装包地址", "Cannot get a package URL from the official feed"), L.T("用 --file 指定本地安装包，或 --url 指定下载地址", "Use --file for a local package, or --url for a download address"), ExitCodes.Error);
            }

            // 幂等：已是目标版本且进程健康
            if (!ctx.Flag("force") && !string.IsNullOrEmpty(version) && string.Equals(version, installed, StringComparison.OrdinalIgnoreCase))
            {
                int running = HostCommands.ProcessesByName(HostCommands.AppDirName).Count;
                ctx.Out.Result("install.desktop", Json.Obj(
                    "verdict", true, "skipped", true, "reason", L.T("已是最新版本", "Already up to date"),
                    "installedVersion", installed, "targetVersion", version, "processCount", running,
                    "detail", L.T("桌面端已是 ", "Desktop app is already ") + installed + L.T("，无需安装（--force 可强制重装）", ", no install needed (use --force to reinstall)")));
                return ExitCodes.Ok;
            }

            var plan = Json.Obj(
                "source", !string.IsNullOrEmpty(file) ? "file" : (url != null && url.StartsWith("http") ? "url" : "feed"),
                "file", file, "url", url, "sha512", sha, "size", expectSize,
                "installedVersion", installed, "targetVersion", version,
                "installDir", installDir, "steps",
                new[] { L.T("下载(如需)", "Download (if needed)"), L.T("校验大小/SHA512/数字签名", "Verify size/SHA512/digital signature"), L.T("结束残留进程", "Kill leftover processes"), L.T("静默安装", "Silent install"), L.T("校验版本", "Verify version"), L.T("启动应用", "Launch the app") });

            if (ctx.DryRun)
            {
                ctx.Out.Result("install.desktop", Json.Obj("dryRun", true, "plan", plan,
                    "detail", L.T("将从 ", "Installing from ") + (file ?? url) + L.T(" 安装，目标版本 ", ", target version ") + (version ?? L.T("未知", "unknown"))));
                return ExitCodes.Ok;
            }
            ctx.ConfirmDestructive(L.T("安装/升级官方桌面端（会关闭正在运行的 DSH）", "Install/upgrade the official desktop app (this closes the running DSH)"));

            // 1) 取包
            string pkg = file;
            if (string.IsNullOrEmpty(pkg))
            {
                string tmp = Path.Combine(Path.GetTempPath(), "dsh-toolbox-install-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                Directory.CreateDirectory(tmp);
                pkg = Path.Combine(tmp, Path.GetFileName(new Uri(url).AbsolutePath));
                ctx.Out.Line(L.T("下载 ", "Downloading ") + url + " …");
                Download(url, pkg, ctx);
            }
            if (!File.Exists(pkg)) throw ToolException.NotFound(L.T("安装包不存在：", "Package not found: ") + pkg);
            long actualSize = new FileInfo(pkg).Length;

            // 2) 校验
            var checks = new List<object>();
            if (expectSize > 0 && actualSize != expectSize)
                throw new ToolException("E_SIZE_MISMATCH",
                    string.Format(L.T("安装包大小不符：期望 {0}，实际 {1}", "Package size mismatch: expected {0}, got {1}"), expectSize, actualSize),
                    L.T("下载可能不完整或是镜像不一致，请重试或改用 --file", "The download may be incomplete or the mirror inconsistent; retry or use --file"), ExitCodes.Error);
            checks.Add(Json.Obj("check", "size", "ok", true, "detail", Fs.FormatSize(actualSize)));

            if (!string.IsNullOrEmpty(sha))
            {
                string actual = Sha512Base64(pkg, ctx.Cancel);
                bool ok = string.Equals(actual, sha.Trim(), StringComparison.Ordinal);
                checks.Add(Json.Obj("check", "sha512", "ok", ok, "detail", ok ? L.T("一致", "match") : (L.T("不一致：期望 ", "mismatch: expected ") + sha.Trim().Substring(0, 16) + L.T("… 实际 ", "… got ") + actual.Substring(0, 16) + "…")));
                if (!ok) throw new ToolException("E_HASH_MISMATCH", L.T("SHA-512 校验失败", "SHA-512 verification failed"), L.T("安装包已损坏或被替换，请重新下载", "The package is corrupt or has been replaced; download it again"), ExitCodes.Error);
            }

            var sig = VerifySignature(ctx, pkg, PublisherHint);
            checks.Add(Json.Obj("check", "signature", "ok", H.B(sig, "ok"), "detail", H.S(sig, "status") + " / " + H.S(sig, "signer")));
            if (!H.B(sig, "ok"))
                throw new ToolException("E_BAD_SIGNATURE", L.T("数字签名校验未通过：", "Digital signature check failed: ") + H.S(sig, "status"), L.T("拒绝安装来源不明的安装包", "Refusing to install a package from an unknown source"), ExitCodes.Error);

            // 3) 结束残留进程
            int procBefore = HostCommands.ProcessesByName(HostCommands.AppDirName).Count;
            if (procBefore > 0)
            {
                ctx.Out.Line(L.T("结束 ", "Killing ") + procBefore + L.T(" 个残留进程…", " leftover process(es)…"));
                try
                {
                    var killCtx = MakeSubCtx(ctx, new[] { "maint.kill-leftovers", "--yes", "--json" });
                    Registry.Find("maint.kill-leftovers").Run(killCtx);
                }
                catch (Exception ex) { ctx.Out.Warn(L.T("结束进程时出错（继续尝试安装）：", "Error while killing processes (continuing with the install): ") + ex.Message); }
                Thread.Sleep(900);
            }
            int stillRunning = HostCommands.ProcessesByName(HostCommands.AppDirName).Count;

            // 4) 静默安装
            string rollback = PreviousInstallerPath();
            string args = "--updated /S /D=" + installDir;
            ctx.Out.Line(L.T("静默安装：", "Silent install: ") + pkg + " " + args);
            var sw = Stopwatch.StartNew();
            int exit = RunAndWait(pkg, args, 15 * 60 * 1000);
            sw.Stop();

            // 5) 版本校验
            string nowVersion = null;
            var deadline = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < deadline)
            {
                nowVersion = HostCommands.FileVersion(exe);
                if (nowVersion != null && (version == null || string.Equals(nowVersion, version, StringComparison.OrdinalIgnoreCase))) break;
                if (nowVersion != null && nowVersion != installed) break;
                Thread.Sleep(800);
            }
            bool versionOk = nowVersion != null && (version == null || string.Equals(nowVersion, version, StringComparison.OrdinalIgnoreCase));

            // 6) 启动
            bool started = false;
            if (versionOk && !ctx.Flag("no-start"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir });
                    var d2 = DateTime.Now.AddSeconds(40);
                    while (DateTime.Now < d2 && HostCommands.ProcessesByName(HostCommands.AppDirName).Count == 0) Thread.Sleep(400);
                    started = HostCommands.ProcessesByName(HostCommands.AppDirName).Count > 0;
                }
                catch (Exception ex) { ctx.Out.Warn(L.T("启动失败：", "Launch failed: ") + ex.Message); }
            }

            var data = Json.Obj(
                "verdict", versionOk,
                "installerExit", exit,
                "elapsedMs", sw.ElapsedMilliseconds,
                "beforeVersion", installed, "afterVersion", nowVersion, "targetVersion", version,
                "checks", checks,
                "killedProcesses", procBefore, "stillRunning", stillRunning,
                "started", started,
                "rollbackInstaller", rollback,
                "reason", versionOk ? null : (L.T("安装后版本仍为 ", "Version after install is still ") + (nowVersion ?? L.T("未知", "unknown")) + L.T("，期望 ", ", expected ") + (version ?? L.T("未知", "unknown"))),
                "items", checks, "count", checks.Count,
                "columns", new[] { "check", "ok", "detail" });

            ctx.Out.Result("install.desktop", data);
            if (!versionOk)
            {
                ctx.Out.Warn(L.T("安装未生效。可用回滚安装包：", "The install did not take effect. Rollback installer: ") + (rollback ?? L.T("（更新缓存中未找到）", "(not found in the update cache)")));
                return ExitCodes.Error;
            }
            return ExitCodes.Ok;
        }

        // ================================================================ install.cli
        static int RunCli(Ctx ctx)
        {
            var cli = HostCommands.CliInfo();
            string npm = FindNpm();
            string version = ctx.Get("version", "");
            string file = ctx.Get("file");
            string registry = ctx.Get("registry");

            if (npm == null && string.IsNullOrEmpty(file))
                throw new ToolException("E_NO_NPM", L.T("未找到 npm（CLI 通过 npm 包分发）", "npm not found (the CLI is distributed as an npm package)"),
                    L.T("请先安装 Node.js（含 npm），或用 --file 指定离线 tgz 包", "Install Node.js (which includes npm) first, or use --file for an offline tgz package"), ExitCodes.NotFound);

            string pkgArg = !string.IsNullOrEmpty(file) ? "\"" + file + "\"" :
                            (string.IsNullOrEmpty(version) ? CliPackage : CliPackage + "@" + version);
            string cmd = "install -g " + pkgArg + " --no-fund --no-audit";
            if (!string.IsNullOrEmpty(registry)) cmd += " --registry \"" + registry + "\"";

            var plan = Json.Obj(
                "kind", "cli", "package", pkgArg, "npm", npm, "command", "npm " + cmd,
                "installedBefore", H.S(cli, "path"), "versionBefore", H.S(cli, "version"),
                "userLevel", true,
                "detail", L.T("npm 全局安装到用户目录（通常 %APPDATA%\\npm），不需要管理员权限", "npm installs globally into the user directory (usually %APPDATA%\\npm); no admin rights needed"));

            if (ctx.DryRun)
            {
                ctx.Out.Result("install.cli", Json.Obj("dryRun", true, "plan", plan,
                    "detail", L.T("将执行：npm ", "Running: npm ") + cmd));
                return ExitCodes.Ok;
            }
            ctx.ConfirmDestructive(L.T("通过 npm 安装官方 CLI：", "Installing the official CLI via npm: ") + pkgArg);

            var sw = Stopwatch.StartNew();
            string output = HostCommands.RunHidden(npm, cmd, 15 * 60 * 1000);
            sw.Stop();

            var after = HostCommands.CliInfo();
            bool found = H.B(after, "found");
            var items = new List<object>
            {
                Json.Obj("name", L.T("npm 退出", "npm exit"), "value", output == null ? L.T("超时/无输出", "timeout/no output") : L.T("完成", "done")),
                Json.Obj("name", L.T("CLI 路径", "CLI path"), "value", H.S(after, "path", L.T("未检测到", "not detected"))),
                Json.Obj("name", L.T("CLI 版本", "CLI version"), "value", H.S(after, "version", "-"))
            };
            ctx.Out.Result("install.cli", Json.Obj(
                "verdict", found,
                "elapsedMs", sw.ElapsedMilliseconds,
                "installed", found,
                "path", H.S(after, "path", ""),
                "version", H.S(after, "version", ""),
                "command", "npm " + cmd,
                "output", Tail(output, 4000),
                "plan", plan,
                "reason", found ? null : L.T("npm 执行完毕但未在 PATH 中检测到 dsh（可能需要重开终端或用 npm bin -g 查看路径）", "npm finished but dsh was not found on PATH (you may need to reopen the terminal, or check the path with npm bin -g)"),
                "items", items, "count", items.Count, "columns", new[] { "name", "value" }));

            if (!found)
            {
                ctx.Out.Warn(L.T("npm 输出（尾部）：", "npm output (tail): ") + Tail(output, 600));
                return ExitCodes.Error;
            }
            return ExitCodes.Ok;
        }

        // ================================================================ install.verify
        static int RunVerify(Ctx ctx)
        {
            string file = ctx.Require("--file");
            if (!File.Exists(file)) throw ToolException.NotFound(L.T("文件不存在：", "File not found: ") + file);
            string expectSha = ctx.Get("sha512");
            string publisher = ctx.Get("publisher", PublisherHint);

            var checks = new List<object>();
            var fi = new FileInfo(file);
            checks.Add(Json.Obj("check", "size", "ok", true, "detail", Fs.FormatSize(fi.Length)));

            if (!string.IsNullOrEmpty(expectSha))
            {
                string actual = Sha512Base64(file, ctx.Cancel);
                bool ok = string.Equals(actual, expectSha.Trim(), StringComparison.Ordinal);
                checks.Add(Json.Obj("check", "sha512", "ok", ok, "detail", ok ? L.T("一致", "match") : L.T("不一致", "mismatch")));
                if (!ok)
                {
                    ctx.Out.Result("install.verify", Json.Obj("verdict", false, "items", checks, "count", checks.Count,
                        "columns", new[] { "check", "ok", "detail" },
                        "reason", L.T("SHA-512 不匹配", "SHA-512 mismatch")));
                    return ExitCodes.Error;
                }
            }

            var sig = VerifySignature(ctx, file, publisher);
            checks.Add(Json.Obj("check", "signature", "ok", H.B(sig, "ok"), "detail", H.S(sig, "status") + " / " + H.S(sig, "signer")));
            bool verdict = checks.TrueForAll(c => H.B((Dictionary<string, object>)c, "ok"));

            ctx.Out.Result("install.verify", Json.Obj(
                "verdict", verdict, "file", file, "elapsedMs", H.L(sig, "elapsedMs"),
                "signature", sig, "items", checks, "count", checks.Count,
                "columns", new[] { "check", "ok", "detail" },
                "reason", verdict ? null : L.T("校验未通过", "Verification failed")));
            return verdict ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ================================================================ 辅助
        static Dictionary<string, object> ReadFeed()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(HostCommands.FeedUrl);
                req.Timeout = 15000; req.ReadWriteTimeout = 15000; req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string yml = sr.ReadToEnd();
                    string latest = Rx(yml, @"(?m)^version:\s*(\S+)");
                    string url = Rx(yml, @"url:\s*>-\s*\r?\n\s*(\S+)");
                    string sha = Rx(yml, @"sha512:\s*>-\s*\r?\n\s*(\S+)");
                    long size = 0; long.TryParse(Rx(yml, @"(?m)^\s*size:\s*(\d+)"), out size);
                    return Json.Obj("latest", latest, "url", url, "sha512", sha, "size", size);
                }
            }
            catch { return Json.Obj(); }
        }

        static string Rx(string text, string pattern)
        {
            if (text == null) return null;
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static void Download(string url, string dest, Ctx ctx)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 30000; req.ReadWriteTimeout = 120000; req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                long total = resp.ContentLength;
                var buf = new byte[1 << 20];
                long done = 0;
                int n;
                long lastReport = 0;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ctx.ThrowIfCancelled();
                    dst.Write(buf, 0, n);
                    done += n;
                    if (done - lastReport > 8L * 1024 * 1024)
                    {
                        lastReport = done;
                        int pct = total > 0 ? (int)(done * 100 / total) : 0;
                        ctx.Out.EmitItem(Json.Obj("phase", "download", "bytes", done, "totalBytes", total, "percent", pct,
                            "done", Fs.FormatSize(done), "total", total > 0 ? Fs.FormatSize(total) : "?"));
                    }
                }
            }
        }

        static string Sha512Base64(string path, CancellationToken ct)
        {
            using (var sha = SHA512.Create())
            using (var fs = new FileStream(Fs.LongPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); sha.TransformBlock(buf, 0, n, buf, 0); }
                sha.TransformFinalBlock(buf, 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        /// <summary>复用工具箱自身的 sign.verify（含目录签名/WinVerifyTrust 逻辑），避免重复实现。</summary>
        static Dictionary<string, object> VerifySignature(Ctx ctx, string path, string publisher)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var psi = new ProcessStartInfo(ctx.ExePath,
                    "sign.verify --path \"" + path + "\" --publisher \"" + publisher + "\" --json")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
                };
                using (var p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } return Json.Obj("ok", false, "status", "Timeout"); }
                    sw.Stop();
                    var env = Json.ParseObject(so.Trim());
                    var data = env.ContainsKey("data") ? env["data"] as Dictionary<string, object> : null;
                    if (data == null) return Json.Obj("ok", false, "status", "NoData", "elapsedMs", sw.ElapsedMilliseconds);
                    return Json.Obj(
                        "ok", data.ContainsKey("valid") && Convert.ToBoolean(data["valid"]),
                        "status", H.S(data, "status"),
                        "signer", H.S(H.Sub(data, "signer"), "subject"),
                        "kind", H.S(data, "signatureKind"),
                        "elapsedMs", sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                return Json.Obj("ok", false, "status", "Error: " + ex.Message, "elapsedMs", sw.ElapsedMilliseconds);
            }
        }

        static int RunAndWait(string file, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        static string FindNpm()
        {
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    foreach (var n in new[] { "npm.cmd", "npm.exe", "npm" })
                    {
                        try { string full = Path.Combine(dir.Trim(), n); if (File.Exists(full)) return full; } catch { }
                    }
                }
                foreach (var c in new[] { @"C:\Program Files\nodejs\npm.cmd", @"C:\Program Files (x86)\nodejs\npm.cmd" })
                    if (File.Exists(c)) return c;
            }
            catch { }
            return null;
        }

        static string PreviousInstallerPath()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "@deepseek-aidsh-desktop-updater", "installer.exe");
            return File.Exists(p) ? p : null;
        }

        static string Tail(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n");
            return s.Length <= n ? s : "…" + s.Substring(s.Length - n);
        }

        /// <summary>为一个子命令构造最小 Ctx（复用现有命令实现）。</summary>
        static Ctx MakeSubCtx(Ctx parent, string[] argv)
        {
            var ctx = new Ctx();
            ctx.Args = ArgMap.Parse(argv);
            ctx.Out = parent.Out;
            ctx.Log = parent.Log;
            ctx.Cwd = parent.Cwd;
            ctx.ExePath = parent.ExePath;
            ctx.DryRun = parent.DryRun;
            ctx.Yes = parent.Yes;
            ctx.Cancel = parent.Cancel;
            return ctx;
        }
    }

    /// <summary>InstallCommands 内部使用的字典读取别名（避免与 HostCommands 私有方法重名）。</summary>
    internal static class H
    {
        public static string S(Dictionary<string, object> d, string key, string def = "")
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            var v = d[key];
            if (v is bool) return ((bool)v) ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static bool B(Dictionary<string, object> d, string key, bool def = false)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToBoolean(d[key]); } catch { return def; }
        }
        public static long L(Dictionary<string, object> d, string key, long def = 0)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToInt64(d[key]); } catch { return def; }
        }
        public static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return new Dictionary<string, object>();
            return d[key] as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
    }
}

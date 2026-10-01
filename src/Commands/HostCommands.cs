using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// 宿主与运维命令：host.status / compat.check / compat.fix / maint.* / elevate.run。
    /// GUI 与 agent 共用同一实现（GUI 通过子进程调 CLI + --json）。
    /// </summary>
    public static class HostCommands
    {
        public const string FeedUrl = "https://download.deepseek.com/dsh-desk/feeds/win-x64/nightly.yml";
        public const string AppDirName = "DeepSeek Harness";
        public const string AppExe = "DeepSeek Harness.exe";

        public static void Register()
        {
            Registry.Add("host.status", Core.L.T("DSH 宿主状态：桌面端/CLI 安装情况、版本、进程、数据目录、权限", "DSH host status: desktop/CLI install state, version, processes, data folder, privileges"),
                "host status [--json]", RunStatus, aliases: new[] { "status" },
                examples: new[] { "dsh-toolbox host.status --json" });

            Registry.Add("compat.check", Core.L.T("环境体检：OS/CPU 指令集/.NET/PowerShell/长路径/Defender/磁盘/网络", "Environment check: OS/CPU instruction sets/.NET/PowerShell/long paths/Defender/disk/network"),
                "compat check [--fast] [--json]", RunCompatCheck,
                examples: new[] { "dsh-toolbox compat.check --json", "dsh-toolbox compat.check --fast" });

            Registry.Add("compat.fix", Core.L.T("按体检结论执行修复（部分需管理员，会自动提示提权）", "Apply fixes based on the check results (some need admin; elevation is prompted)"),
                "compat fix --id <longpaths|defender|caches|leftovers> [--yes]",
                RunCompatFix, examples: new[] { "dsh-toolbox compat.fix --id longpaths --yes --json" });

            Registry.Add("maint.kill-leftovers", Core.L.T("结束 DSH 残留进程（先优雅后强制，含子进程）", "Kill leftover DSH processes (graceful first, then forced; includes child processes)"),
                "maint kill-leftovers [--dry-run] [--yes] [--force] [--include-children]",
                RunKillLeftovers, examples: new[] { "dsh-toolbox maint.kill-leftovers --dry-run --json" });

            Registry.Add("maint.clean-cache", Core.L.T("清理 DSH 缓存（Code Cache/GPUCache/Cache 等；需先关闭应用）", "Clean DSH caches (Code Cache/GPUCache/Cache etc.; close the app first)"),
                "maint clean-cache [--dry-run] [--yes] [--all]",
                RunCleanCache, examples: new[] { "dsh-toolbox maint.clean-cache --dry-run --json" });

            Registry.Add("maint.restart-host", Core.L.T("重启 DSH 桌面端（结束全部进程后重新拉起）", "Restart the DSH desktop app (stop all processes, then relaunch)"),
                "maint restart-host [--dry-run] [--yes] [--wait <dur>]",
                RunRestartHost, examples: new[] { "dsh-toolbox maint.restart-host --yes --json" });

            Registry.Add("maint.pull-update", Core.L.T("重新拉取更新：清除 pending 缓存后重启宿主，让它重新检查更新", "Re-pull updates: clear the pending cache, then restart the host so it re-checks for updates"),
                "maint pull-update [--dry-run] [--yes]",
                RunPullUpdate, examples: new[] { "dsh-toolbox maint.pull-update --dry-run --json" });

            Registry.Add("maint.rebuild-self", Core.L.T("用 build.ps1 重新构建工具箱自身（开发用）", "Rebuild the toolbox itself with build.ps1 (for development)"),
                "maint rebuild-self [--out <name>] [--only <mods>]",
                RunRebuildSelf, examples: new[] { "dsh-toolbox maint.rebuild-self --json" });

            Registry.Add("install.check", Core.L.T("检查官方更新源：最新版本/下载地址/大小/SHA512/是否有更新", "Check the official feed: latest version/download URL/size/SHA512/update availability"),
                "install check [--json]", RunInstallCheck,
                examples: new[] { "dsh-toolbox install.check --json" });

            Registry.Add("elevate.run", Core.L.T("以管理员身份执行一条工具箱命令（弹 UAC；结果通过临时文件回传）", "Run one toolbox command as administrator (raises UAC; the result is returned via a temp file)"),
                Core.L.T("elevate run -- <命令> [参数...]", "elevate run -- <command> [args...]"), RunElevate,
                examples: new[] { "dsh-toolbox elevate.run -- compat.fix --id longpaths --yes --json" });
        }

        // ================================================================ host.status
        static int RunStatus(Ctx ctx)
        {
            bool fast = ctx.Flag("fast");
            var total = Stopwatch.StartNew();
            var swD = Stopwatch.StartNew();
            var desktop = DesktopInfo();
            swD.Stop();
            var swC = Stopwatch.StartNew();
            var cli = fast ? CliInfoCached(false) : CliInfoCached(ctx.Flag("refresh"));
            swC.Stop();
            bool elevated = IsElevated();
            var swT = Stopwatch.StartNew();

            int webPort = ctx.Args.GetInt("port", 19387);
            bool listening = fast ? TestTcp("127.0.0.1", webPort, 300) : TestTcp("127.0.0.1", webPort, 800);
            swT.Stop();
            total.Stop();

            var data = Json.Obj(
                "desktop", desktop,
                "cli", cli,
                "elevated", elevated,
                "user", Environment.UserName,
                "dshHome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"),
                "userData", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop"),
                "webPort", webPort,
                "webListening", listening,
                "fast", fast,
                "totalMs", total.ElapsedMilliseconds,
                "phaseMs", Json.Obj("desktop", swD.ElapsedMilliseconds, "cli", swC.ElapsedMilliseconds, "tcp", swT.ElapsedMilliseconds),
                "toolbox", Json.Obj("version", ToolInfo.Version, "exe", ctx.ExePath, "home", Paths.Home),
                "items", new List<object>
                {
                    Json.Obj("name", Core.L.T("桌面端", "Desktop app"), "state", B(desktop, "installed") ? S(desktop, "version") : Core.L.T("未安装", "Not installed"),
                             "detail", B(desktop, "running") ? Core.L.T("运行中 {0} 进程", "Running {0} processes", L(desktop, "processCount")) : Core.L.T("未运行", "Not running")),
                    Json.Obj("name", "CLI", "state", B(cli, "found") ? Core.L.T("已安装", "Installed") : Core.L.T("未找到", "Not found"),
                             "detail", B(cli, "found") ? S(cli, "path") : Core.L.T("见 cli.candidates", "See cli.candidates")),
                    Json.Obj("name", Core.L.T("权限", "Privileges"), "state", elevated ? Core.L.T("管理员", "Administrator") : Core.L.T("普通用户", "Standard user"),
                             "detail", elevated ? Core.L.T("可执行需要提权的操作", "Can run operations that require elevation") : Core.L.T("需要时会提示并弹 UAC", "Prompts and raises UAC when needed"))
                },
                "count", 3,
                "columns", new[] { "name", "state", "detail" });

            ctx.Out.Result("host.status", data);
            return ExitCodes.Ok;
        }

        public static Dictionary<string, object> DesktopInfo()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppDirName);
            string exe = Path.Combine(dir, AppExe);
            string version = null;
            try { if (File.Exists(exe)) version = FileVersion(exe); } catch { }

            string regVersion = null;
            foreach (var entry in UninstallEntries())
            {
                string dn = Json.GetString(entry, "DisplayName");
                if (!string.IsNullOrEmpty(dn) && dn.IndexOf("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    regVersion = Json.GetString(entry, "DisplayVersion");
                    string loc = Json.GetString(entry, "InstallLocation");
                    if (!string.IsNullOrEmpty(loc)) dir = loc;
                }
            }

            var procs = new List<object>();
            int count = 0;
            foreach (var p in ProcessesByName(AppDirName))
            {
                count++;
                if (procs.Count < 12)
                    procs.Add(Json.Obj("pid", p.Id, "start", SafeStart(p), "memMB", SafeMem(p), "main", SafeMain(p)));
            }

            return Json.Obj(
                "installed", File.Exists(exe) || regVersion != null,
                "version", version ?? regVersion,
                "registryVersion", regVersion,
                "installDir", dir,
                "exe", exe,
                "exeExists", File.Exists(exe),
                "running", count > 0,
                "processCount", count,
                "processes", procs);
        }

        /// <summary>带磁盘缓存的 CLI 探测：PATH 扫描有时间成本，GUI 频繁刷新时不该每次都扫。</summary>
        public static Dictionary<string, object> CliInfoCached(bool forceRefresh)
        {
            string f = Path.Combine(Paths.Cache, "cli-detect.json");
            try
            {
                if (!forceRefresh && File.Exists(f))
                {
                    var o = Json.ParseObject(File.ReadAllText(f, Encoding.UTF8));
                    var ts = o.ContainsKey("ts") ? Convert.ToDateTime(o["ts"]) : DateTime.MinValue;
                    var data = o.ContainsKey("data") ? o["data"] as Dictionary<string, object> : null;
                    if (data != null && (DateTime.Now - ts).TotalMinutes < 10) return data;
                }
            }
            catch { }
            var info = CliInfo();
            try
            {
                Fs.EnsureDir(Paths.Cache);
                File.WriteAllText(f, Json.Write(Json.Obj("ts", DateTime.Now, "data", info)), new UTF8Encoding(false));
            }
            catch { }
            return info;
        }

        public static Dictionary<string, object> CliInfo()
        {
            var candidates = new List<string>();
            string found = null;

            // PATH 扫描
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    foreach (var n in new[] { "dsh.cmd", "dsh.exe", "dsh.bat", "dsh.ps1", "dsh" })
                    {
                        try
                        {
                            string full = Path.Combine(dir.Trim(), n);
                            if (File.Exists(full)) { candidates.Add(full); if (found == null) found = full; }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            foreach (var extra in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "dsh.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "bin", "dsh.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "dsh", "dsh.exe")
            })
            {
                try { if (File.Exists(extra)) { candidates.Add(extra); if (found == null) found = extra; } } catch { }
            }

            string version = null;
            if (found != null) version = ProbeVersion(found);

            return Json.Obj(
                "found", found != null,
                "path", found,
                "version", version,
                "candidates", candidates.Distinct().Take(20).ToList());
        }

        static string ProbeVersion(string cmdPath)
        {
            try
            {
                string ext = Path.GetExtension(cmdPath).ToLowerInvariant();
                string file = cmdPath, args = "--version";
                if (ext == ".cmd" || ext == ".bat") { file = "cmd.exe"; args = "/c \"\"" + cmdPath + "\" --version\""; }
                else if (ext == ".ps1") { file = "powershell.exe"; args = "-NoProfile -ExecutionPolicy Bypass -File \"" + cmdPath + "\" --version"; }
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(6000)) { try { p.Kill(); } catch { } return null; }
                    var m = Regex.Match(so ?? "", @"\d+\.\d+\.\d+[-.\w]*");
                    return m.Success ? m.Value : (so ?? "").Trim().Split('\n').FirstOrDefault();
                }
            }
            catch { return null; }
        }

        // ================================================================ compat.check
        static int RunCompatCheck(Ctx ctx)
        {
            bool fast = ctx.Flag("fast");
            var items = new List<object>();
            int block = 0, warn = 0;

            Action<string, string, string, bool, string, bool, bool, string> add =
                (id, name, level, ok, detail, fixable, needsAdmin, fixId) =>
                {
                    if (!ok && level == "block") block++;
                    else if (!ok && level == "warn") warn++;
                    items.Add(Json.Obj("id", id, "name", name, "level", level, "ok", ok, "detail", detail,
                                       "fixable", fixable, "needsAdmin", needsAdmin, "fixId", fixId));
                };

            var os = Environment.OSVersion;
            bool win10 = os.Version.Major >= 10;
            add("os", Core.L.T("操作系统", "Operating system"), "block", win10, os.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86") + Core.L.T("（本工具基线：Windows 7 SP1 + .NET Framework 4.8）", " (tool baseline: Windows 7 SP1 + .NET Framework 4.8)"), false, false, null);

            add("arch", Core.L.T("64 位架构", "64-bit architecture"), "warn", Environment.Is64BitOperatingSystem,
                Environment.Is64BitOperatingSystem ? "x64" : Core.L.T("32 位系统无法运行官方桌面端", "A 32-bit system cannot run the official desktop app"), false, false, null);

            // CPU 指令集（Electron / 原生模块常见门槛）
            string cpuName = Convert.ToString(RegRead(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", Core.L.T("未知", "Unknown")), CultureInfo.InvariantCulture);
            bool sse42 = IsProcessorFeaturePresent(38);
            bool avx = IsProcessorFeaturePresent(39);
            bool avx2 = IsProcessorFeaturePresent(40);
            add("cpu", Core.L.T("CPU 指令集", "CPU instruction sets"), "warn", sse42 && avx,
                cpuName.Trim() + string.Format(" · SSE4.2={0} AVX={1} AVX2={2}", sse42 ? "✓" : "✗", avx ? "✓" : "✗", avx2 ? "✓" : "✗"),
                false, false, null);
            add("avx2", Core.L.T("AVX2（部分原生模块需要）", "AVX2 (required by some native modules)"), "warn", avx2,
                avx2 ? Core.L.T("支持", "Supported") : Core.L.T("不支持：部分 Node 原生模块（如语音/ONNX 相关）可能无法加载，官方可能未提供非 AVX2 版本", "Not supported: some Node native modules (e.g. speech/ONNX related) may fail to load; the official build may not offer a non-AVX2 variant"),
                false, false, null);

            // .NET Framework
            long rel = 0;
            try { rel = Convert.ToInt64(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0L)); } catch { }
            bool net48 = rel >= 528040;
            add("dotnet", ".NET Framework", "warn", net48, "Release=" + rel + (net48 ? Core.L.T("（4.8+）", " (4.8+)") : Core.L.T("（低于 4.8）", " (below 4.8)")), false, false, null);

            // PowerShell：默认只读注册表（毫秒级）；本机实测启动一次 powershell.exe 要 ~2.5 秒，
            // 所以 LanguageMode 探测放到 --deep 里，避免体检本身变成慢操作。
            string psVer = RegStr(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine", "PowerShellVersion");
            if (string.IsNullOrEmpty(psVer))
                psVer = RegStr(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PowerShell\1\PowerShellEngine", "PowerShellVersion");
            string psMode = null;
            if (ctx.Flag("deep"))
            {
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"$ExecutionContext.SessionState.LanguageMode\"")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using (var p = Process.Start(psi))
                    {
                        string so = p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                        psMode = (so ?? "").Trim();
                        if (psMode.Length == 0) psMode = null;
                    }
                }
                catch { }
            }
            bool psOk = !string.IsNullOrEmpty(psVer) && (psMode == null || psMode == "FullLanguage");
            add("powershell", "PowerShell", "warn", psOk,
                Core.L.T("版本 ", "Version ") + (string.IsNullOrEmpty(psVer) ? Core.L.T("未检测到", "not detected") : psVer) +
                (psMode == null ? Core.L.T("（LanguageMode 未检测，加 --deep 可测；受限模式会限制脚本与验签）", " (LanguageMode not probed; add --deep to test; restricted mode limits scripts and signature verification)")
                                : " · LanguageMode=" + psMode),
                false, false, null);

            // 长路径
            bool longPaths = Convert.ToInt32(RegRead(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 0)) == 1;
            add("longpaths", Core.L.T("超长路径支持", "Long path support"), "warn", longPaths,
                longPaths ? Core.L.T("已启用", "Enabled") : Core.L.T("未启用：深层 node_modules 可能报路径过长（本工具箱自身已内置 \\\\?\\ 兼容）", "Not enabled: deep node_modules paths may be reported as too long (this toolbox already has built-in \\\\?\\ support)"),
                true, true, "longpaths");

            // Defender 排除
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop");
            var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppDirName);
            string exclDetail = Core.L.T("普通用户无法读取排除项（需管理员）", "A standard user cannot read exclusions (admin required)");
            bool exclOk = false;
            add("defender", Core.L.T("Defender 排除项", "Defender exclusions"), "info", exclOk, exclDetail, true, true, "defender");

            // 磁盘
            long free = 0;
            try
            {
                var di = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
                free = di.AvailableFreeSpace;
            }
            catch { }
            add("disk", Core.L.T("可用磁盘空间", "Free disk space"), "warn", free > 3L * 1024 * 1024 * 1024,
                Fs.FormatSize(free) + (free > 3L * 1024 * 1024 * 1024 ? "" : Core.L.T("：低于 3 GB，更新/安装可能失败", ": below 3 GB; update/install may fail")), false, false, null);

            // TLS 1.2
            bool schUse = Convert.ToInt32(RegRead(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework\v4.0.30319", "SchUseStrongCrypto", 0)) == 1;
            add("tls", Core.L.T("TLS 1.2 默认启用", "TLS 1.2 enabled by default"), "info", true,
                schUse ? "SchUseStrongCrypto=1" : Core.L.T("SchUseStrongCrypto 未设置（本工具内部已显式启用 TLS 1.2，不受影响）", "SchUseStrongCrypto not set (this tool enables TLS 1.2 explicitly, so it is unaffected)"), false, false, null);

            // VC++ 运行库
            bool vc = File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140.dll")) || File.Exists(Path.Combine(Environment.SystemDirectory, "msvcp140.dll"));
            add("vcruntime", Core.L.T("VC++ 运行库", "VC++ runtime"), "info", vc, vc ? Core.L.T("已安装", "Installed") : Core.L.T("未检测到 msvcp140/vcruntime140（Electron 自带，通常无碍）", "msvcp140/vcruntime140 not found (Electron bundles its own; usually harmless)"), false, false, null);

            // 更新源连通性
            if (!fast)
            {
                var sw = Stopwatch.StartNew();
                string feedDetail;
                bool feedOk = ProbeUrl(FeedUrl, 12000, out feedDetail);
                sw.Stop();
                add("feed", Core.L.T("更新源连通性", "Update feed connectivity"), "warn", feedOk,
                    feedOk ? (Core.L.T("可达，", "Reachable, ") + sw.ElapsedMilliseconds + " ms") : (Core.L.T("不可达：", "Unreachable: ") + feedDetail), false, false, null);
            }
            else
            {
                add("feed", Core.L.T("更新源连通性", "Update feed connectivity"), "info", true, Core.L.T("已按 --fast 跳过", "Skipped by --fast"), false, false, null);
            }

            // 残留进程
            int procCount = ProcessesByName(AppDirName).Count;
            add("leftovers", Core.L.T("宿主进程", "Host processes"), "info", procCount <= 6,
                Core.L.T("{0} 个 DeepSeek Harness 进程", "{0} DeepSeek Harness processes", procCount) + (procCount > 6 ? Core.L.T("（多于常见的 5~6，可能是残留）", " (more than the usual 5-6; may be leftovers)") : ""),
                procCount > 0, false, "leftovers");

            bool verdict = block == 0;
            ctx.Out.Result("compat.check", Json.Obj(
                "verdict", verdict,
                "blockCount", block,
                "warnCount", warn,
                "items", items,
                "count", items.Count,
                "columns", new[] { "name", "ok", "detail" },
                "reason", verdict ? null : Core.L.T("{0} 项阻断性问题", "{0} blocking issues", block),
                "cpu", cpuName.Trim(),
                "fast", fast,
                "appDir", appDir,
                "userData", userData));
            return verdict ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ================================================================ compat.fix
        static int RunCompatFix(Ctx ctx)
        {
            string id = ctx.Require("--id").ToLowerInvariant();
            bool elevated = IsElevated();

            switch (id)
            {
                case "longpaths":
                    if (!elevated) throw new ToolException("E_ELEVATION_REQUIRED", Core.L.T("启用长路径需要管理员权限", "Enabling long paths requires administrator privileges"), Core.L.T("用 elevate.run 重新执行，或在本界面点“提权执行”", "Re-run with elevate.run, or click “Run elevated” in this UI"), ExitCodes.Denied);
                    if (ctx.DryRun) { ctx.Out.Result("compat.fix", Json.Obj("id", id, "plan", Core.L.T("设置 HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled=1", "Set HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled=1"), "dryRun", true)); return ExitCodes.Ok; }
                    ctx.ConfirmDestructive(Core.L.T("修改系统注册表启用长路径", "Modify the system registry to enable long paths"));
                    Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    ctx.Out.Result("compat.fix", Json.Obj("id", id, "ok", true, "detail", Core.L.T("LongPathsEnabled=1 已写入（需重启相关进程生效）", "LongPathsEnabled=1 written (restart the related processes to apply)")));
                    return ExitCodes.Ok;

                case "defender":
                    if (!elevated) throw new ToolException("E_ELEVATION_REQUIRED", Core.L.T("添加 Defender 排除项需要管理员权限", "Adding Defender exclusions requires administrator privileges"), Core.L.T("用 elevate.run 重新执行", "Re-run with elevate.run"), ExitCodes.Denied);
                    var targets = new List<string>
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppDirName),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "@deepseek-aidsh-desktop-updater")
                    };
                    if (ctx.DryRun)
                    {
                        ctx.Out.Result("compat.fix", Json.Obj("id", id, "plan", "Add-MpPreference -ExclusionPath " + string.Join(" ; ", targets.ToArray()), "dryRun", true));
                        return ExitCodes.Ok;
                    }
                    ctx.ConfirmDestructive(Core.L.T("为应用目录与更新缓存目录添加 Defender 排除项", "Add Defender exclusions for the app folder and the update cache folder"));
                    var outs = new List<object>();
                    foreach (var t in targets)
                    {
                        string cmd = "Add-MpPreference -ExclusionPath '" + t.Replace("'", "''") + "'";
                        string o = RunPowerShell(cmd, 60000);
                        outs.Add(Json.Obj("path", t, "output", (o ?? "").Trim()));
                    }
                    ctx.Out.Result("compat.fix", Json.Obj("id", id, "ok", true, "results", outs, "detail", Core.L.T("已尝试添加 {0} 条排除项", "Attempted to add {0} exclusions", targets.Count)));
                    return ExitCodes.Ok;

                case "leftovers":
                    return RunKillLeftovers(ctx);

                case "caches":
                    return RunCleanCache(ctx);

                default:
                    throw ToolException.Usage(Core.L.T("未知的修复项：", "Unknown fix id: ") + id, Core.L.T("可选：longpaths / defender / caches / leftovers", "Available: longpaths / defender / caches / leftovers"));
            }
        }

        // ================================================================ maint.kill-leftovers
        static int RunKillLeftovers(Ctx ctx)
        {
            var procs = ProcessesByName(AppDirName);
            if (procs.Count == 0)
            {
                ctx.Out.Result("maint.kill-leftovers", Json.Obj("killed", 0, "count", 0, "detail", Core.L.T("无残留进程", "No leftover processes")));
                return ExitCodes.Ok;
            }
            var plan = procs.Select(p => (object)Json.Obj("pid", p.Id, "start", SafeStart(p), "memMB", SafeMem(p))).ToList();
            if (ctx.DryRun)
            {
                ctx.Out.Result("maint.kill-leftovers", Json.Obj("dryRun", true, "plan", plan, "count", plan.Count,
                    "detail", Core.L.T("将结束 {0} 个进程（含子进程树）", "Will terminate {0} processes (including child process trees)", plan.Count)));
                return ExitCodes.Ok;
            }
            ctx.ConfirmDestructive(Core.L.T("结束 {0} 个 DeepSeek Harness 进程", "Terminate {0} DeepSeek Harness processes", procs.Count));

            int graceful = 0, forced = 0;
            foreach (var p in procs)
            {
                try { if (p.CloseMainWindow()) graceful++; } catch { }
            }
            var deadline = DateTime.Now.AddSeconds(ctx.Args.GetInt("graceful", 8));
            while (DateTime.Now < deadline && ProcessesByName(AppDirName).Count > 0)
                Thread.Sleep(400);

            foreach (var p in ProcessesByName(AppDirName))
            {
                try
                {
                    RunHidden("taskkill.exe", "/PID " + p.Id + " /T /F", 20000);
                    forced++;
                }
                catch { }
            }
            Thread.Sleep(600);
            int left = ProcessesByName(AppDirName).Count;
            ctx.Out.Result("maint.kill-leftovers", Json.Obj(
                "killed", plan.Count, "graceful", graceful, "forced", forced, "remaining", left,
                "count", left, "plan", plan,
                "detail", left == 0 ? Core.L.T("已全部结束", "All terminated") : Core.L.T("仍有 {0} 个进程未结束（可能权限不足）", "{0} processes are still running (possibly insufficient privileges)", left)));
            return left == 0 ? ExitCodes.Ok : ExitCodes.Partial;
        }

        // ================================================================ maint.clean-cache
        static string[] CacheNames(bool all)
        {
            var basic = new[] { "Code Cache", "GPUCache", "Cache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache" };
            if (!all) return basic;
            return basic.Concat(new[] { "Local Storage", "Session Storage", "Shared Dictionary", "Network", "Partitions" }).ToArray();
        }

        static int RunCleanCache(Ctx ctx)
        {
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop");
            if (!Directory.Exists(userData)) throw ToolException.NotFound(Core.L.T("未找到 DSH 数据目录：", "DSH data folder not found: ") + userData);

            bool all = ctx.Flag("all");
            var targets = new List<string>();
            foreach (var n in CacheNames(all))
            {
                string p = Path.Combine(userData, n);
                if (Directory.Exists(p)) targets.Add(p);
            }
            long total = 0;
            var plan = new List<object>();
            foreach (var t in targets)
            {
                long sz = DirSize(t);
                total += sz;
                plan.Add(Json.Obj("path", t, "size", sz, "sizeHuman", Fs.FormatSize(sz)));
            }

            int running = ProcessesByName(AppDirName).Count;
            if (ctx.DryRun)
            {
                ctx.Out.Result("maint.clean-cache", Json.Obj("dryRun", true, "plan", plan, "count", plan.Count,
                    "totalBytes", total, "totalHuman", Fs.FormatSize(total), "appRunning", running,
                    "detail", (running > 0 ? Core.L.T("注意：应用正在运行，实际清理前需先关闭。", "Note: the app is running; close it before actually cleaning. ") : "") + Core.L.T("将清理 {0} 个缓存目录", "Will clean {0} cache folders", plan.Count)));
                return ExitCodes.Ok;
            }
            ctx.ConfirmDestructive(Core.L.T("清理 {0} 个缓存目录（{1}）", "Clean {0} cache folders ({1})", plan.Count, Fs.FormatSize(total)));
            if (running > 0)
            {
                RunKillLeftovers(ctx);
                Thread.Sleep(500);
            }
            var failures = new List<object>();
            long freed = 0;
            foreach (var t in targets)
            {
                try
                {
                    long before = DirSize(t);
                    Directory.Delete(t, true);
                    freed += before;
                }
                catch (Exception ex) { failures.Add(Json.Obj("path", t, "error", ex.Message)); }
            }
            ctx.Out.Result("maint.clean-cache", Json.Obj(
                "cleaned", targets.Count - failures.Count, "failed", failures.Count,
                "freedBytes", freed, "freedHuman", Fs.FormatSize(freed),
                "failures", failures, "plan", plan, "count", targets.Count,
                "detail", Core.L.T("已释放 ", "Freed ") + Fs.FormatSize(freed)));
            return failures.Count == 0 ? ExitCodes.Ok : ExitCodes.Partial;
        }

        // ================================================================ maint.restart-host
        static int RunRestartHost(Ctx ctx)
        {
            var d = DesktopInfo();
            string exe = S(d, "exe");
            if (!File.Exists(exe)) throw ToolException.NotFound(Core.L.T("未找到桌面端可执行文件：", "Desktop executable not found: ") + exe + Core.L.T("（请先安装）", " (install it first)"));

            if (ctx.DryRun)
            {
                ctx.Out.Result("maint.restart-host", Json.Obj("dryRun", true, "exe", exe,
                    "plan", Core.L.T("结束全部 DSH 进程 → 等待 → 启动 ", "Stop all DSH processes → wait → launch ") + exe, "detail", Core.L.T("将重启桌面端", "Restarting the desktop app")));
                return ExitCodes.Ok;
            }
            ctx.ConfirmDestructive(Core.L.T("重启 DSH 桌面端（当前会话会中断）", "Restart the DSH desktop app (the current session will be interrupted)"));
            RunKillLeftovers(ctx);
            Thread.Sleep(800);

            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) };
            var p = Process.Start(psi);

            TimeSpan wait = ctx.Args.GetSpan("wait", TimeSpan.FromSeconds(30));
            var deadline = DateTime.Now.Add(wait);
            while (DateTime.Now < deadline && ProcessesByName(AppDirName).Count == 0) Thread.Sleep(300);
            int cnt = ProcessesByName(AppDirName).Count;
            ctx.Out.Result("maint.restart-host", Json.Obj("ok", cnt > 0, "exe", exe, "processCount", cnt,
                "detail", cnt > 0 ? Core.L.T("已启动，进程数 {0}", "Started, {0} processes", cnt) : Core.L.T("已发出启动命令但未检测到进程", "Launch command issued but no process detected")));
            return cnt > 0 ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ================================================================ maint.pull-update
        static int RunPullUpdate(Ctx ctx)
        {
            string updater = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "@deepseek-aidsh-desktop-updater");
            string pending = Path.Combine(updater, "pending");
            var files = new List<object>();
            long total = 0;
            if (Directory.Exists(pending))
            {
                foreach (var f in Directory.GetFiles(pending))
                {
                    long sz = 0; try { sz = new FileInfo(f).Length; } catch { }
                    total += sz;
                    files.Add(Json.Obj("path", f, "size", sz, "sizeHuman", Fs.FormatSize(sz)));
                }
            }
            if (ctx.DryRun)
            {
                ctx.Out.Result("maint.pull-update", Json.Obj("dryRun", true, "pending", files, "count", files.Count,
                    "totalBytes", total, "detail", Core.L.T("将清除 {0} 个待安装文件（{1}）并重启宿主重新检查更新", "Will clear {0} pending files ({1}) and restart the host to re-check for updates", files.Count, Fs.FormatSize(total))));
                return ExitCodes.Ok;
            }
            ctx.ConfirmDestructive(Core.L.T("清除更新缓存并重启宿主以重新拉取更新", "Clear the update cache and restart the host to re-pull updates"));
            var failures = new List<object>();
            foreach (var f in files)
            {
                try { File.Delete((string)((Dictionary<string, object>)f)["path"]); }
                catch (Exception ex) { failures.Add(Json.Obj("path", ((Dictionary<string, object>)f)["path"], "error", ex.Message)); }
            }
            ctx.Out.Result("maint.pull-update", Json.Obj("cleared", files.Count - failures.Count, "failed", failures.Count,
                "freedBytes", total, "freedHuman", Fs.FormatSize(total), "failures", failures,
                "detail", Core.L.T("已清理更新缓存，正在重启宿主", "Update cache cleared; restarting the host")));
            // 重启宿主（若已安装）
            var d = DesktopInfo();
            if (B(d, "installed") && File.Exists(S(d, "exe")))
            {
                try { RunRestartHost(ctx); } catch (Exception ex) { ctx.Out.Warn(Core.L.T("重启宿主失败：", "Failed to restart the host: ") + ex.Message); }
            }
            return failures.Count == 0 ? ExitCodes.Ok : ExitCodes.Partial;
        }

        // ================================================================ maint.rebuild-self
        static int RunRebuildSelf(Ctx ctx)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(ctx.ExePath));   // dist\ -> 仓库根
            string build = Path.Combine(root, "build.ps1");
            if (!File.Exists(build))
            {
                // 兜底：从 exe 所在目录逐级向上找 build.ps1（源码目录/发布目录都能命中，不写死路径）
                var dir = new DirectoryInfo(Path.GetDirectoryName(ctx.ExePath));
                for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                {
                    string cand = Path.Combine(dir.FullName, "build.ps1");
                    if (File.Exists(cand)) { root = dir.FullName; build = cand; break; }
                }
            }
            if (!File.Exists(build)) throw ToolException.NotFound(Core.L.T("找不到 build.ps1：", "build.ps1 not found: ") + build);

            string outArg = ctx.Get("out", "");
            string only = ctx.Get("only", "");
            string args = "-NoProfile -ExecutionPolicy Bypass -File \"" + build + "\"";
            if (!string.IsNullOrEmpty(outArg)) args += " -Out \"" + outArg + "\"";
            if (!string.IsNullOrEmpty(only)) args += " -Only " + only;

            var sw = Stopwatch.StartNew();
            string output = RunHidden("powershell.exe", args, 600000);
            sw.Stop();
            bool ok = output != null && output.IndexOf("构建成功", StringComparison.Ordinal) >= 0;
            ctx.Out.Result("maint.rebuild-self", Json.Obj(
                "ok", ok, "elapsedMs", sw.ElapsedMilliseconds, "buildScript", build,
                "output", output == null ? "" : output.Trim(),
                "detail", ok ? Core.L.T("重建成功", "Rebuild succeeded") : Core.L.T("重建失败，请查看 output", "Rebuild failed; see output")));
            return ok ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ================================================================ install.check
        static int RunInstallCheck(Ctx ctx)
        {
            var d = DesktopInfo();
            var cli = CliInfo();
            string latest = null, url = null, sha = null, released = null, error = null;
            long size = 0;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(FeedUrl);
                req.Timeout = 15000; req.ReadWriteTimeout = 15000; req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string yml = sr.ReadToEnd();
                    latest = Rx(yml, @"(?m)^version:\s*(\S+)");
                    url = Rx(yml, @"url:\s*>-\s*\r?\n\s*(\S+)");
                    sha = Rx(yml, @"sha512:\s*>-\s*\r?\n\s*(\S+)");
                    released = Rx(yml, @"releaseDate:\s*'?([^'\r\n]+)'?");
                    string sz = Rx(yml, @"(?m)^\s*size:\s*(\d+)");
                    if (sz != null) long.TryParse(sz, out size);
                }
            }
            catch (Exception ex) { error = ex.Message; }

            string installed = S(d, "version");
            bool upd = latest != null && installed != null && !string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase);
            ctx.Out.Result("install.check", Json.Obj(
                "feed", FeedUrl,
                "latest", latest,
                "url", url,
                "sha512", sha,
                "size", size,
                "sizeHuman", size > 0 ? Fs.FormatSize(size) : null,
                "releaseDate", released,
                "installedVersion", installed,
                "updateAvailable", upd,
                "desktop", d,
                "cli", cli,
                "feedError", error,
                "items", new List<object>
                {
                    Json.Obj("name", Core.L.T("当前桌面端", "Current desktop app"), "value", installed ?? Core.L.T("未安装", "Not installed")),
                    Json.Obj("name", Core.L.T("最新版本", "Latest version"), "value", latest ?? Core.L.T("(获取失败)", "(fetch failed)")),
                    Json.Obj("name", Core.L.T("安装包大小", "Package size"), "value", size > 0 ? Fs.FormatSize(size) : "-"),
                    Json.Obj("name", Core.L.T("发布日期", "Release date"), "value", released ?? "-"),
                    Json.Obj("name", "CLI", "value", B(cli, "found") ? S(cli, "path") : Core.L.T("未检测到", "Not detected"))
                },
                "count", 5,
                "columns", new[] { "name", "value" }));
            return error == null ? ExitCodes.Ok : ExitCodes.Partial;
        }

        static string Rx(string text, string pattern)
        {
            if (text == null) return null;
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        // ================================================================ elevate.run
        static int RunElevate(Ctx ctx)
        {
            var inner = ctx.Args.Positional;
            if (inner.Count == 0) throw ToolException.Usage(Core.L.T("缺少要执行的命令", "Missing command to run"), Core.L.T("用法：elevate.run -- <命令> [参数...]", "Usage: elevate.run -- <command> [args...]"));
            if (IsElevated())
            {
                // 已经是管理员：直接转交
                throw ToolException.Usage(Core.L.T("当前已是管理员，无需提权", "Already running as administrator; no elevation needed"), Core.L.T("直接运行：", "Run it directly: ") + string.Join(" ", inner.ToArray()));
            }

            string tmp = Path.Combine(Path.GetTempPath(), "dsh-toolbox-elev-" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".json");
            var sb = new StringBuilder();
            foreach (var a in inner) sb.Append(Quote(a)).Append(' ');
            sb.Append("--json --capture-out ").Append(Quote(tmp));

            var psi = new ProcessStartInfo(ctx.ExePath, sb.ToString())
            {
                UseShellExecute = true,      // 必须：Verb=runas 需要 ShellExecute
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                var p = Process.Start(psi);
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } throw ToolException.Timeout(Core.L.T("提权进程超时", "Elevation process timed out")); }
                int code = p.ExitCode;
                string payload = File.Exists(tmp) ? File.ReadAllText(tmp, Encoding.UTF8) : null;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (string.IsNullOrEmpty(payload))
                    throw new ToolException("E_ELEVATION", Core.L.T("提权进程未返回结果（exit={0}）", "Elevation process returned no result (exit={0})", code), Core.L.T("可能是候选进程上下文不同或被 UAC 取消", "UAC may have been cancelled, or the process context differs"), ExitCodes.Error);
                Console.Out.WriteLine(payload);   // 原样透传子进程信封（必须走 stdout）
                return code;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = 用户取消了 UAC
                if (ex.NativeErrorCode == 1223)
                    throw new ToolException("E_UAC_CANCELLED", Core.L.T("用户取消了 UAC 提权", "The user cancelled the UAC elevation"), Core.L.T("需要管理员权限才能完成该操作", "Administrator privileges are required for this operation"), ExitCodes.Denied);
                throw new ToolException("E_ELEVATION", Core.L.T("提权失败：", "Elevation failed: ") + ex.Message, null, ExitCodes.Error);
            }
        }

        static string Quote(string s)
        {
            if (s == null) return "\"\"";
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ================================================================ 字典读取小工具（命令模块自足，不依赖 GUI 层）
        static string S(Dictionary<string, object> d, string key, string def = "")
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            var v = d[key];
            if (v is bool) return ((bool)v) ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        static bool B(Dictionary<string, object> d, string key, bool def = false)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToBoolean(d[key]); } catch { return def; }
        }
        static long L(Dictionary<string, object> d, string key, long def = 0)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToInt64(d[key]); } catch { return def; }
        }

        // ================================================================ 公共小工具
        public static bool IsElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static List<Process> ProcessesByName(string name)
        {
            var list = new List<Process>();
            try { list.AddRange(Process.GetProcessesByName(name)); } catch { }
            return list;
        }

        static string SafeStart(Process p) { try { return p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { return null; } }
        static long SafeMem(Process p) { try { return p.WorkingSet64 / (1024 * 1024); } catch { return 0; } }
        static string SafeMain(Process p) { try { return p.MainWindowTitle; } catch { return null; } }

        public static string FileVersion(string path)
        {
            try { return FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { return null; }
        }

        public static List<Dictionary<string, object>> UninstallEntries()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var root in new[]
            {
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            })
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine)
                    {
                        var sub = root.StartsWith("HKEY_CURRENT_USER") ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;
                        string rel = root.Substring(root.IndexOf('\\') + 1);
                        using (var k = sub.OpenSubKey(rel))
                        {
                            if (k == null) continue;
                            foreach (var n in k.GetSubKeyNames())
                            {
                                using (var e = k.OpenSubKey(n))
                                {
                                    if (e == null) continue;
                                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                                    foreach (var v in new[] { "DisplayName", "DisplayVersion", "InstallLocation", "UninstallString" })
                                    {
                                        object val = null; try { val = e.GetValue(v); } catch { }
                                        if (val != null) d[v] = Convert.ToString(val, CultureInfo.InvariantCulture);
                                    }
                                    if (d.Count > 0) { d["key"] = n; list.Add(d); }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        public static string RegStr(string key, string name)
        {
            try { return Convert.ToString(Microsoft.Win32.Registry.GetValue(key, name, null), CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        public static object RegRead(string key, string name, object def)
        {
            try { return Microsoft.Win32.Registry.GetValue(key, name, def) ?? def; } catch { return def; }
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        public static string RunHidden(string file, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                return (so.Result ?? "") + (se.Result ?? "");
            }
        }

        public static string RunPowerShell(string command, int timeoutMs)
        {
            return RunHidden("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"", timeoutMs);
        }

        public static bool TestTcp(string host, int port, int timeoutMs)
        {
            try
            {
                var c = new System.Net.Sockets.TcpClient();
                var iar = c.BeginConnect(host, port, null, null);
                bool ok = iar.AsyncWaitHandle.WaitOne(timeoutMs, false) && c.Connected;
                c.Close();
                return ok;
            }
            catch { return false; }
        }

        public static bool ProbeUrl(string url, int timeoutMs, out string detail)
        {
            detail = "";
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    detail = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + resp.StatusCode;
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400;
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsProcessorFeaturePresent(uint feature);
    }
}

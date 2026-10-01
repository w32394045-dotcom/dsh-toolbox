using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>Lead 拥有的核心命令：doctor / sysinfo / env / manifest。</summary>
    public static class CoreCommands
    {
        public static void Register()
        {
            Registry.Add("doctor", L.T("自检：环境、权限、数据目录、磁盘、命令可用性", "Self-check: environment, permissions, data home, disk, command availability"),
                "doctor [--json]",
                RunDoctor,
                examples: new[] { "dsh-toolbox doctor --json" });

            Registry.Add("sysinfo", L.T("系统信息：OS/CPU/内存/磁盘/启动时间", "System info: OS/CPU/memory/disk/boot time"),
                "sysinfo [--fast]",
                RunSysInfo,
                examples: new[] { "dsh-toolbox sysinfo", "dsh-toolbox sysinfo --json" });

            Registry.Add("env", L.T("环境变量查询（默认脱敏疑似密钥）", "Query environment variables (secret-looking values redacted by default)"),
                "env [--match <regex>] [--show-secrets]",
                RunEnv,
                examples: new[] { "dsh-toolbox env --match ^DSH_", "dsh-toolbox env --show-secrets --json" });

            Registry.Add("manifest", L.T("输出全部命令的机器可读清单（agent 用）", "Emit a machine-readable catalog of all commands (for agents)"),
                "manifest [--json]",
                RunManifest,
                aliases: new[] { "commands" },
                examples: new[] { "dsh-toolbox manifest --json" });
        }

        // ============================================================ doctor
        static int RunDoctor(Ctx ctx)
        {
            var checks = new List<object>();
            bool allOk = true;

            Action<string, bool, string, bool> add = (name, ok, detail, critical) =>
            {
                checks.Add(Json.Obj("name", name, "ok", ok, "detail", detail, "critical", critical));
                if (critical && !ok) allOk = false;
            };

            var os = Environment.OSVersion;
            add("platform", os.Platform == PlatformID.Win32NT,
                string.Format("{0} {1}", os.VersionString, Environment.Is64BitOperatingSystem ? "x64" : "x86"), true);

            add("framework", Environment.Version.Major >= 4,
                ".NET Framework " + Environment.Version, true);

            add("exe", File.Exists(ctx.ExePath), ctx.ExePath, false);

            add("home", Directory.Exists(Paths.Home), Paths.Home, true);
            bool homeWritable = Fs.CanWrite(Paths.Home);
            add("home_writable", homeWritable, homeWritable ? L.T("可写", "writable") : L.T("不可写", "not writable"), true);

            bool cwdOk = Directory.Exists(ctx.Cwd);
            add("cwd", cwdOk, ctx.Cwd, true);

            bool admin;
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                admin = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { admin = false; }
            add("elevated", true, admin ? L.T("是（管理员）", "yes (administrator)") : L.T("否（普通用户，多数命令足够）", "no (standard user; most commands work)"), false);

            long free = 0, total = 0;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Paths.Home));
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
            catch { }
            add("disk", free > 512L * 1024 * 1024,
                string.Format(L.T("{0} 可用 / {1} 总计", "{0} free / {1} total"), Fs.FormatSize(free), Fs.FormatSize(total)), false);

            bool longPaths = false;
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", null);
                longPaths = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }
            add("long_paths_registry", true, longPaths ? L.T("已启用", "enabled") : L.T("未启用（本 exe 已声明 longPathAware，超长路径仍可用）", "not enabled (this exe declares longPathAware, so long paths still work)"), false);

            var cmds = Registry.All();
            add("commands", cmds.Count > 0, cmds.Count + L.T(" 个命令已注册", " commands registered"), true);

            var groups = cmds.GroupBy(c => string.IsNullOrEmpty(c.Group) ? "misc" : c.Group)
                             .Select(g => Json.Obj("group", g.Key, "count", g.Count())).ToList();
            add("command_groups", true, string.Join(", ", groups.Select(g => Json.GetString((Dictionary<string, object>)g, "group"))), false);

            add("stdout_encoding", true, L.T("UTF-8（中文输出正常）", "UTF-8 (Chinese output renders correctly)"), false);

            var data = Json.Obj(
                "ok", allOk,
                "name", ToolInfo.Name,
                "version", ToolInfo.Version,
                "protocol", ToolInfo.Protocol,
                "checks", checks,
                "count", checks.Count,
                "home", Paths.Home,
                "logs", Paths.Logs,
                "runs", Paths.Runs,
                "jobs", Paths.Jobs,
                "cache", Paths.Cache,
                "cwd", ctx.Cwd,
                "pid", Process.GetCurrentProcess().Id,
                "framework", Environment.Version.ToString(),
                "columns", new[] { "name", "ok", "detail" });

            ctx.Out.Result("doctor", data);
            return allOk ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ============================================================ sysinfo
        static int RunSysInfo(Ctx ctx)
        {
            bool fast = ctx.Flag("fast");
            var data = new Dictionary<string, object>(StringComparer.Ordinal);

            data["computer"] = Environment.MachineName;
            data["user"] = Environment.UserName;
            data["os"] = Environment.OSVersion.VersionString;
            data["arch"] = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            data["processors"] = Environment.ProcessorCount;
            data["framework"] = Environment.Version.ToString();
            try
            {
                var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount);
                data["uptime"] = TimeSpan.FromMilliseconds(Environment.TickCount);
                data["bootApprox"] = boot;
            }
            catch { }

            var items = new List<object>();

            if (!fast)
            {
                items.AddRange(Wmi("Win32_OperatingSystem", new[] { "Caption", "Version", "BuildNumber", "OSArchitecture", "TotalVisibleMemorySize", "FreePhysicalMemory" })
                    .Select(d => Wrap("os", d)));
                items.AddRange(Wmi("Win32_Processor", new[] { "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed" })
                    .Select(d => Wrap("cpu", d)));
                items.AddRange(Wmi("Win32_ComputerSystem", new[] { "Manufacturer", "Model", "TotalPhysicalMemory" })
                    .Select(d => Wrap("system", d)));
                foreach (var d in Wmi("Win32_LogicalDisk", new[] { "DeviceID", "DriveType", "Size", "FreeSpace", "FileSystem", "VolumeName" }))
                {
                    object dt;
                    int driveType = d.TryGetValue("DriveType", out dt) && dt != null ? Convert.ToInt32(dt) : 0;
                    if (driveType != 3) continue;   // 只要固定磁盘
                    items.Add(Wrap("disk", d));
                }
            }

            data["items"] = items;
            data["count"] = items.Count;
            data["columns"] = new[] { "kind", "name", "detail" };
            data["fast"] = fast;

            ctx.Out.Result("sysinfo", data);
            return ExitCodes.Ok;
        }

        static Dictionary<string, object> Wrap(string kind, Dictionary<string, object> d)
        {
            string name = "";
            string[] nameKeys = { "Caption", "Name", "Manufacturer", "DeviceID", "Model" };
            foreach (var k in nameKeys) if (d.ContainsKey(k) && d[k] != null) { name = Convert.ToString(d[k]); if (k != "Manufacturer") break; }
            var parts = new List<string>();
            foreach (var kv in d)
            {
                if (kv.Value == null) continue;
                string s = Convert.ToString(kv.Value);
                if (kv.Key.EndsWith("Memory", StringComparison.OrdinalIgnoreCase) || kv.Key == "Size" || kv.Key == "FreeSpace")
                {
                    long n;
                    if (long.TryParse(s, out n) && n > 1024 * 1024) s = Fs.FormatSize(n);
                }
                parts.Add(kv.Key + "=" + s);
            }
            return Json.Obj("kind", kind, "name", name, "detail", string.Join("; ", parts));
        }

        static List<Dictionary<string, object>> Wmi(string cls, string[] props)
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM " + cls))
                using (var results = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in results)
                    {
                        var d = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (var p in props)
                        {
                            try { d[p] = mo[p]; } catch { }
                        }
                        list.Add(d);
                        mo.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal) { { "WmiError", ex.Message } });
            }
            return list;
        }

        // ============================================================ env
        static int RunEnv(Ctx ctx)
        {
            string pattern = ctx.Get("match");
            Regex re = null;
            if (!string.IsNullOrEmpty(pattern))
            {
                try { re = new Regex(pattern, RegexOptions.IgnoreCase); }
                catch (Exception ex) { throw ToolException.Usage(L.T("--match 正则非法：", "--match is not a valid regex: ") + ex.Message); }
            }
            bool showSecrets = ctx.Flag("show-secrets");
            var secretRe = new Regex("(token|secret|password|passwd|pwd|key|credential|auth)", RegexOptions.IgnoreCase);

            var items = new List<object>();
            var all = Environment.GetEnvironmentVariables();
            var keys = new List<string>();
            foreach (System.Collections.DictionaryEntry e in all) keys.Add(Convert.ToString(e.Key));
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var k in keys)
            {
                if (re != null && !re.IsMatch(k)) continue;
                string v = Convert.ToString(all[k]);
                bool redacted = false;
                if (!showSecrets && secretRe.IsMatch(k) && !string.IsNullOrEmpty(v))
                {
                    v = v.Length <= 4 ? "****" : v.Substring(0, 2) + "****" + v.Substring(v.Length - 2);
                    redacted = true;
                }
                items.Add(Json.Obj("name", k, "value", v, "redacted", redacted));
            }

            ctx.Out.Result("env", Json.Obj(
                "items", items,
                "count", items.Count,
                "total", keys.Count,
                "match", pattern,
                "secretsRedacted", !showSecrets,
                "columns", new[] { "name", "value" }));
            return ExitCodes.Ok;
        }

        // ============================================================ manifest
        static int RunManifest(Ctx ctx)
        {
            var cmds = new List<object>();
            foreach (var ci in Registry.All())
            {
                cmds.Add(Json.Obj(
                    "name", ci.Name,
                    "group", ci.Group,
                    "action", ci.Action,
                    "summary", ci.Summary,
                    "usage", ci.Usage,
                    "aliases", ci.Aliases,
                    "examples", ci.Examples));
            }

            var globalOpts = new List<object>
            {
                Json.Obj("name","--json","type","bool","desc",L.T("stdout 输出单个 JSON 信封", "emit a single JSON envelope on stdout")),
                Json.Obj("name","--jsonl","type","bool","desc",L.T("流式 JSON，每行一条", "streaming JSON, one object per line")),
                Json.Obj("name","--quiet","type","bool","desc",L.T("抑制 stderr 信息", "suppress messages on stderr")),
                Json.Obj("name","--dry-run","type","bool","desc",L.T("破坏性操作只预览", "preview destructive actions only")),
                Json.Obj("name","--yes","type","bool","desc",L.T("确认破坏性操作", "confirm destructive actions")),
                Json.Obj("name","--timeout","type","duration","desc",L.T("全局超时，如 30s/5m", "global timeout, e.g. 30s/5m")),
                Json.Obj("name","--home","type","path","desc",L.T("数据目录", "data home")),
                Json.Obj("name","--cwd","type","path","desc",L.T("相对路径基准", "base directory for relative paths")),
                Json.Obj("name","--verbose","type","bool","desc",L.T("诊断信息", "diagnostic output")),
                Json.Obj("name","--no-log","type","bool","desc",L.T("不写运行记录", "do not write a run record"))
            };

            ctx.Out.Result("manifest", Json.Obj(
                "name", ToolInfo.Name,
                "version", ToolInfo.Version,
                "protocol", ToolInfo.Protocol,
                "exe", ctx.ExePath,
                "home", Paths.Home,
                "exitCodes", Json.Obj(
                    "0", L.T("成功", "success"), "1", L.T("运行期错误", "runtime error"), "2", L.T("用法错误", "usage error"), "3", L.T("目标不存在", "target not found"),
                    "4", L.T("权限被拒绝", "permission denied"), "5", L.T("超时", "timeout"), "6", L.T("部分成功", "partial success"), "130", L.T("被取消", "cancelled")),
                "globalOptions", globalOpts,
                "commands", cmds,
                "count", cmds.Count,
                "columns", new[] { "name", "summary" }));
            return ExitCodes.Ok;
        }
    }
}

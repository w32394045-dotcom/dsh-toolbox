using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    // ==================================================================== 原生小工具
    /// <summary>零依赖 P/Invoke 小工具（提权判定）。不引入任何第三方库。</summary>
    internal static class SysNative
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevationClass = 20;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr hProcess, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
                                               out int TokenInformation, int TokenInformationLength, out int ReturnLength);

        /// <summary>进程令牌是否提权。无法打开（跨会话/权限不足）时返回 null，而不是抛异常。</summary>
        public static bool? IsElevated(int pid)
        {
            if (pid <= 0) return null;
            IntPtr hProc = IntPtr.Zero, hTok = IntPtr.Zero;
            try
            {
                hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProc == IntPtr.Zero) return null;
                if (!OpenProcessToken(hProc, TOKEN_QUERY, out hTok) || hTok == IntPtr.Zero) return null;
                int val, ret;
                if (!GetTokenInformation(hTok, TokenElevationClass, out val, 4, out ret)) return null;
                return val != 0;
            }
            catch { return null; }
            finally
            {
                if (hTok != IntPtr.Zero) { try { CloseHandle(hTok); } catch { } }
                if (hProc != IntPtr.Zero) { try { CloseHandle(hProc); } catch { } }
            }
        }

        public static bool CurrentProcessElevated()
        {
            bool? v = IsElevated(Process.GetCurrentProcess().Id);
            if (v.HasValue) return v.Value;
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    // ==================================================================== 进程模型
    internal sealed class SysProc
    {
        public int Pid;
        public int Ppid;
        public string Name = "";
        public string Path;
        public string CommandLine;
        public DateTime? StartTime;
        public double CpuSeconds;
        public long WorkingSet;
        public long PrivateBytes;
        public int Handles;
        public int Threads;
        public int SessionId;
        public bool? Elevated;
        public string Owner;

        public bool Alive
        {
            get
            {
                try { using (var p = Process.GetProcessById(Pid)) return !p.HasExited; }
                catch { return false; }
            }
        }

        public Dictionary<string, object> ToDict(bool withCmd, bool withOwner)
        {
            var d = Json.Obj(
                "pid", Pid,
                "ppid", Ppid,
                "name", Name,
                "path", Path,
                "cmdline", withCmd ? CommandLine : null,
                "startTime", StartTime,
                "cpuSec", Math.Round(CpuSeconds, 3),
                "memMB", Math.Round(WorkingSet / 1048576.0, 1),
                "memBytes", WorkingSet,
                "handles", Handles,
                "threads", Threads,
                "session", SessionId,
                "elevated", Elevated.HasValue ? (object)Elevated.Value : null);
            if (withOwner) d["owner"] = Owner;
            return d;
        }
    }

    // ==================================================================== 进程枚举
    internal static class SysProcs
    {
        /// <summary>结束这些进程会立刻破坏系统（LSASS/CSRSS 等），一律拒绝——只有 --allow-protected 才放行。</summary>
        public static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "registry", "idle", "memory compression", "smss", "csrss", "wininit",
            "services", "lsass", "winlogon", "fontdrvhost", "memcompression"
        };

        static int ToInt(object o) { if (o == null) return 0; try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; } }
        static long ToLong(object o) { if (o == null) return 0; try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; } }
        static ulong ToULong(object o) { if (o == null) return 0; try { return Convert.ToUInt64(o, CultureInfo.InvariantCulture); } catch { return 0; } }

        /// <summary>最近一次 Snapshot 的分段耗时与"读不到"计数（诊断用，写进 JSON 便于定位慢在哪）。</summary>
        public static long LastWmiMs, LastFillMs, LastElevateMs, LastEnumerateMs;
        public static int LastPathUnavailable, LastElevationUnknown;

        public static string StripExe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        /// <summary>一次 WMI 枚举 + 缺失字段兜底；WMI 完全不可用时降级到 Process 类。</summary>
        public static List<SysProc> Snapshot(out string wmiError)
        {
            wmiError = null;
            var list = new List<SysProc>();
            bool wmiOk = false;
            var tAll = Stopwatch.StartNew();
            var tWmi = Stopwatch.StartNew();
            try
            {
                const string q = "SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,CreationDate," +
                                 "KernelModeTime,UserModeTime,WorkingSetSize,HandleCount,ThreadCount,SessionId FROM Win32_Process";
                using (var searcher = new ManagementObjectSearcher(q))
                using (var results = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in results)
                    {
                        try
                        {
                            var p = new SysProc();
                            p.Pid = ToInt(mo["ProcessId"]);
                            p.Ppid = ToInt(mo["ParentProcessId"]);
                            p.Name = StripExe(Convert.ToString(mo["Name"], CultureInfo.InvariantCulture));
                            p.Path = Convert.ToString(mo["ExecutablePath"], CultureInfo.InvariantCulture);
                            p.CommandLine = Convert.ToString(mo["CommandLine"], CultureInfo.InvariantCulture);
                            string cd = Convert.ToString(mo["CreationDate"], CultureInfo.InvariantCulture);
                            if (!string.IsNullOrEmpty(cd))
                            {
                                try { p.StartTime = ManagementDateTimeConverter.ToDateTime(cd); } catch { }
                            }
                            p.CpuSeconds = (ToULong(mo["KernelModeTime"]) + ToULong(mo["UserModeTime"])) / 10000000.0;
                            p.WorkingSet = ToLong(mo["WorkingSetSize"]);
                            p.Handles = ToInt(mo["HandleCount"]);
                            p.Threads = ToInt(mo["ThreadCount"]);
                            p.SessionId = ToInt(mo["SessionId"]);
                            list.Add(p);
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
                wmiOk = true;
            }
            catch (Exception ex) { wmiError = ex.Message; }
            tWmi.Stop();
            LastWmiMs = tWmi.ElapsedMilliseconds;

            if (!wmiOk)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var sp = new SysProc();
                        sp.Pid = p.Id;
                        sp.Name = StripExe(p.ProcessName);
                        try { sp.WorkingSet = p.WorkingSet64; } catch { }
                        try { sp.PrivateBytes = p.PrivateMemorySize64; } catch { }
                        try { sp.Handles = p.HandleCount; } catch { }
                        try { sp.Threads = p.Threads.Count; } catch { }
                        try { sp.StartTime = p.StartTime; } catch { }
                        try { sp.CpuSeconds = p.TotalProcessorTime.TotalSeconds; } catch { }
                        try { sp.Path = p.MainModule.FileName; } catch { }
                        list.Add(sp);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }

            // 补齐非管理员也有权读取的路径（其它会话的进程 WMI 可能返回 null）
            var tFill = Stopwatch.StartNew();
            int filled = 0, pathUnavailable = 0;
            foreach (var p in list)
            {
                if (!string.IsNullOrEmpty(p.Path)) continue;
                if (filled > 400) { pathUnavailable++; continue; }
                try
                {
                    using (var pr = Process.GetProcessById(p.Pid))
                    {
                        p.Path = pr.MainModule.FileName;
                        if (p.StartTime == null) { try { p.StartTime = pr.StartTime; } catch { } }
                        if (p.WorkingSet == 0) { try { p.WorkingSet = pr.WorkingSet64; } catch { } }
                        filled++;
                    }
                }
                catch { pathUnavailable++; }
            }
            tFill.Stop();
            LastFillMs = tFill.ElapsedMilliseconds;
            LastPathUnavailable = pathUnavailable;

            var tElev = Stopwatch.StartNew();
            int elevUnknown = 0;
            foreach (var p in list)
            {
                try { p.Elevated = SysNative.IsElevated(p.Pid); } catch { }
                if (!p.Elevated.HasValue) elevUnknown++;
            }
            tElev.Stop();
            LastElevateMs = tElev.ElapsedMilliseconds;
            LastElevationUnknown = elevUnknown;
            LastEnumerateMs = tAll.ElapsedMilliseconds;
            return list;
        }

        public static Dictionary<int, SysProc> ByPid(List<SysProc> list)
        {
            var d = new Dictionary<int, SysProc>();
            foreach (var p in list) d[p.Pid] = p;
            return d;
        }

        /// <summary>宽松匹配：正则 > 通配 > 子串（不区分大小写）。名称额外兼容带/不带 .exe。</summary>
        public static bool MatchText(string pattern, string value, Regex re)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (string.IsNullOrEmpty(value)) return false;
            if (re != null) return re.IsMatch(value);
            if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0)
                return Glob.IsMatch(pattern.Replace('\\', '/'), value.Replace('\\', '/'));
            return value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool MatchName(string pattern, string name)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (MatchText(pattern, name, null)) return true;
            string withExe = name + ".exe";
            return MatchText(pattern, withExe, null);
        }

        /// <summary>破坏性命令用：精确匹配或显式通配，绝不做子串匹配。</summary>
        public static bool MatchExactName(string pattern, string name)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0)
                return Glob.IsMatch(pattern.Replace('\\', '/'), name.Replace('\\', '/'));
            return string.Equals(StripExe(pattern), name, StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchExactPath(string pattern, string path)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (string.IsNullOrEmpty(path)) return false;
            if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0)
                return Glob.IsMatch(pattern.Replace('\\', '/'), path.Replace('\\', '/'));
            return string.Equals(pattern.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<int, List<int>> ChildrenOf(List<SysProc> list)
        {
            var map = new Dictionary<int, List<int>>();
            foreach (var p in list)
            {
                List<int> kids;
                if (!map.TryGetValue(p.Ppid, out kids)) { kids = new List<int>(); map[p.Ppid] = kids; }
                kids.Add(p.Pid);
            }
            foreach (var k in map.Keys.ToList()) map[k].Sort();
            return map;
        }

        /// <summary>收集后代 PID（深度优先，含自身）。</summary>
        public static List<int> Descendants(List<SysProc> list, int root)
        {
            var kids = ChildrenOf(list);
            var outList = new List<int>();
            var stack = new Stack<int>();
            stack.Push(root);
            var seen = new HashSet<int>();
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (!seen.Add(cur)) continue;
                outList.Add(cur);
                List<int> c;
                if (kids.TryGetValue(cur, out c))
                    foreach (var k in c) stack.Push(k);
            }
            return outList;
        }

        public static List<int> PidsFromArgs(Ctx ctx)
        {
            var pids = new List<int>();
            foreach (var s in ctx.GetAll("id")) AddPid(pids, s);
            foreach (var s in ctx.GetAll("pid")) AddPid(pids, s);
            return pids;
        }

        static void AddPid(List<int> pids, string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            int v;
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0)
                pids.Add(v);
            else
                throw ToolException.Usage("无效的 PID：" + s);
        }

        public static string GetOwner(int pid)
        {
            try
            {
                using (var mo = new ManagementObject("Win32_Process.Handle=\"" + pid.ToString(CultureInfo.InvariantCulture) + "\""))
                {
                    using (var op = mo.InvokeMethod("GetOwner", null, null))
                    {
                        if (op == null) return null;
                        string user = Convert.ToString(op["User"], CultureInfo.InvariantCulture);
                        string dom = Convert.ToString(op["Domain"], CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(user)) return null;
                        return string.IsNullOrEmpty(dom) ? user : dom + "\\" + user;
                    }
                }
            }
            catch { }
            return null;
        }

        public static Regex Compile(string pattern, string opt)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (Exception ex) { throw ToolException.Usage(opt + " 正则非法：" + ex.Message); }
        }
    }

    // ==================================================================== proc.*
    /// <summary>进程类命令（sysdev 拥有）：proc.list / tree / find / kill / wait / port。</summary>
    public static class ProcCommands
    {
        public static void Register()
        {
            Registry.Add("proc.list", "列出进程：PID/名称/路径/命令行/启动时间/CPU/内存/是否提权",
                "proc list [--name <s|glob>] [--name-regex <re>] [--path <s|glob>] [--cmdline-regex <re>] [--port <n>] [--pid <n>] [--elevated] [--sort pid|name|cpu|mem|start|path] [--reverse] [--top <n>] [--owner]",
                RunList,
                aliases: new[] { "ps" },
                examples: new[] { "dsh-toolbox proc list --json", "dsh-toolbox proc list --name \"DeepSeek Harness\"", "dsh-toolbox proc list --sort mem --top 15 --json" });

            Registry.Add("proc.tree", "进程父子树（缩进 + parent 字段）",
                "proc tree [--root <pid|name>] [--max-depth <n>]",
                RunTree,
                examples: new[] { "dsh-toolbox proc tree --root 9096", "dsh-toolbox proc tree --json" });

            Registry.Add("proc.find", "按名称/路径/命令行/端口定位进程，并给出同名同路径多实例（残留）判定",
                "proc find [--name <s|glob>] [--path <s|glob>] [--cmdline-regex <re>] [--port <n>] [--pid <n>]",
                RunFind,
                examples: new[] { "dsh-toolbox proc find --name \"DeepSeek Harness\" --json", "dsh-toolbox proc find --port 19387" });

            Registry.Add("proc.kill", "结束进程：先 CloseMainWindow 优雅退出，再 taskkill /T /F（需 --yes）",
                "proc kill (--id <pid>|--name <exact|glob>|--path <exact|glob>|--port <n>) [--tree] [--force] [--grace <dur>] [--timeout <dur>] [--dry-run|--yes]",
                RunKill,
                examples: new[] { "dsh-toolbox proc kill --name notepad --dry-run --json", "dsh-toolbox proc kill --name notepad --yes" });

            Registry.Add("proc.wait", "等待进程退出或出现（--timeout 上限）",
                "proc wait (--id <pid>|--name <s|glob>|--path <s|glob>|--port <n>) [--for exit|appear] [--interval <dur>] [--timeout <dur>]",
                RunWait,
                examples: new[] { "dsh-toolbox proc wait --name notepad --for exit --timeout 30s", "dsh-toolbox proc wait --name setup --for appear --timeout 2m --json" });

            Registry.Add("proc.port", "端口占用归属：本地端口 -> 进程（PID/名称/路径/命令行）",
                "proc port --port <n> [--all]",
                RunPort,
                aliases: new[] { "port" },
                examples: new[] { "dsh-toolbox proc port --port 19387 --json", "dsh-toolbox proc port 19387" });
        }

        // ---------------------------------------------------------------- list
        static int RunList(Ctx ctx)
        {
            string wmiErr;
            var procs = SysProcs.Snapshot(out wmiErr);
            if (!string.IsNullOrEmpty(wmiErr)) ctx.Out.Warn("WMI 枚举失败，已降级：" + wmiErr);

            string namePat = ctx.Get("name");
            string nameRe = ctx.Get("name-regex");
            string pathPat = ctx.Get("path");
            string cmdRe = ctx.Get("cmdline-regex");
            string portArg = ctx.Get("port");
            int onlyPid = ctx.Args.GetInt("pid", 0);
            bool elevatedOnly = ctx.Flag("elevated");
            bool withCmd = !ctx.Flag("no-command-line");
            bool withOwner = ctx.Flag("owner");

            Regex rxCmd = SysProcs.Compile(cmdRe, "--cmdline-regex");
            Regex rxName = SysProcs.Compile(nameRe, "--name-regex");

            HashSet<int> portPids = null;
            if (!string.IsNullOrEmpty(portArg))
            {
                int pn;
                if (!int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pn))
                    throw ToolException.Usage("--port 需要端口号");
                portPids = SysPorts.PidsOnLocalPort(pn);
            }

            var matched = new List<SysProc>();
            foreach (var p in procs)
            {
                ctx.ThrowIfCancelled();
                if (onlyPid > 0 && p.Pid != onlyPid) continue;
                if (portPids != null && !portPids.Contains(p.Pid)) continue;
                if (!SysProcs.MatchName(namePat, p.Name)) continue;
                if (rxName != null && !rxName.IsMatch(p.Name)) continue;
                if (!string.IsNullOrEmpty(pathPat) && !SysProcs.MatchText(pathPat, p.Path, null)) continue;
                if (rxCmd != null && !rxCmd.IsMatch(p.CommandLine ?? "")) continue;
                if (elevatedOnly && !(p.Elevated ?? false)) continue;
                matched.Add(p);
            }

            if (withOwner)
                foreach (var p in matched) p.Owner = SysProcs.GetOwner(p.Pid);

            string sort = (ctx.Get("sort") ?? "pid").ToLowerInvariant();
            Comparison<SysProc> cmp;
            switch (sort)
            {
                case "name": cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case "cpu": cmp = (a, b) => a.CpuSeconds.CompareTo(b.CpuSeconds); break;
                case "mem": cmp = (a, b) => a.WorkingSet.CompareTo(b.WorkingSet); break;
                case "start": cmp = (a, b) => Nullable.Compare(a.StartTime, b.StartTime); break;
                case "path": cmp = (a, b) => string.Compare(a.Path ?? "", b.Path ?? "", StringComparison.OrdinalIgnoreCase); break;
                case "pid": cmp = (a, b) => a.Pid.CompareTo(b.Pid); break;
                default: throw ToolException.Usage("--sort 只支持 pid|name|cpu|mem|start|path");
            }
            matched.Sort(cmp);
            if (ctx.Flag("reverse")) matched.Reverse();

            int top = ctx.Args.GetInt("top", 0);
            bool truncated = false;
            if (top > 0 && matched.Count > top) { matched = matched.Take(top).ToList(); truncated = true; }
            if (truncated) ctx.Out.Truncated = true;

            var items = new List<object>();
            foreach (var p in matched) items.Add(p.ToDict(withCmd, withOwner));

            int elevatedCount = procs.Count(x => x.Elevated ?? false);
            ctx.Out.Result("proc.list", Json.Obj(
                "items", items,
                "count", items.Count,
                "total", procs.Count,
                "elevatedCount", elevatedCount,
                "elevationUnknown", SysProcs.LastElevationUnknown,
                "pathUnavailable", SysProcs.LastPathUnavailable,
                "pathUnavailableReason", SysProcs.LastPathUnavailable > 0
                    ? "其它会话/受保护进程的映像路径对普通用户不可读（WMI ExecutablePath 为空且 OpenProcess/MainModule 失败），属于「读不到」而不是「没有路径」"
                    : null,
                "sort", sort,
                "phaseMs", Json.Obj("enumerate", SysProcs.LastEnumerateMs, "wmi", SysProcs.LastWmiMs,
                                    "pathFill", SysProcs.LastFillMs, "elevation", SysProcs.LastElevateMs),
                "filters", Json.Obj("name", namePat, "nameRegex", nameRe, "path", pathPat,
                                    "cmdlineRegex", cmdRe, "port", portArg, "pid", onlyPid > 0 ? (object)onlyPid : null),
                "truncatedAt", truncated ? "top" : null,
                "wmiError", wmiErr,
                "columns", new[] { "pid", "ppid", "name", "path", "cpuSec", "memMB", "elevated" }));
            return ExitCodes.Ok;
        }

        // ---------------------------------------------------------------- tree
        static int RunTree(Ctx ctx)
        {
            string wmiErr;
            var procs = SysProcs.Snapshot(out wmiErr);
            if (!string.IsNullOrEmpty(wmiErr)) ctx.Out.Warn("WMI 枚举失败，已降级：" + wmiErr);

            var byPid = SysProcs.ByPid(procs);
            var kids = SysProcs.ChildrenOf(procs);
            int maxDepth = ctx.Args.GetInt("max-depth", 0);

            var roots = new List<int>();
            string rootArg = ctx.Get("root");
            if (!string.IsNullOrEmpty(rootArg))
            {
                int rpid;
                if (int.TryParse(rootArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out rpid))
                {
                    if (!byPid.ContainsKey(rpid)) throw ToolException.NotFound("PID 不存在：" + rpid);
                    roots.Add(rpid);
                }
                else
                {
                    foreach (var p in procs)
                        if (SysProcs.MatchExactName(rootArg, p.Name) || SysProcs.MatchText(rootArg, p.Name, null))
                            roots.Add(p.Pid);
                    if (roots.Count == 0) throw ToolException.NotFound("找不到名称匹配 --root 的进程：" + rootArg);
                }
            }
            else
            {
                foreach (var p in procs)
                    if (p.Ppid == 0 || !byPid.ContainsKey(p.Ppid)) roots.Add(p.Pid);
            }
            roots.Sort();

            var items = new List<object>();
            var lines = new List<object>();
            var seen = new HashSet<int>();
            Action<int, int> walk = null;
            walk = (pid, depth) =>
            {
                ctx.ThrowIfCancelled();
                if (seen.Contains(pid)) return;
                seen.Add(pid);
                if (maxDepth > 0 && depth > maxDepth) return;
                SysProc p;
                if (!byPid.TryGetValue(pid, out p)) return;
                List<int> c;
                int childCount = kids.TryGetValue(pid, out c) ? c.Count : 0;
                string line = new string(' ', depth * 2) + (depth > 0 ? "\\- " : "") +
                              p.Pid.ToString(CultureInfo.InvariantCulture) + "  " + p.Name;
                lines.Add(line);
                items.Add(Json.Obj(
                    "pid", p.Pid, "ppid", p.Ppid, "name", p.Name, "path", p.Path,
                    "depth", depth, "children", childCount,
                    "startTime", p.StartTime, "memMB", Math.Round(p.WorkingSet / 1048576.0, 1),
                    "line", line));
                if (c != null) foreach (var k in c) walk(k, depth + 1);
            };
            foreach (var r in roots) walk(r, 0);

            if (!ctx.Out.JsonMode && !ctx.Out.JsonLines)
                foreach (var l in lines) ctx.Out.Line(Convert.ToString(l));

            ctx.Out.Result("proc.tree", Json.Obj(
                "items", items,
                "count", items.Count,
                "roots", roots,
                "total", procs.Count,
                "tree", lines,
                "maxDepth", maxDepth,
                "wmiError", wmiErr,
                "columns", new[] { "pid", "ppid", "depth", "name" }));
            return ExitCodes.Ok;
        }

        // ---------------------------------------------------------------- find
        static int RunFind(Ctx ctx)
        {
            string wmiErr;
            var procs = SysProcs.Snapshot(out wmiErr);
            if (!string.IsNullOrEmpty(wmiErr)) ctx.Out.Warn("WMI 枚举失败，已降级：" + wmiErr);

            string namePat = ctx.Get("name");
            string pathPat = ctx.Get("path");
            string cmdRe = ctx.Get("cmdline-regex");
            string portArg = ctx.Get("port");
            var pids = SysProcs.PidsFromArgs(ctx);
            if (string.IsNullOrEmpty(namePat) && string.IsNullOrEmpty(pathPat) &&
                string.IsNullOrEmpty(cmdRe) && string.IsNullOrEmpty(portArg) && pids.Count == 0)
                throw ToolException.Usage("至少给出一个定位条件：--name/--path/--cmdline-regex/--port/--id",
                    "例：proc find --name \"DeepSeek Harness\" --json");

            Regex rxCmd = SysProcs.Compile(cmdRe, "--cmdline-regex");
            HashSet<int> portPids = null;
            List<SysPortRec> portRecs = null;
            if (!string.IsNullOrEmpty(portArg))
            {
                int pn;
                if (!int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pn))
                    throw ToolException.Usage("--port 需要端口号");
                portRecs = SysPorts.OnLocalPort(pn);
                portPids = new HashSet<int>(portRecs.Select(r => r.Pid));
            }

            bool withCmd = !ctx.Flag("no-command-line");
            var matched = new List<SysProc>();
            foreach (var p in procs)
            {
                ctx.ThrowIfCancelled();
                if (pids.Count > 0 && !pids.Contains(p.Pid)) continue;
                if (portPids != null && !portPids.Contains(p.Pid)) continue;
                if (!SysProcs.MatchName(namePat, p.Name)) continue;
                if (!string.IsNullOrEmpty(pathPat) && !SysProcs.MatchText(pathPat, p.Path, null)) continue;
                if (rxCmd != null && !rxCmd.IsMatch(p.CommandLine ?? "")) continue;
                matched.Add(p);
            }

            // 同名同路径多实例 = 残留判定
            var byNamePath = matched.GroupBy(p => p.Name.ToLowerInvariant() + "|" + (p.Path ?? "?").ToLowerInvariant())
                                    .ToDictionary(g => g.Key, g => g.ToList());
            var byName = matched.GroupBy(p => p.Name.ToLowerInvariant())
                                .ToDictionary(g => g.Key, g => g.ToList());

            var items = new List<object>();
            foreach (var p in matched.OrderBy(x => x.Pid))
            {
                string key = p.Name.ToLowerInvariant() + "|" + (p.Path ?? "?").ToLowerInvariant();
                var same = byNamePath[key];
                var sameName = byName[p.Name.ToLowerInvariant()];
                var d = p.ToDict(withCmd, false);
                d["samePathCount"] = same.Count;
                d["sameNameCount"] = sameName.Count;
                d["samePathPids"] = same.Select(x => x.Pid).OrderBy(x => x).Cast<object>().ToList();
                d["residual"] = same.Count > 1;
                d["instanceIndex"] = same.FindIndex(x => x.Pid == p.Pid) + 1;
                if (portRecs != null)
                    d["ports"] = portRecs.Where(r => r.Pid == p.Pid)
                                         .Select(r => (object)(r.Proto + " " + r.LocalAddr + ":" + r.LocalPort)).ToList();
                items.Add(d);
            }

            var groups = new List<object>();
            foreach (var kv in byNamePath.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key))
            {
                var g = kv.Value;
                groups.Add(Json.Obj(
                    "name", g[0].Name,
                    "path", g[0].Path,
                    "count", g.Count,
                    "residual", g.Count > 1,
                    "pids", g.Select(x => x.Pid).OrderBy(x => x).Cast<object>().ToList(),
                    "startTimes", g.Select(x => (object)x.StartTime).ToList()));
            }

            var residuals = groups.Where(o => Convert.ToBoolean(((Dictionary<string, object>)o)["residual"]))
                                  .Cast<object>().ToList();
            int multiInstance = residuals.Count > 0 ? 1 : 0;

            ctx.Out.Result("proc.find", Json.Obj(
                "items", items,
                "count", items.Count,
                "matched", items.Count,
                "scanned", procs.Count,
                "multiInstance", multiInstance == 1,
                "residualGroups", residuals,
                "residualGroupCount", residuals.Count,
                "groups", groups,
                "matchedBy", Json.Obj("name", namePat, "path", pathPat, "cmdlineRegex", cmdRe, "port", portArg,
                                      "ids", pids.Count > 0 ? (object)pids.ToArray() : null),
                "wmiError", wmiErr,
                "columns", new[] { "pid", "name", "path", "samePathCount", "residual" }));

            if (items.Count == 0) return ExitCodes.NotFound;
            return ExitCodes.Ok;
        }

        // ---------------------------------------------------------------- kill
        static int RunKill(Ctx ctx)
        {
            string wmiErr;
            var procs = SysProcs.Snapshot(out wmiErr);
            var targets = ResolveTargets(ctx, procs);
            if (targets.Count == 0)
                throw ToolException.NotFound("未找到匹配的进程", "先用 proc find 确认目标");

            int myPid = Process.GetCurrentProcess().Id;
            bool allowProtected = ctx.Flag("allow-protected");
            foreach (var t in targets)
            {
                if (t.Pid == myPid)
                    throw ToolException.Denied("拒绝结束 dsh-toolbox 自身进程（PID " + t.Pid + "）");
                if (t.Pid <= 4)
                    throw ToolException.Denied("拒绝结束系统空闲/System 进程（PID " + t.Pid + "）");
                if (!allowProtected && SysProcs.Protected.Contains(t.Name))
                    throw ToolException.Denied("拒绝结束受保护的系统关键进程：" + t.Name + "（PID " + t.Pid + "）",
                        "如确需，请显式加 --allow-protected");
            }

            bool tree = ctx.Flag("tree");
            bool force = ctx.Flag("force");
            TimeSpan grace = ctx.Args.GetSpan("grace", TimeSpan.FromSeconds(3));
            TimeSpan timeout = ctx.Args.GetSpan("timeout", TimeSpan.FromSeconds(10));

            // 计划：--tree 时展开子进程；执行顺序为"先子后父"
            var roots = targets.Select(t => t.Pid).ToList();
            var allPids = new List<int>();
            if (tree)
                foreach (var t in targets) allPids.AddRange(SysProcs.Descendants(procs, t.Pid));
            else
                allPids.AddRange(roots);
            var byPid = SysProcs.ByPid(procs);
            allPids = allPids.Distinct().ToList();
            var execOrder = allPids.OrderByDescending(pid =>
            {
                SysProc p;
                return byPid.TryGetValue(pid, out p) ? DepthOf(byPid, pid) : 0;
            }).ToList();

            var planItems = new List<object>();
            foreach (var pid in allPids)
            {
                SysProc p;
                byPid.TryGetValue(pid, out p);
                planItems.Add(Json.Obj(
                    "pid", pid,
                    "name", p != null ? p.Name : null,
                    "path", p != null ? p.Path : null,
                    "cmdline", p != null ? p.CommandLine : null,
                    "root", roots.Contains(pid),
                    "isSelf", pid == myPid,
                    "protected", p != null && SysProcs.Protected.Contains(p.Name),
                    "action", force ? "CloseMainWindow -> taskkill /T /F" : "CloseMainWindow -> Kill"));
            }

            string desc = string.Format("结束 {0} 个进程：{1}", allPids.Count,
                string.Join(", ", planItems.Take(5).Select(o => ((Dictionary<string, object>)o)["name"] + "(" + ((Dictionary<string, object>)o)["pid"] + ")").ToArray()));

            bool execute = ctx.ConfirmDestructive(desc);
            if (!execute)
            {
                ctx.Out.Result("proc.kill", Json.Obj(
                    "dryRun", true,
                    "wouldKill", planItems,
                    "count", planItems.Count,
                    "tree", tree,
                    "force", force,
                    "grace", grace,
                    "timeout", timeout,
                    "roots", roots.Cast<object>().ToList(),
                    "plan", planItems,
                    "wmiError", wmiErr,
                    "columns", new[] { "pid", "name", "path", "action" }));
                return ExitCodes.Ok;
            }

            var results = new List<object>();
            var failures = new List<object>();
            int deniedCount = 0;

            // 阶段 1：优雅退出
            var pending = new List<int>(execOrder);
            foreach (var pid in allPids)
            {
                ctx.ThrowIfCancelled();
                var rec = Json.Obj("pid", pid, "graceful", false, "method", null, "taskkillExit", null, "killed", false, "error", null);
                SysProc p;
                byPid.TryGetValue(pid, out p);
                if (p != null) rec["name"] = p.Name;
                bool windowClosed = false;
                try
                {
                    using (var pr = Process.GetProcessById(pid))
                    {
                        windowClosed = pr.CloseMainWindow();
                    }
                }
                catch (ArgumentException) { rec["killed"] = true; rec["method"] = "already-gone"; results.Add(rec); pending.Remove(pid); continue; }
                catch (Exception ex) { rec["error"] = ex.Message; }
                rec["graceful"] = windowClosed;

                if (windowClosed)
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < grace)
                    {
                        ctx.ThrowIfCancelled();
                        if (!Alive(pid)) break;
                        Thread.Sleep(100);
                    }
                }
                if (!Alive(pid)) { rec["killed"] = true; rec["method"] = windowClosed ? "CloseMainWindow" : "exited-before-kill"; pending.Remove(pid); }
                results.Add(rec);
            }

            // 阶段 2：强杀
            foreach (var pid in pending.ToList())
            {
                ctx.ThrowIfCancelled();
                var rec = results.OfType<Dictionary<string, object>>().FirstOrDefault(o => Convert.ToInt32(o["pid"]) == pid);
                if (rec == null) continue;
                if (!Alive(pid)) { rec["killed"] = true; rec["method"] = "exited"; continue; }
                try
                {
                    if (force)
                    {
                        int tkExit; string tkOut;
                        bool isRoot = roots.Contains(pid);
                        RunTaskkill(pid, tree && isRoot, out tkExit, out tkOut);
                        rec["taskkillExit"] = tkExit;
                        rec["taskkillOutput"] = string.IsNullOrEmpty(tkOut) ? null : tkOut.Trim();
                        rec["method"] = "taskkill" + (tree && isRoot ? " /T /F" : " /F");
                    }
                    else
                    {
                        using (var pr = Process.GetProcessById(pid)) pr.Kill();
                        rec["method"] = "Kill()";
                    }
                    pending.Remove(pid);
                }
                catch (System.ComponentModel.Win32Exception wex)
                {
                    rec["error"] = wex.Message;
                    if (wex.NativeErrorCode == 5) deniedCount++;   // ERROR_ACCESS_DENIED
                }
                catch (InvalidOperationException) { rec["killed"] = true; rec["method"] = "already-gone"; pending.Remove(pid); }
                catch (Exception ex) { rec["error"] = ex.Message; }
            }

            // 阶段 3：二次确认无残留。
            // 以 Process API 轮询为准（用户视角一致）；查不到一律保守当作"仍在运行"，
            // 因为"无法查询（权限不足）"不等于"已结束"。
            var sw2 = Stopwatch.StartNew();
            var survivors = allPids.Where(Alive).ToList();
            while (survivors.Count > 0 && sw2.Elapsed < timeout)
            {
                ctx.ThrowIfCancelled();
                Thread.Sleep(150);
                survivors = allPids.Where(Alive).ToList();
            }

            foreach (var rec in results.OfType<Dictionary<string, object>>())
            {
                int pid = Convert.ToInt32(rec["pid"]);
                if (survivors.Contains(pid))
                {
                    rec["killed"] = false;
                    string err = rec["error"] as string;
                    if (string.IsNullOrEmpty(err)) err = "二次确认仍在运行（可能被其它进程守护、权限不足或无法查询退出状态）";
                    rec["error"] = err;
                    failures.Add(Json.Obj("pid", pid, "error", err));
                }
                else if (!Convert.ToBoolean(rec["killed"]))
                {
                    rec["killed"] = true;
                    if (rec["method"] == null) rec["method"] = "exited";
                }
            }

            int exit = ExitCodes.Ok;
            if (survivors.Count > 0)
                exit = deniedCount > 0 && deniedCount >= allPids.Count ? ExitCodes.Denied : ExitCodes.Partial;

            ctx.Out.Result("proc.kill", Json.Obj(
                "killed", results.OfType<Dictionary<string, object>>().Count(o => Convert.ToBoolean(o["killed"])),
                "count", allPids.Count,
                "survivors", survivors,
                "items", results,
                "failures", failures,
                "tree", tree,
                "force", force,
                "verifyMs", sw2.ElapsedMilliseconds,
                "wmiError", wmiErr,
                "columns", new[] { "pid", "name", "method", "killed", "error" }));
            return exit;
        }

        static int DepthOf(Dictionary<int, SysProc> byPid, int pid)
        {
            int d = 0, cur = pid;
            var guard = new HashSet<int>();
            while (guard.Add(cur))
            {
                SysProc p;
                if (!byPid.TryGetValue(cur, out p) || p.Ppid <= 0) break;
                cur = p.Ppid;
                d++;
                if (d > 64) break;
            }
            return d;
        }

        /// <summary>
        /// 进程是否仍存活。**权限不足/查询失败时保守返回 true**：
        /// 绝不能把"查不到"当成"已经结束"——受保护进程（如 csrss）会因此被误报为已结束。
        /// </summary>
        static bool Alive(int pid)
        {
            if (pid <= 0) return false;
            Process p = null;
            try
            {
                p = Process.GetProcessById(pid);
                try { return !p.HasExited; }
                catch { return true; }          // 拿不到退出状态 -> 保守认为存活
            }
            catch (ArgumentException) { return false; }        // 该 PID 不存在
            catch (InvalidOperationException) { return false; }
            catch { return true; }                             // 无法判定 -> 保守认为存活
            finally { if (p != null) { try { p.Dispose(); } catch { } } }
        }

        static void RunTaskkill(int pid, bool tree, out int exitCode, out string output)
        {
            exitCode = -1;
            output = "";
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe",
                    (tree ? "/T " : "") + "/F /PID " + pid.ToString(CultureInfo.InvariantCulture));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var pr = Process.Start(psi))
                {
                    string o = pr.StandardOutput.ReadToEnd();
                    string e = pr.StandardError.ReadToEnd();
                    pr.WaitForExit(15000);
                    exitCode = pr.HasExited ? pr.ExitCode : -1;
                    output = (o + " " + e).Trim();
                }
            }
            catch (Exception ex) { output = ex.Message; }
        }

        static List<SysProc> ResolveTargets(Ctx ctx, List<SysProc> procs)
        {
            var ids = SysProcs.PidsFromArgs(ctx);
            string name = ctx.Get("name");
            string path = ctx.Get("path");
            string portArg = ctx.Get("port");
            if (ids.Count == 0 && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(path) && string.IsNullOrEmpty(portArg))
                throw ToolException.Usage("需要 --id/--pid/--name/--path/--port 之一", "例：proc kill --name notepad --yes");

            HashSet<int> portPids = null;
            if (!string.IsNullOrEmpty(portArg))
            {
                int pn;
                if (!int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pn))
                    throw ToolException.Usage("--port 需要端口号");
                portPids = SysPorts.PidsOnLocalPort(pn);
            }

            var targets = new List<SysProc>();
            foreach (var p in procs)
            {
                bool hit = false;
                if (ids.Count > 0) hit = ids.Contains(p.Pid);
                if (!hit && portPids != null) hit = portPids.Contains(p.Pid);
                if (!hit && !string.IsNullOrEmpty(name)) hit = SysProcs.MatchExactName(name, p.Name);
                if (!hit && !string.IsNullOrEmpty(path)) hit = SysProcs.MatchExactPath(path, p.Path);
                if (hit) targets.Add(p);
            }
            // 只在给了 --id 时允许匹配到已消失/快照里没有的 PID
            foreach (var id in ids)
                if (!targets.Any(t => t.Pid == id) && Alive(id))
                    targets.Add(new SysProc { Pid = id, Name = "?" });
            return targets.GroupBy(t => t.Pid).Select(g => g.First()).ToList();
        }

        // ---------------------------------------------------------------- wait
        static int RunWait(Ctx ctx)
        {
            string mode = (ctx.Get("for") ?? (ctx.Flag("appear") ? "appear" : "exit")).ToLowerInvariant();
            if (mode != "exit" && mode != "appear")
                throw ToolException.Usage("--for 只支持 exit|appear");

            TimeSpan timeout = ctx.Args.GetSpan("timeout", TimeSpan.FromSeconds(30));
            TimeSpan interval = ctx.Args.GetSpan("interval", TimeSpan.FromMilliseconds(500));
            if (interval <= TimeSpan.Zero) interval = TimeSpan.FromMilliseconds(500);

            var ids = SysProcs.PidsFromArgs(ctx);
            string name = ctx.Get("name");
            string path = ctx.Get("path");
            string portArg = ctx.Get("port");
            if (ids.Count == 0 && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(path) && string.IsNullOrEmpty(portArg))
                throw ToolException.Usage("需要 --id/--name/--path/--port 之一", "例：proc wait --name notepad --for exit --timeout 30s");

            int portNum = 0;
            if (!string.IsNullOrEmpty(portArg) &&
                !int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out portNum))
                throw ToolException.Usage("--port 需要端口号");

            var sw = Stopwatch.StartNew();
            int lastCount = -1;
            var finalPids = new List<int>();
            while (true)
            {
                ctx.ThrowIfCancelled();
                var hits = new List<int>();
                if (ids.Count > 0)
                {
                    foreach (var id in ids) if (Alive(id)) hits.Add(id);
                }
                if (hits.Count == 0 && (ids.Count == 0 || !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(path) || portNum > 0))
                {
                    string err;
                    var procs = SysProcs.Snapshot(out err);
                    HashSet<int> portPids = portNum > 0 ? SysPorts.PidsOnLocalPort(portNum) : null;
                    foreach (var p in procs)
                    {
                        if (!string.IsNullOrEmpty(name) && !SysProcs.MatchName(name, p.Name)) continue;
                        if (!string.IsNullOrEmpty(path) && !SysProcs.MatchText(path, p.Path, null)) continue;
                        if (portPids != null && !portPids.Contains(p.Pid)) continue;
                        hits.Add(p.Pid);
                    }
                }
                hits = hits.Distinct().ToList();
                lastCount = hits.Count;
                finalPids = hits;

                bool done = mode == "exit" ? hits.Count == 0 : hits.Count > 0;
                if (done) break;
                if (sw.Elapsed >= timeout) break;
                Thread.Sleep((int)Math.Min(interval.TotalMilliseconds, 2000));
            }

            bool satisfied = mode == "exit" ? lastCount == 0 : lastCount > 0;
            ctx.Out.Result("proc.wait", Json.Obj(
                "for", mode,
                "satisfied", satisfied,
                "waitedMs", sw.ElapsedMilliseconds,
                "timeout", timeout,
                "matched", lastCount,
                "pids", finalPids.Cast<object>().ToList(),
                "columns", new[] { "for", "satisfied", "waitedMs", "matched" }));
            return satisfied ? ExitCodes.Ok : ExitCodes.Timeout;
        }

        // ---------------------------------------------------------------- port
        static int RunPort(Ctx ctx)
        {
            string portArg = ctx.Get("port");
            if (string.IsNullOrEmpty(portArg) && ctx.Args.Positional.Count > 0) portArg = ctx.Args.Positional[0];
            if (string.IsNullOrEmpty(portArg)) throw ToolException.Usage("缺少 --port", "例：proc port --port 19387");
            int port;
            if (!int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 0 || port > 65535)
                throw ToolException.Usage("端口非法：" + portArg);

            var recs = SysPorts.OnLocalPort(port);
            var pids = recs.Select(r => r.Pid).Distinct().ToList();
            string wmiErr;
            var procs = SysProcs.Snapshot(out wmiErr);
            var byPid = SysProcs.ByPid(procs);

            var items = new List<object>();
            foreach (var r in recs.OrderBy(x => x.Proto).ThenBy(x => x.State))
            {
                SysProc p;
                byPid.TryGetValue(r.Pid, out p);
                items.Add(Json.Obj(
                    "proto", r.Proto,
                    "localAddr", r.LocalAddr,
                    "localPort", r.LocalPort,
                    "remoteAddr", r.RemoteAddr,
                    "remotePort", r.RemotePort,
                    "state", r.State,
                    "pid", r.Pid,
                    "name", p != null ? p.Name : null,
                    "nameUnavailable", p == null,
                    "nameUnavailableReason", p == null
                        ? "进程快照中没有该 PID（可能已退出，或受保护进程无法查询）——这是「读不到」，不代表端口没有归属"
                        : null,
                    "path", p != null ? p.Path : null,
                    "cmdline", p != null ? p.CommandLine : null,
                    "elevated", p != null && p.Elevated.HasValue ? (object)p.Elevated.Value : null));
            }

            var owners = new List<object>();
            foreach (var pid in pids)
            {
                SysProc p;
                byPid.TryGetValue(pid, out p);
                owners.Add(Json.Obj("pid", pid, "name", p != null ? p.Name : null, "path", p != null ? p.Path : null,
                                    "cmdline", p != null ? p.CommandLine : null,
                                    "endpoints", recs.Count(r => r.Pid == pid)));
            }

            ctx.Out.Result("proc.port", Json.Obj(
                "port", port,
                "items", items,
                "count", items.Count,
                "owners", owners,
                "ownerCount", owners.Count,
                "occupied", items.Count > 0,
                "wmiError", wmiErr,
                "columns", new[] { "proto", "localAddr", "localPort", "state", "pid", "name" }));
            return items.Count > 0 ? ExitCodes.Ok : ExitCodes.NotFound;
        }
    }
}

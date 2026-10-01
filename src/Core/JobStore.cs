using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DshToolbox.Core
{
    // ==================================================================== JSON 访问器
    /// <summary>把 Json.Parse 得到的松散对象安全取出来（JavaScriptSerializer 的数组是 ArrayList）。</summary>
    public static class Js
    {
        public static Dictionary<string, object> AsDict(object o) { return o as Dictionary<string, object>; }

        public static List<object> AsList(object o)
        {
            var l = o as List<object>;
            if (l != null) return l;
            var al = o as ArrayList;
            if (al != null) { var r = new List<object>(); foreach (var x in al) r.Add(x); return r; }
            var en = o as IEnumerable;
            if (en != null && !(en is string)) return Json.ToList(en);
            return new List<object>();
        }

        public static string Str(IDictionary<string, object> d, string key, string def = null)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            return Convert.ToString(d[key], CultureInfo.InvariantCulture);
        }

        public static long Long(IDictionary<string, object> d, string key, long def)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            object v = d[key];
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is decimal) return (long)(decimal)v;
            long r;
            if (long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        public static int Int(IDictionary<string, object> d, string key, int def) { return (int)Long(d, key, def); }

        public static bool Bool(IDictionary<string, object> d, string key, bool def)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            object v = d[key];
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s == null) return def;
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (s == "1") return true;
            if (s == "0") return false;
            return def;
        }
    }

    // ==================================================================== 目标命令
    /// <summary>要执行的目标命令（--cmd 原始行 或 -- 之后的 argv）。</summary>
    public sealed class TargetCommand
    {
        public string[] Argv = new string[0];
        public string ShellLine;
        public bool Shell;
        public string Display;

        public Dictionary<string, object> ToJson()
        {
            return Json.Obj(
                "argv", Argv,
                "commandLine", Display,
                "shell", Shell);
        }
    }

    // ==================================================================== 任务记录
    /// <summary>后台任务记录：cmd.json（配置，不可变）+ status.json（状态，可被 helper 回写）。</summary>
    public sealed class JobRecord
    {
        public string JobId = "";
        public string Name = "";
        public string[] Argv = new string[0];
        public string CommandLine = "";
        public string Cwd = "";
        public bool Shell;
        public int TimeoutSec;
        public DateTime? StartedAt;
        public DateTime? CreatedAt;

        public string State = "starting";
        public int HelperPid;
        public int TargetPid;
        public DateTime? HelperStart;
        public DateTime? TargetStart;
        public DateTime? EndedAt;
        public int? ExitCode;
        public long ElapsedMs;
        public long StdoutBytes;
        public long StderrBytes;
        public string Note;

        public string Dir = "";
        public bool Alive;
        public string ObservedState = "starting";

        public string StdoutPath { get { return Path.Combine(Dir, "stdout.log"); } }
        public string StderrPath { get { return Path.Combine(Dir, "stderr.log"); } }
        public string StatusPath { get { return Path.Combine(Dir, "status.json"); } }
        public string CmdPath { get { return Path.Combine(Dir, "cmd.json"); } }
        public string IndexPath { get { return Path.Combine(Dir, "index.jsonl"); } }
        public string KillPath { get { return Path.Combine(Dir, "kill.json"); } }
        public string HelperLogPath { get { return Path.Combine(Dir, "helper.log"); } }

        public bool IsFinal
        {
            get
            {
                return State == "completed" || State == "failed" || State == "killed"
                    || State == "timeout" || State == "error";
            }
        }

        public void Refresh()
        {
            Alive = JobStore.IsAlive(HelperPid) || JobStore.IsAlive(TargetPid);
            if (IsFinal) ObservedState = State;
            else ObservedState = Alive ? "running" : "lost";
        }

        public Dictionary<string, object> Files()
        {
            return Json.Obj(
                "cmd", CmdPath, "status", StatusPath,
                "stdout", StdoutPath, "stderr", StderrPath,
                "index", IndexPath, "helperLog", HelperLogPath);
        }

        public Dictionary<string, object> ToJson()
        {
            return Json.Obj(
                "jobId", JobId,
                "name", Name,
                "state", State,
                "observedState", ObservedState,
                "alive", Alive,
                "commandLine", CommandLine,
                "argv", Argv,
                "cwd", Cwd,
                "shell", Shell,
                "timeoutSec", TimeoutSec,
                "pid", HelperPid,
                "helperPid", HelperPid,
                "targetPid", TargetPid,
                "startedAt", StartedAt,
                "createdAt", CreatedAt,
                "endedAt", EndedAt,
                "elapsedMs", ElapsedMs,
                "exitCode", ExitCode,
                "stdoutBytes", StdoutBytes,
                "stderrBytes", StderrBytes,
                "note", Note,
                "dir", Dir,
                "files", Files());
        }
    }

    // ==================================================================== 落盘存储
    /// <summary>
    /// 后台任务 / 运行记录 / 日志的持久化。全部通过磁盘交互，CLI 每次调用都是新进程也能续上。
    /// </summary>
    public static class JobStore
    {
        static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        public const long DefaultMaxBytes = 4L * 1024 * 1024;

        // ---------------------------------------------------------- id
        public static string NewJobId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public static string NewRunId()
        {
            return "r" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>防止 id 里带路径分隔符（所有 id 都直接当目录名用）。</summary>
        public static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw ToolException.Usage("缺少任务 id", "先运行 job list 查看任务 id");
            if (id.Length > 120) throw ToolException.Usage("任务 id 过长");
            foreach (char c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') continue;
                throw ToolException.Usage("任务 id 含非法字符：" + id, "合法的 id 只包含字母数字和 - _ .");
            }
            if (id.Contains("..")) throw ToolException.Usage("任务 id 非法：" + id);
            return id;
        }

        public static string JobDir(string jobId) { return Path.Combine(Paths.Jobs, SafeId(jobId)); }

        // ---------------------------------------------------------- 文件工具
        public static void AtomicWrite(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Fs.EnsureDir(dir);
            string tmp = path + ".tmp-" + Environment.TickCount.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(tmp, text, Utf8NoBom);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { File.Copy(tmp, path, true); File.Delete(tmp); } catch { }
            }
        }

        public static void AppendJsonl(string file, object record)
        {
            if (string.IsNullOrEmpty(file)) return;
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Fs.EnsureDir(dir);
            byte[] bytes = Utf8NoBom.GetBytes(Json.Write(record) + "\n");
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                fs.Write(bytes, 0, bytes.Length);
        }

        public static string ReadTextShared(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return "";
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    return DecodeBytes(ms.ToArray());
                }
            }
            catch { return ""; }
        }

        public static IEnumerable<string> ReadLinesShared(string file)
        {
            string text = ReadTextShared(file);
            if (text.Length == 0) return new string[0];
            return text.Replace("\r\n", "\n").Split('\n').Where(x => x.Length > 0).ToArray();
        }

        /// <summary>优先严格 UTF-8 解码；失败回退系统 ANSI（中文 Windows = GBK）。</summary>
        public static string DecodeBytes(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            try { return Utf8Strict.GetString(b); }
            catch
            {
                try { return Encoding.Default.GetString(b); }
                catch { return ""; }
            }
        }

        public static Dictionary<string, object> ReadJsonObject(string path)
        {
            string text = ReadTextShared(path);
            if (text.Length == 0) return null;
            try { return Json.Parse(text) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) return d;
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        // ---------------------------------------------------------- 日志/运行记录路径
        public static string TodayLogFile()
        {
            return Path.Combine(Paths.Logs, "toolbox-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
        }

        public static string RunsFile() { return Path.Combine(Paths.Runs, "runs.jsonl"); }

        public static List<string> LogFiles()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(Paths.Logs)) return list;
                foreach (var f in Directory.GetFiles(Paths.Logs, "toolbox-*.jsonl")) list.Add(f);
                foreach (var f in Directory.GetFiles(Paths.Logs, "*.jsonl")) if (!list.Contains(f)) list.Add(f);
            }
            catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            list.Reverse();   // 新的在前
            return list;
        }

        // ---------------------------------------------------------- 单条日志
        public static Dictionary<string, object> AppendLog(string level, string msg, IDictionary<string, string> kv, string file)
        {
            if (string.IsNullOrEmpty(file)) file = TodayLogFile();
            var o = new JsonObject().Add("ts", DateTime.Now).Add("lvl", string.IsNullOrEmpty(level) ? "info" : level).Add("msg", msg);
            if (kv != null) foreach (var e in kv) o.Add(e.Key, e.Value);
            string line = Json.Write(o);
            byte[] bytes = Utf8NoBom.GetBytes(line + "\n");
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Fs.EnsureDir(dir);
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                fs.Write(bytes, 0, bytes.Length);
            var parsed = Json.Parse(line) as Dictionary<string, object>;
            if (parsed == null) parsed = new Dictionary<string, object>(StringComparer.Ordinal);
            parsed["file"] = file;
            parsed["line"] = line;
            return parsed;
        }

        // ---------------------------------------------------------- 从尾部读 N 行
        /// <summary>只从文件尾部按块回读，避免把大文件整读进内存。</summary>
        public static List<string> ReadTailLines(string file, int maxLines, long maxBytes, out long fromOffset, out long totalBytes, out bool truncated)
        {
            var lines = new List<string>();
            fromOffset = 0;
            totalBytes = 0;
            truncated = false;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return lines;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    totalBytes = fs.Length;
                    long lower = Math.Max(0, totalBytes - Math.Max(maxBytes, 4096));
                    int chunk = 65536;
                    var buf = new byte[chunk];
                    long scanEnd = totalBytes;
                    long found = -1;
                    int newlines = 0;
                    while (scanEnd > lower && maxLines >= 0)
                    {
                        int n = (int)Math.Min(chunk, scanEnd - lower);
                        long p = scanEnd - n;
                        fs.Position = p;
                        int got = ReadFull(fs, buf, 0, n);
                        for (int i = got - 1; i >= 0; i--)
                        {
                            if (buf[i] != (byte)'\n') continue;
                            newlines++;
                            if (newlines > maxLines) { found = p + i + 1; break; }
                        }
                        if (found >= 0) break;
                        scanEnd = p;
                    }
                    long from = found >= 0 ? found : lower;
                    truncated = from > 0;
                    fromOffset = from;
                    long len = totalBytes - from;
                    if (len > 0)
                    {
                        if (len > int.MaxValue) len = int.MaxValue;
                        var body = new byte[len];
                        fs.Position = from;
                        int got = ReadFull(fs, body, 0, (int)len);
                        string text = DecodeBytes(got == body.Length ? body : body.Take(got).ToArray());
                        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                        {
                            if (raw.Length == 0) continue;
                            lines.Add(raw.TrimEnd('\r'));
                        }
                        if (lines.Count > maxLines && maxLines >= 0)
                        {
                            lines = lines.Skip(lines.Count - maxLines).ToList();
                            truncated = true;
                        }
                    }
                }
            }
            catch { }
            return lines;
        }

        static int ReadFull(Stream s, byte[] buf, int off, int len)
        {
            int total = 0;
            while (total < len)
            {
                int n = s.Read(buf, off + total, len - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>读取文件末尾 maxBytes 之内的全部行（日志检索用：优先新数据）。</summary>
        public static List<string> ReadAllLines(string file, long maxBytes)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return list;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    long from = maxBytes > 0 && len > maxBytes ? len - maxBytes : 0;
                    long avail = len - from;
                    if (avail <= 0) return list;
                    var buf = new byte[avail];
                    fs.Position = from;
                    int got = ReadFull(fs, buf, 0, (int)avail);
                    if (got < buf.Length) buf = buf.Take(got).ToArray();
                    string text = DecodeBytes(buf);
                    if (from > 0)
                    {
                        int nl = text.IndexOf('\n');       // 丢掉被截断的首行
                        if (nl >= 0) text = text.Substring(nl + 1);
                    }
                    foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                        if (raw.Length > 0) list.Add(raw.TrimEnd('\r'));
                }
            }
            catch { }
            return list;
        }

        /// <summary>读取 offset 之后的完整行，返回新 offset（只推进到最后一个换行）。</summary>
        public static List<string> ReadLinesFrom(string file, long offset, long maxBytes, out long newOffset)
        {
            var lines = new List<string>();
            newOffset = offset;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return lines;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length <= offset) return lines;
                    long avail = Math.Min(fs.Length - offset, maxBytes);
                    var buf = new byte[avail];
                    fs.Position = offset;
                    int got = ReadFull(fs, buf, 0, (int)avail);
                    if (got <= 0) return lines;
                    if (got < buf.Length) buf = buf.Take(got).ToArray();
                    int last = Array.LastIndexOf(buf, (byte)'\n');
                    if (last < 0) { newOffset = offset; return lines; }
                    string text = DecodeBytes(buf.Take(last).ToArray());
                    foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                        if (raw.Length > 0) lines.Add(raw.TrimEnd('\r'));
                    newOffset = offset + last + 1;
                }
            }
            catch { }
            return lines;
        }

        // ---------------------------------------------------------- 任务状态读写
        public static void WriteCmd(JobRecord j, Dictionary<string, string> env, string token, string detachMode, string exePath)
        {
            var o = Json.Obj(
                "jobId", j.JobId,
                "name", j.Name,
                "argv", j.Argv,
                "commandLine", j.CommandLine,
                "cwd", j.Cwd,
                "shell", j.Shell,
                "timeoutSec", j.TimeoutSec,
                "createdAt", j.CreatedAt,
                "env", env,
                "token", token,
                "detach", detachMode,
                "exe", exePath);
            AtomicWrite(j.CmdPath, Json.Write(o));
        }

        public static void WriteStatus(JobRecord j)
        {
            var o = Json.Obj(
                "jobId", j.JobId,
                "name", j.Name,
                "state", j.State,
                "helperPid", j.HelperPid,
                "targetPid", j.TargetPid,
                "helperStart", j.HelperStart,
                "targetStart", j.TargetStart,
                "startedAt", j.StartedAt,
                "endedAt", j.EndedAt,
                "exitCode", j.ExitCode,
                "elapsedMs", j.ElapsedMs,
                "stdoutBytes", j.StdoutBytes,
                "stderrBytes", j.StderrBytes,
                "note", j.Note,
                "updatedAt", DateTime.Now);
            AtomicWrite(j.StatusPath, Json.Write(o));
        }

        /// <summary>只读加载：目录缺失抛 E_NOT_FOUND。</summary>
        public static JobRecord Load(string jobId)
        {
            string dir = JobDir(jobId);
            if (!Directory.Exists(dir))
                throw ToolException.NotFound("任务不存在：" + jobId, "运行 job list 查看全部任务");
            return LoadFromDir(jobId, dir);
        }

        public static JobRecord LoadFromDir(string jobId, string dir)
        {
            var rec = new JobRecord();
            rec.JobId = jobId;
            rec.Dir = dir;

            var cmd = ReadJsonObject(Path.Combine(dir, "cmd.json"));
            if (cmd != null)
            {
                rec.Name = Js.Str(cmd, "name", "");
                rec.CommandLine = Js.Str(cmd, "commandLine", "");
                rec.Cwd = Js.Str(cmd, "cwd", "");
                rec.Shell = Js.Bool(cmd, "shell", false);
                rec.TimeoutSec = Js.Int(cmd, "timeoutSec", 0);
                rec.CreatedAt = ParseDate(Js.Str(cmd, "createdAt"));
                rec.StartedAt = ParseDate(Js.Str(cmd, "createdAt"));
                var argv = new List<string>();
                foreach (var x in Js.AsList(cmd.ContainsKey("argv") ? cmd["argv"] : null))
                    argv.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                rec.Argv = argv.ToArray();
            }

            var st = ReadJsonObject(Path.Combine(dir, "status.json"));
            if (st != null)
            {
                rec.State = Js.Str(st, "state", "unknown");
                rec.HelperPid = Js.Int(st, "helperPid", 0);
                rec.TargetPid = Js.Int(st, "targetPid", 0);
                rec.HelperStart = ParseDate(Js.Str(st, "helperStart"));
                rec.TargetStart = ParseDate(Js.Str(st, "targetStart"));
                rec.ExitCode = st.ContainsKey("exitCode") && st["exitCode"] != null ? (int?)Js.Int(st, "exitCode", 0) : null;
                rec.ElapsedMs = Js.Long(st, "elapsedMs", 0);
                rec.StdoutBytes = Js.Long(st, "stdoutBytes", 0);
                rec.StderrBytes = Js.Long(st, "stderrBytes", 0);
                rec.Note = Js.Str(st, "note", null);
                rec.EndedAt = ParseDate(Js.Str(st, "endedAt"));
                DateTime? started = ParseDate(Js.Str(st, "startedAt"));
                if (started.HasValue) rec.StartedAt = started;
            }

            // 文件大小兜底（helper 崩溃时也能报出真实长度）
            try { if (File.Exists(rec.StdoutPath)) rec.StdoutBytes = Math.Max(rec.StdoutBytes, new FileInfo(rec.StdoutPath).Length); } catch { }
            try { if (File.Exists(rec.StderrPath)) rec.StderrBytes = Math.Max(rec.StderrBytes, new FileInfo(rec.StderrPath).Length); } catch { }
            rec.Refresh();
            return rec;
        }

        public static List<JobRecord> ListAll()
        {
            var list = new List<JobRecord>();
            try
            {
                if (!Directory.Exists(Paths.Jobs)) return list;
                foreach (var d in Directory.GetDirectories(Paths.Jobs))
                {
                    try { list.Add(LoadFromDir(Path.GetFileName(d), d)); }
                    catch { }
                }
            }
            catch { }
            list.Sort((a, b) => string.CompareOrdinal(b.JobId, a.JobId));   // id 含时间戳 → 新的在前
            return list;
        }

        // ---------------------------------------------------------- 进程工具
        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>从 Win32_Process 建父子表，返回 rootPid 的全部后代（不含 root）。</summary>
        static Dictionary<int, List<int>> ChildMap()
        {
            var map = new Dictionary<int, List<int>>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in results)
                    {
                        int pid = 0, ppid = 0;
                        try { pid = Convert.ToInt32(mo["ProcessId"], CultureInfo.InvariantCulture); } catch { }
                        try { ppid = Convert.ToInt32(mo["ParentProcessId"], CultureInfo.InvariantCulture); } catch { }
                        if (pid <= 0) { mo.Dispose(); continue; }
                        List<int> kids;
                        if (!map.TryGetValue(ppid, out kids)) { kids = new List<int>(); map[ppid] = kids; }
                        kids.Add(pid);
                        mo.Dispose();
                    }
                }
            }
            catch { }
            return map;
        }

        public static List<int> ProcessTree(int rootPid)
        {
            var result = new List<int>();
            if (rootPid <= 0) return result;
            var map = ChildMap();
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                List<int> kids;
                if (!map.TryGetValue(cur, out kids)) continue;
                foreach (var k in kids)
                {
                    if (!seen.Add(k)) continue;
                    result.Add(k);
                    queue.Enqueue(k);
                }
            }
            return result;
        }

        /// <summary>杀整棵进程树（先后代后根），返回被杀掉的 pid 列表。</summary>
        public static List<int> KillTree(int rootPid)
        {
            return KillTree(rootPid, null);
        }

        /// <summary>
        /// pid 校验：pid 会被系统复用，所以杀之前必须核对进程创建时间。
        /// 🚨 安全红线：不匹配就当它不存在，绝不误杀无关进程（例如用户的 DeepSeek Harness）。
        /// </summary>
        public static bool VerifyPid(int pid, DateTime? expectedStart)
        {
            if (pid <= 0) return false;
            if (pid == 4 || pid == Process.GetCurrentProcess().Id) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return false;
                    if (!expectedStart.HasValue) return true;
                    DateTime actual = p.StartTime;
                    return Math.Abs((actual - expectedStart.Value).TotalSeconds) < 5;
                }
            }
            catch { return false; }
        }

        /// <summary>根进程先按创建时间校验，再杀掉它的全部后代与自身。</summary>
        public static List<int> KillTree(int rootPid, DateTime? expectedStart)
        {
            var killed = new List<int>();
            if (rootPid <= 0) return killed;
            if (!VerifyPid(rootPid, expectedStart)) return killed;   // pid 复用/已退出 → 不动手
            var descendants = ProcessTree(rootPid);
            descendants.Reverse();
            var order = new List<int>(descendants);
            order.Add(rootPid);
            foreach (var pid in order)
            {
                if (pid == Process.GetCurrentProcess().Id) continue;   // 安全红线：绝不杀自己
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        if (p.HasExited) continue;
                        p.Kill();
                        killed.Add(pid);
                    }
                }
                catch { }
            }
            // 给内核一点时间回收
            try { Thread.Sleep(150); } catch { }
            return killed;
        }

        public static void SkipToEnd(TailFollower f)
        {
            if (f != null) f.SeekToEnd();
        }

        /// <summary>
        /// 分离启动后台执行器。优先级：
        /// 1) CreateProcess(bInheritHandles=FALSE)：目标进程**完全不继承**本进程任何句柄，
        ///    调用方（DSH/PowerShell 的管道）不会被后台任务拖住，后台任务也不会抢 stdin；
        /// 2) WMI Win32_Process.Create：父进程是 WMI 服务，彻底脱离当前进程树/作业对象；
        /// 3) Process.Start：最后兜底（会继承句柄，仅在前两者失败时使用）。
        /// </summary>
        public static int LaunchDetached(string exe, string args, string workingDir, out string mode)
        {
            mode = "createprocess";
            int pid = CreateProcessDetached(exe, args, workingDir);
            if (pid > 0) return pid;
            mode = "createprocess-failed";

            pid = WmiCreate(exe, args, workingDir, ref mode);
            if (pid > 0) return pid;

            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                    psi.WorkingDirectory = workingDir;
                var p = Process.Start(psi);
                if (p != null)
                {
                    int p2 = p.Id;
                    try { p.StandardInput.Close(); } catch { }
                    p.Dispose();
                    mode += " / process";
                    return p2;
                }
                mode += " / process-null";
            }
            catch (Exception ex)
            {
                mode += " / process-failed: " + ex.Message;
            }
            return 0;
        }

        // ---------------------------------------------------------- P/Invoke
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        const uint CREATE_NO_WINDOW = 0x08000000;

        static int CreateProcessDetached(string exe, string args, string workingDir)
        {
            try
            {
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                string cmd = QuoteWinArg(exe) + (string.IsNullOrEmpty(args) ? "" : " " + args);
                string cwd = (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)) ? workingDir : null;
                bool ok = CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW,
                                        IntPtr.Zero, cwd, ref si, out pi);
                if (!ok) return 0;
                int pid = pi.dwProcessId;
                try { CloseHandle(pi.hThread); } catch { }
                try { CloseHandle(pi.hProcess); } catch { }
                return pid;
            }
            catch { return 0; }
        }

        static int WmiCreate(string exe, string args, string workingDir, ref string mode)
        {
            try
            {
                using (var cls = new ManagementClass("Win32_Process"))
                using (var inParams = cls.GetMethodParameters("Create"))
                {
                    inParams["CommandLine"] = QuoteWinArg(exe) + " " + args;
                    if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                        inParams["CurrentDirectory"] = workingDir;
                    using (var outParams = cls.InvokeMethod("Create", inParams, null))
                    {
                        if (outParams == null) { mode += " / wmi:null"; return 0; }
                        uint ret = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);
                        if (ret != 0) { mode += " / wmi-error:" + ret.ToString(CultureInfo.InvariantCulture); return 0; }
                        mode += " / wmi";
                        return Convert.ToInt32(outParams["ProcessId"], CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                mode += " / wmi-exception: " + ex.Message;
                return 0;
            }
        }

        // ---------------------------------------------------------- 目标命令构造
        public static TargetCommand ResolveTarget(Ctx ctx)
        {
            var t = new TargetCommand();
            bool shell = ctx.Flag("shell");
            string raw = ctx.Get("cmd");
            var pos = new List<string>(ctx.Args.Positional);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                t.ShellLine = raw;
                shell = true;
            }
            else if (pos.Count == 0)
            {
                throw ToolException.Usage("缺少要执行的命令",
                    "写法：run -- <命令> [参数...]；或 run --cmd \"echo hi & exit 3\"（原始命令行）");
            }
            else
            {
                t.Argv = pos.ToArray();
            }
            t.Shell = shell;
            t.Display = shell
                ? (t.ShellLine != null ? t.ShellLine : string.Join(" ", t.Argv))
                : string.Join(" ", t.Argv.Select(QuoteWinArg));
            return t;
        }

        public static ProcessStartInfo BuildPsi(TargetCommand t, string cwd, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo();
            if (t.Shell)
            {
                string comspec = Environment.GetEnvironmentVariable("ComSpec");
                psi.FileName = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec;
                psi.Arguments = "/d /s /c \"" + (t.ShellLine ?? string.Join(" ", t.Argv)) + "\"";
            }
            else
            {
                psi.FileName = t.Argv[0];
                var sb = new StringBuilder();
                for (int i = 1; i < t.Argv.Length; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(QuoteWinArg(t.Argv[i]));
                }
                psi.Arguments = sb.ToString();
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // 目标命令的 stdin 给一对已关闭的管道：既不继承调用方（DSH）的 stdin，读起来就是 EOF
            psi.RedirectStandardInput = true;
            psi.WorkingDirectory = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd;
            if (env != null)
                foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            return psi;
        }

        /// <summary>--env KEY=VALUE 解析（可重复）。</summary>
        public static Dictionary<string, string> EnvOverrides(Ctx ctx)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ctx.GetAll("--env"))
            {
                if (string.IsNullOrEmpty(item)) continue;
                int eq = item.IndexOf('=');
                if (eq <= 0)
                {
                    string existing = Environment.GetEnvironmentVariable(item);
                    if (existing == null)
                        throw ToolException.Usage("--env 格式非法：" + item, "写法：--env KEY=VALUE（可重复）");
                    d[item] = existing;
                    continue;
                }
                d[item.Substring(0, eq)] = item.Substring(eq + 1);
            }
            return d;
        }

        /// <summary>Windows 命令行参数引号规则（给 CreateProcess/Argument 用）。</summary>
        public static string QuoteWinArg(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) return a;
            var sb = new StringBuilder();
            sb.Append('"');
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                if (bs > 0) { sb.Append('\\', bs); bs = 0; }
                sb.Append(c);
            }
            if (bs > 0) sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // ---------------------------------------------------------- 索引（支持 --since）
        public static void AppendIndex(string dir, long outLen, long errLen)
        {
            try
            {
                AppendJsonl(Path.Combine(dir, "index.jsonl"), Json.Obj("ts", DateTime.Now, "o", outLen, "e", errLen));
            }
            catch { }
        }

        public static void OffsetsSince(string dir, DateTime since, out long stdoutOffset, out long stderrOffset)
        {
            stdoutOffset = 0;
            stderrOffset = 0;
            foreach (var line in ReadLinesShared(Path.Combine(dir, "index.jsonl")))
            {
                var o = Json.Parse(line) as Dictionary<string, object>;
                if (o == null) continue;
                DateTime? ts = ParseDate(Js.Str(o, "ts"));
                if (!ts.HasValue) continue;
                if (ts.Value <= since)
                {
                    stdoutOffset = Js.Long(o, "o", stdoutOffset);
                    stderrOffset = Js.Long(o, "e", stderrOffset);
                }
                else break;
            }
        }

        // ---------------------------------------------------------- 输出帧
        public static void EmitFrame(object frame)
        {
            Console.Out.WriteLine(Json.Write(frame));
            Console.Out.Flush();
        }

        public static void PumpStream(Stream src, string file, Action<long> onBytes, Func<bool> keepGoing)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 32768))
                {
                    var buf = new byte[32768];
                    while (true)
                    {
                        if (keepGoing != null && !keepGoing()) break;
                        int n = src.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        fs.Flush();
                        if (onBytes != null) onBytes(n);
                    }
                }
            }
            catch { }
        }
    }

    // ==================================================================== 增量跟随
    /// <summary>按行尾随一个不断增长的文件（跨进程写入安全）。</summary>
    public sealed class TailFollower : IDisposable
    {
        readonly string _path;
        FileStream _fs;
        long _pos;
        readonly List<byte> _pending = new List<byte>();

        public TailFollower(string path, long startOffset)
        {
            _path = path;
            _pos = startOffset < 0 ? 0 : startOffset;
        }

        public string Path_ { get { return _path; } }
        public long Offset { get { return _pos; } }
        public bool Exists { get { return File.Exists(_path); } }

        /// <summary>跳到当前文件末尾（--follow 时默认只看之后新产生的行）。</summary>
        public void SeekToEnd()
        {
            try
            {
                if (File.Exists(_path)) _pos = new FileInfo(_path).Length;
                _pending.Clear();
            }
            catch { }
        }

        public List<string> Poll(long maxBytes)
        {
            var lines = new List<string>();
            if (maxBytes <= 0) maxBytes = JobStore.DefaultMaxBytes;
            try
            {
                if (_fs == null)
                {
                    if (!File.Exists(_path)) return lines;
                    _fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384);
                }
                if (_fs.Length < _pos) { _pos = 0; _pending.Clear(); }   // 文件被截断/轮转
                if (_fs.Length <= _pos) return lines;
                _fs.Position = _pos;
                int n = (int)Math.Min(_fs.Length - _pos, Math.Min(maxBytes, 1024 * 1024));
                var buf = new byte[n];
                int got = _fs.Read(buf, 0, n);
                if (got <= 0) return lines;
                _pos += got;
                for (int i = 0; i < got; i++) _pending.Add(buf[i]);

                int start = 0;
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i] != (byte)'\n') continue;
                    var arr = new byte[i - start];
                    _pending.CopyTo(start, arr, 0, arr.Length);
                    lines.Add(JobStore.DecodeBytes(arr).TrimEnd('\r'));
                    start = i + 1;
                }
                if (start > 0) _pending.RemoveRange(0, start);
                if (_pending.Count > 1024 * 1024)   // 超长行保护
                {
                    lines.Add(JobStore.DecodeBytes(_pending.ToArray()));
                    _pending.Clear();
                }
            }
            catch { }
            return lines;
        }

        public void Dispose()
        {
            try { if (_fs != null) _fs.Dispose(); } catch { }
            _fs = null;
        }
    }
}

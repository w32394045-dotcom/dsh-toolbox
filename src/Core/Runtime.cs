using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DshToolbox.Core
{
    public static class ToolInfo
    {
        public const string Name = "dsh-toolbox";
        public const string Version = "0.2.0";
        public const string Protocol = "1";
    }

    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Error = 1;
        public const int Usage = 2;
        public const int NotFound = 3;
        public const int Denied = 4;
        public const int Timeout = 5;
        public const int Partial = 6;
        public const int Cancelled = 130;
    }

    public sealed class ToolException : Exception
    {
        public readonly string Code;
        public readonly string Hint;
        public readonly int Exit;
        public ToolException(string code, string message, string hint = null, int exit = ExitCodes.Error)
            : base(message)
        {
            Code = code; Hint = hint; Exit = exit;
        }
        public static ToolException Usage(string message, string hint = null) { return new ToolException("E_USAGE", message, hint, ExitCodes.Usage); }
        public static ToolException NotFound(string message, string hint = null) { return new ToolException("E_NOT_FOUND", message, hint, ExitCodes.NotFound); }
        public static ToolException Denied(string message, string hint = null) { return new ToolException("E_DENIED", message, hint, ExitCodes.Denied); }
        public static ToolException Timeout(string message, string hint = null) { return new ToolException("E_TIMEOUT", message, hint, ExitCodes.Timeout); }
        public static ToolException NeedsYes(string action)
        {
            return new ToolException("E_NEEDS_CONFIRM",
                string.Format("破坏性操作未确认：{0}", action),
                "加 --dry-run 预览计划，或加 --yes 真正执行", ExitCodes.Usage);
        }
    }

    // ================================================================== 路径
    public static class Paths
    {
        public static string Home { get; private set; }
        public static string Logs { get; private set; }
        public static string Runs { get; private set; }
        public static string Jobs { get; private set; }
        public static string Cache { get; private set; }

        public static void Init(string overrideHome)
        {
            string home = overrideHome;
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("DSH_TOOLBOX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-toolbox");
            Home = Path.GetFullPath(home);
            Logs = Path.Combine(Home, "logs");
            Runs = Path.Combine(Home, "runs");
            Jobs = Path.Combine(Home, "jobs");
            Cache = Path.Combine(Home, "cache");
        }

        public static void Ensure()
        {
            foreach (var d in new[] { Home, Logs, Runs, Jobs, Cache })
            {
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            }
        }
    }

    // ================================================================== 日志
    public sealed class Log : IDisposable
    {
        readonly StreamWriter _writer;
        readonly bool _verbose;
        readonly object _gate = new object();

        public static readonly Log Null = new Log(null, false);

        public Log(string file, bool verbose)
        {
            _verbose = verbose;
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { _writer = null; }
        }

        public void Info(string msg, params object[] kv) { Write("info", msg, kv); }
        public void Warn(string msg, params object[] kv) { Write("warn", msg, kv); }
        public void Error(string msg, params object[] kv) { Write("error", msg, kv); }
        public void Debug(string msg, params object[] kv) { if (_verbose) Write("debug", msg, kv); }

        public void Write(string level, string msg, params object[] kv)
        {
            var o = new JsonObject().Add("ts", DateTime.Now).Add("lvl", level).Add("msg", msg);
            for (int i = 0; i + 1 < (kv == null ? 0 : kv.Length); i += 2)
                o.Add(Convert.ToString(kv[i], CultureInfo.InvariantCulture), kv[i + 1]);
            string line = Json.Write(o);
            lock (_gate)
            {
                if (_writer != null) { try { _writer.WriteLine(line); } catch { } }
                if (_verbose || level == "warn" || level == "error")
                {
                    try { Console.Error.WriteLine("[" + level + "] " + msg); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_writer != null) { try { _writer.Flush(); _writer.Dispose(); } catch { } }
        }
    }

    // ================================================================== 输出
    public sealed class Output
    {
        public bool JsonMode;
        public bool JsonLines;
        public bool Quiet;
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public string Cmd = "";
        public long ElapsedMs;
        public bool Truncated;
        public readonly List<string> Warnings = new List<string>();
        readonly List<object> _items = new List<object>();
        int _emitted;
        bool _streamStarted;
        readonly List<string[]> _rows = new List<string[]>();
        List<string> _humanColumns;

        public void Warn(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Warnings.Add(message);
            if (!Quiet) { try { Console.Error.WriteLine("warn: " + message); } catch { } }
        }

        /// <summary>人类可读文本：JSON/JSONL 模式下写 stderr，保证 stdout 是纯协议。</summary>
        public void Line(string text)
        {
            if (text == null) return;
            if (JsonMode || JsonLines)
            {
                if (!Quiet) { try { Console.Error.WriteLine(text); } catch { } }
                return;
            }
            Console.Out.WriteLine(text);
        }

        /// <summary>追加一行人类表格（JSON 模式下由 Result 统一走 data.items）。</summary>
        public void Row(params object[] cols)
        {
            var arr = new string[cols.Length];
            for (int i = 0; i < cols.Length; i++) arr[i] = Human(cols[i]);
            _rows.Add(arr);
        }

        public void Columns(params string[] names) { _humanColumns = new List<string>(names); }

        /// <summary>流式模式预写 meta 帧，保证 meta 是第一行；非流式为空操作，可重复调用。</summary>
        public void BeginStream(string cmd)
        {
            if (!JsonLines || _streamStarted) return;
            if (!string.IsNullOrEmpty(cmd)) Cmd = cmd;
            _streamStarted = true;
            Console.Out.WriteLine(Json.Write(new JsonObject()
                .Add("type", "meta").Add("cmd", Cmd).Add("version", ToolInfo.Version).Add("protocol", ToolInfo.Protocol)));
        }

        public void EmitItem(object item)
        {
            _emitted++;
            if (JsonLines)
            {
                // 流式：直接输出、不驻留内存（否则百万级结果会把内存打爆）
                Console.Out.WriteLine(Json.Write(new JsonObject().Add("type", "item").Add("item", item)));
                return;
            }
            _items.Add(item);
        }

        /// <summary>统一结果出口。data 建议是 IDictionary，含 items / count / columns。</summary>
        public void Result(string cmd, object data, IEnumerable<string> warnings = null)
        {
            ElapsedMs = Watch.ElapsedMilliseconds;
            if (warnings != null) foreach (var w in warnings) Warn(w);
            Cmd = cmd;

            if (JsonLines)
            {
                BeginStream(cmd);
                if (_emitted == 0)   // 命令未自己流式发过 item，则用 data.items 补发
                    foreach (var it in Json.ToList(ItemsOf(data))) EmitItem(it);   // 先快照，避免边发边改集合
                Console.Out.WriteLine(Json.Write(new JsonObject()
                    .Add("type", "summary").Add("ok", true)
                    .Add("count", _emitted > 0 ? _emitted : _items.Count)
                    .Add("elapsedMs", ElapsedMs)
                    .Add("truncated", Truncated)
                    .Add("warnings", new List<object>(Warnings))));
                return;
            }

            if (JsonMode)
            {
                var env = new JsonObject()
                    .Add("ok", true)
                    .Add("cmd", cmd)
                    .Add("version", ToolInfo.Version)
                    .Add("elapsedMs", ElapsedMs)
                    .Add("data", data)
                    .Add("warnings", new List<object>(Warnings))
                    .Add("truncated", Truncated);
                Console.Out.WriteLine(Json.Write(env));
                return;
            }

            WriteHuman(data);
        }

        public void Fail(string cmd, string code, string message, string hint, int exit)
        {
            ElapsedMs = Watch.ElapsedMilliseconds;
            Cmd = cmd;
            if (JsonMode || JsonLines)
            {
                var err = new JsonObject().Add("code", code).Add("message", message);
                if (!string.IsNullOrEmpty(hint)) err.Add("hint", hint);
                Console.Out.WriteLine(Json.Write(new JsonObject()
                    .Add("ok", false).Add("cmd", cmd).Add("version", ToolInfo.Version)
                    .Add("elapsedMs", ElapsedMs).Add("error", err)));
                return;
            }
            Console.Error.WriteLine("错误: " + message + "  (" + code + ")");
            if (!string.IsNullOrEmpty(hint)) Console.Error.WriteLine("提示: " + hint);
        }

        IEnumerable<object> ItemsOf(object data)
        {
            if (_items.Count > 0) return _items;
            var d = data as IDictionary;
            if (d != null && d.Contains("items"))
            {
                var en = d["items"] as IEnumerable;
                if (en != null && !(en is string)) return Json.ToList(en);
            }
            return new List<object>();
        }

        void WriteHuman(object data)
        {
            var d = data as IDictionary;
            if (d == null)
            {
                if (_rows.Count > 0) { PrintTable(_rows, _humanColumns); return; }
                Console.Out.WriteLine(Human(data));
                return;
            }

            var items = ItemsOf(data);
            var list = items as IList<object> ?? new List<object>(items);
            if (list.Count > 0)
            {
                var cols = _humanColumns;
                if (cols == null && d.Contains("columns"))
                {
                    var ce = d["columns"] as IEnumerable;
                    if (ce != null) cols = ce.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
                }
                var rows = new List<string[]>();
                if (cols == null)
                {
                    var first = list[0] as IDictionary;
                    if (first != null) cols = first.Keys.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
                }
                foreach (var it in list)
                {
                    var id = it as IDictionary;
                    if (cols == null || id == null) { rows.Add(new[] { Human(it) }); continue; }
                    var row = new string[cols.Count];
                    for (int i = 0; i < cols.Count; i++)
                        row[i] = id.Contains(cols[i]) ? Human(id[cols[i]]) : "";
                    rows.Add(row);
                }
                PrintTable(rows, cols);
                Console.Out.WriteLine();
            }

            foreach (DictionaryEntry e in d)
            {
                string k = Convert.ToString(e.Key, CultureInfo.InvariantCulture);
                if (k == "items" || k == "columns") continue;
                Console.Out.WriteLine("  " + k + ": " + Human(e.Value));
            }
            if (Truncated) Console.Out.WriteLine("  truncated: true（结果被截断，用 --jsonl 或提高 --max-results 获取全部）");
            foreach (var w in Warnings) Console.Out.WriteLine("  warn: " + w);
        }

        void PrintTable(List<string[]> rows, List<string> cols)
        {
            if (rows.Count == 0) return;
            int n = cols != null ? cols.Count : rows[0].Length;
            var widths = new int[n];
            Func<string, string> clip = s => s.Length > 60 ? s.Substring(0, 57) + "..." : s;
            for (int i = 0; i < n; i++)
            {
                widths[i] = cols != null && i < cols.Count ? cols[i].Length : 0;
                foreach (var r in rows)
                    if (i < r.Length) widths[i] = Math.Max(widths[i], clip(r[i]).Length);
            }
            if (cols != null) Console.Out.WriteLine(FormatRow(cols.ToArray(), widths, clip));
            if (cols != null) Console.Out.WriteLine(string.Join("  ", widths.Select(w => new string('-', w)).ToArray()));
            foreach (var r in rows) Console.Out.WriteLine(FormatRow(r, widths, clip));
            Console.Out.WriteLine("(" + rows.Count + " 行)");
        }

        static string FormatRow(string[] cells, int[] widths, Func<string, string> clip)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < widths.Length; i++)
            {
                if (i > 0) sb.Append("  ");
                string c = i < cells.Length ? clip(cells[i]) : "";
                sb.Append(c.PadRight(widths[i]));
            }
            return sb.ToString().TrimEnd();
        }

        public static string Human(object v)
        {
            if (v == null) return "";
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is DateTime) return ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss");
            if (v is IEnumerable && !(v is IDictionary) && !(v is byte[]))
            {
                var parts = new List<string>();
                foreach (var x in (IEnumerable)v) { parts.Add(Human(x)); if (parts.Count > 12) { parts.Add("..."); break; } }
                string s = string.Join(", ", parts.ToArray());
                return s.Length > 200 ? s.Substring(0, 197) + "..." : s;
            }
            string js = Json.Write(v);
            return js.Length > 200 ? js.Substring(0, 197) + "..." : js;
        }
    }

    // ================================================================== 命令注册表
    public sealed class CommandInfo
    {
        public string Name;
        public string Group;
        public string Action;
        public string Summary;
        public string Usage;
        public string[] Aliases = new string[0];
        public string[] Examples = new string[0];
        public Func<Ctx, int> Run;
    }

    public static class Registry
    {
        static readonly SortedDictionary<string, CommandInfo> Map = new SortedDictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);

        public static void Add(string name, string summary, string usage, Func<Ctx, int> run,
                               string[] aliases = null, string[] examples = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("命令名不能为空");
            if (run == null) throw new ArgumentException("命令 " + name + " 缺少处理函数");
            var ci = new CommandInfo();
            ci.Name = name.Trim();
            int dot = ci.Name.IndexOf('.');
            ci.Group = dot > 0 ? ci.Name.Substring(0, dot) : "";
            ci.Action = dot > 0 ? ci.Name.Substring(dot + 1) : ci.Name;
            ci.Summary = summary;
            ci.Usage = usage;
            ci.Aliases = aliases ?? new string[0];
            ci.Examples = examples ?? new string[0];
            ci.Run = run;
            Map[ci.Name] = ci;
        }

        public static CommandInfo Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            CommandInfo ci;
            if (Map.TryGetValue(name, out ci)) return ci;
            foreach (var kv in Map)
                foreach (var a in kv.Value.Aliases)
                    if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        public static IList<CommandInfo> All() { return Map.Values.ToList(); }

        public static IEnumerable<IGrouping<string, CommandInfo>> Grouped()
        {
            return Map.Values.GroupBy(c => string.IsNullOrEmpty(c.Group) ? "misc" : c.Group);
        }

        public static string[] Names()
        {
            var l = new List<string>();
            foreach (var kv in Map) l.Add(kv.Key);
            return l.ToArray();
        }
    }

    // ================================================================== 上下文
    public sealed class Ctx
    {
        public ArgMap Args;
        public Output Out;
        public Log Log;
        public string Home;
        public string Cwd;
        public string ExePath;
        public string CmdName;
        public bool DryRun;
        public bool Yes;
        public bool Verbose;
        public bool NoLog;
        public int TimeoutSec;
        public CancellationToken Cancel;

        public Ctx()
        {
            Args = new ArgMap();
            Out = new Output();
            Log = Log.Null;
        }

        public string Require(string opt) { return Args.Require(opt); }
        public string Get(string opt, string def = null) { return Args.Get(opt, def); }
        public bool Flag(string opt, bool def = false) { return Args.Flag(opt, def); }
        public string[] GetAll(string opt) { return Args.GetAll(opt); }
        public bool Has(string opt) { return Args.Has(opt); }
        public int GetInt(string opt, int def = 0) { return Args.GetInt(opt, def); }
        public long GetLong(string opt, long def = 0) { return Args.GetLong(opt, def); }
        public double GetDouble(string opt, double def = 0) { return Args.GetDouble(opt, def); }
        public TimeSpan GetSpan(string opt, TimeSpan def = default(TimeSpan)) { return Args.GetSpan(opt, def); }
        public DateTime? GetDate(string opt) { return Args.GetDate(opt); }
        /// <summary>去掉命令词之后的位置参数。</summary>
        public string[] Positional { get { return Args.Positional.ToArray(); } }

        /// <summary>破坏性操作闸门：--dry-run 返回 true（只预览）；否则必须有 --yes。</summary>
        public bool ConfirmDestructive(string action)
        {
            if (DryRun) return false;
            if (Yes) return true;
            throw ToolException.NeedsYes(action);
        }

        public void ThrowIfCancelled()
        {
            if (Cancel.IsCancellationRequested) throw ToolException.Timeout("操作被取消或超时");
        }
    }
}

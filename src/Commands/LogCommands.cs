using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// run（前台执行并完整记录）+ log append/tail/search/runs。
    /// 运行记录：Paths.Runs\runs.jsonl（摘要，追加）+ Paths.Runs\&lt;runid&gt;.out.json（完整结果，供 artifact.read）。
    /// </summary>
    public static class LogCommands
    {
        public static void Register()
        {
            Registry.Add("run", "执行一条外部命令并完整记录（stdout/stderr/退出码/耗时/工作目录）",
                "run [--cwd <dir>] [--env K=V]... [--shell] [--timeout <dur>] [--capture[=false]] [--max-bytes <n>] [--non-interactive] [--tool <npm|git|msi|nsis|winget|powershell>] [--retry-args <args>] [--cmd <原始命令行>] -- <命令> [参数...]",
                RunRun,
                examples: new[]
                {
                    "dsh-toolbox run --json -- cmd /c \"echo hi & exit 3\"",
                    "dsh-toolbox run --timeout 30s -- git status",
                    "dsh-toolbox run --shell --cmd \"dir /b\" --json",
                    "dsh-toolbox run --non-interactive --json -- cmd /c \"set /p x=Proceed? [y/N]\""
                });

            Registry.Add("log.append", "往结构化日志追加一条（toolbox-YYYYMMDD.jsonl）",
                "log append --msg <文本> [--level <级别>] [--key K=V]... [--file <文件>]",
                RunAppend,
                examples: new[] { "dsh-toolbox log append --msg \"构建完成\" --level info --key step=build" });

            Registry.Add("log.tail", "读取当天/指定日志文件最近 N 行（--follow 持续输出）",
                "log tail [--file <文件>] [--lines <n>] [--since <dur|date>] [--level <级别>] [--follow]",
                RunTail,
                examples: new[] { "dsh-toolbox log tail --lines 20 --json", "dsh-toolbox log tail --follow --jsonl" });

            Registry.Add("log.search", "在日志目录内按正则检索（跨天文件，新的优先）",
                "log search --pattern <正则> [--since <date|dur>] [--limit <n>] [--ignore-case] [--file <文件>] [--level <级别>]",
                RunSearch,
                examples: new[] { "dsh-toolbox log search --pattern \"E_DENIED\" --since 3d --json" });

            Registry.Add("log.runs", "查看运行记录 runs.jsonl（命令/参数/退出码/耗时）",
                "log runs [--limit <n>] [--cmd <子串>] [--exit <码>] [--since <date|dur>] [--type run|call]",
                RunRuns,
                examples: new[] { "dsh-toolbox log runs --limit 10 --json", "dsh-toolbox log runs --exit 6" });
        }

        // ================================================================ run
        static int RunRun(Ctx ctx)
        {
            var target = JobStore.ResolveTarget(ctx);
            var env = JobStore.EnvOverrides(ctx);
            string cwd = ResolveCwd(ctx);
            int timeoutMs = (int)ctx.GetSpan("--timeout", TimeSpan.Zero).TotalMilliseconds;
            bool capture = !ctx.Has("capture") || ctx.Flag("capture", true);
            long maxBytes = ctx.GetLong("--max-bytes", 8L * 1024 * 1024);

            // ---- 非交互模式：给"会弹提示的安装器/工具"用。
            // 关掉 stdin（提示立刻 EOF 而不是挂住）+ 按工具注入免交互环境变量 + 识别疑似提示，
            // 命中后用 --retry-args 追加参数自动重试（两次尝试都记进 interactiveTrace）。
            if (ctx.Flag("non-interactive"))
            {
                var psiH = JobStore.BuildPsi(target, cwd, env);
                if (timeoutMs <= 0) timeoutMs = 10 * 60 * 1000;
                string tool = ctx.Get("tool", "");
                string retryArgs = ctx.Get("retry-args", "");
                Dictionary<string, object> trace;
                var hr = Proc.RunHardened(psiH.FileName, psiH.Arguments, timeoutMs,
                                         string.IsNullOrEmpty(tool) ? null : tool, retryArgs, cwd, out trace);
                bool okRun = hr.Exit == 0 && !hr.TimedOut && !hr.PromptSuspected;
                var hitems = new List<object>
                {
                    Json.Obj("name", L.T("退出码", "exit code"), "value", hr.Exit),
                    Json.Obj("name", L.T("耗时", "elapsed"), "value", hr.ElapsedMs + " ms"),
                    Json.Obj("name", L.T("超时", "timed out"), "value", hr.TimedOut),
                    Json.Obj("name", L.T("疑似在等输入", "waiting for input?"), "value", hr.PromptSuspected,
                             "detail", hr.PromptHint),
                    Json.Obj("name", L.T("stdout", "stdout"), "value", hr.Tail(2000))
                };
                ctx.Out.Result("run", Json.Obj(
                    "hardened", true, "tool", tool, "cmd", target.Display, "cwd", cwd,
                    "exitCode", hr.Exit, "elapsedMs", hr.ElapsedMs,
                    "timedOut", hr.TimedOut, "promptSuspected", hr.PromptSuspected, "promptHint", hr.PromptHint,
                    "interactiveTrace", trace,
                    "stdout", hr.Tail(8000), "stderr", (hr.Stderr ?? "").Trim().Length > 0 ? hr.Stderr.Substring(0, Math.Min(4000, hr.Stderr.Length)) : "",
                    "items", hitems, "count", hitems.Count, "columns", new[] { "name", "value" },
                    "reason", okRun ? null : (hr.PromptSuspected
                        ? L.T("疑似停在交互提示上（已尝试非交互重试）；用 --retry-args 指定该工具的静默参数",
                              "appears stuck at an interactive prompt (non-interactive retry attempted); pass --retry-args with the tool\\u0027s silent flags")
                        : (hr.TimedOut ? L.T("超时被杀", "timed out and was killed") : L.T("子进程非零退出", "child exited non-zero")))));
                // 契约 §4.2：包装类命令失败 → 工具 exit 6，子命令退出码在 data.exitCode
                return okRun ? ExitCodes.Ok : ExitCodes.Partial;
            }

            string runId = JobStore.NewRunId();
            string outFile = Path.Combine(Paths.Runs, runId + ".out.json");

            var psi = JobStore.BuildPsi(target, cwd, env);
            var proc = new Process { StartInfo = psi };
            var so = new BoundedSink(maxBytes);
            var se = new BoundedSink(maxBytes);
            var sw = Stopwatch.StartNew();
            int pid = 0;
            try
            {
                if (!proc.Start()) throw new ToolException("E_SPAWN", "无法启动命令：" + psi.FileName);
                pid = proc.Id;
                try { proc.StandardInput.Close(); } catch { }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw ToolException.NotFound("无法启动命令：" + psi.FileName + "（" + ex.Message + "）",
                    "确认命令在 PATH 中，或用 --shell 走 cmd.exe");
            }
            catch (ToolException) { throw; }
            catch (Exception ex)
            {
                throw new ToolException("E_SPAWN", "无法启动命令：" + ex.Message, "加 --verbose 查看细节");
            }

            var t1 = Task.Factory.StartNew(() => PumpTo(proc.StandardOutput.BaseStream, so));
            var t2 = Task.Factory.StartNew(() => PumpTo(proc.StandardError.BaseStream, se));

            bool timedOut = false, cancelled = false;
            while (true)
            {
                bool exited = false;
                try { exited = proc.WaitForExit(200); } catch { exited = true; }
                if (exited) break;
                if (ctx.Cancel.IsCancellationRequested) { cancelled = true; break; }
                if (timeoutMs > 0 && sw.ElapsedMilliseconds >= timeoutMs) { timedOut = true; break; }
            }
            if (timedOut || cancelled)
            {
                JobStore.KillTree(pid, null);
                try { proc.WaitForExit(3000); } catch { }
            }
            try { Task.WaitAll(new[] { t1, t2 }, 8000); } catch { }
            int exitCode = -1;
            try { if (proc.HasExited) exitCode = proc.ExitCode; } catch { }
            sw.Stop();
            long ms = sw.ElapsedMilliseconds;
            string outText = so.Text();
            string errText = se.Text();
            bool truncated = so.Truncated || se.Truncated;

            var summary = Json.Obj(
                "runId", runId, "cmd", target.Display, "cwd", cwd, "shell", target.Shell,
                "exitCode", exitCode, "elapsedMs", ms, "timedOut", timedOut,
                "stdoutBytes", so.Total, "stderrBytes", se.Total, "outFile", outFile);
            var failures = new List<object>();
            if (exitCode != 0 && !timedOut)
                failures.Add(Json.Obj("cmd", target.Display, "exitCode", exitCode, "error", "子进程退出码 " + exitCode));

            if (!ctx.NoLog)
            {
                try
                {
                    var rec = Json.Obj(
                        "runId", runId, "ts", DateTime.Now, "cmd", target.Display, "argv", target.Argv,
                        "cwd", cwd, "shell", target.Shell, "exitCode", exitCode, "elapsedMs", ms,
                        "timedOut", timedOut, "stdoutBytes", so.Total, "stderrBytes", se.Total,
                        "captured", capture, "truncated", truncated,
                        "stdout", capture ? outText : null, "stderr", capture ? errText : null,
                        "pid", pid);
                    JobStore.AtomicWrite(outFile, Json.Write(rec));
                    JobStore.AppendJsonl(JobStore.RunsFile(), Json.Obj(
                        "type", "run", "runId", runId, "ts", DateTime.Now, "cmd", target.Display, "argv", target.Argv,
                        "cwd", cwd, "exit", exitCode, "ms", ms, "timedOut", timedOut,
                        "outFile", outFile, "pid", pid, "stdoutBytes", so.Total, "stderrBytes", se.Total));
                }
                catch (Exception ex)
                {
                    ctx.Out.Warn("运行记录写入失败：" + ex.Message);
                }
            }

            // 人类模式：像普通包装器一样把子进程输出透传
            if (!ctx.Out.JsonMode && !ctx.Out.JsonLines)
            {
                if (outText.Length > 0) Console.Out.Write(outText);
                if (errText.Length > 0) Console.Error.Write(errText);
            }

            if (cancelled)
                throw new ToolException("E_CANCELLED", "命令被取消（超时或 Ctrl+C）",
                    "完整记录：" + outFile, ExitCodes.Cancelled);

            if (timedOut)
                throw ToolException.Timeout(
                    "命令超过 --timeout " + timeoutMs + "ms 被终止：" + target.Display,
                    "完整记录（含已捕获输出）：" + outFile);

            if (truncated) ctx.Out.Truncated = true;
            var data = Json.Obj(
                "runId", runId, "cmd", target.Display, "argv", target.Argv, "cwd", cwd, "shell", target.Shell,
                "exitCode", exitCode, "elapsedMs", ms, "pid", pid, "timedOut", timedOut,
                "stdout", outText, "stderr", errText,
                "stdoutBytes", so.Total, "stderrBytes", se.Total,
                "truncated", truncated, "captured", capture,
                "outFile", outFile, "runsFile", JobStore.RunsFile(),
                "failures", failures,
                "items", new List<object> { summary },
                "count", 1,
                "columns", new[] { "runId", "exitCode", "elapsedMs", "cmd" });
            ctx.Out.Result("run", data);
            return exitCode == 0 ? ExitCodes.Ok : ExitCodes.Partial;
        }

        static string ResolveCwd(Ctx ctx)
        {
            string cwdArg = ctx.Get("cwd");
            string cwd = string.IsNullOrEmpty(cwdArg) ? ctx.Cwd : Fs.Expand(cwdArg, ctx.Cwd);
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
                throw ToolException.NotFound("工作目录不存在：" + (cwdArg ?? ctx.Cwd), "用 --cwd <dir> 指定已存在的目录");
            return cwd;
        }

        /// <summary>有上限的内存缓冲：超出上限继续计数但不再保留字节（防止把 GB 级输出读爆内存）。</summary>
        sealed class BoundedSink
        {
            readonly MemoryStream _ms = new MemoryStream();
            readonly long _cap;
            public long Total;
            public bool Truncated;
            public BoundedSink(long cap) { _cap = cap <= 0 ? 8L * 1024 * 1024 : cap; }
            public void Write(byte[] buf, int n)
            {
                Total += n;
                if (_ms.Length >= _cap) { Truncated = true; return; }
                int room = (int)Math.Min(n, _cap - _ms.Length);
                _ms.Write(buf, 0, room);
                if (room < n) Truncated = true;
            }
            public string Text() { return JobStore.DecodeBytes(_ms.ToArray()); }
        }

        static void PumpTo(Stream src, BoundedSink sink)
        {
            try
            {
                var buf = new byte[32768];
                while (true)
                {
                    int n = src.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sink.Write(buf, n);
                }
            }
            catch { }
        }

        // ================================================================ log append
        static int RunAppend(Ctx ctx)
        {
            string msg = ctx.Get("msg");
            if (string.IsNullOrEmpty(msg) && ctx.Positional.Length > 0)
                msg = string.Join(" ", ctx.Positional);
            if (string.IsNullOrEmpty(msg))
                throw ToolException.Usage("缺少 --msg", "例：log append --msg \"构建完成\" --level info --key step=build");

            string level = ctx.Get("level", "info");
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in ctx.GetAll("--key"))
            {
                if (string.IsNullOrEmpty(item)) continue;
                int eq = item.IndexOf('=');
                if (eq <= 0) throw ToolException.Usage("--key 格式非法：" + item, "写法：--key name=value（可重复）");
                kv[item.Substring(0, eq)] = item.Substring(eq + 1);
            }
            string file = ResolveLogFile(ctx.Get("file"));
            var parsed = JobStore.AppendLog(level, msg, kv, file);
            var item0 = Json.Obj(
                "ts", parsed.ContainsKey("ts") ? parsed["ts"] : null,
                "lvl", level, "msg", msg, "file", file,
                "keys", kv, "raw", parsed.ContainsKey("line") ? parsed["line"] : null);
            ctx.Out.Result("log.append", Json.Obj(
                "file", file,
                "level", level,
                "msg", msg,
                "keys", kv,
                "line", parsed.ContainsKey("line") ? parsed["line"] : null,
                "items", new List<object> { item0 },
                "count", 1,
                "columns", new[] { "ts", "lvl", "msg" }));
            return ExitCodes.Ok;
        }

        // ================================================================ log tail
        static int RunTail(Ctx ctx)
        {
            string fileArg = ctx.Get("file");
            string file = ResolveLogFile(fileArg);
            int lines = ctx.GetInt("--lines", 50);
            string level = ctx.Get("level");
            bool follow = ctx.Flag("follow");
            DateTime? since = ctx.GetDate("--since");
            long maxBytes = ctx.GetLong("--max-bytes", JobStore.DefaultMaxBytes);

            if (follow) return FollowTail(ctx, file, fileArg, lines, level, since, maxBytes);

            long total = 0, from = 0;
            bool trunc = false;
            List<string> raw;
            if (since.HasValue) raw = JobStore.ReadAllLines(file, maxBytes);
            else raw = JobStore.ReadTailLines(file, lines <= 0 ? 50 : lines, maxBytes, out from, out total, out trunc);

            var items = new List<object>();
            foreach (var line in Filter(raw, since, level))
                items.Add(ParseLogLine(line, file));
            if (items.Count > lines && lines > 0)
            {
                items = items.Skip(items.Count - lines).ToList();
                trunc = true;
            }
            if (trunc) ctx.Out.Truncated = true;

            ctx.Out.Result("log.tail", Json.Obj(
                "file", file,
                "name", Path.GetFileName(file),
                "lines", items,
                "items", items,
                "count", items.Count,
                "since", since,
                "level", level,
                "truncated", trunc,
                "columns", new[] { "ts", "lvl", "msg" }));
            return ExitCodes.Ok;
        }

        static int FollowTail(Ctx ctx, string file, string fileArg, int lines, string level, DateTime? since, long maxBytes)
        {
            ctx.Out.JsonLines = true;   // --follow 必须逐行 NDJSON
            JobStore.EmitFrame(Json.Obj("type", "meta", "cmd", "log.tail", "version", ToolInfo.Version, "file", file, "follow", true));

            int count = 0;
            long total, from;
            bool trunc;
            var initial = JobStore.ReadTailLines(file, lines <= 0 ? 20 : lines, maxBytes, out from, out total, out trunc);
            foreach (var line in Filter(initial, since, level))
            {
                JobStore.EmitFrame(Json.Obj("type", "item", "item", ParseLogLine(line, file)));
                count++;
            }

            string current = file;
            var follower = new TailFollower(current, 0);
            try
            {
                follower.SeekToEnd();
                while (true)
                {
                    if (ctx.Cancel.IsCancellationRequested) break;
                    // 默认文件跨天时自动切到新文件
                    if (string.IsNullOrEmpty(fileArg))
                    {
                        string today = JobStore.TodayLogFile();
                        if (!string.Equals(today, current, StringComparison.OrdinalIgnoreCase))
                        {
                            follower.Dispose();
                            current = today;
                            follower = new TailFollower(current, 0);
                            JobStore.EmitFrame(Json.Obj("type", "event", "event", "rotate", "file", current));
                        }
                    }
                    foreach (var line in follower.Poll(maxBytes))
                    {
                        var parsed = ParseLogLine(line, current);
                        if (!MatchLevel(parsed, level)) continue;
                        if (since.HasValue)
                        {
                            DateTime? ts = JobStore.ParseDate(Js.Str(parsed, "ts"));
                            if (!ts.HasValue || ts.Value < since.Value) continue;
                        }
                        JobStore.EmitFrame(Json.Obj("type", "item", "item", parsed));
                        count++;
                    }
                    Thread.Sleep(400);
                }
            }
            finally { follower.Dispose(); }

            JobStore.EmitFrame(Json.Obj("type", "summary", "ok", true, "cmd", "log.tail", "count", count, "file", current));
            return ctx.Cancel.IsCancellationRequested ? ExitCodes.Timeout : ExitCodes.Ok;
        }

        // ================================================================ log search
        static int RunSearch(Ctx ctx)
        {
            string pattern = ctx.Get("pattern");
            if (string.IsNullOrEmpty(pattern))
                throw ToolException.Usage("缺少 --pattern", "例：log search --pattern \"E_DENIED\" --since 3d");
            var data = SearchData(pattern, ctx.GetDate("--since"), ctx.GetInt("--limit", 200),
                ctx.Flag("ignore-case"), ctx.Get("file"), ctx.Get("level"),
                ctx.GetLong("--max-bytes", JobStore.DefaultMaxBytes));
            if (Js.Bool(data, "truncated", false)) ctx.Out.Truncated = true;
            ctx.Out.Result("log.search", data);
            return ExitCodes.Ok;
        }

        /// <summary>日志检索核心（serve 的 log.search 也走这里）。</summary>
        public static Dictionary<string, object> SearchData(string pattern, DateTime? since, int limit, bool ignoreCase,
                                                            string fileArg, string level, long maxBytes)
        {
            Regex re;
            try { re = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None); }
            catch (Exception ex) { throw ToolException.Usage("--pattern 正则非法：" + ex.Message); }

            var files = string.IsNullOrEmpty(fileArg)
                ? JobStore.LogFiles()
                : new List<string> { ResolveLogFile(fileArg) };

            var items = new List<object>();
            long scannedLines = 0;
            int scannedFiles = 0;
            bool truncated = false;
            foreach (var f in files)
            {
                if (limit > 0 && items.Count >= limit) { truncated = true; break; }
                scannedFiles++;
                var raw = JobStore.ReadAllLines(f, maxBytes);
                for (int i = raw.Count - 1; i >= 0; i--)     // 新行优先
                {
                    string line = raw[i];
                    scannedLines++;
                    if (!re.IsMatch(line)) continue;
                    var parsed = ParseLogLine(line, f);
                    if (since.HasValue)
                    {
                        DateTime? ts = JobStore.ParseDate(Js.Str(parsed, "ts"));
                        if (!ts.HasValue || ts.Value < since.Value) continue;
                    }
                    if (!MatchLevel(parsed, level)) continue;
                    items.Add(parsed);
                    if (limit > 0 && items.Count >= limit) { truncated = true; break; }
                }
            }

            return Json.Obj(
                "pattern", pattern,
                "since", since,
                "limit", limit,
                "ignoreCase", ignoreCase,
                "level", level,
                "files", scannedFiles,
                "scannedLines", scannedLines,
                "items", items,
                "count", items.Count,
                "truncated", truncated,
                "logs", Paths.Logs,
                "columns", new[] { "ts", "lvl", "msg", "file" });
        }

        // ================================================================ log runs
        static int RunRuns(Ctx ctx)
        {
            int limit = ctx.GetInt("--limit", 20);
            string cmdFilter = ctx.Get("cmd");
            bool hasExit = ctx.Has("exit");
            int exitFilter = ctx.GetInt("--exit", 0);
            DateTime? since = ctx.GetDate("--since");
            string type = ctx.Get("type");
            long maxBytes = ctx.GetLong("--max-bytes", 8L * 1024 * 1024);

            var file = JobStore.RunsFile();
            var raw = JobStore.ReadAllLines(file, maxBytes);
            var items = new List<object>();
            bool truncated = false;
            for (int i = raw.Count - 1; i >= 0; i--)
            {
                if (limit > 0 && items.Count >= limit) { truncated = true; break; }
                Dictionary<string, object> o;
                try { o = Json.Parse(raw[i]) as Dictionary<string, object>; }
                catch { continue; }
                if (o == null) continue;
                if (!string.IsNullOrEmpty(cmdFilter))
                {
                    string c = Js.Str(o, "cmd", "");
                    if (c.IndexOf(cmdFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (hasExit && Js.Int(o, "exit", int.MinValue) != exitFilter) continue;
                if (since.HasValue)
                {
                    DateTime? ts = JobStore.ParseDate(Js.Str(o, "ts"));
                    if (!ts.HasValue || ts.Value < since.Value) continue;
                }
                if (!string.IsNullOrEmpty(type) && !string.Equals(Js.Str(o, "type", "call"), type, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(Json.Obj(
                    "runId", Js.Str(o, "runId", null),
                    "type", Js.Str(o, "type", "call"),
                    "ts", o.ContainsKey("ts") ? o["ts"] : null,
                    "cmd", Js.Str(o, "cmd", ""),
                    "argv", o.ContainsKey("argv") ? o["argv"] : null,
                    "exit", Js.Int(o, "exit", -1),
                    "ms", Js.Long(o, "ms", 0),
                    "cwd", Js.Str(o, "cwd", null),
                    "pid", Js.Int(o, "pid", 0),
                    "outFile", Js.Str(o, "outFile", null),
                    "raw", raw[i]));
            }
            if (truncated) ctx.Out.Truncated = true;
            ctx.Out.Result("log.runs", Json.Obj(
                "file", file,
                "runsFile", file,
                "items", items,
                "count", items.Count,
                "limit", limit,
                "truncated", truncated,
                "columns", new[] { "ts", "type", "cmd", "exit", "ms" }));
            return ExitCodes.Ok;
        }

        // ================================================================ 公共工具
        public static string ResolveLogFile(string fileArg)
        {
            if (string.IsNullOrEmpty(fileArg)) return JobStore.TodayLogFile();
            bool looksPath = Path.IsPathRooted(fileArg) || fileArg.IndexOf('\\') >= 0 || fileArg.IndexOf('/') >= 0;
            string full = looksPath ? Fs.Expand(fileArg, Paths.Logs) : Path.Combine(Paths.Logs, fileArg);
            full = Path.GetFullPath(full);
            EnsureInsideHome(full);
            return full;
        }

        /// <summary>契约 §9.4：永不触碰数据目录之外的文件。</summary>
        public static void EnsureInsideHome(string full)
        {
            string home = Path.GetFullPath(Paths.Home);
            string f = Path.GetFullPath(full);
            if (!f.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                throw ToolException.Denied("只允许访问数据目录内的文件：" + f, "数据目录：" + home);
        }

        public static Dictionary<string, object> ParseLogLine(string line, string file)
        {
            Dictionary<string, object> d = null;
            if (!string.IsNullOrEmpty(line) && line[0] == '{')
            {
                try { d = Json.Parse(line) as Dictionary<string, object>; }
                catch { d = null; }
            }
            if (d == null)
            {
                return Json.Obj("ts", null, "lvl", null, "msg", line, "file", Path.GetFileName(file),
                    "path", file, "raw", line, "parsed", false);
            }
            return Json.Obj(
                "ts", d.ContainsKey("ts") ? d["ts"] : null,
                "lvl", Js.Str(d, "lvl", null),
                "msg", Js.Str(d, "msg", ""),
                "file", Path.GetFileName(file),
                "path", file,
                "raw", line,
                "parsed", true);
        }

        static bool MatchLevel(Dictionary<string, object> parsed, string level)
        {
            if (string.IsNullOrEmpty(level)) return true;
            return string.Equals(Js.Str(parsed, "lvl", ""), level, StringComparison.OrdinalIgnoreCase);
        }

        static List<string> Filter(List<string> raw, DateTime? since, string level)
        {
            var result = new List<string>();
            foreach (var line in raw)
            {
                if (since.HasValue || !string.IsNullOrEmpty(level))
                {
                    var p = ParseLogLine(line, "");
                    if (since.HasValue)
                    {
                        DateTime? ts = JobStore.ParseDate(Js.Str(p, "ts"));
                        if (!ts.HasValue || ts.Value < since.Value) continue;
                    }
                    if (!MatchLevel(p, level)) continue;
                }
                result.Add(line);
            }
            return result;
        }
    }
}

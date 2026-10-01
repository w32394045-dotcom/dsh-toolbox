using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// serve --stdio：面向行的 JSON-RPC 2.0（NDJSON），契约见 docs/CLI-CONTRACT.md §8。
    /// 铁律：stdout 只允许协议帧（每帧单行合法 JSON 且立即 flush），一切人类文本走 stderr。
    /// 状态全部来自磁盘（JobStore / LogCommands），因此 DSH 可以长连接地看日志、查结果、发任务。
    /// </summary>
    public static class ServeCommand
    {
        static readonly object Sync = new object();

        public static void Register()
        {
            Registry.Add("serve", "常驻 stdio JSON-RPC 2.0 服务（NDJSON）：读日志/查结果/发任务",
                "serve --stdio",
                RunServe,
                examples: new[] { "dsh-toolbox serve --stdio" });
        }

        sealed class ServeState
        {
            public bool Initialized;
            public TailFollower Follow;
            public string FollowFile;
            public DateTime? FollowSince;
            public int FollowCount;
            public bool Shutdown;
        }

        // ================================================================ 主循环
        static int RunServe(Ctx ctx)
        {
            if (!ctx.Flag("stdio"))
                Console.Error.WriteLine("[serve] 未显式给 --stdio，仍以 stdio 模式运行（stdout 只会出现协议帧）");

            var queue = new BlockingCollection<string>();

            var reader = new Thread(() =>
            {
                // 显式 UTF-8（不带 BOM 检测）：Windows 客户端经常写出带 BOM 的文件，
                // 不能让 U+FEFF 混进第一帧里（否则 initialize 会失败，整条连接被判成未初始化）。
                StreamReader stdin = null;
                try { stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 8192); }
                catch { }
                try
                {
                    string l;
                    while ((l = (stdin != null ? stdin.ReadLine() : Console.In.ReadLine())) != null)
                        queue.Add(l);
                }
                catch { }
                finally
                {
                    try { if (stdin != null) stdin.Dispose(); } catch { }
                    try { queue.CompleteAdding(); } catch { }
                }
            });
            reader.IsBackground = true;
            reader.Start();

            Console.Error.WriteLine("[serve] dsh-toolbox " + ToolInfo.Version + " ready  home=" + Paths.Home);

            var st = new ServeState();
            try
            {
                while (true)
                {
                    string line = null;
                    bool got = false;
                    try { got = queue.TryTake(out line, 200); }
                    catch (InvalidOperationException) { got = false; }

                    if (got)
                    {
                        if (line != null && line.Trim().Length > 0)
                        {
                            bool keep = HandleLine(ctx, line, st);
                            if (!keep) break;
                        }
                        continue;
                    }
                    if (queue.IsCompleted) break;               // stdin EOF → 干净退出

                    PumpFollowEvents(st);                       // 空闲时推送 log.tail --follow 事件
                    if (st.Shutdown) break;
                    if (ctx.Cancel.IsCancellationRequested) break;
                }
            }
            finally
            {
                if (st.Follow != null) st.Follow.Dispose();
            }

            Console.Error.WriteLine("[serve] bye");
            return ExitCodes.Ok;
        }

        // ================================================================ 单帧处理
        static bool HandleLine(Ctx ctx, string line, ServeState st)
        {
            line = StripBom(line);
            object parsed;
            try { parsed = Json.Parse(line); }
            catch (Exception ex) { SendError(null, -32700, "JSON 解析失败：" + ex.Message); return true; }

            var req = parsed as Dictionary<string, object>;
            if (req == null) { SendError(null, -32600, "请求必须是 JSON 对象"); return true; }

            string method = Js.Str(req, "method");
            bool hasId = req.ContainsKey("id") && req["id"] != null;
            object id = hasId ? req["id"] : null;

            if (string.IsNullOrEmpty(method))
            {
                SendError(id, -32600, "缺少 method");
                return true;
            }
            if (!st.Initialized && method != "initialize")
            {
                SendError(id, -32002, "服务未初始化：请先发送 initialize", Json.Obj("method", method));
                return true;
            }

            var prm = req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null;
            string logId = hasId ? Convert.ToString(id, CultureInfo.InvariantCulture) : "-";

            try
            {
                switch (method)
                {
                    // ---------------------------------------------------- initialize
                    case "initialize":
                        st.Initialized = true;
                        if (hasId)
                            SendResult(id, Json.Obj(
                                "serverInfo", Json.Obj("name", ToolInfo.Name, "version", ToolInfo.Version, "protocol", ToolInfo.Protocol),
                                "protocolVersion", ToolInfo.Protocol,
                                "home", Paths.Home,
                                "logs", Paths.Logs,
                                "runs", Paths.Runs,
                                "jobs", Paths.Jobs,
                                "cache", Paths.Cache,
                                "cwd", ctx.Cwd,
                                "pid", Process.GetCurrentProcess().Id,
                                "framework", Environment.Version.ToString(),
                                "capabilities", Json.Obj("ndjson", true, "events", true, "artifactRead", true, "jobKill", true),
                                "commands", CommandList(),
                                "commandNames", Registry.Names()));
                        return true;

                    case "notifications/initialized":      // 客户端惯用通知：忽略即可
                        return true;

                    // ---------------------------------------------------- ping
                    case "ping":
                        if (hasId)
                            SendResult(id, Json.Obj("pong", true, "ts", DateTime.Now,
                                "pid", Process.GetCurrentProcess().Id,
                                "uptimeMs", ctx.Out.Watch.ElapsedMilliseconds));
                        return true;

                    // ---------------------------------------------------- commands
                    case "commands.list":
                        {
                            var cmds = CommandList();
                            if (hasId)
                                SendResult(id, Json.Obj("name", ToolInfo.Name, "version", ToolInfo.Version,
                                    "home", Paths.Home, "commands", cmds, "count", cmds.Count,
                                    "columns", new[] { "name", "summary" }));
                            return true;
                        }

                    case "commands.schema":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{\"name\":\"scan.find\"}");
                            string name = Js.Str(prm, "name") ?? Js.Str(prm, "cmd");
                            if (string.IsNullOrWhiteSpace(name)) throw ToolException.Usage("params.name 必填");
                            var ci = FindCommand(name);
                            if (ci == null) throw ToolException.Usage("未知命令：" + name, "用 commands.list 查看全部命令");
                            if (hasId)
                                SendResult(id, Json.Obj(
                                    "name", ci.Name, "group", ci.Group, "action", ci.Action,
                                    "summary", ci.Summary, "usage", ci.Usage,
                                    "aliases", ci.Aliases, "examples", ci.Examples,
                                    "options", OptionsOf(ci),
                                    "globalOptions", GlobalOptions(),
                                    "returns", "单个 JSON 信封（等价 CLI 加 --json）"));
                            return true;
                        }

                    // ---------------------------------------------------- call
                    case "call":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{\"cmd\":\"scan find\",\"args\":[...]}");
                            string cmdName = Js.Str(prm, "cmd");
                            if (string.IsNullOrWhiteSpace(cmdName)) throw ToolException.Usage("params.cmd 必填");
                            var ci = FindCommand(cmdName);
                            if (ci == null)
                            {
                                if (hasId)
                                    SendResult(id, Json.Obj("ok", false, "cmd", cmdName, "version", ToolInfo.Version,
                                        "error", Json.Obj("code", "E_NOT_FOUND", "message", "未知命令：" + cmdName,
                                                          "hint", "用 commands.list 查看全部命令")));
                                return true;
                            }
                            if (string.Equals(ci.Name, "serve", StringComparison.OrdinalIgnoreCase))
                                throw ToolException.Usage("serve 内不支持再调用 serve（会死锁）");

                            var argList = new List<string>();
                            foreach (var x in Js.AsList(prm.ContainsKey("args") ? prm["args"] : null))
                                argList.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                            StripCmdPrefix(argList, ci);

                            bool quiet = Js.Bool(prm, "quiet", true);
                            int exit;
                            string raw;
                            object env = InvokeCommand(ctx, ci, argList, quiet, out exit, out raw);
                            if (env == null)
                            {
                                SendError(id, -32603, "命令没有产生 JSON 信封", Json.Obj("cmd", ci.Name, "exit", exit, "stdout", raw));
                                return true;
                            }
                            var d = env as Dictionary<string, object>;
                            if (d != null) d["exit"] = exit;
                            if (hasId) SendResult(id, env);
                            return true;
                        }

                    // ---------------------------------------------------- log
                    case "log.tail":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{file?, lines?, follow?}");
                            string fileArg = Js.Str(prm, "file");
                            string file = LogCommands.ResolveLogFile(fileArg);
                            int lines = (int)Js.Long(prm, "lines", 50);
                            bool follow = Js.Bool(prm, "follow", false);
                            DateTime? since = ParamDate(prm, "since");
                            long maxBytes = Js.Long(prm, "maxBytes", JobStore.DefaultMaxBytes);

                            long total, from;
                            bool trunc;
                            var rawLines = JobStore.ReadTailLines(file, lines <= 0 ? 50 : lines, maxBytes, out from, out total, out trunc);
                            var items = new List<object>();
                            foreach (var l in rawLines) items.Add(LogCommands.ParseLogLine(l, file));

                            if (hasId)
                                SendResult(id, Json.Obj("file", file, "name", Path.GetFileName(file),
                                    "lines", items, "items", items, "count", items.Count,
                                    "truncated", trunc, "follow", follow, "since", since));

                            if (follow)
                            {
                                if (st.Follow != null) st.Follow.Dispose();
                                var f = new TailFollower(file, 0);
                                f.SeekToEnd();
                                st.Follow = f;
                                st.FollowFile = file;
                                st.FollowSince = since;
                                st.FollowCount = 0;
                            }
                            return true;
                        }

                    case "log.search":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{pattern, since?, limit?}");
                            string pattern = Js.Str(prm, "pattern");
                            if (string.IsNullOrEmpty(pattern)) throw ToolException.Usage("params.pattern 必填");
                            var data = LogCommands.SearchData(pattern, ParamDate(prm, "since"),
                                (int)Js.Long(prm, "limit", 200), Js.Bool(prm, "ignoreCase", false),
                                Js.Str(prm, "file"), Js.Str(prm, "level"),
                                Js.Long(prm, "maxBytes", JobStore.DefaultMaxBytes));
                            if (hasId) SendResult(id, data);
                            return true;
                        }

                    // ---------------------------------------------------- job
                    case "job.list":
                        {
                            int limit = (int)Js.Long(prm, "limit", 50);
                            string state = Js.Str(prm, "state");
                            var all = JobStore.ListAll();
                            var items = new List<object>();
                            bool truncated = false;
                            foreach (var r in all)
                            {
                                if (!string.IsNullOrEmpty(state)
                                    && !string.Equals(r.State, state, StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(r.ObservedState, state, StringComparison.OrdinalIgnoreCase)) continue;
                                if (limit > 0 && items.Count >= limit) { truncated = true; break; }
                                items.Add(r.ToJson());
                            }
                            if (hasId)
                                SendResult(id, Json.Obj("items", items, "count", items.Count, "total", all.Count,
                                    "jobsDir", Paths.Jobs, "truncated", truncated,
                                    "columns", new[] { "jobId", "state", "observedState", "alive", "exitCode", "commandLine" }));
                            return true;
                        }

                    case "job.get":
                    case "job.status":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{\"jobId\":\"...\"}");
                            string jobId = ParamJobId(prm);
                            var rec = LoadJobOrThrow(jobId);
                            if (hasId) SendResult(id, Json.Obj(
                                "jobId", rec.JobId, "state", rec.State, "observedState", rec.ObservedState,
                                "alive", rec.Alive, "exitCode", rec.ExitCode, "elapsedMs", rec.ElapsedMs,
                                "startedAt", rec.StartedAt, "endedAt", rec.EndedAt,
                                "commandLine", rec.CommandLine, "cwd", rec.Cwd, "note", rec.Note,
                                "stdoutBytes", rec.StdoutBytes, "stderrBytes", rec.StderrBytes,
                                "files", rec.Files(),
                                "items", new List<object> { rec.ToJson() }, "count", 1,
                                "columns", new[] { "jobId", "state", "exitCode", "elapsedMs", "commandLine" }));
                            return true;
                        }

                    case "job.output":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{jobId, tail?, since?}");
                            string jobId = ParamJobId(prm);
                            var rec = LoadJobOrThrow(jobId);
                            int tail = (int)Js.Long(prm, "tail", 100);
                            long maxBytes = Js.Long(prm, "maxBytes", JobStore.DefaultMaxBytes);
                            DateTime? since = ParamDate(prm, "since");
                            long oOff = 0, eOff = 0;
                            if (since.HasValue) JobStore.OffsetsSince(rec.Dir, since.Value, out oOff, out eOff);
                            var items = new List<object>();
                            var sbOut = new StringBuilder();
                            var sbErr = new StringBuilder();
                            CollectInto(rec.StdoutPath, "stdout", tail, since, oOff, maxBytes, items, sbOut);
                            CollectInto(rec.StderrPath, "stderr", tail, since, eOff, maxBytes, items, sbErr);
                            if (hasId) SendResult(id, Json.Obj(
                                "jobId", rec.JobId, "state", rec.State, "observedState", rec.ObservedState,
                                "alive", rec.Alive, "exitCode", rec.ExitCode, "elapsedMs", rec.ElapsedMs,
                                "stdout", sbOut.ToString(), "stderr", sbErr.ToString(),
                                "stdoutBytes", rec.StdoutBytes, "stderrBytes", rec.StderrBytes,
                                "files", rec.Files(), "items", items, "count", items.Count,
                                "columns", new[] { "stream", "n", "line" }));
                            return true;
                        }

                    case "job.kill":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{\"jobId\":\"...\",\"yes\":true}");
                            string jobId = ParamJobId(prm);
                            LoadJobOrThrow(jobId);   // 先确认存在
                            bool dry = Js.Bool(prm, "dryRun", false);
                            if (!dry && !Js.Bool(prm, "yes", false))
                            {
                                SendError(id, -32003, "破坏性操作需要确认：请在 params 里传 yes:true",
                                    Json.Obj("hint", "或传 dryRun:true 只看计划；CLI 等价：job kill " + jobId + " --yes"));
                                return true;
                            }
                            var ci = Registry.Find("job.kill");
                            var argList = new List<string> { jobId, dry ? "--dry-run" : "--yes" };
                            string reason = Js.Str(prm, "reason");
                            if (!string.IsNullOrEmpty(reason)) { argList.Add("--reason"); argList.Add(reason); }
                            int exit;
                            string raw;
                            object env = InvokeCommand(ctx, ci, argList, true, out exit, out raw);
                            if (env == null)
                            {
                                SendError(id, -32603, "job.kill 未产生 JSON 信封", Json.Obj("exit", exit, "stdout", raw));
                                return true;
                            }
                            var d = env as Dictionary<string, object>;
                            if (d != null) d["exit"] = exit;
                            if (hasId) SendResult(id, env);
                            return true;
                        }

                    // ---------------------------------------------------- artifact
                    case "artifact.read":
                        {
                            if (prm == null) throw ToolException.Usage("params 必填：{runId|path, offset?, limit?}");
                            string runId = Js.Str(prm, "runId");
                            string path = Js.Str(prm, "path");
                            if (string.IsNullOrEmpty(runId) && string.IsNullOrEmpty(path))
                                throw ToolException.Usage("需要 runId 或 path");
                            if (!string.IsNullOrEmpty(runId))
                            {
                                string idRaw = runId;
                                if (idRaw.EndsWith(".out.json", StringComparison.OrdinalIgnoreCase))
                                    idRaw = idRaw.Substring(0, idRaw.Length - ".out.json".Length);
                                foreach (char c in idRaw)
                                    if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                                        throw ToolException.Usage("runId 非法：" + runId);
                                path = Path.Combine(Paths.Runs, idRaw + ".out.json");
                            }
                            else
                            {
                                path = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Paths.Runs, path));
                            }
                            if (!File.Exists(path))
                                throw ToolException.NotFound("结果文件不存在：" + path, "runs 目录：" + Paths.Runs);
                            LogCommands.EnsureInsideHome(path);   // 契约 §9.4：只在数据目录内读

                            long offset = Js.Long(prm, "offset", 0);
                            long limit = Js.Long(prm, "limit", 65536);
                            if (offset < 0) offset = 0;
                            if (limit <= 0 || limit > 8L * 1024 * 1024) limit = 8L * 1024 * 1024;
                            byte[] data;
                            long size;
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                size = fs.Length;
                                if (offset > size) offset = size;
                                long avail = Math.Min(size - offset, limit);
                                data = new byte[avail];
                                fs.Position = offset;
                                int got = 0;
                                while (got < data.Length)
                                {
                                    int n = fs.Read(data, got, data.Length - got);
                                    if (n <= 0) break;
                                    got += n;
                                }
                                if (got < data.Length) data = data.Take(got).ToArray();
                            }
                            if (hasId)
                                SendResult(id, Json.Obj(
                                    "path", path, "name", Path.GetFileName(path), "runId", runId,
                                    "size", size, "offset", offset, "length", (long)data.Length,
                                    "eof", offset + data.Length >= size,
                                    "encoding", "utf-8", "truncated", offset + data.Length < size,
                                    "content", JobStore.DecodeBytes(data)));
                            return true;
                        }

                    // ---------------------------------------------------- shutdown
                    case "shutdown":
                        if (hasId) SendResult(id, Json.Obj("ok", true, "bye", DateTime.Now));
                        st.Shutdown = true;
                        return true;

                    default:
                        SendError(id, -32601, "未知方法：" + method,
                            Json.Obj("methods", Methods()));
                        return true;
                }
            }
            catch (ToolException te)
            {
                SendError(id, -32602, te.Message, te.Hint == null ? null : Json.Obj("hint", te.Hint, "method", method));
                return true;
            }
            catch (Exception ex)
            {
                SendError(id, -32603, "内部错误：" + ex.Message, Json.Obj("type", ex.GetType().Name, "method", method));
                return true;
            }
        }

        // ================================================================ call 复用
        static object InvokeCommand(Ctx ctx, CommandInfo ci, List<string> argList, bool quiet, out int exit, out string raw)
        {
            exit = ExitCodes.Ok;
            raw = "";
            for (int i = argList.Count - 1; i >= 0; i--)
                if (string.Equals(argList[i], "--jsonl", StringComparison.OrdinalIgnoreCase)) argList.RemoveAt(i);
            if (!argList.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase))) argList.Add("--json");

            var m = ArgMap.Parse(argList);
            m.Set("json", "true");
            m.Set("jsonl", "false");
            m.Set("quiet", quiet ? "true" : "false");

            var outw = new Output { JsonMode = true, JsonLines = false, Quiet = quiet };
            var sub = new Ctx();
            sub.Args = m;
            sub.Out = outw;
            sub.Home = Paths.Home;
            sub.Cwd = ctx.Cwd;
            sub.ExePath = ctx.ExePath;
            sub.Verbose = m.Flag("verbose");
            sub.DryRun = m.Flag("dry-run") || m.Flag("dryrun");
            sub.Yes = m.Flag("yes");
            sub.NoLog = m.Flag("no-log");
            sub.CmdName = ci.Name;
            sub.Cancel = ctx.Cancel;
            sub.Log = ctx.Log;

            var oldOut = Console.Out;
            var sw = new StringWriter(new StringBuilder(4096), CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(sw);
                try { exit = ci.Run(sub); }
                catch (ToolException te)
                {
                    sw.Write(Json.Write(FailEnvelope(ci.Name, te.Code, te.Message, te.Hint, outw)));
                    exit = te.Exit;
                }
                catch (OperationCanceledException)
                {
                    sw.Write(Json.Write(FailEnvelope(ci.Name, "E_CANCELLED", "操作被取消（超时或 Ctrl+C）", "--timeout 可调整上限", outw)));
                    exit = ExitCodes.Timeout;
                }
                catch (Exception ex)
                {
                    sw.Write(Json.Write(FailEnvelope(ci.Name, "E_INTERNAL", ex.GetType().Name + ": " + ex.Message, "加 verbose:true 查看细节", outw)));
                    exit = ExitCodes.Error;
                }
            }
            finally { Console.SetOut(oldOut); }

            raw = sw.ToString();
            string text = raw.Trim();
            if (text.Length == 0) return null;
            try { return Json.Parse(text); }
            catch { return null; }
        }

        static Dictionary<string, object> FailEnvelope(string cmd, string code, string message, string hint, Output outw)
        {
            var err = Json.Obj("code", code, "message", message);
            if (!string.IsNullOrEmpty(hint)) err["hint"] = hint;
            return Json.Obj("ok", false, "cmd", cmd, "version", ToolInfo.Version,
                "elapsedMs", outw.Watch.ElapsedMilliseconds, "error", err);
        }

        // ================================================================ 事件推送
        static void PumpFollowEvents(ServeState st)
        {
            if (st.Follow == null) return;
            List<string> lines;
            try { lines = st.Follow.Poll(JobStore.DefaultMaxBytes); }
            catch { return; }
            foreach (var l in lines)
            {
                var parsed = LogCommands.ParseLogLine(l, st.FollowFile);
                if (st.FollowSince.HasValue)
                {
                    DateTime? ts = JobStore.ParseDate(Js.Str(parsed, "ts"));
                    if (!ts.HasValue || ts.Value < st.FollowSince.Value) continue;
                }
                st.FollowCount++;
                Send(Json.Obj("jsonrpc", "2.0", "method", "event",
                    "params", Json.Obj("type", "log", "seq", st.FollowCount, "file", st.FollowFile, "line", parsed)));
            }
        }

        // ================================================================ 帧输出
        static void Send(object frame)
        {
            lock (Sync)
            {
                Console.Out.WriteLine(Json.Write(frame));
                Console.Out.Flush();
            }
        }

        static void SendResult(object id, object result)
        {
            Send(new JsonObject().Add("jsonrpc", "2.0").Add("id", id).Add("result", result));
        }

        static void SendError(object id, int code, string message, object data = null)
        {
            var e = new JsonObject().Add("code", code).Add("message", message);
            if (data != null) e.Add("data", data);
            Send(new JsonObject().Add("jsonrpc", "2.0").Add("id", id).Add("error", e));
        }

        // ================================================================ 小工具
        /// <summary>剥掉行首的 U+FEFF（UTF-8 BOM 被当字符读进来时）与零宽字符。</summary>
        static string StripBom(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            int i = 0;
            while (i < line.Length && (line[i] == '\uFEFF' || line[i] == '\u200B')) i++;
            return i == 0 ? line : line.Substring(i);
        }

        static string[] Methods()
        {
            return new[]
            {
                "initialize", "ping", "commands.list", "commands.schema", "call",
                "log.tail", "log.search", "job.list", "job.get", "job.status", "job.output",
                "job.kill", "artifact.read", "shutdown"
            };
        }

        static CommandInfo FindCommand(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var ci = Registry.Find(name.Trim());
            if (ci != null) return ci;
            return Registry.Find(name.Trim().Replace(' ', '.'));
        }

        static List<object> CommandList()
        {
            var cmds = new List<object>();
            foreach (var ci in Registry.All())
                cmds.Add(Json.Obj("name", ci.Name, "group", ci.Group, "action", ci.Action,
                    "summary", ci.Summary, "usage", ci.Usage, "aliases", ci.Aliases));
            return cmds;
        }

        static List<object> GlobalOptions()
        {
            return new List<object>
            {
                Json.Obj("name", "--json", "type", "bool", "desc", "stdout 输出单个 JSON 信封"),
                Json.Obj("name", "--jsonl", "type", "bool", "desc", "流式 JSON，每行一条"),
                Json.Obj("name", "--quiet", "type", "bool", "desc", "抑制 stderr 信息"),
                Json.Obj("name", "--dry-run", "type", "bool", "desc", "破坏性操作只预览"),
                Json.Obj("name", "--yes", "type", "bool", "desc", "确认破坏性操作"),
                Json.Obj("name", "--timeout", "type", "duration", "desc", "全局超时，如 30s/5m"),
                Json.Obj("name", "--home", "type", "path", "desc", "数据目录"),
                Json.Obj("name", "--cwd", "type", "path", "desc", "相对路径基准"),
                Json.Obj("name", "--verbose", "type", "bool", "desc", "诊断信息"),
                Json.Obj("name", "--no-log", "type", "bool", "desc", "不写运行记录")
            };
        }

        static readonly Regex OptRe = new Regex(@"--([A-Za-z0-9][A-Za-z0-9-]*)(?:\s+<([^>]+)>)?");

        static List<object> OptionsOf(CommandInfo ci)
        {
            var list = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string usage = ci.Usage ?? "";
            foreach (Match m in OptRe.Matches(usage))
            {
                string name = "--" + m.Groups[1].Value;
                if (!seen.Add(name)) continue;
                string ph = m.Groups[2].Success ? m.Groups[2].Value : null;
                list.Add(Json.Obj("name", name, "type", ph == null ? "bool" : GuessType(ph), "placeholder", ph));
            }
            foreach (var g in GlobalOptions())
            {
                var gd = g as Dictionary<string, object>;
                string gname = Js.Str(gd, "name");
                if (seen.Add(gname)) list.Add(g);
            }
            return list;
        }

        static string GuessType(string ph)
        {
            string p = ph.ToLowerInvariant();
            if (p == "n" || p.Contains("num") || p.Contains("count") || p.Contains("limit")
                || p.Contains("offset") || p.Contains("lines") || p.Contains("size")) return "int";
            if (p.Contains("dur") || p.Contains("timeout") || p.Contains("since")) return "duration|date";
            if (p.Contains("dir") || p.Contains("path") || p.Contains("file")) return "path";
            if (p.Contains("re") || p.Contains("regex") || p.Contains("pattern")) return "regex";
            return "string";
        }

        static void StripCmdPrefix(List<string> args, CommandInfo ci)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].StartsWith("-", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(ci.Group) && i + 1 < args.Count
                    && string.Equals(args[i], ci.Group, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(args[i + 1], ci.Action, StringComparison.OrdinalIgnoreCase))
                {
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
                else if (string.Equals(args[i], ci.Name, StringComparison.OrdinalIgnoreCase))
                {
                    args.RemoveAt(i);
                }
                return;
            }
        }

        static DateTime? ParamDate(IDictionary<string, object> prm, string key)
        {
            string s = Js.Str(prm, key);
            if (string.IsNullOrEmpty(s)) return null;
            DateTime? d = ArgMap.TryParseDate(s);
            if (d == null) throw ToolException.Usage("params." + key + " 时间格式非法：" + s, "例：2026-10-01 / -2h / 3d");
            return d;
        }

        static string ParamJobId(IDictionary<string, object> prm)
        {
            string jobId = Js.Str(prm, "jobId");
            if (string.IsNullOrEmpty(jobId)) jobId = Js.Str(prm, "id");
            if (string.IsNullOrEmpty(jobId)) throw ToolException.Usage("params.jobId 必填");
            return jobId;
        }

        static JobRecord LoadJobOrThrow(string jobId)
        {
            try { return JobStore.Load(jobId); }
            catch (ToolException te)
            {
                if (te.Exit == ExitCodes.NotFound)
                    throw ToolException.Usage("任务不存在：" + jobId, "用 job.list 查看全部任务 id");
                throw;
            }
        }

        static void CollectInto(string file, string label, int tail, DateTime? since, long sinceOffset,
                                long maxBytes, List<object> items, StringBuilder sb)
        {
            List<string> lines;
            if (since.HasValue)
            {
                long newOff;
                lines = JobStore.ReadLinesFrom(file, sinceOffset, maxBytes, out newOff);
            }
            else
            {
                long from, total;
                bool trunc;
                lines = JobStore.ReadTailLines(file, tail <= 0 ? int.MaxValue : tail, maxBytes, out from, out total, out trunc);
            }
            int n = 0;
            foreach (var l in lines)
            {
                n++;
                items.Add(Json.Obj("stream", label, "n", n, "line", l));
                sb.Append(l).Append('\n');
            }
        }
    }
}

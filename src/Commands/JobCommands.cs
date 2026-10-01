using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// 后台任务：job start / list / status(get) / output / kill。
    /// 全部状态落盘（Paths.Jobs\&lt;jobid&gt;\cmd.json|status.json|stdout.log|stderr.log|index.jsonl），
    /// 进程内不留任何状态，任何一次新 CLI 调用都能从磁盘还原。
    /// job.start 通过"分离进程"启动本 exe 的内部执行器（--exec-helper），执行器不写 stdout，
    /// 只负责跑目标命令并把状态写进 status.json，因此不会随调用它的 CLI 进程一起退出。
    /// </summary>
    public static class JobCommands
    {
        public static void Register()
        {
            Registry.Add("job.start", "后台启动一条命令（分离进程，不随 CLI 退出而终止）",
                "job start [--name <标签>] [--cwd <dir>] [--env K=V]... [--shell] [--timeout <dur>] [--cmd <原始命令行>] -- <命令> [参数...]",
                RunStart,
                examples: new[]
                {
                    "dsh-toolbox job start -- cmd /c \"ping -n 20 127.0.0.1\"",
                    "dsh-toolbox job start --name build --timeout 10m -- dotnet build -c Release",
                    "dsh-toolbox job start --shell -- \"echo hi > out.txt & exit 3\""
                });

            Registry.Add("job.list", "列出后台任务（默认 50 条，新的在前）",
                "job list [--limit <n>] [--state <state>] [--all]",
                RunList,
                examples: new[] { "dsh-toolbox job list --json", "dsh-toolbox job list --state running" });

            Registry.Add("job.status", "查看某个后台任务的状态与文件位置",
                "job status (<jobId>|--id <jobId>)",
                RunStatus,
                aliases: new[] { "job.get" },
                examples: new[] { "dsh-toolbox job status 20260101-120000-000-ab12cd --json" });

            Registry.Add("job.output", "读取后台任务输出（--tail / --since / --follow）",
                "job output (<jobId>|--id <jobId>) [--tail <n>] [--since <dur|date>] [--stream stdout|stderr|both] [--follow] [--max-bytes <n>]",
                RunOutput,
                examples: new[] { "dsh-toolbox job output <jobId> --tail 50 --json", "dsh-toolbox job output <jobId> --follow --jsonl" });

            Registry.Add("job.kill", "结束后台任务（破坏性：需 --yes；--dry-run 只预览）",
                "job kill (<jobId>|--id <jobId>) [--dry-run|--yes] [--reason <文本>]",
                RunKill,
                examples: new[] { "dsh-toolbox job kill <jobId> --dry-run", "dsh-toolbox job kill <jobId> --yes" });
        }

        // ================================================================ start
        static int RunStart(Ctx ctx)
        {
            // 内部执行器模式（由 job.start 分离启动；不是给人与 agent 直接调用的）
            string helperId = ctx.Get("exec-helper");
            if (!string.IsNullOrEmpty(helperId)) return RunHelper(ctx, helperId);

            var target = JobStore.ResolveTarget(ctx);
            var env = JobStore.EnvOverrides(ctx);
            string cwd = ResolveCwd(ctx);
            int timeoutSec = (int)ctx.GetSpan("--timeout", TimeSpan.Zero).TotalSeconds;

            var rec = new JobRecord();
            rec.JobId = JobStore.NewJobId();
            rec.Dir = JobStore.JobDir(rec.JobId);
            rec.Name = ctx.Get("name", "");
            rec.Argv = target.Argv;
            rec.CommandLine = target.Display;
            rec.Cwd = cwd;
            rec.Shell = target.Shell;
            rec.TimeoutSec = timeoutSec;
            rec.CreatedAt = DateTime.Now;
            rec.StartedAt = DateTime.Now;
            rec.State = "starting";
            Fs.EnsureDir(rec.Dir);

            string token = Guid.NewGuid().ToString("N");
            try { using (File.Create(rec.StdoutPath)) { } } catch { }
            try { using (File.Create(rec.StderrPath)) { } } catch { }
            JobStore.AppendIndex(rec.Dir, 0, 0);
            JobStore.WriteStatus(rec);
            JobStore.WriteCmd(rec, env, token, "pending", ctx.ExePath);

            string args = "job start --exec-helper " + rec.JobId
                        + " --token " + token
                        + " --home " + JobStore.QuoteWinArg(Paths.Home)
                        + " --no-log --quiet";

            string mode;
            int pid = JobStore.LaunchDetached(ctx.ExePath, args, rec.Dir, out mode);
            if (pid <= 0)
                throw new ToolException("E_SPAWN", "后台任务启动失败：" + mode,
                    "可用 run 前台执行，或检查杀软/策略是否拦截了进程创建");

            rec.HelperPid = pid;
            JobStore.WriteCmd(rec, env, token, mode, ctx.ExePath);
            JobStore.WriteStatus(rec);   // 把 helperPid 落盘，job list/status 立刻可见

            // 启动握手：等 helper 落盘 state=running（含 targetPid）/终态，最多 3s。
            // 🚨 绝不能用"helper 是否还活着"当失败判据：秒退命令的 helper 会先退出，
            //    但任务其实已经正常跑完（status.json = completed）。只有"helper 已退出且状态仍是
            //    starting"才说明执行器真的没起来。
            var handshake = Stopwatch.StartNew();
            var fresh = JobStore.LoadFromDir(rec.JobId, rec.Dir);
            while (fresh.State == "starting" && handshake.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(100);
                fresh = JobStore.LoadFromDir(rec.JobId, rec.Dir);
            }
            if (fresh.State == "starting" && !JobStore.IsAlive(pid))
            {
                string tail = TailText(rec.HelperLogPath, 20);
                throw new ToolException("E_SPAWN", "后台执行器未能启动（pid " + pid + " 已退出，状态仍为 starting）",
                    string.IsNullOrEmpty(tail) ? "见 " + rec.HelperLogPath : tail);
            }
            // 目标进程已经结束（秒级命令）→ 再等最多 1s 让 helper 回写终态，直接返回 completed + exitCode，
            // 这样调用方不用再来一次 job.status。
            if (fresh.State == "running")
            {
                var settle = Stopwatch.StartNew();
                while (fresh.State == "running" && settle.ElapsedMilliseconds < 900)
                {
                    Thread.Sleep(60);
                    fresh = JobStore.LoadFromDir(rec.JobId, rec.Dir);
                }
            }

            var job = fresh.ToJson();
            ctx.Out.Result("job.start", Json.Obj(
                "jobId", fresh.JobId,
                "state", fresh.State,
                "observedState", fresh.ObservedState,
                "commandLine", fresh.CommandLine,
                "cwd", fresh.Cwd,
                "pid", fresh.HelperPid,
                "targetPid", fresh.TargetPid,
                "detach", mode,
                "timeoutSec", fresh.TimeoutSec,
                "exitCode", fresh.ExitCode,
                "jobElapsedMs", fresh.ElapsedMs,
                "stdoutBytes", fresh.StdoutBytes,
                "stderrBytes", fresh.StderrBytes,
                "startedAt", fresh.StartedAt,
                "endedAt", fresh.EndedAt,
                "dir", fresh.Dir,
                "files", fresh.Files(),
                "items", new List<object> { job },
                "count", 1,
                "columns", new[] { "jobId", "state", "observedState", "pid", "commandLine" }));
            return ExitCodes.Ok;
        }

        static string ResolveCwd(Ctx ctx)
        {
            string cwdArg = ctx.Get("cwd");
            string cwd = string.IsNullOrEmpty(cwdArg) ? ctx.Cwd : Fs.Expand(cwdArg, ctx.Cwd);
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
                throw ToolException.NotFound("工作目录不存在：" + (cwdArg ?? ctx.Cwd), "用 --cwd <dir> 指定已存在的目录");
            return cwd;
        }

        static string TailText(string file, int lines)
        {
            long from, total;
            bool trunc;
            var l = JobStore.ReadTailLines(file, lines, 64 * 1024, out from, out total, out trunc);
            return string.Join(" | ", l.ToArray());
        }

        // ================================================================ 执行器
        /// <summary>
        /// 独立进程里的真正执行者：跑目标命令 → 写 stdout.log/stderr.log → 回写 status.json。
        /// 只写文件，绝不写 stdout/stderr（父句柄可能已随调用方 CLI 关闭）。
        /// </summary>
        static int RunHelper(Ctx ctx, string jobId)
        {
            string dir;
            try { dir = JobStore.JobDir(jobId); }
            catch (Exception) { return ExitCodes.Usage; }

            var rec = JobStore.LoadFromDir(jobId, dir);
            var cmd = JobStore.ReadJsonObject(rec.CmdPath);
            string token = cmd == null ? null : Js.Str(cmd, "token", null);
            if (string.IsNullOrEmpty(token) || !string.Equals(token, ctx.Get("token"), StringComparison.Ordinal))
                return ExitCodes.Usage;   // 令牌不符：拒绝执行

            Fs.EnsureDir(dir);
            Paths.Ensure();
            // 执行器绝不写 stdout/stderr：它们的父句柄可能已经随调用方 CLI 一起关闭了。
            try { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); } catch { }
            StreamWriter log = null;
            try { log = new StreamWriter(rec.HelperLogPath, true, new UTF8Encoding(false)) { AutoFlush = true }; } catch { }
            Action<string> trace = m =>
            {
                try { if (log != null) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + m); } catch { }
            };
            trace("helper start job=" + jobId + " pid=" + Process.GetCurrentProcess().Id);

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var envObj = cmd != null && cmd.ContainsKey("env") ? cmd["env"] as Dictionary<string, object> : null;
            if (envObj != null)
                foreach (var kv in envObj) env[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);

            var target = new TargetCommand();
            target.Shell = rec.Shell;
            target.Argv = rec.Argv;
            target.ShellLine = rec.Shell ? rec.CommandLine : null;
            target.Display = rec.CommandLine;

            var self = Process.GetCurrentProcess();
            DateTime selfStart = DateTime.Now;
            try { selfStart = self.StartTime; } catch { }

            if ((!target.Shell && target.Argv.Length == 0) || (target.Shell && string.IsNullOrEmpty(target.ShellLine)))
            {
                rec.State = "error";
                rec.Note = "cmd.json 里的目标命令为空";
                rec.HelperPid = self.Id;
                rec.EndedAt = DateTime.Now;
                JobStore.WriteStatus(rec);
                trace("empty target");
                if (log != null) log.Dispose();
                return ExitCodes.Error;
            }

            Process p;
            var psi = JobStore.BuildPsi(target, rec.Cwd, env);
            try
            {
                p = new Process { StartInfo = psi };
                if (!p.Start())
                {
                    rec.State = "error"; rec.Note = "进程创建失败";
                    rec.HelperPid = self.Id; rec.EndedAt = DateTime.Now;
                    JobStore.WriteStatus(rec);
                    if (log != null) log.Dispose();
                    return ExitCodes.Error;
                }
                try { p.StandardInput.Close(); } catch { }
            }
            catch (Exception ex)
            {
                rec.State = "error";
                rec.Note = ex.Message;
                rec.HelperPid = self.Id;
                rec.EndedAt = DateTime.Now;
                JobStore.WriteStatus(rec);
                trace("start failed: " + ex.Message);
                if (log != null) log.Dispose();
                return ExitCodes.Error;
            }

            int childPid = 0;
            DateTime childStart = DateTime.Now;
            try { childPid = p.Id; } catch { }
            try { childStart = p.StartTime; } catch { }

            rec.HelperPid = self.Id;
            rec.HelperStart = selfStart;
            rec.TargetPid = childPid;
            rec.TargetStart = childStart;
            rec.State = "running";
            rec.StartedAt = DateTime.Now;
            JobStore.WriteStatus(rec);
            JobStore.AppendIndex(rec.Dir, 0, 0);
            trace("target started pid=" + childPid + " file=" + psi.FileName + " args=" + psi.Arguments);

            long outBytes = 0, errBytes = 0;
            var tOut = Task.Factory.StartNew(() => JobStore.PumpStream(p.StandardOutput.BaseStream, rec.StdoutPath, n => Interlocked.Add(ref outBytes, n), () => true));
            var tErr = Task.Factory.StartNew(() => JobStore.PumpStream(p.StandardError.BaseStream, rec.StderrPath, n => Interlocked.Add(ref errBytes, n), () => true));

            var sw = Stopwatch.StartNew();
            bool killed = false, timedOut = false;
            long lastIndexed = -1;
            DateTime nextStatus = DateTime.Now.AddSeconds(5);
            DateTime nextIndex = DateTime.Now.AddMilliseconds(800);

            while (true)
            {
                bool exited = false;
                try { exited = p.WaitForExit(250); } catch { exited = true; }
                if (exited) break;

                if (ctx.Cancel.IsCancellationRequested) { killed = true; rec.Note = "执行器被取消"; JobStore.KillTree(childPid, childStart); break; }
                if (rec.TimeoutSec > 0 && sw.Elapsed.TotalSeconds >= rec.TimeoutSec) { timedOut = true; JobStore.KillTree(childPid, childStart); break; }
                if (File.Exists(rec.KillPath)) { killed = true; rec.Note = "收到 job kill 请求"; JobStore.KillTree(childPid, childStart); break; }

                long total = Interlocked.Read(ref outBytes) + Interlocked.Read(ref errBytes);
                if (total != lastIndexed && DateTime.Now >= nextIndex)
                {
                    lastIndexed = total;
                    JobStore.AppendIndex(rec.Dir, Interlocked.Read(ref outBytes), Interlocked.Read(ref errBytes));
                    nextIndex = DateTime.Now.AddMilliseconds(800);
                }
                if (DateTime.Now >= nextStatus)
                {
                    rec.StdoutBytes = Interlocked.Read(ref outBytes);
                    rec.StderrBytes = Interlocked.Read(ref errBytes);
                    rec.ElapsedMs = sw.ElapsedMilliseconds;
                    JobStore.WriteStatus(rec);
                    nextStatus = DateTime.Now.AddSeconds(5);
                }
            }

            try { p.WaitForExit(5000); } catch { }
            try { Task.WaitAll(new[] { tOut, tErr }, 8000); } catch { }
            int exitCode = -1;
            try { if (p.HasExited) exitCode = p.ExitCode; } catch { }
            sw.Stop();

            rec.HelperPid = self.Id;
            rec.TargetPid = childPid;
            rec.StdoutBytes = Interlocked.Read(ref outBytes);
            rec.StderrBytes = Interlocked.Read(ref errBytes);
            rec.ElapsedMs = sw.ElapsedMilliseconds;
            rec.EndedAt = DateTime.Now;
            rec.ExitCode = exitCode;
            if (timedOut)
            {
                rec.State = "timeout";
                rec.Note = "超过 --timeout " + rec.TimeoutSec + "s，已结束后台进程树";
            }
            else if (killed || File.Exists(rec.KillPath))
            {
                rec.State = "killed";
                rec.Note = string.IsNullOrEmpty(rec.Note) ? "被 job kill 结束" : rec.Note;
            }
            else if (exitCode == 0)
            {
                rec.State = "completed";
                rec.Note = null;
            }
            else
            {
                rec.State = "failed";
                rec.Note = "退出码 " + exitCode.ToString(CultureInfo.InvariantCulture);
            }
            JobStore.AppendIndex(rec.Dir, rec.StdoutBytes, rec.StderrBytes);
            JobStore.WriteStatus(rec);
            trace("helper end state=" + rec.State + " exit=" + exitCode + " out=" + rec.StdoutBytes + " err=" + rec.StderrBytes + " ms=" + rec.ElapsedMs);
            if (log != null) log.Dispose();
            return ExitCodes.Ok;
        }

        // ================================================================ list
        static int RunList(Ctx ctx)
        {
            int limit = ctx.GetInt("--limit", 50);
            string state = ctx.Get("--state");
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
            if (truncated) ctx.Out.Truncated = true;
            ctx.Out.Result("job.list", Json.Obj(
                "items", items,
                "count", items.Count,
                "total", all.Count,
                "jobsDir", Paths.Jobs,
                "limit", limit,
                "state", state,
                "truncated", truncated,
                "columns", new[] { "jobId", "state", "observedState", "alive", "exitCode", "commandLine" }));
            return ExitCodes.Ok;
        }

        // ================================================================ status
        static int RunStatus(Ctx ctx)
        {
            var rec = RequireJob(ctx);
            var job = rec.ToJson();
            ctx.Out.Result("job.status", Json.Obj(
                "jobId", rec.JobId,
                "state", rec.State,
                "observedState", rec.ObservedState,
                "alive", rec.Alive,
                "exitCode", rec.ExitCode,
                "elapsedMs", rec.ElapsedMs,
                "startedAt", rec.StartedAt,
                "endedAt", rec.EndedAt,
                "commandLine", rec.CommandLine,
                "cwd", rec.Cwd,
                "stdoutBytes", rec.StdoutBytes,
                "stderrBytes", rec.StderrBytes,
                "note", rec.Note,
                "files", rec.Files(),
                "items", new List<object> { job },
                "count", 1,
                "columns", new[] { "jobId", "state", "exitCode", "elapsedMs", "commandLine" }));
            return ExitCodes.Ok;
        }

        // ================================================================ output
        static int RunOutput(Ctx ctx)
        {
            var rec = RequireJob(ctx);
            int tail = ctx.GetInt("--tail", 100);
            string stream = (ctx.Get("stream", "both") ?? "both").ToLowerInvariant();
            if (stream != "stdout" && stream != "stderr" && stream != "both")
                throw ToolException.Usage("--stream 只能是 stdout / stderr / both：" + stream);
            bool follow = ctx.Flag("follow");
            DateTime? since = ctx.GetDate("--since");
            long maxBytes = ctx.GetLong("--max-bytes", JobStore.DefaultMaxBytes);

            long outOff = 0, errOff = 0;
            if (since.HasValue) JobStore.OffsetsSince(rec.Dir, since.Value, out outOff, out errOff);

            if (follow) return FollowOutput(ctx, rec, stream, since, outOff, errOff, maxBytes, tail > 0 ? tail : 100);

            var items = new List<object>();
            bool truncated = false;
            string outText = "", errText = "";
            if (stream != "stderr") outText = Collect(rec.StdoutPath, "stdout", tail, since, outOff, maxBytes, items, ref truncated);
            if (stream != "stdout") errText = Collect(rec.StderrPath, "stderr", tail, since, errOff, maxBytes, items, ref truncated);
            if (truncated) ctx.Out.Truncated = true;

            ctx.Out.Result("job.output", Json.Obj(
                "jobId", rec.JobId,
                "state", rec.State,
                "observedState", rec.ObservedState,
                "alive", rec.Alive,
                "exitCode", rec.ExitCode,
                "elapsedMs", rec.ElapsedMs,
                "stdoutBytes", rec.StdoutBytes,
                "stderrBytes", rec.StderrBytes,
                "stdout", outText,
                "stderr", errText,
                "since", since,
                "tail", tail,
                "stream", stream,
                "files", rec.Files(),
                "items", items,
                "count", items.Count,
                "truncated", truncated,
                "columns", new[] { "stream", "n", "line" }));
            return ExitCodes.Ok;
        }

        static string Collect(string file, string label, int tail, DateTime? since, long sinceOffset,
                              long maxBytes, List<object> items, ref bool truncated)
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
                if (trunc) truncated = true;
            }
            var sb = new StringBuilder();
            int n = 0;
            foreach (var line in lines)
            {
                n++;
                items.Add(Json.Obj("stream", label, "n", n, "line", line));
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        static int FollowOutput(Ctx ctx, JobRecord rec, string stream, DateTime? since, long outOff, long errOff, long maxBytes, int tail)
        {
            ctx.Out.JsonLines = true;    // --follow 必须逐行 NDJSON，否则无法边跑边读
            JobStore.EmitFrame(Json.Obj("type", "meta", "cmd", "job.output", "version", ToolInfo.Version,
                "jobId", rec.JobId, "follow", true, "stream", stream));

            int count = 0;
            if (stream != "stderr")
                foreach (var line in ReadInitial(rec.StdoutPath, tail, since, outOff, maxBytes))
                {
                    JobStore.EmitFrame(Json.Obj("type", "item", "jobId", rec.JobId, "stream", "stdout", "line", line));
                    count++;
                }
            if (stream != "stdout")
                foreach (var line in ReadInitial(rec.StderrPath, tail, since, errOff, maxBytes))
                {
                    JobStore.EmitFrame(Json.Obj("type", "item", "jobId", rec.JobId, "stream", "stderr", "line", line));
                    count++;
                }

            var fo = stream == "stderr" ? null : new TailFollower(rec.StdoutPath, 0);
            var fe = stream == "stdout" ? null : new TailFollower(rec.StderrPath, 0);
            try
            {
                // 跟随起点：给定 --since 从偏移起，否则从当前文件末尾起
                if (fo != null && !since.HasValue) fo.SeekToEnd();
                if (fe != null && !since.HasValue) fe.SeekToEnd();

                int idleRounds = 0;
                while (true)
                {
                    if (ctx.Cancel.IsCancellationRequested) break;
                    var fresh = JobStore.LoadFromDir(rec.JobId, rec.Dir);
                    bool alive = fresh.ObservedState == "running";
                    bool got = false;
                    if (fo != null)
                        foreach (var line in fo.Poll(maxBytes))
                        {
                            JobStore.EmitFrame(Json.Obj("type", "item", "jobId", rec.JobId, "stream", "stdout", "line", line));
                            count++; got = true;
                        }
                    if (fe != null)
                        foreach (var line in fe.Poll(maxBytes))
                        {
                            JobStore.EmitFrame(Json.Obj("type", "item", "jobId", rec.JobId, "stream", "stderr", "line", line));
                            count++; got = true;
                        }
                    if (!alive)
                    {
                        idleRounds++;
                        if (!got && idleRounds >= 2) break;   // 任务结束后再排空两轮
                    }
                    else idleRounds = 0;
                    Thread.Sleep(400);
                }
            }
            finally
            {
                if (fo != null) fo.Dispose();
                if (fe != null) fe.Dispose();
            }

            var final = JobStore.LoadFromDir(rec.JobId, rec.Dir);
            JobStore.EmitFrame(Json.Obj("type", "summary", "ok", true, "jobId", rec.JobId,
                "state", final.State, "observedState", final.ObservedState, "exitCode", final.ExitCode,
                "count", count, "elapsedMs", final.ElapsedMs));
            return ctx.Cancel.IsCancellationRequested ? ExitCodes.Timeout : ExitCodes.Ok;
        }

        static List<string> ReadInitial(string file, int tail, DateTime? since, long sinceOffset, long maxBytes)
        {
            if (since.HasValue)
            {
                long newOff;
                return JobStore.ReadLinesFrom(file, sinceOffset, maxBytes, out newOff);
            }
            long from, total;
            bool trunc;
            return JobStore.ReadTailLines(file, tail <= 0 ? 100 : tail, maxBytes, out from, out total, out trunc);
        }

        // ================================================================ kill
        static int RunKill(Ctx ctx)
        {
            var rec = RequireJob(ctx);
            string action = "结束后台任务 " + rec.JobId + "（" + rec.CommandLine + "）";
            bool go = ctx.ConfirmDestructive(action);   // --dry-run → false；无 --yes → 抛 E_NEEDS_CONFIRM

            var children = new List<int>();
            if (rec.TargetPid > 0) children.AddRange(JobStore.ProcessTree(rec.TargetPid));

            if (!go)
            {
                ctx.Out.Result("job.kill", Json.Obj(
                    "dryRun", true,
                    "jobId", rec.JobId,
                    "state", rec.State,
                    "observedState", rec.ObservedState,
                    "plan", Json.Obj(
                        "action", "kill-tree",
                        "helperPid", rec.HelperPid,
                        "targetPid", rec.TargetPid,
                        "descendants", children,
                        "killFile", rec.KillPath,
                        "reason", ctx.Get("reason")),
                    "items", new List<object> { rec.ToJson() },
                    "count", 1,
                    "columns", new[] { "jobId", "state", "targetPid" }));
                return ExitCodes.Ok;
            }

            if (rec.ObservedState != "running")
            {
                ctx.Out.Result("job.kill", Json.Obj(
                    "jobId", rec.JobId,
                    "alreadyEnded", true,
                    "state", rec.State,
                    "observedState", rec.ObservedState,
                    "exitCode", rec.ExitCode,
                    "killed", new List<object>(),
                    "note", "任务已经结束，未做任何操作",
                    "items", new List<object> { rec.ToJson() },
                    "count", 1,
                    "columns", new[] { "jobId", "state", "targetPid" }));
                return ExitCodes.Ok;
            }

            // 杀死前先立牌：执行器看到 kill.json 会把状态写成 killed（而不是 lost）
            JobStore.AtomicWrite(rec.KillPath, Json.Write(Json.Obj(
                "ts", DateTime.Now, "by", Environment.UserName,
                "byPid", Process.GetCurrentProcess().Id,
                "reason", ctx.Get("reason"))));

            var killed = new List<int>();
            if (rec.TargetPid > 0)
                killed.AddRange(JobStore.KillTree(rec.TargetPid, rec.TargetStart));

            // 等执行器自己收尾（它会把 status.json 写成 killed）
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 3000 && JobStore.IsAlive(rec.HelperPid))
            {
                var probe = JobStore.LoadFromDir(rec.JobId, rec.Dir);
                if (probe.IsFinal) break;
                Thread.Sleep(200);
            }
            if (JobStore.IsAlive(rec.HelperPid))
                killed.AddRange(JobStore.KillTree(rec.HelperPid, rec.HelperStart));

            Thread.Sleep(200);
            var after = JobStore.LoadFromDir(rec.JobId, rec.Dir);
            var uniq = killed.Distinct().ToList();
            ctx.Out.Result("job.kill", Json.Obj(
                "jobId", rec.JobId,
                "state", after.State,
                "observedState", after.ObservedState,
                "alive", after.Alive,
                "exitCode", after.ExitCode,
                "killed", uniq,
                "killedCount", uniq.Count,
                "killFile", rec.KillPath,
                "files", after.Files(),
                "items", new List<object> { after.ToJson() },
                "count", 1,
                "columns", new[] { "jobId", "state", "targetPid" }));
            return uniq.Count > 0 ? ExitCodes.Ok : ExitCodes.Partial;
        }

        // ================================================================ 公共小工具
        /// <summary>
        /// 取任务 id：`--id <jobId>` 与位置参数 `<jobId>` 都支持（job.status / job.output / job.kill 一致）。
        /// 任务不存在 → E_NOT_FOUND（exit 3），不是用法错误。
        /// </summary>
        public static JobRecord RequireJob(Ctx ctx)
        {
            string id = ctx.Get("id");
            if (string.IsNullOrEmpty(id) && ctx.Positional.Length > 0) id = ctx.Positional[0];
            if (string.IsNullOrEmpty(id))
                throw ToolException.Usage("缺少任务 id", "写法：job status <jobId> 或 job status --id <jobId>；先用 job list 查看");
            return JobStore.Load(id);
        }
    }
}

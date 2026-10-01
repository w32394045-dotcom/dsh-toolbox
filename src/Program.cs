using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DshToolbox.Commands;
using DshToolbox.Core;

namespace DshToolbox
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 双模分派：无参数且独占控制台（双击启动）→ GUI；带参数 → CLI（见 GuiHost 注释）
            try { if (DshToolbox.Gui.GuiHost.WantsGui(args)) return DshToolbox.Gui.GuiHost.Run(args); } catch { }

            // 内部选项：把本次 stdout 落盘（供提权子进程把结果回传给父进程）
            string captureOut = ExtractOption(args, "--capture-out");
            if (!string.IsNullOrEmpty(captureOut))
            {
                var capture = new StringWriter();
                Console.SetOut(capture);
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { File.WriteAllText(captureOut, capture.ToString(), new UTF8Encoding(false)); } catch { }
                };
            }

            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            try { Console.InputEncoding = new UTF8Encoding(false); } catch { }

            // 语言与数据目录必须在命令注册之前定下来（命令摘要文本在注册时取值）
            try
            {
                Paths.Init(ExtractOption(args, "--home"));
                Paths.Ensure();
                Settings.Load();
                L.Use(ExtractOption(args, "--lang"));
            }
            catch { }

            RegisterAll();

            string[] argv = args ?? new string[0];
            var watch = Stopwatch.StartNew();
            ArgMap parsed;
            try { parsed = ArgMap.Parse(argv); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(L.T("参数解析失败: ","Argument parse failed: ") + ex.Message);
                return ExitCodes.Usage;
            }

            bool jsonMode = parsed.Flag("json");
            bool jsonlMode = parsed.Flag("jsonl");
            var output = new Output
            {
                JsonMode = jsonMode,
                JsonLines = jsonlMode,
                Quiet = parsed.Flag("quiet")
            };

            string cmdName = ResolveCommandName(parsed);

            // ---------------- 内建：version / help ----------------
            if (parsed.Flag("version") || string.Equals(cmdName, "version", StringComparison.OrdinalIgnoreCase))
            {
                output.Result("version", Json.Obj("name", ToolInfo.Name, "version", ToolInfo.Version, "protocol", ToolInfo.Protocol,
                                                  "exe", ExePath(), "framework", Environment.Version.ToString()));
                return ExitCodes.Ok;
            }
            if (string.Equals(cmdName, "help", StringComparison.OrdinalIgnoreCase) || parsed.Flag("help") || argv.Length == 0)
            {
                string topic = parsed.Positional.Count > 1 ? parsed.Positional[1] : null;
                return PrintHelp(output, topic);
            }

            // ---------------- 环境初始化 ----------------
            var ctx = new Ctx();
            ctx.Args = parsed;
            ctx.Out = output;
            ctx.ExePath = ExePath();
            ctx.Verbose = parsed.Flag("verbose");
            ctx.DryRun = parsed.Flag("dry-run") || parsed.Flag("dryrun");
            ctx.Yes = parsed.Flag("yes");
            ctx.NoLog = parsed.Flag("no-log");
            ctx.Cwd = Fs.Expand(parsed.Get("cwd") ?? Environment.CurrentDirectory, Environment.CurrentDirectory);
            if (!Directory.Exists(ctx.Cwd)) ctx.Cwd = Environment.CurrentDirectory;

            Paths.Init(parsed.Get("home"));
            Paths.Ensure();
            ctx.Home = Paths.Home;

            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Process.GetCurrentProcess().Id;
            try { Environment.SetEnvironmentVariable("DSH_TOOLBOX_RUN_ID", runId); } catch { }
            try { Environment.SetEnvironmentVariable("DSH_TOOLBOX_HOME", Paths.Home); } catch { }

            Log log = Log.Null;
            if (!ctx.NoLog)
            {
                string logFile = Path.Combine(Paths.Logs, "toolbox-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");
                log = new Log(logFile, ctx.Verbose);
            }
            ctx.Log = log;

            var cts = new CancellationTokenSource();
            ctx.Cancel = cts.Token;
            TimeSpan timeout;
            string timeoutArg = parsed.Get("timeout");
            if (!string.IsNullOrEmpty(timeoutArg) && ArgMap.TryParseDuration(timeoutArg, out timeout))
                cts.CancelAfter(timeout);
            bool userCancelled = false;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; userCancelled = true; try { cts.Cancel(); } catch { } };

            var ci = Registry.Find(cmdName);
            if (ci == null)
            {
                string msg = string.IsNullOrEmpty(cmdName)
                    ? L.T("缺少命令", "No command given")
                    : L.T(L.T("未知命令：","Unknown command: "), "Unknown command: ") + cmdName;
                output.Fail(cmdName, "E_USAGE", msg, L.T(L.T("运行 dsh-toolbox help 查看全部命令","Run dsh-toolbox help to list all commands"), "Run dsh-toolbox help to list all commands"), ExitCodes.Usage);
                if (!jsonMode && !jsonlMode)
                {
                    Console.Error.WriteLine();
                    PrintHelp(new Output(), null, true);
                }
                return ExitCodes.Usage;
            }

            ctx.CmdName = ci.Name;

            // 把命令词从位置参数中移除，命令内部只看到真正的参数
            int cmdTokens = 1;
            if (parsed.Positional.Count >= 2 &&
                string.Equals(parsed.Positional[0] + "." + parsed.Positional[1], cmdName, StringComparison.OrdinalIgnoreCase))
                cmdTokens = 2;
            if (parsed.Positional.Count >= cmdTokens) parsed.Positional.RemoveRange(0, cmdTokens);
            int exit = ExitCodes.Ok;
            var cmdWatch = Stopwatch.StartNew();
            log.Info("command.start", "cmd", ci.Name, "runId", runId, "argv", argv);

            try
            {
                exit = ci.Run(ctx);
            }
            catch (ToolException te)
            {
                output.Fail(ci.Name, te.Code, te.Message, te.Hint, te.Exit);
                exit = te.Exit;
                log.Error("command.error", "cmd", ci.Name, "code", te.Code, "message", te.Message);
            }
            catch (OperationCanceledException)
            {
                // 区分用户 Ctrl+C 与 --timeout 到期（契约 §4：130 取消 / 5 超时）
                int cc = userCancelled ? ExitCodes.Cancelled : ExitCodes.Timeout;
                output.Fail(ci.Name, userCancelled ? "E_CANCELLED" : "E_TIMEOUT",
                    userCancelled ? L.T("操作被用户取消（Ctrl+C）","Cancelled by the user (Ctrl+C)") : L.T("操作超时","Timed out"),
                    userCancelled ? null : L.T("可用 --timeout 调整上限","use --timeout to raise the limit"), cc);
                exit = cc;
            }
            catch (Exception ex)
            {
                string detail = ctx.Verbose ? ex.ToString() : (ex.GetType().Name + ": " + ex.Message);
                output.Fail(ci.Name, "E_INTERNAL", detail, L.T("加 --verbose 查看堆栈","add --verbose for the stack trace"), ExitCodes.Error);
                exit = ExitCodes.Error;
                log.Error("command.internal", "cmd", ci.Name, "message", ex.Message, "type", ex.GetType().FullName);
            }
            finally
            {
                cmdWatch.Stop();
                try { AppendRun(ctx, argv, ci.Name, exit, cmdWatch.ElapsedMilliseconds, runId); } catch { }
                log.Info("command.end", "cmd", ci.Name, "exit", exit, "ms", cmdWatch.ElapsedMilliseconds);
                log.Dispose();
                cts.Dispose();
            }

            return exit;
        }

        /// <summary>从原始 argv 里抠出 --k v / --k=v 形式的值（用于必须在早期生效的选项）。</summary>
        static string ExtractOption(string[] argv, string name)
        {
            if (argv == null) return null;
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == name && i + 1 < argv.Length) return argv[i + 1];
                if (argv[i] != null && argv[i].StartsWith(name + "=", StringComparison.Ordinal)) return argv[i].Substring(name.Length + 1);
            }
            return null;
        }

        static string ResolveCommandName(ArgMap m)
        {
            var pos = m.Positional;
            if (pos.Count == 0) return "";
            if (pos.Count >= 2)
            {
                string two = pos[0] + "." + pos[1];
                if (Registry.Find(two) != null) return two;
            }
            if (Registry.Find(pos[0]) != null) return pos[0];
            return pos[0];
        }

        static string ExePath()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null && !string.IsNullOrEmpty(asm.Location)) return asm.Location;
            }
            catch { }
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        static void AppendRun(Ctx ctx, string[] argv, string cmd, int exit, long ms, string runId)
        {
            if (ctx.NoLog) return;
            try
            {
                var rec = new JsonObject()
                    .Add("runId", runId)
                    .Add("ts", DateTime.Now)
                    .Add("cmd", cmd)
                    .Add("argv", argv)
                    .Add("exit", exit)
                    .Add("ms", ms)
                    .Add("cwd", ctx.Cwd)
                    .Add("user", Environment.UserName)
                    .Add("dryRun", ctx.DryRun)
                    .Add("pid", Process.GetCurrentProcess().Id);
                File.AppendAllText(Path.Combine(Paths.Runs, "runs.jsonl"), Json.Write(rec) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        static int PrintHelp(Output output, string topic, bool toStderr = false)
        {
            if (!string.IsNullOrEmpty(topic))
            {
                var ci = Registry.Find(topic);
                if (ci == null)
                {
                    output.Fail("help", "E_NOT_FOUND", L.T("未知命令：","Unknown command: ") + topic, L.T("运行 dsh-toolbox help 查看全部命令","Run dsh-toolbox help to list all commands"), ExitCodes.NotFound);
                    return ExitCodes.NotFound;
                }
                output.Result("help", Json.Obj(
                    "name", ci.Name, "group", ci.Group, "action", ci.Action,
                    "summary", ci.Summary, "usage", ci.Usage,
                    "aliases", ci.Aliases, "examples", ci.Examples,
                    "globalOptions", GlobalOptionsHelp()));
                return ExitCodes.Ok;
            }

            if (output.JsonMode || output.JsonLines)
            {
                var cmds = new List<object>();
                foreach (var ci in Registry.All())
                    cmds.Add(Json.Obj("name", ci.Name, "summary", ci.Summary, "usage", ci.Usage));
                output.Result("help", Json.Obj("usage", L.T("dsh-toolbox <命令> [选项]","dsh-toolbox <command> [options]"), "commands", cmds, "globalOptions", GlobalOptionsHelp()));
                return ExitCodes.Ok;
            }

            var sb = new StringBuilder();
            sb.AppendLine(ToolInfo.Name + " " + ToolInfo.Version + "  " + L.T("—— DSH 工具箱（Windows 单文件 CLI）", "— DSH toolbox (single-file Windows CLI)"));
            sb.AppendLine();
            sb.AppendLine(L.T("用法: dsh-toolbox <命令> [选项]", "Usage: dsh-toolbox <command> [options]"));
            sb.AppendLine(L.T("      dsh-toolbox <组> <动作> [选项]", "       dsh-toolbox <group> <action> [options]"));
            sb.AppendLine(L.T("      dsh-toolbox help <命令>", "       dsh-toolbox help <command>"));
            sb.AppendLine();
            foreach (var g in Registry.Grouped())
            {
                sb.AppendLine("[" + g.Key + "]");
                foreach (var c in g)
                    sb.AppendLine("  " + c.Name.PadRight(20) + c.Summary);
                sb.AppendLine();
            }
            sb.AppendLine(L.T("全局选项:", "Global options:"));
            foreach (var o in GlobalOptionsHelp()) sb.AppendLine("  " + o);
            sb.AppendLine();
            sb.AppendLine(L.T("机器可读清单: dsh-toolbox manifest --json", "Machine-readable catalog: dsh-toolbox manifest --json"));
            (toStderr ? Console.Error : Console.Out).Write(sb.ToString());
            return ExitCodes.Ok;
        }

        static string[] GlobalOptionsHelp()
        {
            return new[]
            {
                L.T("--json                 stdout 输出单个 JSON 信封", "--json                 emit a single JSON envelope on stdout"),
                L.T("--jsonl                流式 JSON（每行一条）", "--jsonl                streaming JSON, one object per line"),
                L.T("--quiet                抑制 stderr 进度信息", "--quiet                suppress progress on stderr"),
                L.T("--dry-run              破坏性操作只预览，零副作用", "--dry-run              preview destructive actions with zero side effects"),
                L.T("--yes                  确认执行破坏性操作", "--yes                  confirm destructive actions"),
                L.T("--timeout <30s|5m>     全局超时", "--timeout <30s|5m>     global timeout"),
                L.T("--home <dir>           数据目录（默认 %LOCALAPPDATA%\\dsh-toolbox）", "--home <dir>           data home (default %LOCALAPPDATA%\\dsh-toolbox)"),
                L.T("--cwd <dir>            相对路径基准目录", "--cwd <dir>            base directory for relative paths"),
                L.T("--lang <zh-CN|en-US>   界面与输出语言（也支持环境变量 DSH_TOOLBOX_LANG）", "--lang <zh-CN|en-US>   UI and output language (also DSH_TOOLBOX_LANG)"),
                L.T("--verbose              诊断信息", "--verbose              diagnostic output"),
                L.T("--no-log               本次不写运行记录", "--no-log               do not write a run record")
            };
        }

        /// <summary>
        /// 命令模块接入点。新增模块：在 Commands 下建类并提供 public static void Register()，
        /// 然后在此处加一行调用（见 docs/ARCHITECTURE.md §5）。
        /// </summary>
        static void RegisterAll()
        {
            CoreCommands.Register();
            HostCommands.Register();
            InstallCommands.Register();
            ConfigCommands.Register();
            ScanCommands.Register();
            HashCommands.Register();
            ProcCommands.Register();
            SysCommands.Register();
            NetCommands.Register();
            SignCommands.Register();
            JobCommands.Register();
            LogCommands.Register();
            ServeCommand.Register();
        }
    }
}

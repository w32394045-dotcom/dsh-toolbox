using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DshToolbox.Core
{
    public sealed class ProcResult
    {
        public int Exit;
        public string Stdout = "";
        public string Stderr = "";
        public bool TimedOut;
        /// <summary>输出里出现"在等输入"的迹象（或超时且无输出）——自动化里这是必须显式处理的失败。</summary>
        public bool PromptSuspected;
        public string PromptHint = "";
        public long ElapsedMs;

        public bool Ok { get { return Exit == 0 && !TimedOut && !PromptSuspected; } }
        public string Combined { get { return (Stdout ?? "") + (Stderr ?? ""); } }

        public string Tail(int n)
        {
            string s = (Stdout ?? "").Trim();
            if (s.Length == 0) s = (Stderr ?? "").Trim();
            if (s.Length <= n) return s;
            return "…" + s.Substring(s.Length - n);
        }
    }

    /// <summary>
    /// 非交互执行器：安装依赖时最容易"卡死"的地方。
    ///
    /// 做三件事：
    ///   1) **关掉子进程的 stdin**（立刻 EOF）——任何想读输入的提示会立刻失败，而不是把自动化挂住；
    ///   2) 按工具注入"免交互"环境变量（npm/git/powershell/winget/msi/nsis 各有各的坑）；
    ///   3) 识别"疑似在等输入"（提示串或超时无输出）并标记，调用方可据此**换非交互参数重试**。
    ///
    /// 设计原则：宁可明确失败，也不假装成功、更不允许静默挂起。
    /// </summary>
    public static class Proc
    {
        // ------------------------------------------------------------ 免交互环境变量
        static readonly Dictionary<string, Dictionary<string, string>> ToolEnv =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "npm", new Dictionary<string, string>
                {
                    { "npm_config_yes", "true" },              // 自动回答 "Ok to proceed?" 之类
                    { "npm_config_fund", "false" },
                    { "npm_config_audit", "false" },
                    { "npm_config_progress", "false" },
                    { "npm_config_loglevel", "error" },
                    { "npm_config_update_notifier", "false" },
                    { "npm_config_color", "false" },
                    { "npm_config_unicode", "false" },
                    { "NO_UPDATE_NOTIFIER", "1" },
                    { "CI", "1" },                              // 多数 CLI 见到 CI 就不交互
                    { "TERM", "dumb" }
                }
            },
            { "node", new Dictionary<string, string> { { "NO_UPDATE_NOTIFIER", "1" }, { "CI", "1" } } },
            { "git", new Dictionary<string, string>
                {
                    { "GIT_TERMINAL_PROMPT", "0" },             // 绝不停下来要用户名/密码
                    { "GIT_ASKPASS", "echo" },
                    { "SSH_ASKPASS", "echo" },
                    { "GIT_SSH_COMMAND", "ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new" },
                    { "GIT_CONFIG_PARAMETERS", "'core.askPass='" },
                    { "GCM_INTERACTIVE", "never" }
                }
            },
            { "winget", new Dictionary<string, string>
                {
                    { "WINGET_DISABLE_INTERACTIVITY", "1" },
                    { "WINGET_ACCEPT_PACKAGE_AGREEMENTS", "1" },
                    { "WINGET_ACCEPT_SOURCE_AGREEMENTS", "1" },
                    { "WINGET_DISABLE_UPDATE_NOTIFICATION", "1" }
                }
            },
            { "powershell", new Dictionary<string, string>
                {
                    { "POWERSHELL_TELEMETRY_OPTOUT", "1" },
                    { "POWERSHELL_UPDATECHECK", "Off" }
                }
            },
            { "msi", new Dictionary<string, string> { { "MsiExecCmdLineOptions", "" } } }
        };

        /// <summary>把某工具的免交互环境变量写进 ProcessStartInfo（不覆盖调用方已显式设置的）。</summary>
        public static void HardenEnv(ProcessStartInfo psi, string toolHint)
        {
            if (psi == null || string.IsNullOrEmpty(toolHint)) return;
            Dictionary<string, string> env;
            if (!ToolEnv.TryGetValue(toolHint, out env)) return;
            foreach (var kv in env)
            {
                try { if (!psi.EnvironmentVariables.ContainsKey(kv.Key)) psi.EnvironmentVariables[kv.Key] = kv.Value; }
                catch { }
            }
        }

        /// <summary>各依赖的推荐非交互调用方式（供文档与 --json 输出展示）。</summary>
        public static Dictionary<string, object> NonInteractiveRecipe(string tool)
        {
            switch ((tool ?? "").ToLowerInvariant())
            {
                case "node":
                    return Json.Obj("tool", "node",
                        "approach", "官方 zip 直接解压（不走 MSI/EXE 安装器，因此没有任何 UI 与协议页），解压到用户目录，不需要管理员",
                        "english", "official zip extracted into the user directory — no MSI/EXE installer, so no UI, no license page, no admin",
                        "flags", new[] { "--file <zip>（离线时）", "SHASUMS256.txt 校验" });
                case "npm":
                    return Json.Obj("tool", "npm",
                        "approach", "全部走 CLI 参数 + 环境变量：--yes --no-fund --no-audit --loglevel=error，并置 npm_config_yes/CI=1",
                        "english", "CLI flags plus environment: --yes --no-fund --no-audit --loglevel=error, npm_config_yes/CI=1",
                        "flags", new[] { "-g", "--prefix <用户目录>", "--yes", "--no-fund", "--no-audit", "--registry <url>" });
                case "git":
                    return Json.Obj("tool", "git",
                        "approach", "禁止任何凭据交互：GIT_TERMINAL_PROMPT=0、GIT_ASKPASS=echo、ssh BatchMode=yes；可选依赖",
                        "english", "no credential prompts: GIT_TERMINAL_PROMPT=0, GIT_ASKPASS=echo, ssh BatchMode=yes; optional",
                        "flags", new[] { "clone --depth 1", "-c core.askPass=", "-c advice.detachedHead=false" });
                case "winget":
                    return Json.Obj("tool", "winget",
                        "approach", "本工具不依赖微软商店/winget；若你主动要求用它装 Node，也会带上静默与协议接受参数",
                        "english", "not required at all; if you explicitly ask to use it, silent + agreement flags are added",
                        "flags", new[] { "install --silent --accept-package-agreements --accept-source-agreements --disable-interactivity" });
                case "msi":
                    return Json.Obj("tool", "msi", "approach", "msiexec /qn /norestart（无 UI、不重启）",
                        "english", "msiexec /qn /norestart (no UI, no restart)", "flags", new[] { "/qn", "/norestart", "/L*v <log>" });
                case "nsis":
                    return Json.Obj("tool", "nsis", "approach", "NSIS 安装器 /S 静默 + /D= 指定目录",
                        "english", "NSIS installer /S silent plus /D= target dir", "flags", new[] { "/S", "/D=<dir>" });
                default:
                    return Json.Obj("tool", tool);
            }
        }

        // ------------------------------------------------------------ 提示识别
        static readonly Regex PromptRx = new Regex(
            @"(?i)(\[y/n\]|\[y/N\]|\(y/n\)|\(yes/no\)|\[yes/no\]|ok to proceed|are you sure|do you want|would you like|press any key|" +
            @"enter password|password:|username for|proceed\?|continue\?|overwrite\?|replace\?|already exists|" +
            @"请按任意键|是否继续|确认|请输入|按 y|按任意键|是否删除|是否覆盖)",
            RegexOptions.Compiled);

        public static bool LooksLikePrompt(string text, out string hint)
        {
            hint = "";
            if (string.IsNullOrEmpty(text)) return false;
            var m = PromptRx.Match(text);
            if (m.Success) { hint = m.Value; return true; }
            return false;
        }

        // ------------------------------------------------------------ 执行
        /// <summary>
        /// 非交互执行：stdin 立刻关闭（EOF），按工具注入免交互环境变量，超时可杀，
        /// 输出里若出现提示串或"超时且几乎无输出"则标记 PromptSuspected。
        /// </summary>
        public static ProcResult Run(string file, string args, int timeoutMs,
                                     string toolHint = null, string cwd = null, string stdinText = null,
                                     CancellationToken ct = default(CancellationToken))
        {
            var r = new ProcResult();
            var sw = Stopwatch.StartNew();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
                HardenEnv(psi, toolHint);

                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    try
                    {
                        if (!string.IsNullOrEmpty(stdinText)) p.StandardInput.Write(stdinText);
                        p.StandardInput.Close();       // ← 关键：让任何交互提示立刻拿到 EOF
                    }
                    catch { }

                    bool exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        r.TimedOut = true;
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }
                    else
                    {
                        try { p.WaitForExit(); } catch { }   // 等异步读取收尾
                        try { r.Exit = p.ExitCode; } catch { r.Exit = -1; }
                    }

                    if (ct.IsCancellationRequested) r.TimedOut = true;
                }
            }
            catch (Exception ex)
            {
                r.Stderr = (r.Stderr ?? "") + ex.Message;
                r.Exit = -1;
            }
            sw.Stop();
            lock (stdout) r.Stdout = stdout.ToString();
            lock (stderr) r.Stderr = stderr.ToString();
            r.ElapsedMs = sw.ElapsedMilliseconds;

            string hint;
            if (LooksLikePrompt(r.Combined, out hint)) { r.PromptSuspected = true; r.PromptHint = hint; }
            else if (r.TimedOut && r.Combined.Trim().Length < 64) { r.PromptSuspected = true; r.PromptHint = L.T("超时且几乎没有输出（很可能在等输入）", "timed out with almost no output (likely waiting for input)"); }

            return r;
        }

        /// <summary>
        /// 带"疑似交互 → 换参数重试"的执行：第一次原样跑，若判定为在等输入，则用调用方给的
        /// 非交互参数再跑一次，并把两次结果都记下来（便于用户看清到底发生了什么）。
        /// </summary>
        public static ProcResult RunHardened(string file, string args, int timeoutMs, string toolHint,
                                             string nonInteractiveArgs, string cwd,
                                             out Dictionary<string, object> trace)
        {
            var first = Run(file, args, timeoutMs, toolHint, cwd);
            trace = Json.Obj(
                "attempt1", Json.Obj("args", args, "exit", first.Exit, "timedOut", first.TimedOut,
                                     "promptSuspected", first.PromptSuspected, "hint", first.PromptHint),
                "retried", false);

            if (!first.PromptSuspected || string.IsNullOrEmpty(nonInteractiveArgs)) return first;

            string args2 = args + " " + nonInteractiveArgs;
            var second = Run(file, args2, timeoutMs, toolHint, cwd);
            trace = Json.Obj(
                "attempt1", Json.Obj("args", args, "exit", first.Exit, "timedOut", first.TimedOut,
                                     "promptSuspected", true, "hint", first.PromptHint),
                "retried", true,
                "attempt2", Json.Obj("args", args2, "exit", second.Exit, "timedOut", second.TimedOut,
                                     "promptSuspected", second.PromptSuspected, "hint", second.PromptHint));
            return second;
        }
    }
}

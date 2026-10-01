using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DshToolbox.Gui
{
    internal enum TermKind { Plain, Input, Ok, Warn, Err, Dim, Info }

    internal sealed class TermLine
    {
        public string Text;
        public TermKind Kind;
        public TermLine(string text, TermKind kind) { Text = text; Kind = kind; }
    }

    /// <summary>
    /// 终端会话：一个常驻 PowerShell 子进程，命令走 stdin、输出异步读回。
    ///
    /// 为什么不自己实现 PTY：Windows ConPTY 在 .NET Framework 下要手写大量 P/Invoke，
    /// 而这里的目标是"能跑命令、看得舒服"，不是完整终端仿真。因此用 PowerShell 的
    /// `-Command -`（从 stdin 读命令）即可获得接近交互的体验：cd 之类还能保持状态。
    ///
    /// 另外做了三件"美化"事：剥掉 ANSI 转义序列、按语义给行着色、把 PS 的啰嗦输出压掉。
    /// </summary>
    internal sealed class TerminalSession : IDisposable
    {
        Process _proc;
        readonly List<TermLine> _lines = new List<TermLine>();
        readonly object _gate = new object();
        bool _disposed;

        public event Action<TermLine> Line;
        public event Action Exited;

        public bool Running { get { try { return _proc != null && !_proc.HasExited; } catch { return false; } } }
        public List<TermLine> Lines { get { lock (_gate) return new List<TermLine>(_lines); } }

        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[a-zA-Z]|\x1B\][^\x07]*\x07|\x1B[()][A-Z0-9]|\r", RegexOptions.Compiled);

        public static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Ansi.Replace(s, "");
        }

        /// <summary>按语义猜测行类型，用于着色。</summary>
        public static TermKind Classify(string s)
        {
            if (string.IsNullOrEmpty(s)) return TermKind.Plain;
            string t = s.TrimStart();
            if (t.StartsWith("PS ") || t.StartsWith(">")) return TermKind.Input;
            if (Regex.IsMatch(t, @"(?i)(\berror\b|\bfailed\b|\bexception\b|✗|\bE_[A-Z_]+\b|not recognized|cannot find)")) return TermKind.Err;
            if (Regex.IsMatch(t, @"(?i)(\bwarn(ing)?\b|⚠|deprecat)")) return TermKind.Warn;
            if (Regex.IsMatch(t, @"(?i)(\bok\b|\bsuccess\b|✓|\bPASS\b|\bdone\b)")) return TermKind.Ok;
            if (Regex.IsMatch(t, @"(?i)(^#|^//|^--|npm notice|\bdebug\b)")) return TermKind.Dim;
            return TermKind.Plain;
        }

        public void Start(string cwd, int columns = 140)
        {
            if (Running) return;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd
            };
            // 让终端里的工具也别弹交互、别画进度条（美化 + 防止挂住）
            psi.EnvironmentVariables["TERM"] = "dumb";
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            psi.EnvironmentVariables["npm_config_progress"] = "false";
            psi.EnvironmentVariables["npm_config_fund"] = "false";
            psi.EnvironmentVariables["npm_config_audit"] = "false";
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) Push(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Push(e.Data, true); };
            _proc.Exited += (s, e) => { var h = Exited; if (h != null) h(); };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            Push("dsh-toolbox 终端 · PowerShell " + Environment.Version + " · 工作目录 " + psi.WorkingDirectory, TermKind.Info);
            Push("提示：命令走 CLI 通道；安装类命令默认要求管理员模式。", TermKind.Dim);
        }

        void Push(string text, bool isErr = false)
        {
            string s = Strip(text);
            var line = new TermLine(s, isErr && Classify(s) == TermKind.Plain ? TermKind.Warn : Classify(s));
            lock (_gate) { _lines.Add(line); if (_lines.Count > 4000) _lines.RemoveAt(0); }
            var h = Line; if (h != null) h(line);
        }

        void Push(string text, TermKind kind)
        {
            string s = Strip(text);
            var line = new TermLine(s, kind);
            lock (_gate) { _lines.Add(line); if (_lines.Count > 4000) _lines.RemoveAt(0); }
            var h = Line; if (h != null) h(line);
        }

        public void Echo(string command)
        {
            Push("PS> " + command, false);
            // 把自己回声的行标成输入色
            lock (_gate) { if (_lines.Count > 0) _lines[_lines.Count - 1].Kind = TermKind.Input; }
        }

        public void Send(string command)
        {
            if (!Running) return;
            try
            {
                _proc.StandardInput.Write(command + "\r\n");
                _proc.StandardInput.Flush();
            }
            catch (Exception ex) { Push("发送失败：" + ex.Message, true); }
        }

        public void Interrupt()
        {
            // 没有 PTY，做不到精确 Ctrl+C；退而求其次：重启会话
            Restart();
        }

        public void Restart()
        {
            string cwd = null;
            try { cwd = _proc != null ? _proc.StartInfo.WorkingDirectory : null; } catch { }
            Kill();
            Start(cwd);
        }

        void Kill()
        {
            try { if (_proc != null && !_proc.HasExited) { _proc.StandardInput.Close(); _proc.Kill(); } } catch { }
            try { if (_proc != null) _proc.Dispose(); } catch { }
            _proc = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Kill();
        }
    }
}

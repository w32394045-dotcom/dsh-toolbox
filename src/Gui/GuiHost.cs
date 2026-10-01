using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DshToolbox.Gui
{
    /// <summary>
    /// 双模宿主：同一个 exe 既做 GUI 又做 CLI。
    /// 判据（已在 Windows 10 17763 实测）：
    ///   * 有参数 → CLI；
    ///   * 无参数 + 本进程**独占**控制台（GetConsoleProcessList == 1，即被双击/ShellExecute 启动）→ GUI，并隐藏该控制台窗口；
    ///   * 无参数 + 控制台被共享（从已有终端运行）→ CLI（打印帮助），绝不吞掉用户终端；
    ///   * `--gui` / `--cli` 可强制。
    /// 之所以不做 winexe：实测 winexe 进程的 stdout 在 PowerShell 管道捕获下会丢失（cmd 重定向却正常），
    /// 会破坏 DSH 对 CLI 的输出契约。console 子系统 + 归零隐藏才是两全做法。
    /// </summary>
    public static class GuiHost
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessCount);
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();

        const int SW_HIDE = 0;

        public static bool WantsGui(string[] args)
        {
            if (args != null)
            {
                // --gui / --cli 在任意位置都生效（便于 --gui --page 3 这类组合）
                foreach (var raw in args)
                {
                    string a = (raw ?? "").Trim().ToLowerInvariant();
                    if (a == "--gui" || a == "gui") return true;
                    if (a == "--cli" || a == "cli") return false;
                }
                if (args.Length > 0) return false;
            }

            var buf = new uint[64];
            uint n = GetConsoleProcessList(buf, (uint)buf.Length);
            return n <= 1;   // 独占控制台 = 我们是控制台主人 = 双击启动
        }

        /// <summary>启动 GUI（会先把双击带来的控制台窗口藏掉）。</summary>
        public static int Run(string[] args)
        {
            IntPtr h = GetConsoleWindow();
            if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
            try { FreeConsole(); } catch { }   // 真正脱离，避免控制台窗口残留

            // 语言与主题必须在构造控件之前确定（字段初始化器里就会取值）
            try
            {
                DshToolbox.Core.Paths.Init(null);
                DshToolbox.Core.Paths.Ensure();
                DshToolbox.Core.Settings.Load();
                DshToolbox.Core.L.Use(null);
                Ui.ApplyTheme(DshToolbox.Core.Settings.EffectiveTheme());
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                // 分阶段记录，便于把DshToolbox.Core.L.T("界面没出来","window never appeared")这类问题定位到具体环节
                MainForm form;
                try { form = new MainForm(args ?? new string[0]); }
                catch (Exception ex) { LogError(DshToolbox.Core.L.T("构造主窗体失败","Failed to create the main window"), ex); throw; }
                try { Application.Run(form); }
                catch (Exception ex) { LogError(DshToolbox.Core.L.T("消息循环异常","Message loop crashed"), ex); throw; }
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    string detail = LogError(DshToolbox.Core.L.T("界面启动失败","Failed to start the UI"), ex);
                    MessageBox.Show("界面启动失败：\n" + ex.Message + "\n\n详细信息已写入：\n" + detail,
                        "dsh-toolbox", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }
        }

        /// <summary>把 GUI 异常写到 %LOCALAPPDATA%\dsh-toolbox\logs\gui-error.log，并返回日志路径。</summary>
        public static string LogError(string stage, Exception ex)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-toolbox", "logs");
            string file = Path.Combine(dir, "gui-error.log");
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(file,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + stage + Environment.NewLine +
                    ex.ToString() + Environment.NewLine + new string('-', 72) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
            return file;
        }

        public static bool IsElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}

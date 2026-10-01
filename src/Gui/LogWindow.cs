using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DshToolbox.Core;

namespace DshToolbox.Gui
{
    public sealed class LogEntry
    {
        public readonly string Text;
        public readonly Ui.LogTone Tone;
        public LogEntry(string text, Ui.LogTone tone) { Text = text; Tone = tone; }
    }

    /// <summary>独立的活动日志窗口：与主界面同一套视觉（无边框、圆角、同配色）。</summary>
    internal sealed class LogWindow : Form
    {
        readonly TitleBar _title = new TitleBar();
        readonly RichTextBox _box = new RichTextBox();
        readonly FlatButton _follow = new FlatButton();
        readonly FlatButton _clear = new FlatButton();
        readonly FlatButton _save = new FlatButton();
        readonly List<LogEntry> _entries;
        public bool Follow = true;

        public LogWindow(List<LogEntry> entries)
        {
            _entries = entries;
            Text = L.T("活动日志","Activity log");
            FormBorderStyle = FormBorderStyle.None;
            try { var ic = Icon.ExtractAssociatedIcon(Bridge.ExePath); if (ic != null) Icon = ic; } catch { }
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(920, 620);
            MinimumSize = new Size(560, 360);
            BackColor = Ui.Bg;
            Font = Ui.FBody;
            ShowInTaskbar = false;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _title.TitleText = L.T("活动日志","Activity log");
            _title.Subtitle = L.T("与 CLI 同源 · 可选中复制","Same source as the CLI · selectable");
            _title.ShowRefresh = false;
            _title.ShowMin = false;
            _title.CloseClick += (s, e) => Close();
            _title.MaxClick += (s, e) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            Controls.Add(_title);

            _follow.Text = L.T("跟随最新 ✓","Follow ✓");
            _follow.Style = FlatButton.Kind.Primary;
            _follow.Click += (s, e) =>
            {
                Follow = !Follow;
                _follow.Text = Follow ? L.T("跟随最新 ✓","Follow ✓") : L.T("跟随最新","Follow");
                _follow.Style = Follow ? FlatButton.Kind.Primary : FlatButton.Kind.Ghost;
                if (Follow) ScrollEnd();
            };
            Controls.Add(_follow);

            _clear.Text = L.T("清空","Clear");
            _clear.Style = FlatButton.Kind.Ghost;
            _clear.Click += (s, e) => { _entries.Clear(); _box.Clear(); };
            Controls.Add(_clear);

            _save.Text = L.T("导出…","Export…");
            _save.Style = FlatButton.Kind.Ghost;
            _save.Click += (s, e) => SaveAs();
            Controls.Add(_save);

            _box.BorderStyle = BorderStyle.None;
            _box.ReadOnly = true;
            _box.BackColor = Ui.Card;
            _box.Font = Ui.Mono(9.5f);
            _box.WordWrap = false;
            _box.ScrollBars = RichTextBoxScrollBars.Both;
            _box.DetectUrls = false;
            Controls.Add(_box);

            // 卡片底
            var card = new Panel { BackColor = Ui.Bg };
            Controls.Add(card);
            card.SendToBack();

            foreach (var e in _entries) Append(e, false);
            ScrollEnd();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Resize += (s, e) => { Layout2(); ApplyRegion(); };
            Shown += (s, e) => { Layout2(); ApplyRegion(); };
        }

        void Layout2()
        {
            _title.Bounds = new Rectangle(0, 0, ClientSize.Width, 46);
            int y = 46 + 14;
            _follow.Bounds = new Rectangle(18, y, 118, 30);
            _clear.Bounds = new Rectangle(144, y, 72, 30);
            _save.Bounds = new Rectangle(224, y, 88, 30);
            _box.Bounds = new Rectangle(14, y + 42, ClientSize.Width - 28, ClientSize.Height - (y + 42) - 14);
        }

        /// <summary>主题变化时刷新本窗口配色。</summary>
        public void ApplyTheme()
        {
            BackColor = Ui.Bg;
            _box.BackColor = Ui.Card;
            _box.ForeColor = Ui.Text;
            _title.BackColor = Ui.Card;
            _box.Clear();
            foreach (var e in _entries)
            {
                _box.SelectionStart = _box.TextLength;
                _box.SelectionColor = Ui.Tone(e.Tone);
                _box.AppendText(e.Text + Environment.NewLine);
            }
            ScrollEnd();
            Invalidate(true);
            foreach (Control c in Controls) c.Invalidate();
        }

        void ApplyRegion()
        {
            try
            {
                if (WindowState == FormWindowState.Maximized) { Region = null; return; }
                using (var p = Ui.Round(new Rectangle(0, 0, Width, Height), 12)) Region = new Region(p);
            }
            catch { }
        }

        public void Append(LogEntry e) { Append(e, true); }

        void Append(LogEntry e, bool scroll)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Append(e, scroll))); return; }
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
            _box.SelectionColor = Ui.Tone(e.Tone);
            _box.AppendText(e.Text + Environment.NewLine);
            if (scroll && Follow) ScrollEnd();
        }

        void ScrollEnd()
        {
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }

        void SaveAs()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = L.T("文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*","Text files (*.txt)|*.txt|All files (*.*)|*.*");
                dlg.FileName = "dsh-toolbox-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, _box.Text, new UTF8Encoding(false)); }
                catch (Exception ex) { MessageBox.Show(this, L.T("导出失败：","Export failed: ") + ex.Message, L.T("活动日志","Activity log"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && (int)m.Result == 1 && WindowState == FormWindowState.Normal)
                Chrome.ResizeHitTest(this, ref m);
        }
    }
}

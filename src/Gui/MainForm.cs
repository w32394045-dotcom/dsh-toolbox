using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DshToolbox.Core;

namespace DshToolbox.Gui
{
    /// <summary>无边框窗体的边缘缩放命中（只在客户区空白处生效，不抢子控件的点击）。</summary>
    internal static class Chrome
    {
        public const int Grip = 6;
        public static void ResizeHitTest(Form f, ref Message m)
        {
            var p = f.PointToClient(Cursor.Position);
            bool left = p.X <= Grip, right = p.X >= f.ClientSize.Width - Grip;
            bool top = p.Y <= Grip, bottom = p.Y >= f.ClientSize.Height - Grip;
            int r = 0;
            if (top && left) r = 13; else if (top && right) r = 14;
            else if (bottom && left) r = 16; else if (bottom && right) r = 17;
            else if (left) r = 10; else if (right) r = 11;
            else if (top) r = 12; else if (bottom) r = 15;
            if (r != 0) m.Result = (IntPtr)r;
        }
    }

    internal sealed class StackPage : Panel
    {
        public readonly List<Control> Cards = new List<Control>();
        bool _laying;
        public StackPage()
        {
            AutoScroll = true;
            BackColor = Ui.Bg;
            Padding = new Padding(26, 20, 26, 26);
        }
        public T Add<T>(T c) where T : Control { Cards.Add(c); Controls.Add(c); Relayout(); return c; }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        public void Relayout()
        {
            if (_laying) return;
            _laying = true;
            try
            {
                int avail = ClientSize.Width - Padding.Left - Padding.Right;
                if (VerticalScroll.Visible) avail -= SystemInformation.VerticalScrollBarWidth;
                if (avail < 260) avail = 260;
                int y = Padding.Top;
                foreach (var c in Cards) { c.Location = new Point(Padding.Left, y); c.Width = avail; y += c.Height + 14; }
            }
            finally { _laying = false; }
        }
    }

    internal sealed class BtnSpec
    {
        public string Text = "";
        public int Width = 120;
        public Action Click;
        public FlatButton.Kind Kind = FlatButton.Kind.Ghost;
        public bool Admin;
    }

    /// <summary>卡片：内部控件一律走流式布局，结构上不可能互相重叠。</summary>
    internal sealed class BodyCard : CardPanel
    {
        public int Pad = 18;
        public int Gap = 8;
        int _y = -1;
        readonly List<Action<BodyCard>> _layouts = new List<Action<BodyCard>>();

        public int BodyTop { get { return TitleHeight + 6; } }
        public int InnerW { get { return Math.Max(60, Width - Pad * 2); } }

        void Ensure() { if (_y < 0) _y = BodyTop; }

        void Reg(Control c, Action<BodyCard> layout)
        {
            Controls.Add(c);
            _layouts.Add(layout);
            try { layout(this); } catch { }
        }

        public T Row<T>(T c, int height) where T : Control
        {
            Ensure();
            int y = _y; _y += height + Gap;
            c.Height = height;
            Reg(c, card => c.Bounds = new Rectangle(card.Pad, y, card.InnerW, height));
            return c;
        }

        public void Two(Control left, Control right, int height, int gap)
        {
            Ensure();
            int y = _y; _y += height + Gap;
            Reg(left, card => { int w = (card.InnerW - gap) / 2; left.Bounds = new Rectangle(card.Pad, y, w, height); });
            Reg(right, card => { int w = (card.InnerW - gap) / 2; right.Bounds = new Rectangle(card.Pad + w + gap, y, w, height); });
        }

        public void Buttons(int height, params BtnSpec[] specs)
        {
            Ensure();
            int y = _y; _y += height + Gap;
            int x = 0;
            foreach (var s in specs)
            {
                var spec = s;
                var b = new FlatButton { Text = spec.Admin ? (spec.Text + L.T("（需管理员）"," (admin)")) : spec.Text, Style = spec.Kind };
                if (spec.Click != null) b.Click += (o, e) => spec.Click();
                int bx = x, bw = spec.Width;
                Reg(b, card => b.Bounds = new Rectangle(card.Pad + bx, y, bw, height));
                x += bw + 8;
            }
        }

        /// <summary>三列信息行：左状态胶囊 + 中说明 + 右动作（可为 null）。</summary>
        public void InfoRow(Control pill, Control text, Control action, int height, int pillW, int actionW)
        {
            Ensure();
            int y = _y; _y += height + Gap;
            Reg(pill, card => pill.Bounds = new Rectangle(card.Pad, y + (height - 22) / 2, pillW, 22));
            int reserved = pillW + 12 + (actionW > 0 ? actionW + 12 : 0);
            Reg(text, card => text.Bounds = new Rectangle(card.Pad + pillW + 12, y, Math.Max(80, card.InnerW - reserved), height));
            if (action != null)
                Reg(action, card => action.Bounds = new Rectangle(card.Width - card.Pad - actionW, y + (height - 24) / 2, actionW, 24));
        }

        public T HeaderSlot<T>(T c, int width, int height, int rightOffset = 0) where T : Control
        {
            Reg(c, card => c.Bounds = new Rectangle(card.Width - card.Pad - rightOffset - width, 15, width, height));
            return c;
        }

        public FlatButton HeaderButton(string text, int width, Action click, FlatButton.Kind kind = FlatButton.Kind.Ghost, int rightOffset = 0)
        {
            var b = new FlatButton { Text = text, Style = kind };
            if (click != null) b.Click += (o, e) => click();
            return HeaderSlot(b, width, 30, rightOffset);
        }

        public int Finish(int extra = 0)
        {
            Ensure();
            Height = _y - Gap + Pad + extra;
            return Height;
        }

        public void ResetBody()
        {
            var kids = new List<Control>();
            foreach (Control c in Controls) kids.Add(c);
            foreach (var c in kids) { Controls.Remove(c); c.Dispose(); }
            _layouts.Clear();
            _y = BodyTop;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            foreach (var l in _layouts) { try { l(this); } catch { } }
        }
    }

    internal sealed class TitleBar : Control
    {
        public Rectangle BtnRefresh, BtnTheme, BtnMin, BtnMax, BtnClose;
        public int Hover = -1, Down = -1;
        public event EventHandler RefreshClick, ThemeClick, MinClick, MaxClick, CloseClick;
        public string Subtitle = "";
        public string TitleText = L.T(L.T("dsh-toolbox 工具箱","dsh-toolbox"), "dsh-toolbox");
        public bool ShowBrand = true, ShowRefresh = true, ShowTheme = true, ShowMin = true, ShowMax = true, ShowClose = true;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public TitleBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 46;
            BackColor = Ui.Card;
        }

        void Calc()
        {
            int w = 44, h = 30, y = (Height - h) / 2;
            int x = Width - w - 8;
            BtnClose = ShowClose ? new Rectangle(x, y, w, h) : Rectangle.Empty; x -= w + 2;
            BtnMax = ShowMax ? new Rectangle(x, y, w, h) : Rectangle.Empty; x -= w + 2;
            BtnMin = ShowMin ? new Rectangle(x, y, w, h) : Rectangle.Empty; x -= w + 2;
            BtnRefresh = ShowRefresh ? new Rectangle(x - 10, y, w, h) : Rectangle.Empty;
            BtnTheme = ShowTheme ? new Rectangle(BtnRefresh.IsEmpty ? x - 10 : BtnRefresh.X - w - 2, y, w, h) : Rectangle.Empty;
        }

        int Hit(Point p)
        {
            Calc();
            if (ShowClose && BtnClose.Contains(p)) return 3;
            if (ShowMax && BtnMax.Contains(p)) return 2;
            if (ShowMin && BtnMin.Contains(p)) return 1;
            if (ShowRefresh && BtnRefresh.Contains(p)) return 0;
            if (ShowTheme && BtnTheme.Contains(p)) return 4;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != Hover) { Hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { Hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Down = Hit(e.Location);
            Invalidate();
            if (Down < 0)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int up = Hit(e.Location);
            if (up == Down && up >= 0)
            {
                if (up == 3 && CloseClick != null) CloseClick(this, EventArgs.Empty);
                else if (up == 2 && MaxClick != null) MaxClick(this, EventArgs.Empty);
                else if (up == 1 && MinClick != null) MinClick(this, EventArgs.Empty);
                else if (up == 0 && RefreshClick != null) RefreshClick(this, EventArgs.Empty);
                else if (up == 4 && ThemeClick != null) ThemeClick(this, EventArgs.Empty);
            }
            Down = -1; Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (ShowMax && MaxClick != null) MaxClick(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Calc();
            g.Clear(Ui.Card);
            using (var p = new Pen(Ui.Border)) g.DrawLine(p, 0, Height - 1, Width - 1, Height - 1);

            int textX = 18;
            if (ShowBrand)
            {
                var mark = new Rectangle(18, (Height - 26) / 2, 26, 26);
                var appIcon = Ui.AppIcon;
                if (appIcon != null)
                {
                    // 与系统里同一个图标：界面内品牌位直接绘制它
                    var old = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(appIcon, mark);
                    g.InterpolationMode = old;
                }
                else
                {
                    using (var lg = new LinearGradientBrush(mark, Ui.Accent, Color.FromArgb(0x7A, 0x5A, 0xF0), 45f))
                    using (var path = Ui.Round(mark, 8)) g.FillPath(lg, path);
                    Ui.DrawText(g, "DSH", Ui.F(9f, FontStyle.Bold), Color.White, mark,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                textX = 56;
            }

            Ui.DrawText(g, TitleText, Ui.FBold, Ui.Text, new Rectangle(textX, 0, 300, Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(Subtitle))
                Ui.DrawText(g, Subtitle, Ui.FSmall, Ui.Muted, new Rectangle(textX + 190, 0, 440, Height),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            if (ShowRefresh) DrawBtn(g, BtnRefresh, 0, Hover == 0, Down == 0);
            if (ShowTheme) DrawBtn(g, BtnTheme, 4, Hover == 4, Down == 4);
            if (ShowMin) DrawBtn(g, BtnMin, 1, Hover == 1, Down == 1);
            if (ShowMax) DrawBtn(g, BtnMax, 2, Hover == 2, Down == 2);
            if (ShowClose) DrawBtn(g, BtnClose, 3, Hover == 3, Down == 3);
        }

        void DrawBtn(Graphics g, Rectangle r, int kind, bool hover, bool down)
        {
            if (r.IsEmpty) return;
            if (hover || down)
            {
                Color bg = kind == 3 ? Color.FromArgb(0xE8, 0x11, 0x23) : (down ? Color.FromArgb(0xE4, 0xE8, 0xEE) : Color.FromArgb(0xEF, 0xF2, 0xF6));
                Ui.FillRound(g, r, 7, bg);
            }
            Color fg = (kind == 3 && hover) ? Color.White : Ui.SubText;
            using (var pen = new Pen(fg, 1.4f))
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                switch (kind)
                {
                    case 0:
                        g.DrawArc(pen, cx - 6, cy - 6, 12, 12, 40, 260);
                        g.DrawLine(pen, cx + 3, cy - 7, cx + 6, cy - 3);
                        g.DrawLine(pen, cx + 3, cy - 7, cx - 1, cy - 6);
                        break;
                    case 1: g.DrawLine(pen, cx - 5, cy, cx + 5, cy); break;
                    case 2: g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10); break;
                    case 4:   // 主题按钮：浅色画月亮、深色画太阳
                        if (Ui.IsDark)
                        {
                            g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                            for (int k = 0; k < 8; k++)
                            {
                                double ang = k * Math.PI / 4;
                                g.DrawLine(pen, cx + (float)(Math.Cos(ang) * 6.5), cy + (float)(Math.Sin(ang) * 6.5),
                                                cx + (float)(Math.Cos(ang) * 9), cy + (float)(Math.Sin(ang) * 9));
                            }
                        }
                        else
                        {
                            using (var b1 = new SolidBrush(fg)) g.FillEllipse(b1, cx - 3, cy - 4, 10, 10);
                            using (var b2 = new SolidBrush(hover ? Ui.HoverSoft : Ui.Card)) g.FillEllipse(b2, cx - 6, cy - 7, 10, 10);
                        }
                        break;
                    default:
                        g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                        g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                        break;
                }
            }
        }
    }

    internal sealed class SideBar : Panel
    {
        public string Version = "";
        public bool Elevated;
        public event EventHandler ElevatedClick;
        bool _hoverPill;

        Rectangle PillRect() { return new Rectangle(18, Height - 74 + 22, Elevated ? 92 : 168, 22); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = PillRect().Contains(e.Location);
            if (over != _hoverPill) { _hoverPill = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { _hoverPill = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (PillRect().Contains(e.Location) && ElevatedClick != null) ElevatedClick(this, EventArgs.Empty);
            base.OnMouseUp(e);
        }
        public SideBar()
        {
            BackColor = Ui.Sidebar;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Sidebar);
            using (var p = new Pen(Ui.Border)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);
            Ui.DrawText(g, L.T("导航", "NAVIGATION"), Ui.FSmall, Ui.Muted, new Rectangle(22, 10, 120, 18),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // 中英宽度不同：放不下就不画右侧标签，避免与 NAVIGATION 叠字
            // 中英宽度不同：单行实测宽度 + 留足间距，放不下就干脆不画，避免叠字
            int navW = Ui.MeasureText(L.T("导航", "NAVIGATION"), Ui.FSmall).Width;
            int hostW = Ui.MeasureText(L.T("DSH 宿主与工具箱", "host & toolbox"), Ui.FSmall).Width;
            if (22 + navW + 20 + hostW + 22 <= Width)
                Ui.DrawText(g, L.T("DSH 宿主与工具箱", "host & toolbox"), Ui.FSmall, Ui.Muted, new Rectangle(0, 6, Width - 22, 18),
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int fy = Height - 74;
            using (var p = new Pen(Ui.Border)) g.DrawLine(p, 16, fy - 10, Width - 16, fy - 10);
            Ui.DrawText(g, "dsh-toolbox " + Version, Ui.FSmall, Ui.SubText, new Rectangle(18, fy, Width - 36, 18),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var pill = PillRect();
            Ui.FillRound(g, pill, 11, Elevated ? Ui.SuccessSoft : (_hoverPill ? Ui.AccentSoft : Ui.HoverSoft));
            Ui.DrawText(g, Elevated ? L.T("● 管理员", "● Admin") : L.T("● 普通用户 · 点击提权", "● Standard · click for admin"), Ui.FSmall, Elevated ? Ui.Success : (_hoverPill ? Ui.Accent : Ui.SubText), pill,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public class MainForm : Form
    {
        const int TitleH = 46, SidebarW = 224;

        readonly string[] _titles = { L.T(L.T("概览","Overview"),"Overview"), L.T(L.T("安装与升级","Install & upgrade"),"Install & upgrade"), L.T(L.T("维护刷新","Maintenance"),"Maintenance"), L.T(L.T("环境体检","Environment check"),"Environment check"), L.T(L.T("任务与日志","Jobs & logs"),"Jobs & logs"), L.T(L.T("关于","About"),"About") };
        readonly string[] _glyphs = { "◈", "⬇", "⟳", "✚", "≡", "ⓘ" };

        readonly TitleBar _title = new TitleBar();
        readonly SideBar _sidebar = new SideBar();
        readonly Panel _main = new Panel();
        readonly Panel _pageHost = new Panel();
        readonly StackPage[] _pages = new StackPage[6];
        readonly NavItem[] _navs = new NavItem[6];
        readonly List<Action> _refreshers = new List<Action>();
        readonly RichTextBox _log = new RichTextBox();
        readonly FlatProgress _progress = new FlatProgress();
        readonly Label _status = new Label();
        readonly List<LogEntry> _logEntries = new List<LogEntry>();
        readonly string[] _startArgs;
        LogWindow _logWindow;
        BodyCard _logCard;
        System.Windows.Forms.Timer _tick;
        int _current;
        bool _busy;

        public MainForm(string[] args)
        {
            Text = L.T("dsh-toolbox 工具箱","dsh-toolbox");
            FormBorderStyle = FormBorderStyle.None;
            try { var ic = Icon.ExtractAssociatedIcon(Bridge.ExePath); if (ic != null) Icon = ic; } catch { }   // 任务栏/Alt+Tab 图标
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1140, 760);
            MinimumSize = new Size(980, 660);
            BackColor = Ui.Bg;
            Font = Ui.FBody;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;   // 系统 DPI 缩放下按比例放大
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _startArgs = args ?? new string[0];
            BuildChrome();
            BuildPages();
            int startPage = 0;
            for (int i = 0; i + 1 < (args == null ? 0 : args.Length); i++)
                if (args[i] == "--page" || args[i] == "--gui-page") int.TryParse(args[i + 1], out startPage);
            if (startPage < 0 || startPage > 5) startPage = 0;
            Select(startPage);

            Shown += (s, e) => { LayoutChrome(); AfterShown(); };
            Resize += (s, e) => { ApplyRegion(); LayoutChrome(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshCurrent(); } };
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

        void BuildChrome()
        {
            _title.RefreshClick += (s, e) => RefreshCurrent();
            _title.MinClick += (s, e) => WindowState = FormWindowState.Minimized;
            _title.MaxClick += (s, e) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            _title.CloseClick += (s, e) => Close();
            _title.ThemeClick += (s, e) => ToggleTheme();
            _title.Subtitle = "v" + ToolInfo.Version + " · GUI + CLI";
            Ui.ApplyTheme(Settings.EffectiveTheme());
            Ui.ThemeChanged += (s, e) => ApplyThemeToUi();
            Controls.Add(_title);

            _sidebar.Version = ToolInfo.Version;
            _sidebar.ElevatedClick += (s, e) => RequestElevation();
            Controls.Add(_sidebar);

            _main.BackColor = Ui.Bg;
            Controls.Add(_main);
            _pageHost.BackColor = Ui.Bg;
            _main.Controls.Add(_pageHost);

            var logCard = new BodyCard();
            logCard.SetHeader(L.T(L.T("活动日志","Activity log"),"Activity log"), L.T(L.T("单击此处打开独立窗口（可选中、复制、导出）","Click to pop out a window (selectable, copyable, exportable)"),"Click to pop out a window (selectable, copyable, exportable)"));
            logCard.BackColor = Ui.Card;
            _main.Controls.Add(logCard);
            _logCard = logCard;
            logCard.HeaderButton(L.T("独立窗口", "Pop out"), 96, OpenLogWindow);

            _log.BorderStyle = BorderStyle.None;
            _log.ReadOnly = true;
            _log.BackColor = Ui.Card;
            _log.ForeColor = Ui.Text;
            _log.Font = Ui.Mono(9f);
            _log.WordWrap = false;
            _log.ScrollBars = RichTextBoxScrollBars.Vertical;
            _log.DetectUrls = false;
            _log.Cursor = Cursors.Hand;
            _log.Click += (s, e) => OpenLogWindow();
            logCard.Controls.Add(_log);
            logCard.Click += (s, e) => OpenLogWindow();

            logCard.Controls.Add(_progress);
            _status.AutoSize = false;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = Ui.Muted;
            _status.Font = Ui.FSmall;
            _status.BackColor = Color.Transparent;
            _status.Text = L.T("就绪", "Ready");
            logCard.Controls.Add(_status);
            logCard.SizeChanged += (s, e) => LayoutLogCard();

            _tick = new System.Windows.Forms.Timer { Interval = 1000 };
            _tick.Tick += (s, e) => { if (!_busy) _progress.Value = 0; };
            _tick.Start();
        }

        void LayoutLogCard()
        {
            if (_logCard == null) return;
            int top = _logCard.BodyTop;
            _log.Bounds = new Rectangle(_logCard.Pad, top, _logCard.InnerW, Math.Max(30, _logCard.Height - top - 34));
            _progress.Bounds = new Rectangle(_logCard.Pad, _logCard.Height - 28, _logCard.InnerW, 4);
            _status.Bounds = new Rectangle(_logCard.Pad, _logCard.Height - 22, _logCard.InnerW, 18);
        }

        void LayoutChrome()
        {
            _title.Bounds = new Rectangle(0, 0, ClientSize.Width, TitleH);
            _sidebar.Bounds = new Rectangle(0, TitleH, SidebarW, ClientSize.Height - TitleH);
            _main.Bounds = new Rectangle(SidebarW, TitleH, ClientSize.Width - SidebarW, ClientSize.Height - TitleH);
            int logH = Math.Max(104, Math.Min(150, ClientSize.Height / 5));
            _logCard.Bounds = new Rectangle(20, _main.ClientSize.Height - logH - 16, _main.ClientSize.Width - 40, logH);
            _pageHost.Bounds = new Rectangle(0, 0, _main.ClientSize.Width, _main.ClientSize.Height - logH - 24);
            LayoutLogCard();
        }

        void Select(int index)
        {
            _current = index;
            for (int i = 0; i < 6; i++)
            {
                _navs[i].Selected = i == index;
                _pages[i].Visible = i == index;
            }
            _title.Subtitle = "v" + ToolInfo.Version + L.T(" · "," · ") + _titles[index];
            _title.Invalidate();
            if (index < _refreshers.Count && _refreshers[index] != null && !_busy)
            {
                try { _refreshers[index](); } catch { }
            }
        }

        void RefreshCurrent()
        {
            if (_busy) { Log(L.T("正在执行上一个操作，请稍候…","Busy, please wait…"), Ui.Warn); return; }
            if (_current < _refreshers.Count && _refreshers[_current] != null) _refreshers[_current]();
        }

        void Log(string text, Color? color = null)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(text, color))); return; }
            var entry = new LogEntry("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text, ToneOf(color));
            _logEntries.Add(entry);
            if (_logEntries.Count > 5000) _logEntries.RemoveAt(0);

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = Ui.Tone(entry.Tone);
            _log.AppendText(entry.Text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();

            if (_logWindow != null && !_logWindow.IsDisposed) _logWindow.Append(entry);
        }

        /// <summary>把调用点传入的颜色映射成语义色调；换主题时历史日志也能正确重绘。</summary>
        static Ui.LogTone ToneOf(Color? c)
        {
            if (c == null) return Ui.LogTone.Info;
            var v = c.Value;
            if (v.ToArgb() == Ui.Accent.ToArgb()) return Ui.LogTone.Accent;
            if (v.ToArgb() == Ui.Success.ToArgb()) return Ui.LogTone.Success;
            if (v.ToArgb() == Ui.Warn.ToArgb()) return Ui.LogTone.Warn;
            if (v.ToArgb() == Ui.Danger.ToArgb()) return Ui.LogTone.Danger;
            if (v.ToArgb() == Ui.Muted.ToArgb()) return Ui.LogTone.Muted;
            if (v.ToArgb() == Ui.SubText.ToArgb()) return Ui.LogTone.Sub;
            return Ui.LogTone.Info;
        }

        void OpenLogWindow()
        {
            if (_logWindow != null && !_logWindow.IsDisposed) { _logWindow.Activate(); return; }
            _logWindow = new LogWindow(_logEntries);
            _logWindow.FormClosed += (s, e) => { _logWindow = null; };
            _logWindow.Show(this);
        }

        void SetStatus(string text, int progress = 0)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, progress))); return; }
            _status.Text = text;
            _progress.Value = progress;
        }

        void Run(string title, string cmdLine, Action<ToolResult> done = null)
        {
            if (_busy) { Log(L.T("上一个操作仍在执行，已忽略：","Busy; ignored: ") + title, Ui.Warn); return; }
            _busy = true;
            Log("▶ " + title, Ui.Accent);
            Log("  $ dsh-toolbox " + cmdLine, Ui.Muted);
            SetStatus(title + L.T(" 执行中…"," running…"), 30);
            var sw = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(delegate
            {
                ToolResult res;
                try { res = Bridge.Call(cmdLine); }
                catch (Exception ex) { res = new ToolResult { Exit = 1, Raw = ex.Message }; }
                sw.Stop();
                BeginInvoke(new Action(delegate
                {
                    _busy = false;
                    SetStatus((res.Ok ? L.T("完成","done") : L.T("失败","failed")) + L.T(" · "," · ") + title + L.T(" · "," · ") + sw.ElapsedMilliseconds + " ms", 0);
                    if (res.Ok)
                    {
                        Log("  ✓ " + title + L.T(" 完成（"," done (") + sw.ElapsedMilliseconds + " ms）", Ui.Success);
                        PrintData(res.Data);
                    }
                    else Log("  ✗ " + title + L.T(" 失败（exit="," failed (exit=") + res.Exit + "）：" + res.Error, Ui.Danger);
                    if (done != null) done(res);
                }));
            });
        }

        void PrintData(Dictionary<string, object> data)
        {
            if (data == null) return;
            int shown = 0;
            foreach (var kv in data)
            {
                if (kv.Key == "items" || kv.Key == "columns") continue;
                string v = Bridge.Compact(kv.Value);
                if (string.IsNullOrEmpty(v)) continue;
                if (v.Length > 150) v = v.Substring(0, 147) + "...";
                Log("      " + kv.Key + " = " + v, Ui.SubText);
                if (++shown >= 6) break;
            }
            if (data.ContainsKey("items"))
            {
                try
                {
                    var items = Json.ToList((System.Collections.IEnumerable)data["items"]);
                    int n = 0;
                    foreach (var it in items)
                    {
                        var d = it as Dictionary<string, object>;
                        if (d == null) continue;
                        var parts = new List<string>();
                        foreach (var kv in d) { parts.Add(kv.Key + "=" + Bridge.Compact(kv.Value)); if (parts.Count >= 4) break; }
                        Log("      · " + string.Join("  ", parts.ToArray()), Ui.Muted);
                        if (++n >= 5) { if (items.Count > 5) Log(L.T("      · …还有 ","      · …") + (items.Count - 5) + L.T(" 条"," more"), Ui.Muted); break; }
                    }
                }
                catch { }
            }
        }

        void AdminRun(string title, string cmdLine, string why, Action<ToolResult> done = null, bool stream = false)
        {
            if (_busy) return;
            if (!GuiHost.IsElevated())
            {
                var r = MessageBox.Show(this,
                    title + "\n\n" + why + "\n\n该操作需要管理员权限，继续将弹出 UAC 授权窗口。",
                    L.T("需要管理员权限","Administrator required"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK) { Log(L.T("已取消（需要管理员权限）：", "Cancelled (admin required): ") + title, Ui.Muted); return; }
                if (stream) RunStream(title + L.T("（提权）", " (elevated)"), "elevate.run -- " + cmdLine, done);
                else Run(title + L.T("（提权）", " (elevated)"), "elevate.run -- " + cmdLine, done);
                return;
            }
            if (stream) RunStream(title, cmdLine, done); else Run(title, cmdLine, done);
        }

        void BuildPages()
        {
            for (int i = 0; i < 6; i++)
            {
                var p = new StackPage();
                _pages[i] = p;
                _pageHost.Controls.Add(p);
                p.Dock = DockStyle.Fill;
                p.Visible = i == 0;

                var nav = new NavItem { Text = _titles[i], Glyph = _glyphs[i] };
                int idx = i;
                nav.Click += (s, e) => Select(idx);
                _sidebar.Controls.Add(nav);
                nav.Bounds = new Rectangle(12, 34 + i * 42, SidebarW - 24, 40);
                _navs[i] = nav;
            }
            while (_refreshers.Count < 6) _refreshers.Add(null);

            BuildOverview(_pages[0]);
            BuildInstall(_pages[1]);
            BuildMaint(_pages[2]);
            BuildCompat(_pages[3]);
            BuildLogs(_pages[4]);
            BuildAbout(_pages[5]);
        }

        BodyCard Card(StackPage page, string title, string sub)
        {
            var c = new BodyCard();
            c.SetHeader(title, sub);
            page.Add(c);
            return c;
        }

        static LabelValue Lv(string label, string value = "—", Color? color = null)
        {
            return new LabelValue { Label = label, Value = value, ValueColor = color ?? Ui.Text };
        }

        static BtnSpec Btn(string text, int width, Action click, FlatButton.Kind kind = FlatButton.Kind.Ghost, bool admin = false)
        {
            return new BtnSpec { Text = text, Width = width, Click = click, Kind = kind, Admin = admin };
        }

        LabelValue _ovVersion, _ovProcess, _ovPort, _ovCli, _ovHome, _ovCompatLine, _ovCompatLine2;
        StatusPill _ovPill;
        string _compatSummary = L.T("尚未体检","Not checked yet");

        void BuildOverview(StackPage page)
        {
            var c1 = Card(page, L.T("宿主状态","Host status"), L.T("DeepSeek Harness 桌面端、CLI 与数据目录","Desktop app, CLI and data folders"));
            _ovPill = c1.HeaderSlot(new StatusPill(), 96, 22);
            _ovPill.Set(L.T("检测中","Checking"), StatusPill.Tone.Neutral);
            c1.HeaderButton(L.T("刷新","Refresh"), 84, LoadOverview, FlatButton.Kind.Ghost, 108);
            _ovVersion = c1.Row(Lv(L.T("桌面端版本","Desktop version")), 24);
            _ovProcess = c1.Row(Lv(L.T("运行进程数","Processes")), 24);
            _ovPort = c1.Row(Lv(L.T("Web 端口","Web port")), 24);
            _ovCli = c1.Row(Lv("CLI"), 24);
            _ovHome = c1.Row(Lv(L.T("数据目录","Data folder")), 24);
            c1.Finish(4);

            var c2 = Card(page, L.T("快速操作","Quick actions"), L.T("插件更新、升级残留、界面异常时先用这里","Start here for plugin updates, leftovers or a broken UI"));
            c2.Buttons(34,
                Btn(L.T("重启宿主","Restart host"), 108, () => AdminRun(L.T("重启 DSH 桌面端","Restart DSH desktop app"), "maint.restart-host --yes", "将结束全部 DSH 进程并重新启动（进行中的会话会中断）。")),
                Btn(L.T("清理缓存","Clean cache"), 108, () => Run(L.T("清理 DSH 缓存（预览）","Clean DSH cache (preview)"), "maint.clean-cache --dry-run")),
                Btn(L.T("结束残留进程","Kill leftovers"), 128, () => Run(L.T(L.T("结束残留进程（预览）","Kill leftovers (preview)"),"Kill leftovers (preview)"), "maint.kill-leftovers --dry-run")),
                Btn(L.T("重新拉取更新","Re-pull update"), 128, () => AdminRun(L.T("重新拉取更新","Re-pull update"), "maint.pull-update --yes", "将清除已下载的待安装包并重启宿主，让它重新检查更新。")),
                Btn(L.T("环境体检","Environment check"), 108, () => Select(3), FlatButton.Kind.Primary));
            c2.Finish(4);

            var c3 = Card(page, L.T("环境体检摘要","Environment summary"), L.T("13 项环境检测的结论与建议","Verdict and advice from 13 checks"));
            c3.HeaderButton(L.T("开始体检","Run checks"), 108, () => Run(L.T("环境体检","Environment check"), "compat.check", r =>
            {
                if (r.Data.ContainsKey("blockCount"))
                    _compatSummary = L.T("阻断 ","blocked ") + Bridge.S(r.Data, "blockCount") + L.T(" · 警告 "," · warned ") + Bridge.S(r.Data, "warnCount");
                c3.SetHeader(L.T("环境体检摘要","Environment summary"), _compatSummary);
                UpdateCompatSummary(r);
            }), FlatButton.Kind.Primary);
            _ovCompatLine = c3.Row(Lv(L.T("结论","Verdict"), L.T("尚未体检","Not checked yet")), 24);
            _ovCompatLine2 = c3.Row(Lv(L.T("建议","Advice"), L.T("点右上角按钮开始","Use the button above")), 24);
            c3.Finish(4);

            _refreshers[0] = LoadOverview;
        }

        void UpdateCompatSummary(ToolResult r)
        {
            int block = (int)Bridge.L(r.Data, "blockCount");
            int warn = (int)Bridge.L(r.Data, "warnCount");
            _ovCompatLine.Value = block == 0 ? (warn == 0 ? L.T("未发现明显问题","No obvious issues") : warn + L.T(" 项警告"," warnings")) : block + L.T(" 项阻断性问题"," blocking issues");
            _ovCompatLine.ValueColor = block == 0 ? (warn == 0 ? Ui.Success : Ui.Warn) : Ui.Danger;
            var tips = new List<string>();
            foreach (var it in r.Items)
            {
                var d = it as Dictionary<string, object>;
                if (d == null || Bridge.B(d, "ok")) continue;
                tips.Add(Bridge.S(d, "name"));
                if (tips.Count >= 3) break;
            }
            _ovCompatLine2.Value = tips.Count == 0 ? L.T("无需处理","Nothing to do") : string.Join("、", tips.ToArray());
            _ovPill.Set(r.Ok ? L.T("正常","OK") : L.T("需注意","Attention"), r.Ok ? StatusPill.Tone.Ok : StatusPill.Tone.Warn);
            _pages[0].Relayout();
        }

        void LoadOverview()
        {
            Run(L.T("读取宿主状态","Reading host status"), "host.status", r =>
            {
                var d = r.Data;
                var desktop = Bridge.Sub(d, "desktop");
                var cli = Bridge.Sub(d, "cli");
                _ovVersion.Value = Bridge.B(desktop, "installed") ? Bridge.S(desktop, "version", L.T("未知","Unknown")) : L.T("未安装","Not installed");
                _ovVersion.ValueColor = Bridge.B(desktop, "installed") ? Ui.Text : Ui.Warn;
                _ovProcess.Value = Bridge.S(desktop, "processCount") + L.T(" 个","") + (Bridge.B(desktop, "running") ? L.T("（运行中）"," (running)") : "");
                _ovPort.Value = Bridge.S(d, "webPort") + (Bridge.B(d, "webListening") ? L.T("  监听中 ✓","  listening ✓") : L.T("  未监听","  not listening"));
                _ovCli.Value = Bridge.B(cli, "found") ? Bridge.S(cli, "path") : L.T("未检测到（安装页可安装）","Not detected (install it on the Install page)");
                _ovHome.Value = Bridge.S(d, "dshHome");
                _ovPill.Set(Bridge.B(desktop, "installed") ? L.T("已安装","Installed") : L.T("未安装","Not installed"),
                            Bridge.B(desktop, "installed") ? StatusPill.Tone.Ok : StatusPill.Tone.Warn);
                _sidebar.Elevated = Bridge.B(d, "elevated") || GuiHost.IsElevated();
                _sidebar.Invalidate();
                _pages[0].Relayout();
            });
        }

        string _installKind = "desktop";
        LabelValue _ivLatest, _ivInstalled, _ivSize, _ivDate, _ivCli;
        FlatButton _btnDesktop, _btnCli;

        void BuildInstall(StackPage page)
        {
            var c1 = Card(page, L.T("选择安装形态","Choose a distribution"), L.T("官方桌面端与 CLI 二选一；两者可共存","Desktop app or CLI; they can coexist"));
            _btnDesktop = new FlatButton { Text = L.T("官方桌面端\n图形界面 · 托盘 · 自动更新", "Desktop app\nGUI · tray · auto-update"), Style = FlatButton.Kind.Primary };
            _btnCli = new FlatButton { Text = L.T("CLI 命令行版\n供脚本/agent 调用", "CLI\nfor scripts and agents"), Style = FlatButton.Kind.Ghost };
            c1.Two(_btnDesktop, _btnCli, 74, 14);
            _btnDesktop.Click += (s, e) => PickKind("desktop");
            _btnCli.Click += (s, e) => PickKind("cli");
            c1.Buttons(32,
                Btn(L.T("检查更新","Check for updates"), 108, LoadInstallInfo, FlatButton.Kind.Primary),
                Btn(L.T("查看官方源","View feed"), 108, () => Run(L.T("官方更新源","Official feed"), "install.check", FillInstallInfo)));
            c1.Finish(4);

            var c2 = Card(page, L.T("版本与来源","Version and source"), L.T("数据来自官方更新源 nightly.yml","Data comes from the official nightly.yml feed"));
            _ivLatest = c2.Row(Lv(L.T("最新版本","Latest version")), 24);
            _ivInstalled = c2.Row(Lv(L.T("已安装版本","Installed version")), 24);
            _ivSize = c2.Row(Lv(L.T("安装包","Package")), 24);
            _ivDate = c2.Row(Lv(L.T("发布日期","Released")), 24);
            _ivCli = c2.Row(Lv("CLI"), 24);
            c2.Finish(4);

            var c3 = Card(page, L.T("执行安装 / 升级","Run install / upgrade"), L.T("自动完成：结束残留 → 校验大小/SHA512/签名 → 静默安装 → 校验版本 → 启动","Automatic: kill leftovers; verify size/SHA512/signature; silent install; verify version; launch"));
            c3.Buttons(34,
                Btn(L.T("安装 / 升级桌面端", "Install / upgrade desktop"), 200, () => AdminRun(L.T("安装 / 升级桌面端", "Install / upgrade desktop"), "install.desktop --yes",
                    L.T("将下载并安装官方桌面端（会关闭当前运行的 DSH）。", "Downloads and installs the official desktop app (it will close the running DSH)."), null, true), FlatButton.Kind.Primary, true),
                Btn(L.T("安装 CLI","Install CLI"), 130, () => AdminRun(L.T("安装 CLI","Install CLI"), "install.cli --yes",
                    L.T("将通过 npm 安装官方命令行版 @deepseek-ai/dsh。","Installs the official CLI @deepseek-ai/dsh via npm.")), FlatButton.Kind.Ghost, true),
                Btn(L.T("从本地安装包…","From local package…"), 148, PickLocalInstaller));
            c3.Finish(4);

            _refreshers[1] = LoadInstallInfo;
            PickKind("desktop");
        }

        void LoadInstallInfo() { Run(L.T("检查官方更新源","Checking the official feed"), "install.check", FillInstallInfo); }

        void FillInstallInfo(ToolResult r)
        {
            _ivLatest.Value = Bridge.S(r.Data, "latest", L.T("获取失败","fetch failed"));
            _ivInstalled.Value = Bridge.S(r.Data, "installedVersion", L.T("未安装","Not installed"));
            _ivSize.Value = Bridge.S(r.Data, "sizeHuman", "-");
            _ivDate.Value = Bridge.S(r.Data, "releaseDate", "-");
            var cli = Bridge.Sub(r.Data, "cli");
            _ivCli.Value = Bridge.B(cli, "found") ? Bridge.S(cli, "path") : L.T("未检测到（可安装）","Not detected (installable)");
            _ivCli.ValueColor = Bridge.B(cli, "found") ? Ui.Text : Ui.Warn;
            _pages[1].Relayout();
        }

        void PickKind(string kind)
        {
            _installKind = kind;
            _btnDesktop.Style = kind == "desktop" ? FlatButton.Kind.Primary : FlatButton.Kind.Ghost;
            _btnCli.Style = kind == "cli" ? FlatButton.Kind.Primary : FlatButton.Kind.Ghost;
            Log(L.T("安装形态已选择：","Distribution: ") + (kind == "desktop" ? L.T("官方桌面端","Desktop app") : L.T("CLI 命令行版","CLI")), Ui.Accent);
        }

        void PickLocalInstaller()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = L.T("选择安装包","Choose a package");
                dlg.Filter = L.T("安装包 (*.exe;*.tgz;*.zip)|*.exe;*.tgz;*.zip|所有文件 (*.*)|*.*","Package (*.exe;*.tgz;*.zip)|*.exe;*.tgz;*.zip|All files (*.*)|*.*");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = dlg.FileName;
                string cmd = _installKind == "cli" ? "install.cli" : "install.desktop";
                AdminRun(L.T("从本地安装包安装（","Install from a local package (") + (_installKind == "cli" ? "CLI" : "桌面端") + "）",
                         cmd + " --yes --file \"" + path + "\"", L.T("将使用本地文件：","Using local file: ") + path);
            }
        }

        void BuildMaint(StackPage page)
        {
            var c1 = Card(page, L.T(L.T("快速刷新","Quick refresh"),"Quick refresh"), L.T(L.T("插件在重启后才会重新加载；升级失败、界面异常时先用这里","Plugins reload only after a restart; start here after a failed upgrade"),"Plugins reload only after a restart; start here after a failed upgrade"));
            c1.Buttons(34,
                Btn(L.T(L.T("重启宿主（刷新插件）","Restart host (reload plugins)"),"Restart host (reload plugins)"), 190, () => AdminRun(L.T("重启 DSH 桌面端","Restart DSH desktop app"), "maint.restart-host --yes", "将结束全部进程并重新启动。"), FlatButton.Kind.Primary, true),
                Btn(L.T(L.T(L.T("重新拉取更新","Re-pull update"),"Re-pull update"), "Re-pull update"), 134, () => AdminRun(L.T(L.T(L.T("重新拉取更新","Re-pull update"),"Re-pull update"), "Re-pull update"), "maint.pull-update --yes", L.T("清除已下载内容并重启，让它重新检查更新。", "Clears downloaded packages and restarts so it re-checks.")), FlatButton.Kind.Ghost, true));
            c1.Buttons(34,
                Btn(L.T(L.T(L.T("结束残留进程（预览）","Kill leftovers (preview)"),"Kill leftovers (preview)"),"Kill leftovers (preview)"), 168, () => Run(L.T(L.T(L.T("结束残留进程（预览）","Kill leftovers (preview)"),"Kill leftovers (preview)"),"Kill leftovers (preview)"), "maint.kill-leftovers --dry-run")),
                Btn(L.T(L.T("清理缓存（预览）","Clean cache (preview)"),"Clean cache (preview)"), 140, () => Run(L.T(L.T("清理缓存（预览）","Clean cache (preview)"),"Clean cache (preview)"), "maint.clean-cache --dry-run")),
                Btn(L.T(L.T("清理缓存并执行","Clean cache (run)"),"Clean cache (run)"), 140, () => AdminRun(L.T("清理 DSH 缓存","Clean DSH cache"), "maint.clean-cache --yes", "会关闭应用并删除缓存目录。"), FlatButton.Kind.Ghost, true));
            c1.Buttons(34,
                Btn(L.T(L.T("彻底清理（含会话存储）","Deep clean (incl. storage)"),"Deep clean (incl. storage)"), 200, () => AdminRun(L.T("彻底清理缓存","Deep cache clean"), "maint.clean-cache --yes --all", "除常规缓存外还会清理 Local/Session Storage（会退出登录态）。"), FlatButton.Kind.Danger, true),
                Btn(L.T(L.T("重建工具箱自身","Rebuild toolbox"),"Rebuild toolbox"), 148, () => Run(L.T("重建 dsh-toolbox","Rebuilding dsh-toolbox"), "maint.rebuild-self")));
            c1.Finish(4);

            var c2 = Card(page, L.T(L.T("目录","Folders"),"Folders"), L.T(L.T("排障时常用的位置","Places you need while troubleshooting"),"Places you need while troubleshooting"));
            c2.Buttons(34,
                Btn(L.T(L.T("打开数据目录","Open data folder"),"Open data folder"), 130, () => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop"))),
                Btn(L.T(L.T("打开工具箱日志","Open toolbox logs"),"Open toolbox logs"), 140, () => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-toolbox", "logs"))),
                Btn(L.T(L.T("打开更新缓存","Open update cache"),"Open update cache"), 130, () => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "@deepseek-aidsh-desktop-updater"))),
                Btn(L.T(L.T("打开安装目录","Open install folder"),"Open install folder"), 130, () => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeepSeek Harness"))));
            c2.Finish(4);

            _refreshers[2] = () => { };
        }

        void OpenPath(string path)
        {
            try
            {
                if (!Directory.Exists(path)) { Log(L.T(L.T("目录不存在：","Folder not found: "),"Folder not found: ") + path, Ui.Warn); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                Log(L.T("已打开：","Opened: ") + path, Ui.Muted);
            }
            catch (Exception ex) { Log(L.T(L.T("打开失败：","Open failed: "),"Open failed: ") + ex.Message, Ui.Danger); }
        }

        BodyCard _compatCard;

        void BuildCompat(StackPage page)
        {
            var c1 = Card(page, L.T(L.T("环境体检","Environment check"),"Environment check"), L.T(L.T("针对验签超时、指令集不支持、脚本受限等已知故障场景","Known failure modes: signing timeouts, unsupported instruction sets, restricted scripting"),"Known failure modes: signing timeouts, unsupported instruction sets, restricted scripting"));
            c1.Buttons(34,
                Btn(L.T(L.T("开始体检","Run checks"),"Run checks"), 108, () => Run(L.T(L.T("环境体检","Environment check"),"Environment check"), "compat.check", RenderCompat), FlatButton.Kind.Primary),
                Btn(L.T(L.T("快速体检（跳过联网）","Quick check (offline)"),"Quick check (offline)"), 178, () => Run(L.T("环境体检（--fast）","Environment check (--fast)"), "compat.check --fast", RenderCompat)),
                Btn(L.T(L.T("应用可自动修复项","Apply auto-fixes"),"Apply auto-fixes"), 178, ApplyFixes, FlatButton.Kind.Ghost, true));
            c1.Finish(4);

            _compatCard = Card(page, L.T(L.T("检测结果","Check results"),"Check results"), L.T(L.T("每项都给出结论与原因；读不到就说读不到，绝不假装正常","Every item reports a verdict and a reason; unreadable is reported as unreadable"),"Every item reports a verdict and a reason; unreadable is reported as unreadable"));
            _compatCard.Row(Lv(L.T("提示","Info"), L.T("点上方「开始体检」开始检测","Use Run checks above")), 24);
            _compatCard.Finish(4);

            _refreshers[3] = () => Run(L.T(L.T("环境体检","Environment check"),"Environment check"), "compat.check", RenderCompat);
        }

        void RenderCompat(ToolResult r)
        {
            _compatCard.ResetBody();
            foreach (var it in r.Items)
            {
                var d = it as Dictionary<string, object>;
                if (d == null) continue;
                string name = Bridge.S(d, "name");
                string detail = Bridge.S(d, "detail");
                bool ok = Bridge.B(d, "ok");
                bool fixable = Bridge.B(d, "fixable");
                string fixId = Bridge.S(d, "fixId");
                string level = Bridge.S(d, "level");

                var pill = new StatusPill();
                pill.Set(ok ? L.T("通过","Pass") : (level == "block" ? L.T("阻断","Blocked") : level == "warn" ? L.T("警告","Warn") : L.T("提示","Info")),
                         ok ? StatusPill.Tone.Ok : (level == "block" ? StatusPill.Tone.Bad : StatusPill.Tone.Warn));
                var line = Lv(name, detail, ok ? Ui.SubText : (level == "block" ? Ui.Danger : Ui.Warn));

                FlatButton fix = null;
                if (fixable && !string.IsNullOrEmpty(fixId))
                {
                    string id = fixId;
                    fix = new FlatButton { Text = L.T("修复","Fix"), Style = FlatButton.Kind.Ghost, Font = Ui.FSmall };
                    fix.Click += (s, e) => FixOne(id);
                }
                _compatCard.InfoRow(pill, line, fix, 26, 66, fix == null ? 0 : 62);
            }
            _compatCard.Finish(6);
            _pages[3].Relayout();

            int block = (int)Bridge.L(r.Data, "blockCount");
            int warn = (int)Bridge.L(r.Data, "warnCount");
            Log(L.T("体检结论：","Environment check: ") + (block == 0 ? L.T("无阻断项","no blockers") : block + L.T(" 项阻断"," blocked")) + L.T("，警告 ","; warnings ") + warn + L.T(" 项",""), block == 0 ? Ui.Success : Ui.Warn);
        }

        void FixOne(string id)
        {
            switch (id)
            {
                case "longpaths": AdminRun(L.T("启用超长路径","Enable long paths"), "compat.fix --id longpaths --yes", "将写入 HKLM 的 LongPathsEnabled=1。"); break;
                case "defender": AdminRun(L.T("添加 Defender 排除项","Add Defender exclusions"), "compat.fix --id defender --yes", "把应用目录与更新缓存排除出实时扫描，可显著加快验签与安装。"); break;
                case "leftovers": Run(L.T(L.T(L.T("结束残留进程（预览）","Kill leftovers (preview)"),"Kill leftovers (preview)"),"Kill leftovers (preview)"), "maint.kill-leftovers --dry-run"); break;
                case "caches": Run(L.T(L.T("清理缓存（预览）","Clean cache (preview)"),"Clean cache (preview)"), "maint.clean-cache --dry-run"); break;
                default: Log(L.T("该项需手动处理：","Handle manually: ") + id, Ui.Warn); break;
            }
        }

        void ApplyFixes()
        {
            var r = MessageBox.Show(this,
                "将依次应用「可自动修复」的项目：\n\n  · 启用超长路径（需管理员）\n  · 添加 Defender 排除项（需管理员）\n\n继续会弹出 UAC 授权。",
                L.T("应用修复","Apply fixes"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
            AdminRun(L.T("启用超长路径","Enable long paths"), "compat.fix --id longpaths --yes", "写入 LongPathsEnabled=1。", res =>
            {
                AdminRun(L.T("添加 Defender 排除项","Add Defender exclusions"), "compat.fix --id defender --yes", "添加两条目录排除项。");
            });
        }

        LabelValue _logPreview;

        void BuildLogs(StackPage page)
        {
            var c1 = Card(page, L.T("运行记录","Run history"), L.T("每次调用都会落盘到 runs.jsonl，agent 与人都能回看","Every call is appended to runs.jsonl; readable by agents and humans"));
            _logPreview = c1.Row(Lv(L.T("最近调用","Recent calls"), "—"), 24);
            c1.Buttons(32,
                Btn(L.T("刷新","Refresh"), 88, LoadLogs, FlatButton.Kind.Primary),
                Btn(L.T("查看失败记录","Failed calls"), 128, () => Run(L.T("失败的调用","Failed calls"), "log.runs --exit 1 --limit 10")),
                Btn(L.T("打开日志目录","Open log folder"), 128, () => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-toolbox", "logs"))),
                Btn(L.T("打开独立日志窗口","Pop out log window"), 152, OpenLogWindow));
            c1.Finish(4);

            var c2 = Card(page, L.T("日志与后台任务","Logs and background jobs"), L.T("可开启自动刷新（每 5 秒）","Optional auto-refresh (every 5 s)"));
            var auto = new CheckBox
            {
                Text = L.T("自动刷新（5 秒）","Auto-refresh (5 s)"),
                ForeColor = Ui.SubText,
                Font = Ui.FSmall,
                BackColor = Ui.Card,
                FlatStyle = FlatStyle.Flat
            };
            auto.CheckedChanged += (s, e) =>
            {
                if (auto.Checked) { _tick.Interval = 5000; _tick.Tick += AutoTick; _tick.Start(); Log(L.T("已开启自动刷新（5 秒）","Auto-refresh on (5 s)"), Ui.Muted); }
                else { _tick.Tick -= AutoTick; _tick.Interval = 1000; Log(L.T("已关闭自动刷新","Auto-refresh off"), Ui.Muted); }
            };
            c2.Row(auto, 24);
            c2.Buttons(32,
                Btn(L.T("日志尾部","Log tail"), 108, () => Run(L.T("读取日志尾部","Reading log tail"), "log.tail --lines 30")),
                Btn(L.T("后台任务","Background jobs"), 108, () => Run(L.T("后台任务列表","Background jobs"), "job.list --limit 10")),
                Btn(L.T("失败的 job","Failed jobs"), 118, () => Run(L.T("后台任务列表","Background jobs"), "job.list --state failed --limit 10")),
                Btn(L.T("清屏","Clear"), 88, ClearLog, FlatButton.Kind.Subtle));
            c2.Finish(4);

            _refreshers[4] = LoadLogs;
        }

        void ClearLog()
        {
            _log.Clear();
            _logEntries.Clear();
            if (_logWindow != null && !_logWindow.IsDisposed) _logWindow.Close();
        }

        void AutoTick(object s, EventArgs e) { if (!_busy && _current == 4) Run(L.T("自动刷新","Auto refresh"), "log.tail --lines 5"); }

        void LoadLogs()
        {
            Run(L.T("最近调用记录","Recent calls"), "log.runs --limit 6", r => { _logPreview.Value = r.Items.Count + " 条（明细见活动日志）"; _pages[4].Relayout(); });
        }

        LabelValue _setLangLine, _setThemeLine;

        static string LangSummary()
        {
            return Settings.LangIsAuto
                ? L.T("自动（系统：" + Settings.SystemLang() + "）", "auto (system: " + Settings.SystemLang() + ")")
                : Settings.Lang + L.T("（手动指定）", " (explicit)");
        }

        static string ThemeSummary()
        {
            return Settings.EffectiveTheme() + (Settings.ThemeIsAuto
                ? L.T("（自动，跟随系统）", " (auto, follows system)")
                : L.T("（手动指定）", " (explicit)"));
        }

        void BuildAbout(StackPage page)
        {
            var c1 = Card(page, "dsh-toolbox", L.T("DSH 的 Windows 工具箱：GUI + CLI 双模，单文件 exe","The DSH toolbox for Windows: GUI + CLI in a single exe"));
            c1.Row(Lv(L.T("版本","Version"), ToolInfo.Version + L.T("    协议 ","    protocol ") + ToolInfo.Protocol), 24);
            c1.Row(Lv(L.T("可执行文件","Executable"), Redact.Text(Bridge.ExePath)), 24);
            c1.Row(Lv(L.T("数据目录","Data folder"), Redact.Text(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-toolbox"))), 24);
            c1.Row(Lv(L.T("运行时","Runtime"), ".NET Framework " + Environment.Version + L.T("（系统自带）"," (bundled with Windows)")), 24);
            c1.Buttons(32,
                Btn(L.T("打开程序目录","Open app folder"), 128, () => OpenPath(System.IO.Path.GetDirectoryName(Bridge.ExePath))),
                Btn(L.T("查看命令清单","View command list"), 128, () => Run(L.T("命令清单","Command catalog"), "manifest")),
                Btn(L.T("打开独立日志窗口","Pop out log window"), 152, OpenLogWindow));
            c1.Finish(4);

            var c2 = Card(page, L.T("给 agent 的接入方式","How agents connect"), L.T("CLI 与 stdio 通道并存，契约见 docs/CLI-CONTRACT.md","CLI and stdio channel; see docs/CLI-CONTRACT.md"));
            var box = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                BackColor = Ui.CodeBg,
                Font = Ui.Mono(9f),
                WordWrap = false,
                ForeColor = Ui.SubText
            };
            box.Text = "dsh-toolbox.exe manifest --json          " + L.T("# 命令清单", "# command catalog") + "\r\n" +
                       "dsh-toolbox.exe host.status --json        " + L.T("# 宿主状态", "# host status") + "\r\n" +
                       "dsh-toolbox.exe compat.check --json       " + L.T("# 环境体检", "# environment check") + "\r\n" +
                       "dsh-toolbox.exe install.check --json      " + L.T("# 检查更新", "# check for updates") + "\r\n" +
                       "dsh-toolbox.exe sign.verify --path x.exe  " + L.T("# 签名校验（不设 20s 超时）", "# signature check (no 20s timeout)") + "\r\n" +
                       "dsh-toolbox.exe serve --stdio             " + L.T("# JSON-RPC 通道（agent 长连接）", "# JSON-RPC channel for agents");
            c2.Row(box, 108);
            c2.Finish(4);

            var c3 = Card(page, L.T("设置","Settings"), L.T("默认跟随系统；修改后持久化到 settings.json","Follows the system by default; changes persist to settings.json"));
            _setLangLine = c3.Row(Lv(L.T("语言","Language"), LangSummary()), 24);
            _setThemeLine = c3.Row(Lv(L.T("主题","Theme"), ThemeSummary()), 24);
            c3.Buttons(34,
                Btn(L.T("切换主题","Toggle theme"), 118, ToggleTheme, FlatButton.Kind.Primary),
                Btn(L.T("主题跟随系统","Theme: follow system"), 156, () => SetTheme("auto")));
            c3.Buttons(34,
                Btn("中文", 76, () => SetLanguage("zh-CN")),
                Btn("English", 88, () => SetLanguage("en-US")),
                Btn(L.T("语言跟随系统","Language: follow system"), 168, () => SetLanguage("auto")),
                Btn(L.T("打开设置目录","Open settings folder"), 148, () => OpenPath(Paths.Home)));
            c3.Finish(4);
            _refreshers[5] = () => { };
        }

        void AfterShown()
        {
            LayoutChrome();
            Log(L.T("dsh-toolbox " + ToolInfo.Version + " 已启动（GUI 模式）。", "dsh-toolbox " + ToolInfo.Version + " started (GUI mode)."), Ui.Success);
            Log(L.T("F5 刷新当前页；单击下方活动日志可打开独立窗口。", "F5 refreshes the page; click the activity log to pop it out."), Ui.Muted);
            if (!GuiHost.IsElevated())
                Log(L.T("当前为普通用户：需要管理员的动作会先提示，确认后自动弹出 UAC。", "Standard user: admin actions ask first, then raise UAC."), Ui.Muted);
            LoadOverview();
            foreach (var a in _startArgs) if (a == "--log-window") OpenLogWindow();   // 仅供自动化截图验证
            Run(L.T("环境体检（快速）","Environment check (quick)"), "compat.check --fast", UpdateCompatSummary);
        }

        // ------------------------------------------------------------ 主题
        void ToggleTheme()
        {
            Settings.Theme = Ui.IsDark ? "light" : "dark";
            Settings.Save();
            Ui.ApplyTheme(Settings.EffectiveTheme());
            Log(L.T("主题已切换为 " + Settings.Theme, "Theme switched to " + Settings.Theme), Ui.Accent);
        }

        void ApplyThemeToUi()
        {
            BackColor = Ui.Bg;
            _main.BackColor = Ui.Bg;
            _pageHost.BackColor = Ui.Bg;
            _sidebar.BackColor = Ui.Sidebar;
            _log.BackColor = Ui.Card;
            _log.ForeColor = Ui.Text;
            _status.ForeColor = Ui.Muted;
            _logCard.BackColor = Ui.Card;
            foreach (var page in _pages)
            {
                page.BackColor = Ui.Bg;
                foreach (Control c in page.Cards) { c.BackColor = Ui.Card; c.Invalidate(); }
                page.Relayout();
            }
            if (_logWindow != null && !_logWindow.IsDisposed) _logWindow.ApplyTheme();
            _sidebar.Invalidate();
            _title.Invalidate();
            Invalidate(true);
            foreach (Control c in Controls) c.Invalidate(true);
        }

        // ------------------------------------------------------------ 提权（管理员模式）
        void RequestElevation()
        {
            if (GuiHost.IsElevated())
            {
                MessageBox.Show(this, L.T("当前已经是管理员模式。", "Already running in admin mode."),
                    L.T(L.T("管理员模式","Admin mode"), "Admin mode"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var r = MessageBox.Show(this,
                L.T("是否进入管理员模式？\n\n• 会重新启动一个拥有管理员权限的工具箱窗口\n• 需要你在 UAC 窗口点击「是」\n• 之后需要提权的操作不再逐次询问\n\n当前窗口会在新窗口启动后关闭。",
                    "Enter admin mode?\n\n• A new toolbox window will start with administrator rights\n• You need to click Yes in the UAC prompt\n• Admin-only actions then run without asking each time\n\nThis window closes once the new one starts."),
                L.T(L.T("进入管理员模式","Enter admin mode"), "Enter admin mode"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { Log(L.T("已取消提权", "Elevation cancelled"), Ui.Muted); return; }
            try
            {
                var psi = new ProcessStartInfo(Bridge.ExePath, "--gui")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(Bridge.ExePath)
                };
                Process.Start(psi);
                Log(L.T("已启动管理员实例，正在关闭当前窗口…", "Elevated instance started; closing this window…"), Ui.Success);
                BeginInvoke(new Action(delegate { Close(); }));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                    Log(L.T("你在 UAC 窗口点了「否」，保持普通用户模式", "UAC declined; staying in standard mode"), Ui.Muted);
                else Log(L.T("提权失败：", "Elevation failed: ") + ex.Message, Ui.Danger);
            }
            catch (Exception ex) { Log(L.T("提权失败：", "Elevation failed: ") + ex.Message, Ui.Danger); }
        }

        // ------------------------------------------------------------ 语言
        void SetLanguage(string lang)
        {
            string norm = string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase) ? "" : lang;
            if (string.Equals(Settings.Lang, norm, StringComparison.OrdinalIgnoreCase)) return;
            Settings.Lang = norm;
            Settings.Save();
            L.Use(null);
            var r = MessageBox.Show(this,
                Settings.LangIsAuto
                    ? L.T("语言已切换为「跟随系统」，需要重建窗口才能生效。现在重建？", "Language set to follow the system. The window must be rebuilt to apply. Rebuild now?")
                    : L.T("语言已切换，需要重建窗口才能生效。现在重建？", "Language changed. The window must be rebuilt to apply. Rebuild now?"),
                "dsh-toolbox", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) { try { Application.Restart(); } catch { } }
        }

        void SetTheme(string theme)
        {
            string norm = string.Equals(theme, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : (theme == "dark" ? "dark" : "light");
            if (string.Equals(Settings.Theme, norm, StringComparison.OrdinalIgnoreCase)) return;
            Settings.Theme = norm;
            Settings.Save();
            Ui.ApplyTheme(Settings.EffectiveTheme());
            if (_setThemeLine != null) { _setThemeLine.Value = ThemeSummary(); _pages[5].Relayout(); }
            Log(L.T("主题：", "Theme: ") + (Settings.ThemeIsAuto
                    ? L.T("跟随系统（当前 ", "follows system (") + Settings.SystemTheme() + ")"
                    : Settings.Theme), Ui.Accent);
        }

        // ------------------------------------------------------------ 长任务：实时进度
        void RunStream(string title, string cmdLine, Action<ToolResult> done = null)
        {
            if (_busy) { Log(L.T(L.T("上一个操作仍在执行，已忽略：","Busy; ignored: "), "Busy; ignored: ") + title, Ui.Warn); return; }
            _busy = true;
            Log("▶ " + title, Ui.Accent);
            Log("  $ dsh-toolbox " + cmdLine, Ui.Muted);
            SetStatus(title + " …", 3);
            var sw = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(delegate
            {
                ToolResult res;
                try
                {
                    res = Bridge.Call(cmdLine, 1800000, true, delegate(string line)
                    {
                        try
                        {
                            var o = Json.ParseObject(line);
                            if (Json.GetString(o, "type") != "item") return;
                            var item = o.ContainsKey("item") ? o["item"] as Dictionary<string, object> : null;
                            if (item == null) return;
                            int pct = (int)Bridge.L(item, "percent", -1);
                            string dn = Bridge.Compact(item.ContainsKey("done") ? item["done"] : null);
                            string tt = Bridge.Compact(item.ContainsKey("total") ? item["total"] : null);
                            if (pct < 0) return;
                            BeginInvoke(new Action(delegate
                            {
                                _progress.Value = pct;
                                SetStatus(title + "  " + pct + "%   " + dn + " / " + tt, pct);
                            }));
                        }
                        catch { }
                    });
                }
                catch (Exception ex) { res = new ToolResult { Exit = 1, Raw = ex.Message }; }
                sw.Stop();
                BeginInvoke(new Action(delegate
                {
                    _busy = false;
                    _progress.Value = 0;
                    bool ok = res.Ok;
                    SetStatus((ok ? L.T(L.T("完成","done"), "done") : L.T(L.T("失败","failed"), "failed")) + L.T(" · "," · ") + title + L.T(" · "," · ") + sw.ElapsedMilliseconds + " ms", 0);
                    if (ok) { Log("  ✓ " + title + " " + L.T(L.T("完成","done"), "done") + "（" + sw.ElapsedMilliseconds + " ms）", Ui.Success); PrintData(res.Data); }
                    else Log("  ✗ " + title + " " + L.T(L.T("失败","failed"), "failed") + "（exit=" + res.Exit + "）：" + res.Error, Ui.Danger);
                    if (done != null) done(res);
                }));
            });
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && (int)m.Result == 1 && WindowState == FormWindowState.Normal)
                Chrome.ResizeHitTest(this, ref m);
        }
    }
}

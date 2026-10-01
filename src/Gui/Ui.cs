using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DshToolbox.Gui
{
    /// <summary>现代浅色主题：调色板、字体、圆角绘制、自绘控件。</summary>
    public static class Ui
    {
                // ---------------------------------------------------------- 主题（light / dark）
        public sealed class Palette
        {
            public Color Bg, Sidebar, Card, Border, BorderStrong, Text, SubText, Muted,
                         Accent, AccentHover, AccentPress, AccentSoft, Success, SuccessSoft,
                         Warn, WarnSoft, Danger, DangerSoft, HoverSoft, CodeBg;
        }

        public static readonly Palette Light = new Palette
        {
            Bg = Color.FromArgb(0xF4, 0xF5, 0xF7), Sidebar = Color.FromArgb(0xFF, 0xFF, 0xFF),
            Card = Color.FromArgb(0xFF, 0xFF, 0xFF), Border = Color.FromArgb(0xE5, 0xE8, 0xEC),
            BorderStrong = Color.FromArgb(0xD0, 0xD7, 0xDE), Text = Color.FromArgb(0x1F, 0x23, 0x28),
            SubText = Color.FromArgb(0x5B, 0x64, 0x6E), Muted = Color.FromArgb(0x8B, 0x94, 0x9E),
            Accent = Color.FromArgb(0x2F, 0x6F, 0xED), AccentHover = Color.FromArgb(0x25, 0x60, 0xD8),
            AccentPress = Color.FromArgb(0x1E, 0x50, 0xB8), AccentSoft = Color.FromArgb(0xEC, 0xF2, 0xFE),
            Success = Color.FromArgb(0x1A, 0x7F, 0x37), SuccessSoft = Color.FromArgb(0xE8, 0xF5, 0xEC),
            Warn = Color.FromArgb(0x9A, 0x67, 0x00), WarnSoft = Color.FromArgb(0xFD, 0xF3, 0xDD),
            Danger = Color.FromArgb(0xCF, 0x22, 0x2E), DangerSoft = Color.FromArgb(0xFC, 0xEB, 0xEC),
            HoverSoft = Color.FromArgb(0xF0, 0xF2, 0xF5), CodeBg = Color.FromArgb(0xFA, 0xFB, 0xFC)
        };

        public static readonly Palette Dark = new Palette
        {
            Bg = Color.FromArgb(0x16, 0x18, 0x1C), Sidebar = Color.FromArgb(0x1B, 0x1E, 0x23),
            Card = Color.FromArgb(0x21, 0x24, 0x29), Border = Color.FromArgb(0x2C, 0x30, 0x37),
            BorderStrong = Color.FromArgb(0x3A, 0x40, 0x48), Text = Color.FromArgb(0xE6, 0xE8, 0xEB),
            SubText = Color.FromArgb(0xA8, 0xAF, 0xB9), Muted = Color.FromArgb(0x7C, 0x84, 0x8F),
            Accent = Color.FromArgb(0x4C, 0x8D, 0xFF), AccentHover = Color.FromArgb(0x6B, 0xA1, 0xFF),
            AccentPress = Color.FromArgb(0x3A, 0x78, 0xE6), AccentSoft = Color.FromArgb(0x1E, 0x2A, 0x3F),
            Success = Color.FromArgb(0x3F, 0xB9, 0x50), SuccessSoft = Color.FromArgb(0x17, 0x30, 0x1D),
            Warn = Color.FromArgb(0xD2, 0x99, 0x22), WarnSoft = Color.FromArgb(0x2E, 0x27, 0x16),
            Danger = Color.FromArgb(0xF8, 0x51, 0x49), DangerSoft = Color.FromArgb(0x3A, 0x1D, 0x1D),
            HoverSoft = Color.FromArgb(0x28, 0x2C, 0x33), CodeBg = Color.FromArgb(0x1A, 0x1D, 0x22)
        };

        /// <summary>
        /// 高对比度：不自己发明配色，而是直接取系统高对比度方案的颜色
        /// （Window/WindowText/Highlight/HotTrack/GrayText），因此兼容用户选的任意 HC 主题。
        /// 语义色不再靠色相区分——界面里本来就带 ✓/✗/⚠ 字形与文字标签，颜色只是辅助通道。
        /// </summary>
        public static Palette BuildContrast()
        {
            var p = new Palette();
            p.Bg = SystemColors.Window;
            p.Card = SystemColors.Window;
            p.Sidebar = SystemColors.Window;
            p.Border = SystemColors.WindowText;
            p.BorderStrong = SystemColors.WindowText;
            p.Text = SystemColors.WindowText;
            p.SubText = SystemColors.WindowText;
            p.Muted = SystemColors.GrayText;
            p.Accent = SystemColors.Highlight;
            p.AccentHover = SystemColors.Highlight;
            p.AccentPress = SystemColors.Highlight;
            p.AccentSoft = SystemColors.Window;
            p.Success = SystemColors.WindowText;
            p.SuccessSoft = SystemColors.Window;
            p.Warn = SystemColors.HotTrack;
            p.WarnSoft = SystemColors.Window;
            p.Danger = SystemColors.Highlight;
            p.DangerSoft = SystemColors.Window;
            p.HoverSoft = SystemColors.Window;
            p.CodeBg = SystemColors.Window;
            return p;
        }

        public static bool IsContrast { get { return ThemeName == "contrast"; } }

        static Palette _p = Light;
        public static string ThemeName = "light";
        public static event EventHandler ThemeChanged;
        public static bool IsDark { get { return object.ReferenceEquals(_p, Dark); } }

        public static void ApplyTheme(string name)
        {
            if (string.Equals(name, "contrast", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "high-contrast", StringComparison.OrdinalIgnoreCase))
            {
                ThemeName = "contrast";
                _p = BuildContrast();          // 每次重建：跟随用户当前的高对比度方案
            }
            else
            {
                ThemeName = string.Equals(name, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
                _p = ThemeName == "dark" ? Dark : Light;
            }
            var h = ThemeChanged;
            if (h != null) h(null, EventArgs.Empty);
        }

        public static Color Bg { get { return _p.Bg; } }
        public static Color Sidebar { get { return _p.Sidebar; } }
        public static Color Card { get { return _p.Card; } }
        public static Color Border { get { return _p.Border; } }
        public static Color BorderStrong { get { return _p.BorderStrong; } }
        public static Color Text { get { return _p.Text; } }
        public static Color SubText { get { return _p.SubText; } }
        public static Color Muted { get { return _p.Muted; } }
        public static Color Accent { get { return _p.Accent; } }
        public static Color AccentHover { get { return _p.AccentHover; } }
        public static Color AccentPress { get { return _p.AccentPress; } }
        public static Color AccentSoft { get { return _p.AccentSoft; } }
        public static Color Success { get { return _p.Success; } }
        public static Color SuccessSoft { get { return _p.SuccessSoft; } }
        public static Color Warn { get { return _p.Warn; } }
        public static Color WarnSoft { get { return _p.WarnSoft; } }
        public static Color Danger { get { return _p.Danger; } }
        public static Color DangerSoft { get { return _p.DangerSoft; } }
        public static Color HoverSoft { get { return _p.HoverSoft; } }
        public static Color CodeBg { get { return _p.CodeBg; } }

        /// <summary>语义色调：日志与状态色随主题变化，调用方只表达DshToolbox.Core.L.T("含义","Meaning")。</summary>
        public enum LogTone { Info, Accent, Success, Warn, Danger, Muted, Sub }

        static Image _appIcon;
        static bool _appIconTried;

        /// <summary>应用图标：从 exe 自身关联的图标读取（构建时由 /win32icon 嵌入）。失败返回 null。</summary>
        public static Image AppIcon
        {
            get
            {
                if (!_appIconTried)
                {
                    _appIconTried = true;
                    try
                    {
                        using (var ico = Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath))
                            if (ico != null) _appIcon = ico.ToBitmap();
                    }
                    catch { }
                }
                return _appIcon;
            }
        }

        public static Color Tone(LogTone t)
        {
            switch (t)
            {
                case LogTone.Accent: return Accent;
                case LogTone.Success: return Success;
                case LogTone.Warn: return Warn;
                case LogTone.Danger: return Danger;
                case LogTone.Muted: return Muted;
                case LogTone.Sub: return SubText;
                default: return Text;
            }
        }

        // ---------------------------------------------------------- 字体
        static readonly string FamilyName = PickFamily();
        static string PickFamily()
        {
            foreach (var f in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" })
            {
                try
                {
                    using (var ff = new FontFamily(f)) return f;
                }
                catch { }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        public static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            try { return new Font(FamilyName, size, style, GraphicsUnit.Point); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point); }
        }
        public static Font Mono(float size)
        {
            try { return new Font("Consolas", size); } catch { return F(size); }
        }

        public static readonly Font FBody = F(9.5f);
        public static readonly Font FSmall = F(8.5f);
        public static readonly Font FBold = F(9.5f, FontStyle.Bold);
        public static readonly Font FTitle = F(15f, FontStyle.Bold);
        public static readonly Font FPageTitle = F(18f, FontStyle.Bold);
        public static readonly Font FCardTitle = F(11f, FontStyle.Bold);
        public static readonly Font FH1 = F(22f, FontStyle.Bold);

        // ---------------------------------------------------------- 绘制辅助
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
            g.SmoothingMode = old;
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float w = 1f)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rr = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var p = Round(rr, radius))
            using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }

        /// <summary>GDI 文本绘制（对中文回退友好、比 GDI+ 清晰）。</summary>
        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle rect,
                                    TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            TextRenderer.DrawText(g, text ?? "", font, rect, color, flags);
        }

        public static Size MeasureText(string text, Font font, int maxWidth = 0)
        {
            // maxWidth = 0：按「单行」测量。注意不能带 WordBreak，否则 Size.Empty 会把文本折行、
            // 量出偏窄的宽度（曾导致侧栏排版判断错误、标签叠字）。
            if (maxWidth > 0)
                return TextRenderer.MeasureText(text ?? "", font, new Size(maxWidth, int.MaxValue),
                                                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            return TextRenderer.MeasureText(text ?? "", font, Size.Empty, TextFormatFlags.NoPadding);
        }

        /// <summary>把长文本按宽度折行（用于说明文字）。</summary>
        public static string Wrap(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var words = text.Split(' ');
            var sb = new System.Text.StringBuilder();
            var line = new System.Text.StringBuilder();
            foreach (var w in words)
            {
                var probe = line.Length == 0 ? w : line + " " + w;
                if (MeasureText(probe, font).Width > width && line.Length > 0)
                {
                    sb.AppendLine(line.ToString());
                    line.Clear();
                    line.Append(w);
                }
                else
                {
                    if (line.Length > 0) line.Append(' ');
                    line.Append(w);
                }
            }
            if (line.Length > 0) sb.Append(line.ToString());
            return sb.ToString();
        }
    }

    // ================================================================== 自绘按钮
    public class FlatButton : Control
    {
        public enum Kind { Primary, Ghost, Danger, Subtle }

        Kind _kind = Kind.Ghost;
        bool _hover, _press;
        public Kind Style { get { return _kind; } set { _kind = value; Invalidate(); } }
        public string Glyph = "";
        public string Note = "";
        public bool Busy;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(120, 34);
            Cursor = Cursors.Hand;
            Font = Ui.FBody;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            var r = new Rectangle(0, 0, Width, Height);

            Color fill, border, fg;
            switch (_kind)
            {
                case Kind.Primary:
                    fill = !Enabled ? Color.FromArgb(0xAF, 0xC3, 0xF2) : (_press ? Ui.AccentPress : (_hover ? Ui.AccentHover : Ui.Accent));
                    border = fill; fg = Color.White; break;
                case Kind.Danger:
                    fill = !Enabled ? Ui.DangerSoft : (_press ? Color.FromArgb(0xB0, 0x1B, 0x26) : (_hover ? Color.FromArgb(0xBE, 0x1F, 0x2A) : Ui.Danger));
                    border = fill; fg = Enabled ? Color.White : Ui.Danger; break;
                case Kind.Subtle:
                    fill = _hover ? Ui.HoverSoft : Color.Transparent;
                    border = Color.Transparent; fg = Ui.SubText; break;
                default:
                    fill = _press ? Ui.HoverSoft : (_hover ? Color.FromArgb(0xF7, 0xF8, 0xFA) : Ui.Card);
                    border = Ui.BorderStrong; fg = Ui.Text; break;
            }
            if (fill.A > 0) Ui.FillRound(g, r, 8, fill);
            if (border.A > 0) Ui.StrokeRound(g, r, 8, border);

            string label = Busy ? DshToolbox.Core.L.T("处理中…","Working…") : Text;
            if (!string.IsNullOrEmpty(Glyph)) label = Glyph + "  " + label;
            var area = new Rectangle(10, 0, Width - 20, Height);
            Ui.DrawText(g, label, Font, Enabled ? fg : Ui.Muted, area,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                        TextFormatFlags.EndEllipsis);
        }
    }

    // ================================================================== 卡片容器
    public class CardPanel : Panel
    {
        public string Title = "";
        public string Subtitle = "";
        public int TitleHeight = 0;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Ui.Card;
            Padding = new Padding(18, 16, 18, 16);
        }

        public void SetHeader(string title, string subtitle)
        {
            Title = title; Subtitle = subtitle;
            TitleHeight = string.IsNullOrEmpty(subtitle) ? 40 : 62;
            Padding = new Padding(18, TitleHeight + 6, 18, 16);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new Rectangle(0, 0, Width, Height);
            Ui.FillRound(g, r, 10, Ui.Card);
            Ui.StrokeRound(g, r, 10, Ui.Border);

            if (!string.IsNullOrEmpty(Title))
            {
                Ui.DrawText(g, Title, Ui.FCardTitle, Ui.Text, new Rectangle(18, 14, Width - 36, 22),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (!string.IsNullOrEmpty(Subtitle))
                    Ui.DrawText(g, Subtitle, Ui.FSmall, Ui.Muted, new Rectangle(18, 34, Width - 36, 20),
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                                TextFormatFlags.EndEllipsis);
            }
        }
    }

    // ================================================================== 状态胶囊
    public class StatusPill : Control
    {
        public enum Tone { Ok, Warn, Bad, Info, Neutral }
        Tone _tone = Tone.Neutral;
        public Tone Kind { get { return _tone; } set { _tone = value; Invalidate(); } }

        public StatusPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(76, 22);
            Font = Ui.FSmall;
        }

        public void Set(string text, Tone tone)
        {
            Text = text; _tone = tone;
            var sz = Ui.MeasureText(text, Font);
            Width = Math.Max(56, sz.Width + 20);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg, fg;
            switch (_tone)
            {
                case Tone.Ok: bg = Ui.SuccessSoft; fg = Ui.Success; break;
                case Tone.Warn: bg = Ui.WarnSoft; fg = Ui.Warn; break;
                case Tone.Bad: bg = Ui.DangerSoft; fg = Ui.Danger; break;
                case Tone.Info: bg = Ui.AccentSoft; fg = Ui.Accent; break;
                default: bg = Ui.HoverSoft; fg = Ui.SubText; break;
            }
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            Ui.FillRound(g, new Rectangle(0, 0, Width, Height), Height / 2, bg);
            Ui.DrawText(g, Text, Font, fg, new Rectangle(0, 0, Width, Height),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ================================================================== 侧栏导航项
    public class NavItem : Control
    {
        bool _selected, _hover;
        public bool Selected { get { return _selected; } set { _selected = value; Invalidate(); } }
        public string Glyph = "●";
        public int Badge = 0;

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 40;
            Cursor = Cursors.Hand;
            Font = Ui.FBody;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Sidebar);
            var r = new Rectangle(0, 0, Width, Height);
            if (_selected) Ui.FillRound(g, new Rectangle(0, 2, Width - 0, Height - 4), 8, Ui.AccentSoft);
            else if (_hover) Ui.FillRound(g, new Rectangle(0, 2, Width, Height - 4), 8, Ui.HoverSoft);

            if (_selected)
            {
                Ui.FillRound(g, new Rectangle(0, 10, 3, Height - 20), 2, Ui.Accent);
            }

            var fg = _selected ? Ui.Accent : Ui.SubText;
            Ui.DrawText(g, Glyph, Ui.F(11f), fg, new Rectangle(14, 0, 22, Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Ui.DrawText(g, Text, _selected ? Ui.FBold : Ui.FBody, _selected ? Ui.Text : Ui.SubText,
                        new Rectangle(40, 0, Width - 56, Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                        TextFormatFlags.EndEllipsis);
            if (Badge > 0)
            {
                var t = Badge > 99 ? "99+" : Badge.ToString();
                var sz = Ui.MeasureText(t, Ui.FSmall);
                int w = sz.Width + 14;
                var br = new Rectangle(Width - w - 12, (Height - 18) / 2, w, 18);
                Ui.FillRound(g, br, 9, Ui.Accent);
                Ui.DrawText(g, t, Ui.FSmall, Color.White, br,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // ================================================================== 细进度条
    public class FlatProgress : Control
    {
        int _value;
        public int Value { get { return _value; } set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

        public FlatProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 6;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            Ui.FillRound(g, new Rectangle(0, 0, Width, Height), Height / 2, Color.FromArgb(0xE9, 0xEC, 0xF0));
            int w = (int)(Width * (_value / 100.0));
            if (w > 0) Ui.FillRound(g, new Rectangle(0, 0, Math.Max(w, Height), Height), Height / 2, Ui.Accent);
        }
    }

    // ================================================================== 分隔线
    public class HairLine : Control
    {
        public HairLine() { Height = 1; BackColor = Color.Transparent; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, 0, 0, Width, 0);
        }
    }

    // ================================================================== 悬浮提示按钮（带说明）
    public class LabelValue : Control
    {
        public string Label = "";
        string _value = "";
        Color _valueColor = Ui.Text;
        // 注意：必须是会触发重绘的属性——之前是公有字段，赋值后界面停留在旧值（启动时卡片宽度不变就不会重绘）
        public string Value
        {
            get { return _value; }
            set { if (_value != value) { _value = value; Invalidate(); } }
        }
        public Color ValueColor
        {
            get { return _valueColor; }
            set { if (_valueColor.ToArgb() != value.ToArgb()) { _valueColor = value; Invalidate(); } }
        }
        public LabelValue()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 24;
            Font = Ui.FBody;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            int lw = Math.Min(150, Width / 2);
            Ui.DrawText(e.Graphics, Label, Ui.FBody, Ui.Muted, new Rectangle(0, 0, lw, Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Ui.DrawText(e.Graphics, Value, Ui.FBody, ValueColor, new Rectangle(lw + 6, 0, Width - lw - 6, Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                        TextFormatFlags.EndEllipsis);
        }
    }
}

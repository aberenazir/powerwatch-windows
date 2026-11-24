// A flicker-free view made of "blocks" (text, cards, app rows, buttons...), laid out top to bottom.
// It redraws every few seconds without rebuilding any controls, so the panel stays smooth.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PowerWatch
{
    static class Theme
    {
        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static readonly Color Back = Color.FromArgb(32, 32, 32);
        public static readonly Color Card = Color.FromArgb(44, 44, 44);
        public static readonly Color CardHover = Color.FromArgb(54, 54, 54);
        public static readonly Color Line = Color.FromArgb(62, 62, 62);
        public static readonly Color Text = Color.FromArgb(240, 240, 240);
        public static readonly Color Dim = Color.FromArgb(160, 160, 160);
        public static readonly Color Accent = Color.FromArgb(76, 158, 255);

        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font SmallBold = new Font("Segoe UI Semibold", 8.5f);
        public static readonly Font Tiny = new Font("Segoe UI", 7.5f);
        public static readonly Font Headline = new Font("Segoe UI Semibold", 11f);
        public static readonly Font Huge = new Font("Segoe UI Semibold", 22f);
        public static readonly Font Big = new Font("Segoe UI Semibold", 13f);
        public static readonly Font Mono = new Font("Consolas", 8f);
        public static readonly Font Icons = new Font(IconFamily(), 11f);
        public static readonly Font IconsBig = new Font(IconFamily(), 15f);

        static string IconFamily()
        {
            foreach (FontFamily f in FontFamily.Families) if (f.Name == "Segoe Fluent Icons") return f.Name;
            return "Segoe MDL2 Assets";
        }

        public const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix;
        public const TextFormatFlags Line1 = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public static int Measure(string text, Font f, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            return TextRenderer.MeasureText(text, f, new Size(width, int.MaxValue), Wrap).Height;
        }

        public static int WidthOf(string text, Font f)
        {
            return TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        public static void Draw(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags flags = Wrap)
        {
            TextRenderer.DrawText(g, text, f, r, c, flags);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Max(1, radius * 2);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, Rectangle r, Color c, float radius)
        {
            using (GraphicsPath p = Rounded(r, radius))
            using (SolidBrush b = new SolidBrush(c))
            {
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, p);
                g.SmoothingMode = old;
            }
        }

        public static Color Tint(Color c, int alpha)
        {
            // blend over the background (TextRenderer needs solid colours)
            return Color.FromArgb(Back.R + (c.R - Back.R) * alpha / 255, Back.G + (c.G - Back.G) * alpha / 255, Back.B + (c.B - Back.B) * alpha / 255);
        }

        // A small pill button; returns its rectangle.
        public static Rectangle Pill(Graphics g, string label, int x, int y, bool primary, bool measureOnly = false)
        {
            int w = WidthOf(label, Small) + S(18), h = S(24);
            Rectangle r = new Rectangle(x, y, w, h);
            if (measureOnly || g == null) return r;
            Fill(g, r, primary ? Accent : Color.FromArgb(62, 62, 62), S(5));
            Draw(g, label, Small, r, primary ? Color.White : Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return r;
        }
    }

    class Hot
    {
        public Rectangle Rect;
        public Action Click;
        public Action<Point> RightClick;
    }

    abstract class Block
    {
        public int Top, Height;
        public List<Hot> Hots = new List<Hot>();
        public Color? Background;           // set by cards for their children
        public abstract int Layout(int width);
        public abstract void Paint(Graphics g, int width);
        protected Color Back { get { return Background ?? Theme.Back; } }
    }

    class Gap : Block
    {
        readonly int h;
        public Gap(int px) { h = px; }
        public override int Layout(int width) { return Theme.S(h); }
        public override void Paint(Graphics g, int width) { }
    }

    class Divider : Block
    {
        public override int Layout(int width) { return Theme.S(13); }
        public override void Paint(Graphics g, int width)
        {
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, 0, Theme.S(6), width, Theme.S(6));
        }
    }

    class TextBlock : Block
    {
        readonly string text, glyph;
        readonly Font font;
        readonly Color color, glyphColor;
        readonly int indent, after;
        public Action OnClick;

        public TextBlock(string text, Font font, Color color, string glyph = null, Color? glyphColor = null, int after = 6, int indent = 0)
        {
            this.text = text ?? ""; this.font = font; this.color = color; this.glyph = glyph;
            this.glyphColor = glyphColor ?? color; this.after = after; this.indent = indent;
        }

        int TextX { get { return Theme.S(indent) + (glyph != null ? Theme.S(20) : 0); } }

        public override int Layout(int width)
        {
            Hots.Clear();
            int h = Theme.Measure(text, font, width - TextX);
            if (OnClick != null) Hots.Add(new Hot { Rect = new Rectangle(TextX, 0, Math.Min(width - TextX, Theme.WidthOf(text, font)), h), Click = OnClick });
            return h + Theme.S(after);
        }

        public override void Paint(Graphics g, int width)
        {
            if (glyph != null) Theme.Draw(g, glyph, Theme.Icons, new Rectangle(Theme.S(indent), Theme.S(1), Theme.S(18), Theme.S(18)), glyphColor, TextFormatFlags.NoPadding);
            Theme.Draw(g, text, font, new Rectangle(TextX, 0, width - TextX, Height), color);
        }
    }

    class Card : Block
    {
        readonly List<Block> children;
        readonly Color tint;
        readonly int pad;

        public Card(Color tint, List<Block> children, int pad = 10)
        {
            this.tint = tint; this.children = children; this.pad = pad;
            foreach (Block b in children) b.Background = tint;
        }

        public override int Layout(int width)
        {
            Hots.Clear();
            int y = Theme.S(pad), inner = width - Theme.S(pad) * 2;
            foreach (Block b in children)
            {
                b.Top = y;
                b.Height = b.Layout(inner);
                foreach (Hot h in b.Hots)
                    Hots.Add(new Hot { Rect = new Rectangle(h.Rect.X + Theme.S(pad), h.Rect.Y + y, h.Rect.Width, h.Rect.Height), Click = h.Click, RightClick = h.RightClick });
                y += b.Height;
            }
            return y + Theme.S(pad) - Theme.S(4) + Theme.S(8);
        }

        public override void Paint(Graphics g, int width)
        {
            Theme.Fill(g, new Rectangle(0, 0, width, Height - Theme.S(8)), tint, Theme.S(9));
            foreach (Block b in children)
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(Theme.S(pad), b.Top);
                b.Paint(g, width - Theme.S(pad) * 2);
                g.Restore(st);
            }
        }
    }

    class Buttons : Block
    {
        readonly List<Tuple<string, Action, bool>> items = new List<Tuple<string, Action, bool>>();
        readonly List<Rectangle> rects = new List<Rectangle>();

        public Buttons Add(string label, Action click, bool primary = false)
        {
            items.Add(Tuple.Create(label, click, primary));
            return this;
        }

        public override int Layout(int width)
        {
            Hots.Clear(); rects.Clear();
            int x = 0, y = 0, rowH = Theme.S(24);
            foreach (var it in items)
            {
                Rectangle r = Theme.Pill(null, it.Item1, x, y, it.Item3, true);
                if (x > 0 && r.Right > width) { x = 0; y += rowH + Theme.S(6); r = Theme.Pill(null, it.Item1, x, y, it.Item3, true); }
                rects.Add(r);
                Hots.Add(new Hot { Rect = r, Click = it.Item2 });
                x = r.Right + Theme.S(6);
            }
            return y + rowH + Theme.S(8);
        }

        public override void Paint(Graphics g, int width)
        {
            for (int i = 0; i < items.Count; i++) Theme.Pill(g, items[i].Item1, rects[i].X, rects[i].Y, items[i].Item3);
        }
    }

    class Segmented : Block
    {
        readonly string[] options;
        readonly int selected;
        readonly Action<int> onSelect;

        public Segmented(string[] options, int selected, Action<int> onSelect)
        {
            this.options = options; this.selected = selected; this.onSelect = onSelect;
        }

        public override int Layout(int width)
        {
            Hots.Clear();
            int w = width / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                Hots.Add(new Hot { Rect = new Rectangle(i * w, 0, w, Theme.S(28)), Click = delegate { onSelect(idx); } });
            }
            return Theme.S(36);
        }

        public override void Paint(Graphics g, int width)
        {
            Theme.Fill(g, new Rectangle(0, 0, width, Theme.S(28)), Color.FromArgb(50, 50, 50), Theme.S(6));
            int w = width / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                Rectangle r = new Rectangle(i * w + Theme.S(2), Theme.S(2), w - Theme.S(4), Theme.S(24));
                if (i == selected) Theme.Fill(g, r, Color.FromArgb(78, 78, 78), Theme.S(5));
                Theme.Draw(g, options[i], i == selected ? Theme.SmallBold : Theme.Small, r, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }

    class Sparkline : Block
    {
        readonly List<double> values;
        readonly string caption;

        public Sparkline(List<double> values, string caption) { this.values = values; this.caption = caption; }

        public override int Layout(int width) { return Theme.S(36) + Theme.Measure(caption, Theme.Tiny, width) + Theme.S(6); }

        public override void Paint(Graphics g, int width)
        {
            int h = Theme.S(32);
            double top = 10;
            foreach (double v in values) top = Math.Max(top, v);
            if (values.Count >= 2)
            {
                PointF[] pts = new PointF[values.Count];
                for (int i = 0; i < values.Count; i++)
                    pts[i] = new PointF(width * i / (float)(values.Count - 1), h * (1 - (float)(values[i] / top)));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath area = new GraphicsPath())
                {
                    area.AddLine(pts[0].X, h, pts[0].X, pts[0].Y);
                    area.AddLines(pts);
                    area.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, h);
                    area.CloseFigure();
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(40, Theme.Accent))) g.FillPath(b, area);
                }
                using (Pen p = new Pen(Theme.Accent, 1.5f)) g.DrawLines(p, pts);
                g.SmoothingMode = SmoothingMode.None;
            }
            Theme.Draw(g, caption, Theme.Tiny, new Rectangle(0, Theme.S(36), width, Height), Theme.Dim);
        }
    }

    // One app in "What's using power".
    class AppRow : Block
    {
        readonly AppGroup g;
        readonly Image icon;
        readonly string advice;
        readonly Color adviceColor;
        readonly bool expanded, canQuit;
        readonly Action onToggle, onQuit;
        readonly Action<Point> onMenu;
        Rectangle quitRect;

        public AppRow(AppGroup g, Image icon, string advice, Color adviceColor, bool expanded, bool canQuit, Action onToggle, Action onQuit, Action<Point> onMenu)
        {
            this.g = g; this.icon = icon; this.advice = advice; this.adviceColor = adviceColor; this.expanded = expanded;
            this.canQuit = canQuit; this.onToggle = onToggle; this.onQuit = onQuit; this.onMenu = onMenu;
        }

        int TextX { get { return Theme.S(40); } }
        int TextW(int width) { return width - TextX - (canQuit ? Theme.S(64) : Theme.S(4)); }

        public override int Layout(int width)
        {
            Hots.Clear();
            int tw = TextW(width), y = Theme.S(6);
            y += Theme.S(19);                                                                     // name
            if (g.Tags.Count > 0) y += Theme.Measure(string.Join(" · ", g.Tags.ToArray()), Theme.Small, tw) + Theme.S(2);
            y += Theme.S(17);                                                                     // stats
            y += Theme.Measure(advice, Theme.Small, tw - Theme.S(16)) + Theme.S(2);
            if (expanded) y += Math.Min(g.Procs.Count, 30) * Theme.S(14) + Theme.S(4);
            y += Theme.S(6);
            if (canQuit)
            {
                quitRect = Theme.Pill(null, g.Kind == Kind.Cli ? "Stop" : "Quit", width - Theme.S(58), Theme.S(8), false, true);
                Hots.Add(new Hot { Rect = quitRect, Click = onQuit });
            }
            Hots.Add(new Hot { Rect = new Rectangle(0, 0, width, y), Click = onToggle, RightClick = onMenu });
            return y;
        }

        public override void Paint(Graphics gr, int width)
        {
            if (g.Warn) Theme.Fill(gr, new Rectangle(0, 0, width, Height - Theme.S(2)), Theme.Tint(Palette.Orange, 22), Theme.S(6));
            Color bg = g.Warn ? Theme.Tint(Palette.Orange, 22) : Back;
            int tw = TextW(width), y = Theme.S(6);
            if (icon != null) gr.DrawImage(icon, new Rectangle(Theme.S(6), y + Theme.S(2), Theme.S(26), Theme.S(26)));
            else Theme.Draw(gr, "", Theme.IconsBig, new Rectangle(Theme.S(8), y + Theme.S(3), Theme.S(26), Theme.S(26)), Theme.Dim, TextFormatFlags.NoPadding);

            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush dot = new SolidBrush(Palette.Level(g.Level))) gr.FillEllipse(dot, TextX, y + Theme.S(6), Theme.S(7), Theme.S(7));
            gr.SmoothingMode = SmoothingMode.None;
            string name = g.Name;
            int nameW = tw - Theme.S(12) - (g.Warn ? Theme.S(18) : 0);
            Theme.Draw(gr, name, Theme.Bold, new Rectangle(TextX + Theme.S(12), y, nameW, Theme.S(19)), Theme.Text, Theme.Line1);
            if (g.Warn)
            {
                int nw = Math.Min(nameW, Theme.WidthOf(name, Theme.Bold));
                Theme.Draw(gr, "", Theme.Icons, new Rectangle(TextX + Theme.S(16) + nw, y + Theme.S(1), Theme.S(18), Theme.S(18)), Palette.Orange, TextFormatFlags.NoPadding);
            }
            y += Theme.S(19);
            if (g.Tags.Count > 0)
            {
                string tags = string.Join(" · ", g.Tags.ToArray());
                int h = Theme.Measure(tags, Theme.Small, tw);
                Theme.Draw(gr, tags, Theme.Small, new Rectangle(TextX, y, tw, h), g.Kind == Kind.Cli || g.Warn ? Palette.Orange : Theme.Dim);
                y += h + Theme.S(2);
            }
            // share bar + numbers
            Rectangle bar = new Rectangle(TextX, y + Theme.S(5), Theme.S(60), Theme.S(6));
            Theme.Fill(gr, bar, Color.FromArgb(70, 70, 70), Theme.S(3));
            Theme.Fill(gr, new Rectangle(bar.X, bar.Y, Math.Max(Theme.S(4), (int)(bar.Width * Math.Min(1, g.Share))), bar.Height), Palette.Level(g.Level), Theme.S(3));
            string stats = (int)Math.Round(g.Share * 100) + "% of power · CPU " + (int)Math.Round(g.Cpu) + "% · " + Fmt.Mem(g.Mem);
            Theme.Draw(gr, stats, Theme.Small, new Rectangle(bar.Right + Theme.S(8), y, tw - bar.Width - Theme.S(8), Theme.S(16)), Theme.Dim, Theme.Line1);
            y += Theme.S(17);
            Theme.Draw(gr, "", Theme.Icons, new Rectangle(TextX, y, Theme.S(14), Theme.S(14)), adviceColor, TextFormatFlags.NoPadding);
            int ah = Theme.Measure(advice, Theme.Small, tw - Theme.S(16));
            Theme.Draw(gr, advice, Theme.Small, new Rectangle(TextX + Theme.S(16), y, tw - Theme.S(16), ah), adviceColor);
            y += ah + Theme.S(2);
            if (expanded)
            {
                foreach (ProcLine p in g.Procs.GetRange(0, Math.Min(30, g.Procs.Count)))
                {
                    string line = p.Pid.ToString().PadLeft(6) + "  " + p.Cpu.ToString("0.0").PadLeft(5) + "% CPU  " + (string.IsNullOrEmpty(p.Command) ? p.Name : p.Command);
                    Theme.Draw(gr, line, Theme.Mono, new Rectangle(TextX, y, width - TextX, Theme.S(14)), Theme.Dim, Theme.Line1);
                    y += Theme.S(14);
                }
            }
            if (canQuit) Theme.Pill(gr, g.Kind == Kind.Cli ? "Stop" : "Quit", quitRect.X, quitRect.Y, false);
        }
    }

    class SystemRow : Block
    {
        readonly AppGroup g;
        public SystemRow(AppGroup g) { this.g = g; }

        public override int Layout(int width)
        {
            return Theme.S(4) + Theme.S(17) + (g.Explain != null ? Theme.Measure(g.Explain, Theme.Small, width - Theme.S(130)) : 0) + Theme.S(6);
        }

        public override void Paint(Graphics gr, int width)
        {
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush dot = new SolidBrush(Palette.Level(g.Level))) gr.FillEllipse(dot, Theme.S(4), Theme.S(9), Theme.S(7), Theme.S(7));
            gr.SmoothingMode = SmoothingMode.None;
            Theme.Draw(gr, g.Name, Theme.Bold, new Rectangle(Theme.S(16), Theme.S(4), width - Theme.S(130), Theme.S(17)), Theme.Text, Theme.Line1);
            if (g.Explain != null) Theme.Draw(gr, g.Explain, Theme.Small, new Rectangle(Theme.S(16), Theme.S(21), width - Theme.S(130), Height), Theme.Dim);
            string right = (int)Math.Round(g.Share * 100) + "% · CPU " + (int)Math.Round(g.Cpu) + "%";
            Theme.Draw(gr, right, Theme.Small, new Rectangle(width - Theme.S(110), Theme.S(5), Theme.S(110), Theme.S(16)), Theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    class ModeRow : Block
    {
        readonly ModeEstimate e;
        readonly bool best;
        static readonly string[] Glyphs = { "", "", "" };
        static readonly Color[] Colors = { Palette.Blue, Palette.Green, Palette.Orange };

        public ModeRow(ModeEstimate e, bool best) { this.e = e; this.best = best; }

        int TextW(int width) { return width - Theme.S(24) - Theme.S(96); }

        public override int Layout(int width)
        {
            return Theme.S(20) + Theme.Measure(e.Note, Theme.Small, TextW(width)) + Theme.S(10);
        }

        public override void Paint(Graphics g, int width)
        {
            Theme.Draw(g, Glyphs[e.Mode], Theme.Icons, new Rectangle(0, Theme.S(2), Theme.S(18), Theme.S(18)), Colors[e.Mode], TextFormatFlags.NoPadding);
            int x = Theme.S(24);
            string name = Model.ModeNames[e.Mode];
            Theme.Draw(g, name, Theme.Bold, new Rectangle(x, 0, TextW(width), Theme.S(18)), Theme.Text, Theme.Line1);
            x += Math.Min(TextW(width), Theme.WidthOf(name, Theme.Bold)) + Theme.S(6);
            if (e.IsCurrent) x = Badge(g, "in use now", Theme.Tint(Theme.Accent, 90), x) + Theme.S(4);
            if (best) Badge(g, "best for you", Theme.Tint(Palette.Green, 90), x);
            Theme.Draw(g, e.Note, Theme.Small, new Rectangle(Theme.S(24), Theme.S(20), TextW(width), Height), Theme.Dim);
            Theme.Draw(g, e.Minutes > 0 ? Fmt.Duration(e.Minutes) : "-", Theme.Bold, new Rectangle(width - Theme.S(92), 0, Theme.S(92), Theme.S(18)), Theme.Text,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Theme.Draw(g, (e.IsCurrent ? "" : "≈ ") + e.Watts.ToString("0.0") + " W", Theme.Small, new Rectangle(width - Theme.S(92), Theme.S(19), Theme.S(92), Theme.S(16)), Theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        static int Badge(Graphics g, string text, Color c, int x)
        {
            int w = Theme.WidthOf(text, Theme.Tiny) + Theme.S(10);
            Rectangle r = new Rectangle(x, Theme.S(2), w, Theme.S(15));
            Theme.Fill(g, r, c, Theme.S(7));
            Theme.Draw(g, text, Theme.Tiny, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return r.Right;
        }
    }

    class CheckRow : Block
    {
        readonly CheckItem it;
        readonly Action<string> open;
        Rectangle button;

        public CheckRow(CheckItem it, Action<string> open) { this.it = it; this.open = open; }

        int TW(int width) { return width - Theme.S(32); }

        public override int Layout(int width)
        {
            Hots.Clear();
            int y = Theme.Measure(it.Title, Theme.Bold, TW(width)) + Theme.S(3) + Theme.Measure(it.Text, Theme.Body, TW(width));
            if (it.ButtonLabel != null)
            {
                button = Theme.Pill(null, it.ButtonLabel, Theme.S(32), y + Theme.S(6), false, true);
                Hots.Add(new Hot { Rect = button, Click = delegate { open(it.ButtonUrl); } });
                y = button.Bottom;
            }
            return y + Theme.S(14);
        }

        public override void Paint(Graphics g, int width)
        {
            Theme.Draw(g, it.Glyph, Theme.IconsBig, new Rectangle(0, 0, Theme.S(26), Theme.S(26)), it.Color, TextFormatFlags.NoPadding);
            int th = Theme.Measure(it.Title, Theme.Bold, TW(width));
            Theme.Draw(g, it.Title, Theme.Bold, new Rectangle(Theme.S(32), 0, TW(width), th), Theme.Text);
            Theme.Draw(g, it.Text, Theme.Body, new Rectangle(Theme.S(32), th + Theme.S(3), TW(width), Height), Theme.Dim);
            if (it.ButtonLabel != null) Theme.Pill(g, it.ButtonLabel, button.X, button.Y, false);
        }
    }

    class FocusRow : Block
    {
        readonly AppGroup g;
        readonly Image icon;
        readonly bool ticked;
        readonly string note, right;
        readonly Action onToggle;

        public FocusRow(AppGroup g, Image icon, bool ticked, string note, string right, Action onToggle)
        {
            this.g = g; this.icon = icon; this.ticked = ticked; this.note = note; this.right = right; this.onToggle = onToggle;
        }

        public override int Layout(int width)
        {
            Hots.Clear();
            int h = Theme.S(22) + (note != null ? Theme.S(15) : 0) + Theme.S(4);
            Hots.Add(new Hot { Rect = new Rectangle(0, 0, width, h), Click = onToggle });
            return h;
        }

        public override void Paint(Graphics gr, int width)
        {
            Rectangle box = new Rectangle(0, Theme.S(3), Theme.S(15), Theme.S(15));
            Theme.Fill(gr, box, ticked ? Theme.Accent : Color.FromArgb(70, 70, 70), Theme.S(3));
            if (ticked) Theme.Draw(gr, "", Theme.Small, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int x = Theme.S(22);
            if (icon != null) gr.DrawImage(icon, new Rectangle(x, Theme.S(1), Theme.S(19), Theme.S(19)));
            x += Theme.S(25);
            int rw = Theme.S(90);
            Theme.Draw(gr, g.Name, Theme.Bold, new Rectangle(x, Theme.S(2), width - x - rw, Theme.S(18)), Theme.Text, Theme.Line1);
            if (note != null) Theme.Draw(gr, note, Theme.Small, new Rectangle(x, Theme.S(20), width - x - rw, Theme.S(15)), Palette.Orange, Theme.Line1);
            Theme.Draw(gr, right, Theme.Small, new Rectangle(width - rw, Theme.S(3), rw, Theme.S(16)), Theme.Dim, TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    class ValueRow : Block
    {
        readonly string name, value, sub;
        public ValueRow(string name, string value, string sub) { this.name = name; this.value = value; this.sub = sub; }
        public override int Layout(int width) { return Theme.S(20); }
        public override void Paint(Graphics g, int width)
        {
            int subW = Theme.S(150), valW = Theme.S(60);
            Theme.Draw(g, name, Theme.Body, new Rectangle(0, 0, width - subW - valW, Theme.S(18)), Theme.Text, Theme.Line1);
            Theme.Draw(g, value, Theme.Bold, new Rectangle(width - subW - valW, 0, valW, Theme.S(18)), Theme.Text, TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Theme.Draw(g, sub, Theme.Small, new Rectangle(width - subW, Theme.S(1), subW, Theme.S(18)), Theme.Dim, TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    // Draws a list of blocks and passes clicks to them.
    class DocView : Control
    {
        List<Block> blocks = new List<Block>();
        int pad;

        public DocView(int padding = 14)
        {
            pad = padding;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Back;
        }

        int Pad { get { return Theme.S(pad); } }

        public void SetBlocks(List<Block> list)
        {
            blocks = list;
            Relayout();
        }

        public int Relayout()
        {
            int y = Theme.S(4), inner = Math.Max(Theme.S(100), Width - Pad * 2);
            foreach (Block b in blocks)
            {
                b.Top = y;
                b.Height = b.Layout(inner);
                y += b.Height;
            }
            int h = y + Theme.S(6);
            if (Height != h) Height = h;
            Invalidate();
            return h;
        }

        int lastWidth = -1;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (blocks.Count > 0 && Width != lastWidth) { lastWidth = Width; Relayout(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            int inner = Math.Max(Theme.S(100), Width - Pad * 2);
            foreach (Block b in blocks)
            {
                if (b.Top + b.Height < e.ClipRectangle.Top || b.Top > e.ClipRectangle.Bottom) continue;
                GraphicsState st = g.Save();
                g.TranslateTransform(Pad, b.Top);
                b.Paint(g, inner);
                g.Restore(st);
            }
        }

        Hot HotAt(Point p, bool right)
        {
            Point q = new Point(p.X - Pad, p.Y);
            foreach (Block b in blocks)
            {
                if (q.Y < b.Top || q.Y >= b.Top + b.Height) continue;
                Point local = new Point(q.X, q.Y - b.Top);
                foreach (Hot h in b.Hots)
                    if (h.Rect.Contains(local) && (right ? h.RightClick != null : h.Click != null)) return h;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = HotAt(e.Location, false) != null ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            try
            {
                if (e.Button == MouseButtons.Left) { Hot h = HotAt(e.Location, false); if (h != null) h.Click(); }
                else if (e.Button == MouseButtons.Right) { Hot h = HotAt(e.Location, true); if (h != null) h.RightClick(PointToScreen(e.Location)); }
            }
            catch (Exception ex) { Log.Write("click: " + ex); }
        }
    }
}

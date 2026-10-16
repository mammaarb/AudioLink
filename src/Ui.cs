// Small custom-drawn controls for a cleaner look than the stock WinForms ones.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AudioLink
{
    static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentLight = Color.FromArgb(232, 240, 254);
        public static readonly Color Text = Color.FromArgb(28, 28, 32);
        public static readonly Color Muted = Color.FromArgb(100, 104, 115);
        public static readonly Color Border = Color.FromArgb(218, 221, 228);
        public static readonly Color Surface = Color.FromArgb(246, 247, 249);
        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color Amber = Color.FromArgb(217, 119, 6);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color Grey = Color.FromArgb(160, 165, 175);
        public static readonly Color WarnBack = Color.FromArgb(255, 247, 237);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Button PrimaryButton(string text)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 78, 216);
            return b;
        }

        public static Button SecondaryButton(string text)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Text, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Surface;
            return b;
        }

        // Colour for a status message.
        public static Color Classify(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            if (s.Length == 0 || s == "off" || s.StartsWith("not running") || s.StartsWith("stopped")) return Grey;
            if (s.Contains("couldn't") || s.Contains("could not") || s.Contains("error") || s.Contains("refused") || s.Contains("different pairing")) return Red;
            if (s.StartsWith("playing") || s.StartsWith("sending") || s.StartsWith("connected") || s.StartsWith("controlling") ||
                s.StartsWith("in use") || s.StartsWith("receiving")) return Green;
            return Amber;
        }
    }

    abstract class PaintedControl : Control
    {
        protected PaintedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
    }

    // A big clickable choice ("Main PC" / "Laptop").
    class ModeCard : PaintedControl
    {
        public string Title = "", Description = "";
        public bool LaptopIcon;
        public event EventHandler Picked;
        bool selected, hover;
        readonly Font titleFont = new Font("Segoe UI Semibold", 10.5f), descFont = new Font("Segoe UI", 8.5f);

        public ModeCard() { Cursor = Cursors.Hand; TabStop = true; }

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Focus(); if (Enabled && Picked != null) Picked(this, e); base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(e); base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Height / 78f;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(r, 8 * s))
            {
                using (var b = new SolidBrush(selected ? Theme.AccentLight : hover ? Theme.Surface : Color.White)) g.FillPath(b, path);
                using (var p = new Pen(selected || Focused ? Theme.Accent : Theme.Border, selected ? 2f : 1f)) g.DrawPath(p, path);
            }

            // icon
            var ic = selected ? Theme.Accent : Theme.Muted;
            float ix = 16 * s, iy = Height / 2f - 12 * s;
            using (var p = new Pen(ic, 2f * s))
            {
                if (LaptopIcon)
                {
                    g.DrawRectangle(p, ix + 3 * s, iy + 2 * s, 22 * s, 14 * s);
                    g.DrawLine(p, ix, iy + 20 * s, ix + 28 * s, iy + 20 * s);
                }
                else
                {
                    g.DrawRectangle(p, ix, iy, 28 * s, 18 * s);
                    g.DrawLine(p, ix + 14 * s, iy + 18 * s, ix + 14 * s, iy + 23 * s);
                    g.DrawLine(p, ix + 8 * s, iy + 23 * s, ix + 20 * s, iy + 23 * s);
                }
            }

            // radio dot
            float rd = 14 * s, rx = Width - rd - 12 * s, ry = 12 * s;
            using (var p = new Pen(selected ? Theme.Accent : Theme.Border, 1.5f)) g.DrawEllipse(p, rx, ry, rd, rd);
            if (selected) using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, rx + 3.5f * s, ry + 3.5f * s, rd - 7 * s, rd - 7 * s);

            int tx = (int)(58 * s);
            TextRenderer.DrawText(g, Title, titleFont, new Point(tx, (int)(13 * s)), Theme.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Description, descFont, new Rectangle(tx, (int)(36 * s), Width - tx - (int)(12 * s), Height - (int)(38 * s)),
                Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Rounded "● Connected" badge.
    class StatusPill : PaintedControl
    {
        Color dot = Theme.Grey;
        readonly Font font = new Font("Segoe UI Semibold", 9f);

        public void Set(string text, Color c) { if (Text == text && dot == c) return; Text = text; dot = c; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Size ts = TextRenderer.MeasureText(Text, font);
            float s = Height / 28f;
            float w = Math.Min(Width - 2, ts.Width + 34 * s);
            var r = new RectangleF(Width - w - 1, 1, w, Height - 3);
            using (var path = Theme.Round(r, r.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(28, dot))) g.FillPath(b, path);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, r.X + 12 * s, r.Y + r.Height / 2 - 4 * s, 8 * s, 8 * s);
            TextRenderer.DrawText(g, Text, font, new Rectangle((int)(r.X + 26 * s), (int)r.Y, (int)(w - 28 * s), (int)r.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // "● Sound      playing sound from LAPTOP"
    class StatusRow : PaintedControl
    {
        public string Caption = "";
        string value = "";
        Color dot = Theme.Grey;
        readonly Font capFont = new Font("Segoe UI Semibold", 9f), valFont = new Font("Segoe UI", 9f);

        public string Value { get { return value; } }
        public void Set(string v, Color c) { if (value == v && dot == c) return; value = v; dot = c; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Height / 22f;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, 1, Height / 2f - 4 * s, 8 * s, 8 * s);
            TextRenderer.DrawText(g, Caption, capFont, new Rectangle((int)(18 * s), 0, (int)(130 * s), Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int vx = (int)(150 * s);
            TextRenderer.DrawText(g, value, valFont, new Rectangle(vx, 0, Width - vx, Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    class LevelBar : PaintedControl
    {
        float level;
        public float Value
        {
            get { return level; }
            set { float v = Math.Max(0, Math.Min(1, value)); if (Math.Abs(v - level) < 0.004f) return; level = v; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, r.Height / 2))
            using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, path);
            if (level > 0.005f)
            {
                var f = new RectangleF(0, 0, Math.Max(Height, (Width - 1) * level), Height - 1);
                using (var path = Theme.Round(f, f.Height / 2))
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
            }
        }
    }

    // Light grey rounded box that other controls sit on.
    class Card : Panel
    {
        public Color Fill = Theme.Surface;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Color.White);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8f))
            using (var b = new SolidBrush(Fill)) e.Graphics.FillPath(b, path);
        }
    }

    // Drag the laptop to the side of the PC screen where it physically sits.
    class LayoutCanvas : PaintedControl
    {
        public event EventHandler EdgeChanged;
        string edge = "Right", hoverEdge;
        string laptopName = "Laptop";
        bool dragging;
        Point grab;
        Rectangle dragRect;
        readonly Font labelFont = new Font("Segoe UI Semibold", 8.5f), smallFont = new Font("Segoe UI", 8f);

        public LayoutCanvas() { TabStop = true; }

        public string Edge { get { return edge; } set { edge = value; Invalidate(); } }
        public string LaptopName { set { string v = string.IsNullOrEmpty(value) ? "Laptop" : value; if (v != laptopName) { laptopName = v; Invalidate(); } } }

        float S { get { return Height / 150f; } }

        Rectangle PcRect()
        {
            int vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN)), vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            double aspect = Math.Max(1.25, Math.Min(2.6, vw / (double)vh));
            int ph = (int)(68 * S), pw = (int)(ph * aspect);
            var pc = new Rectangle(0, 0, pw, ph);
            var u = Rectangle.Union(pc, LaptopRect(edge, pc));
            pc.Offset((Width - u.Width) / 2 - u.X, (Height - u.Height) / 2 - u.Y);
            return pc;
        }

        Rectangle LaptopRect(string e, Rectangle pc)
        {
            int lh = (int)(50 * S), lw = (int)(lh * 1.9), gap = (int)(10 * S);
            switch (e)
            {
                case "Left": return new Rectangle(pc.X - gap - lw, pc.Y + (pc.Height - lh) / 2, lw, lh);
                case "Top": return new Rectangle(pc.X + (pc.Width - lw) / 2, pc.Y - gap - lh, lw, lh);
                case "Bottom": return new Rectangle(pc.X + (pc.Width - lw) / 2, pc.Bottom + gap, lw, lh);
                default: return new Rectangle(pc.Right + gap, pc.Y + (pc.Height - lh) / 2, lw, lh);
            }
        }

        static string Nearest(Point c, Rectangle pc)
        {
            float dx = (c.X - (pc.X + pc.Width / 2f)) / (pc.Width / 2f);
            float dy = (c.Y - (pc.Y + pc.Height / 2f)) / (pc.Height / 2f);
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? "Right" : "Left";
            return dy >= 0 ? "Bottom" : "Top";
        }

        void SetEdge(string e)
        {
            if (e == edge) return;
            edge = e;
            Invalidate();
            if (EdgeChanged != null) EdgeChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var lap = LaptopRect(edge, PcRect());
            if (Enabled && lap.Contains(e.Location))
            {
                dragging = true;
                grab = new Point(e.X - lap.X, e.Y - lap.Y);
                dragRect = lap;
                hoverEdge = edge;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pc = PcRect();
            if (dragging)
            {
                dragRect = new Rectangle(e.X - grab.X, e.Y - grab.Y, dragRect.Width, dragRect.Height);
                hoverEdge = Nearest(new Point(dragRect.X + dragRect.Width / 2, dragRect.Y + dragRect.Height / 2), pc);
                Invalidate();
            }
            else Cursor = Enabled && LaptopRect(edge, pc).Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                Capture = false;
                SetEdge(hoverEdge);
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) SetEdge("Left");
            else if (e.KeyCode == Keys.Right) SetEdge("Right");
            else if (e.KeyCode == Keys.Up) SetEdge("Top");
            else if (e.KeyCode == Keys.Down) SetEdge("Bottom");
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = S;
            using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8 * s))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                if (Focused) using (var p = new Pen(Theme.Accent, 1f)) g.DrawPath(p, path);
            }

            var pc = PcRect();
            DrawScreen(g, pc, "This PC", false, false);

            if (dragging)
            {
                var ghost = LaptopRect(hoverEdge, pc);
                using (var path = Theme.Round(ghost, 6 * s))
                using (var p = new Pen(Theme.Accent, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(p, path);
                DrawScreen(g, dragRect, laptopName, true, true);
            }
            else DrawScreen(g, LaptopRect(edge, pc), laptopName, true, false);

            TextRenderer.DrawText(g, "drag me", smallFont, new Rectangle(0, Height - (int)(22 * s), Width - (int)(10 * s), (int)(20 * s)),
                Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        void DrawScreen(Graphics g, Rectangle r, string label, bool laptop, bool lifted)
        {
            float s = S;
            var outer = new RectangleF(r.X, r.Y, r.Width, laptop ? r.Height - 6 * s : r.Height);
            if (lifted)
                using (var sh = Theme.Round(new RectangleF(outer.X + 3 * s, outer.Y + 4 * s, outer.Width, outer.Height), 6 * s))
                using (var b = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillPath(b, sh);
            using (var path = Theme.Round(outer, 6 * s))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(laptop ? Theme.Accent : Theme.Border, 1.5f)) g.DrawPath(p, path);
            }
            var inner = RectangleF.Inflate(outer, -4 * s, -4 * s);
            using (var path = Theme.Round(inner, 3 * s))
            using (var b = new SolidBrush(laptop ? Theme.AccentLight : Color.FromArgb(236, 238, 242))) g.FillPath(b, path);
            if (laptop)
            {
                var deck = new RectangleF(r.X - 5 * s, r.Bottom - 6 * s, r.Width + 10 * s, 5 * s);
                using (var path = Theme.Round(deck, 2.5f * s))
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, label, labelFont, Rectangle.Round(inner), laptop ? Theme.Accent : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Live view of what AudioLink is doing.
    class LogForm : Form
    {
        readonly TextBox box;

        public LogForm(Icon icon)
        {
            Text = "AudioLink log";
            Icon = icon;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false,
                Font = new Font("Consolas", 9f), BackColor = Color.White, BorderStyle = BorderStyle.None
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Theme.Surface };
            var copy = Theme.SecondaryButton("Copy all");
            copy.SetBounds(12, 10, 100, 28);
            copy.Click += delegate { if (box.TextLength > 0) Clipboard.SetText(box.Text); };
            var open = Theme.SecondaryButton("Open log file");
            open.SetBounds(120, 10, 120, 28);
            open.Click += delegate { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Net.LogFile + "\""); } catch { } };
            bar.Controls.AddRange(new Control[] { copy, open });
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 4, 4), BackColor = Color.White };
            pad.Controls.Add(box);
            Controls.Add(pad);
            Controls.Add(bar);
            box.Text = string.Join("\r\n", Net.RecentLines()) + "\r\n";
            Net.LineAdded += OnLine;
            FormClosed += delegate { Net.LineAdded -= OnLine; };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }

        void OnLine(string line)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((Action)(() => box.AppendText(line + "\r\n"))); } catch { }
        }
    }
}

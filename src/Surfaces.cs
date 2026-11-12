using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class Shapes
    {
        public static GraphicsPath Round(RectangleF box, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
            path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
        public static void Drag(Form form)
        {
            Native.ReleaseCapture();
            Native.SendMessage(form.Handle, 0xa1, new IntPtr(2), IntPtr.Zero);
        }
    }

    internal class RoundedPanel : Panel
    {
        public int Radius = 14;
        public Color BorderColor = Color.Transparent;
        public Color Fill = Color.White;
        public RoundedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius))
            using (var fill = new SolidBrush(Fill))
            using (var border = new Pen(BorderColor))
            {
                e.Graphics.FillPath(fill, path);
                if (BorderColor.A > 0) e.Graphics.DrawPath(border, path);
            }
            base.OnPaint(e);
        }
    }

    internal class RoundedForm : Form
    {
        public bool ResizeEdges = true;
        private bool systemCorners;
        private bool calculatingClientSize;
        protected virtual int CornerRadius { get { return 14; } }
        protected override bool ShowWithoutActivation { get { return Ui.RenderOffscreen; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                if (ResizeEdges && !calculatingClientSize) value.Style |= 0x00040000; // WS_THICKFRAME enables native sizing.
                return value;
            }
        }
        protected RoundedForm()
        {
            // Derived forms resume after adding controls, so the initial DPI pass scales the whole tree.
            SuspendLayout();
            Ui.Setup(this);
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Padding = new Padding(7);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Padding = new Padding(Ui.Scale(this, 7));
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Regions have hard pixel edges and prevent DWM rounding. Unsupported systems keep a plain rectangle.
            int preference = CornerRadius == 0 ? 1 : 2; // DWMWCP_DONOTROUND / DWMWCP_ROUND
            systemCorners = Native.DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)) >= 0 && CornerRadius != 0;
            if (systemCorners)
            {
                int border = 205 | 207 << 8 | 211 << 16;
                Native.DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            }
        }
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value);
            if (!value || !Visible || !IsHandleCreated) return;
            // VisibleChanged runs before the native show finishes; painting there can validate an invisible surface.
            // Commit the complete parent and child surface only after WinForms has actually shown the window.
            Invalidate(true);
            Update();
        }
        protected override void SetClientSizeCore(int width, int height)
        {
            // WinForms otherwise adds a frame that WM_NCCALCSIZE removes, corrupting its client-size cache.
            bool previous = calculatingClientSize;
            calculatingClientSize = true;
            try { base.SetClientSizeCore(width, height); }
            finally { calculatingClientSize = previous; }
        }
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            bool previous = calculatingClientSize;
            calculatingClientSize = true;
            try { base.SetBoundsCore(x, y, width, height, specified); }
            finally { calculatingClientSize = previous; }
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            bool previous = calculatingClientSize;
            calculatingClientSize = true;
            try { base.ScaleControl(factor, specified); }
            finally { calculatingClientSize = previous; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!systemCorners && CornerRadius != 0)
                using (var pen = new Pen(Color.FromArgb(205, 207, 211))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        protected override void WndProc(ref Message m)
        {
            // Keep the native sizing behavior while drawing the entire frame ourselves.
            if (m.Msg == 0x83 && ResizeEdges) { m.Result = IntPtr.Zero; return; }
            if (ResizeEdges && WindowState != FormWindowState.Minimized)
            {
                // DefWindowProc paints a thick frame here even after WM_NCCALCSIZE removed it.
                if (m.Msg == 0x86) { m.Result = new IntPtr(1); return; } // WM_NCACTIVATE must allow deactivation.
                if (m.Msg == 0x85) { m.Result = IntPtr.Zero; return; } // WM_NCPAINT: client painting owns the frame.
            }
            bool previous = calculatingClientSize;
            // Form also converts cached client bounds when processing WM_WINDOWPOSCHANGED.
            if (m.Msg == 0x47) calculatingClientSize = true;
            try { base.WndProc(ref m); }
            finally { calculatingClientSize = previous; }
            if (m.Msg != 0x84 || !ResizeEdges || m.Result.ToInt32() != 1) return;
            long packed = m.LParam.ToInt64();
            Point point = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            int grip = Math.Min(Math.Max(7, (int)Math.Round(7.0 * DeviceDpi / 96)), Math.Min(ClientSize.Width, ClientSize.Height) / 3);
            bool left = point.X < grip, right = point.X >= ClientSize.Width - grip, top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
            if (top) m.Result = new IntPtr(left ? 13 : right ? 14 : 12);
            else if (bottom) m.Result = new IntPtr(left ? 16 : right ? 17 : 15);
            else if (left) m.Result = new IntPtr(10);
            else if (right) m.Result = new IntPtr(11);
        }
    }

    internal sealed class GlyphButton : Control
    {
        private static readonly ConditionalWeakTable<Form, ToolTip> windowHints = new ConditionalWeakTable<Form, ToolTip>();
        private ToolTip hints;
        private string hint;
        public string Glyph;
        public bool Selected, Primary;
        public Color Swatch { get; set; }
        private bool hover, pressed, keyboardFocus;
        public GlyphButton(string glyph, string title, Action action, bool focusable = true)
        {
            Glyph = glyph; Text = ""; AccessibleName = hint = title;
            AccessibleRole = AccessibleRole.PushButton;
            Size = new Size(40, 40);
            Cursor = Cursors.Hand;
            TabStop = focusable;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            SetStyle(ControlStyles.Selectable, focusable);
            if (!focusable) SetStyle(ControlStyles.UserMouse, true);
            BackColor = Color.Transparent;
            Click += delegate { action(); };
            MouseEnter += delegate { UpdateHint(); hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
        public void SetHint(string title) { AccessibleName = hint = title; UpdateHint(); }
        private static ToolTip CreateHints(Form owner)
        {
            var value = new ToolTip { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 12000, ShowAlways = true };
            owner.Disposed += delegate { windowHints.Remove(owner); value.Dispose(); };
            return value;
        }
        private void UpdateHint()
        {
            Form owner = FindForm();
            ToolTip next = owner == null || owner.IsDisposed ? null : windowHints.GetValue(owner, CreateHints);
            if (hints != next)
            {
                if (hints != null) hints.SetToolTip(this, null);
                hints = next;
            }
            if (hints != null) hints.SetToolTip(this, hint);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateHint(); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); UpdateHint(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { keyboardFocus = false; pressed = true; Capture = true; if (CanSelect) Focus(); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); bool click = pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location); pressed = false; Capture = false; Invalidate(); if (click) PerformClick(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); keyboardFocus = true; if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; PerformClick(); } Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing && hints != null) { hints.SetToolTip(this, null); hints = null; } base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? Ui.Accent : Selected ? Color.FromArgb(232, 238, 255) : hover || pressed ? Color.FromArgb(234, 235, 238) : Color.Transparent;
            if (fill.A > 0)
                using (var path = Shapes.Round(new RectangleF(1, 1, Width - 2, Height - 2), Math.Min(12, Height / 3)))
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            float inset = Math.Max(7, Width * .23f);
            if (!Swatch.IsEmpty)
            {
                using (var brush = new SolidBrush(Swatch)) e.Graphics.FillEllipse(brush, 6, 6, Width - 12, Height - 12);
                using (var pen = new Pen(Selected ? Ui.Accent : Color.FromArgb(200, 202, 209), Selected ? 2 : 1)) e.Graphics.DrawEllipse(pen, 4, 4, Width - 8, Height - 8);
            }
            else Glyphs.Draw(e.Graphics, Glyph, new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), Enabled ? Primary ? Color.White : Selected ? Ui.Accent : Color.FromArgb(70, 73, 78) : Color.Gray);
            if (Focused && keyboardFocus) using (var path = Shapes.Round(new RectangleF(2, 2, Width - 4, Height - 4), 8)) using (var pen = new Pen(Color.FromArgb(149, 171, 242))) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class RoundedButton : Button
    {
        private bool hover, pressed;
        internal RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UseVisualStyleBackColor = false;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { pressed = false; Invalidate(); } }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var panel = Parent as RoundedPanel;
            e.Graphics.Clear(panel != null ? panel.Fill : Parent == null ? Ui.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Ui.Background : pressed ? ControlPaint.Dark(BackColor, .08f) : hover ? ControlPaint.Light(BackColor, .08f) : BackColor;
            using (var path = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Ui.Scale(this, 8)))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
                if (FlatAppearance.BorderSize > 0)
                    using (var pen = new Pen(Enabled ? FlatAppearance.BorderColor : Color.FromArgb(220, 222, 227))) e.Graphics.DrawPath(pen, path);
                if (Focused && ShowFocusCues)
                    using (var focus = Shapes.Round(new RectangleF(3, 3, Math.Max(1, Width - 6), Math.Max(1, Height - 6)), Ui.Scale(this, 6)))
                    using (var pen = new Pen(ForeColor) { DashStyle = DashStyle.Dot }) e.Graphics.DrawPath(pen, focus);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            AutoSize = false; Size = new Size(46, 28); Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            CheckedChanged += delegate { Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = Math.Min(Width / 46f, Height / 28f);
            var track = new RectangleF((Width - 44 * unit) / 2, (Height - 24 * unit) / 2, 44 * unit, 24 * unit);
            using (var path = Shapes.Round(track, track.Height / 2))
            using (var brush = new SolidBrush(Enabled && Checked ? Ui.Accent : Color.FromArgb(205, 208, 214))) e.Graphics.FillPath(brush, path);
            e.Graphics.FillEllipse(Brushes.White, track.X + (Checked ? 22 : 2) * unit, track.Y + 2 * unit, 20 * unit, 20 * unit);
            if (Focused && ShowFocusCues) using (var path = Shapes.Round(track, track.Height / 2)) using (var pen = new Pen(Color.FromArgb(133, 156, 225), Math.Max(1, unit))) e.Graphics.DrawPath(pen, path);
        }
    }

    internal static class Glyphs
    {
        public static void Draw(Graphics g, string name, RectangleF box, Color color)
        {
            var saved = g.Save();
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(box.Width / 24, box.Height / 24);
            using (var p = new Pen(color, 1.8f))
            using (var b = new SolidBrush(color))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                switch (name)
                {
                    case "close": g.DrawLine(p, 5, 5, 19, 19); g.DrawLine(p, 19, 5, 5, 19); break;
                    case "search": g.DrawEllipse(p, 3, 3, 13, 13); g.DrawLine(p, 14, 14, 21, 21); break;
                    case "open": g.DrawLines(p, new[] { new Point(10, 4), new Point(3, 4), new Point(3, 21), new Point(20, 21), new Point(20, 14) }); g.DrawLines(p, new[] { new Point(14, 3), new Point(21, 3), new Point(21, 10) }); g.DrawLine(p, 11, 13, 21, 3); break;
                    case "image": g.DrawRectangle(p, 3, 4, 18, 16); g.DrawEllipse(p, 7, 7, 3, 3); g.DrawLines(p, new[] { new Point(4, 18), new Point(10, 12), new Point(14, 16), new Point(18, 11), new Point(21, 14) }); break;
                    case "trash": g.DrawLine(p, 4, 6, 20, 6); g.DrawLines(p, new[] { new Point(9, 5), new Point(9, 3), new Point(15, 3), new Point(15, 5) }); g.DrawLines(p, new[] { new Point(6, 7), new Point(7, 21), new Point(17, 21), new Point(18, 7) }); g.DrawLine(p, 10, 10, 10, 17); g.DrawLine(p, 14, 10, 14, 17); break;
                    case "min": g.DrawLine(p, 5, 15, 19, 15); break;
                    case "info":
                        g.DrawEllipse(p, 3, 3, 18, 18); g.FillEllipse(b, 10.8f, 6.5f, 2.4f, 2.4f);
                        g.DrawLines(p, new[] { new Point(10, 11), new Point(12, 11), new Point(12, 17) }); g.DrawLine(p, 10, 17, 14, 17); break;
                    case "eye":
                    case "eye-off":
                        g.DrawBezier(p, 2, 12, 7, 4, 17, 4, 22, 12); g.DrawBezier(p, 22, 12, 17, 20, 7, 20, 2, 12); g.DrawEllipse(p, 9, 9, 6, 6);
                        if (name == "eye-off") g.DrawLine(p, 4, 4, 20, 20);
                        break;
                    case "copy": g.DrawRectangle(p, 8, 3, 12, 14); g.DrawLines(p, new[] { new Point(4, 7), new Point(4, 21), new Point(16, 21) }); break;
                    case "paste": g.DrawRectangle(p, 5, 5, 14, 16); g.FillRectangle(Brushes.White, 8, 3, 8, 4); g.DrawRectangle(p, 8, 3, 8, 4); break;
                    case "settings":
                        for (int i = 0; i < 3; i++) { int y = 5 + i * 7; g.DrawLine(p, 3, y, 21, y); g.FillRectangle(Brushes.White, i == 1 ? 7 : 14, y - 3, 3, 6); g.DrawLine(p, i == 1 ? 8 : 15, y - 3, i == 1 ? 8 : 15, y + 3); } break;
                    case "pin": g.DrawLines(p, new[] { new Point(8, 3), new Point(16, 3), new Point(15, 12), new Point(18, 15), new Point(6, 15), new Point(9, 12), new Point(8, 3) }); g.DrawLine(p, 12, 15, 12, 22); break;
                    case "rect": g.DrawRectangle(p, 3, 5, 18, 14); break;
                    case "ellipse": g.DrawEllipse(p, 3, 3, 18, 18); break;
                    case "line": g.DrawLine(p, 3, 12, 21, 12); break;
                    case "plus": g.DrawLine(p, 5, 12, 19, 12); g.DrawLine(p, 12, 5, 12, 19); break;
                    case "arrow": g.DrawLine(p, 3, 12, 21, 12); g.DrawLines(p, new[] { new Point(16, 7), new Point(21, 12), new Point(16, 17) }); break;
                    case "pen": g.DrawLines(p, new[] { new Point(4, 18), new Point(3, 22), new Point(8, 21), new Point(21, 7), new Point(17, 3), new Point(4, 18) }); g.DrawLine(p, 15, 5, 19, 9); break;
                    case "brush": g.DrawCurve(p, new[] { new PointF(3, 5), new PointF(8, 3), new PointF(6, 12), new PointF(16, 6), new PointF(18, 13), new PointF(8, 20), new PointF(21, 19) }); break;
                    case "mosaic": for (int x = 4; x <= 20; x += 4) for (int y = 4; y <= 20; y += 4) g.FillRectangle(b, x, y, 2, 2); break;
                    case "crop": g.DrawLines(p, new[] { new Point(7, 2), new Point(7, 17), new Point(22, 17) }); g.DrawLines(p, new[] { new Point(2, 7), new Point(17, 7), new Point(17, 22) }); break;
                    case "text": g.DrawLine(p, 4, 4, 20, 4); g.DrawLine(p, 12, 4, 12, 21); g.DrawLine(p, 8, 21, 16, 21); break;
                    case "bold": using (var font = new Font("Segoe UI", 21, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("B", font, b, new PointF(3, -2)); break;
                    case "undo": g.DrawArc(p, 5, 6, 16, 14, 205, 285); g.DrawLines(p, new[] { new Point(4, 4), new Point(4, 11), new Point(11, 11) }); break;
                    case "redo": g.ScaleTransform(-1, 1); g.TranslateTransform(-24, 0); g.DrawArc(p, 5, 6, 16, 14, 205, 285); g.DrawLines(p, new[] { new Point(4, 4), new Point(4, 11), new Point(11, 11) }); break;
                    case "move": g.DrawLine(p, 12, 2, 12, 22); g.DrawLine(p, 2, 12, 22, 12); g.DrawLines(p, new[] { new Point(8, 6), new Point(12, 2), new Point(16, 6) }); g.DrawLines(p, new[] { new Point(6, 8), new Point(2, 12), new Point(6, 16) }); break;
                    case "ocr": g.DrawLines(p, new[] { new Point(3, 8), new Point(3, 3), new Point(8, 3) }); g.DrawLines(p, new[] { new Point(16, 3), new Point(21, 3), new Point(21, 8) }); g.DrawLines(p, new[] { new Point(21, 16), new Point(21, 21), new Point(16, 21) }); g.DrawLines(p, new[] { new Point(8, 21), new Point(3, 21), new Point(3, 16) }); g.DrawLine(p, 8, 9, 16, 9); g.DrawLine(p, 12, 9, 12, 17); break;
                    case "save": g.DrawRectangle(p, 4, 3, 16, 18); g.DrawRectangle(p, 8, 3, 8, 6); g.DrawRectangle(p, 8, 14, 8, 7); break;
                    case "scroll": g.DrawRectangle(p, 5, 2, 14, 20); g.DrawLine(p, 12, 6, 12, 18); g.DrawLines(p, new[] { new Point(8, 14), new Point(12, 18), new Point(16, 14) }); break;
                    case "gif": g.DrawRectangle(p, 2, 4, 20, 16); g.DrawEllipse(p, 9, 9, 6, 6); g.DrawLine(p, 5, 4, 5, 20); g.DrawLine(p, 19, 4, 19, 20); break;
                    case "swap": g.DrawLine(p, 3, 8, 20, 8); g.DrawLines(p, new[] { new Point(16, 4), new Point(20, 8), new Point(16, 12) }); g.DrawLine(p, 21, 16, 4, 16); g.DrawLines(p, new[] { new Point(8, 12), new Point(4, 16), new Point(8, 20) }); break;
                    case "chevron": g.DrawLines(p, new[] { new Point(6, 9), new Point(12, 15), new Point(18, 9) }); break;
                    case "stop": g.FillRectangle(b, 6, 6, 12, 12); break;
                    case "translate":
                        using (var font = new Font("Microsoft YaHei UI", 14, FontStyle.Regular, GraphicsUnit.Pixel)) g.DrawString("文", font, b, new PointF(0, 0));
                        using (var font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel)) g.DrawString("A", font, b, new PointF(13, 12));
                        break;
                    case "history":
                        g.DrawArc(p, 4, 3, 17, 18, 210, 300); g.DrawLines(p, new[] { new Point(3, 3), new Point(3, 9), new Point(9, 9) });
                        g.DrawLines(p, new[] { new Point(12, 7), new Point(12, 13), new Point(16, 15) }); break;
                    case "image-translate":
                        g.DrawLines(p, new[] { new Point(10, 20), new Point(2, 20), new Point(2, 3), new Point(22, 3), new Point(22, 10) });
                        g.DrawEllipse(p, 6, 6, 3, 3); g.DrawLines(p, new[] { new Point(3, 17), new Point(9, 11), new Point(13, 15) });
                        g.DrawLines(p, new[] { new Point(13, 22), new Point(17, 12), new Point(21, 22) }); g.DrawLine(p, 15, 18, 19, 18);
                        break;
                    case "general": for (int x = 5; x <= 19; x += 7) for (int y = 5; y <= 19; y += 7) g.FillEllipse(b, x - 1.5f, y - 1.5f, 3, 3); break;
                    case "palette":
                        g.DrawEllipse(p, 3, 3, 18, 18); g.FillEllipse(Brushes.IndianRed, 6, 7, 4, 4); g.FillEllipse(Brushes.RoyalBlue, 13, 6, 4, 4); g.FillEllipse(Brushes.SeaGreen, 14, 13, 4, 4); g.FillEllipse(Brushes.Goldenrod, 6, 14, 4, 4); break;
                    case "keyboard": g.DrawRectangle(p, 2, 5, 20, 14); for (int x = 6; x <= 18; x += 4) for (int y = 9; y <= 12; y += 3) g.DrawLine(p, x, y, x + 1, y); g.DrawLine(p, 7, 16, 17, 16); break;
                    default:
                        using (var font = new Font("Microsoft YaHei UI", name == "translate" ? 12 : 10, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString(name == "translate" ? "文A" : name, font, b, new RectangleF(0, 2, 25, 22));
                        break;
                }
            }
            g.Restore(saved);
        }
    }
}

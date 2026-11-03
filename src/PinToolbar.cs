using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    // A separate owned tool window keeps controls out of the screenshot and its text hit testing.
    internal sealed class PinToolbar : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000080; return value; } // NOACTIVATE | TOOLWINDOW
        }
        internal PinToolbar(GlyphButton button)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            DoubleBuffered = true;
            Padding = new Padding(1);
            button.Dock = DockStyle.Fill;
            Controls.Add(button);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var border = new Pen(Color.FromArgb(210, 215, 224)))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            e.Cancel = true; // Only the image owner positions/sizes this toolbar; never accept a second scaling pass.
            base.OnDpiChanged(e);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
        internal static Rectangle Place(Rectangle image, Rectangle area, int side, int gap)
        {
            int x = Math.Max(area.Left, Math.Min(image.Right - side, area.Right - side));
            int y = Math.Max(area.Top, Math.Min(image.Top, area.Bottom - side));
            var candidates = new[] {
                new Rectangle(x, image.Top - side - gap, side, side),
                new Rectangle(image.Right + gap, y, side, side),
                new Rectangle(image.Left - side - gap, y, side, side),
                new Rectangle(x, image.Bottom + gap, side, side)
            };
            foreach (Rectangle candidate in candidates)
                if (area.Contains(candidate) && !image.IntersectsWith(candidate)) return candidate;
            // A screen-filling image has no outside space; Space and the context menu remain available.
            return Rectangle.Empty;
        }
    }
}

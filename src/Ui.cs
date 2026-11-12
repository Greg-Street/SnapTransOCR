using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class Ui
    {
        internal static bool RenderOffscreen = false;
        public static readonly Color Ink = Color.FromArgb(27, 28, 31);
        public static readonly Color Muted = Color.FromArgb(119, 120, 124);
        public static readonly Color Accent = Color.FromArgb(27, 73, 241);
        public static readonly Color Background = Color.FromArgb(245, 245, 246);
        public static readonly Font Font = new Font("Microsoft YaHei UI", 10F);
        public static void Setup(Form form)
        {
            form.Font = Font;
            form.ForeColor = Ink;
            form.BackColor = Background;
            form.AutoScaleDimensions = new SizeF(96, 96);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.Icon = Program.AppIcon;
        }
        public static Button Button(string text, Action action, bool primary = false)
        {
            var button = new RoundedButton { Text = text, AutoSize = true, MinimumSize = new Size(84, 34), Padding = new Padding(10, 3, 10, 3), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new Padding(4) };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(219, 226, 231);
            button.Click += delegate { action(); };
            return button;
        }
        public static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 8, 4, 5) };
        }
        public static ComboBox Combo(string[] choices, int selected)
        {
            var combo = new StyledComboBox { Width = 170, Margin = new Padding(4) };
            combo.Items.AddRange(choices);
            combo.SelectedIndex = selected;
            return combo;
        }
        public static int Scale(Control control, int value) { return (int)Math.Round(value * control.DeviceDpi / 96.0); }
        public static Button TextAction(string text, Action action)
        {
            var button = new RoundedButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9F), AutoSize = false };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { action(); };
            return button;
        }
        public static void SetWordWrap(RichTextBox text, bool enabled)
        {
            text.WordWrap = enabled;
            text.ScrollBars = enabled ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
        }
        public static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(12, 5, 12, 5), BackColor = Color.White };
        }
        public static void Error(IWin32Window owner, Exception exception)
        {
            MessageBox.Show(owner, exception is InvalidOperationException || exception is FormatException || exception is System.IO.IOException || exception is UnauthorizedAccessException ? exception.Message : "操作未完成（" + exception.GetType().Name + "），请重试。", "SnapTransOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        public static void NearCursor(Form form)
        {
            if (RenderOffscreen) { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); return; }
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(Math.Max(area.Left, Math.Min(Cursor.Position.X + 16, area.Right - form.Width)), Math.Max(area.Top, Math.Min(Cursor.Position.Y + 16, area.Bottom - form.Height)));
        }
    }
}

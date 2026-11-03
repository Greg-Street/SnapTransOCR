using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class TextInputField : RoundedPanel
    {
        private readonly TextBox input;
        private readonly GlyphButton visibility;
        public TextInputField(TextBox text)
        {
            input = text;
            input.BorderStyle = BorderStyle.None;
            input.BackColor = Color.White;
            Fill = Color.White; BorderColor = Color.FromArgb(216, 220, 227); Radius = 6;
            Size = new Size(250, 34); TabStop = false;
            if (input.UseSystemPasswordChar)
            {
                visibility = new GlyphButton("eye", "显示凭据", delegate
                {
                    int start = input.SelectionStart, length = input.SelectionLength;
                    input.UseSystemPasswordChar = !input.UseSystemPasswordChar;
                    input.Select(start, length);
                    visibility.Glyph = input.UseSystemPasswordChar ? "eye" : "eye-off";
                    visibility.SetHint(input.UseSystemPasswordChar ? "显示凭据" : "隐藏凭据");
                    visibility.Invalidate();
                }) { BackColor = Color.White };
                Controls.Add(visibility);
            }
            Controls.Add(input);
            input.FontChanged += delegate { PerformLayout(); };
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) input.Focus(); };
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (input == null) return;
            int inset = Ui.Scale(this, 10), button = Ui.Scale(this, 28), gap = Ui.Scale(this, 4);
            Radius = Ui.Scale(this, 6);
            Height = Math.Max(Ui.Scale(this, 34), input.PreferredHeight + Ui.Scale(this, 12));
            int right = visibility == null ? inset : button + gap * 2;
            input.SetBounds(inset, (Height - input.PreferredHeight) / 2, Math.Max(1, Width - inset - right), input.PreferredHeight);
            if (visibility != null) visibility.SetBounds(Width - button - gap, (Height - button) / 2, button, button);
        }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); BorderColor = Ui.Accent; Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); BorderColor = Color.FromArgb(216, 220, 227); Invalidate(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
    }
}

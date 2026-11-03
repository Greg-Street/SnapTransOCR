using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class TextInputField : RoundedPanel
    {
        private readonly TextBox input;
        private readonly GlyphButton visibility;
        private readonly GlyphButton clear;
        private readonly string leadingGlyph;
        public TextInputField(TextBox text, string leadingGlyph = null, bool clearable = false)
        {
            input = text;
            this.leadingGlyph = leadingGlyph;
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
            if (clearable)
            {
                clear = new GlyphButton("close", "清除搜索", delegate { input.Clear(); input.Focus(); }) { BackColor = Color.White, Visible = input.TextLength > 0 };
                Controls.Add(clear);
                input.TextChanged += delegate { clear.Visible = input.TextLength > 0; PerformLayout(); };
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
            int right = visibility == null && (clear == null || input.TextLength == 0) ? inset : button + gap * 2;
            int left = leadingGlyph == null ? inset : Ui.Scale(this, 36);
            input.SetBounds(left, (Height - input.PreferredHeight) / 2, Math.Max(1, Width - left - right), input.PreferredHeight);
            if (visibility != null) visibility.SetBounds(Width - button - gap, (Height - button) / 2, button, button);
            if (clear != null) clear.SetBounds(Width - button - gap, (Height - button) / 2, button, button);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (leadingGlyph != null)
            {
                int size = Ui.Scale(this, 18);
                Glyphs.Draw(e.Graphics, leadingGlyph, new RectangleF(Ui.Scale(this, 11), (Height - size) / 2, size, size), ContainsFocus ? Ui.Accent : Ui.Muted);
            }
        }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); BorderColor = Ui.Accent; Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); BorderColor = Color.FromArgb(216, 220, 227); Invalidate(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
    }
}

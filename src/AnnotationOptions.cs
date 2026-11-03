using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal readonly struct AnnotationStyle
    {
        internal readonly Color Color;
        internal readonly int Stroke, TextSize;
        internal readonly bool TextBold;
        internal AnnotationStyle(Color color, int stroke, int textSize, bool textBold)
        {
            Color = color; Stroke = Math.Max(1, Math.Min(20, stroke));
            TextSize = Math.Max(8, Math.Min(144, textSize)); TextBold = textBold;
        }
        internal AnnotationStyle(AppSettings settings) : this(System.Drawing.Color.FromArgb(settings.CaptureColorArgb), settings.CaptureStroke, settings.CaptureTextSize, settings.CaptureTextBold) { }
    }

    internal sealed class AnnotationOptions : FlowLayoutPanel
    {
        private ImageCanvas canvas;
        private Color color;
        private readonly NumericUpDown stroke, size;
        private readonly GlyphButton bold;
        private readonly List<GlyphButton> swatches = new List<GlyphButton>();
        private readonly List<Size> controlSizes = new List<Size>();
        private readonly List<Padding> controlMargins = new List<Padding>();
        private Font scaledFont;
        private float currentScale;
        internal AnnotationStyle Style { get { return new AnnotationStyle(color, (int)stroke.Value, (int)size.Value, bold.Selected); } }
        public AnnotationOptions(float initialStroke) : this(new AnnotationStyle(Color.FromArgb(239, 74, 64), (int)Math.Round(initialStroke), 24, false)) { }
        public AnnotationOptions(AppSettings settings) : this(new AnnotationStyle(settings)) { }
        internal AnnotationOptions(AnnotationStyle initial)
        {
            WrapContents = true; BackColor = Color.White; Padding = new Padding(4, 3, 4, 3); AutoSize = true;
            foreach (Color preset in new[] { Color.FromArgb(239, 74, 64), Ui.Accent, Color.FromArgb(0, 155, 98), Color.FromArgb(255, 185, 0), Color.Black, Color.White })
            {
                Color chosen = preset;
                var button = new GlyphButton("", "颜色", delegate { SetColor(chosen); if (canvas != null) canvas.FocusText(); }) { Swatch = preset, Size = new Size(30, 30), Margin = new Padding(1, 3, 1, 0), BackColor = Color.White };
                swatches.Add(button); Controls.Add(button);
            }
            Controls.Add(new GlyphButton("palette", "自定义颜色", delegate
            {
                using (var dialog = new ColorDialog { Color = color, FullOpen = true, AnyColor = true }) if (dialog.ShowDialog(FindForm()) == DialogResult.OK) SetColor(dialog.Color);
                if (canvas != null) canvas.FocusText();
            }) { Size = new Size(34, 34), Margin = new Padding(2, 1, 12, 0), BackColor = Color.White });
            Controls.Add(new Label { Text = "线宽", AutoSize = true, Margin = new Padding(4, 9, 5, 0), ForeColor = Ui.Muted, Font = Ui.Font });
            stroke = Number(1, 20, initial.Stroke);
            Controls.Add(stroke);
            Controls.Add(new Label { Text = "字号", AutoSize = true, Margin = new Padding(12, 9, 5, 0), ForeColor = Ui.Muted, Font = Ui.Font });
            size = Number(8, 144, initial.TextSize); Controls.Add(size);
            bold = new GlyphButton("bold", "粗体", delegate { bold.Selected = !bold.Selected; bold.Invalidate(); Apply(); if (canvas != null) canvas.FocusText(); }) { Selected = initial.TextBold, Size = new Size(34, 34), Margin = new Padding(7, 1, 1, 0), BackColor = Color.White };
            Controls.Add(bold);
            stroke.ValueChanged += delegate { Apply(); }; size.ValueChanged += delegate { Apply(); };
            SetColor(initial.Color);
            foreach (Control control in Controls) { controlSizes.Add(control.Size); controlMargins.Add(control.Margin); }
        }
        private static NumericUpDown Number(decimal minimum, decimal maximum, decimal initial)
        {
            return new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = initial, Width = 68, Font = Ui.Font, Margin = new Padding(0, 5, 0, 0), AccessibleName = maximum == 20 ? "线宽" : "字号" };
        }
        private void SetColor(Color value) { color = value; foreach (var swatch in swatches) { swatch.Selected = swatch.Swatch.ToArgb() == value.ToArgb(); swatch.Invalidate(); } Apply(); }
        private void Apply() { if (canvas == null) return; canvas.Color = color; canvas.Stroke = (float)stroke.Value; canvas.TextSize = (float)size.Value; canvas.TextBold = bold.Selected; }
        public void Bind(ImageCanvas value) { canvas = value; Apply(); }
        internal void SetScale(float scale)
        {
            if (Math.Abs(scale - currentScale) < .01f) return;
            currentScale = scale;
            Font previous = scaledFont;
            scaledFont = new Font(Ui.Font.FontFamily, Ui.Font.SizeInPoints * 96f / 72f * scale, Ui.Font.Style, GraphicsUnit.Pixel);
            SuspendLayout();
            Padding = new Padding((int)(4 * scale), (int)(3 * scale), (int)(4 * scale), (int)(3 * scale));
            for (int i = 0; i < Controls.Count; i++)
            {
                Control control = Controls[i];
                control.Font = scaledFont;
                control.Size = new Size((int)(controlSizes[i].Width * scale), (int)(controlSizes[i].Height * scale));
                Padding margin = controlMargins[i];
                control.Margin = new Padding((int)(margin.Left * scale), (int)(margin.Top * scale), (int)(margin.Right * scale), (int)(margin.Bottom * scale));
            }
            ResumeLayout(true);
            if (previous != null) previous.Dispose();
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && scaledFont != null) { scaledFont.Dispose(); scaledFont = null; } }
    }
}

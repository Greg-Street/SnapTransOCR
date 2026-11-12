using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class EditorForm : RoundedForm
    {
        private readonly ImageCanvas canvas;
        private readonly AnnotationOptions styles;
        private readonly FlowLayoutPanel bar;
        private readonly GlyphButton recognition;
        internal AnnotationStyle Style { get { return styles.Style; } }
        public event Action<Bitmap, bool> OcrRequested;
        public event Action<Bitmap, RecognitionKind> SpecialRecognitionRequested;
        public event Action<Bitmap> ImageTranslateRequested;
        public event Action<Bitmap> PinRequested;
        public event Action ScrollRequested;
        public EditorForm(Bitmap bitmap, int stroke = 2) : this(bitmap, new AppSettings { CaptureStroke = stroke }) { }
        internal EditorForm(Bitmap bitmap, AppSettings settings)
        {
            Text = "SnapTransOCR · 图片编辑"; ClientSize = new Size(960, 680); MinimumSize = new Size(560, 320); KeyPreview = true;
            canvas = new ImageCanvas(bitmap);
            bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.White, Padding = new Padding(8, 6, 8, 6) };
            var buttons = new List<GlyphButton>();
            Tool[] tools = { Tool.Pan, Tool.Rectangle, Tool.Ellipse, Tool.Line, Tool.Arrow, Tool.Pen, Tool.Text, Tool.Mosaic, Tool.Crop };
            string[] icons = { "move", "rect", "ellipse", "line", "arrow", "brush", "text", "mosaic", "crop" };
            string[] labels = { "移动", "矩形", "椭圆", "直线", "箭头", "画笔", "文字", "马赛克", "裁剪" };
            for (int i = 0; i < tools.Length; i++)
            {
                Tool tool = tools[i];
                var button = Add(bar, icons[i], labels[i], delegate
                {
                    canvas.Tool = tool;
                    foreach (var item in buttons) { item.Selected = (Tool)item.Tag == tool; item.Invalidate(); }
                });
                button.Tag = tool; button.Selected = i == 0; buttons.Add(button);
            }
            Add(bar, "undo", "撤销", canvas.Undo); Add(bar, "redo", "重做", canvas.Redo);
            Add(bar, "ocr", "识别文字", delegate { if (OcrRequested != null) OcrRequested(canvas.Render(), false); });
            recognition = Add(bar, "general", "专项识别：手写、二维码、表格", delegate { });
            RecognitionMenu.Attach(recognition, settings, delegate(RecognitionKind kind)
            {
                if (SpecialRecognitionRequested != null) SpecialRecognitionRequested(canvas.Render(), kind);
            });
            Add(bar, "translate", "翻译图片", delegate { if (OcrRequested != null) OcrRequested(canvas.Render(), true); });
            Add(bar, "image-translate", "图片原位翻译", delegate { if (ImageTranslateRequested != null) ImageTranslateRequested(canvas.Render()); });
            Add(bar, "scroll", "滚动截图", delegate { if (ScrollRequested != null) ScrollRequested(); });
            Add(bar, "pin", "贴图", delegate { if (PinRequested != null) PinRequested(canvas.Render()); });
            Add(bar, "save", "保存", Save); Add(bar, "copy", "复制", Copy);
            Add(bar, "close", "关闭", Close);
            bar.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
            styles = new AnnotationOptions(settings) { Dock = DockStyle.Top }; styles.Bind(canvas);
            Controls.Add(canvas); Controls.Add(styles); Controls.Add(bar);
            Shown += delegate { canvas.Fit(); canvas.Focus(); };
            ResumeLayout(true);
        }
        internal void RefreshSettings(AppSettings settings) { RecognitionMenu.Update(recognition, settings); }
        internal void EnableHistoryNavigation(Action returnToHistory)
        {
            MinimumSize = new Size(Ui.Scale(this, 360), Ui.Scale(this, 220));
            Control neighbor = bar.Controls[0];
            GlyphButton back = Add(bar, "history", "返回历史记录", delegate { Close(); returnToHistory(); });
            back.Size = neighbor.Size;
            back.Margin = neighbor.Margin;
            bar.Controls.SetChildIndex(back, 0);
        }
        internal Size PreferredImageSize(Size available)
        {
            Size image = canvas.Crop.Size;
            int frameWidth = Padding.Horizontal + 32;
            int width = Math.Min(available.Width, Math.Max(MinimumSize.Width, image.Width + frameWidth));
            while (true)
            {
                int contentWidth = Math.Max(1, width - Padding.Horizontal);
                int height = Padding.Vertical + 32 + bar.GetPreferredSize(new Size(contentWidth, 0)).Height + styles.GetPreferredSize(new Size(contentWidth, 0)).Height;
                Size size = EditorPlacement.FitImage(image, available, MinimumSize, new Size(frameWidth, height));
                // Narrowing can wrap another toolbar row. Recompute until its discrete row count is stable.
                if (size.Width >= width) return size;
                width = size.Width;
            }
        }
        private static GlyphButton Add(FlowLayoutPanel bar, string glyph, string title, Action action)
        {
            var button = new GlyphButton(glyph, title, action) { BackColor = Color.White, Margin = new Padding(1), Size = new Size(44, 44) };
            bar.Controls.Add(button); return button;
        }
        private async void Copy() { try { using (var image = canvas.Render()) await Clip.SetImage(image); } catch (Exception ex) { Ui.Error(this, ex); } }
        private void Save() { try { using (var image = canvas.Render()) ImageFiles.Save(this, image); } catch (Exception ex) { Ui.Error(this, ex); } }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (canvas.EditingText)
            {
                if (e.KeyCode == Keys.Escape) { canvas.CancelText(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Enter) { canvas.CommitText(); e.Handled = e.SuppressKeyPress = true; }
                base.OnKeyDown(e); return;
            }
            if (e.Control && e.KeyCode == Keys.Z) { canvas.Undo(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.Y) { canvas.Redo(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.S) { Save(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.C) { Copy(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }
    }
}

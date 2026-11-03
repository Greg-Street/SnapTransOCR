using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed partial class PinForm
    {
        private OcrTextBlock[] textBlocks = new OcrTextBlock[0];
        private string[] translatedText;
        private ImageTextLayout[] textLayouts, sourceLayouts;
        private TextGlyph[][] sourceGlyphs, translatedGlyphs;
        private int[] sourceOffsets = new int[0], translatedOffsets = new int[0];
        private ToolStripMenuItem copyOriginal, copyTranslated, copySelection;
        private int selectionAnchor = -1, selectionEnd = -1;
        private bool selectingText;
        private bool OriginalTextVisible { get { return original == null || showingOriginal; } }
        private ImageTextLayout[] ActiveLayouts { get { return OriginalTextVisible ? sourceLayouts : textLayouts; } }
        internal string SelectedImageText
        {
            get
            {
                string text = AllText(OriginalTextVisible);
                int start = Math.Min(selectionAnchor, selectionEnd), end = Math.Max(selectionAnchor, selectionEnd);
                return start < 0 || end <= start ? "" : text.Substring(start, end - start);
            }
        }
        private struct TextGlyph
        {
            internal int Start, Length;
            internal RectangleF Bounds;
        }
        internal void SetTextBlocks(OcrTextBlock[] blocks, string[] translated = null, ImageTextLayout[] layouts = null)
        {
            if (translated != null && (translated.Length != blocks.Length || layouts == null || layouts.Length != blocks.Length))
                throw new ArgumentException("文字与译图布局数量不一致。");
            HideTextSelection();
            textBlocks = (OcrTextBlock[])blocks.Clone();
            translatedText = translated == null ? null : (string[])translated.Clone();
            sourceOffsets = BuildBlockOffsets(textBlocks.Length, index => textBlocks[index].Text.Length);
            translatedOffsets = translatedText == null ? new int[0] : BuildBlockOffsets(translatedText.Length, index => translatedText[index].Length);
            textLayouts = layouts == null ? null : (ImageTextLayout[])layouts.Clone();
            sourceLayouts = blocks.Select(ImageTranslation.OriginalLayout).ToArray();
            sourceGlyphs = new TextGlyph[blocks.Length][];
            translatedGlyphs = new TextGlyph[blocks.Length][];
            if (copyOriginal == null)
            {
                copySelection = new ToolStripMenuItem("复制选中文字", null, async delegate { await CopySelectedText(); });
                copyOriginal = new ToolStripMenuItem("复制全部原文", null, async delegate { await CopyText(AllText(true)); });
                copyTranslated = new ToolStripMenuItem("复制全部译文", null, async delegate { await CopyText(AllText(false)); });
                menu.Items.Insert(menu.Items.Count - 1, copySelection);
                menu.Items.Insert(menu.Items.Count - 1, copyOriginal);
                menu.Items.Insert(menu.Items.Count - 1, copyTranslated);
                menu.Opening += delegate { copySelection.Enabled = selectionAnchor >= 0 && selectionAnchor != selectionEnd; };
            }
            copyOriginal.Enabled = textBlocks.Length > 0;
            copyTranslated.Enabled = translatedText != null && translatedText.Any(text => !String.IsNullOrEmpty(text));
            Invalidate();
        }
        private string AllText(bool source)
        {
            return String.Join(Environment.NewLine, source ? textBlocks.Select(block => block.Text) : translatedText ?? new string[0]);
        }
        private async Task CopyText(string text)
        {
            try { if (!String.IsNullOrEmpty(text)) await Clip.SetText(text); }
            catch (Exception ex) { if (!IsDisposed) Ui.Error(this, ex); }
        }
        internal Task CopySelectedText() { return CopyText(SelectedImageText); }
        internal static int[] BuildBlockOffsets(int count, Func<int, int> textLength)
        {
            var offsets = new int[count];
            for (int index = 1; index < count; index++) offsets[index] = offsets[index - 1] + textLength(index - 1) + Environment.NewLine.Length;
            return offsets;
        }
        private int BlockOffset(int index)
        {
            return (OriginalTextVisible ? sourceOffsets : translatedOffsets)[index];
        }
        private TextGlyph[] Glyphs(int index)
        {
            TextGlyph[][] cache = OriginalTextVisible ? sourceGlyphs : translatedGlyphs;
            if (cache[index] != null) return cache[index];
            ImageTextLayout layout = ActiveLayouts[index];
            string text = OriginalTextVisible ? textBlocks[index].Text : translatedText[index];
            var glyphs = new List<TextGlyph>();
            if (layout.Bounds.IsEmpty) return cache[index] = glyphs.ToArray();
            using (var canvas = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(canvas))
            using (var font = new Font("Microsoft YaHei UI", layout.FontPixels, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var format = ImageTranslation.CreateTextFormat())
            {
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                foreach (ImageTextLine line in layout.Lines)
                {
                    int lineStart = glyphs.Count;
                    string value = text.Substring(line.Start, line.Length);
                    int[] starts = StringInfo.ParseCombiningCharacters(value);
                    for (int first = 0; first < starts.Length; first += 32)
                    {
                        int count = Math.Min(32, starts.Length - first);
                        var ranges = new CharacterRange[count];
                        for (int i = 0; i < count; i++)
                        {
                            int position = first + i, end = position + 1 < starts.Length ? starts[position + 1] : value.Length;
                            ranges[i] = new CharacterRange(starts[position], end - starts[position]);
                        }
                        format.SetMeasurableCharacterRanges(ranges);
                        Region[] regions = graphics.MeasureCharacterRanges(value, font, line.Bounds, format);
                        for (int i = 0; i < count; i++)
                            using (regions[i]) glyphs.Add(new TextGlyph { Start = line.Start + ranges[i].First, Length = ranges[i].Length, Bounds = regions[i].GetBounds(graphics) });
                    }
                    if (OriginalTextVisible && glyphs.Count > lineStart)
                    {
                        RectangleF measured = glyphs[lineStart].Bounds;
                        for (int i = lineStart + 1; i < glyphs.Count; i++) measured = RectangleF.Union(measured, glyphs[i].Bounds);
                        if (measured.Width <= 0) continue;
                        for (int i = lineStart; i < glyphs.Count; i++)
                        {
                            TextGlyph glyph = glyphs[i];
                            glyph.Bounds = new RectangleF(line.Bounds.Left + (glyph.Bounds.Left - measured.Left) * line.Bounds.Width / measured.Width,
                                line.Bounds.Top, glyph.Bounds.Width * line.Bounds.Width / measured.Width, line.Bounds.Height);
                            glyphs[i] = glyph;
                        }
                    }
                }
            }
            return cache[index] = glyphs.ToArray();
        }
        private PointF ImagePoint(Point point)
        {
            float x = point.X / ImageScale, y = (point.Y - ImageDestination.Top) / ImageScale;
            switch (quarterTurns)
            {
                case 1: return new PointF(y, CurrentImage.Height - x);
                case 2: return new PointF(CurrentImage.Width - x, CurrentImage.Height - y);
                case 3: return new PointF(CurrentImage.Width - y, x);
                default: return new PointF(x, y);
            }
        }
        private int TextBlockAt(Point point)
        {
            ImageTextLayout[] layouts = ActiveLayouts;
            if (layouts == null) return -1;
            PointF pixel = ImagePoint(point);
            for (int i = 0; i < layouts.Length; i++)
                if (layouts[i].Lines.Any(line => line.Bounds.Contains(pixel))) return i;
            return -1;
        }
        private int CharacterAt(Point point, bool nearest)
        {
            ImageTextLayout[] layouts = ActiveLayouts;
            if (layouts == null || layouts.Length == 0) return -1;
            PointF pixel = ImagePoint(point);
            int block = -1; float distance = Single.MaxValue;
            for (int i = 0; i < layouts.Length; i++)
                foreach (ImageTextLine line in layouts[i].Lines)
                {
                    float dx = Math.Max(0, Math.Max(line.Bounds.Left - pixel.X, pixel.X - line.Bounds.Right));
                    float dy = Math.Max(0, Math.Max(line.Bounds.Top - pixel.Y, pixel.Y - line.Bounds.Bottom));
                    float value = dx * dx + dy * dy * 4;
                    if (value < distance) { distance = value; block = i; }
                }
            if (block < 0 || (!nearest && distance > 0)) return -1;
            TextGlyph[] glyphs = Glyphs(block);
            TextGlyph best = new TextGlyph(); distance = Single.MaxValue; bool found = false;
            foreach (TextGlyph glyph in glyphs)
            {
                float dx = Math.Max(0, Math.Max(glyph.Bounds.Left - pixel.X, pixel.X - glyph.Bounds.Right));
                float dy = Math.Max(0, Math.Max(glyph.Bounds.Top - pixel.Y, pixel.Y - glyph.Bounds.Bottom));
                float value = dx * dx + dy * dy * 4;
                if (value < distance) { distance = value; best = glyph; found = true; }
            }
            return !found ? -1 : BlockOffset(block) + best.Start + (pixel.X >= best.Bounds.Left + best.Bounds.Width / 2 ? best.Length : 0);
        }
        private IEnumerable<RectangleF> SelectionRectangles()
        {
            if (selectionAnchor < 0 || selectionAnchor == selectionEnd || ActiveLayouts == null) yield break;
            int start = Math.Min(selectionAnchor, selectionEnd), end = Math.Max(selectionAnchor, selectionEnd);
            for (int i = 0; i < textBlocks.Length; i++)
            {
                int offset = BlockOffset(i), length = OriginalTextVisible ? textBlocks[i].Text.Length : translatedText[i].Length;
                if (end <= offset || start >= offset + length) continue;
                foreach (TextGlyph glyph in Glyphs(i))
                    if (offset + glyph.Start < end && offset + glyph.Start + glyph.Length > start)
                        yield return DisplayTextRectangle(glyph.Bounds);
            }
        }
        private Rectangle SelectionArea()
        {
            RectangleF area = RectangleF.Empty;
            foreach (RectangleF rectangle in SelectionRectangles()) area = area.IsEmpty ? rectangle : RectangleF.Union(area, rectangle);
            Rectangle result = Rectangle.Ceiling(area); result.Inflate(2, 2);
            return Rectangle.Intersect(ClientRectangle, result);
        }
        private void PaintTextSelection(Graphics graphics)
        {
            using (var brush = new SolidBrush(Color.FromArgb(85, Ui.Accent)))
                foreach (RectangleF rectangle in SelectionRectangles()) graphics.FillRectangle(brush, rectangle);
        }
        private void HideTextSelection()
        {
            Rectangle previous = SelectionArea();
            bool releaseSelectionCapture = selectingText;
            selectionAnchor = selectionEnd = -1; selectingText = false;
            if (releaseSelectionCapture && Capture) Capture = false;
            if (!previous.IsEmpty) Invalidate(previous);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (selectingText)
            {
                Rectangle previous = SelectionArea();
                int position = CharacterAt(e.Location, true);
                if (position >= 0) selectionEnd = position;
                Invalidate(Rectangle.Union(previous, SelectionArea()));
            }
            else Cursor = TextBlockAt(e.Location) >= 0 ? Cursors.IBeam : Cursors.Default;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int position = CharacterAt(e.Location, false);
            Rectangle previous = SelectionArea();
            if (position < 0) { HideTextSelection(); return; }
            if ((ModifierKeys & Keys.Shift) == 0 || selectionAnchor < 0) selectionAnchor = position;
            selectionEnd = position; selectingText = true; Capture = true; Focus();
            Invalidate(Rectangle.Union(previous, SelectionArea()));
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && selectingText) { selectingText = false; Capture = false; }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) selectingText = false;
            base.OnMouseCaptureChanged(e);
        }
    }
}

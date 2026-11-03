using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;

namespace SnapTransOCR
{
    internal static class ImageTranslationTests
    {
        internal static void Run(Action<bool, string> check)
        {
            foreach (Color background in new[] { Color.White, Color.FromArgb(20, 24, 28) })
            using (var original = new Bitmap(420, 180))
            {
                Paint(original, background);
                var blocks = new[] {
                    new OcrTextBlock("保存", new Rectangle(20, 20, 104, 24)),
                    new OcrTextBlock("其它", new Rectangle(170, 20, 110, 24)),
                    new OcrTextBlock("下一行", new Rectangle(20, 85, 200, 24)) };
                var text = new[] { "Save the translated image to a file on this computer without omitting the final words", "Other text", "The next line" };
                ImageTextLayout[] layout = ImageTranslation.Layout(original, blocks, text);
                using (var before = (Bitmap)original.Clone())
                using (var result = ImageTranslation.Render(original, blocks, text, layout))
                {
                    check(result.Width == original.Width && result.Height > original.Height, "long translation grows the canvas vertically at the original width");
                    check(Equal(original, before), "translation rendering does not alter its source");
                    check(Preserved(original, result, layout), "every pixel outside recognized boxes survives inserted row bands");
                    check(layout[0].FontPixels >= 8 && layout[0].FontPixels <= ImageTranslation.OriginalLayout(blocks[0]).FontPixels, "long translation shrinks within the font floor before expanding");
                    check(layout[2].Bounds.Top > blocks[2].Bounds.Top && layout[2].Bounds.Top >= layout[0].Bounds.Bottom, "later source text moves below the expanded translation");
                    check(Disjoint(layout), "same-row columns and subsequent rows never overlap");
                    check(Complete(text, layout), "line indices retain all translated characters without ellipsis");
                    check(Ink(result, layout[0].Lines.Last().Bounds, layout[0].Background), "last translated line is drawn without requiring a click");
                    check(InkHeight(result, layout[0].Lines.Last().Bounds, layout[0].Background) >= layout[0].FontPixels * .5f, "the final line contains full-height glyphs rather than clipped remnants");
                    check(Ink(result, layout[1].Lines[0].Bounds, layout[1].Background), "both light and dark source regions receive visible text");
                }
            }
            ComplexBands(check);
            TextBoundaries(check);
            DpiAndEmpty(check);
            NarrowColumnsAndLimits(check);
            SavePreview();
        }

        private static void ComplexBands(Action<bool, string> check)
        {
            using (var original = new Bitmap(400, 220))
            {
                Paint(original, Color.White);
                var blocks = new[] {
                    new OcrTextBlock("tall\nparagraph\nsource\nlines", new Rectangle(220, 20, 150, 108)),
                    new OcrTextBlock("上方", new Rectangle(20, 20, 120, 24)),
                    new OcrTextBlock("下方", new Rectangle(20, 78, 120, 24)),
                    new OcrTextBlock("overlapping", new Rectangle(30, 82, 120, 28)),
                    new OcrTextBlock("untouched", new Rectangle(20, 170, 160, 24)) };
                var text = new[] { "A tall translated paragraph that contains several visible lines.", "Upper text remains entirely visible with extra lines.", "Lower text also remains visible.", "Overlapping OCR boxes must still receive distinct rows.", "" };
                ImageTextLayout[] layout = ImageTranslation.Layout(original, blocks, text);
                using (var result = ImageTranslation.Render(original, blocks, text, layout))
                {
                    check(Disjoint(layout), "staggered columns and overlapping source OCR rectangles have disjoint translated rows");
                    check(Preserved(original, result, layout), "complex row groups preserve unknown artwork and untranslated OCR blocks");
                    check(Complete(text, layout), "complex row groups lay out every translated character");
                    check(layout.Take(4).All(value => value.InsertionY == 128), "a horizontal insertion never splits a tall neighbouring OCR block");
                }
            }
        }

        private static void TextBoundaries(Action<bool, string> check)
        {
            using (var original = new Bitmap(400, 100))
            {
                using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.White);
                string text = "leading  spaces  \r\n\n中文 English e\u0301 😀 end\n";
                var blocks = new[] { new OcrTextBlock("source", new Rectangle(10, 10, 110, 24)) };
                ImageTextLayout[] layout = ImageTranslation.Layout(original, blocks, new[] { text });
                check(Complete(new[] { text }, layout), "line wrapping retains leading and trailing spaces, CRLF, empty lines and Unicode");
                check(layout[0].Lines.Count(line => line.Length == 0) >= 2, "explicit empty lines including the final line remain in the layout");
                check(layout[0].Lines.All(line => line.Length == 0 || !Char.IsLowSurrogate(text[line.Start])), "line wrapping never starts inside a surrogate pair");
                check(layout[0].Lines.All(line => line.Length == 0 || text[line.Start] != '\u0301'), "line wrapping never starts at a combining accent");
                using (var result = ImageTranslation.Render(original, blocks, new[] { text }, layout))
                    check(result.Height >= layout[0].Bounds.Bottom, "canvas contains the complete multiline layout");
                var originalLayout = ImageTranslation.OriginalLayout(new OcrTextBlock("first\r\nsecond", new Rectangle(5, 6, 160, 48)));
                check(originalLayout.Lines.Length == 2 && originalLayout.Lines[1].Start == 7 && originalLayout.Lines[0].Length == 5 && originalLayout.Lines[1].Bounds.Top == 30,
                    "original text maps explicit source rows to OCR coordinates and original UTF-16 indices");
                string longText = String.Concat(Enumerable.Repeat("Long translation 中文 e\u0301 😀. ", 2500));
                var watch = Stopwatch.StartNew();
                layout = ImageTranslation.Layout(original, blocks, new[] { longText });
                watch.Stop();
                check(Complete(new[] { longText }, layout), "a long translation remains complete without whole-paragraph truncation");
                bool rejected = false;
                try { using (ImageTranslation.Render(original, blocks, new[] { longText }, layout)) { } }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("24000"); }
                check(rejected, "an over-height translation fails before allocating a giant bitmap instead of truncating text");
                Console.WriteLine("Long image layout: " + longText.Length + " UTF-16 units, " + layout[0].Lines.Length + " rows, " + watch.ElapsedMilliseconds + " ms");
            }
        }

        private static void DpiAndEmpty(Action<bool, string> check)
        {
            using (var original = new Bitmap(280, 100))
            {
                Paint(original, Color.White);
                var blocks = new[] { new OcrTextBlock("edge", new Rectangle(-8, -6, 150, 28)), new OcrTextBlock("outside", new Rectangle(300, 120, 20, 20)) };
                var text = new[] { "Text partly outside the image", "outside" };
                ImageTextLayout[] layout = ImageTranslation.Layout(original, blocks, text);
                check(layout[1].Bounds.IsEmpty && layout[0].SourceBounds.Left == 0 && layout[0].SourceBounds.Top == 0, "OCR geometry is clipped to image coordinates");
                original.SetResolution(192, 192);
                ImageTextLayout[] scaled = ImageTranslation.Layout(original, blocks, text);
                check(scaled[0].Bounds == layout[0].Bounds && scaled[0].FontPixels == layout[0].FontPixels, "bitmap DPI metadata does not change physical text layout");
                using (var normal = (Bitmap)original.Clone())
                {
                    normal.SetResolution(96, 96);
                    using (var high = ImageTranslation.Render(original, blocks, text))
                    using (var low = ImageTranslation.Render(normal, blocks, text))
                        check(Equal(high, low), "rendering uses the same physical pixels at 96 and 192 DPI metadata");
                }
                using (var prepared = ImageTranslation.Render(original, blocks, text, scaled))
                using (var automatic = ImageTranslation.Render(original, blocks, text))
                    check(Equal(prepared, automatic), "prepared hit-test lines and automatically rendered lines are identical");
                using (var empty = ImageTranslation.Render(original, new OcrTextBlock[0], new string[0]))
                    check(Equal(empty, original) && !ReferenceEquals(empty, original), "no OCR returns an independent unchanged bitmap");
                using (var empty = ImageTranslation.Render(original, new[] { blocks[0] }, new[] { " \r\n" }))
                    check(Equal(empty, original), "blank translation keeps the image unchanged");
                bool rejected = false;
                try { using (ImageTranslation.Render(original, blocks, new[] { "text" })) { } }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected, "mismatched region and translation counts are rejected");
            }
        }

        private static bool Complete(string[] texts, ImageTextLayout[] layouts)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(texts[i]) || layouts[i].Bounds.IsEmpty) continue;
                var covered = new bool[texts[i].Length];
                foreach (ImageTextLine line in layouts[i].Lines)
                    for (int j = line.Start; j < line.Start + line.Length; j++) { if (covered[j]) return false; covered[j] = true; }
                for (int j = 0; j < covered.Length; j++) if (!covered[j] && texts[i][j] != '\r' && texts[i][j] != '\n') return false;
            }
            return true;
        }

        private static void NarrowColumnsAndLimits(Action<bool, string> check)
        {
            using (var original = new Bitmap(120, 60))
            {
                using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.White);
                var blocks = new[] { new OcrTextBlock("窄", new Rectangle(113, 10, 5, 24)) };
                var text = new[] { "窄列也完整显示" };
                ImageTextLayout[] layout = ImageTranslation.Layout(original, blocks, text);
                using (var result = ImageTranslation.Render(original, blocks, text, layout))
                {
                    check(layout[0].Lines.All(line => line.Bounds.Right <= result.Width && line.Bounds.Left >= 0), "narrow edge columns expand inside the inserted band without leaving the canvas");
                    check(Preserved(original, result, layout), "narrow-column widening does not cover original artwork");
                    check(InkHeight(result, layout[0].Lines.Last().Bounds, layout[0].Background) >= layout[0].FontPixels * .5f, "the final glyph remains readable in an extremely narrow original OCR column");
                }
            }
            using (var original = new Bitmap(4000, 2))
            {
                var block = new OcrTextBlock("a", new Rectangle(0, 0, 10, 2));
                var layout = new ImageTextLayout(block.Bounds, 6, Color.White, Color.Black, new ImageTextLine[0], block.Bounds, 2, 13000);
                bool rejected = false;
                try { using (ImageTranslation.Render(original, new[] { block }, new[] { "b" }, new[] { layout })) { } }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("5000"); }
                check(rejected, "the 50-million-pixel limit is checked independently of the canvas height before allocation");
            }
        }

        private static bool Disjoint(ImageTextLayout[] layouts)
        {
            for (int i = 0; i < layouts.Length; i++)
                for (int j = i + 1; j < layouts.Length; j++)
                    foreach (ImageTextLine a in layouts[i].Lines)
                        foreach (ImageTextLine b in layouts[j].Lines)
                            if (RectangleF.Intersect(a.Bounds, b.Bounds).Height > .01f && RectangleF.Intersect(a.Bounds, b.Bounds).Width > .01f) return false;
            return true;
        }

        private static bool Preserved(Bitmap original, Bitmap result, ImageTextLayout[] layouts)
        {
            var insertions = new SortedDictionary<int, int>();
            foreach (ImageTextLayout layout in layouts) if (layout.InsertionHeight > 0) insertions[layout.InsertionY] = layout.InsertionHeight;
            for (int y = 0; y < original.Height; y++)
            {
                int movedY = y;
                foreach (var insertion in insertions) if (insertion.Key <= y) movedY += insertion.Value;
                for (int x = 0; x < original.Width; x++)
                {
                    if (layouts.Any(layout => !layout.Bounds.IsEmpty && layout.SourceBounds.Contains(x, y))) continue;
                    if (original.GetPixel(x, y).ToArgb() != result.GetPixel(x, movedY).ToArgb()) return false;
                }
            }
            return true;
        }

        private static bool Ink(Bitmap bitmap, RectangleF area, Color background)
        {
            Rectangle bounds = Rectangle.Intersect(Rectangle.Ceiling(area), new Rectangle(Point.Empty, bitmap.Size));
            for (int y = bounds.Top; y < bounds.Bottom; y++)
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    Color value = bitmap.GetPixel(x, y);
                    if (Math.Abs(value.R - background.R) + Math.Abs(value.G - background.G) + Math.Abs(value.B - background.B) > 100) return true;
                }
            return false;
        }

        private static int InkHeight(Bitmap bitmap, RectangleF area, Color background)
        {
            Rectangle bounds = Rectangle.Intersect(Rectangle.Ceiling(area), new Rectangle(Point.Empty, bitmap.Size));
            int rows = 0;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
                if (Ink(bitmap, new RectangleF(bounds.Left, y, bounds.Width, 1), background)) rows++;
            return rows;
        }

        private static void Paint(Bitmap bitmap, Color background)
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(background);
                graphics.DrawLine(Pens.CornflowerBlue, 150, 0, 150, bitmap.Height - 1);
                graphics.DrawLine(Pens.Orange, 0, 60, bitmap.Width - 1, 60);
                graphics.FillEllipse(Brushes.Magenta, 300, 130, 20, 20);
            }
        }

        private static void SavePreview()
        {
            string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output"));
            Directory.CreateDirectory(output);
            var blocks = new[] { new OcrTextBlock("Translate this image", new Rectangle(20, 25, 240, 26)), new OcrTextBlock("Keep the surrounding artwork", new Rectangle(20, 145, 360, 26)) };
            using (var original = new Bitmap(560, 240))
            {
                using (var graphics = Graphics.FromImage(original))
                using (var dark = new SolidBrush(Color.FromArgb(28, 34, 44)))
                using (var font = new Font("Microsoft YaHei UI", 22, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var format = ImageTranslation.CreateTextFormat())
                {
                    graphics.Clear(Color.White); graphics.FillRectangle(dark, 0, 120, 560, 120);
                    graphics.FillEllipse(Brushes.CornflowerBlue, 440, 24, 72, 72);
                    graphics.FillPolygon(Brushes.Orange, new[] { new Point(440, 206), new Point(476, 136), new Point(512, 206) });
                    graphics.SetClip(blocks[0].Bounds);
                    graphics.DrawString(blocks[0].Text, font, Brushes.Black, blocks[0].Bounds, format);
                    graphics.SetClip(blocks[1].Bounds);
                    graphics.DrawString(blocks[1].Text, font, Brushes.White, blocks[1].Bounds, format);
                    graphics.ResetClip();
                }
                using (var translated = ImageTranslation.Render(original, blocks, new[] { "将截图识别出的文字翻译后显示在原来的位置，并且完整展示所有内容", "图案像素保持不变；译文自动增加画布高度，可直接拖选并复制，不需要点击后展开" }))
                {
                    original.Save(Path.Combine(output, "image-translation-original.png"));
                    translated.Save(Path.Combine(output, "image-translation-translated.png"));
                }
            }
        }

        internal static bool Equal(Bitmap left, Bitmap right)
        {
            if (left.Size != right.Size) return false;
            for (int y = 0; y < left.Height; y++)
                for (int x = 0; x < left.Width; x++)
                    if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb()) return false;
            return true;
        }
    }
}

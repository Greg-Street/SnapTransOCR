using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;

namespace SnapTransOCR
{
    internal static class ImageFontFitTests
    {
        internal static void Run(Action<bool, string> check, string output)
        {
            using (var original = new Bitmap(440, 200))
            {
                using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.White);
                var blocks = new[] {
                    new OcrTextBlock("第一行原文", new Rectangle(20, 20, 380, 24)),
                    new OcrTextBlock("第二行原文", new Rectangle(20, 70, 380, 24)),
                    new OcrTextBlock("第三行原文", new Rectangle(20, 120, 380, 24)) };
                var text = new[] {
                    "First translated line has enough detail and should stay in its original place.",
                    "Second translated line keeps the full text while fitting the available width.",
                    "Third translated line can be selected directly without expanding the image." };
                using (var graphics = Graphics.FromImage(original))
                using (var font = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    foreach (OcrTextBlock block in blocks) graphics.DrawString(block.Text, font, Brushes.Black, block.Bounds);
                    graphics.FillRectangle(Brushes.Magenta, 410, 0, 10, original.Height);
                }
                var layouts = ImageTranslation.Layout(original, blocks, text);
                using (var rendered = ImageTranslation.Render(original, blocks, text, layouts))
                {
                    check(rendered.Size == original.Size && layouts.All(value => value.InsertionHeight == 0), "three fitting translated source lines keep the original image dimensions");
                    check(layouts.All(value => value.Lines.Length == 1), "three source line blocks remain three translated lines after font fitting");
                    check(layouts.Select((value, i) => value.FontPixels < ImageTranslation.OriginalLayout(blocks[i]).FontPixels && value.FontPixels >= 8).All(value => value), "translation chooses smaller fonts down to the eight pixel floor");
                    check(layouts.Select((value, i) => value.Lines.All(line => Contains(blocks[i].Bounds, line.Bounds))).All(value => value), "fitted line boxes stay inside their original OCR rectangles");
                    check(Complete(text, layouts) && FitsGlyphs(text, layouts), "fitted rendered text and selection indices cover every complete character inside the available width");
                    check(rendered.GetPixel(415, 80) == original.GetPixel(415, 80), "in-place font fitting preserves nearby non-text artwork");
                    Directory.CreateDirectory(output);
                    original.Save(Path.Combine(output, "image-fit-three-lines-original.png"));
                    rendered.Save(Path.Combine(output, "image-fit-three-lines-translated.png"));
                }
            }
            using (var original = new Bitmap(420, 230))
            {
                using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.White);
                var blocks = new[] {
                    new OcrTextBlock("第一项", new Rectangle(15, 15, 170, 48)),
                    new OcrTextBlock("第二列", new Rectangle(235, 15, 170, 48)),
                    new OcrTextBlock("中文", new Rectangle(15, 90, 220, 48)),
                    new OcrTextBlock("empty", new Rectangle(250, 90, 100, 24)) };
                var text = new[] { "• First item remains in the left column.", new string('W', 55), "完整中文与 English e\u0301 😀 字符均可选择和复制。", "  \r\n" };
                var layouts = ImageTranslation.Layout(original, blocks, text);
                check(Complete(text, layouts), "font fitting preserves full CJK, long unspaced text and combining-character indices");
                check(layouts[0].Bounds.Right <= blocks[1].Bounds.Left && layouts[1].Bounds.Left >= blocks[1].Bounds.Left, "adjacent list items and columns do not merge or cover each other");
                check(layouts[3].Bounds.IsEmpty, "blank translations leave their original block unchanged");
                check(FitsGlyphs(text.Take(3).ToArray(), layouts.Take(3).ToArray()), "long unspaced and mixed-language glyphs fit their measured line boxes without clipped ends");
                using (var rendered = ImageTranslation.Render(original, blocks, text, layouts))
                    rendered.Save(Path.Combine(output, "image-fit-columns-translated.png"));
            }
            using (var original = new Bitmap(180, 80))
            {
                using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.White);
                var block = new OcrTextBlock("原文", new Rectangle(10, 10, 80, 24));
                string text = String.Join(" ", Enumerable.Repeat("A long translation remains completely selectable.", 12));
                var layout = ImageTranslation.Layout(original, new[] { block }, new[] { text });
                using (var rendered = ImageTranslation.Render(original, new[] { block }, new[] { text }, layout))
                {
                    check(layout[0].FontPixels == 8 && rendered.Height > original.Height, "text that cannot fit at eight pixels expands instead of shrinking below the floor or truncating");
                    check(Complete(new[] { text }, layout), "fallback expansion keeps the complete translated text selectable");
                    rendered.Save(Path.Combine(output, "image-fit-overflow-translated.png"));
                }
                var small = new OcrTextBlock("a", new Rectangle(10, 10, 80, 6));
                layout = ImageTranslation.Layout(original, new[] { small }, new[] { "b" });
                check(layout[0].FontPixels == ImageTranslation.OriginalLayout(small).FontPixels && layout[0].FontPixels < 8, "an originally smaller font is not enlarged to the eight pixel floor");
            }
        }

        private static bool Contains(Rectangle outer, RectangleF inner)
        { return inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom; }

        private static bool Complete(string[] text, ImageTextLayout[] layouts)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(text[i])) continue;
                var seen = new bool[text[i].Length];
                foreach (ImageTextLine line in layouts[i].Lines)
                {
                    if (line.Length > 0 && (Char.IsLowSurrogate(text[i][line.Start]) || text[i][line.Start] == '\u0301')) return false;
                    for (int j = line.Start; j < line.Start + line.Length; j++) { if (seen[j]) return false; seen[j] = true; }
                }
                for (int j = 0; j < seen.Length; j++) if (!seen[j] && text[i][j] != '\r' && text[i][j] != '\n') return false;
            }
            return true;
        }

        private static bool FitsGlyphs(string[] text, ImageTextLayout[] layouts)
        {
            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var format = ImageTranslation.CreateTextFormat())
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < layouts.Length; i++)
                using (var font = new Font("Microsoft YaHei UI", layouts[i].FontPixels, FontStyle.Regular, GraphicsUnit.Pixel))
                    foreach (ImageTextLine line in layouts[i].Lines)
                    {
                        if (line.Length == 0) continue;
                        SizeF measured = graphics.MeasureString(text[i].Substring(line.Start, line.Length), font, PointF.Empty, format);
                        if (measured.Width > line.Bounds.Width || font.GetHeight(graphics) > line.Bounds.Height) return false;
                    }
                return true;
            }
        }
    }
}

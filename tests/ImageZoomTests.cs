using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

namespace SnapTransOCR
{
    internal static class ImageZoomTests
    {
        internal static void Run(Action<bool, string> check, string output)
        {
            Directory.CreateDirectory(output);
            foreach (Color background in new[] { Color.White, Color.FromArgb(25, 30, 35) })
            using (var original = new Bitmap(300, 70))
            {
                using (var graphics = Graphics.FromImage(original))
                using (var font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(background == Color.White ? Color.Black : Color.White))
                {
                    graphics.Clear(background);
                    graphics.DrawString("Original source text 中文", font, brush, 12, 11);
                }
                var blocks = new[] { new OcrTextBlock("原文", new Rectangle(10, 10, 260, 20)) };
                var text = new[] { "Small translated text pinyin e\u0301 and symbols 中文" };
                var layout = ImageTranslation.Layout(original, blocks, text);
                using (var exported = ImageTranslation.Render(original, blocks, text, layout))
                using (var originalCopy = (Bitmap)original.Clone())
                using (var exportCopy = (Bitmap)exported.Clone())
                foreach (float scale in new[] { 1.5f, 2f, 4f })
                {
                    var size = new Size((int)(exported.Width * scale), (int)(exported.Height * scale));
                    using (var transform = new Matrix(scale, 0, 0, scale, 0, 0))
                    using (var live = Preview(original, text, layout, size, transform))
                    using (var expected = ExpectedText(background, text, layout, size, transform))
                    using (var enlargedRaster = Enlarge(exported, size))
                    {
                        check(Equal(live, expected), "zoom " + scale + " draws fresh glyphs without rasterized text halos on " + background.ToArgb());
                        check(!Equal(live, enlargedRaster), "zoom " + scale + " does not interpolate the previously rasterized small-font text");
                        if (scale == 4 && background == Color.White)
                        {
                            live.Save(Path.Combine(output, "image-zoom-live-4x.png"));
                            enlargedRaster.Save(Path.Combine(output, "image-zoom-old-bitmap-4x.png"));
                        }
                    }
                    check(Equal(original, originalCopy) && Equal(exported, exportCopy), "zoom redraw leaves source and PNG-export pixels unchanged");
                }
            }
            RotationAndClipping(check, output);
        }

        private static void RotationAndClipping(Action<bool, string> check, string output)
        {
            using (var original = new Bitmap(220, 100))
            {
                using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.White);
                var blocks = new[] { new OcrTextBlock("source", new Rectangle(10, 10, 180, 24)) };
                var text = new[] { String.Join(" ", Enumerable.Repeat("Visible text 中文 e\u0301.", 30)) };
                var layout = ImageTranslation.Layout(original, blocks, text);
                using (var exported = ImageTranslation.Render(original, blocks, text, layout))
                {
                    const float scale = 2;
                    for (int turn = 0; turn < 4; turn++)
                    {
                        Size size = (turn & 1) == 0 ? new Size(exported.Width * 2, exported.Height * 2) : new Size(exported.Height * 2, exported.Width * 2);
                        using (var transform = Rotation(turn, scale, exported.Size, 0))
                        using (var full = Preview(original, text, layout, size, transform))
                        {
                            int top = Math.Min(80, size.Height / 4);
                            var view = new Rectangle(0, top, Math.Min(size.Width, 360), Math.Min(size.Height - top, 160));
                            using (var viewportTransform = Rotation(turn, scale, exported.Size, -top))
                            using (var viewport = Preview(original, text, layout, view.Size, viewportTransform))
                            using (var expected = full.Clone(view, PixelFormat.Format32bppPArgb))
                                check(Equal(viewport, expected), "rotated translated viewport " + turn + " draws the same visible pixels after scrolling and clipping");
                            if (turn == 0) full.Save(Path.Combine(output, "image-zoom-overflow-2x.png"));
                        }
                    }
                    using (var target = new Bitmap(440, 240))
                    using (var graphics = Graphics.FromImage(target))
                    using (var transform = Rotation(0, scale, exported.Size, -180))
                    {
                        graphics.Transform = transform; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
                        using (var before = graphics.Transform)
                        {
                            ImageTranslation.Draw(graphics, original, text, layout);
                            using (var after = graphics.Transform)
                                check(before.Elements.SequenceEqual(after.Elements) && graphics.PixelOffsetMode == PixelOffsetMode.Half && graphics.TextRenderingHint == TextRenderingHint.SingleBitPerPixel,
                                    "live text drawing restores transform and rendering state for subsequent selection painting");
                        }
                        for (int i = 0; i < 5; i++) ImageTranslation.Draw(graphics, original, text, layout);
                        var watch = Stopwatch.StartNew();
                        for (int i = 0; i < 60; i++) ImageTranslation.Draw(graphics, original, text, layout);
                        watch.Stop();
                        Console.WriteLine("Zoom viewport draw: 60 frames, " + layout[0].Lines.Length + " source rows, " + (watch.Elapsed.TotalMilliseconds / 60).ToString("F3") + " ms/frame");
                    }
                }
            }
        }

        private static Matrix Rotation(int turn, float scale, Size image, int top)
        {
            switch (turn)
            {
                case 1: return new Matrix(0, scale, -scale, 0, image.Height * scale, top);
                case 2: return new Matrix(-scale, 0, 0, -scale, image.Width * scale, image.Height * scale + top);
                case 3: return new Matrix(0, -scale, scale, 0, 0, image.Width * scale + top);
                default: return new Matrix(scale, 0, 0, scale, 0, top);
            }
        }

        private static Bitmap Preview(Bitmap original, string[] text, ImageTextLayout[] layouts, Size size, Matrix transform)
        {
            var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.Transform = transform;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                ImageTranslation.Draw(graphics, original, text, layouts);
            }
            return result;
        }

        private static Bitmap ExpectedText(Color background, string[] text, ImageTextLayout[] layouts, Size size, Matrix transform)
        {
            var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(result))
            using (var format = ImageTranslation.CreateTextFormat())
            {
                graphics.Clear(background); graphics.Transform = transform;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < layouts.Length; i++)
                using (var font = new Font("Microsoft YaHei UI", layouts[i].FontPixels, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var foreground = new SolidBrush(layouts[i].Foreground))
                    foreach (ImageTextLine line in layouts[i].Lines)
                        graphics.DrawString(text[i].Substring(line.Start, line.Length), font, foreground, line.Bounds, format);
            }
            return result;
        }

        private static Bitmap Enlarge(Bitmap source, Size size)
        {
            var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(result))
            using (var attributes = new ImageAttributes())
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(source, new Rectangle(Point.Empty, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            return result;
        }

        private static bool Equal(Bitmap left, Bitmap right)
        {
            if (left.Size != right.Size) return false;
            for (int y = 0; y < left.Height; y++)
                for (int x = 0; x < left.Width; x++)
                    if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb()) return false;
            return true;
        }
    }
}

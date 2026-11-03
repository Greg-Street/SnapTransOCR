using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Threading;

namespace SnapTransOCR
{
    internal static class ImageTranslation
    {
        internal static StringFormat CreateTextFormat()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags &= ~StringFormatFlags.LineLimit;
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
            format.Trimming = StringTrimming.None;
            return format;
        }

        internal static ImageTextLayout OriginalLayout(OcrTextBlock block)
        {
            string text = block.Text ?? "";
            var lines = new List<ImageTextLine>();
            int count = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') count++;
            float height = block.Bounds.Height / (float)count;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                int length = i - start;
                if (length > 0 && text[start + length - 1] == '\r') length--;
                lines.Add(new ImageTextLine(start, length, new RectangleF(block.Bounds.Left, block.Bounds.Top + lines.Count * height, block.Bounds.Width, height)));
                start = i + 1;
            }
            return new ImageTextLayout(block.Bounds, Math.Max(6, Math.Min(128, height * .92f)), Color.Empty, Color.Empty,
                lines.ToArray(), block.Bounds, 0, 0);
        }

        internal static Bitmap Render(Bitmap original, OcrTextBlock[] blocks, string[] translated, CancellationToken cancellation = default(CancellationToken))
        { return Render(original, blocks, translated, Layout(original, blocks, translated, cancellation), cancellation); }

        internal static ImageTextLayout[] Layout(Bitmap original, OcrTextBlock[] blocks, string[] translated, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            if (blocks.Length != translated.Length) throw new InvalidOperationException("译文数量与识别区域不一致。");
            var layouts = new ImageTextLayout[blocks.Length];
            var source = new Rectangle[blocks.Length];
            var order = new List<int>();
            for (int i = 0; i < blocks.Length; i++)
            {
                source[i] = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size), blocks[i].Bounds);
                layouts[i] = new ImageTextLayout(Rectangle.Empty, 0, Color.Empty, Color.Empty);
                if (!source[i].IsEmpty) order.Add(i);
            }
            order.Sort((a, b) => source[a].Top != source[b].Top ? source[a].Top.CompareTo(source[b].Top) : source[a].Left.CompareTo(source[b].Left));
            using (var measuringImage = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(measuringImage))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int offset = 0;
                for (int first = 0; first < order.Count;)
                {
                    // Insert only below a complete band: no recognized rectangle is cut in two.
                    int last = first + 1, bottom = source[order[first]].Bottom;
                    while (last < order.Count && source[order[last]].Top < bottom)
                    { bottom = Math.Max(bottom, source[order[last]].Bottom); last++; }
                    var occupied = new List<RectangleF>();
                    int added = 0;
                    for (int p = first; p < last; p++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int i = order[p];
                        Rectangle area = source[i];
                        if (area.Width < 3 || area.Height < 3 || String.IsNullOrWhiteSpace(translated[i])) continue;
                        Color background = Background(original, area);
                        Color foreground = background.R * .299 + background.G * .587 + background.B * .114 > 140 ? Color.FromArgb(25, 27, 31) : Color.White;
                        float size = FitFontSize(graphics, translated[i], area, OriginalLayout(blocks[i]).FontPixels, cancellation);
                        using (var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
                        {
                            float lineHeight = (float)Math.Ceiling(font.GetHeight(graphics)) + 1;
                            float width = Math.Min(original.Width, Math.Max(area.Width - 4, size * 2));
                            float left = Math.Min(area.Left + 2, original.Width - width);
                            List<ImageTextLine> rows = Wrap(graphics, translated[i], font, width, lineHeight, cancellation);
                            bool overlaps = width > area.Width - 4;
                            for (int q = first; q < last; q++)
                                if (order[q] != i && area.IntersectsWith(source[order[q]])) { overlaps = true; break; }
                            // Original pixels outside OCR boxes are preserved. If a taller neighbouring
                            // block prevents expansion, remaining lines continue in the inserted band.
                            bool contiguous = !overlaps && area.Bottom == bottom;
                            int within = overlaps ? 0 : Math.Min(rows.Count, Math.Max(0, (int)((area.Height - 2) / lineHeight)));
                            float nextY = area.Top + offset + 1;
                            float overflowY = contiguous ? Math.Max(bottom + offset, nextY + rows.Count * lineHeight) : bottom + offset + (rows.Count - within) * lineHeight;
                            float overflowHeight = Math.Max(0, overflowY - bottom - offset);
                            float insertionTop = bottom + offset;
                            var reservation = new RectangleF(left, insertionTop, width, overflowHeight);
                            for (int previous = 0; previous < occupied.Count; previous++)
                            {
                                if (!reservation.IntersectsWith(occupied[previous])) continue;
                                reservation.Y = occupied[previous].Bottom;
                                contiguous = false; within = 0; reservation.Height = rows.Count * lineHeight;
                                previous = -1;
                            }
                            if (reservation.Height > 0) occupied.Add(reservation);
                            var placed = new ImageTextLine[rows.Count];
                            RectangleF union = RectangleF.Empty;
                            for (int row = 0; row < rows.Count; row++)
                            {
                                float y = contiguous || row < within ? nextY + row * lineHeight : reservation.Top + (row - within) * lineHeight;
                                RectangleF bounds = new RectangleF(left, y, width, lineHeight);
                                placed[row] = new ImageTextLine(rows[row].Start, rows[row].Length, bounds);
                                union = union.IsEmpty ? bounds : RectangleF.Union(union, bounds);
                            }
                            added = Math.Max(added, (int)Math.Ceiling(union.Bottom - bottom - offset) + 1);
                            layouts[i] = new ImageTextLayout(Rectangle.Ceiling(union), size, background, foreground, placed, area, bottom, 0);
                        }
                    }
                    added = Math.Max(0, added);
                    for (int p = first; p < last; p++)
                    {
                        int i = order[p]; ImageTextLayout value = layouts[i];
                        if (value.Bounds.IsEmpty) continue;
                        layouts[i] = new ImageTextLayout(value.Bounds, value.FontPixels, value.Background, value.Foreground,
                            value.Lines, value.SourceBounds, bottom, added);
                    }
                    offset = checked(offset + added);
                    first = last;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return layouts;
        }

        private static float FitFontSize(Graphics graphics, string text, Rectangle area, float originalSize, CancellationToken cancellation)
        {
            float minimum = Math.Min(8, originalSize);
            float width = area.Width - 4, height = area.Height - 2;
            if (width <= 0 || height <= 0) return minimum;
            if (Fits(graphics, text, originalSize, width, height, cancellation)) return originalSize;
            if (!Fits(graphics, text, minimum, width, height, cancellation)) return minimum;
            float low = minimum, high = originalSize;
            for (int i = 0; i < 8 && high - low > .1f; i++)
            {
                float middle = (low + high) / 2;
                if (Fits(graphics, text, middle, width, height, cancellation)) low = middle;
                else high = middle;
            }
            return low;
        }

        private static bool Fits(Graphics graphics, string text, float size, float width, float height, CancellationToken cancellation)
        {
            using (var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var format = CreateTextFormat())
            {
                float lineHeight = (float)Math.Ceiling(font.GetHeight(graphics)) + 1;
                int maximumLines = (int)(height / lineHeight);
                if (maximumLines < 1) return false;
                List<ImageTextLine> rows = Wrap(graphics, text, font, width, lineHeight, cancellation, maximumLines);
                if (rows.Count > maximumLines) return false;
                foreach (ImageTextLine line in rows)
                    if (line.Length > 0 && graphics.MeasureString(text.Substring(line.Start, line.Length), font, PointF.Empty, format).Width > width) return false;
                return true;
            }
        }

        private static List<ImageTextLine> Wrap(Graphics graphics, string text, Font font, float width, float height, CancellationToken cancellation, int maximumLines = Int32.MaxValue)
        {
            var rows = new List<ImageTextLine>();
            using (var format = CreateTextFormat())
            {
                format.FormatFlags &= ~StringFormatFlags.NoWrap;
                format.FormatFlags |= StringFormatFlags.LineLimit;
                for (int start = 0; start <= text.Length;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int end = text.IndexOf('\n', start);
                    if (end < 0) end = text.Length;
                    int contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
                    if (start == contentEnd)
                    {
                        rows.Add(new ImageTextLine(start, 0, RectangleF.Empty));
                        if (rows.Count > maximumLines) return rows;
                    }
                    while (start < contentEnd)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int length = Math.Min(contentEnd - start, Math.Max(128, (int)(width / font.Size * 4)));
                        if (start + length < contentEnd && Char.IsHighSurrogate(text[start + length - 1])) length--;
                        string candidate = text.Substring(start, length);
                        int fit, measuredLines;
                        graphics.MeasureString(candidate, font, new SizeF(width, height * 1.01f), format, out fit, out measuredLines);
                        int[] elements = StringInfo.ParseCombiningCharacters(candidate);
                        if (fit < candidate.Length)
                        {
                            int element = Array.BinarySearch(elements, fit);
                            if (element < 0) fit = elements[Math.Max(0, ~element - 1)];
                            if (fit == 0) fit = elements.Length > 1 ? elements[1] : candidate.Length;
                        }
                        rows.Add(new ImageTextLine(start, fit, RectangleF.Empty));
                        if (rows.Count > maximumLines) return rows;
                        start += fit;
                    }
                    if (end == text.Length) break;
                    start = end + 1;
                }
            }
            return rows;
        }

        internal static Bitmap Render(Bitmap original, OcrTextBlock[] blocks, string[] translated, ImageTextLayout[] layouts, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            if (blocks.Length != translated.Length || blocks.Length != layouts.Length) throw new InvalidOperationException("译文数量与识别区域不一致。");
            var insertions = Insertions(layouts);
            long height = original.Height;
            foreach (int insertion in insertions.Values) height += insertion;
            if (height > 24000 || original.Width * height > 50000000)
                throw new InvalidOperationException("完整译图超过 24000 像素高或 5000 万像素上限，请缩小截图后重试。原图和已识别文字仍可查看、复制。");
            var result = new Bitmap(original.Width, (int)height, PixelFormat.Format32bppPArgb);
            try
            {
                using (var graphics = Graphics.FromImage(result)) Draw(graphics, original, translated, layouts, insertions, (int)height, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal static void Draw(Graphics graphics, Bitmap original, string[] translated, ImageTextLayout[] layouts, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            var insertions = Insertions(layouts);
            int height = original.Height;
            foreach (int insertion in insertions.Values) height += insertion;
            Draw(graphics, original, translated, layouts, insertions, height, cancellation);
        }

        private static SortedDictionary<int, int> Insertions(ImageTextLayout[] layouts)
        {
            var insertions = new SortedDictionary<int, int>();
            foreach (ImageTextLayout layout in layouts)
                if (layout.InsertionHeight > 0) insertions[layout.InsertionY] = layout.InsertionHeight;
            return insertions;
        }

        private static void Draw(Graphics graphics, Bitmap original, string[] translated, ImageTextLayout[] layouts,
            SortedDictionary<int, int> insertions, int height, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            GraphicsState state = graphics.Save();
            var fonts = new Dictionary<float, Font>();
            try
            {
                using (var format = CreateTextFormat())
                {
                    using (var background = new SolidBrush(original.GetPixel(0, 0)))
                        graphics.FillRectangle(background, 0, 0, original.Width, height);
                    int sourceY = 0, offset = 0, insertionIndex = 0;
                    var insertionPositions = new int[insertions.Count];
                    var insertionOffsets = new int[insertions.Count];
                    foreach (var insertion in insertions)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        CopyStrip(graphics, original, sourceY, insertion.Key, offset);
                        using (var background = new SolidBrush(original.GetPixel(0, Math.Max(0, insertion.Key - 1))))
                            graphics.FillRectangle(background, 0, insertion.Key + offset, original.Width, insertion.Value);
                        sourceY = insertion.Key; offset += insertion.Value;
                        insertionPositions[insertionIndex] = insertion.Key;
                        insertionOffsets[insertionIndex++] = offset;
                    }
                    CopyStrip(graphics, original, sourceY, original.Height, offset);
                    foreach (ImageTextLayout layout in layouts)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (layout.Bounds.IsEmpty) continue;
                        Rectangle area = layout.SourceBounds;
                        int previous = Array.BinarySearch(insertionPositions, area.Top);
                        if (previous < 0) previous = ~previous - 1;
                        if (previous >= 0) area.Y += insertionOffsets[previous];
                        if (graphics.IsVisible(area))
                            using (var brush = new SolidBrush(layout.Background)) graphics.FillRectangle(brush, area);
                    }
                    // Re-rasterize glyph outlines at the current transform, never enlarge a text bitmap.
                    graphics.PixelOffsetMode = PixelOffsetMode.Default;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    for (int i = 0; i < layouts.Length; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ImageTextLayout layout = layouts[i];
                        if (layout.Bounds.IsEmpty || !graphics.IsVisible(layout.Bounds)) continue;
                        Font font;
                        if (!fonts.TryGetValue(layout.FontPixels, out font))
                        {
                            font = new Font("Microsoft YaHei UI", layout.FontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
                            fonts.Add(layout.FontPixels, font);
                        }
                        using (var brush = new SolidBrush(layout.Foreground))
                        using (var background = new SolidBrush(layout.Background))
                        foreach (ImageTextLine line in layout.Lines)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (!graphics.IsVisible(line.Bounds)) continue;
                            graphics.FillRectangle(background, line.Bounds);
                            if (line.Length > 0) graphics.DrawString(translated[i].Substring(line.Start, line.Length), font, brush, line.Bounds, format);
                        }
                    }
                }
            }
            finally
            {
                foreach (Font font in fonts.Values) font.Dispose();
                graphics.Restore(state);
            }
        }

        private static void CopyStrip(Graphics graphics, Bitmap original, int top, int bottom, int offset)
        {
            if (bottom <= top || !graphics.IsVisible(new Rectangle(0, top + offset, original.Width, bottom - top))) return;
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(original, new Rectangle(0, top + offset, original.Width, bottom - top),
                    0, top, original.Width, bottom - top, GraphicsUnit.Pixel, attributes);
            }
        }

        private static Color Background(Bitmap image, Rectangle bounds)
        {
            int left = Math.Max(0, bounds.Left - 1), right = Math.Min(image.Width - 1, bounds.Right);
            int top = Math.Max(0, bounds.Top - 1), bottom = Math.Min(image.Height - 1, bounds.Bottom);
            var samples = new[] { image.GetPixel(left, top), image.GetPixel(right, top), image.GetPixel(left, bottom), image.GetPixel(right, bottom) };
            int[] red = new int[4], green = new int[4], blue = new int[4];
            for (int i = 0; i < samples.Length; i++) { red[i] = samples[i].R; green[i] = samples[i].G; blue[i] = samples[i].B; }
            Array.Sort(red); Array.Sort(green); Array.Sort(blue);
            return Color.FromArgb((red[1] + red[2]) / 2, (green[1] + green[2]) / 2, (blue[1] + blue[2]) / 2);
        }
    }
}

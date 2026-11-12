using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ScreenCapture
    {
        public static IEnumerable<Rectangle> OcrRegions(Bitmap image)
        {
            int columns = (image.Width + 4095) / 4096;
            int rows = (image.Height + 3499) / 3500;
            int width = (image.Width + columns - 1) / columns;
            int height = (image.Height + rows - 1) / rows;
            for (int top = 0; top < image.Height; )
            {
                int bottom = Math.Min(image.Height, top + height);
                if (bottom < image.Height)
                {
                    // Prefer a uniform row to avoid cutting a line of text at the tile boundary.
                    for (int y = bottom; y > Math.Max(top + 100, bottom - 100); y--)
                    {
                        Color first = image.GetPixel(0, y);
                        bool uniform = true;
                        for (int x = 1; x < 64; x++)
                        {
                            Color sample = image.GetPixel((image.Width - 1) * x / 63, y);
                            if (Math.Abs(sample.R - first.R) + Math.Abs(sample.G - first.G) + Math.Abs(sample.B - first.B) > 24) { uniform = false; break; }
                        }
                        if (uniform) { bottom = y; break; }
                    }
                }
                for (int left = 0; left < image.Width; left += width)
                    yield return new Rectangle(left, top, Math.Min(width, image.Width - left), bottom - top);
                top = bottom;
            }
        }
        public static Bitmap Desktop(Rectangle bounds, bool includeCursor = false)
        { return CaptureDesktop(bounds, includeCursor, PixelFormat.Format32bppRgb); }
        internal static Bitmap FrozenDesktop(Rectangle bounds, bool includeCursor = false)
        { return CaptureDesktop(bounds, includeCursor, PixelFormat.Format32bppPArgb); }
        private static Bitmap CaptureDesktop(Rectangle bounds, bool includeCursor, PixelFormat format)
        {
            // Capture into a top-down DIB to avoid GetHdc/ReleaseHdc's extra full-screen bitmap copy.
            IntPtr screen = Native.GetDC(IntPtr.Zero), target = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            if (screen == IntPtr.Zero) throw new InvalidOperationException("无法读取当前桌面，请确认屏幕已解锁。");
            try
            {
                target = Native.CreateCompatibleDC(screen);
                if (target == IntPtr.Zero) throw new InvalidOperationException("无法创建截图画布。");
                var info = new Native.BITMAPINFO { Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32 };
                IntPtr bits;
                bitmap = Native.CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero) throw new InvalidOperationException("无法分配截图画布，请缩小屏幕范围后重试。");
                previous = Native.SelectObject(target, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new InvalidOperationException("无法准备截图画布。");
                if (!Native.BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40cc0020))
                    throw new InvalidOperationException("截图失败，请确认屏幕已解锁且当前画面允许捕获。");
                if (includeCursor) CaptureSessionUi.DrawCursor(target, bounds);
                Native.GdiFlush();
                // Treat undefined GDI alpha as opaque, then make an independently owned display bitmap.
                using (var source = new Bitmap(bounds.Width, bounds.Height, bounds.Width * 4, PixelFormat.Format32bppRgb, bits))
                {
                    if (format == PixelFormat.Format32bppPArgb) return ForDisplay(source);
                    var result = new Bitmap(bounds.Width, bounds.Height, format);
                    try
                    {
                        using (var graphics = Graphics.FromImage(result)) graphics.DrawImageUnscaled(source, 0, 0);
                        return result;
                    }
                    catch { result.Dispose(); throw; }
                }
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(target, previous);
                if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
                if (target != IntPtr.Zero) Native.DeleteDC(target);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }
        internal static Bitmap ForDisplay(Bitmap source)
        {
            // RGB capture ignores undefined GDI alpha; convert once for fast clipped GDI+ painting.
            return source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format32bppPArgb);
        }
        public static Rectangle Selection(Point first, Point last, Size bounds)
        {
            return Rectangle.Intersect(Rectangle.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Max(first.X, last.X), Math.Max(first.Y, last.Y)), new Rectangle(Point.Empty, bounds));
        }
        public static byte[] EncodeOcr(Bitmap original)
        {
            if (Math.Min(original.Width, original.Height) < 15) throw new InvalidOperationException("OCR 截图的宽和高都需至少 15 像素，请扩大选区。");
            double scale = Math.Min(1.0, 4096.0 / Math.Max(original.Width, original.Height));
            using (var image = new Bitmap(Math.Max(15, (int)(original.Width * scale)), Math.Max(15, (int)(original.Height * scale)), PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(original, new Rectangle(Point.Empty, image.Size));
                }
                using (var stream = new MemoryStream())
                {
                    image.Save(stream, ImageFormat.Png);
                    if (stream.Length <= 3500000) return stream.ToArray();
                    stream.SetLength(0);
                    ImageCodecInfo jpeg = null;
                    foreach (var codec in ImageCodecInfo.GetImageEncoders()) if (codec.MimeType == "image/jpeg") jpeg = codec;
                    using (var options = new EncoderParameters(1))
                    {
                        options.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                        image.Save(stream, jpeg, options);
                    }
                    if (stream.Length > 3500000) throw new InvalidOperationException("截图内容过大，请缩小选区后重试。");
                    return stream.ToArray();
                }
            }
        }
    }

}

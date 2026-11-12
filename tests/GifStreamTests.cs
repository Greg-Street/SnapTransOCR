using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SnapTransOCR
{
    internal static class GifStreamTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string path = Path.Combine(Path.GetTempPath(), "SnapTransOCR-gif-" + Guid.NewGuid().ToString("N") + ".gif");
            try
            {
                using (var red = Frame(Color.Red))
                using (var blue = Frame(Color.Blue))
                using (var green = Frame(Color.Lime))
                using (var writer = new GifStreamWriter(path, red.Size))
                {
                    writer.AddFrame(red, 0); writer.AddFrame(red, 100); writer.AddFrame(blue, 240); writer.AddFrame(green, 400);
                    writer.Complete(700); writer.Complete(700);
                    check(writer.FrameCount == 3 && writer.Length > 100, "streamed GIF coalesces identical frames and completes only once");
                }
                using (var image = Image.FromFile(path))
                {
                    check(image.Size == new Size(80, 60) && image.GetFrameCount(FrameDimension.Time) == 3, "Windows GIF decoder reads the complete animation and original dimensions");
                    var expected = new[] { Color.Red, Color.Blue, Color.Lime };
                    for (int index = 0; index < expected.Length; index++)
                    {
                        image.SelectActiveFrame(FrameDimension.Time, index);
                        using (var bitmap = new Bitmap(image))
                        {
                            Color actual = bitmap.GetPixel(20, 20), target = expected[index];
                            check(Math.Abs(actual.R - target.R) + Math.Abs(actual.G - target.G) + Math.Abs(actual.B - target.B) < 30, "animation frame retains independently decoded pixels: " + index);
                        }
                    }
                    byte[] delays = image.GetPropertyItem(0x5100).Value;
                    check(BitConverter.ToInt32(delays, 0) == 24 && BitConverter.ToInt32(delays, 4) == 16 && BitConverter.ToInt32(delays, 8) == 30,
                        "GIF delays preserve elapsed time including repeated captured frames");
                    check(BitConverter.ToUInt16(image.GetPropertyItem(0x5101).Value, 0) == 0, "GIF loop metadata requests continuous playback");
                }
                File.Copy(path, Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", "gif-encoder-example.gif")), true);
                File.Delete(path);
                using (var writer = new GifStreamWriter(path, new Size(80, 60)))
                using (var wrong = new Bitmap(10, 10))
                {
                    bool rejected = false; try { writer.AddFrame(wrong, 0); } catch (InvalidOperationException) { rejected = true; }
                    check(rejected, "GIF rejects changed frame dimensions before encoding");
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            DeltaFrames(check);
        }
        private static void DeltaFrames(Action<bool, string> check)
        {
            string path = Path.Combine(Path.GetTempPath(), "SnapTransOCR-delta-" + Guid.NewGuid().ToString("N") + ".gif");
            try
            {
                using (var first = new Bitmap(240, 160, PixelFormat.Format32bppRgb))
                using (var second = new Bitmap(240, 160, PixelFormat.Format32bppRgb))
                using (var third = new Bitmap(240, 160, PixelFormat.Format32bppRgb))
                {
                    foreach (var frame in new[] { first, second, third }) using (var graphics = Graphics.FromImage(frame)) { graphics.Clear(Color.White); graphics.FillRectangle(Brushes.Blue, 220, 140, 20, 20); }
                    using (var graphics = Graphics.FromImage(second)) graphics.FillRectangle(Brushes.Red, 12, 24, 16, 16);
                    using (var graphics = Graphics.FromImage(third)) graphics.FillRectangle(Brushes.Red, 120, 80, 16, 16);
                    using (var writer = new GifStreamWriter(path, first.Size))
                    {
                        writer.AddFrame(first, 0); writer.AddFrame(second, 100); writer.AddFrame(second, 200); writer.AddFrame(third, 300); writer.AddFrame(first, 500); writer.Complete(700);
                        check(writer.FrameCount == 4, "delta GIF merges an unchanged cropped frame without adding another patch");
                    }
                    using (var gif = Image.FromFile(path))
                    {
                        check(gif.Size == first.Size && gif.GetFrameCount(FrameDimension.Time) == 4, "independent Windows decoder recognizes a full canvas with four partial-update frames");
                        for (int index = 0; index < 4; index++)
                        {
                            gif.SelectActiveFrame(FrameDimension.Time, index);
                            using (var frame = new Bitmap(gif))
                            {
                                check(frame.GetPixel(225, 145).B > 220 && frame.GetPixel(50, 120).R > 220, "delta frame retains untouched background outside its patch: " + index);
                                check((frame.GetPixel(16, 28).G < 40) == (index == 1) && (frame.GetPixel(124, 84).G < 40) == (index == 2), "delta frame restores the previous moving object location without trails: " + index);
                            }
                        }
                        byte[] delays = gif.GetPropertyItem(0x5100).Value;
                        check(BitConverter.ToInt32(delays, 0) == 10 && BitConverter.ToInt32(delays, 4) == 20 && BitConverter.ToInt32(delays, 8) == 20 && BitConverter.ToInt32(delays, 12) == 20,
                            "partial-update frames preserve total duration and the merged repeated frame delay");
                    }
                }
                File.Copy(path, Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", "gif-delta-example.gif")), true);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        private static Bitmap Frame(Color color)
        {
            var image = new Bitmap(80, 60, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
            return image;
        }
    }
}

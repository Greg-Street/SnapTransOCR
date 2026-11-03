using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnapTransOCR
{
    internal static class ScrollingOptimizationTests
    {
        internal static void Run(Action<bool, string> check)
        {
            foreach (int channel in new[] { 0, 1, 2, 3 })
            using (var page = Pattern(321, 980, channel))
            using (var first = Crop(page, 0, 600))
            using (var next = Crop(page, 240, 600))
            using (var stitcher = new Stitcher(first))
            {
                string message;
                bool appended = stitcher.Append(next, out message);
                check(appended && stitcher.TotalHeight == 840, "scroll recognizes texture in RGB channel " + channel);
                using (var result = stitcher.Finish())
                    check(appended && Matches(page, result), "scroll retains exact stitched pixels for RGB channel " + channel);
            }
            using (var page = Pattern(240, 1000, 3))
            using (var first = Crop(page, 0, 600))
            using (var stitcher = new Stitcher(first))
            {
                int offset = 0;
                string message;
                for (int shift = 1; shift <= 4; shift++)
                {
                    offset += shift;
                    using (var frame = Crop(page, offset, 600))
                        check(stitcher.Append(frame, out message) && stitcher.TotalHeight == 600 + offset, "scroll accepts a textured " + shift + " pixel movement");
                }
                using (var unchanged = Crop(page, offset, 600))
                    check(!stitcher.Append(unchanged, out message) && stitcher.FrameCount == 5, "unchanged scroll frame adds no duplicate strip");
                using (var unrelated = Pattern(240, 600, 3, 938))
                    check(!stitcher.Append(unrelated, out message) && stitcher.TotalHeight == 610, "unrelated scroll frame does not change the accepted baseline");
                using (var next = Crop(page, 250, 600))
                    check(stitcher.Append(next, out message) && stitcher.TotalHeight == 850, "valid scrolling continues after a rejected unrelated frame");
                using (var result = stitcher.Finish())
                    check(Matches(page, result), "micro scrolling and ordinary scrolling compose without skipped or duplicate rows");
            }
            using (var first = new Bitmap(240, 400, PixelFormat.Format24bppRgb))
            using (var changed = new Bitmap(240, 400, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(first)) graphics.Clear(Color.FromArgb(100, 100, 100));
                using (var graphics = Graphics.FromImage(changed)) graphics.Clear(Color.FromArgb(101, 101, 101));
                using (var stitcher = new Stitcher(first))
                {
                    string message;
                    check(!stitcher.Append(changed, out message) && stitcher.TotalHeight == 400, "changed blank background remains too ambiguous to stitch");
                }
            }
            using (var tile = Pattern(240, 20, 3))
            using (var repeated = new Bitmap(240, 640, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(repeated))
                    for (int y = 0; y < repeated.Height; y += tile.Height) graphics.DrawImageUnscaled(tile, 0, y);
                using (var first = Crop(repeated, 0, 600))
                using (var shifted = Crop(repeated, 7, 600))
                using (var stitcher = new Stitcher(first))
                {
                    string message;
                    check(!stitcher.Append(shifted, out message) && stitcher.TotalHeight == 600, "repeating rows with equally good offsets are rejected instead of guessing the scroll distance");
                }
            }
            using (var page = Pattern(240, 900, 3))
            using (var first = Crop(page, 0, 600))
            using (var next = Crop(page, 200, 600))
            using (var stitcher = new Stitcher(first))
            {
                AdjustPixels(next, 1, false);
                string message;
                check(stitcher.Append(next, out message) && stitcher.TotalHeight == 800, "small rendering noise within the matching threshold still stitches at the exact offset");
            }
            using (var page = Pattern(240, 900, 3))
            using (var first = Crop(page, 0, 600))
            using (var misleading = Crop(page, 200, 600))
            using (var next = Crop(page, 200, 600))
            using (var stitcher = new Stitcher(first))
            {
                AdjustPixels(misleading, 0, true);
                string message;
                check(!stitcher.Append(misleading, out message) && stitcher.TotalHeight == 600, "similar content with alternating row displacement has no valid uniform overlap");
                check(stitcher.Append(next, out message) && stitcher.TotalHeight == 800, "a rejected nonuniform overlap does not change the reference frame");
            }
            using (var first = new Bitmap(240, 600, PixelFormat.Format24bppRgb))
            using (var tinted = new Bitmap(240, 600, PixelFormat.Format24bppRgb))
            {
                uint seed = 731;
                int blue = 120;
                for (int y = 0; y < first.Height; y++)
                {
                    seed = unchecked(1664525u * seed + 1013904223u);
                    blue += (seed & 0x80000000u) != 0 ? 6 : -6;
                    if (blue < 12) blue = 24 - blue;
                    if (blue > 240) blue = 480 - blue;
                    for (int x = 0; x < first.Width; x++)
                    {
                        int red = 50 + (x % 2) * 100;
                        first.SetPixel(x, y, Color.FromArgb(red, 80, blue));
                        tinted.SetPixel(x, y, Color.FromArgb(red + 1, 80, blue));
                    }
                }
                using (var stitcher = new Stitcher(first))
                {
                    string message;
                    check(!stitcher.Append(tinted, out message) && stitcher.TotalHeight == 600, "a static slightly tinted image is not treated as a better two pixel scroll match");
                }
            }
            using (var first = Pattern(240, 400, 3))
            {
                var originalPixel = first.GetPixel(100, 100);
                using (var stitcher = new Stitcher(first))
                {
                    using (var graphics = Graphics.FromImage(first)) graphics.Clear(Color.Magenta);
                    using (var result = stitcher.Finish())
                        check(result.GetPixel(100, 100) == originalPixel, "stitcher owns its first snapshot independently of the caller");
                }
                check(first.GetPixel(100, 100).ToArgb() == Color.Magenta.ToArgb(), "disposing a stitcher does not dispose the caller's snapshot");
            }
        }

        internal static Bitmap Pattern(int width, int height, int channel, int seed = 731)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var random = new Random(seed);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                byte[] row = new byte[data.Stride];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = (byte)random.Next(20, 235);
                        for (int c = 0; c < 3; c++) row[x * 3 + c] = channel == 3 || channel == c ? value : (byte)0;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }

        private static Bitmap Crop(Bitmap page, int y, int height)
        {
            return page.Clone(new Rectangle(0, y, page.Width, height), PixelFormat.Format24bppRgb);
        }

        private static void AdjustPixels(Bitmap bitmap, int amount, bool alternateShift)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, bitmap.PixelFormat);
            try
            {
                byte[] row = new byte[data.Stride], previous = new byte[data.Stride];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    IntPtr address = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(address, row, 0, row.Length);
                    if (alternateShift && y % 2 != 0) Marshal.Copy(previous, 0, address, previous.Length);
                    else
                    {
                        for (int i = 0; i < row.Length; i++) row[i] = (byte)Math.Min(255, row[i] + amount);
                        Marshal.Copy(row, 0, address, row.Length);
                        Buffer.BlockCopy(row, 0, previous, 0, row.Length);
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static bool Matches(Bitmap expected, Bitmap actual)
        {
            if (expected.Width != actual.Width || expected.Height < actual.Height) return false;
            BitmapData first = expected.LockBits(new Rectangle(0, 0, actual.Width, actual.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                BitmapData second = actual.LockBits(new Rectangle(Point.Empty, actual.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] a = new byte[actual.Width * 3], b = new byte[a.Length];
                    for (int y = 0; y < actual.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(first.Scan0, y * first.Stride), a, 0, a.Length);
                        Marshal.Copy(IntPtr.Add(second.Scan0, y * second.Stride), b, 0, b.Length);
                        for (int x = 0; x < a.Length; x++) if (a[x] != b[x]) return false;
                    }
                    return true;
                }
                finally { actual.UnlockBits(second); }
            }
            finally { expected.UnlockBits(first); }
        }
    }
}

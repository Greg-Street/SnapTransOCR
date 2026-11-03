using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CapturePerformanceTests
    {
        [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr window, IntPtr rectangle);
        [DllImport("user32.dll")] private static extern int GetUpdateRgn(IntPtr window, IntPtr region, bool erase);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static void RunNative(Action<bool, string> check)
        {
            using (var fixture = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(50, 60, 257, 173), TopMost = true, ShowInTaskbar = false })
            {
                fixture.Paint += delegate(object sender, PaintEventArgs e)
                {
                    e.Graphics.Clear(Color.Black);
                    e.Graphics.FillRectangle(Brushes.Magenta, 0, 0, 128, 86);
                    e.Graphics.FillRectangle(Brushes.Lime, 128, 0, 129, 86);
                    e.Graphics.FillRectangle(Brushes.White, 0, 86, 128, 87);
                };
                fixture.Show(); fixture.Update(); Native.DwmFlush();
                Bitmap retained;
                using (var reference = ScreenCapture.Desktop(fixture.Bounds))
                {
                    retained = ScreenCapture.FrozenDesktop(fixture.Bounds);
                    check(retained.Size == fixture.Size && SamePixels(reference, retained), "DIB frozen capture preserves every RGB pixel and top-down physical coordinates");
                    check(retained.PixelFormat == PixelFormat.Format32bppPArgb && retained.GetPixel(200, 120).A == 255, "DIB frozen capture preserves opaque black pixels");
                }
                using (retained)
                {
                    using (var expectedFrame = (Bitmap)retained.Clone())
                    using (var overlay = new CaptureForm("", new AppSettings { CaptureMagnifier = false }, 0, (Bitmap)retained.Clone(), fixture.Bounds))
                    {
                        using (var graphics = Graphics.FromImage(expectedFrame))
                        using (var shade = new SolidBrush(Color.FromArgb(115, 10, 13, 20))) graphics.FillRectangle(shade, 0, 0, expectedFrame.Width, expectedFrame.Height);
                        overlay.Show(); overlay.Update(); Native.DwmFlush();
                        using (var firstFrame = ScreenCapture.Desktop(overlay.Bounds))
                            check(SamePixels(expectedFrame, firstFrame), "first native screenshot frame is fully painted without uninitialized background pixels");
                        overlay.Close();
                    }
                    int handles = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
                    for (int i = 0; i < 32; i++) using (var next = ScreenCapture.FrozenDesktop(fixture.Bounds)) { }
                    check(GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0) <= handles, "repeated frozen captures release their native DIB and device contexts");
                    check(retained.GetPixel(5, 5).ToArgb() == Color.Magenta.ToArgb() && retained.GetPixel(200, 5).ToArgb() == Color.Lime.ToArgb(), "returned frozen bitmap remains valid after the native DIB is released and reused");
                    using (var stream = new MemoryStream())
                    {
                        retained.Save(stream, ImageFormat.Png); stream.Position = 0;
                        using (var png = new Bitmap(stream)) check(SamePixels(retained, png), "native frozen screenshot PNG export is lossless");
                    }
                }
                check(fixture.Visible, "frozen screenshot does not hide the captured application window");
            }
        }

        internal static void Run(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            try
            {
                DrawingPixels(check);
                InitialPointer(check);
                CaptureLoss(check);
                foreach (bool magnifier in new[] { false, true })
                using (var fixture = new Bitmap(1200, 800, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(fixture))
                    using (var brush = new LinearGradientBrush(Point.Empty, new Point(1200, 800), Color.LightBlue, Color.DarkSlateGray)) g.FillRectangle(brush, 0, 0, 1200, 800);
                    using (var overlay = new CaptureForm("", new AppSettings { CaptureMagnifier = magnifier }, 0, (Bitmap)fixture.Clone(), new Rectangle(-1200, -200, 1200, 800)))
                    using (var partial = new Bitmap(1200, 800))
                    using (var expected = new Bitmap(1200, 800))
                    using (var partialGraphics = Graphics.FromImage(partial))
                    using (var expectedGraphics = Graphics.FromImage(expected))
                    {
                        check(!overlay.Controls.OfType<RoundedPanel>().Any(), "capture defers annotation controls until a selection exists");
                        overlay.Location = new Point(-32000, -32000); overlay.ShowInTaskbar = false; overlay.Show();
                        typeof(CaptureForm).GetField("dragging", Private).SetValue(overlay, true);
                        typeof(CaptureForm).GetField("start", Private).SetValue(overlay, new Point(100, 80));
                        var paint = typeof(CaptureForm).GetMethod("OnPaint", Private);
                        var move = typeof(CaptureForm).GetMethod("OnMouseMove", Private);
                        paint.Invoke(overlay, new object[] { new PaintEventArgs(partialGraphics, overlay.ClientRectangle) });
                        bool pixelsMatch = true, smallUpdate = false;
                        IntPtr nativeRegion = CreateRectRgn(0, 0, 0, 0);
                        try
                        {
                            Point[] path = { new Point(1000, 650), new Point(1001, 651), new Point(998, 648), new Point(50, 40), new Point(49, 39), new Point(1199, 799), new Point(100, 80) };
                            for (int i = 0; i < path.Length; i++)
                            {
                                ValidateRect(overlay.Handle, IntPtr.Zero);
                                move.Invoke(overlay, new object[] { new MouseEventArgs(MouseButtons.Left, 0, path[i].X, path[i].Y, 0) });
                                GetUpdateRgn(overlay.Handle, nativeRegion, false);
                                using (var region = Region.FromHrgn(nativeRegion))
                                {
                                    if (i == 1)
                                    using (var identity = new Matrix()) smallUpdate = region.GetRegionScans(identity).Sum(rect => rect.Width * rect.Height) < 1200 * 800 / 8;
                                    partialGraphics.SetClip(region, CombineMode.Replace);
                                    paint.Invoke(overlay, new object[] { new PaintEventArgs(partialGraphics, Rectangle.Round(region.GetBounds(partialGraphics))) });
                                }
                                expectedGraphics.ResetClip();
                                paint.Invoke(overlay, new object[] { new PaintEventArgs(expectedGraphics, overlay.ClientRectangle) });
                                pixelsMatch &= SamePixels(partial, expected);
                            }
                        }
                        finally { DeleteObject(nativeRegion); }
                        check(smallUpdate, "selection movement repaints only changed edges and overlays, magnifier=" + magnifier);
                        check(pixelsMatch, "partial selection repaint matches a full repaint through growth, reversal and corners, magnifier=" + magnifier);
                        overlay.PreviewSelection(new Rectangle(40, 50, 300, 200));
                        check(overlay.Controls.OfType<ImageCanvas>().Count() == 1 && overlay.Controls.OfType<RoundedPanel>().Count() == 1, "selecting creates the annotation toolbar once");
                        overlay.PreviewSelection(new Rectangle(60, 70, 320, 220));
                        check(overlay.Controls.OfType<RoundedPanel>().Count() == 1, "reselection reuses annotation controls");
                    }
                }
            }
            finally { Ui.RenderOffscreen = previous; }
        }
        private static void InitialPointer(Action<bool, string> check)
        {
            Point pointer = Cursor.Position;
            Rectangle bounds = new Rectangle(pointer.X - 400, pointer.Y - 280, 800, 600);
            using (var frozen = new Bitmap(bounds.Width, bounds.Height))
            using (var overlay = new CaptureForm("", new AppSettings { CaptureMagnifier = true }, 0, (Bitmap)frozen.Clone(), bounds))
            using (var frame = new Bitmap(bounds.Width, bounds.Height))
            using (var graphics = Graphics.FromImage(frame))
            {
                IntPtr handle = overlay.Handle;
                Point actual = (Point)typeof(CaptureForm).GetField("mouse", Private).GetValue(overlay);
                check(actual == overlay.PointToClient(Cursor.Position), "first capture frame initializes the magnifier from the physical cursor position before any mouse movement");
                var paint = typeof(CaptureForm).GetMethod("OnPaint", Private);
                paint.Invoke(overlay, new object[] { new PaintEventArgs(graphics, overlay.ClientRectangle) });
                check(frame.GetPixel(22, 22).ToArgb() != Color.White.ToArgb(), "first capture frame does not draw a stray white magnifier square at the screen origin");
                int x = Math.Min(overlay.ClientSize.Width - 130, actual.X + 25) - 3;
                int y = Math.Min(overlay.ClientSize.Height - 145, actual.Y + 25) - 3;
                check(frame.GetPixel(x, y).ToArgb() == Color.White.ToArgb(), "first capture frame draws the magnifier beside the current cursor");
                typeof(CaptureForm).GetMethod("OnMouseMove", Private).Invoke(overlay, new object[] { new MouseEventArgs(MouseButtons.None, 0, -10, 20, 0) });
                paint.Invoke(overlay, new object[] { new PaintEventArgs(graphics, overlay.ClientRectangle) });
                check(frame.GetPixel(12, 42).ToArgb() != Color.White.ToArgb(), "capture does not draw a clamped magnifier after the pointer leaves its client area");
            }
        }
        private static void CaptureLoss(Action<bool, string> check)
        {
            using (var overlay = new CaptureForm("", new AppSettings { CaptureMagnifier = false }, 2, new Bitmap(600, 400), new Rectangle(-30000, -30000, 600, 400)))
            {
                overlay.ShowInTaskbar = false; overlay.Show();
                Native.SendMessage(overlay.Handle, 0x201, new IntPtr(1), MousePoint(40, 50));
                Native.SendMessage(overlay.Handle, 0x200, new IntPtr(1), MousePoint(240, 180));
                overlay.Capture = false;
                Native.SendMessage(overlay.Handle, 0x200, IntPtr.Zero, MousePoint(300, 210));
                Native.SendMessage(overlay.Handle, 0x202, IntPtr.Zero, MousePoint(300, 210));
                check(!(bool)typeof(CaptureForm).GetField("dragging", Private).GetValue(overlay) &&
                    ((Rectangle)typeof(CaptureForm).GetField("selected", Private).GetValue(overlay)).IsEmpty && overlay.Captured == null,
                    "lost mouse capture cancels unfinished selection without triggering screenshot translation");
                Rectangle selection = new Rectangle(40, 50, 200, 130);
                overlay.PreviewSelection(selection);
                ImageCanvas canvas = overlay.Controls.OfType<ImageCanvas>().Single();
                typeof(CaptureForm).GetMethod("BeginTransform", Private).Invoke(overlay, new object[] { -1, overlay.PointToScreen(new Point(100, 100)) });
                Native.SendMessage(overlay.Handle, 0x200, new IntPtr(1), MousePoint(140, 130));
                overlay.Capture = false;
                Native.SendMessage(overlay.Handle, 0x200, IntPtr.Zero, MousePoint(200, 200));
                check(!(bool)typeof(CaptureForm).GetField("transforming", Private).GetValue(overlay) &&
                    (Rectangle)typeof(CaptureForm).GetField("selected", Private).GetValue(overlay) == selection &&
                    ReferenceEquals(canvas, overlay.Controls.OfType<ImageCanvas>().Single()) && canvas.Visible && overlay.Controls.OfType<RoundedPanel>().Single().Visible,
                    "lost mouse capture restores the previous selection and existing annotation canvas");
            }
        }
        private static IntPtr MousePoint(int x, int y) { return new IntPtr(unchecked(y << 16 | x & 0xffff)); }
        private static void DrawingPixels(Action<bool, string> check)
        {
            using (var original = new Bitmap(257, 73, PixelFormat.Format32bppRgb))
            {
                var bytes = new byte[original.Width * original.Height * 4];
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    bytes[i] = (byte)(i * 13); bytes[i + 1] = (byte)(i * 7); bytes[i + 2] = (byte)(i / 4);
                    bytes[i + 3] = (byte)(i / 4); // RGB must ignore undefined native alpha bytes.
                }
                BitmapData data = original.LockBits(new Rectangle(Point.Empty, original.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
                finally { original.UnlockBits(data); }
                using (var drawing = ScreenCapture.ForDisplay(original))
                using (var stream = new MemoryStream())
                {
                    check(drawing.PixelFormat == PixelFormat.Format32bppPArgb && SamePixels(original, drawing), "display conversion ignores native alpha bytes and preserves every opaque RGB pixel");
                    drawing.Save(stream, ImageFormat.Png); stream.Position = 0;
                    using (var png = new Bitmap(stream)) check(SamePixels(original, png), "PNG export preserves every RGB pixel after display conversion");
                }
            }
        }
        private static bool SamePixels(Bitmap first, Bitmap second)
        {
            Rectangle bounds = new Rectangle(Point.Empty, first.Size);
            BitmapData a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int length = a.Stride * first.Height;
                var left = new byte[length]; var right = new byte[length];
                Marshal.Copy(a.Scan0, left, 0, length); Marshal.Copy(b.Scan0, right, 0, length);
                return left.SequenceEqual(right);
            }
            finally { first.UnlockBits(a); second.UnlockBits(b); }
        }
    }
}

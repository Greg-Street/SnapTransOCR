using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class MosaicRenderTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            foreach (PixelFormat format in new[] { PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb, PixelFormat.Format32bppPArgb })
            foreach (bool cropped in new[] { false, true })
            using (var canvas = new ImageCanvas(Source(format)))
            {
                AddMarks(canvas);
                if (cropped) canvas.AddMark(new Mark { Kind = Tool.Crop, Bounds = new RectangleF(70, 60, 700, 460) });
                Rectangle crop = canvas.Crop;
                using (var full = canvas.Render())
                foreach (Rectangle region in new[] { new Rectangle(crop.Left, crop.Top, 120, 90), new Rectangle(123, 155, 240, 100), new Rectangle(345, 170, 200, 140), new Rectangle(crop.Right - 90, crop.Bottom - 60, 90, 60) })
                using (var expected = full.Clone(new Rectangle(region.X - crop.X, region.Y - crop.Y, region.Width, region.Height), PixelFormat.Format24bppRgb))
                using (var actual = (Bitmap)typeof(ImageCanvas).GetMethod("Render", Private, null, new[] { typeof(Rectangle) }, null).Invoke(canvas, new object[] { region }))
                    check(Equal(expected, actual), "local mosaic source equals composited crop including all annotations: " + format + ", crop=" + cropped + ", " + region);

                // Drive the real drawing action, then compare its pixels with the previous whole-image path.
                canvas.Size = new Size(900, 600); canvas.PixelView = true; canvas.Fit(); canvas.Tool = Tool.Mosaic;
                Rectangle selected = new Rectangle(150, 150, 240, 120);
                using (var before = canvas.Render())
                using (var patch = before.Clone(new Rectangle(selected.X - crop.X, selected.Y - crop.Y, selected.Width, selected.Height), PixelFormat.Format24bppRgb))
                using (var expectedPixels = new Bitmap(selected.Width / 12, selected.Height / 12))
                {
                    using (var graphics = Graphics.FromImage(expectedPixels)) graphics.DrawImage(patch, new Rectangle(Point.Empty, expectedPixels.Size));
                    Mouse(canvas, "OnMouseDown", selected.Left - crop.Left, selected.Top - crop.Top);
                    Mouse(canvas, "OnMouseMove", selected.Right - crop.Left, selected.Bottom - crop.Top);
                    Mouse(canvas, "OnMouseUp", selected.Right - crop.Left, selected.Bottom - crop.Top);
                    var marks = (System.Collections.Generic.List<Mark>)typeof(ImageCanvas).GetField("marks", Private).GetValue(canvas);
                    Mark actual = marks.Last();
                    check(actual.Kind == Tool.Mosaic && actual.Bounds == selected && Equal(expectedPixels, actual.Pixels), "mosaic mouse action retains composited source at original coordinates: " + format + ", crop=" + cropped);
                    canvas.Undo();
                    using (var restored = canvas.Render()) check(Equal(before, restored), "undo restores all pixels after local mosaic: " + format + ", crop=" + cropped);
                    canvas.Redo();
                    check(canvas.Crop == crop, "redo preserves crop after local mosaic: " + format + ", crop=" + cropped);
                }
            }
        }
        private static Bitmap Source(PixelFormat format)
        {
            var image = new Bitmap(900, 600, format);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(format == PixelFormat.Format32bppPArgb ? Color.Transparent : Color.WhiteSmoke);
                for (int y = 0; y < image.Height; y += 37) graphics.FillRectangle(Brushes.LightBlue, 20, y, image.Width - 40, 19);
            }
            return image;
        }
        private static void AddMarks(ImageCanvas canvas)
        {
            canvas.AddMark(new Mark { Kind = Tool.Rectangle, Bounds = new RectangleF(90, 100, 700, 270), Color = Color.Red, Width = 4 });
            canvas.AddMark(new Mark { Kind = Tool.Ellipse, Bounds = new RectangleF(250, 135, 200, 120), Color = Color.Orange, Width = 3 });
            canvas.AddMark(new Mark { Kind = Tool.Text, Bounds = new RectangleF(120, 160, 600, 120), Text = "Synthetic annotation 合成标注", FontSize = 28, Color = Color.Navy });
            canvas.AddMark(new Mark { Kind = Tool.Arrow, First = new PointF(40, 40), Last = new PointF(480, 300), Color = Color.Green, Width = 5 });
            canvas.AddMark(new Mark { Kind = Tool.Line, First = new PointF(50, 210), Last = new PointF(560, 220), Color = Color.Purple, Width = 2 });
            var pen = new Mark { Kind = Tool.Pen, Color = Color.DarkCyan, Width = 6 };
            pen.Points.AddRange(new[] { new PointF(100, 140), new PointF(310, 320), new PointF(500, 150) }); canvas.AddMark(pen);
            var pixels = new Bitmap(10, 10);
            using (var graphics = Graphics.FromImage(pixels)) { graphics.Clear(Color.Gray); graphics.FillRectangle(Brushes.Black, 0, 0, 5, 5); }
            canvas.AddMark(new Mark { Kind = Tool.Mosaic, Bounds = new RectangleF(350, 170, 120, 120), Pixels = pixels });
        }
        private static void Mouse(Control control, string method, int x, int y)
        {
            typeof(ImageCanvas).GetMethod(method, Private).Invoke(control, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        }
        private static bool Equal(Bitmap first, Bitmap second)
        {
            if (first.Size != second.Size) return false;
            return Bytes(first).SequenceEqual(Bytes(second));
        }
        private static byte[] Bytes(Bitmap image)
        {
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { var bytes = new byte[data.Stride * data.Height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); return bytes; }
            finally { image.UnlockBits(data); }
        }
    }
}

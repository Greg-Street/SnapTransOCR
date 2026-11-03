using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ImageCanvasCaptureTests
    {
        internal static void Run(Action<bool, string> check)
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false, ClientSize = new Size(300, 200) })
            using (var canvas = new ImageCanvas(new Bitmap(300, 200)) { PixelView = true })
            {
                form.Controls.Add(canvas); form.Show(); canvas.Fit();
                foreach (Tool tool in new[] { Tool.Rectangle, Tool.Ellipse, Tool.Line, Tool.Arrow, Tool.Pen, Tool.Mosaic, Tool.Crop })
                {
                    canvas.Tool = tool;
                    Mouse(canvas, 0x201, 1, 20, 20); Mouse(canvas, 0x200, 1, 80, 60);
                    canvas.Capture = false;
                    Mouse(canvas, 0x200, 0, 150, 120); Mouse(canvas, 0x202, 0, 150, 120);
                    check(Field<Mark>(canvas, "current") == null && Field<int>(canvas, "count") == 0 && canvas.Crop == new Rectangle(0, 0, 300, 200), tool + " discards an unfinished annotation after losing mouse capture");
                    Mouse(canvas, 0x201, 1, 20, 20); Mouse(canvas, 0x200, 1, 80, 60); Mouse(canvas, 0x202, 0, 80, 60);
                    check(Field<int>(canvas, "count") == 1 && Field<Mark>(canvas, "current") == null && !canvas.Capture, tool + " still commits exactly once after a normal mouse release");
                    canvas.Undo();
                }
                canvas.Tool = Tool.Pan;
                Mouse(canvas, 0x207, 16, 20, 20); Mouse(canvas, 0x200, 16, 60, 50);
                PointF stopped = Field<PointF>(canvas, "pan");
                canvas.Capture = false;
                Mouse(canvas, 0x200, 0, 150, 120);
                check(Field<PointF>(canvas, "pan") == stopped && !Field<bool>(canvas, "panning") && canvas.Cursor == Cursors.Default, "losing mouse capture stops panning without moving the image on subsequent hover");
                form.Close();
            }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
        private static void Mouse(Control control, int message, int buttons, int x, int y) { Native.SendMessage(control.Handle, message, new IntPtr(buttons), new IntPtr(x | y << 16)); }
    }
}

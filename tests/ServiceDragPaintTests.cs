using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ServiceDragPaintTests
    {
        internal static void Run(Action<bool, string> check)
        {
            using (var control = new ServiceOrderControl(new[] { "volc", "baidu" }, value => value))
            {
                IntPtr handle = control.Handle;
                Mouse(control, "OnMouseDown", 12, 15);
                Mouse(control, "OnMouseMove", 12, 25);
                using (var bitmap = new Bitmap(control.Width, control.Height))
                {
                    control.DrawToBitmap(bitmap, control.ClientRectangle);
                    check(bitmap.GetPixel(control.Width - 12, 7).ToArgb() != Ui.Accent.ToArgb() &&
                        bitmap.GetPixel(control.Width - 12, control.ItemHeight + 5).ToArgb() == Ui.Accent.ToArgb(),
                        "dragged service follows the pointer between row positions");
                }
                Mouse(control, "OnMouseMove", 12, control.ItemHeight + 12);
                int invalidations = 0;
                control.Invalidated += delegate { invalidations++; };
                for (int i = 0; i < 100; i++) Mouse(control, "OnMouseMove", 12, control.ItemHeight + 12);
                check(invalidations == 0, "repeated pointer coordinates do not repaint the service drag");
                Mouse(control, "OnMouseMove", 12, control.ItemHeight + 10);
                check(invalidations == 1, "moving within a target row repaints the following service once");
                Mouse(control, "OnMouseMove", 12, 15);
                check(invalidations == 2, "changing a service drop target schedules one further buffered repaint");
                Mouse(control, "OnMouseMove", 12, control.ItemHeight + 12);
                Mouse(control, "OnMouseUp", 12, control.ItemHeight + 12);
                check(control.Order.SequenceEqual(new[] { "baidu", "volc" }) && !control.Capture, "batched service reorder commits the chosen drop once and releases capture");
                using (var bitmap = new Bitmap(control.Width, control.Height))
                {
                    control.DrawToBitmap(bitmap, control.ClientRectangle);
                    check(bitmap.GetPixel(40, control.ItemHeight / 2).ToArgb() != Color.White.ToArgb() && bitmap.GetPixel(40, control.ItemHeight * 3 / 2).ToArgb() != Color.White.ToArgb(), "both service rows remain painted after the batched move");
                }
            }
        }
        private static void Mouse(Control control, string method, int x, int y)
        {
            control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        }
    }
}

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CaptureDpiTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var bounds = new Rectangle(-1600, -200, 2000, 1200);
            using (var overlay = new CaptureForm("", new AppSettings(), 0, new Bitmap(bounds.Width, bounds.Height), bounds))
            {
                overlay.PreviewSelection(new Rectangle(1450, 230, 300, 150));
                overlay.Location = new Point(-32000, -32000);
                overlay.Complete(CaptureAction.Ocr);
                using (var image = overlay.Captured)
                    check(overlay.ScreenRegion == new Rectangle(-150, 30, 300, 150) && image.Size == new Size(300, 150), "capture coordinates use frozen physical desktop origin across monitors");
            }
            using (var overlay = new CaptureForm("", new AppSettings(), 0, new Bitmap(800, 600), new Rectangle(0, 0, 800, 600)))
            {
                overlay.PreviewSelection(new Rectangle(40, 30, 200, 100));
                var canvas = overlay.Controls.OfType<ImageCanvas>().Single();
                canvas.AddMark(new Mark { Kind = Tool.Crop, Bounds = new RectangleF(10, 15, 120, 60) });
                overlay.Complete(CaptureAction.Copy);
                using (var image = overlay.Captured)
                    check(overlay.ScreenRegion == new Rectangle(50, 45, 120, 60) && image.Size == new Size(120, 60), "cropped screenshot physical region follows the crop");
            }
            using (var overlay = new CaptureForm("", new AppSettings(), 1, new Bitmap(400, 300), new Rectangle(0, 0, 400, 300)))
            {
                overlay.PreviewSelection(new Rectangle(20, 30, 120, 80));
                var mouse = new MouseEventArgs(MouseButtons.Left, 1, 3, 3, 0);
                typeof(CaptureForm).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { mouse });
                typeof(CaptureForm).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { mouse });
                check(overlay.Captured == null && !overlay.Controls.OfType<ImageCanvas>().Any(), "empty re-selection does not OCR the previous hidden capture");
            }
            using (var overlay = new CaptureForm("", new AppSettings(), 0, new Bitmap(800, 600), new Rectangle(-100, 20, 800, 600)))
            {
                Rectangle before = overlay.Bounds;
                IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
                try
                {
                    var suggested = new Native.RECT { Left = 50, Top = 70, Right = 1250, Bottom = 970 };
                    Marshal.StructureToPtr(suggested, pointer, false);
                    Message message = Message.Create(overlay.Handle, 0x02e0, new IntPtr(144 | (144 << 16)), pointer);
                    object[] arguments = { message };
                    typeof(CaptureForm).GetMethod("WndProc", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, arguments);
                    check(overlay.Bounds == before && overlay.ClientSize == new Size(800, 600), "DPI change cannot scale or relocate the frozen screenshot pixels");
                }
                finally { Marshal.FreeHGlobal(pointer); }
            }
            using (var canvas = new ImageCanvas(new Bitmap(500, 240)) { PixelView = true, Size = new Size(500, 240) })
            {
                canvas.Fit(); canvas.BeginText(new PointF(30, 35));
                canvas.TextInput.Text = "first line\r\nsecond line";
                check(canvas.TextInput.Multiline && canvas.TextInput.AcceptsReturn, "capture text accepts inline line breaks");
                Point origin = canvas.TextInput.Location;
                origin.Offset(canvas.TextInput.Parent.Location);
                canvas.CommitText();
                var marks = (System.Collections.Generic.List<Mark>)typeof(ImageCanvas).GetField("marks", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
                check(marks.Single().First == new PointF(origin.X, origin.Y), "exported text retains the inline text input position");
            }
            using (var owner = new Form())
            using (var button = new GlyphButton("ocr", "OCR 文字识别", delegate { }))
            {
                owner.Controls.Add(button);
                var tips = typeof(GlyphButton).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(field => field.FieldType == typeof(ToolTip)).Select(field => (ToolTip)field.GetValue(button));
                check(tips.Any(tip => tip != null && tip.GetToolTip(button) == "OCR 文字识别"), "screenshot icon has a visible hover tooltip");
            }
            using (var styles = new AnnotationOptions(3))
            {
                bool sizesMatch = true;
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 1f })
                {
                    styles.SetScale(scale);
                    sizesMatch &= styles.Controls.OfType<GlyphButton>().First().Width == (int)(30 * scale)
                        && styles.Controls.OfType<NumericUpDown>().First().Width == (int)(68 * scale);
                }
                check(sizesMatch, "capture style controls scale for 100/125/150/200 percent without accumulating rounding");
            }
        }
    }
}

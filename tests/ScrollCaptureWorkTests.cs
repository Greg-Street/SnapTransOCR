using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ScrollCaptureWorkTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        // Serialized UI suite: uses synthetic pixels and hidden handles; no desktop capture or hotkey registration.
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen;
            SynchronizationContext context = SynchronizationContext.Current;
            Ui.RenderOffscreen = true;
            try
            {
                CompleteRegion(check);
                using (var pump = new Control())
                using (var application = new ApplicationContext())
                {
                    IntPtr handle = pump.Handle;
                    Exception failure = null;
                    pump.BeginInvoke((Action)(async delegate
                    {
                        try { await CaptureWork(check); }
                        catch (Exception ex) { failure = ex; }
                        finally { application.ExitThread(); }
                    }));
                    Application.Run(application);
                    if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = offscreen; SynchronizationContext.SetSynchronizationContext(context); }
        }

        private static void CompleteRegion(Action<bool, string> check)
        {
            using (var overlay = new CaptureForm("", new AppSettings(), 0, new Bitmap(400, 300), new Rectangle(-600, -200, 400, 300)))
            {
                overlay.PreviewSelection(new Rectangle(30, 40, 140, 140));
                var canvas = overlay.Controls.OfType<ImageCanvas>().Single();
                canvas.AddMark(new Mark { Kind = Tool.Crop, Bounds = new RectangleF(5, 7, 100, 120) });
                canvas.BeginText(new PointF(15, 20));
                canvas.TextInput.Text = "not part of the fresh scrolling capture";
                int commits = 0;
                canvas.Changed += delegate { commits++; };
                overlay.Complete(CaptureAction.Scroll);
                using (overlay.Captured)
                {
                    check(overlay.Action == CaptureAction.Scroll && overlay.Captured == null && commits == 0,
                        "starting scroll capture returns no rendered bitmap and does not commit unused annotations");
                    check(overlay.ScreenRegion == new Rectangle(-565, -153, 100, 120),
                        "scroll capture keeps physical desktop and crop coordinates when returning only the region");
                }
            }
            using (var pixels = new Bitmap(240, 180))
            {
                using (var graphics = Graphics.FromImage(pixels)) graphics.Clear(Color.Coral);
                using (var overlay = new CaptureForm("", new AppSettings(), 0, (Bitmap)pixels.Clone(), new Rectangle(10, 20, 240, 180)))
                {
                    overlay.PreviewSelection(new Rectangle(20, 30, 100, 120));
                    overlay.Complete(CaptureAction.Copy);
                    using (var captured = overlay.Captured)
                        check(captured != null && captured.Size == new Size(100, 120) && captured.GetPixel(50, 60).ToArgb() == Color.Coral.ToArgb(),
                            "ordinary screenshot completion still exports the selected image pixels");
                }
            }
        }

        private static async Task CaptureWork(Action<bool, string> check)
        {
            var region = new Rectangle(-30000, -30000, 100, 140);
            using (var first = new Bitmap(region.Width, region.Height))
            {
                int captures = 0;
                Func<Rectangle, Bitmap> capture = bounds => { captures++; return (Bitmap)first.Clone(); };
                using (var form = new ScrollForm(region, first, capture))
                {
                    form.Bounds = region;
                    IntPtr handle = form.Handle;
                    Task pending = (Task)typeof(ScrollForm).GetMethod("CaptureFrame", Private).Invoke(form, null);
                    check(!pending.IsCompleted, "overlapping scroll controls wait before obtaining the next frame");
                    form.Close();
                    check(!form.IsDisposed, "cancellation waits for the active capture continuation before disposing its images");
                    await pending;
                    check(captures == 0 && form.IsDisposed, "cancelling during the hide wait captures and appends no frame, then releases the scroll form");
                }
                captures = 0;
                using (var form = new ScrollForm(region, first, capture))
                {
                    form.Bounds = new Rectangle(-28000, -28000, 200, 100);
                    await (Task)typeof(ScrollForm).GetMethod("CaptureFrame", Private).Invoke(form, null);
                    var stitcher = (Stitcher)typeof(ScrollForm).GetField("stitcher", Private).GetValue(form);
                    check(captures == 1 && stitcher.FrameCount == 1 && !form.IsDisposed,
                        "an uncancelled capture still reads one frame and rejects an unchanged synthetic image");
                }
            }
        }
    }
}

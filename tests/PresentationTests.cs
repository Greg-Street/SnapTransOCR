using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class PresentationTests
    {
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        internal static void Run(Action<bool, string> check, string output)
        {
            bool offscreen = Ui.RenderOffscreen;
            Ui.RenderOffscreen = false;
            try
            {
                using (var background = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.LimeGreen, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(40, 40, 1100, 850), TopMost = true, ShowInTaskbar = false })
                using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" }, ScreenshotCopyOnConfirm = false, HistoryEnabled = false }, false, persistSettings: delegate { }))
                {
                    var main = (MainForm)typeof(DesktopApp).GetField("main", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                    try
                    {
                    background.Show(); background.Update();
                    main.Location = new Point(100, 100);
                    main.TopMost = true;
                    main.SetSource("Presentation regression sample");
                    typeof(DesktopApp).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                    var options = (SettingsForm)typeof(DesktopApp).GetField("settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
                    options.Location = new Point(-30000, -30000);
                    var ocr = (OcrForm)typeof(DesktopApp).GetMethod("OcrWindow", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                    ocr.StartPosition = FormStartPosition.Manual; ocr.Location = new Point(-30000, -30000); ocr.Show();
                    main.Show(); main.Activate();
                    int hiddenWindows = 0;
                    foreach (Form form in new Form[] { main, options, ocr })
                        form.VisibleChanged += delegate(object sender, EventArgs e) { if (!((Form)sender).Visible) hiddenWindows++; };
                    Pump();
                    using (var before = new Bitmap(1, 1))
                    {
                        using (var graphics = Graphics.FromImage(before)) graphics.CopyFromScreen(new Point(200, 200), Point.Empty, before.Size);
                        check(before.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "translation window is visibly painted before capture");
                    }
                    main.RefreshSettings(new AppSettings { TranslateHideOnBlur = true, TranslateTopmost = true });
                    bool ownWindowIncluded = false, windowsRemainVisible = false;
                    Rectangle capturedRegion = Rectangle.Empty;
                    using (var complete = new System.Windows.Forms.Timer { Interval = 20 })
                    {
                        complete.Tick += delegate
                        {
                            var overlay = Application.OpenForms.OfType<CaptureForm>().FirstOrDefault();
                            if (overlay == null) return;
                            complete.Stop();
                            var pixels = (Bitmap)typeof(CaptureForm).GetField("desktop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                            var bounds = (Rectangle)typeof(CaptureForm).GetField("desktopBounds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                            ownWindowIncluded = pixels.GetPixel(200 - bounds.X, 200 - bounds.Y).ToArgb() == Color.White.ToArgb();
                            windowsRemainVisible = main.Visible && options.Visible && ocr.Visible;
                            overlay.PreviewSelection(new Rectangle(40, 40, 80, 50));
                            overlay.Complete(CaptureAction.Pin);
                            capturedRegion = overlay.ScreenRegion;
                        };
                        complete.Start();
                        var task = (Task)typeof(DesktopApp).GetMethod("Capture", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, new object[] { 0, 0 });
                        await task;
                    }
                    check(main.Visible, "translation window stays visible after a screenshot");
                    check(Application.OpenForms.OfType<PinForm>().Single().Bounds == capturedRegion, "completed screenshot pin exactly overlaps its source selection in physical pixels");
                    check(ownWindowIncluded, "screenshot includes the visible translation window");
                    check(windowsRemainVisible && hiddenWindows == 0, "capture never hides translation, OCR or settings windows, including hide-on-blur mode");
                    Pump();
                    check(Painted(main, Path.Combine(output, "translation-after-capture-screen.png")), "translation frame is painted immediately after the actual capture workflow");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { typeof(DesktopApp).GetMethod("Exit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
                using (var background = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Magenta, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(60, 60, 1100, 820), ShowInTaskbar = false })
                using (var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(90, 90), TopMost = true, ShowInTaskbar = false })
                {
                    background.Show();
                    main.SetSource("Window repaint sample");
                    bool painted = true;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        main.Show(); Pump();
                        painted &= Painted(main, pass == 2 ? Path.Combine(output, "translation-restored-screen.png") : null);
                        main.Hide(); Pump();
                    }
                    check(painted, "native screen pixels repaint after hide and show without hover or input focus");
                }
                using (var settings = new SettingsForm(new AppSettings(), delegate { }) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80), TopMost = true, ShowInTaskbar = false })
                {
                    settings.Show(); Pump();
                    bool edges = true;
                    foreach (Point local in new[] { new Point(3, settings.Height / 2), new Point(settings.Width - 3, settings.Height / 2), new Point(settings.Width / 2, 3), new Point(settings.Width / 2, settings.Height - 3) })
                        edges &= WindowFromPoint(settings.PointToScreen(local)) == settings.Handle;
                    check(edges, "actual settings edge hit targets belong to the resizable form rather than docked panels");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static bool Painted(Form form, string path)
        {
            using (var pixels = new Bitmap(form.Width, form.Height))
            {
                using (var graphics = Graphics.FromImage(pixels)) graphics.CopyFromScreen(form.Location, Point.Empty, pixels.Size);
                if (path != null) pixels.Save(path);
                return new[] { new Point(form.Width / 2, 20), new Point(2, form.Height / 2), new Point(form.Width - 3, form.Height / 2) }
                    .All(point => pixels.GetPixel(point.X, point.Y).ToArgb() == Ui.Background.ToArgb());
            }
        }
        private static void Pump()
        {
            for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(10); }
            Native.DwmFlush();
        }
    }
}

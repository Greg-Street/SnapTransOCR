using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CaptureHistoryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-capture-history-" + Guid.NewGuid().ToString("N"));
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            var settings = new AppSettings { Hotkeys = new[] { "", "", "", "" }, PinTopmost = false,
                ScreenshotCopyOnConfirm = false, TranslateNearCursor = false, OcrProvider = "tencent", TencentId = "synthetic-id", TencentSecret = "synthetic-secret" };
            try
            {
                using (var handler = new OcrHandler())
                using (var app = new DesktopApp(settings, true))
                {
                    var store = new HistoryStore(directory);
                    typeof(DesktopApp).GetField("history", Private).SetValue(app, store);
                    Field<CloudApi>(app, "api").Dispose();
                    typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                    var main = Field<MainForm>(app, "main");
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                        try
                        {
                            var gate = Field<SemaphoreSlim>(store, "gate");
                            await gate.WaitAsync();
                            Task pending;
                            try
                            {
                                pending = Invoke(app, "CompleteCapture", Sample(), CaptureAction.Pin, (Point?)new Point(-30000, -30000));
                                var pin = Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single();
                                var writes = Field<HashSet<Task>>(app, "historyWrites");
                                check(pending.IsCompleted && pin.Visible && writes.Count == 1 && !writes.Single().IsCompleted, "a confirmed screenshot releases its interaction task while tracking the blocked history write separately");
                                pin.Close();
                            }
                            finally { gate.Release(); }
                            await pending;
                            await Task.WhenAll(Field<HashSet<Task>>(app, "historyWrites").ToArray());
                            var records = await store.LoadAsync();
                            check(records.Length == 1 && records[0].Kind == "capture" && records[0].Source.Length == 0 && records[0].Translation.Length == 0 && records[0].OcrProvider.Length == 0, "a confirmed screenshot pin adds one capture record without OCR metadata or duplicate text entries");
                            using (var loaded = await store.LoadImageAsync(records[0].Id))
                                check(loaded.Size == new Size(40, 30) && loaded.GetPixel(0, 0).ToArgb() == Color.CadetBlue.ToArgb(), "closing a pin during history persistence does not dispose or corrupt the retained screenshot");

                            await Invoke(app, "Ocr", Sample(), false);
                            records = await store.LoadAsync();
                            check(handler.Calls == 1 && records.Length == 2 && records[0].Kind == "ocr" && records[0].Source == "recognized example" && records[0].Translation.Length == 0, "standalone OCR stores exactly one OCR result with recognized text");
                            using (var loaded = await store.LoadImageAsync(records[0].Id))
                                check(loaded != null && loaded.GetPixel(0, 0).ToArgb() == Color.CadetBlue.ToArgb(), "standalone OCR history retains the associated lossless screenshot");

                            await gate.WaitAsync();
                            try
                            {
                                pending = Invoke(app, "OpenEditor", Sample(), true);
                                var editor = Field<List<Form>>(app, "imageWindows").OfType<EditorForm>().Single();
                                editor.Location = new Point(-30000, -30000);
                                check(pending.IsCompleted && editor.Visible && Field<HashSet<Task>>(app, "historyWrites").Any(write => !write.IsCompleted), "a long screenshot releases its interaction task while its image is saved by a tracked background task");
                                editor.Close();
                            }
                            finally { gate.Release(); }
                            await pending;
                            await Task.WhenAll(Field<HashSet<Task>>(app, "historyWrites").ToArray());
                            records = await store.LoadAsync();
                            check(records.Length == 3 && records[0].Kind == "capture", "a completed long screenshot creates one image record independently of closing its editor");
                            using (var loaded = await store.LoadImageAsync(records[0].Id))
                                check(loaded != null && loaded.Size == new Size(40, 30), "a completed long screenshot remains readable after its editor closes");

                            settings.HistoryEnabled = false;
                            await Invoke(app, "CompleteCapture", Sample(), CaptureAction.Pin, (Point?)new Point(-30000, -30000));
                            check((await store.LoadAsync()).Length == 3 && Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single().Visible, "disabling history preserves screenshot pin behavior without storing new images");
                            settings.HistoryEnabled = true;
                            await gate.WaitAsync();
                            try
                            {
                                await Invoke(app, "CompleteCapture", Sample(), CaptureAction.Pin, (Point?)new Point(-30000, -30000));
                                await Invoke(app, "CompleteCapture", Sample(), CaptureAction.Pin, (Point?)new Point(-30000, -30000));
                                check(Field<HashSet<Task>>(app, "historyWrites").Count(write => !write.IsCompleted) == 2, "consecutive screenshot confirmations finish while their two history writes remain blocked and tracked");
                            }
                            finally { gate.Release(); }
                            typeof(DesktopApp).GetMethod("Exit", Private).Invoke(app, null);
                            check(Field<HashSet<Task>>(app, "historyWrites").Count == 0 && Field<List<Form>>(app, "imageWindows").Count == 0 && store.LoadAsync().GetAwaiter().GetResult().Length == 5, "exit joins screenshot history writes and releases all tracked tasks and pin windows");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { typeof(DesktopApp).GetMethod("Exit", Private).Invoke(app, null); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
            }
            finally
            {
                Ui.RenderOffscreen = offscreen;
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        }
        private static Bitmap Sample()
        {
            var image = new Bitmap(40, 30);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CadetBlue);
            return image;
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static Task Invoke(DesktopApp app, string name, params object[] arguments) { return (Task)typeof(DesktopApp).GetMethod(name, Private).Invoke(app, arguments); }
        private sealed class OcrHandler : HttpMessageHandler
        {
            internal int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                if (request.RequestUri.Host != "ocr.tencentcloudapi.com") throw new InvalidOperationException("Unexpected mock service");
                cancellation.ThrowIfCancellationRequested(); Calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"recognized example\"}],\"RequestId\":\"synthetic\"}}") });
            }
        }
    }
}

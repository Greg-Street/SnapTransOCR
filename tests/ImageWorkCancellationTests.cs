using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ImageWorkCancellationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void RunPure(Action<bool, string> check)
        {
            using (var image = Sample())
            using (var cancelled = new CancellationTokenSource())
            {
                var blocks = new[] { new OcrTextBlock("source", new Rectangle(10, 10, 120, 25)) };
                var text = new[] { "translated" };
                var layouts = ImageTranslation.Layout(image, blocks, text);
                cancelled.Cancel();
                CheckCancelled(check, delegate { ImageTranslation.Layout(image, blocks, text, cancelled.Token); }, "cancelled image layout stops before measuring blocks");
                CheckCancelled(check, delegate { using (ImageTranslation.Render(image, blocks, text, layouts, cancelled.Token)) { } }, "cancelled image rendering stops before allocating a result");
                using (var graphics = Graphics.FromImage(image))
                    CheckCancelled(check, delegate { ImageTranslation.Draw(graphics, image, text, layouts, cancelled.Token); }, "cancelled image drawing stops before altering the target");
                check(image.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "cancelled work leaves the caller-owned original usable and unchanged");
            }
            using (var image = Sample())
            using (var cancellation = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim())
            {
                var blocks = Enumerable.Range(0, 10000).Select(index => new OcrTextBlock("source", new Rectangle(10, 10, 120, 25))).ToArray();
                var text = Enumerable.Repeat(new string('字', 300), blocks.Length).ToArray();
                Task work = Task.Run(delegate { started.Set(); ImageTranslation.Layout(image, blocks, text, cancellation.Token); });
                started.Wait(); Thread.Sleep(20);
                bool pending = !work.IsCompleted;
                cancellation.Cancel();
                CheckCancelled(check, delegate { work.GetAwaiter().GetResult(); }, "cancellation interrupts multi-block layout rather than returning a full layout");
                check(pending, "the multi-block cancellation fixture was still running when cancellation was requested");
                using (var rendered = ImageTranslation.Render(image, new[] { blocks[0] }, new[] { "ok" }))
                    check(rendered.Width == image.Width, "a cancelled layout releases drawing resources for subsequent rendering");
            }
            using (var image = Sample())
            using (var cancellation = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim())
            {
                var blocks = new[] { new OcrTextBlock("source", new Rectangle(10, 10, 120, 25)) };
                string longText = new string('字', 1000000);
                Task work = Task.Run(delegate { started.Set(); ImageTranslation.Layout(image, blocks, new[] { longText }, cancellation.Token); });
                started.Wait(); Thread.Sleep(20);
                bool pending = !work.IsCompleted;
                cancellation.Cancel();
                CheckCancelled(check, delegate { work.GetAwaiter().GetResult(); }, "cancellation interrupts line wrapping within a single large text block");
                check(pending, "the single-block cancellation fixture was still running when cancellation was requested");
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            RunPure(check);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                var settings = new AppSettings {
                    OcrProvider = "tencent", TencentId = "synthetic", TencentSecret = "synthetic",
                    TranslateProvider = "volc", VolcId = "synthetic", VolcSecret = "synthetic", TargetLanguage = "zh",
                    BaiduImageTranslateEnabled = true, BaiduTranslateId = "synthetic", BaiduTranslateSecret = "synthetic",
                    HistoryEnabled = false, PinTopmost = false, TranslateNearCursor = false,
                    Hotkeys = Enumerable.Repeat("", AppSettings.HotkeyCount).ToArray()
                };
                using (var handler = new Handler())
                using (var app = new DesktopApp(settings, true, persistSettings: value => { }))
                {
                    Field<CloudApi>(app, "api").Dispose();
                    typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                    var main = Field<MainForm>(app, "main");
                    main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-30000, -30000);
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                        try
                        {
                            int uiThread = Thread.CurrentThread.ManagedThreadId;
                            foreach (string route in new[] { "ocr", "pipeline", "baidu_image" })
                            {
                                handler.Threads.Clear();
                                settings.ImageTranslateProvider = route == "baidu_image" ? route : "pipeline";
                                if (route == "ocr") await Invoke(app, "Ocr", Sample(), true);
                                else await Invoke(app, "TranslateImage", Sample(), new Point(-30000, -30000));
                                check(handler.Threads.Count == (route == "baidu_image" ? 1 : 2) && handler.Threads.All(thread => thread != uiThread), route + " encodes and prepares every API request off the UI thread");
                                check(Thread.CurrentThread.ManagedThreadId == uiThread, route + " resumes UI updates on the original UI thread");
                                if (route == "ocr")
                                    check(Field<RichTextBox>(main, "source").Text == "source" && Field<Dictionary<string, ServiceCard>>(main, "cards")["volc"].Value == "translated", "background OCR and text translation retain their completed results");
                                else
                                {
                                    var pin = Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single();
                                    check(Field<Bitmap>(pin, "original") != null && Field<Label>(pin, "status").Text.Length == 0, route + " installs its rendered image and clears progress on the UI thread");
                                }
                                foreach (var pin in Field<List<Form>>(app, "imageWindows").OfType<PinForm>().ToArray()) pin.Close();
                            }
                            handler.Hold = true;
                            Task pending = Invoke(app, "TranslateImage", Sample(), new Point(-30000, -30000));
                            await handler.Started.Task;
                            var closing = Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single();
                            closing.Close(); await pending;
                            check(handler.Cancelled && closing.IsDisposed && Field<List<Form>>(app, "imageWindows").Count == 0, "closing the pin cancels the background request and does not install a late image");
                        }
                        catch (Exception error) { failure = error; }
                        finally { typeof(DesktopApp).GetMethod("Exit", Private).Invoke(app, null); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static void CheckCancelled(Action<bool, string> check, Action action, string message)
        {
            bool cancelled = false;
            try { action(); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, message);
        }
        private static Bitmap Sample()
        {
            var image = new Bitmap(180, 90);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
            return image;
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static Task Invoke(DesktopApp app, string name, params object[] arguments) { return (Task)typeof(DesktopApp).GetMethod(name, Private).Invoke(app, arguments); }
        private sealed class Handler : HttpMessageHandler
        {
            internal readonly List<int> Threads = new List<int>();
            internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Hold, Cancelled;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Threads.Add(Thread.CurrentThread.ManagedThreadId);
                if (Hold)
                {
                    Started.SetResult(true);
                    try { await Task.Delay(Timeout.Infinite, cancellation).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Cancelled = true; throw; }
                }
                string body = request.RequestUri.Host == "ocr.tencentcloudapi.com"
                    ? "{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"source\",\"ItemPolygon\":{\"X\":10,\"Y\":10,\"Width\":120,\"Height\":25}}]}}"
                    : request.RequestUri.Host == "translate.volcengineapi.com"
                    ? "{\"TranslationList\":[{\"Translation\":\"translated\"}]}"
                    : "{\"error_code\":0,\"data\":{\"content\":[{\"src\":\"source\",\"dst\":\"translated\",\"rect\":\"10 10 120 25\",\"points\":[{\"x\":10,\"y\":10},{\"x\":130,\"y\":10},{\"x\":130,\"y\":35},{\"x\":10,\"y\":35}]}]}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}

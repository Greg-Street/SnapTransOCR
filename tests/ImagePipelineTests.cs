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
    internal static class ImagePipelineTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            string historyDirectory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-Pipeline-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = new AppSettings {
                    OcrProvider = "tencent", TencentId = "test-id", TencentSecret = "test-secret",
                    TranslateProvider = "volc", VolcId = "test-id", VolcSecret = "test-secret", TargetLanguage = "zh",
                    Hotkeys = new[] { "", "", "", "" }, TranslateNearCursor = false, PinTopmost = false
                };
                using (var handler = new PipelineHandler())
                using (var app = new DesktopApp(settings, true))
                {
                    var history = new HistoryStore(historyDirectory);
                    typeof(DesktopApp).GetField("history", Private).SetValue(app, history);
                    Field<CloudApi>(app, "api").Dispose();
                    typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                    var main = Field<MainForm>(app, "main");
                    main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-30000, -30000);
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                        try
                        {
                            await InvokeTask(app, "TranslateImage", Sample(), new Point(-30000, -30000));
                            var coldPin = Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single();
                            check(handler.OcrCalls == 1 && handler.TranslateCalls == 1 && Field<Bitmap>(coldPin, "original") != null
                                && Field<Label>(coldPin, "status").Text.Length == 0,
                                "a fresh desktop app completes direct image translation without any preceding plain OCR request");
                            coldPin.Close();
                            await history.ClearAsync(); handler.OcrCalls = handler.TranslateCalls = 0;
                            main.SetSource("previous text");
                            await InvokeTask(app, "Ocr", Sample(), true);
                            check(handler.OcrCalls == 1 && handler.TranslateCalls == 1, "screenshot text translation completes OCR followed by a translation request");
                            check(Field<RichTextBox>(main, "source").Text == "source text" && Field<Dictionary<string, ServiceCard>>(main, "cards")["volc"].Value == "translated text", "programmatic OCR source updates do not cancel the active screenshot translation");
                            check(!Field<bool>(main, "busy") && Field<Label>(main, "status").Text.Length == 0 && Field<CancellationTokenSource>(app, "job") == null, "completed screenshot translation clears its busy state and active job");
                            var records = await history.LoadAsync();
                            check(records.Length == 1 && records[0].Source == "source text" && records[0].Translation == "translated text", "successful screenshot translation records original and translated text together");
                            using (var stored = await history.LoadImageAsync(records[0].Id))
                            using (var expected = Sample()) check(stored != null && ImageTranslationTests.Equal(stored, expected), "screenshot translation history saves the corresponding lossless original PNG");

                            using (var baseline = Sample())
                            {
                                await InvokeTask(app, "TranslateImage", (Bitmap)baseline.Clone(), new Point(-30000, -30000));
                                var windows = Field<List<Form>>(app, "imageWindows");
                                var pin = windows.OfType<PinForm>().Single();
                                var translated = Field<Bitmap>(pin, "image"); var original = Field<Bitmap>(pin, "original");
                                check(handler.OcrCalls == 2 && handler.TranslateCalls == 2 && !pin.IsDisposed, "image translation completes both API stages and retains its tracked pin window");
                                check(pin.Left < -10000 && pin.Top < -10000 && main.Left < -10000, "pipeline test windows remain offscreen throughout the real message loop");
                                check(pin.Location == new Point(-30000, -30000), "image translation retains the supplied screenshot origin through both API stages");
                                check(original != null && ImageTranslationTests.Equal(original, baseline) && translated.Width == baseline.Width && translated.Height >= baseline.Height && !ImageTranslationTests.Equal(translated, baseline), "image translation preserves the original and installs the complete translated image at the original width");
                                check(ReferenceEquals(CurrentImage(pin), translated) && Field<Label>(pin, "status").Text.Length == 0, "completed image translation initially displays the translated image without progress text");
                                check(Field<OcrTextBlock[]>(pin, "textBlocks").Single().Text == "source text" && Field<string[]>(pin, "translatedText").Single() == "translated text", "completed translated image retains both selectable text layers");
                                records = await history.LoadAsync();
                                check(records.Length == 2 && records[0].Kind == "image_translation", "image translation adds one history entry after the result is displayed");
                                using (var stored = await history.LoadImageAsync(records[0].Id))
                                    check(stored != null && ImageTranslationTests.Equal(stored, baseline), "expanded image translation history retains the corresponding original PNG");
                                var menu = Field<ToolStripMenuItem>(pin, "toggleItem");
                                menu.PerformClick();
                                check(ReferenceEquals(CurrentImage(pin), original) && menu.Text == "查看译文", "the pin toggle displays the original image and offers the translated image");
                                menu.PerformClick();
                                check(ReferenceEquals(CurrentImage(pin), translated) && menu.Text == "查看原图", "the pin toggle returns to the translated image");
                                pin.Close();
                                check(windows.Count == 0, "closing a completed image translation removes its tracked window");
                            }

                            handler.Translation = new string('字', 50000);
                            using (var baseline = Sample())
                            {
                                await InvokeTask(app, "TranslateImage", (Bitmap)baseline.Clone(), new Point(-30000, -30000));
                                var pin = Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single();
                                check(Field<Bitmap>(pin, "original") == null && ImageTranslationTests.Equal(CurrentImage(pin), baseline), "an over-limit translated canvas retains the unchanged original without installing a substitute image");
                                check(Field<Label>(pin, "status").Text.Contains("24000"), "an over-limit translated canvas retains its explicit size-limit error");
                                check(Field<Label>(pin, "status").Text.StartsWith("图片排版未完成"), "local image layout errors are identified separately from translation API failures");
                                var retained = Field<string[]>(pin, "translatedText");
                                var copy = pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "复制全部译文");
                                string copyText = (string)typeof(PinForm).GetMethod("AllText", Private).Invoke(pin, new object[] { false });
                                check(retained != null && retained.Single() == handler.Translation && copy.Enabled && copyText == handler.Translation, "successful complete translation remains available for context-menu copying after its canvas exceeds the limit");
                                check((await history.LoadAsync()).Length == 2, "a canvas-limit failure does not add an unsuccessful image translation to history");
                                pin.Close();
                            }
                            handler.Translation = "translated text";
                            handler.HoldTranslation = true;
                            Task pending = InvokeTask(app, "TranslateImage", Sample(), new Point(-30000, -30000));
                            await handler.TranslationStarted.Task;
                            var pendingWindows = Field<List<Form>>(app, "imageWindows");
                            var pendingPin = pendingWindows.OfType<PinForm>().Single();
                            check(Field<Bitmap>(pendingPin, "original") == null, "pending image translation keeps the source image until rendering completes");
                            pendingPin.Close();
                            check(handler.CancellationObserved, "closing an image translation pin cancels its in-flight API request");
                            handler.ReleaseTranslation.SetResult(true);
                            await pending;
                            check(pendingPin.IsDisposed && pendingWindows.Count == 0, "a late translation response cannot reopen or replace a closed pin window");
                            check((await history.LoadAsync()).Length == 2, "cancelled image translations do not create a history entry");
                            handler.HoldTranslation = false;
                            settings.HistoryEnabled = false;
                            await InvokeTask(app, "Translate", "history disabled text", null);
                            check((await history.LoadAsync()).Length == 2 && Field<Dictionary<string, ServiceCard>>(main, "cards")["volc"].Value == "translated text", "turning history off preserves translation behavior and existing records without adding new ones");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { typeof(DesktopApp).GetMethod("Exit", Private).Invoke(app, null); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = offscreen; if (Directory.Exists(historyDirectory)) Directory.Delete(historyDirectory, true); }
        }
        private static Bitmap Sample()
        {
            var image = new Bitmap(180, 90);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Magenta, 40, 25, 55, 15);
            }
            return image;
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static Bitmap CurrentImage(PinForm pin) { return (Bitmap)typeof(PinForm).GetProperty("CurrentImage", Private).GetValue(pin, null); }
        private static Task InvokeTask(DesktopApp app, string name, params object[] arguments) { return (Task)typeof(DesktopApp).GetMethod(name, Private).Invoke(app, arguments); }
        private sealed class PipelineHandler : HttpMessageHandler
        {
            internal int OcrCalls, TranslateCalls;
            internal bool HoldTranslation, CancellationObserved;
            internal string Translation = "translated text";
            internal readonly TaskCompletionSource<bool> TranslationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReleaseTranslation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                await Task.Yield();
                string response;
                if (request.RequestUri.Host == "ocr.tencentcloudapi.com")
                {
                    OcrCalls++;
                    response = "{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"source text\",\"Polygon\":[{\"X\":20,\"Y\":20},{\"X\":150,\"Y\":20},{\"X\":150,\"Y\":55},{\"X\":20,\"Y\":55}]}],\"RequestId\":\"test\"}}";
                }
                else if (request.RequestUri.Host == "translate.volcengineapi.com")
                {
                    TranslateCalls++;
                    if (HoldTranslation)
                        using (cancellation.Register(delegate { CancellationObserved = true; }))
                        {
                            TranslationStarted.SetResult(true);
                            // Deliberately return a late server response even after cancellation.
                            await ReleaseTranslation.Task.ConfigureAwait(false);
                        }
                    response = Json.Write(new { TranslationList = new[] { new { Translation } } });
                }
                else throw new InvalidOperationException("Unexpected test request: " + request.RequestUri.Host);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
            }
        }
    }
}

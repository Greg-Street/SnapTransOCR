using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
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
    internal static class HistoryEditorTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-editor-" + Guid.NewGuid().ToString("N"));
            bool previousOffscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                var store = new HistoryStore(directory);
                using (var image = Sample()) store.AddAsync(new HistoryEntry { Kind = "capture" }, image).GetAwaiter().GetResult();
                var settings = new AppSettings { Hotkeys = new[] { "", "", "", "" }, CaptureStroke = 8,
                    PinTopmost = false, TranslateNearCursor = false, ScreenshotCopyOnConfirm = false,
                    OcrProvider = "tencent", TencentId = "test", TencentSecret = "test",
                    TranslateProvider = "volc", VolcId = "test", VolcSecret = "test", TargetLanguage = "zh" };
                using (var handler = new PipelineHandler())
                using (var app = new DesktopApp(settings, true, persistSettings: delegate { }))
                {
                    typeof(DesktopApp).GetField("history", Private).SetValue(app, store);
                    Field<CloudApi>(app, "api").Dispose();
                    typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                    var main = Field<MainForm>(app, "main");
                    main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-30000, -30000);
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                        try { await Exercise(app, store, directory, handler, check); }
                        catch (Exception ex) { failure = ex; }
                        finally { Invoke(app, "Exit"); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
            }
            finally
            {
                Ui.RenderOffscreen = previousOffscreen;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static async Task Exercise(DesktopApp app, HistoryStore store, string directory, PipelineHandler handler, Action<bool, string> check)
        {
            HistoryEntry original = (await store.LoadAsync()).Single();
            string originalPath = Path.Combine(directory, original.ImageFile);
            byte[] originalPng = File.ReadAllBytes(originalPath);
            Invoke(app, "ShowHistory");
            var settings = Field<SettingsForm>(app, "settingsWindow");
            settings.StartPosition = FormStartPosition.Manual; settings.Location = new Point(-30000, -30000);
            var view = Field<HistoryView>(settings, "historyView");
            await Ready(view);
            var preview = Field<PictureBox>(view, "preview");
            var windows = Field<List<Form>>(app, "imageWindows");
            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            var editor = windows.OfType<EditorForm>().Single(); editor.Location = new Point(-30000, -30000);
            var canvas = Field<ImageCanvas>(editor, "canvas");
            var owned = Field<Bitmap>(canvas, "image");
            check(editor.Visible && !windows.OfType<PinForm>().Any() && !ReferenceEquals(owned, preview.Image) && owned.Size == new Size(180, 100),
                "clicking a history thumbnail follows the application event chain into an independent full-resolution editor rather than a pin");
            check((await store.LoadAsync()).Length == 1 && Field<HashSet<Task>>(app, "historyWrites").Count == 0 && handler.OcrCalls == 0 && handler.TranslateCalls == 0,
                "opening a historical screenshot queues no duplicate capture record and sends no OCR or translation request");
            check(canvas.Stroke == 8, "the history editor starts with the configured screenshot stroke width");
            check(editor.Owner == null && !editor.TopMost && settings.Enabled,
                "history editing leaves settings independently accessible instead of keeping an owned topmost window above it");
            Annotate(canvas);
            using (var rendered = canvas.Render())
            using (var png = new MemoryStream())
            {
                rendered.Save(png, ImageFormat.Png); png.Position = 0;
                using (var exported = new Bitmap(png))
                    check(exported.Size == new Size(100, 70) && Blue(exported.GetPixel(0, 20)), "history crop and annotation render into lossless PNG at source pixel dimensions");
            }
            canvas.Undo(); canvas.Undo();
            using (var restored = canvas.Render())
                check(restored.Size == owned.Size && restored.GetPixel(10, 30).ToArgb() == Color.White.ToArgb(), "undo restores the original history pixels without changing the stored source");
            canvas.Redo(); canvas.Redo();
            using (var redone = canvas.Render()) check(redone.Size == new Size(100, 70) && Blue(redone.GetPixel(0, 20)), "redo restores the edited crop before export");
            editor.Controls.OfType<FlowLayoutPanel>().SelectMany(bar => bar.Controls.OfType<GlyphButton>()).Single(button => button.Glyph == "history").PerformClick();
            check(settings.Visible && !settings.IsDisposed && Field<HistoryView>(settings, "historyView") == view,
                "return-to-history closes the editor and restores the same history page without recreating its view");
            check(editor.IsDisposed && Disposed(owned) && windows.Count == 0 && File.ReadAllBytes(originalPath).SequenceEqual(originalPng) && (await store.LoadAsync()).Length == 1,
                "closing a history editor releases its bitmap and leaves the original PNG and record unchanged");

            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            editor = windows.OfType<EditorForm>().Single(); editor.Location = new Point(-30000, -30000);
            canvas = Field<ImageCanvas>(editor, "canvas"); owned = Field<Bitmap>(canvas, "image");
            Annotate(canvas);
            Bitmap transferred = null;
            editor.ImageTranslateRequested += image => transferred = image;
            editor.Controls.OfType<FlowLayoutPanel>().SelectMany(bar => bar.Controls.OfType<GlyphButton>()).Single(button => button.Glyph == "image-translate").PerformClick();
            var pin = windows.OfType<PinForm>().Single(); pin.Location = new Point(-30000, -30000);
            var status = Field<Label>(pin, "status");
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler statusChanged = delegate
            {
                if (status.Text.Length == 0 || status.Text.Contains("未完成") || status.Text.Contains("未识别")) completed.TrySetResult(true);
            };
            status.TextChanged += statusChanged;
            check(editor.IsDisposed && Disposed(owned) && transferred != null && !ReferenceEquals(transferred, owned) && transferred.Size == new Size(100, 70) && Blue(transferred.GetPixel(0, 20)),
                "the image-translation action closes the editor while transferring an independently rendered edited bitmap");
            statusChanged(null, EventArgs.Empty);
            await completed.Task; status.TextChanged -= statusChanged;
            HistoryEntry[] records = await store.LoadAsync();
            check(handler.OcrCalls == 1 && handler.TranslateCalls == 1 && status.Text.Length == 0 && records.Length == 2 && records.Count(entry => entry.Kind == "capture") == 1 && records.Count(entry => entry.Kind == "image_translation") == 1,
                "explicit image translation creates only its successful result record after the history editor request");
            using (var stored = await store.LoadImageAsync(records.Single(entry => entry.Kind == "image_translation").Id))
                check(stored.Size == new Size(100, 70) && Blue(stored.GetPixel(0, 20)) && File.ReadAllBytes(originalPath).SequenceEqual(originalPng),
                    "image-translation history retains the edited input while the prior historical PNG remains byte-for-byte unchanged");
            pin.Close();
            check(Disposed(transferred) && windows.Count == 0, "closing the translated image releases the editor's transferred bitmap and its tracked window");

            var rejectedImage = Sample();
            using (var rejected = new RejectShowForm(rejectedImage))
            {
                bool failed = false;
                try { Invoke(app, "Track", rejected); }
                catch (TargetInvocationException ex) { failed = ex.InnerException is InvalidOperationException; }
                check(failed && rejected.IsDisposed && Disposed(rejectedImage) && !windows.Contains(rejected),
                    "a failed window show removes tracking and disposes its owned image instead of leaving a broken editor alive");
            }
        }
        private static void Annotate(ImageCanvas canvas)
        {
            canvas.AddMark(new Mark { Kind = Tool.Rectangle, Color = Color.Blue, Width = 4, Bounds = new RectangleF(10, 10, 60, 40) });
            canvas.AddMark(new Mark { Kind = Tool.Crop, Bounds = new RectangleF(10, 10, 100, 70) });
        }
        private static bool Blue(Color color) { return color.B > 230 && color.R < 20 && color.G < 20; }
        private static bool Disposed(Bitmap image) { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
        private static async Task Ready(HistoryView view)
        { while (Field<bool>(view, "busy") || Field<CancellationTokenSource>(view, "previewRequest") != null) await Task.Delay(1); }
        private static Bitmap Sample()
        {
            var image = new Bitmap(180, 100);
            using (var graphics = Graphics.FromImage(image)) { graphics.Clear(Color.White); graphics.FillRectangle(Brushes.Coral, 130, 70, 20, 20); }
            return image;
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static object Invoke(object owner, string name, params object[] arguments) { return owner.GetType().GetMethod(name, Private).Invoke(owner, arguments); }
        private sealed class RejectShowForm : Form
        {
            internal RejectShowForm(Bitmap image) { Controls.Add(new ImageCanvas(image)); }
            protected override void SetVisibleCore(bool value)
            { if (value) throw new InvalidOperationException("Synthetic show failure"); base.SetVisibleCore(value); }
        }
        private sealed class PipelineHandler : HttpMessageHandler
        {
            internal int OcrCalls, TranslateCalls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); string response;
                if (request.RequestUri.Host == "ocr.tencentcloudapi.com")
                {
                    OcrCalls++;
                    response = "{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"source\",\"Polygon\":[{\"X\":20,\"Y\":20},{\"X\":80,\"Y\":20},{\"X\":80,\"Y\":45},{\"X\":20,\"Y\":45}]}],\"RequestId\":\"test\"}}";
                }
                else if (request.RequestUri.Host == "translate.volcengineapi.com")
                { TranslateCalls++; response = "{\"TranslationList\":[{\"Translation\":\"译文\"}]}"; }
                else throw new InvalidOperationException("Unexpected history-editor test service");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
            }
        }
    }
}

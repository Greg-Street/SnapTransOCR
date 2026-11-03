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
    internal static class ExtendedRecognitionFlowTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-special-flow-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var image = Sample(10, 120))
                using (var stream = new MemoryStream(DesktopApp.EncodeBaiduImage(image)))
                using (var padded = new Bitmap(stream))
                    check(padded.Width == 40 && padded.Height == 120 && padded.GetPixel(0, 0).ToArgb() == Color.Magenta.ToArgb() && padded.GetPixel(39, 119).ToArgb() == Color.White.ToArgb(), "native image API padding preserves the source pixels and origin without scaling");
                using (var button = new Button())
                using (var menu = RecognitionMenu.Attach(button, new AppSettings { BaiduTableEnabled = true }, delegate(RecognitionKind kind) { check(kind == RecognitionKind.BaiduTable, "special menu dispatches the explicitly selected table provider"); }))
                {
                    check(menu.Items.Count == 4 && menu.Items.Cast<ToolStripItem>().Count(item => item.Enabled) == 1, "special menu exposes only individually enabled services");
                    menu.Items.Cast<ToolStripMenuItem>().Single(item => item.Enabled).PerformClick();
                }
                using (var form = new OcrForm())
                {
                    form.Show(); form.ClientSize = new Size(360, 420);
                    var source = Field<RichTextBox>(form, "source");
                    form.SetRecognitionResult(new RecognitionResult("A\tB", new byte[] { 1 }), RecognitionKind.BaiduTable);
                    var save = Field<GlyphButton>(form, "saveSpreadsheet");
                    check(save.Visible && !Field<GlyphButton>(form, "format").Visible && Field<Label>(form, "caption").Right <= Field<GlyphButton>(form, "settingsButton").Left, "table save action replaces formatting without overlapping the narrow window caption");
                    source.AppendText("C");
                    check(Field<byte[]>(form, "spreadsheet") == null && !save.Visible, "editing table text releases the now-stale server workbook");
                    form.SetRecognitionResult(new RecognitionResult("A", new byte[] { 1 }), RecognitionKind.Table);
                    form.Close();
                    check(Field<byte[]>(form, "spreadsheet") == null && !form.Visible, "closing OCR releases its workbook while retaining the reusable window");
                }
                var settings = new AppSettings {
                    Hotkeys = new[] { "", "", "", "" }, TranslateNearCursor = false, PinTopmost = false,
                    BaiduOcrKey = "test-key", BaiduOcrSecret = "test-secret", BaiduQrEnabled = true,
                    BaiduImageTranslateEnabled = true, ImageTranslateProvider = "baidu_image", BaiduImageTargetLanguage = "zh",
                    BaiduTranslateId = "test-id", BaiduTranslateSecret = "test-secret", TargetLanguage = "smart"
                };
                using (var handler = new Handler())
                using (var app = new DesktopApp(settings, true, persistSettings: delegate { }))
                {
                    var history = new HistoryStore(directory);
                    typeof(DesktopApp).GetField("history", Private).SetValue(app, history);
                    Field<CloudApi>(app, "api").Dispose();
                    typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                    var main = Field<MainForm>(app, "main");
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                        try
                        {
                            await Invoke(app, "SpecialOcr", Sample(180, 90), RecognitionKind.QrCode);
                            var records = await history.LoadAsync();
                            check(records.Length == 1 && records[0].OcrProvider == "baidu_qrcode" && records[0].Source == "https://example.test/", "QR result is recorded as OCR text without opening its URL");
                            check(Field<RichTextBox>(Field<OcrForm>(app, "ocrWindow"), "source").Text == records[0].Source, "special recognition displays its complete result in the existing selectable OCR editor");
                            await Invoke(app, "TranslateImage", Sample(180, 90), new Point(-30000, -30000));
                            var pin = Field<List<Form>>(app, "imageWindows").OfType<PinForm>().Single();
                            check(handler.ImageCalls == 1 && handler.QrCalls == 1 && handler.OtherCalls == 0 && handler.FixedTarget, "native picture translation makes one image request with a fixed target and no separate OCR or text-translation call");
                            check(Field<OcrTextBlock[]>(pin, "textBlocks").Single().Text == "hello" && Field<string[]>(pin, "translatedText").Single() == "你好" && Field<Label>(pin, "status").Text == "", "native picture translation retains selectable source and translated layers");
                            check(pin.Location == new Point(-30000, -30000), "native picture translation keeps the screenshot position");
                            records = await history.LoadAsync();
                            check(records.Length == 2 && records[0].Kind == "image_translation" && records[0].TranslateProvider == "baidu_image", "native image history stores the direct provider and original image");
                            pin.Close();
                            handler.HoldQr = true;
                            Task pending = Invoke(app, "SpecialOcr", Sample(180, 90), RecognitionKind.QrCode);
                            await handler.Started.Task;
                            Field<OcrForm>(app, "ocrWindow").Close();
                            handler.Release.SetResult(true); await pending;
                            check((await history.LoadAsync()).Length == 2 && !Field<OcrForm>(app, "ocrWindow").Visible, "closing pending recognition prevents a late response reopening the window or adding history");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { typeof(DesktopApp).GetMethod("Exit", Private).Invoke(app, null); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = previous; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static Bitmap Sample(int width, int height)
        {
            var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Magenta);
            return bitmap;
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static Task Invoke(DesktopApp app, string name, params object[] arguments) { return (Task)typeof(DesktopApp).GetMethod(name, Private).Invoke(app, arguments); }
        private sealed class Handler : HttpMessageHandler
        {
            internal int ImageCalls, QrCalls, OtherCalls;
            internal bool FixedTarget, HoldQr;
            internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                string response;
                if (request.RequestUri.AbsolutePath.Contains("oauth")) response = "{\"access_token\":\"mock-token\",\"expires_in\":3600}";
                else if (request.RequestUri.AbsolutePath.EndsWith("qrcode"))
                {
                    QrCalls++;
                    if (HoldQr) { Started.SetResult(true); await Release.Task; }
                    response = "{\"codes_result\":[{\"text\":[\"https://example.test/\"]}]}";
                }
                else if (request.RequestUri.Host == "fanyi-api.baidu.com")
                {
                    ImageCalls++;
                    string body = await request.Content.ReadAsStringAsync();
                    FixedTarget = body.Contains("zh");
                    response = "{\"error_code\":0,\"data\":{\"content\":[{\"src\":\"hello\",\"dst\":\"你好\",\"rect\":\"10 10 80 30\",\"points\":[{\"x\":10,\"y\":10},{\"x\":90,\"y\":10},{\"x\":90,\"y\":40},{\"x\":10,\"y\":40}]}]}}";
                }
                else { OtherCalls++; throw new InvalidOperationException("Unexpected provider request."); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
            }
        }
    }
}

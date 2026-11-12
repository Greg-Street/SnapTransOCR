using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class Tests
    {
        private static int assertions;
        private static bool headless;
        private static readonly string Output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output"));
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Control.CheckForIllegalCrossThreadCalls = true;
                if (args.Contains("--probe"))
                {
                    IntPtr window = Native.FindWindow(null, CommandWindow.Caption);
                    Console.WriteLine("Broker=" + window + " Post=" + Native.PostMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero) + " LastError=" + Marshal.GetLastWin32Error());
                    return window == IntPtr.Zero ? 1 : 0;
                }
                if (args.Contains("--fixture"))
                {
                    Application.EnableVisualStyles();
                    using (var fixture = new Form { Text = "SnapTransOCR QA Fixture", ClientSize = new Size(920, 720), StartPosition = FormStartPosition.CenterScreen })
                    {
                        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                        var picture = new PictureBox { Image = PageImage(850, 4800), SizeMode = PictureBoxSizeMode.AutoSize };
                        var selected = new TextBox { Dock = DockStyle.Top, Text = "SnapTransOCR selection test", Height = 35, Font = new Font("Segoe UI", 15) };
                        panel.Controls.Add(picture);
                        fixture.Controls.Add(panel);
                        fixture.Controls.Add(selected);
                        fixture.Shown += delegate { Native.ShowWindow(fixture.Handle, 9); };
                        Application.Run(fixture);
                        picture.Image.Dispose();
                    }
                    return 0;
                }
                Directory.CreateDirectory(Output);
                headless = args.Contains("--headless") || args.Contains("--render-only");
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Program.AppIcon = SystemIcons.Application;
                if (args.Contains("--cold-selection-only"))
                {
                    if (args.Contains("--document")) ColdSelectionStartupTests.RunDocumentAncestor(Check);
                    else ColdSelectionStartupTests.Run(Check, args.Contains("--copy"), args.Contains("--blur"));
                    Console.WriteLine("PASS: " + assertions + " cold-start assertions"); return 0;
                }
                if (args.Contains("--live-tencent-table")) { LiveTencentTableChecks.Run(Output).GetAwaiter().GetResult(); return 0; }
                if (args.Contains("--live-baidu")) { LiveBaiduChecks.Run(Output).GetAwaiter().GetResult(); return 0; }
                if (args.Contains("--cloud-services-only")) { TranslationModelTests.Run(Check); ExtendedTranslationTests.Run(Check).GetAwaiter().GetResult(); SpecialRecognitionTests.Run(Check).GetAwaiter().GetResult(); ExtendedServiceSettingsTests.Run(Check); ConsolidatedTranslationSettingsTests.Run(Check); ExtendedRecognitionFlowTests.Run(Check); SpecialUiLifecycleTests.Run(Check); Console.WriteLine("PASS: " + assertions + " assertions"); return 0; }
                if (args.Contains("--translation-services-only")) { TranslationModelTests.Run(Check); ExtendedTranslationTests.Run(Check).GetAwaiter().GetResult(); ArkParallelTests.Run(Check); Console.WriteLine("PASS: " + assertions + " translation assertions"); return 0; }
                if (args.Contains("--gif-only")) { GifStreamTests.Run(Check); Console.WriteLine("PASS: " + assertions + " GIF assertions"); return 0; }
                if (args.Contains("--capture-tools-only")) { GifStreamTests.Run(Check); GifQualityTests.Run(Check); EditorPlacementTests.Run(Check); ScrollingOptimizationTests.Run(Check); ScrollCaptureWorkTests.Run(Check); CaptureToolsTests.Run(Check); Console.WriteLine("PASS: " + assertions + " capture tools assertions"); return 0; }
                if (args.Contains("--gif-native-only")) { CaptureToolsTests.RunNative(Check); Console.WriteLine("PASS: " + assertions + " native GIF assertions"); return 0; }
                if (args.Contains("--capture-refinement-only")) { CaptureRefinementTests.Run(Check, Output); Console.WriteLine("PASS: " + assertions + " capture refinement assertions"); return 0; }
                if (args.Contains("--scroll-directions-only")) { ScrollDirectionTests.Run(Check); Console.WriteLine("PASS: " + assertions + " bidirectional scrolling assertions"); return 0; }
                if (args.Contains("--capture-interaction-only")) { CaptureInteractionTests.Run(Check, Output); Console.WriteLine("PASS: " + assertions + " capture interaction assertions"); return 0; }
                if (args.Contains("--capture-refinement-native")) { CaptureRefinementTests.RunNative(Check, Output); CapturePerformanceTests.RunNative(Check); Console.WriteLine("PASS: " + assertions + " native capture refinement assertions"); return 0; }
                if (args.Contains("--settings-layout-only")) { SettingsLayoutTests.Run(Check); Console.WriteLine("PASS: " + assertions + " settings layout assertions"); return 0; }
                if (args.Contains("--settings-controls-only")) { DropdownTests.Run(Check); PasswordFieldTests.Run(Check); HistoryRetentionSettingsTests.Run(Check); Console.WriteLine("PASS: " + assertions + " settings control assertions"); return 0; }
                if (args.Contains("--window-regression-only")) { WindowSizeTests.Run(Check); WindowMotionTests.Run(Check); ResultScrollingTests.Run(Check); Console.WriteLine("PASS: " + assertions + " window regression assertions"); return 0; }
                if (args.Contains("--screenshot-regression-only")) { CaptureDpiTests.Run(Check); CapturePerformanceTests.Run(Check); AnnotationStyleTests.Run(Check); ImageCanvasCaptureTests.Run(Check); MosaicRenderTests.Run(Check); Console.WriteLine("PASS: " + assertions + " screenshot regression assertions"); return 0; }
                if (args.Contains("--optimization-only")) { OptimizationTests.Run(Check); Console.WriteLine("PASS: " + assertions + " optimization assertions"); return 0; }
                if (args.Contains("--architecture-only")) { HistorySnapshotTests.Run(Check).GetAwaiter().GetResult(); CloudApiTests.Run(Check).GetAwaiter().GetResult(); EfficiencyTests.Run(Check); Console.WriteLine("PASS: " + assertions + " architecture assertions"); return 0; }
                if (args.Contains("--pin-text-only")) { PinTextTests.Run(Check); Console.WriteLine("PASS: " + assertions + " assertions"); return 0; }
                if (args.Contains("--pinned-window-only")) { PinnedWindowTests.Run(Check); Console.WriteLine("PASS: " + assertions + " pinned window assertions"); return 0; }
                if (args.Contains("--image-work-only")) { ImageWorkCancellationTests.Run(Check); Console.WriteLine("PASS: " + assertions + " image work assertions"); return 0; }
                if (args.Contains("--presentation-only")) { PresentationTests.Run(Check, Output); return 0; }
                if (args.Contains("--render-only")) { RenderReferences(); Console.WriteLine("Rendered three reference layouts without opening desktop windows."); return 0; }
                if (args.Contains("--history-window-only")) { HistoryStandaloneTests.Run(Check); Console.WriteLine("PASS: " + assertions + " standalone history assertions"); return 0; }
                if (args.Contains("--history-clipboard-only"))
                {
                    if (System.Diagnostics.Process.GetProcessesByName("SnapTransOCR").Length != 0)
                        throw new InvalidOperationException("请先从托盘退出正在运行的 SnapTransOCR，再执行剪贴板回归，避免测试内容进入真实历史。");
                    HistoryTests.Run(Check).GetAwaiter().GetResult();
                    ClipboardShortcutSettingsTests.Run(Check);
                    SevenDayHistoryTests.Run(Check).GetAwaiter().GetResult();
                    RetentionHistoryTests.Run(Check).GetAwaiter().GetResult();
                    ClipboardHistoryTests.Run(Check);
                    CaptureHistoryTests.Run(Check); HistoryEditorTests.Run(Check); HistoryWindowLifecycleTests.Run(Check);
                    HistoryTests.RunUi(Check); HistoryFilterTests.Run(Check); HistoryThumbnailTests.Run(Check);
                    OptionalHotkeyTests.Run(Check); SettingsSaveTests.Run(Check);
                    HistoryRetentionSettingsTests.Run(Check);
                    HistoryStandaloneTests.Run(Check);
                    ClipboardHistoryViewTests.Run(Check); ClipboardHistoryViewTests.RunUi(Check);
                    if (!headless) ClipboardHistoryTests.RunNative(Check).GetAwaiter().GetResult();
                    Console.WriteLine("PASS: " + assertions + " history/clipboard assertions"); return 0;
                }
                if (!headless)
                {
                    var previousContext = System.Threading.SynchronizationContext.Current;
                    try
                    {
                        SelectionShortcutTests.RunEmptySelection(Check);
                        SelectionRecoveryTests.Run(Check);
                        CapturePerformanceTests.RunNative(Check);
                        PinTextTests.RunClipboard(Check);
                        PresentationTests.Run(Check, Output);
                        UiAndCapture(); ClipboardSelection(); ClipboardSnapshotRaceTests.Run(Check);
                        using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" } }, true)) { app.Dispose(); app.Dispose(); }
                        Check(true, "desktop app disposal is idempotent");
                    }
                    finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
                }
                Signatures();
                using (var commands = new CommandWindow("SnapTransOCR.Tests." + Guid.NewGuid().ToString("N")))
                {
                    var title = new StringBuilder(200);
                    Native.GetWindowText(commands.Handle, title, title.Capacity);
                    Console.WriteLine("Command window title: " + title);
                    Check(Native.FindWindow(null, title.ToString()) == commands.Handle, "single-instance command window discoverable");
                }
                Settings();
                OptionalHotkeyTests.Run(Check);
                ClipboardShortcutSettingsTests.Run(Check);
                SevenDayHistoryTests.Run(Check).GetAwaiter().GetResult();
                RetentionHistoryTests.Run(Check).GetAwaiter().GetResult();
                ClipboardHistoryTests.Run(Check);
                TextSplitting();
                Api().GetAwaiter().GetResult();
                CloudApiTests.Run(Check).GetAwaiter().GetResult();
                BingTranslationTests.Run(Check).GetAwaiter().GetResult();
                TranslationModelTests.Run(Check); ExtendedTranslationTests.Run(Check).GetAwaiter().GetResult();
                SpecialRecognitionTests.Run(Check).GetAwaiter().GetResult();
                ApiErrorTests.Run(Check).GetAwaiter().GetResult();
                NetworkFailureTests.Run(Check).GetAwaiter().GetResult();
                LayoutApiTests.Run(Check).GetAwaiter().GetResult();
                ColdImageApiTests.Run(Check).GetAwaiter().GetResult();
                HistoryTests.Run(Check).GetAwaiter().GetResult();
                CaptureHistoryTests.Run(Check);
                HistoryEditorTests.Run(Check);
                HistoryWindowLifecycleTests.Run(Check);
                HistoryStandaloneTests.Run(Check);
                EditorPlacementTests.Run(Check);
                ServiceSettingsTests();
                ExtendedServiceSettingsTests.Run(Check); ConsolidatedTranslationSettingsTests.Run(Check);
                ExtendedRecognitionFlowTests.Run(Check); SpecialUiLifecycleTests.Run(Check);
                TextUiTests.Run(Check);
                ResultScrollingTests.Run(Check);
                MainScrollWorkTests.Run(Check);
                WindowGeometryTests.Run(Check);
                SettingsLayoutTests.Run(Check);
                CaptureRefinementTests.Run(Check, Output);
                CaptureInteractionTests.Run(Check, Output);
                SettingsScrollTests.Run(Check);
                SettingsSaveTests.Run(Check);
                TraySettingsTests.Run(Check);
                SelectionShortcutTests.Run(Check);
                LanguageSelectionTests.Run(Check);
                OcrNarrowTests.Run(Check);
                DropdownTests.Run(Check);
                PasswordFieldTests.Run(Check);
                TooltipLifetimeTests.Run(Check);
                WindowSizeTests.Run(Check);
                WindowMotionTests.Run(Check);
                ServiceOrderTests.Run(Check);
                BingUiTests.Run(Check);
                ServiceDragPaintTests.Run(Check);
                EfficiencyTests.Run(Check);
                OptimizationTests.Run(Check);
                PinnedWindowTests.Run(Check);
                PinRotationTests.Run(Check);
                PinTextTests.Run(Check);
                PinTextOffsetTests.Run(Check); PinTextOffsetTests.RunUi(Check);
                HistoryTests.RunUi(Check);
                HistoryFilterTests.Run(Check);
                ClipboardHistoryViewTests.Run(Check); ClipboardHistoryViewTests.RunUi(Check);
                HistoryThumbnailTests.Run(Check);
                InteractionLifecycleTests.Run(Check);
                WindowFrameTests.Run(Check);
                WindowRegionResizeTests.Run(Check);
                Images();
                ScrollingOptimizationTests.Run(Check);
                ScrollDirectionTests.Run(Check);
                ScrollCaptureWorkTests.Run(Check);
                Annotation();
                MosaicRenderTests.Run(Check);
                AnnotationStyleTests.Run(Check);
                ImageCanvasCaptureTests.Run(Check);
                InlineText();
                CaptureDpiTests.Run(Check);
                CapturePerformanceTests.Run(Check);
                ImageTranslationTests.Run(Check);
                ImageFontFitTests.Run(Check, Output);
                ImageZoomTests.Run(Check, Output);
                ImagePipelineTests.Run(Check);
                ImageWorkCancellationTests.Run(Check);
                Startup();
                Console.WriteLine("PASS: " + assertions + " assertions");
                File.WriteAllText(Path.Combine(Output, "test-result.txt"), "PASS: " + assertions + " assertions\n" + DateTime.UtcNow.ToString("o"), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            assertions++;
            Console.WriteLine("ok " + name);
        }
        private static void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception) { assertions++; Console.WriteLine("ok " + name); return; }
            throw new Exception("FAIL (expected rejection): " + name);
        }
        private static void Signatures()
        {
            // Frozen vectors generated independently with Python hashlib/hmac from official protocols.
            Check(Signing.Baidu("2015063000000001", "apple", "1435660288", "12345678") == "f89f9594663708c1605f3d736d01d2d4", "Baidu official MD5 example");
            Check(Signing.Tencent("test-id", "test-secret", "{\"ImageBase64\":\"dGVzdA==\"}", "GeneralBasicOCR", 1551113065).EndsWith("262c007e9c59e74dc3c6071877c2b92bf875ba399c5822d817855c1e4cd82013"), "Tencent TC3 independent vector / UTC boundary");
            Check(Signing.Volc("test-id", "test-secret", "{\"TargetLanguage\":\"zh\",\"TextList\":[\"hello\"]}", new DateTime(2026, 10, 28, 1, 2, 3, DateTimeKind.Utc)).EndsWith("b44da1cdc31dd54dd011cc72dd121c869cababd70832c0d8038eca11624d5332"), "Volc independent vector");
        }
        private static void Settings()
        {
            string path = Path.Combine(Output, "settings-test.json");
            var settings = new AppSettings { VolcSecret = "local-test-secret", BaiduOcrKey = "key-test" };
            SettingsStore.Save(settings, path);
            Check(SettingsStore.Load(path).VolcSecret == settings.VolcSecret, "local JSON settings round trip");
            Check(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(settings.VolcSecret), "plain local config as requested");
            settings.TargetLanguage = "en";
            SettingsStore.Save(settings, path);
            Check(SettingsStore.Load(path).TargetLanguage == "en", "atomic replacement");
            byte[] corrupted = File.ReadAllBytes(path);
            corrupted[0] = 0;
            File.WriteAllBytes(path, corrupted);
            Reject(delegate { SettingsStore.Load(path); }, "corrupt settings rejected");
            Check(File.ReadAllBytes(path).SequenceEqual(corrupted), "corrupt settings preserved");
            File.Delete(path);
            var defaults = new AppSettings();
            Check(defaults.Hotkeys[0] == "Alt+1" && defaults.Hotkeys[3] == "Ctrl+Shift+A", "requested shortcuts");
            Check(Hotkey.Parse("Alt+1").Key == Keys.D1, "numeric shortcut parsing");
            Reject(delegate { Hotkey.Parse("A"); }, "unmodified shortcut rejected");
            Reject(delegate { Hotkey.Validate(new[] { "Ctrl+Shift+A", "Shift+Ctrl+A" }); }, "equivalent duplicate shortcuts rejected");
            Check(defaults.EffectiveTarget("hello") == "zh" && defaults.EffectiveTarget("你好") == "en", "smart Chinese / English target");
        }
        private static void ServiceSettingsTests()
        {
            var legacyRegion = Json.Read<AppSettings>("{\"TencentRegion\":\"ap-guangzhou\",\"TencentId\":\"legacy-id\"}");
            Check(legacyRegion.TencentId == "legacy-id" && !Json.Write(legacyRegion).Contains("TencentRegion"), "old region config loads while nearest-region setting replaces it");
            var original = new AppSettings();
            var latest = new AppSettings { SourceLanguage = "en", TargetLanguage = "fr" };
            var draft = original.Copy();
            draft.PreserveLiveLanguages(original, latest);
            Check(draft.SourceLanguage == "en" && draft.TargetLanguage == "fr", "saving unrelated settings preserves live translation language changes");
            draft = original.Copy(); draft.TargetLanguage = "ja";
            draft.PreserveLiveLanguages(original, latest);
            Check(draft.SourceLanguage == "en" && draft.TargetLanguage == "ja", "explicit settings language choice takes precedence field by field");
            var settings = Json.Read<AppSettings>("{\"OcrProvider\":\"baidu\",\"TranslateProvider\":\"volc\"}");
            Check(settings.BaiduOcrEnabled && settings.TencentOcrEnabled && settings.VolcTranslateEnabled && settings.BaiduTranslateEnabled, "old settings keep service availability");
            settings.BaiduOcrEnabled = false; settings.VolcTranslateEnabled = false; settings.NormalizeProviders();
            Check(settings.OcrProvider == "tencent" && settings.TranslateProvider == "baidu", "disabled defaults switch to enabled service");
            settings.TencentOcrEnabled = settings.BaiduTranslateEnabled = false; settings.NormalizeProviders();
            Check(settings.OcrProvider == "" && settings.TranslateProvider == "", "all services can be disabled");
            Reject(settings.RequireOcr, "disabled OCR rejected before request");
            Reject(settings.RequireTranslation, "disabled translation rejected before request");
            settings.OcrProvider = "unknown"; settings.TranslateProvider = "unknown";
            Reject(settings.RequireOcr, "unknown OCR provider rejected");
            Reject(settings.RequireTranslation, "unknown translator rejected");
            string folder = Path.Combine(Output, "migration-" + Guid.NewGuid().ToString("N"));
            string legacy = Path.Combine(folder, "old.json"), current = Path.Combine(folder, "new.json");
            SettingsStore.Save(new AppSettings { BaiduOcrKey = "test-migration" }, legacy);
            Check(SettingsStore.LoadMigrating(current, legacy).BaiduOcrKey == "test-migration" && File.Exists(legacy), "rename migrates settings and preserves original");
            SettingsStore.Save(new AppSettings { BaiduOcrKey = "new-config" }, current);
            Check(SettingsStore.LoadMigrating(current, legacy).BaiduOcrKey == "new-config", "migration never overwrites new settings");
            File.Delete(legacy); File.Delete(current); Directory.Delete(folder);
            using (var form = new SettingsForm(new AppSettings(), delegate { }))
            {
                string[] serviceFlags = { "BaiduOcrEnabled", "TencentOcrEnabled", "VolcTranslateEnabled", "BaiduTranslateEnabled", "BingTranslateEnabled" };
                var checks = Descendants(form).OfType<CheckBox>().Where(x => serviceFlags.Contains(x.AccessibleName)).ToArray();
                Check(checks.Length == 5, "five independent service checkboxes");
                var history = Descendants(form).OfType<CheckBox>().Single(x => x.AccessibleName == "HistoryEnabled");
                Check(history.Checked && !Json.Read<AppSettings>("{\"HistoryEnabled\":false}").HistoryEnabled, "history defaults on and an explicit off preference survives loading");
                var selectors = new[] { "ocr", "translator" }.Select(name => (ComboBox)typeof(SettingsForm).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form)).ToArray();
                foreach (var box in checks) box.Checked = false;
                Check(selectors.All(x => !x.Enabled), "all-disabled service selectors are unavailable");
                foreach (var box in checks) box.Checked = true;
                Check(selectors.Length == 2 && selectors.All(x => x.Enabled) && selectors.Select(x => x.Items.Count).OrderBy(x => x).SequenceEqual(new[] { 2, 3 }), "service checkboxes repopulate two OCR and three translation choices");
            }
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }
        private static void TextSplitting()
        {
            string input = String.Concat(Enumerable.Repeat("你好 😀 some words\n", 2300));
            string[] pieces = CloudApi.SplitText(input, 4500).ToArray();
            Check(String.Concat(pieces) == input, "long-text split is lossless");
            Check(pieces.All(x => Encoding.UTF8.GetByteCount(x) <= 4500), "UTF8 byte limit");
            Check(pieces.All(x => !Char.IsHighSurrogate(x[x.Length - 1]) && !Char.IsLowSurrogate(x[0])), "surrogate pairs preserved");
        }
        private sealed class FakeHttp : HttpMessageHandler
        {
            public readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> Steps = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
            public int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                if (Steps.Count == 0) throw new Exception("Unexpected request: " + request.RequestUri.Host);
                return Steps.Dequeue()(request);
            }
            public void Respond(string response, Action<HttpRequestMessage, string> inspect = null)
            {
                Steps.Enqueue(async delegate(HttpRequestMessage request)
                {
                    string content = await request.Content.ReadAsStringAsync();
                    if (inspect != null) inspect(request, content);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
                });
            }
        }
        private static async Task Api()
        {
            var settings = new AppSettings { AccurateOcr = false, BaiduOcrKey = "test-key", BaiduOcrSecret = "test-secret", TencentId = "test-id", TencentSecret = "test-secret", BaiduTranslateId = "test-id", BaiduTranslateSecret = "test-secret", VolcId = "test-id", VolcSecret = "test-secret" };
            var fake = new FakeHttp();
            fake.Respond("{\"access_token\":\"test-token\",\"expires_in\":2592000}", delegate(HttpRequestMessage request, string body)
            {
                Check(request.RequestUri.Query.Length == 0 && body.Contains("client_secret=test-secret"), "Baidu credentials in POST body");
            });
            fake.Respond("{\"words_result\":[{\"words\":\"你好\"},{\"words\":\"世界\"}]}", delegate(HttpRequestMessage request, string body)
            {
                Check(request.RequestUri.AbsolutePath.EndsWith("general_basic") && body.Contains("image="), "Baidu OCR endpoint / image");
            });
            fake.Respond("{\"words_result\":[{\"words\":\"cached token\"}]}");
            using (var api = new CloudApi(fake))
            {
                Check(await api.Ocr(settings, new byte[] { 1, 2, 3 }, CancellationToken.None) == "你好" + Environment.NewLine + "世界", "Baidu OCR response");
                Check(await api.Ocr(settings, new byte[] { 1, 2, 3 }, CancellationToken.None) == "cached token", "Baidu token reused");
                Check(fake.Requests == 3, "no duplicate token request");
                fake.Respond("{\"error_code\":111}");
                fake.Respond("{\"access_token\":\"new-token\",\"expires_in\":2592000}");
                fake.Respond("{\"words_result\":[{\"words\":\"renewed\"}]}", delegate(HttpRequestMessage request, string body) { Check(request.RequestUri.Query.Contains("new-token"), "expired token replaced"); });
                Check(await api.Ocr(settings, new byte[] { 1, 2, 3 }, CancellationToken.None) == "renewed", "expired token refresh once");
                settings.OcrProvider = "tencent";
                fake.Respond("{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"tencent result\"}]}}", delegate(HttpRequestMessage request, string body)
                {
                    string action = request.Headers.GetValues("X-TC-Action").Single();
                    Check(request.RequestUri.Host == "ocr.tencentcloudapi.com" && !request.Headers.Contains("X-TC-Region"), "Tencent OCR uses nearest endpoint without a fixed region");
                    long stamp = Int64.Parse(request.Headers.GetValues("X-TC-Timestamp").Single());
                    Check(request.Headers.GetValues("Authorization").Single() == Signing.Tencent("test-id", "test-secret", body, action, stamp), "Tencent wire body matches signature");
                    Check(request.Content.Headers.ContentType.ToString() == "application/json; charset=utf-8", "Tencent signed content type");
                });
                Check(await api.Ocr(settings, new byte[] { 1, 2, 3 }, CancellationToken.None) == "tencent result", "Tencent OCR response");
                settings.AccurateOcr = true;
                fake.Respond("{\"Response\":{\"TextDetections\":[]}}", delegate(HttpRequestMessage request, string body) { Check(request.Headers.GetValues("X-TC-Action").Single() == "GeneralAccurateOCR" && !body.Contains("LanguageType") && !request.Headers.Contains("X-TC-Region"), "Tencent accurate endpoint uses nearest region"); });
                Check(await api.Ocr(settings, new byte[] { 1, 2, 3 }, CancellationToken.None) == "", "empty OCR remains empty");
                fake.Respond("{\"TranslationList\":[{\"Translation\":\"你好\"}]}", delegate(HttpRequestMessage request, string body)
                {
                    Check(request.RequestUri.Host == "translate.volcengineapi.com", "current official Volc host");
                    Check(request.Content.Headers.ContentType.ToString() == "application/json", "Volc signed content type without charset");
                    DateTime utc = DateTime.ParseExact(request.Headers.GetValues("X-Date").Single(), "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();
                    Check(request.Headers.GetValues("Authorization").Single() == Signing.Volc("test-id", "test-secret", body, utc), "Volc wire body matches signature");
                    Check(request.Headers.GetValues("X-Content-Sha256").Single() == Signing.Sha(body), "Volc body digest");
                });
                Check(await api.Translate(settings, "hello", CancellationToken.None) == "你好", "Volc translation response");
                int requests = fake.Requests;
                Check(await api.Translate(settings, "hello", CancellationToken.None) == "你好" && fake.Requests == requests, "translation memory cache avoids API request");
                settings.TranslateProvider = "baidu";
                settings.TargetLanguage = "ja";
                fake.Respond("{\"trans_result\":[{\"dst\":\"こんにちは\"}]}", delegate(HttpRequestMessage request, string body) { Check(body.Contains("to=jp") && body.Contains("sign="), "Baidu language mapping and signature"); });
                Check(await api.Translate(settings, "hello", CancellationToken.None) == "こんにちは", "Baidu translation response");
                fake.Respond("{\"error_code\":54003,\"error_msg\":\"test-secret\"}");
                try { await api.Translate(settings, "another", CancellationToken.None); throw new Exception("Expected quota error"); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("54003") && !ex.Message.Contains("test-secret"), "provider errors redact raw bodies"); }
                fake.Steps.Enqueue(async delegate(HttpRequestMessage request) { await Task.Delay(20); throw new TaskCanceledException(); });
                try { await api.Translate(settings, "timeout", CancellationToken.None); throw new Exception("Expected timeout"); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("25 秒"), "timeout explained"); }
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    try { await api.Translate(settings, "cancel", cancellation.Token); throw new Exception("Expected cancellation"); }
                    catch (OperationCanceledException) { Check(true, "request cancellation"); }
                }
                Check(fake.Steps.Count == 0, "all expected API exchanges consumed");
            }
        }
        private static Bitmap PageImage(int width, int height)
        {
            var page = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(page))
            using (var font = new Font("Segoe UI", 12))
            {
                graphics.Clear(Color.White);
                var random = new Random(42);
                for (int y = 0; y < height; y += 31)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(random.Next(20, 220), random.Next(20, 220), random.Next(20, 220))))
                    {
                        graphics.FillRectangle(brush, 8, y + 4, random.Next(30, width - 20), 9);
                        graphics.DrawString("Line " + y + " / SnapTransOCR 测试", font, Brushes.Black, 20, y + 10);
                    }
                }
            }
            return page;
        }
        private static void Images()
        {
            Check(ScreenCapture.Selection(new Point(300, 200), new Point(-20, -10), new Size(500, 400)) == new Rectangle(0, 0, 300, 200), "reverse selection and clipping");
            using (var tiny = new Bitmap(3, 10)) Reject(delegate { ScreenCapture.EncodeOcr(tiny); }, "small OCR selection rejected");
            using (var page = PageImage(480, 1900))
            using (var first = page.Clone(new Rectangle(0, 0, 480, 600), PixelFormat.Format24bppRgb))
            using (var second = page.Clone(new Rectangle(0, 240, 480, 600), PixelFormat.Format24bppRgb))
            using (var third = page.Clone(new Rectangle(0, 470, 480, 600), PixelFormat.Format24bppRgb))
            using (var unrelated = page.Clone(new Rectangle(0, 1250, 480, 600), PixelFormat.Format24bppRgb))
            using (var stitcher = new Stitcher(first))
            {
                string message;
                Check(!stitcher.Append(first, out message), "unchanged scroll frame skipped");
                Check(stitcher.Append(second, out message) && stitcher.TotalHeight == 840, "scroll overlap 240 pixels");
                Check(stitcher.Append(third, out message) && stitcher.TotalHeight == 1070, "second scroll overlap 230 pixels");
                Check(!stitcher.Append(unrelated, out message) && stitcher.TotalHeight == 1070, "insufficient overlap rejected without changing output");
                using (var result = stitcher.Finish())
                {
                    bool same = true;
                    for (int y = 0; y < result.Height; y += 7) for (int x = 0; x < result.Width; x += 7) if (result.GetPixel(x, y).ToArgb() != page.GetPixel(x, y).ToArgb()) same = false;
                    Check(same, "stitched pixels match original page");
                    result.Save(Path.Combine(Output, "scroll-test.png"));
                }
                using (var encoded = new MemoryStream(ScreenCapture.EncodeOcr(first)))
                using (var decoded = Image.FromStream(encoded)) Check(decoded.Width == first.Width && decoded.Height == first.Height, "OCR image encoding keeps ordinary resolution");
            }
            using (var wide = new Bitmap(5000, 100))
            using (var encoded = new MemoryStream(ScreenCapture.EncodeOcr(wide)))
            using (var decoded = Image.FromStream(encoded)) Check(decoded.Width == 4096, "large OCR image resized to bounded dimensions");
            using (var tall = new Bitmap(820, 9100))
            {
                Rectangle[] regions = ScreenCapture.OcrRegions(tall).ToArray();
                Check(regions.Length == 3 && regions.Sum(r => r.Height) == tall.Height, "long OCR split covers entire image");
                Check(regions.All(r => r.Width == tall.Width && r.Height <= 3500), "long OCR preserves text resolution");
            }
        }
        private static void Annotation()
        {
            var source = new Bitmap(200, 160, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.White);
            using (var canvas = new ImageCanvas(source) { Size = new Size(400, 300) })
            {
                canvas.AddMark(new Mark { Kind = Tool.Rectangle, Bounds = new RectangleF(20, 20, 80, 60), Color = Color.Blue, Width = 4 });
                using (var result = canvas.Render()) Check(result.GetPixel(20, 40).B > 240 && result.GetPixel(20, 40).R < 30, "rectangle annotation rendered");
                canvas.AddMark(new Mark { Kind = Tool.Crop, Bounds = new RectangleF(10, 10, 100, 80) });
                using (var result = canvas.Render()) Check(result.Size == new Size(100, 80) && result.GetPixel(10, 30).R < 30, "crop transforms image and annotations");
                canvas.Undo();
                using (var result = canvas.Render()) Check(result.Size == source.Size, "crop undo restores canvas");
                canvas.Redo();
                using (var result = canvas.Render()) Check(result.Size == new Size(100, 80), "crop redo");
                var pixels = new Bitmap(2, 2);
                using (var graphics = Graphics.FromImage(pixels)) graphics.Clear(Color.Green);
                canvas.AddMark(new Mark { Kind = Tool.Mosaic, Bounds = new RectangleF(40, 40, 30, 30), Pixels = pixels });
                using (var result = canvas.Render()) Check(result.GetPixel(40, 40).G == Color.Green.G && result.GetPixel(40, 40).R == 0, "mosaic flattened into exported image");
                canvas.Undo();
                using (var result = canvas.Render()) Check(result.GetPixel(40, 40).R == 255, "mosaic undo");
            }
            using (var overlay = new CaptureForm("", new AppSettings(), 0, PageImage(400, 300), new Rectangle(-300, 10, 400, 300)))
            {
                overlay.PreviewSelection(new Rectangle(20, 30, 120, 80));
                overlay.Complete(CaptureAction.Ocr);
                using (var image = overlay.Captured) Check(image.Size == new Size(120, 80) && overlay.ScreenRegion == new Rectangle(-280, 40, 120, 80), "capture returns region at negative desktop coordinates");
                Check(overlay.Action == CaptureAction.Ocr, "capture toolbar action is retained");
            }
        }
        private static void InlineText()
        {
            var source = new Bitmap(500, 240);
            using (var g = Graphics.FromImage(source)) g.Clear(Color.White);
            using (var canvas = new ImageCanvas(source) { PixelView = true, Size = new Size(500, 240) })
            {
                canvas.Fit(); canvas.BeginText(new PointF(30, 35));
                Check(canvas.EditingText && canvas.TextInput.Parent.Parent == canvas, "text is edited directly inside capture canvas");
                canvas.TextInput.Text = "Inline text\r\n文字标注";
                canvas.Color = Color.Blue; canvas.TextSize = 36; canvas.TextBold = true;
                Check(canvas.TextInput.ForeColor == Color.Blue && canvas.TextInput.Font.Bold, "live text color and bold changes");
                using (var rendered = canvas.Render())
                {
                    int blue = 0; for (int y = 20; y < 180; y++) for (int x = 20; x < 300; x++) { Color p = rendered.GetPixel(x,y); if (p.B > 150 && p.R < 130) blue++; }
                    Check(blue > 100, "inline multilingual text is flattened into exported screenshot");
                }
                Check(!canvas.EditingText, "export commits current text without a dialog");
                canvas.Undo();
                using (var rendered = canvas.Render()) Check(rendered.GetPixel(50,50).ToArgb() == Color.White.ToArgb(), "inline text undo");
                canvas.BeginText(new PointF(10,10)); canvas.TextInput.Text = "cancel"; canvas.CancelText();
                Check(!canvas.EditingText, "escape cancels inline editor");
                using (var rendered = canvas.Render()) Check(rendered.GetPixel(10,10).ToArgb() == Color.White.ToArgb(), "cancelled text is not exported");
            }
            using (var button = new GlyphButton("copy", "copy", delegate { })) Check(!typeof(Button).IsAssignableFrom(button.GetType()), "icons have no native button chrome");
        }
        private static void Startup()
        {
            Check(AutoStart.Command(@"C:\Program Files\SnapTransOCR\SnapTransOCR.exe") == "\"C:\\Program Files\\SnapTransOCR\\SnapTransOCR.exe\" --tray", "startup command quotes spaces and starts in tray");
            Reject(delegate { AutoStart.Command("bad\"path"); }, "invalid startup executable path rejected");
            const string testPath = @"Software\SnapTransOCR\Tests\Startup";
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testPath))
                {
                    AutoStart.Write(key, "test --tray"); Check((string)key.GetValue("SnapTransOCR") == "test --tray", "startup registry value write");
                    AutoStart.Write(key, null); Check(key.GetValue("SnapTransOCR") == null, "startup registry value can be disabled");
                }
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(testPath, false); }
        }
        private static void ClipboardSelection()
        {
            Exception failure = null;
            using (ClipboardSnapshot previous = ClipboardSnapshot.Capture())
            {
            using (var fixture = new Form { Text = "SnapTransOCR clipboard verification", ClientSize = new Size(480, 100) })
            using (var bitmap = new Bitmap(24, 24))
            {
                var input = new ClipboardCopyTextBox { Dock = DockStyle.Top, Text = "read this selection only" };
                fixture.Controls.Add(input);
                fixture.Shown += async delegate
                {
                    try
                    {
                        Native.ShowWindow(fixture.Handle, 9);
                        Native.SetForegroundWindow(fixture.Handle);
                        input.Focus(); input.Select(5, 14);
                        await Task.Delay(180);
                        if (Native.GetForegroundWindow() != fixture.Handle)
                        {
                            Console.WriteLine("SKIP interactive clipboard: Windows did not grant foreground focus; no clipboard change was made.");
                            return;
                        }
                        var original = new DataObject();
                        original.SetText("clipboard sentinel");
                        original.SetImage(bitmap);
                        original.SetData("SnapTransOCR.TestMarker", "custom format sentinel");
                        Clipboard.SetDataObject(original, true);
                        string text = await new SelectionReader().ReadCopy(fixture.Handle);
                        Check(text == "this selection", "real Ctrl+C selected text acquisition");
                        uint restoredSequence = Native.GetClipboardSequenceNumber();
                        await ClipboardContentionTests.CheckReadable(Check, restoredSequence, delegate { return Clipboard.GetText() == "clipboard sentinel"; }, "text clipboard restored after selection");
                        await ClipboardContentionTests.CheckReadable(Check, restoredSequence, delegate { return Clipboard.ContainsImage(); }, "image clipboard format preserved");
                        await ClipboardContentionTests.CheckReadable(Check, restoredSequence, delegate { return Clipboard.GetData("SnapTransOCR.TestMarker") as string == "custom format sentinel"; }, "custom clipboard format preserved");
                        input.Select(0, 0);
                        string empty = await new SelectionReader().TryReadExplicit(fixture.Handle);
                        Check(String.IsNullOrEmpty(empty), "unselected text returns silently");
                        await ClipboardContentionTests.CheckReadable(Check, restoredSequence, delegate { return Clipboard.GetText() == "clipboard sentinel"; }, "empty selection leaves clipboard unchanged");
                        input.Select(5, 14);
                        await ClipboardContentionTests.Run(Check, fixture, input, original);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { fixture.Close(); }
                };
                try { Application.Run(fixture); }
                finally { if (previous.Data != null) Clipboard.SetDataObject(previous.Data, true); else Clipboard.Clear(); }
            }
            if (failure != null) throw failure;
            }
        }
        private static void Snapshot(Form form, string file)
        {
            if (headless)
            {
                Ui.RenderOffscreen = true; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000); form.ShowInTaskbar = false;
            }
            form.Show(); Application.DoEvents(); form.PerformLayout();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                using (var graphics = Graphics.FromImage(bitmap)) PrintRichEdit(form, form, graphics);
                bitmap.Save(Path.Combine(Output, file));
            }
            form.Hide();
            Ui.RenderOffscreen = false;
        }
        [StructLayout(LayoutKind.Sequential)] private struct PrintRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct CharRange { public int First, Last; }
        [StructLayout(LayoutKind.Sequential)] private struct FormatRange { public IntPtr Hdc, Target; public PrintRect Area, Page; public CharRange Range; }
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr PrintMessage(IntPtr window, int message, IntPtr parameter, ref FormatRange range);
        private static void PrintRichEdit(Form form, Control parent, Graphics graphics)
        {
            foreach (Control child in parent.Controls)
            {
                var edit = child as RichTextBox;
                if (edit != null && edit.Visible && edit.TextLength > 0)
                {
                    Point origin = form.PointToClient(edit.PointToScreen(Point.Empty));
                    double twips = 1440.0 / graphics.DpiX;
                    var box = new PrintRect { Left = (int)(origin.X * twips), Top = (int)(origin.Y * twips), Right = (int)((origin.X + edit.Width) * twips), Bottom = (int)((origin.Y + edit.Height) * twips) };
                    IntPtr hdc = graphics.GetHdc();
                    try { var range = new FormatRange { Hdc = hdc, Target = hdc, Area = box, Page = box, Range = new CharRange { First = 0, Last = -1 } }; PrintMessage(edit.Handle, 0x439, new IntPtr(1), ref range); }
                    finally { Native.SendMessage(edit.Handle, 0x439, IntPtr.Zero, IntPtr.Zero); graphics.ReleaseHdc(hdc); }
                }
                PrintRichEdit(form, child, graphics);
            }
        }
        private static void UiAndCapture()
        {
            using (var main = new MainForm())
            {
                main.RefreshSettings(new AppSettings());
                main.SetSource("Lightweight capture.\r\n截图、OCR、翻译，常用功能集中在一个工具中。");
                main.SetOutput("轻量截图。\r\nCapture, OCR and translation in one tool.");
                Snapshot(main, "main.png");
                Check(main.ClientSize.Width <= Ui.Scale(main, 600) && main.ClientSize.Width >= Ui.Scale(main, 480), "translation opens at a compact width");
            }
            using (var form = new SettingsForm(new AppSettings(), delegate { })) { Snapshot(form, "settings.png"); Check(form.Controls.Count > 0, "settings UI constructs without credentials"); }
            using (var image = PageImage(840, 440))
            {
                using (var editor = new EditorForm((Bitmap)image.Clone())) { Snapshot(editor, "editor.png"); Check(editor.Controls.OfType<ImageCanvas>().Any(), "editor UI constructs"); }
                using (var pin = new PinForm((Bitmap)image.Clone())) { Snapshot(pin, "pin.png"); Check(pin.TopMost, "pinned image is topmost"); }
            }
            using (var desktop = ScreenCapture.Desktop(new Rectangle(0, 0, 80, 80))) Check(desktop.Width == 80 && desktop.Height == 80, "real GDI desktop capture");
        }
        private static void RenderReferences()
        {
            using (var ocr = new OcrForm())
            {
                ocr.RefreshSettings(new AppSettings { OcrProvider = "tencent" });
                ocr.SetSource("Screen capture and OCR.\r\n识别结果可编辑、复制或发送到翻译窗口。\r\n\r\n保留段落，按需合并断行。");
                Snapshot(ocr, "ocr-result.png");
            }
            using (var main = new MainForm())
            {
                main.RefreshSettings(new AppSettings { TargetLanguage = "zh" });
                Snapshot(main, "translation-empty.png");
                main.SetSource("High performance.");
                main.SetOutput("高性能。");
                Snapshot(main, "translation-result.png");
            }
            using (var settings = new SettingsForm(new AppSettings(), delegate { }))
            {
                settings.SelectPage("翻译设置"); Snapshot(settings, "settings-translation.png");
                settings.SelectPage("识别设置"); Snapshot(settings, "settings-ocr.png");
                settings.SelectPage("热键设置"); Snapshot(settings, "settings-hotkeys.png");
                settings.SelectPage("常规设置"); Snapshot(settings, "settings-general.png");
            }
            var desktop = new Bitmap(1280, 800);
            using (var graphics = Graphics.FromImage(desktop))
            using (var heading = new Font("Microsoft YaHei UI", 22))
            using (var body = new Font("Microsoft YaHei UI", 13))
            {
                graphics.Clear(Color.FromArgb(35, 63, 101));
                graphics.FillRectangle(Brushes.White, 150, 65, 980, 650);
                graphics.FillRectangle(new SolidBrush(Ui.Background), 150, 65, 980, 45);
                graphics.DrawString("Documents", heading, Brushes.Black, 195, 145);
                graphics.DrawString("Research notes", body, Brushes.Gray, 195, 220);
                graphics.DrawString("Circuit design", body, Brushes.Gray, 195, 264);
                graphics.DrawString("References", body, Brushes.Gray, 195, 308);
            }
            using (var capture = new CaptureForm("截图 / OCR", new AppSettings(), 0, desktop, new Rectangle(0, 0, 1280, 800)))
            {
                capture.PreviewSelection(new Rectangle(180, 130, 880, 370)); Snapshot(capture, "screenshot-overlay.png");
                var canvas = capture.Controls.OfType<ImageCanvas>().Single();
                canvas.Tool = Tool.Text; canvas.BeginText(new PointF(30, 210)); canvas.TextInput.Text = "直接输入文字";
                canvas.TextSize = 32; canvas.TextBold = true;
                Snapshot(capture, "screenshot-text.png");
            }
        }
    }
}

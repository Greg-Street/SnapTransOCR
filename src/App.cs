using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class CommandWindow : NativeWindow, IDisposable
    {
        public const string Caption = "SnapTransOCR.Commands.v1";
        public event Action<int> Command;
        public CommandWindow(string caption = null) { CreateHandle(new CreateParams { Caption = caption ?? Caption }); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312 && Command != null) Command(message.WParam.ToInt32());
            else if (message.Msg == 0x8007 && Command != null) Command(100);
            else if (message.Msg == 0x8008 && Command != null) Command(101);
            base.WndProc(ref message);
        }
        public void Dispose() { DestroyHandle(); }
    }

    internal sealed partial class DesktopApp : ApplicationContext
    {
        private readonly MainForm main = new MainForm();
        private readonly CommandWindow commands = new CommandWindow();
        private readonly CloudApi api = new CloudApi();
        private readonly HistoryStore history;
        private readonly SelectionReader reader = new SelectionReader();
        private readonly NotifyIcon tray;
        private readonly List<Form> imageWindows = new List<Form>();
        private readonly HashSet<PinForm> hiddenPins = new HashSet<PinForm>();
        private readonly HashSet<Task> historyWrites = new HashSet<Task>();
        private readonly Action<AppSettings> persistSettings;
        private readonly System.Windows.Forms.Timer languageSaveTimer;
        private ClipboardHistory clipboardHistory;
        private System.Windows.Forms.Timer historyCleanupTimer;
        private Task historyCleanup = Task.CompletedTask;
        private Task languageWrite = Task.CompletedTask;
        private bool languageSavePending;
        private AppSettings settings;
        private CancellationTokenSource job;
        private CancellationTokenSource ocrJob;
        private OcrForm ocrWindow;
        private int ocrVersion;
        private int version;
        private bool capturing, reading, exiting, disposed;
        private SettingsForm settingsWindow;
        private HistoryForm historyWindow;
        private bool historyTopmost;
        private EditorForm historyEditor;
        private ScrollForm scroll;
        private GifRecorderForm recorder;
        public DesktopApp(AppSettings initial, bool hidden, string startupWarning = null, Action<AppSettings> persistSettings = null, bool startClipboardHistory = false)
        {
            settings = initial;
            history = new HistoryStore(retentionDays: () => settings.HistoryRetentionDays);
            this.persistSettings = persistSettings ?? SettingsStore.Save;
            languageSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            languageSaveTimer.Tick += delegate { SaveLanguages(); };
            IntPtr unused = main.Handle;
            main.RefreshSettings(settings);
            main.UserSizeChanged += delegate(int width, int height)
            {
                settings.TranslateWindowWidth = width; settings.TranslateWindowHeight = height;
                try { SaveSettings(settings); } catch (Exception ex) { main.SetStatus("窗口尺寸未保存（" + ex.GetType().Name + "）"); }
            };
            main.TranslateRequested += async delegate(string text) { await Translate(text); };
            main.ServiceTranslateRequested += async delegate(string text, string provider) { await Translate(text, provider); };
            main.LanguagesChanged += delegate(string from, string to)
            {
                settings.SourceLanguage = from; settings.TargetLanguage = to;
                languageSavePending = true;
                languageSaveTimer.Stop(); languageSaveTimer.Start();
            };
            main.SettingsRequested += ShowSettings;
            main.HistoryRequested += ShowHistory;
            main.CancelRequested += Cancel;
            commands.Command += async delegate(int id)
            {
                if (exiting) return;
                if (id == 100) { main.Present(null); return; }
                if (id == 101) { Exit(); return; }
                if (id == 3) await SelectedTranslation();
                else if (id >= 0 && id <= 2) await Capture(id, 0);
                else if (id == 4) await Capture(4, 0);
                else if (id == 5) ClipboardOcr();
                else if (id == 6) ClipboardPin();
                else if (id == 7) ShowHistory();
                else if (id == 8) TogglePins();
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("输入翻译", null, delegate { main.Present(null); });
            menu.Items.Add("OCR 文字窗口", null, delegate { OcrWindow().Present(null); });
            menu.Items.Add("历史 / 剪贴板", null, delegate { ShowHistory(); });
            var pauseHistory = new ToolStripMenuItem("暂停记录") { CheckOnClick = true, Checked = !settings.HistoryEnabled };
            pauseHistory.CheckedChanged += delegate { ChangeHistoryRecording(!pauseHistory.Checked); };
            menu.Opening += delegate { pauseHistory.Checked = !settings.HistoryEnabled; };
            menu.Items.Add(pauseHistory);
            menu.Items.Add("截图 / OCR 入口   Alt+1", null, async delegate { await Capture(0, 0); });
            menu.Items.Add("延时 3 秒截图", null, async delegate { await Capture(0, 3000); });
            menu.Items.Add("延时 5 秒截图", null, async delegate { await Capture(0, 5000); });
            menu.Items.Add("滚动长截图", null, async delegate { await Capture(3, 0); });
            menu.Items.Add("录制 GIF 动图", null, async delegate { await Capture(5, 0); });
            menu.Items.Add("剪贴板贴图", null, delegate { ClipboardPin(); });
            menu.Items.Add("剪贴板 OCR", null, delegate { ClipboardOcr(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("API 与快捷键设置", null, delegate { ShowSettings(); });
            menu.Items.Add("退出", null, delegate { Exit(); });
            tray = new NotifyIcon { Icon = Program.AppIcon, Text = "SnapTransOCR · Alt+1 截图 / Ctrl+Shift+A 划词翻译", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowSettings(); };
            try { Register(settings, false); }
            catch (Exception ex) { main.SetStatus(ex.Message); }
            if (startupWarning != null) main.SetStatus(startupWarning);
            // Background capture is started by the production bootstrap; isolated UI fixtures remain side-effect free.
            if (startClipboardHistory)
            {
                try { clipboardHistory = new ClipboardHistory(history, () => settings.HistoryEnabled, ex => { if (!exiting && !main.IsDisposed) main.SetStatus("剪贴板记录未保存（" + ex.GetType().Name + "）。"); }); }
                catch (Exception ex) { main.SetStatus("剪贴板监听未启动（" + ex.GetType().Name + "）。"); }
                historyCleanupTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
                historyCleanupTimer.Tick += delegate { PruneHistory(); };
                historyCleanupTimer.Start(); PruneHistory();
            }
            if (!hidden) main.Present(null);
        }

        private void PruneHistory()
        {
            if (exiting || !historyCleanup.IsCompleted) return;
            historyCleanup = history.PruneAsync();
            historyCleanup.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        private void ChangeHistoryRecording(bool enabled)
        {
            if (enabled == settings.HistoryEnabled) return;
            var next = settings.Copy(); next.HistoryEnabled = enabled;
            try
            {
                SaveSettings(next); settings = next;
                if (clipboardHistory != null) clipboardHistory.ResetEnabledState();
                if (settingsWindow != null) settingsWindow.RefreshHistory(enabled);
                if (historyWindow != null) historyWindow.View.RefreshRecording(enabled);
            }
            catch (Exception ex)
            {
                if (settingsWindow != null) settingsWindow.RefreshHistory(settings.HistoryEnabled);
                if (historyWindow != null) historyWindow.View.RefreshRecording(settings.HistoryEnabled);
                main.SetStatus("记录开关未保存（" + ex.GetType().Name + "）。");
            }
        }

        private async void SaveLanguages()
        {
            languageSaveTimer.Stop();
            if (!languageSavePending || exiting) return;
            if (!languageWrite.IsCompleted) { languageSaveTimer.Start(); return; }
            AppSettings snapshot = settings.Copy();
            languageSavePending = false;
            Task write = languageWrite = Task.Run(delegate { persistSettings(snapshot); });
            try { await write; }
            catch (Exception ex)
            {
                if (languageWrite == write && !exiting)
                {
                    languageSavePending = true;
                    main.SetStatus("语言设置未保存（" + ex.GetType().Name + "）。可在设置中重试保存。");
                }
            }
        }
        private void SaveSettings(AppSettings value)
        {
            languageSaveTimer.Stop();
            // An explicit full save must follow any older language snapshot on disk.
            try { languageWrite.GetAwaiter().GetResult(); }
            catch (Exception) { } // The current full snapshot retries a failed background write.
            persistSettings(value);
            languageWrite = Task.CompletedTask;
            languageSavePending = false;
        }

        private void Register(AppSettings value, bool strict)
        {
            value.NormalizeHotkeys();
            Hotkey.Validate(value.Hotkeys);
            var failed = new List<string>();
            for (int i = 0; i < AppSettings.HotkeyCount; i++)
            {
                Hotkey key = Hotkey.Parse(value.Hotkeys[i]);
                if (key != null && !Native.RegisterHotKey(commands.Handle, i, key.Modifiers | 0x4000, (uint)key.Key)) failed.Add(value.Hotkeys[i]);
            }
            if (failed.Count > 0)
            {
                string message = "快捷键被其他软件占用：" + String.Join("、", failed) + "。请退出冲突软件或在设置中修改。";
                if (strict) throw new InvalidOperationException(message);
                main.SetStatus(message);
            }
        }
        private void Unregister() { for (int i = 0; i < AppSettings.HotkeyCount; i++) Native.UnregisterHotKey(commands.Handle, i); }
        private void ApplySettings(AppSettings next)
        {
            next.NormalizeHotkeys();
            bool retentionChanged = next.HistoryRetentionDays != settings.HistoryRetentionDays;
            bool changed = !settings.Hotkeys.SequenceEqual(next.Hotkeys);
            string previousStart = AutoStart.Read();
            try
            {
                if (changed) { Unregister(); Register(next, true); }
                if (next.StartWithWindows != settings.StartWithWindows) AutoStart.Set(next.StartWithWindows, Application.ExecutablePath);
                SaveSettings(next);
            }
            catch { if (changed) { Unregister(); Register(settings, false); } if (AutoStart.Read() != previousStart) AutoStart.Restore(previousStart); throw; }
            settings = next;
            if (retentionChanged) PruneHistory();
            if (clipboardHistory != null) clipboardHistory.ResetEnabledState();
            Cancel(); CancelOcr();
            main.RefreshSettings(next);
            if (ocrWindow != null) ocrWindow.RefreshSettings(next);
            foreach (var editor in imageWindows.OfType<EditorForm>()) editor.RefreshSettings(next);
            if (settingsWindow != null)
            {
                settingsWindow.RefreshHistory(next.HistoryEnabled);
                if (retentionChanged) settingsWindow.RefreshHistoryEntries();
            }
            if (historyWindow != null)
            {
                historyWindow.View.RefreshRecording(next.HistoryEnabled);
                historyWindow.RefreshShortcut(next.Hotkeys[7]);
                if (retentionChanged) historyWindow.View.RefreshEntries();
            }
            main.SetStatus("");
        }
        private void ShowSettings()
        {
            if (exiting) return;
            if (settingsWindow != null && !settingsWindow.IsDisposed) { settingsWindow.Show(); settingsWindow.Activate(); return; }
            settings.StartWithWindows = AutoStart.Enabled(Application.ExecutablePath);
            AppSettings original = settings.Copy();
            settingsWindow = new SettingsForm(original, delegate(AppSettings next)
            {
                next.PreserveLiveLanguages(original, settings);
                next.PreserveLiveWindowSize(settings);
                next.PreserveLiveAnnotationStyle(original, settings);
                next.GifQuality = settings.GifQuality;
                if (next.HistoryEnabled == original.HistoryEnabled) next.HistoryEnabled = settings.HistoryEnabled;
                ApplySettings(next);
                original = next.Copy();
            }, history);
            settingsWindow.HistoryImageRequested += async delegate(Bitmap image) { await OpenEditor(image, false); };
            settingsWindow.HistoryRecordingChanged += ChangeHistoryRecording;
            settingsWindow.HistoryRequested += ShowHistory;
            settingsWindow.FormClosed += delegate { settingsWindow = null; };
            settingsWindow.Show();
        }

        private void ShowHistory()
        {
            if (exiting) return;
            if (historyWindow == null || historyWindow.IsDisposed)
            {
                historyWindow = new HistoryForm(history, settings.HistoryEnabled, historyTopmost, settings.Hotkeys[7]);
                historyWindow.View.ImageRequested += async delegate(Bitmap image) { await OpenEditor(image, false); };
                historyWindow.View.RecordingChanged += ChangeHistoryRecording;
                historyWindow.SettingsRequested += ShowSettings;
                historyWindow.PinChanged += delegate(bool pinned) { historyTopmost = pinned; };
                historyWindow.FormClosed += delegate { historyWindow = null; };
            }
            historyWindow.Present();
        }
        private async Task<string> RecordHistory(AppSettings snapshot, string kind, string source, string translated, Bitmap image = null)
        {
            if (!snapshot.HistoryEnabled || !settings.HistoryEnabled || exiting) return "";
            try
            {
                bool saved = await history.AddAsync(new HistoryEntry { Kind = kind, Source = source, Translation = translated,
                    OcrProvider = image == null ? "" : snapshot.OcrProvider, TranslateProvider = String.IsNullOrEmpty(translated) ? "" : snapshot.TranslateProvider == "ark" ? "ark:" + snapshot.ArkModel : snapshot.TranslateProvider }, image);
                if (!exiting && settingsWindow != null) settingsWindow.RefreshHistoryEntries();
                return saved ? "" : "已完成，内容超过历史记录容量，未保存历史。";
            }
            catch (Exception ex) { return "已完成，历史记录未保存（" + ex.GetType().Name + "）。"; }
        }
        private void QueueCaptureHistory(AppSettings snapshot, Bitmap image)
        {
            if (!snapshot.HistoryEnabled || !settings.HistoryEnabled || exiting) { if (image != null) image.Dispose(); return; }
            Task<string> write = SaveCaptureHistory(image);
            historyWrites.Add(write);
            write.ContinueWith(completed =>
            {
                try
                {
                    main.BeginInvoke(new Action(delegate
                    {
                        historyWrites.Remove(write);
                        if (exiting) return;
                        if (settingsWindow != null) settingsWindow.RefreshHistoryEntries();
                        if (completed.Result.Length > 0) main.SetStatus(completed.Result);
                    }));
                }
                catch (InvalidOperationException) { } // The UI can be gone after Exit has joined this write.
            }, TaskScheduler.Default);
        }
        private async Task<string> SaveCaptureHistory(Bitmap image)
        {
            using (image)
            {
                try
                {
                    bool saved = await history.AddAsync(new HistoryEntry { Kind = "capture", Source = "", Translation = "", OcrProvider = "", TranslateProvider = "" }, image).ConfigureAwait(false);
                    return saved ? "" : "已完成，内容超过历史记录容量，未保存历史。";
                }
                catch (Exception ex) { return "已完成，历史记录未保存（" + ex.GetType().Name + "）。"; }
            }
        }

        private async Task Capture(int mode, int delay)
        {
            if (capturing || scroll != null || recorder != null || exiting) return;
            capturing = true;
            main.CaptureInProgress = true;
            try
            {
                if (mode == 1) settings.RequireOcr();
                if (mode == 2 || mode == 4 && settings.ImageTranslateProvider != "baidu_image") { settings.RequireOcr(); settings.RequireTranslation(); }
                if (mode == 4 && settings.ImageTranslateProvider == "baidu_image") RequireBaiduImage(settings);
                if (delay > 0) await Task.Delay(delay);
                else await Task.Yield();
                if (exiting) return;
                // Let the invoking menu finish closing; keep application windows in the captured desktop.
                Native.DwmFlush();
                using (var capture = new CaptureForm(mode == 3 ? "框选滚动内容区域" : mode == 5 ? "框选 GIF 录制区域" : "截图 / OCR", settings.Copy(), mode))
                {
                    AnnotationStyle originalStyle = capture.Style;
                    DialogResult result = capture.ShowDialog();
                    RememberAnnotationStyle(originalStyle, capture.Style);
                    if (result != DialogResult.OK) return;
                    Bitmap image = capture.Captured;
                    if (capture.Action == CaptureAction.Scroll)
                    {
                        await Task.Yield();
                        Native.DwmFlush();
                        image = ScreenCapture.Desktop(capture.ScreenRegion);
                        try
                        {
                            scroll = new ScrollForm(capture.ScreenRegion, image);
                            scroll.Completed += async delegate(Bitmap completed) { await OpenEditor(completed); };
                            scroll.FormClosed += delegate { scroll = null; main.CaptureInProgress = false; };
                            scroll.Show();
                        }
                        finally { image.Dispose(); }
                    }
                    else if (capture.Action == CaptureAction.Gif)
                    {
                        recorder = new GifRecorderForm(capture.ScreenRegion, initialQuality: settings.GifQuality, rememberQuality: quality =>
                        {
                            if (settings.GifQuality == quality) return;
                            settings.GifQuality = quality;
                            try { SaveSettings(settings); }
                            catch (Exception ex) { main.SetStatus("GIF 画质未保存（" + ex.GetType().Name + "）。"); }
                        });
                        recorder.FormClosed += delegate { recorder = null; main.CaptureInProgress = false; };
                        recorder.Show();
                    }
                    else if (capture.Action == CaptureAction.Ocr || capture.Action == CaptureAction.Translate) { var task = Ocr(image, capture.Action == CaptureAction.Translate); }
                    else if (capture.Action == CaptureAction.Handwriting || capture.Action == CaptureAction.QrCode || capture.Action == CaptureAction.Table || capture.Action == CaptureAction.BaiduTable)
                    {
                        var task = SpecialOcr(image, capture.Action == CaptureAction.Handwriting ? RecognitionKind.Handwriting : capture.Action == CaptureAction.QrCode ? RecognitionKind.QrCode : capture.Action == CaptureAction.BaiduTable ? RecognitionKind.BaiduTable : RecognitionKind.Table);
                    }
                    else if (capture.Action == CaptureAction.ImageTranslate) { var task = TranslateImage(image, capture.ScreenRegion.Location); }
                    else await CompleteCapture(image, capture.Action, capture.ScreenRegion.Location);
                }
            }
            catch (Exception ex) { if (!exiting) Ui.Error(main.Visible ? main : null, ex); }
            finally
            {
                capturing = false;
                if (scroll == null && recorder == null) main.CaptureInProgress = false;
            }
        }
        private async Task CompleteCapture(Bitmap image, CaptureAction action, Point? location = null)
        {
            Bitmap retained = null;
            var snapshot = settings.Copy();
            try
            {
                bool completed = action == CaptureAction.Copy || snapshot.ScreenshotCopyOnConfirm;
                if (completed) await Clip.SetImage(image);
                if (action == CaptureAction.Save) completed = ImageFiles.Save(null, image) || completed;
                if (action == CaptureAction.Pin)
                {
                    // The pin owns its bitmap. Disk encoding retains an independent copy in case the pin closes.
                    if (snapshot.HistoryEnabled) retained = (Bitmap)image.Clone();
                    Track(new PinForm(image, snapshot.PinTopmost, sourceLocation: location));
                    image = null; completed = true;
                }
                if (completed)
                {
                    Bitmap pending = retained ?? image;
                    retained = image = null;
                    QueueCaptureHistory(snapshot, pending);
                }
            }
            finally { if (image != null) image.Dispose(); if (retained != null) retained.Dispose(); }
        }
        private void Track(Form window)
        {
            imageWindows.Add(window);
            window.Disposed += delegate { imageWindows.Remove(window); if (window is PinForm) hiddenPins.Remove((PinForm)window); };
            try { window.Show(); }
            catch { imageWindows.Remove(window); window.Dispose(); throw; }
        }
        private void TogglePins()
        {
            if (hiddenPins.Count > 0)
            {
                foreach (var pin in hiddenPins.ToArray()) if (!pin.IsDisposed) pin.Show();
                hiddenPins.Clear();
                return;
            }
            foreach (var pin in imageWindows.OfType<PinForm>().Where(pin => pin.Visible).ToArray())
            {
                hiddenPins.Add(pin);
                pin.Hide();
            }
        }
        private void RememberAnnotationStyle(AnnotationStyle original, AnnotationStyle current)
        {
            var next = settings.Copy();
            if (original.Color != current.Color) next.CaptureColorArgb = current.Color.ToArgb();
            if (original.Stroke != current.Stroke) next.CaptureStroke = current.Stroke;
            if (original.TextSize != current.TextSize) next.CaptureTextSize = current.TextSize;
            if (original.TextBold != current.TextBold) next.CaptureTextBold = current.TextBold;
            if (next.CaptureColorArgb == settings.CaptureColorArgb && next.CaptureStroke == settings.CaptureStroke
                && next.CaptureTextSize == settings.CaptureTextSize && next.CaptureTextBold == settings.CaptureTextBold) return;
            settings = next;
            try { SaveSettings(next); }
            catch (Exception ex) { main.SetStatus("标注样式未保存（" + ex.GetType().Name + "）。可在设置中重试保存。"); }
        }
        private Task OpenEditor(Bitmap image, bool recordHistory = true)
        {
            Bitmap retained = null;
            try
            {
                if (!recordHistory && historyEditor != null) historyEditor.Close();
                if (recordHistory && settings.HistoryEnabled) retained = (Bitmap)image.Clone();
                var editor = new EditorForm(image, settings.Copy());
                AnnotationStyle originalStyle = editor.Style;
                image = null; // The canvas now owns the image, including when showing the window fails.
                editor.FormClosed += delegate { RememberAnnotationStyle(originalStyle, editor.Style); };
                editor.OcrRequested += async delegate(Bitmap bitmap, bool translate) { editor.Close(); await Ocr(bitmap, translate); };
                editor.SpecialRecognitionRequested += async delegate(Bitmap bitmap, RecognitionKind kind) { editor.Close(); await SpecialOcr(bitmap, kind); };
                editor.ImageTranslateRequested += async delegate(Bitmap bitmap) { editor.Close(); await TranslateImage(bitmap); };
                editor.PinRequested += delegate(Bitmap bitmap)
                {
                    try { Pin(bitmap); editor.Close(); }
                    catch (Exception ex) { if (!exiting) Ui.Error(editor, ex); }
                };
                editor.ScrollRequested += async delegate { editor.Close(); await Capture(3, 0); };
                try
                {
                    if (!recordHistory)
                    {
                        historyEditor = editor;
                        editor.Disposed += delegate { if (historyEditor == editor) historyEditor = null; };
                        editor.EnableHistoryNavigation(ShowHistory);
                    }
                    EditorPlacement.Place(editor, !recordHistory ? (Form)historyWindow ?? settingsWindow : null);
                    Track(editor);
                }
                catch { editor.Dispose(); throw; }
                if (recordHistory)
                {
                    Bitmap pending = retained; retained = null;
                    QueueCaptureHistory(settings.Copy(), pending);
                }
            }
            catch (Exception ex) { if (!exiting) Ui.Error(main.Visible ? main : null, ex); }
            finally { if (image != null) image.Dispose(); if (retained != null) retained.Dispose(); }
            return Task.CompletedTask;
        }
        private void Pin(Bitmap image)
        {
            PinForm pin = null;
            try { pin = new PinForm(image, settings.PinTopmost); Track(pin); }
            catch { if (pin == null) image.Dispose(); throw; }
        }
        private async Task TranslateImage(Bitmap image, Point? sourceLocation = null)
        {
            if (exiting) { image.Dispose(); return; }
            var snapshot = settings.Copy();
            using (var cancellation = new CancellationTokenSource())
            using (var processing = (Bitmap)image.Clone())
            {
                var pin = new PinForm(image, snapshot.PinTopmost, sourceLocation: sourceLocation);
                FormClosedEventHandler closed = delegate { cancellation.Cancel(); };
                pin.FormClosed += closed;
                string stage = "配置检查";
                try
                {
                    Track(pin);
                    pin.SetStatus("正在识别图片…");
                    bool direct = snapshot.ImageTranslateProvider == "baidu_image";
                    if (direct) { RequireBaiduImage(snapshot); snapshot.TargetLanguage = snapshot.BaiduImageTargetLanguage; }
                    else { snapshot.RequireOcr(); snapshot.RequireTranslation(); }
                    stage = direct ? "图片翻译" : "识别";
                    var blocks = new List<OcrTextBlock>();
                    var directTranslations = new List<string>();
                    int characters = 0;
                    Rectangle[] regions = await Task.Run(delegate { return ScreenCapture.OcrRegions(processing).ToArray(); });
                    for (int index = 0; index < regions.Length; index++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        pin.SetStatus("正在" + stage + " " + (index + 1) + " / " + regions.Length + " 段…");
                        using (var tile = processing.Clone(regions[index], System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                        {
                            ImageTranslationResult part = await Task.Run(async delegate
                            {
                                byte[] encoded = direct ? EncodeBaiduImage(tile) : ScreenCapture.EncodeOcr(tile);
                                if (direct) return await api.BaiduImageTranslate(snapshot, encoded, cancellation.Token).ConfigureAwait(false);
                                return new ImageTranslationResult(await api.OcrWithLayout(snapshot, encoded, cancellation.Token).ConfigureAwait(false), null, null);
                            }, cancellation.Token);
                            var recognized = part.Blocks;
                            for (int blockIndex = 0; blockIndex < recognized.Length; blockIndex++)
                            {
                                var block = recognized[blockIndex];
                                Rectangle bounds = Rectangle.Intersect(new Rectangle(Point.Empty, tile.Size), block.Bounds);
                                if (bounds.Width < 3 || bounds.Height < 3) continue;
                                characters += block.Text.Length + (direct ? part.Translations[blockIndex].Length : 0);
                                if (characters > 2000000 || blocks.Count >= 10000) throw new InvalidOperationException("图片文字超过处理上限，请缩小截图区域。");
                                bounds.Offset(regions[index].Location);
                                blocks.Add(new OcrTextBlock(block.Text, bounds));
                                if (direct) directTranslations.Add(part.Translations[blockIndex]);
                            }
                        }
                        if (index + 1 < regions.Length) await Task.Delay(1050, cancellation.Token);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (blocks.Count == 0) { pin.SetStatus("未识别到带位置的文字，可右键保存原图。"); return; }
                    OcrTextBlock[] recognizedBlocks = blocks.ToArray();
                    pin.SetTextBlocks(recognizedBlocks);
                    stage = "翻译";
                    if (!direct) pin.SetStatus("OCR 完成，正在翻译…");
                    string[] translated = direct ? directTranslations.ToArray() : await Task.Run(() => api.TranslateBlocks(snapshot, recognizedBlocks.Select(block => block.Text).ToArray(), cancellation.Token), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    stage = "图片排版"; pin.SetStatus("翻译完成，正在绘制…");
                    ImageTextLayout[] layouts = null;
                    Bitmap rendered;
                    try
                    {
                        rendered = await Task.Run(delegate
                        {
                            layouts = ImageTranslation.Layout(processing, recognizedBlocks, translated, cancellation.Token);
                            return ImageTranslation.Render(processing, recognizedBlocks, translated, layouts, cancellation.Token);
                        }, cancellation.Token);
                    }
                    catch (InvalidOperationException)
                    {
                        if (!pin.IsDisposed && !exiting && !cancellation.IsCancellationRequested)
                            pin.SetTextBlocks(recognizedBlocks, translated, recognizedBlocks.Select(ImageTranslation.OriginalLayout).ToArray());
                        throw;
                    }
                    if (cancellation.IsCancellationRequested || pin.IsDisposed || exiting) { rendered.Dispose(); return; }
                    pin.SetTranslation(rendered);
                    pin.SetTextBlocks(recognizedBlocks, translated, layouts);
                    if (direct) { snapshot.OcrProvider = "baidu_image"; snapshot.TranslateProvider = "baidu_image"; }
                    string historyStatus = await RecordHistory(snapshot, "image_translation", String.Join(Environment.NewLine, recognizedBlocks.Select(block => block.Text)), String.Join(Environment.NewLine, translated), processing);
                    if (!pin.IsDisposed && !exiting) pin.SetStatus(historyStatus);
                }
                catch (OperationCanceledException) { if (!pin.IsDisposed && !exiting) pin.SetStatus("已取消"); }
                catch (Exception ex)
                {
                    if (!pin.IsDisposed && !exiting)
                        pin.SetStatus(stage + "未完成：" + (ex is InvalidOperationException ? ex.Message : ex.GetType().Name));
                }
                finally { pin.FormClosed -= closed; }
            }
        }
        private void ClipboardPin()
        {
            try
            {
                using (Image image = Clipboard.GetImage())
                {
                    if (image == null) throw new InvalidOperationException("剪贴板中没有图片，请先复制图片。");
                    Pin(new Bitmap(image));
                }
            }
            catch (Exception ex) { Ui.Error(main.Visible ? main : null, ex); }
        }
        private async void ClipboardOcr()
        {
            try
            {
                using (Image image = Clipboard.GetImage())
                {
                    if (image == null) throw new InvalidOperationException("剪贴板中没有图片，请先复制图片。");
                    await Ocr(new Bitmap(image), false);
                }
            }
            catch (Exception ex) { Ui.Error(main.Visible ? main : null, ex); }
        }
        private void Cancel() { if (job != null) job.Cancel(); }
        private void CancelOcr() { if (ocrJob != null) ocrJob.Cancel(); }
        private OcrForm OcrWindow()
        {
            if (ocrWindow == null)
            {
                ocrWindow = new OcrForm();
                ocrWindow.RefreshSettings(settings);
                ocrWindow.SettingsRequested += ShowSettings;
                ocrWindow.CancelRequested += CancelOcr;
                ocrWindow.TranslateRequested += async delegate(string text) { await Translate(text); };
            }
            return ocrWindow;
        }
        private CancellationTokenSource BeginJob(out int stamp)
        {
            Cancel();
            stamp = ++version;
            job = new CancellationTokenSource();
            main.Present(null, settings.TranslateNearCursor);
            main.Busy(true);
            return job;
        }
        private void EndJob(CancellationTokenSource cancellation, int stamp)
        {
            if (stamp == version) { main.Busy(false); job = null; }
            cancellation.Dispose();
        }
        private async Task Ocr(Bitmap image, bool translate)
        {
            if (exiting) { image.Dispose(); return; }
            int stamp;
            CancellationTokenSource cancellation;
            OcrForm window = null;
            if (translate) cancellation = BeginJob(out stamp);
            else
            {
                CancelOcr(); stamp = ++ocrVersion;
                cancellation = ocrJob = new CancellationTokenSource();
                window = OcrWindow(); window.Present(null, settings.TranslateNearCursor); window.Busy(true);
            }
            Action<string> setStatus = translate ? (Action<string>)main.SetStatus : window.SetStatus;
            Action<string> setSource = translate ? (Action<string>)main.SetSource : window.SetSource;
            Func<bool> current = delegate { return !exiting && stamp == (translate ? version : ocrVersion); };
            var watch = Stopwatch.StartNew();
            AppSettings snapshot = settings.Copy();
            setSource("");
            if (translate) main.SetActiveProvider(snapshot.TranslateProvider);
            setStatus("正在识别截图…");
            string stage = "识别";
            try
            {
                snapshot.RequireOcr();
                if (translate) snapshot.RequireTranslation();
                Rectangle[] regions = await Task.Run(delegate { return ScreenCapture.OcrRegions(image).ToArray(); });
                var lines = new List<string>();
                for (int index = 0; index < regions.Length; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!current()) return;
                    setStatus("正在识别 " + (index + 1) + " / " + regions.Length + " 段…");
                    using (var tile = image.Clone(regions[index], System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        string part = await Task.Run(async delegate
                        {
                            byte[] encoded = ScreenCapture.EncodeOcr(tile);
                            return await api.Ocr(snapshot, encoded, cancellation.Token).ConfigureAwait(false);
                        }, cancellation.Token);
                        if (!String.IsNullOrWhiteSpace(part)) lines.Add(part);
                    }
                    if (index + 1 < regions.Length) await Task.Delay(1050, cancellation.Token);
                }
                string text = String.Join(Environment.NewLine, lines);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!current()) return;
                setSource(text);
                if (String.IsNullOrWhiteSpace(text)) { setStatus("未识别到文字，耗时 " + watch.ElapsedMilliseconds + " ms"); return; }
                if (translate)
                {
                    stage = "翻译";
                    main.SetStatus("OCR 完成，正在翻译…");
                    string translatedStatus = await TranslateAndShow(snapshot, text, cancellation.Token, stamp, null, image);
                    if (current()) setStatus(translatedStatus);
                    return;
                }
                string historyStatus = await RecordHistory(snapshot, "ocr", text, "", image);
                if (current()) setStatus(historyStatus);
            }
            catch (OperationCanceledException) { if (current()) setStatus("请求已取消"); }
            catch (Exception ex) { if (current()) { setStatus(stage + "未完成"); Ui.Error(translate ? (Form)main : window, ex); } }
            finally
            {
                image.Dispose();
                if (translate && !exiting) EndJob(cancellation, stamp);
                else
                {
                    if (current()) { window.Busy(false); ocrJob = null; }
                    cancellation.Dispose();
                }
            }
        }
        private async Task Translate(string text, string provider = null)
        {
            if (exiting || String.IsNullOrWhiteSpace(text)) return;
            int stamp;
            var cancellation = BeginJob(out stamp);
            AppSettings snapshot = settings.Copy();
            main.SetSource(text);
            main.SetStatus("正在翻译…");
            try
            {
                string result = await TranslateAndShow(snapshot, text, cancellation.Token, stamp, provider);
                if (stamp == version && !exiting) main.SetStatus(result);
            }
            catch (OperationCanceledException) { if (stamp == version && !exiting) main.SetStatus("请求已取消"); }
            catch (Exception ex) { if (stamp == version && !exiting) { main.SetStatus("翻译未完成"); Ui.Error(main, ex); } }
            finally { if (!exiting) EndJob(cancellation, stamp); else cancellation.Dispose(); }
        }
        private async Task<string> TranslateAndShow(AppSettings snapshot, string text, CancellationToken cancellation, int stamp, string requested = null, Bitmap image = null)
        {
            string[] providers = requested == null ? snapshot.TranslationRequests() : new[] { requested };
            main.SetActiveProvider(requested ?? snapshot.TranslateProvider);
            foreach (string provider in providers) main.SetNotice(provider, "正在翻译…");
            var results = await Task.WhenAll(providers.Select(async provider =>
            {
                try
                {
                    AppSettings selected = snapshot.ForTranslation(provider);
                    string value = await Task.Run(() => api.Translate(selected, text, cancellation), cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (stamp != version || exiting) return "";
                    main.SetOutput(value, provider);
                    return await RecordHistory(selected, "translation", text, value, provider == snapshot.TranslateProvider ? image : null);
                }
                catch (OperationCanceledException)
                {
                    if (stamp == version && !exiting) main.SetNotice(provider, "请求已取消，单击重新翻译");
                    throw;
                }
                catch (Exception ex)
                {
                    if (stamp == version && !exiting)
                        main.SetNotice(provider, ex is InvalidOperationException ? ex.Message : "翻译未完成（" + ex.GetType().Name + "），单击重试");
                    return "部分翻译未完成，可点击对应模型重试";
                }
            }));
            cancellation.ThrowIfCancellationRequested();
            return String.Join("；", results.Where(value => value.Length > 0).Distinct());
        }
        private async Task SelectedTranslation()
        {
            if (reading || capturing || scroll != null || exiting) return;
            reading = true;
            try
            {
                string text;
                try
                {
                    IntPtr foreground = Native.GetForegroundWindow();
                    if (settingsWindow != null && foreground == settingsWindow.Handle) return;
                    text = await reader.TryReadExplicit(foreground);
                }
                finally { reading = false; }
                if (String.IsNullOrWhiteSpace(text)) return;
                await Translate(text);
            }
            catch (Exception ex) { if (!exiting) Ui.Error(main.Visible ? main : null, ex); }
        }
        private void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (historyCleanupTimer != null) historyCleanupTimer.Stop();
            if (clipboardHistory != null) { clipboardHistory.Dispose(); clipboardHistory.FlushAsync().GetAwaiter().GetResult(); }
            try { historyCleanup.GetAwaiter().GetResult(); } catch (Exception) { } // A locked history file must not prevent shutdown.
            languageSaveTimer.Stop();
            if (languageSavePending || !languageWrite.IsCompleted || languageWrite.IsFaulted)
                try { SaveSettings(settings); }
                catch (Exception ex) { Ui.Error(main.Visible ? main : null, ex); }
            Cancel();
            CancelOcr();
            if (settingsWindow != null) settingsWindow.Close();
            if (historyWindow != null) historyWindow.Close();
            Task.WaitAll(historyWrites.ToArray());
            historyWrites.Clear();
            try { history.FlushAsync().GetAwaiter().GetResult(); } catch (Exception) { } // Preserve the original file on an unreadable store and allow exit.
            foreach (var window in imageWindows.ToArray()) window.Close();
            if (scroll != null) scroll.Close();
            if (recorder != null) recorder.Close();
            tray.Visible = false;
            main.Exiting = true;
            main.Close();
            if (ocrWindow != null) { ocrWindow.Exiting = true; ocrWindow.Close(); }
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                if (!exiting) Exit();
                Unregister();
                commands.Dispose();
                languageSaveTimer.Dispose();
                if (historyCleanupTimer != null) historyCleanupTimer.Dispose();
                tray.ContextMenuStrip.Dispose(); tray.Dispose();
                main.Dispose(); if (ocrWindow != null) ocrWindow.Dispose(); api.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

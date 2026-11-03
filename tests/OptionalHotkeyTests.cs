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
    internal static class OptionalHotkeyTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check) { RunSettings(check); RunApplication(check); }
        internal static void RunSettings(Action<bool, string> check)
        {
            var defaults = new AppSettings();
            check(defaults.Hotkeys.Length == 9 && defaults.Hotkeys[0] == "Alt+1" && defaults.Hotkeys[3] == "Ctrl+Shift+A"
                && defaults.Hotkeys[7] == "Ctrl+Shift+S" && new[] { 4, 5, 6, 8 }.All(index => String.IsNullOrEmpty(defaults.Hotkeys[index])), "history shortcut defaults to Ctrl+Shift+S while other optional commands stay unassigned");
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-hotkeys-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            try
            {
                var legacy = new AppSettings { Hotkeys = new[] { "Ctrl+Alt+F13", "", "Ctrl+Alt+F14", "Ctrl+Alt+F15" },
                    TencentId = "synthetic-preserved-id", HistoryEnabled = false, TranslateWindowWidth = 397, CaptureStroke = 7 };
                SettingsStore.Save(legacy, path);
                var migrated = SettingsStore.Load(path);
                check(migrated.Hotkeys.Length == 9 && migrated.Hotkeys.Take(4).SequenceEqual(legacy.Hotkeys)
                    && migrated.Hotkeys.Skip(4).All(String.IsNullOrEmpty), "loading four legacy shortcuts appends five blank entries without changing existing shortcuts");
                check(migrated.TencentId == legacy.TencentId && !migrated.HistoryEnabled && migrated.TranslateWindowWidth == 397 && migrated.CaptureStroke == 7,
                    "shortcut migration preserves unrelated settings");
                if (migrated.Hotkeys.Length == 9)
                {
                    migrated.Hotkeys[8] = "Ctrl+Alt+F16";
                    SettingsStore.Save(migrated, path);
                    check(SettingsStore.Load(path).Hotkeys.SequenceEqual(migrated.Hotkeys), "all nine shortcuts survive a settings round trip");
                    var copy = migrated.Copy(); copy.Hotkeys[8] = "";
                    check(migrated.Hotkeys[8] == "Ctrl+Alt+F16", "draft shortcut edits do not mutate the current configuration");
                    migrated.Hotkeys[8] = migrated.Hotkeys[0];
                    bool rejected = false;
                    try { Hotkey.Validate(migrated.Hotkeys); } catch (FormatException) { rejected = true; }
                    check(rejected, "optional shortcuts cannot duplicate the original commands");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static void RunApplication(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-commands-" + Guid.NewGuid().ToString("N"));
            var saved = new List<AppSettings>();
            bool failSave = false;
            try
            {
                using (var blocker = new CommandWindow("OptionalHotkeyTest." + Guid.NewGuid().ToString("N")))
                using (var clipboard = ClipboardSnapshot.Capture())
                using (var handler = new OcrHandler())
                {
                    int[] keys = ReserveKeys(blocker);
                    uint clipboardSequence = 0;
                    try
                    {
                        var initial = new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false,
                            OcrProvider = "tencent", TencentId = "synthetic", TencentSecret = "synthetic", TranslateNearCursor = false, PinTopmost = false };
                        using (var app = new DesktopApp(initial, true, persistSettings: value =>
                        {
                            if (failSave) throw new IOException("Synthetic write failure");
                            saved.Add(value.Copy());
                        }))
                        {
                            typeof(DesktopApp).GetField("history", Private).SetValue(app, new HistoryStore(directory));
                            Field<CloudApi>(app, "api").Dispose();
                            typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                            var main = Field<MainForm>(app, "main");
                            Exception failure = null;
                            main.BeginInvoke((Action)(async delegate
                            {
                                try
                                {
                                    Registration(app, blocker, keys, check, () => failSave = true, () => failSave = false);
                                    var windows = Field<List<Form>>(app, "imageWindows");
                                    using (var image = new Bitmap(120, 80))
                                    {
                                        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
                                        await Clip.SetImage(image); clipboardSequence = Native.GetClipboardSequenceNumber();
                                    }
                                    Command(app, 6);
                                    var first = windows.OfType<PinForm>().Single();
                                    check(first.Visible && first.ClientSize.Width > 0, "the clipboard-pin command opens an owned image without changing the clipboard");
                                    Command(app, 5);
                                    while (Field<CancellationTokenSource>(app, "ocrJob") != null) await Task.Delay(10);
                                    check(handler.Calls == 1 && Field<RichTextBox>(Field<OcrForm>(app, "ocrWindow"), "source").Text == "synthetic clipboard OCR",
                                        "the clipboard-OCR command reaches the selected provider and presents its result");

                                    var preHidden = new PinForm(new Bitmap(40, 30), false);
                                    Invoke(app, "Track", preHidden); preHidden.Hide();
                                    await OpenEditor(app);
                                    var editor = windows.OfType<EditorForm>().Single();
                                    Command(app, 8);
                                    check(!first.Visible && !preHidden.Visible && editor.Visible && !first.IsDisposed, "hide-all targets visible pins and leaves editors and pre-hidden pins unchanged");
                                    Command(app, 6);
                                    var newer = windows.OfType<PinForm>().Single(pin => pin != first && pin != preHidden);
                                    Command(app, 8);
                                    check(first.Visible && newer.Visible && !preHidden.Visible, "restore brings back only the pins hidden by the command and preserves a new visible pin");
                                    Command(app, 8); first.Close();
                                    Command(app, 8);
                                    check(first.IsDisposed && !windows.Contains(first) && newer.Visible && !preHidden.Visible && Field<HashSet<PinForm>>(app, "hiddenPins").Count == 0,
                                        "closing a hidden pin removes it from both tracking collections and restore never revives it");
                                    newer.Close(); preHidden.Close(); editor.Close();
                                    var disposed = new PinForm(new Bitmap(40, 30), false);
                                    Invoke(app, "Track", disposed); Command(app, 8); disposed.Dispose();
                                    check(!windows.Contains(disposed) && Field<HashSet<PinForm>>(app, "hiddenPins").Count == 0,
                                        "direct disposal also releases a hidden pin from every tracking collection");
                                    Command(app, 8);
                                    check(Field<HashSet<PinForm>>(app, "hiddenPins").Count == 0, "toggle with no existing pins has no retained state");

                                    Command(app, 7);
                                    var historyWindow = Field<HistoryForm>(app, "historyWindow");
                                    check(historyWindow.Visible && Field<SettingsForm>(app, "settingsWindow") == null, "the history command opens its independent window without settings");
                                    Invoke(app, "ShowSettings");
                                    var options = Field<SettingsForm>(app, "settingsWindow");
                                    check(options.Visible && historyWindow.Visible, "settings and the history window remain independently usable");
                                    var fields = Field<Dictionary<string, TextBox>>(options, "fields");
                                    check(Enumerable.Range(0, 9).All(index => fields.ContainsKey("hotkey" + index)), "settings exposes all original and optional shortcut fields");
                                    await Styles(app, options, saved, check, () => failSave = true, () => failSave = false);
                                }
                                catch (Exception error) { failure = error; }
                                finally { failSave = false; Invoke(app, "Exit"); }
                            }));
                            Application.Run(app);
                            if (failure != null) throw failure;
                        }
                        foreach (int key in keys.Take(5))
                        {
                            bool available = Native.RegisterHotKey(blocker.Handle, key, 7, (uint)key);
                            check(available, "disposing the application releases optional key " + (Keys)key);
                            if (available) Native.UnregisterHotKey(blocker.Handle, key);
                        }
                    }
                    finally
                    {
                        foreach (int key in keys) Native.UnregisterHotKey(blocker.Handle, key);
                        if (clipboardSequence != 0 && Native.GetClipboardSequenceNumber() == clipboardSequence)
                        { if (clipboard.Data != null) Clipboard.SetDataObject(clipboard.Data, true); else Clipboard.Clear(); }
                    }
                }
            }
            finally { Ui.RenderOffscreen = previous; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static int[] ReserveKeys(CommandWindow window)
        {
            var keys = new List<int>();
            for (int key = (int)Keys.F13; key <= (int)Keys.F24 && keys.Count < 6; key++)
                if (Native.RegisterHotKey(window.Handle, key, 7, (uint)key)) keys.Add(key);
            if (keys.Count < 6) { foreach (int key in keys) Native.UnregisterHotKey(window.Handle, key); throw new InvalidOperationException("Insufficient unassigned synthetic hotkeys."); }
            foreach (int key in keys.Take(5)) Native.UnregisterHotKey(window.Handle, key);
            return keys.ToArray();
        }
        private static void Registration(DesktopApp app, CommandWindow blocker, int[] keys, Action<bool, string> check, Action fail, Action recover)
        {
            var enabled = Field<AppSettings>(app, "settings").Copy();
            for (int index = 0; index < 5; index++) enabled.Hotkeys[index + 4] = "Ctrl+Alt+Shift+" + (Keys)keys[index];
            Invoke(app, "ApplySettings", enabled);
            check(keys.Take(5).All(key => IsHeld(blocker, key)), "all five optional shortcuts register independently");
            var conflict = enabled.Copy(); conflict.Hotkeys[8] = "Ctrl+Alt+Shift+" + (Keys)keys[5];
            bool rejected = false;
            try { Invoke(app, "ApplySettings", conflict); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && Field<AppSettings>(app, "settings").Hotkeys.SequenceEqual(enabled.Hotkeys) && keys.Take(5).All(key => IsHeld(blocker, key)),
                "an externally occupied optional shortcut rolls back partial registrations and preserves every previous binding");
            var blank = enabled.Copy(); for (int index = 4; index < 9; index++) blank.Hotkeys[index] = "";
            fail(); rejected = false;
            try { Invoke(app, "ApplySettings", blank); } catch (IOException) { rejected = true; }
            finally { recover(); }
            check(rejected && keys.Take(5).All(key => IsHeld(blocker, key)), "a settings-write failure restores optional shortcut registrations");
            Invoke(app, "ApplySettings", blank);
            check(keys.Take(5).All(key => !IsHeld(blocker, key)), "clearing optional shortcuts unregisters their global bindings");
            Invoke(app, "ApplySettings", enabled);
        }
        private static bool IsHeld(CommandWindow window, int key)
        {
            bool registered = Native.RegisterHotKey(window.Handle, key, 7, (uint)key);
            if (registered) Native.UnregisterHotKey(window.Handle, key);
            return !registered;
        }
        private static async Task Styles(DesktopApp app, SettingsForm options, List<AppSettings> saved, Action<bool, string> check, Action fail, Action recover)
        {
            var windows = Field<List<Form>>(app, "imageWindows");
            await OpenEditor(app); var older = windows.OfType<EditorForm>().Single();
            await OpenEditor(app); var newer = windows.OfType<EditorForm>().Single(window => window != older);
            check(!older.IsDisposed && older.Visible && newer.Visible && windows.OfType<EditorForm>().Count() == 2,
                "independent image editors remain open for concurrent annotation-style changes");
            var controls = Field<AnnotationOptions>(newer, "styles");
            Field<NumericUpDown>(controls, "stroke").Value = 7;
            Field<NumericUpDown>(controls, "size").Value = 35;
            Invoke(controls, "SetColor", Color.Blue);
            newer.Close(); int count = saved.Count; older.Close();
            var current = Field<AppSettings>(app, "settings");
            check(saved.Count == count && current.CaptureStroke == 7 && current.CaptureTextSize == 35 && current.CaptureColorArgb == Color.Blue.ToArgb(),
                "closing an unchanged older editor preserves the style saved by a newer editor without another write");
            Invoke(options, "Save"); Invoke(options, "Save");
            current = Field<AppSettings>(app, "settings");
            check(current.CaptureStroke == 7 && Field<ComboBox>(options, "stroke").SelectedIndex == 6 && current.CaptureTextSize == 35,
                "repeated saves from an already-open settings draft preserve live annotation styles and display exact custom stroke widths");
            Field<ComboBox>(options, "stroke").SelectedIndex = 3; Invoke(options, "Save");
            check(Field<AppSettings>(app, "settings").CaptureStroke == 4, "an explicitly edited settings stroke still overrides the live style");
            await OpenEditor(app); older = windows.OfType<EditorForm>().Single();
            await OpenEditor(app); newer = windows.OfType<EditorForm>().Single(window => window != older);
            Field<NumericUpDown>(Field<AnnotationOptions>(newer, "styles"), "stroke").Value = 9; newer.Close();
            Field<NumericUpDown>(Field<AnnotationOptions>(older, "styles"), "size").Value = 42; older.Close();
            current = Field<AppSettings>(app, "settings");
            check(current.CaptureStroke == 9 && current.CaptureTextSize == 42, "concurrent editors merge only their changed style fields");
            await OpenEditor(app); var failed = windows.OfType<EditorForm>().Single();
            Field<NumericUpDown>(Field<AnnotationOptions>(failed, "styles"), "stroke").Value = 3;
            fail(); failed.Close(); recover();
            check(failed.IsDisposed && !windows.Contains(failed) && Field<AppSettings>(app, "settings").CaptureStroke == 3
                && Field<Label>(Field<MainForm>(app, "main"), "status").Text.Contains("标注样式未保存"),
                "style persistence failure closes and releases the editor while retaining its session style and reporting inline status");
            Invoke(options, "Save");
            check(saved.Last().CaptureStroke == 3, "an explicit settings save retries a previously failed style write");
        }
        private static Task OpenEditor(DesktopApp app) { return (Task)Invoke(app, "OpenEditor", new Bitmap(120, 80), true); }
        private static void Command(DesktopApp app, int id) { Native.SendMessage(Field<CommandWindow>(app, "commands").Handle, 0x312, new IntPtr(id), IntPtr.Zero); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static object Invoke(object owner, string name, params object[] args)
        {
            try { return owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private sealed class OcrHandler : HttpMessageHandler
        {
            internal int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Response\":{\"TextDetections\":[{\"DetectedText\":\"synthetic clipboard OCR\"}]}}") });
            }
        }
    }
}

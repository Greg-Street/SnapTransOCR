using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class HistoryStandaloneTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-standalone-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                store.AddAsync(new HistoryEntry { Kind = "translation", Source = "A useful tool stays out of your way.", Translation = "好用的工具，让操作更顺手。" }).GetAwaiter().GetResult();
                using (var image = new Bitmap(640, 360))
                {
                    using (var graphics = Graphics.FromImage(image))
                    {
                        graphics.Clear(Color.FromArgb(235, 242, 255));
                        using (var font = new Font("Microsoft YaHei UI", 24F)) graphics.DrawString("截图 · 标注 · OCR", font, Brushes.RoyalBlue, 100, 150);
                    }
                    store.AddAsync(new HistoryEntry { Kind = "capture" }, image).GetAwaiter().GetResult();
                }
                store.AddAsync(new HistoryEntry { Kind = "ocr", Source = "识别后的文字可在这里查找、选取和复制。" }).GetAwaiter().GetResult();
                store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "needle\r\n复制的文字和图片，随时找回来。\r\n\r\nCtrl+Shift+S 打开历史与剪贴板。" }).GetAwaiter().GetResult();
                using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" } }, true, persistSettings: delegate { }))
                {
                    typeof(DesktopApp).GetField("history", Private).SetValue(app, store);
                    Exception failure = null;
                    Field<MainForm>(app, "main").BeginInvoke((Action)(async delegate
                    {
                        try { await Exercise(app, store, check); }
                        catch (Exception ex) { failure = ex; }
                        finally { Invoke(app, "Exit"); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
                Layouts(store, check);
            }
            finally
            {
                Ui.RenderOffscreen = offscreen;
                store.FlushAsync().GetAwaiter().GetResult();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static async Task Exercise(DesktopApp app, HistoryStore store, Action<bool, string> check)
        {
            Hotkey(app);
            var window = Field<HistoryForm>(app, "historyWindow"); var view = window.View;
            window.RefreshShortcut("Ctrl+Shift+S");
            await Ready(view);
            var list = Field<ListBox>(view, "list"); var search = Field<TextBox>(view, "search");
            var filters = Field<HistoryButton[]>(view, "filterButtons"); var preview = Field<PictureBox>(view, "preview");
            check(window.Visible && window.Enabled && Field<SettingsForm>(app, "settingsWindow") == null && list.Items.Count == 4,
                "history hotkey opens its own nonmodal window and leaves settings unopened");
            check(filters.Length == 7 && filters.Select(button => button.Text).SequenceEqual(new[] { "全部", "截图", "OCR", "翻译", "剪贴板", "文字", "图片" }) && !view.Controls.OfType<ComboBox>().Any(),
                "all seven history filters are individual keyboard-focusable buttons without a dropdown");
            Snapshot(window, "history-standalone-text.png");
            filters[1].PerformClick(); await Ready(view);
            check(list.Items.Count == 1 && ((HistoryEntry)list.SelectedItem).Kind == "capture" && filters.Count(button => button.Selected) == 1,
                "one click switches the screenshot filter and its exclusive selected style");
            check(Field<Button>(view, "openImage").Visible && preview.Image != null, "image history exposes an explicit edit action alongside its full-image copy action");
            Snapshot(window, "history-standalone-image.png");
            filters[0].PerformClick(); search.Text = "needle"; await Ready(view);
            string id = ((HistoryEntry)list.SelectedItem).Id;
            Hotkey(app); await Ready(view);
            check(ReferenceEquals(Field<HistoryForm>(app, "historyWindow"), window) && search.Text == "needle" && ((HistoryEntry)list.SelectedItem).Id == id,
                "repeated history hotkeys reuse the window and preserve its query and selected record");
            Field<GlyphButton>(window, "pin").PerformClick();
            check(window.TopMost && Field<GlyphButton>(window, "pin").Selected && Field<GlyphButton>(window, "pin").AccessibleName.Contains("取消"),
                "pin toggles native topmost state and its icon and accessible hint together");
            list.Focus(); Key(window, Keys.Control | Keys.F);
            check(search.Focused && search.SelectionLength == search.TextLength, "Ctrl+F focuses and selects the current search text");
            Field<GlyphButton>(Field<TextInputField>(view, "searchField"), "clear").PerformClick();
            check(search.TextLength == 0 && list.Items.Count == 4, "the search clear icon restores the cached records without changing the category");
            search.Text = "needle";
            var previousView = view;
            check(Key(window, Keys.Escape) && window.IsDisposed && view.IsDisposed && Field<HistoryForm>(app, "historyWindow") == null,
                "Esc closes and disposes the independent window even when its search is focused and nonempty");
            Hotkey(app); window = Field<HistoryForm>(app, "historyWindow"); view = window.View; await Ready(view);
            check(window.TopMost && !ReferenceEquals(view, previousView) && (await store.LoadAsync()).Length == 4,
                "reopening history keeps the session pin preference and never deletes or duplicates records");
            Field<GlyphButton>(window, "pin").PerformClick();
            check(!window.TopMost && !Field<GlyphButton>(window, "pin").Selected, "the history pin can also be switched off");
            Field<GlyphButton>(window, "settings").PerformClick();
            var settings = Field<SettingsForm>(app, "settingsWindow");
            check(settings != null && settings.Visible && window.Visible && window.Enabled, "opening settings from history leaves both nonmodal windows usable");
            Field<ToggleSwitch>(view, "recording").Checked = false;
            check(!Field<AppSettings>(app, "settings").HistoryEnabled && !settings.Value.HistoryEnabled, "pausing records in the independent window synchronizes the live settings");
            Field<Dictionary<string, ToggleSwitch>>(settings, "toggles")["HistoryEnabled"].Checked = true;
            check(Field<ToggleSwitch>(view, "recording").Checked && Field<AppSettings>(app, "settings").HistoryEnabled, "resuming records in settings updates the standalone switch without feedback loops");
            settings.SelectPage("历史记录"); Field<GlyphButton>(settings, "openHistory").PerformClick();
            check(ReferenceEquals(window, Field<HistoryForm>(app, "historyWindow")), "the settings history popout button reuses the independent history window");
            var gate = Field<SemaphoreSlim>(store, "gate"); await gate.WaitAsync();
            try
            {
                Field<HistoryButton[]>(view, "filterButtons")[6].PerformClick();
                check(Field<CancellationTokenSource>(view, "previewRequest") != null, "an image filter starts a cancellable asynchronous preview");
                Key(window, Keys.Escape);
            }
            finally { gate.Release(); }
            await store.FlushAsync(); await Task.Delay(30);
            check(window.IsDisposed && Field<PictureBox>(view, "preview").Image == null && Field<HistoryEntry[]>(view, "entries").Length == 0,
                "closing during a pending thumbnail load releases images and cached records without a late UI update");
            Hotkey(app); window = Field<HistoryForm>(app, "historyWindow"); await Ready(window.View);
            Invoke(app, "Exit");
            check(window.IsDisposed && settings.IsDisposed, "application exit also disposes the independent history and settings windows");
        }
        private static void Layouts(HistoryStore store, Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            foreach (int width in new[] { 740, 900, 1100 })
            {
                var fonts = new List<Font>();
                try
                {
                    using (var window = new HistoryForm(store, true, false, "Ctrl+Shift+S"))
                    {
                        SettingsLayoutTests.SimulateDpi(window, dpi, fonts);
                        window.ClientSize = new Size(Ui.Scale(window, width), Ui.Scale(window, 480)); window.PerformLayout();
                        var view = window.View; var buttons = Field<HistoryButton[]>(view, "filterButtons");
                        var title = Field<Label>(window, "title"); var shortcut = Field<Label>(window, "shortcut"); var pin = Field<GlyphButton>(window, "pin");
                        string context = width + "px at " + dpi + " DPI";
                        check(window.ClientRectangle.Contains(view.Bounds) && view.Top > title.Bottom && shortcut.Right < pin.Left, "standalone history header and body fit the resized window: " + context);
                        check(buttons.All(button => view.ClientRectangle.Contains(button.Bounds)) && buttons.Zip(buttons.Skip(1), (a, b) => a.Right < b.Left || a.Bottom < b.Top).All(value => value), "independent filter buttons do not overlap at the minimum window size: " + context);
                    }
                }
                finally { foreach (Font font in fonts) font.Dispose(); }
            }
        }
        private static void Snapshot(Form form, string name)
        {
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", name))); }
        }
        private static bool Key(Form form, Keys key)
        { return (bool)typeof(HistoryForm).GetMethod("ProcessCmdKey", Private).Invoke(form, new object[] { new Message(), key }); }
        private static void Hotkey(DesktopApp app) { Native.SendMessage(Field<CommandWindow>(app, "commands").Handle, 0x312, new IntPtr(7), IntPtr.Zero); }
        private static async Task Ready(HistoryView view)
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(view, "busy") || Field<CancellationTokenSource>(view, "previewRequest") != null)
            { if (DateTime.UtcNow > limit) throw new InvalidOperationException("Standalone history did not finish loading."); await Task.Delay(10); }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static object Invoke(object owner, string name) { return owner.GetType().GetMethod(name, Private).Invoke(owner, null); }
    }
}

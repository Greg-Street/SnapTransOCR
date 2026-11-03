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
    internal static class HistoryFilterTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-filter-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                Add(store, "capture", null, null, true).GetAwaiter().GetResult();
                Add(store, "ocr", "Alpha invoice", "", true).GetAwaiter().GetResult();
                Add(store, "translation", "Mixed Case ALPHA", "花园", false).GetAwaiter().GetResult();
                Add(store, "image_translation", "garden text", "原位译文", true).GetAwaiter().GetResult();
                Add(store, "translation", "screenshot sample", "dELTa", true).GetAwaiter().GetResult();
                Add(store, "translation", null, "Only translated BRAVO", false).GetAwaiter().GetResult();
                Exception failure = null;
                using (var form = new Form { ClientSize = new Size(760, 500), StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                using (var view = new HistoryView(store, true) { Dock = DockStyle.Fill })
                {
                    form.Controls.Add(view);
                    form.Shown += async delegate
                    {
                        try
                        {
                            await view.RefreshAsync(); await Ready(view);
                            var list = Field<ListBox>(view, "list"); var search = Field<TextBox>(view, "search"); var type = Field<HistoryButton[]>(view, "filterButtons");
                            var preview = Field<PictureBox>(view, "preview"); var status = Field<Label>(view, "status");
                            check(list.Items.Count == 6 && view.FilterIndex == 0 && search.Text.Length == 0, "history initially presents all loaded records without a filter");
                            Select(list, item => item.Kind == "ocr"); await Ready(view);
                            string selectedId = ((HistoryEntry)list.SelectedItem).Id;
                            Bitmap originalPreview = (Bitmap)preview.Image;
                            var gate = Field<SemaphoreSlim>(store, "gate");
                            await gate.WaitAsync();
                            try
                            {
                                Type(search, "aLpHa");
                                check(list.Items.Count == 2 && ((HistoryEntry)list.SelectedItem).Id == selectedId && ReferenceEquals(preview.Image, originalPreview), "typing filters cached original text case-insensitively and preserves the selected record and thumbnail without disk access");
                                Type(search, "");
                                check(list.Items.Count == 6 && ((HistoryEntry)list.SelectedItem).Id == selectedId, "clearing the query restores cached records immediately while storage is busy");
                            }
                            finally { gate.Release(); }
                            Choose(type, 3);
                            check(list.Items.Count == 4 && list.Items.Cast<HistoryEntry>().All(item => item.Kind == "translation" || item.Kind == "image_translation"), "keyboard type selection groups text, screenshot and image translations together");
                            Type(search, "delta"); await Ready(view);
                            check(list.Items.Count == 1 && Field<RichTextBox>(view, "translation").Text == "dELTa" && status.Text.Contains("1 / 6"), "query and type combine and search the complete translated text case-insensitively");
                            Bitmap filteredPreview = (Bitmap)preview.Image;
                            string filteredId = ((HistoryEntry)list.SelectedItem).Id;
                            Type(search, "no matching text");
                            check(list.Items.Count == 0 && Field<Label>(view, "empty").Text == "没有匹配记录" && status.Text.Contains("0 / 6"), "an unmatched query reports zero matches separately from empty history");
                            check(preview.Image == null && Disposed(filteredPreview) && Field<RichTextBox>(view, "source").TextLength == 0 && Field<RichTextBox>(view, "translation").TextLength == 0
                                && !Field<Button>(view, "remove").Enabled && !Field<GlyphButton>(view, "copySource").Enabled && !Field<GlyphButton>(view, "copyTranslation").Enabled,
                                "filtering to no records clears details, disables their actions and releases the previous bitmap");
                            Type(search, "delta"); await Ready(view);
                            check(((HistoryEntry)list.SelectedItem).Id == filteredId && preview.Image != null && !ReferenceEquals(preview.Image, filteredPreview), "restoring a matching query restores its record identity with a newly owned preview");
                            await gate.WaitAsync();
                            try
                            {
                                Type(search, "garden");
                                check(Field<CancellationTokenSource>(view, "previewRequest") != null && preview.Image == null, "a filter change clears stale pixels before loading the new selected thumbnail");
                                Type(search, "nothing found");
                                check(Field<CancellationTokenSource>(view, "previewRequest") == null && preview.Image == null, "a zero-match filter cancels its pending thumbnail request");
                            }
                            finally { gate.Release(); }
                            await store.FlushAsync(); await Task.Yield();
                            check(preview.Image == null && list.Items.Count == 0, "a late cancelled thumbnail cannot replace the empty filtered view");
                            Type(search, "BRAVO");
                            check(list.Items.Count == 1 && ((HistoryEntry)list.SelectedItem).Source == "", "translation-only records are searchable when their original text is absent");
                            Type(search, ""); Choose(type, 1); await Ready(view);
                            check(list.Items.Count == 1 && ((HistoryEntry)list.SelectedItem).Kind == "capture" && preview.Image != null, "the screenshot filter keeps image-only entries selectable");
                            Choose(type, 2); await Ready(view);
                            check(list.Items.Count == 1 && Field<RichTextBox>(view, "source").Text == "Alpha invoice", "the OCR filter shows recognition records independently of translations");
                            Choose(type, 3); Type(search, "delta"); await Ready(view);
                            Bitmap removedPreview = (Bitmap)preview.Image;
                            Field<Button>(view, "remove").PerformClick(); await Ready(view);
                            check(list.Items.Count == 0 && search.Text == "delta" && view.FilterIndex == 3 && status.Text.Contains("0 / 5") && Disposed(removedPreview), "deleting the only match preserves filter controls, updates the loaded total and releases its thumbnail");
                            await Add(store, "translation", "new record", "DELTA replacement", false);
                            await view.RefreshAsync(); await Ready(view);
                            check(list.Items.Count == 1 && Field<RichTextBox>(view, "translation").Text == "DELTA replacement" && status.Text.Contains("1 / 6"), "refreshing new history reapplies the active query and type to the refreshed cache");
                            Field<Button>(view, "clear").PerformClick(); await Ready(view);
                            check(list.Items.Count == 0 && (await store.LoadAsync()).Length == 0 && status.Text == "暂无历史记录" && Field<Label>(view, "empty").Text == "暂无历史记录"
                                && search.Text == "delta" && view.FilterIndex == 3, "clear all removes unfiltered records too and retains the user's current filter controls");
                            await Add(store, "capture", "", "", true);
                            Type(search, ""); Choose(type, 0); await view.RefreshAsync(); await Ready(view);
                            Bitmap finalPreview = (Bitmap)preview.Image;
                            view.Dispose();
                            check(Disposed(finalPreview) && Field<HistoryEntry[]>(view, "entries").Length == 0, "disposing history releases both the filtered cache and its selected thumbnail");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                if (failure != null) throw failure;
                Layouts(store, check);
            }
            finally
            {
                Ui.RenderOffscreen = offscreen;
                store.FlushAsync().GetAwaiter().GetResult();
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        }
        private static void Layouts(HistoryStore store, Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            foreach (int width in new[] { 860, 1100 })
            {
                var fonts = new List<Font>();
                try
                {
                    using (var form = new SettingsForm(new AppSettings(), delegate { }, store))
                    {
                        SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                        form.ClientSize = new Size(Ui.Scale(form, width), Ui.Scale(form, 580));
                        form.SelectPage("历史记录"); form.PerformLayout();
                        var view = Field<HistoryView>(form, "historyView");
                        var field = Field<TextInputField>(view, "searchField"); var search = Field<TextBox>(view, "search"); var type = Field<HistoryButton[]>(view, "filterButtons");
                        var toggle = Field<ToggleSwitch>(view, "recording"); var clear = Field<Button>(view, "clear"); var list = Field<RoundedPanel>(view, "listPanel");
                        string context = width + "px at " + dpi + " DPI";
                        check(view.ClientRectangle.Contains(field.Bounds) && type.All(button => view.ClientRectangle.Contains(button.Bounds)) && field.ClientRectangle.Contains(search.Bounds)
                            && field.Right < toggle.Left && toggle.Right < clear.Left, "history search, recording and clear actions fit their toolbar without overlap: " + context);
                        check(field.Bottom < type.Min(button => button.Top) && type.Max(button => button.Bottom) < list.Top && list.Bottom < view.Height,
                            "separate category buttons stay between the search row and history content: " + context);
                        check(search.Width >= Ui.Scale(view, 120) && type.Zip(type.Skip(1), (a, b) => a.Right < b.Left || a.Bottom < b.Top).All(value => value), "narrow high-DPI history retains readable search and nonoverlapping category buttons: " + context);
                        check(type.All(button => TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding).Width <= button.Width - Ui.Scale(button, 28)),
                            "all seven category labels fit beside their own icons: " + context);
                    }
                }
                finally { foreach (Font font in fonts) font.Dispose(); }
            }
        }
        private static async Task Add(HistoryStore store, string kind, string source, string translation, bool screenshot)
        {
            using (var image = screenshot ? new Bitmap(80, 50) : null)
            {
                if (image != null) using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue);
                await store.AddAsync(new HistoryEntry { Kind = kind, Source = source, Translation = translation }, image);
            }
        }
        private static void Type(TextBox input, string text)
        {
            input.Focus(); input.SelectAll();
            if (text.Length == 0) Native.SendMessage(input.Handle, 0x102, new IntPtr(8), IntPtr.Zero);
            else foreach (char value in text) Native.SendMessage(input.Handle, 0x102, new IntPtr(value), IntPtr.Zero);
        }
        private static void Choose(HistoryButton[] buttons, int index)
        {
            var button = buttons[index]; button.Focus();
            Native.SendMessage(button.Handle, 0x100, new IntPtr((int)Keys.Space), IntPtr.Zero);
            Native.SendMessage(button.Handle, 0x101, new IntPtr((int)Keys.Space), IntPtr.Zero);
            if (!button.Selected) throw new InvalidOperationException("The category button did not respond to keyboard navigation.");
        }
        private static void Select(ListBox list, Func<HistoryEntry, bool> predicate) { list.SelectedIndex = list.Items.Cast<HistoryEntry>().Select((item, index) => new { item, index }).Single(pair => predicate(pair.item)).index; }
        private static async Task Ready(HistoryView view) { while (Field<bool>(view, "busy") || Field<CancellationTokenSource>(view, "previewRequest") != null) await Task.Delay(1); }
        private static bool Disposed(Bitmap image) { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

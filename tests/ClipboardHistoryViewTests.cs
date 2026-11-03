using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ClipboardHistoryViewTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            var text = new HistoryEntry { Kind = "clipboard_text", Source = "Clipboard ALPHA" };
            var image = new HistoryEntry { Kind = "clipboard_image", ImageFile = "image.png" };
            var capture = new HistoryEntry { Kind = "capture", ImageFile = "capture.png" };
            var ocr = new HistoryEntry { Kind = "ocr", Source = "识别结果", ImageFile = "ocr.png" };
            var translated = new HistoryEntry { Kind = "image_translation", Source = "source", Translation = "翻译内容", ImageFile = "translation.png" };
            check(HistoryView.MatchesFilter(text, 4, "") && HistoryView.MatchesFilter(image, 4, "") && !HistoryView.MatchesFilter(capture, 4, ""), "clipboard filter includes text and image clips without including app captures");
            check(!HistoryView.MatchesFilter(text, 3, "") && !HistoryView.MatchesFilter(image, 3, "") && HistoryView.MatchesFilter(translated, 3, ""), "translation filter excludes new clipboard kinds");
            check(HistoryView.MatchesFilter(text, 5, "") && HistoryView.MatchesFilter(ocr, 5, "") && HistoryView.MatchesFilter(translated, 5, "") && !HistoryView.MatchesFilter(image, 5, ""), "text filter includes records with actual text rather than image-only records");
            check(HistoryView.MatchesFilter(image, 6, "") && HistoryView.MatchesFilter(capture, 6, "") && HistoryView.MatchesFilter(ocr, 6, "") && !HistoryView.MatchesFilter(text, 6, ""), "image filter includes stored images from clipboard and app history");
            check(HistoryView.MatchesFilter(text, 4, "alpha") && !HistoryView.MatchesFilter(text, 4, "missing") && HistoryView.MatchesFilter(translated, 3, "翻译"), "clipboard and existing translations share complete case-insensitive content search");
            check(HistoryView.KindLabel(text) == "剪贴板文字" && HistoryView.KindLabel(image) == "剪贴板图片", "clipboard rows have separate text and image labels");
            check(!HistoryView.MatchesFilter(null, 0, ""), "missing history entries do not match a filter");
        }

        internal static void RunUi(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-clipboard-view-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            Exception failure = null;
            try
            {
                store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "Alpha original" }).GetAwaiter().GetResult();
                AddImage(store).GetAwaiter().GetResult();
                using (var form = new Form { ClientSize = new Size(860, 560), StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                using (var view = new HistoryView(store, true) { Dock = DockStyle.Fill })
                {
                    form.Controls.Add(view);
                    form.Shown += async delegate
                    {
                        try
                        {
                            await view.RefreshAsync(); await Ready(view);
                            var list = Field<ListBox>(view, "list"); var search = Field<TextBox>(view, "search");
                            var type = Field<HistoryButton[]>(view, "filterButtons"); var copy = Field<Button>(view, "copySelected");
                            list.SelectedIndex = list.Items.Cast<HistoryEntry>().Select((entry, index) => new { entry, index }).Single(pair => pair.entry.Kind == "clipboard_text").index;
                            check(Field<RichTextBox>(view, "source").Text == "Alpha original" && !Field<RoundedPanel>(view, "translationPanel").Visible && !Field<RoundedPanel>(view, "previewPanel").Visible,
                                "clipboard text fills one editable-selection detail card without an empty translation panel");
                            check(copy.Enabled && copy.Text == "复制文字" && Field<Label>(view, "storagePath").Text.Contains(store.DirectoryPath), "history shows a text copy action and its actual storage directory");

                            list.SelectedIndex = list.Items.Cast<HistoryEntry>().Select((entry, index) => new { entry, index }).Single(pair => pair.entry.Kind == "clipboard_image").index;
                            await Ready(view);
                            check(copy.Enabled && copy.Text == "复制图片" && !Field<RoundedPanel>(view, "sourcePanel").Visible && !Field<RoundedPanel>(view, "translationPanel").Visible
                                && Field<PictureBox>(view, "preview").Image.Size == new Size(120, 80), "clipboard images retain the full-image copy action and large image preview without empty text cards");
                            using (var rendered = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                                rendered.Save(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", "history-clipboard-preview.png")));
                            }

                            search.Text = "Alpha";
                            string selected = ((HistoryEntry)list.SelectedItem).Id;
                            await Task.Run(async delegate { await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "Alpha incoming" }); });
                            await Until(delegate { return list.Items.Count == 2 && !Field<bool>(view, "busy"); });
                            check(search.Text == "Alpha" && ((HistoryEntry)list.SelectedItem).Id == selected && list.Items.Count == 2,
                                "background clipboard changes refresh the open view while preserving query and selected record");

                            view.Visible = false;
                            int hiddenCount = list.Items.Count;
                            await Task.Run(async delegate { await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "Alpha while hidden" }); });
                            await Task.Delay(240);
                            check(list.Items.Count == hiddenCount && Field<PictureBox>(view, "preview").Image == null, "hidden history defers live reload and holds no thumbnail");
                            view.Visible = true;
                            await Until(delegate { return list.Items.Count == 3 && !Field<bool>(view, "busy"); });
                            check(list.Items.Count == 3 && search.Text == "Alpha" && ((HistoryEntry)list.SelectedItem).Id == selected, "returning to history applies deferred clipboard changes without resetting the current selection");
                            check(type.Length == 7 && Field<Label>(view, "status").Text.Contains("7 天"), "history presents seven-day retention with separate clipboard, text and image category buttons");

                            view.Dispose();
                            await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "after disposal" });
                            check(view.IsDisposed && Field<HistoryEntry[]>(view, "entries").Length == 0, "closed history unsubscribes from store changes and releases cached records");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                if (failure != null) throw failure;
            }
            finally
            {
                Ui.RenderOffscreen = offscreen;
                store.FlushAsync().GetAwaiter().GetResult();
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        }
        private static async Task AddImage(HistoryStore store)
        {
            using (var image = new Bitmap(120, 80))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Coral);
                await store.AddAsync(new HistoryEntry { Kind = "clipboard_image" }, image).ConfigureAwait(false);
            }
        }
        private static async Task Ready(HistoryView view) { await Until(delegate { return !Field<bool>(view, "busy") && Field<CancellationTokenSource>(view, "previewRequest") == null; }); }
        private static async Task Until(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (!condition()) { if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("History view did not reach its expected live state."); await Task.Delay(10); }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

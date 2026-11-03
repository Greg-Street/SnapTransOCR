using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class HistoryTests
    {
        internal static async Task Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-test-" + Guid.NewGuid().ToString("N"));
            DateTime now = DateTime.UtcNow;
            var store = new HistoryStore(directory, () => now);
            try
            {
                check((await store.LoadAsync()).Length == 0 && !Directory.Exists(directory), "history startup does not create storage or load images");
                check(!await store.AddAsync(new HistoryEntry { Source = " " }), "history rejects empty outcomes");
                using (var image = new Bitmap(17, 13))
                {
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.MediumPurple);
                    check(await store.AddAsync(new HistoryEntry { Kind = "capture" }, image), "pure screenshot history accepts a PNG without invented OCR or translation text");
                    HistoryEntry capture = (await store.LoadAsync()).Single();
                    using (var loaded = await store.LoadImageAsync(capture.Id))
                        check(capture.Kind == "capture" && capture.Source.Length == 0 && capture.Translation.Length == 0 && loaded.Size == image.Size && loaded.GetPixel(0, 0).ToArgb() == Color.MediumPurple.ToArgb(), "pure screenshot history retains its type and exact corresponding image");
                }
                await store.ClearAsync();
                using (var image = new Bitmap(32, 24))
                {
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue);
                    check(await store.AddAsync(new HistoryEntry { Kind = "image_translation", Source = "Hello", Translation = "你好", OcrProvider = "tencent", TranslateProvider = "volc" }, image), "history saves successful text and its PNG screenshot");
                    check(image.GetPixel(0, 0).ToArgb() == Color.CornflowerBlue.ToArgb(), "history encoding does not dispose or change the caller's image");
                }
                HistoryEntry entry = (await store.LoadAsync())[0];
                check(entry.Id.Length == 32 && entry.ImageFile == entry.Id + ".png" && entry.CreatedUtc.Kind == DateTimeKind.Utc, "history assigns UTC timestamps and managed image names");
                check(entry.Source == "Hello" && entry.Translation == "你好" && entry.OcrProvider == "tencent" && entry.TranslateProvider == "volc", "history round-trips source, translation, and provider metadata");
                byte[] png = File.ReadAllBytes(Path.Combine(directory, entry.ImageFile));
                check(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "history screenshots use lossless PNG encoding");
                string json = File.ReadAllText(Path.Combine(directory, "history.json"));
                check(!json.Contains("Secret") && !json.Contains("Key") && !json.Contains("ImageBase64"), "history schema has no API credential or base64 image fields");
                using (var loaded = await store.LoadImageAsync(entry.Id))
                {
                    await store.DeleteAsync(entry.Id);
                    check(!File.Exists(Path.Combine(directory, entry.ImageFile)) && loaded.GetPixel(0, 0).ToArgb() == Color.CornflowerBlue.ToArgb(), "history image copies release file handles and survive deleting their record");
                }
                check((await store.LoadAsync()).Length == 0, "deleting history removes its metadata");
                await LockedImages(store, directory, check, () => now = now.AddDays(8));
                await store.AddAsync(new HistoryEntry { Kind = "translation", Source = "Selected text", Translation = "划词译文" });
                entry = (await store.LoadAsync())[0];
                check(String.IsNullOrEmpty(entry.ImageFile) && await store.LoadImageAsync(entry.Id) == null, "typed and selected-text translations work without screenshots");
                var gate = (SemaphoreSlim)typeof(HistoryStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
                await gate.WaitAsync();
                var pending = store.AddAsync(new HistoryEntry { Source = "queued" });
                check(!pending.IsCompleted, "history operations wait asynchronously when disk work is already queued");
                gate.Release(); await pending; await store.FlushAsync();
                check((await store.LoadAsync())[0].Source == "queued", "flushing waits for queued history writes");
                await store.ClearAsync();
                using (var image = new Bitmap(5, 5))
                {
                    for (int i = 0; i < 53; i++) await store.AddAsync(new HistoryEntry { Kind = "ocr", Source = "record " + i }, image);
                }
                HistoryEntry[] entries = await store.LoadAsync();
                check(entries.Length == 53 && entries[0].Source == "record 52" && entries[52].Source == "record 0", "history retains more than fifty results within the seven-day window");
                check(Directory.GetFiles(directory, "*.png").Length == 53, "history retains each current week's corresponding screenshot");
                await store.ClearAsync();
                check(Directory.GetFiles(directory, "*.png").Length == 0 && (await store.LoadAsync()).Length == 0, "clearing history deletes its managed screenshots and metadata");
                var writes = Enumerable.Range(0, 12).Select(i => store.AddAsync(new HistoryEntry { Source = "parallel " + i })).ToArray();
                await Task.WhenAll(writes);
                check((await store.LoadAsync()).Length == 12 && Directory.GetFiles(directory, "*.tmp").Length == 0, "concurrent history additions serialize without losing records or temporary files");
                string before = File.ReadAllText(Path.Combine(directory, "history.json"));
                check(!await store.AddAsync(new HistoryEntry { Source = new string('字', HistoryStore.MaxTextBytes / 2) }), "oversized individual records are rejected without truncating their text");
                check(File.ReadAllText(Path.Combine(directory, "history.json")) == before, "rejecting oversized text preserves existing history");
                await store.ClearAsync();
                for (int i = 0; i < 6; i++) await store.AddAsync(new HistoryEntry { Source = new string('中', 1000000), Translation = "record " + i });
                entries = await store.LoadAsync();
                check(entries.Length == 5 && entries[0].Translation == "record 4" && new FileInfo(Path.Combine(directory, "history.json")).Length <= HistoryStore.MaxTextBytes, "a full text index rejects the next record without evicting this week's saved text");
                await store.ClearAsync();
                string unrelated = Path.Combine(directory, "keep-user-image.png"); File.WriteAllText(unrelated, "not managed");
                string similar = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pngbackup"); File.WriteAllText(similar, "not managed either");
                var tampered = new HistoryEntry { Id = Guid.NewGuid().ToString("N"), Source = "tampered", ImageFile = "..\\outside.png" };
                File.WriteAllText(Path.Combine(directory, "history.json"), Json.Write(new[] { tampered }), new UTF8Encoding(false));
                check(await store.LoadImageAsync(tampered.Id) == null, "history rejects image paths that escape its managed naming scheme");
                await store.DeleteAsync(tampered.Id); await store.ClearAsync();
                check(File.Exists(unrelated) && File.Exists(similar), "history cleanup leaves unrelated files and similar extensions untouched");
                string corrupt = "{ invalid history"; File.WriteAllText(Path.Combine(directory, "history.json"), corrupt);
                bool rejected = false;
                try { await store.LoadAsync(); } catch (InvalidOperationException) { rejected = true; }
                check(rejected && File.ReadAllText(Path.Combine(directory, "history.json")) == corrupt, "unreadable history is reported without overwriting existing content");
                await store.ClearAsync();
                check((await store.LoadAsync()).Length == 0, "the clear action recovers an unreadable history file");
                string blockedIndex = Path.Combine(directory, "history.json"); File.Delete(blockedIndex); Directory.CreateDirectory(blockedIndex);
                bool failedWrite = false;
                try
                {
                    using (var image = new Bitmap(4, 4)) await store.AddAsync(new HistoryEntry { Source = "Cannot commit" }, image);
                }
                catch (IOException) { failedWrite = true; }
                finally { Directory.Delete(blockedIndex); }
                check(failedWrite && Directory.GetFiles(directory, "*.tmp").Length == 0 && Directory.GetFiles(directory, "*.png").All(path => path == unrelated || path == similar), "failed index commits remove their new screenshot and temporary files");
            }
            finally
            {
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        }
        private static async Task LockedImages(HistoryStore store, string directory, Action<bool, string> check, Action advanceWeek)
        {
            using (var image = new Bitmap(5, 5))
            {
                await store.AddAsync(new HistoryEntry { Source = "locked deletion" }, image);
                HistoryEntry entry = (await store.LoadAsync())[0];
                string path = Path.Combine(directory, entry.ImageFile);
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await store.DeleteAsync(entry.Id);
                    HistoryEntry[] entries = await store.LoadAsync();
                    check(entries.Length == 0 && File.Exists(path), "deleting history commits its index while locked image cleanup remains retryable");
                }
                check((await store.LoadAsync()).Length == 0 && !File.Exists(path), "loading history retries a deleted record's image cleanup after the image is unlocked");

                await store.AddAsync(new HistoryEntry { Source = "locked clear" }, image);
                entry = (await store.LoadAsync()).Single(); path = Path.Combine(directory, entry.ImageFile);
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool cleared = true;
                    try { await store.ClearAsync(); } catch (IOException) { cleared = false; }
                    check(cleared && (await store.LoadAsync()).Length == 0 && File.Exists(path), "clearing history succeeds after committing the empty index even when a PNG remains locked");
                }
                check((await store.LoadAsync()).Length == 0 && !File.Exists(path), "loading history retries cleared screenshot cleanup after its lock is released");

                await store.AddAsync(new HistoryEntry { Source = "index lock" }, image);
                entry = (await store.LoadAsync()).Single(); path = Path.Combine(directory, entry.ImageFile);
                bool rejected = false;
                using (var locked = File.Open(Path.Combine(directory, "history.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { await store.DeleteAsync(entry.Id); } catch (IOException) { rejected = true; }
                    check(rejected && File.Exists(path) && (await store.LoadAsync()).Single().Id == entry.Id, "failed index commits preserve both a deleted record and its screenshot for retry");
                }
                await store.DeleteAsync(entry.Id);
                check((await store.LoadAsync()).Length == 0 && !File.Exists(path), "deletion succeeds without orphaning records after the index is unlocked");

                await store.AddAsync(new HistoryEntry { Source = "locked expiration" }, image);
                entry = (await store.LoadAsync()).Last(); path = Path.Combine(directory, entry.ImageFile);
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    advanceWeek();
                    bool saved = await store.AddAsync(new HistoryEntry { Source = "committed despite old lock" }, image);
                    HistoryEntry[] entries = await store.LoadAsync();
                    check(saved && entries.Length == 1 && entries[0].Source == "committed despite old lock" && File.Exists(path), "a locked expired image does not turn a committed history addition into a reported failure");
                }
                await store.LoadAsync();
                check(!File.Exists(path) && Directory.GetFiles(directory, "*.png").Length == 1, "loading history retries deferred image cleanup after an expiration lock is released");

                entry = (await store.LoadAsync()).Last(); path = Path.Combine(directory, entry.ImageFile);
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                { advanceWeek(); await store.AddAsync(new HistoryEntry { Source = "second locked expiration" }, image); }
                await store.AddAsync(new HistoryEntry { Source = "retry cleanup on add" }, image);
                check(!File.Exists(path) && Directory.GetFiles(directory, "*.png").Length == 2, "adding history also retries cleanup of previously locked orphan images");

                path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png"); image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    check((await store.LoadAsync()).Length == 2 && File.Exists(path), "an inaccessible orphan image does not prevent reading committed history");
                }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
                await store.LoadAsync();
                check(!File.Exists(path), "orphan cleanup resumes once the file permits deletion");
                string unrelated = Path.Combine(directory, "keep-review-image.png"), similar = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pngbackup");
                File.WriteAllText(unrelated, "not managed"); File.WriteAllText(similar, "not managed either");
                await store.LoadAsync();
                check(File.Exists(unrelated) && File.Exists(similar), "automatic orphan cleanup preserves non-managed names and similar image extensions");
                File.Delete(unrelated); File.Delete(similar);
                await store.ClearAsync();
            }
        }
        internal static void RunUi(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-ui-" + Guid.NewGuid().ToString("N"));
            using (var form = new SettingsForm(new AppSettings(), delegate { }, new HistoryStore(directory)))
            {
                var view = Field<HistoryView>(form, "historyView");
                var list = Field<ListBox>(view, "list"); var source = Field<RichTextBox>(view, "source"); var translation = Field<RichTextBox>(view, "translation");
                list.Items.Add(new HistoryEntry { Id = "ui", CreatedUtc = DateTime.UtcNow, Kind = "translation", Source = "Source text", Translation = "译文" }); list.SelectedIndex = 0;
                check(source.ReadOnly && source.Text == "Source text" && translation.ReadOnly && translation.Text == "译文", "history selection exposes both complete texts in selectable read-only controls");
                check(!list.HorizontalScrollbar && source.WordWrap && translation.WordWrap && source.ScrollBars == RichTextBoxScrollBars.Vertical, "history lists and text controls use vertical scrolling only");
                int changes = 0; form.HistoryRecordingChanged += delegate { changes++; };
                form.RefreshHistory(false); check(changes == 0, "refreshing the history setting does not emit a user change");
                Field<ToggleSwitch>(view, "recording").Checked = true;
                var toggles = Field<System.Collections.Generic.Dictionary<string, ToggleSwitch>>(form, "toggles");
                check(changes == 1 && toggles["HistoryEnabled"].Checked && form.Value.HistoryEnabled, "history page switch updates the general switch and emits one immediate setting change");
                toggles["HistoryEnabled"].Checked = false;
                check(changes == 2 && !Field<ToggleSwitch>(view, "recording").Checked && !form.Value.HistoryEnabled, "general history switch stays synchronized with the embedded page");
                form.ClientSize = new Size(Ui.Scale(form, 860), Ui.Scale(form, 580)); form.SelectPage("历史记录"); form.PerformLayout();
                check(source.Parent.ClientRectangle.Contains(source.Bounds) && translation.Parent.ClientRectangle.Contains(translation.Bounds), "embedded history text areas fit the smallest settings window");
                CheckSeparateCards(view, check, "minimum size");
                int pairedHeight = source.Parent.Height;
                list.Items.Add(new HistoryEntry { Id = "capture", CreatedUtc = DateTime.UtcNow, Kind = "capture", ImageFile = "capture.png" }); list.SelectedIndex = 1;
                check(Field<Label>(view, "sourceTitle").Text == "截图" && source.Parent.Height > pairedHeight && !Field<GlyphButton>(view, "copySource").Enabled && Field<RoundedPanel>(view, "previewPanel").Bounds == source.Parent.Bounds, "pure screenshot entries use a full-height preview with text copy disabled");
                list.Items.Add(new HistoryEntry { Id = "ocr", CreatedUtc = DateTime.UtcNow, Kind = "ocr", Source = "Recognized content", ImageFile = "ocr.png" }); list.SelectedIndex = 2;
                check(Field<Label>(view, "sourceTitle").Text == "OCR 识别文字" && source.Text == "Recognized content" && source.Parent.Height > pairedHeight && Field<GlyphButton>(view, "copySource").Enabled, "OCR-only history uses its full text card without an empty translation card");
                check(HistoryView.KindLabel((HistoryEntry)list.Items[1]) == "截图" && HistoryView.KindLabel((HistoryEntry)list.Items[2]) == "OCR" && HistoryView.KindLabel(new HistoryEntry { Kind = "translation", ImageFile = "image.png" }) == "截图翻译", "history labels distinguish screenshots, OCR and screenshot translation");
                list.SelectedIndex = 0;
                CheckSeparateCards(view, check, "after screenshot and OCR entries");
                check(view.Parent != null && view.FindForm() == form && !(view.Parent is Form), "history is a UserControl inside the settings page rather than another window");
                check(!Field<bool>(form, "historyLoaded") && !Directory.Exists(directory), "hidden history pages do not load persistent data before being presented");
            }
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            {
                var fonts = new System.Collections.Generic.List<Font>();
                try
                {
                    using (var form = new SettingsForm(new AppSettings(), delegate { }, new HistoryStore(directory)))
                    {
                        SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                        form.ClientSize = new Size(Ui.Scale(form, 860), Ui.Scale(form, 580));
                        form.SelectPage("历史记录"); form.PerformLayout();
                        CheckSeparateCards(Field<HistoryView>(form, "historyView"), check, dpi + " DPI");
                    }
                }
                finally { foreach (Font font in fonts) font.Dispose(); }
            }
            EmbeddedLifecycle(check);
        }
        private static void CheckSeparateCards(HistoryView view, Action<bool, string> check, string context)
        {
            var source = Field<RichTextBox>(view, "source"); var translation = Field<RichTextBox>(view, "translation");
            check(source.Parent is RoundedPanel && translation.Parent is RoundedPanel && source.Parent != translation.Parent &&
                translation.Parent.Top - source.Parent.Bottom >= Ui.Scale(view, 12), "history source and translation occupy separate spaced cards at " + context);
            check(source.Parent.ClientRectangle.Contains(source.Bounds) && translation.Parent.ClientRectangle.Contains(translation.Bounds) &&
                view.ClientRectangle.Contains(source.Parent.Bounds) && view.ClientRectangle.Contains(translation.Parent.Bounds), "both history text cards stay inside their content area at " + context);
            var sourceCopy = Field<GlyphButton>(view, "copySource"); var translationCopy = Field<GlyphButton>(view, "copyTranslation");
            check(sourceCopy.Parent == source.Parent && translationCopy.Parent == translation.Parent &&
                sourceCopy.Parent.ClientRectangle.Contains(sourceCopy.Bounds) && translationCopy.Parent.ClientRectangle.Contains(translationCopy.Bounds), "each history text card retains its own copy action at " + context);
            var remove = Field<Button>(view, "remove");
            check(remove.Parent != source.Parent && remove.Parent != translation.Parent &&
                remove.Parent.Top >= translation.Parent.Bottom + Ui.Scale(view, 6) && view.ClientRectangle.Contains(remove.Parent.Bounds), "delete action stays below the history text cards at " + context);
        }
        private static void EmbeddedLifecycle(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-page-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            Bitmap requested = null; Exception failure = null;
            try
            {
                using (var image = new Bitmap(12, 8))
                {
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue);
                    store.AddAsync(new HistoryEntry { Kind = "ocr", Source = "Saved source", Translation = "已保存译文" }, image).GetAwaiter().GetResult();
                }
                using (var form = new SettingsForm(new AppSettings(), delegate { }, store))
                {
                    var view = Field<HistoryView>(form, "historyView"); var list = Field<ListBox>(view, "list");
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
                    form.HistoryImageRequested += delegate(Bitmap image) { requested = image; };
                    form.Shown += async delegate
                    {
                        try
                        {
                            check(!Field<bool>(form, "historyLoaded") && list.Items.Count == 0, "opening ordinary settings does not load the saved history");
                            form.SelectPage("历史记录");
                            while (Field<bool>(view, "busy")) await Task.Delay(1);
                            check(Field<bool>(form, "historyLoaded") && list.Items.Count == 1 && Field<RichTextBox>(view, "source").Text == "Saved source", "entering the embedded history page loads its saved result");
                            await (Task)typeof(HistoryView).GetMethod("ShowImage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
                            check(requested != null && requested.Size == new Size(12, 8), "the settings history page passes an independent screenshot to its host");
                            await store.AddAsync(new HistoryEntry { Kind = "translation", Source = "New result" });
                            form.RefreshHistoryEntries();
                            while (Field<bool>(view, "busy")) await Task.Delay(1);
                            check(list.Items.Count == 2, "newly saved results refresh an already opened history page");
                            while (Field<CancellationTokenSource>(view, "previewRequest") != null) await Task.Delay(1);
                            string imagePath = Path.Combine(directory, list.Items.Cast<HistoryEntry>().Single(item => !String.IsNullOrEmpty(item.ImageFile)).ImageFile);
                            using (var locked = File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                Field<Button>(view, "clear").PerformClick();
                                while (Field<bool>(view, "busy")) await Task.Delay(1);
                                check(list.Items.Count == 0 && Field<RichTextBox>(view, "source").TextLength == 0 && Field<RichTextBox>(view, "translation").TextLength == 0 &&
                                    Field<PictureBox>(view, "preview").Image == null && !Field<RoundedPanel>(view, "previewPanel").Visible &&
                                    Field<Label>(view, "status").Text == "暂无历史记录" && File.Exists(imagePath), "the clear button refreshes the history page and releases its preview even when a screenshot is locked");
                            }
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                    if (failure != null) throw failure;
                    check(view.IsDisposed && list.Items.Count == 0 && requested.GetPixel(0, 0).ToArgb() == Color.CornflowerBlue.ToArgb(), "closing settings releases history text while its requested image remains independently owned");
                }
            }
            finally
            {
                if (requested != null) requested.Dispose(); Ui.RenderOffscreen = offscreen;
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
    }
}

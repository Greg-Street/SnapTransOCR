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
    internal static class InteractionLifecycleTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-interaction-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            Exception failure = null;
            try
            {
                Add(store, "capture", Color.Crimson); Add(store, "ocr", Color.CornflowerBlue); Add(store, "image_translation", Color.ForestGreen);
                using (var context = new ApplicationContext())
                using (var first = Create(new AppSettings(), delegate { }, store))
                {
                    first.Shown += async delegate
                    {
                        try { await Exercise(first, store, check); }
                        catch (Exception error) { failure = error; }
                        finally { first.Dispose(); context.ExitThread(); }
                    };
                    first.Show(); Application.Run(context);
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
        private static async Task Exercise(SettingsForm first, HistoryStore store, Action<bool, string> check)
        {
            first.SelectPage("历史记录");
            var view = Field<HistoryView>(first, "historyView");
            var list = Field<ListBox>(view, "list"); var preview = Field<PictureBox>(view, "preview");
            await Ready(view);
            var original = preview.Image;
            var source = Field<RichTextBox>(view, "source"); var translation = Field<RichTextBox>(view, "translation");
            source.Select(2, 4); translation.Select(1, 3);
            string selected = ((HistoryEntry)list.SelectedItem).Id;
            await store.AddAsync(new HistoryEntry { Kind = "translation", Source = "New background text", Translation = "后台新增文字" });
            await view.RefreshAsync(); await Ready(view);
            check(original != null && list.Items.Count == 4 && ((HistoryEntry)list.SelectedItem).Id == selected && ReferenceEquals(original, preview.Image), "background history additions preserve the current screenshot Bitmap without decoding it again");
            check(source.SelectionStart == 2 && source.SelectionLength == 4 && translation.SelectionStart == 1 && translation.SelectionLength == 3, "refreshing the same history result preserves both text selections");

            var gate = Field<SemaphoreSlim>(store, "gate");
            await gate.WaitAsync();
            try
            {
                Select(list, "capture");
                var token = Field<CancellationTokenSource>(view, "previewRequest").Token;
                ResizePages(first);
                Field<Button>(view, "remove").PerformClick();
                check(Field<bool>(view, "busy") && preview.Image == null, "deleting during a queued thumbnail load keeps the history UI responsive without stale pixels");
                first.SelectPage("关于"); first.ClientSize = Logical(first, 1100, 800); first.SelectPage("历史记录");
                check(token.IsCancellationRequested, "page switching cancels the queued thumbnail while deletion and resizing are pending");
            }
            finally { gate.Release(); }
            await Ready(view);
            check(list.Items.Count == 3 && list.Items.Cast<HistoryEntry>().All(item => item.Kind != "capture") && preview.Image == null &&
                ((HistoryEntry)list.SelectedItem).Source == "New background text", "queued deletion selects the remaining text record and no stale screenshot reappears");
            check(IsDisposed(original), "changing away from the refreshed selection releases its previously retained thumbnail");

            SettingsForm reopened = null;
            try
            {
                await gate.WaitAsync();
                try
                {
                    Select(list, "ocr"); var token = Field<CancellationTokenSource>(view, "previewRequest").Token;
                    Field<Button>(view, "clear").PerformClick();
                    ResizePages(first); first.Close();
                    check(view.IsDisposed && token.IsCancellationRequested && preview.Image == null, "closing settings during queued clear and thumbnail work cancels its preview and releases the view");
                    reopened = Create(new AppSettings(), delegate { }, store); reopened.Show(); reopened.SelectPage("历史记录");
                }
                finally { gate.Release(); }
                var nextView = Field<HistoryView>(reopened, "historyView");
                await Ready(nextView); await store.FlushAsync();
                check(Field<ListBox>(nextView, "list").Items.Count == 0 && Field<PictureBox>(nextView, "preview").Image == null && Field<Label>(nextView, "status").Text == "暂无历史记录", "reopening settings during a pending clear observes the committed empty history");
                Add(store, "capture", Color.Goldenrod);
                reopened.RefreshHistoryEntries(); ResizePages(reopened); await Ready(nextView);
                var nextPreview = Field<PictureBox>(nextView, "preview");
                check(Field<ListBox>(nextView, "list").Items.Count == 1 && ((Bitmap)nextPreview.Image).GetPixel(10, 10).ToArgb() == Color.Goldenrod.ToArgb(), "old requests from a closed view cannot overwrite new screenshot pixels after reopening");

                Bitmap opened = null;
                reopened.HistoryImageRequested += image => opened = image;
                try
                {
                    await (Task)typeof(HistoryView).GetMethod("ShowImage", Private).Invoke(nextView, null);
                    var thumbnail = nextPreview.Image;
                    await gate.WaitAsync();
                    Task pending;
                    try
                    {
                        pending = nextView.RefreshAsync(); reopened.Close();
                        check(nextView.IsDisposed && IsDisposed(thumbnail), "closing a reopened window while refreshing releases its current preview immediately");
                    }
                    finally { gate.Release(); }
                    await pending; await store.FlushAsync();
                    check(opened != null && opened.GetPixel(10, 10).ToArgb() == Color.Goldenrod.ToArgb(), "an enlarged history image remains independently usable after its settings window closes during refresh");
                }
                finally { if (opened != null) opened.Dispose(); }
            }
            finally { if (reopened != null) reopened.Dispose(); }
            await SaveReorder(store, check);
        }
        private static async Task SaveReorder(HistoryStore store, Action<bool, string> check)
        {
            var saved = new List<AppSettings>();
            using (var form = Create(new AppSettings(), value => saved.Add(value), store))
            {
                form.Show();
                var save = Field<Button>(form, "saveButton");
                save.PerformClick();
                var translation = Field<ServiceOrderControl>(form, "translationOrder");
                var recognition = Field<ServiceOrderControl>(form, "recognitionOrder");
                form.SelectPage("翻译设置"); translation.MoveSelected(1);
                form.SelectPage("识别设置"); recognition.MoveSelected(1);
                ResizePages(form); form.SelectPage("识别设置"); save.PerformClick();
                check(saved.Count == 2 && saved[0].TranslateServiceOrder.SequenceEqual(new[] { "volc", "baidu", "bing", "ark", "baidu_llm", "baidu_domain" }) && saved[1].TranslateServiceOrder.SequenceEqual(new[] { "baidu", "volc", "bing", "ark", "baidu_llm", "baidu_domain" }) &&
                    saved[1].OcrServiceOrder.SequenceEqual(new[] { "tencent", "baidu" }), "saving, reordering both services, resizing and saving again keeps independent configuration snapshots");
                check(saved[1].TranslateProvider == saved[0].TranslateProvider && saved[1].OcrProvider == saved[0].OcrProvider && form.Visible && Field<Label>(form, "pageTitle").Text == "识别设置", "repeated saves after page changes preserve default providers and keep the current page open");
                translation.MoveSelected(-1); recognition.MoveSelected(-1);
                ResizePages(form); save.PerformClick();
                check(saved.Count == 3 && saved[2].TranslateServiceOrder.SequenceEqual(saved[0].TranslateServiceOrder) && saved[2].OcrServiceOrder.SequenceEqual(saved[0].OcrServiceOrder) &&
                    saved[1].TranslateServiceOrder[0] == "baidu", "a second reorder and save restores the original order without mutating an earlier saved snapshot");
                await Ready(Field<HistoryView>(form, "historyView"));
                form.Close();
            }
            using (var form = Create(saved.Last(), delegate { }, store))
            {
                form.Show(); ResizePages(form); form.SelectPage("翻译设置");
                check(Field<ServiceOrderControl>(form, "translationOrder").Order.SequenceEqual(saved.Last().TranslateServiceOrder) &&
                    ((string[])Field<ComboBox>(form, "translator").Tag).SequenceEqual(saved.Last().TranslateServiceOrder.Where(saved.Last().IsTranslationEnabled)), "reopened saved settings retain service and enabled dropdown order after width round trips");
                await Ready(Field<HistoryView>(form, "historyView"));
                form.Close();
            }
        }
        private static void ResizePages(SettingsForm form)
        {
            foreach (string page in new[] { "识别设置", "历史记录", "翻译设置", "截图设置", "历史记录" })
            {
                form.ClientSize = Logical(form, 1100, 800); form.SelectPage(page);
                form.ClientSize = Logical(form, 860, 580); form.ClientSize = Logical(form, 1000, 730);
            }
        }
        private static SettingsForm Create(AppSettings settings, Action<AppSettings> save, HistoryStore store)
        {
            return new SettingsForm(settings, save, store) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
        }
        private static void Add(HistoryStore store, string kind, Color color)
        {
            using (var image = new Bitmap(1000, 600))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
                store.AddAsync(new HistoryEntry { Kind = kind, Source = kind == "capture" ? "" : "Original synthetic " + kind, Translation = kind == "image_translation" ? "合成翻译文字" : "" }, image).GetAwaiter().GetResult();
            }
        }
        private static Size Logical(Control form, int width, int height) { return new Size(Ui.Scale(form, width), Ui.Scale(form, height)); }
        private static async Task Ready(HistoryView view) { while (Field<bool>(view, "busy") || Field<CancellationTokenSource>(view, "previewRequest") != null) await Task.Delay(1); }
        private static void Select(ListBox list, string kind) { list.SelectedIndex = list.Items.Cast<HistoryEntry>().Select((item, index) => new { item, index }).Single(value => value.item.Kind == kind).index; }
        private static bool IsDisposed(Image image) { try { int width = image.Width; return false; } catch (ArgumentException) { return true; } }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

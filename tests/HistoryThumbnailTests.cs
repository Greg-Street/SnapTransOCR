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
    internal static class HistoryThumbnailTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-thumbnail-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            Bitmap enlarged = null;
            Exception failure = null;
            try
            {
                Add(store, "capture", Color.Crimson, new Size(1600, 900));
                Add(store, "ocr", Color.CornflowerBlue, new Size(64, 48));
                Add(store, "image_translation", Color.ForestGreen, new Size(1200, 1600));
                store.AddAsync(new HistoryEntry { Kind = "translation", Source = "Typed text", Translation = "输入翻译" }).GetAwaiter().GetResult();
                HistoryEntry[] entries = store.LoadAsync().GetAwaiter().GetResult();
                string captureId = entries.Single(item => item.Kind == "capture").Id;
                using (var thumbnail = store.LoadThumbnailAsync(captureId, new Size(800, 600), CancellationToken.None).GetAwaiter().GetResult())
                    check(thumbnail.Size == new Size(800, 450) && thumbnail.GetPixel(100, 100).ToArgb() == Color.Crimson.ToArgb(), "history thumbnail scales a large screenshot within its pixel budget while preserving aspect ratio and pixels");

                using (var form = new Form { ClientSize = new Size(760, 500), StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                using (var view = new HistoryView(store, true) { Dock = DockStyle.Fill })
                {
                    form.Controls.Add(view);
                    view.ImageRequested += image => enlarged = image;
                    form.Shown += async delegate
                    {
                        form.ClientSize = new Size(Ui.Scale(form, 760), Ui.Scale(form, 500));
                        try
                        {
                            await view.RefreshAsync();
                            var list = Field<ListBox>(view, "list"); var preview = Field<PictureBox>(view, "preview");
                            var panel = Field<RoundedPanel>(view, "previewPanel");
                            Select(list, "capture"); await Ready(view);
                            check(preview.Image != null && preview.Image.Size == new Size(800, 450) && panel.Bounds == Field<RoundedPanel>(view, "sourcePanel").Bounds, "selecting a screenshot automatically fills the main history detail area with its bounded thumbnail");
                            CheckLayout(view, check, "screenshot");
                            Bitmap previous = (Bitmap)preview.Image;
                            Select(list, "ocr"); await Ready(view);
                            check(IsDisposed(previous) && preview.Image.Size == new Size(64, 48), "switching records releases the previous preview and does not upscale a small screenshot");
                            check(Field<RichTextBox>(view, "source").Text == "OCR sample" && panel.Left >= Field<RoundedPanel>(view, "listPanel").Right && panel.Bottom < Field<RoundedPanel>(view, "sourcePanel").Top, "OCR keeps its text card beneath the automatic screenshot preview in the right detail column");
                            CheckLayout(view, check, "OCR");

                            var gate = Field<SemaphoreSlim>(store, "gate");
                            await gate.WaitAsync();
                            try
                            {
                                for (int i = 0; i < 25; i++) { Select(list, "capture"); Select(list, "image_translation"); }
                                Select(list, "ocr");
                                check(preview.Image == null && Field<CancellationTokenSource>(view, "previewRequest") != null, "rapid record switching clears stale preview pixels while asynchronous loading is blocked");
                            }
                            finally { gate.Release(); }
                            await Ready(view);
                            check(preview.Image != null && preview.Image.Size == new Size(64, 48) && ((Bitmap)preview.Image).GetPixel(10, 10).ToArgb() == Color.CornflowerBlue.ToArgb(), "cancelled old thumbnail requests cannot overwrite the latest selection");

                            typeof(Control).GetMethod("OnClick", Private).Invoke(preview, new object[] { EventArgs.Empty });
                            while (Field<bool>(view, "busy")) await Task.Delay(1);
                            check(enlarged != null && enlarged.Size == new Size(64, 48) && !ReferenceEquals(enlarged, preview.Image), "clicking the automatic thumbnail opens an independently owned full screenshot through the existing image action");
                            Select(list, "image_translation"); await Ready(view);
                            check(Field<RichTextBox>(view, "source").Text == "Original sample" && Field<RichTextBox>(view, "translation").Text == "翻译示例" && preview.Image.Size == new Size(450, 600), "image translation retains both text cards below its automatic screenshot preview");
                            CheckLayout(view, check, "image translation");
                            previous = (Bitmap)preview.Image;
                            Select(list, "translation");
                            check(preview.Image == null && IsDisposed(previous) && !panel.Visible, "selecting text-only history releases and hides the screenshot preview");

                            Select(list, "capture"); await Ready(view); previous = (Bitmap)preview.Image;
                            await store.DeleteAsync(captureId); await view.RefreshAsync(); await Ready(view);
                            check(IsDisposed(previous) && list.Items.Cast<HistoryEntry>().All(item => item.Id != captureId), "deleting the selected screenshot releases its thumbnail and selects a remaining record");
                            check(!File.Exists(Path.Combine(directory, captureId + ".png")), "thumbnail decoding closes its source file so deleting history removes the PNG immediately");
                            await store.ClearAsync(); await view.RefreshAsync();
                            check(preview.Image == null && list.Items.Count == 0 && !panel.Visible, "clearing history removes the selected preview as well as its records");

                            Add(store, "capture", Color.Crimson, new Size(1600, 900)); await view.RefreshAsync(); await Ready(view);
                            previous = (Bitmap)preview.Image;
                            view.Visible = false;
                            check(preview.Image == null && IsDisposed(previous), "leaving the history page releases its displayed thumbnail");
                            view.Visible = true; await Ready(view); previous = (Bitmap)preview.Image;
                            view.Dispose();
                            check(IsDisposed(previous) && enlarged.GetPixel(0, 0).ToArgb() == Color.CornflowerBlue.ToArgb(), "closing the history view releases its preview while an opened full image remains usable");
                        }
                        catch (Exception error) { failure = error; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                if (failure != null) throw failure;
                Layouts(store, check);
            }
            finally
            {
                if (enlarged != null) enlarged.Dispose();
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
                        var view = Field<HistoryView>(form, "historyView"); var list = Field<ListBox>(view, "list");
                        foreach (string kind in new[] { "capture", "ocr", "image_translation" })
                        {
                            list.Items.Clear(); list.Items.Add(new HistoryEntry { Id = kind, Kind = kind, Source = "Original", Translation = kind == "image_translation" ? "译文" : "", ImageFile = "image.png" }); list.SelectedIndex = 0;
                            CheckLayout(view, check, kind + " " + width + "px at " + dpi + " DPI");
                        }
                    }
                }
                finally { foreach (Font font in fonts) font.Dispose(); }
            }
        }
        private static void CheckLayout(HistoryView view, Action<bool, string> check, string context)
        {
            var preview = Field<PictureBox>(view, "preview"); var panel = Field<RoundedPanel>(view, "previewPanel");
            var list = Field<RoundedPanel>(view, "listPanel");
            var source = Field<RichTextBox>(view, "source"); var translation = Field<RichTextBox>(view, "translation");
            bool capture = ((HistoryEntry)Field<ListBox>(view, "list").SelectedItem).Kind == "capture";
            check(view.ClientRectangle.Contains(panel.Bounds) && panel.ClientRectangle.Contains(preview.Bounds) && panel.Left >= list.Right && list.Height == view.ClientSize.Height - Ui.Scale(view, 80), "history preview stays in the right detail column and preserves the full-height record list: " + context);
            check(capture || (panel.Bottom + Ui.Scale(view, 12) <= source.Parent.Top && source.Parent.ClientRectangle.Contains(source.Bounds) && source.Height >= source.Font.Height), "the right preview leaves a complete readable text line in the source card: " + context);
            if (((HistoryEntry)Field<ListBox>(view, "list").SelectedItem).Kind == "image_translation")
                check(source.Parent.Bottom + Ui.Scale(view, 12) <= translation.Parent.Top && translation.Parent.ClientRectangle.Contains(translation.Bounds) && translation.Height >= translation.Font.Height, "the translation retains its separate readable card below the source: " + context);
        }
        private static void Add(HistoryStore store, string kind, Color color, Size size)
        {
            using (var image = new Bitmap(size.Width, size.Height))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
                store.AddAsync(new HistoryEntry { Kind = kind, Source = kind == "capture" ? "" : kind == "ocr" ? "OCR sample" : "Original sample", Translation = kind == "image_translation" ? "翻译示例" : "" }, image).GetAwaiter().GetResult();
            }
        }
        private static void Select(ListBox list, string kind) { list.SelectedIndex = list.Items.Cast<HistoryEntry>().Select((item, index) => new { item, index }).Single(pair => pair.item.Kind == kind).index; }
        private static async Task Ready(HistoryView view) { while (Field<CancellationTokenSource>(view, "previewRequest") != null) await Task.Delay(1); }
        private static bool IsDisposed(Bitmap image) { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

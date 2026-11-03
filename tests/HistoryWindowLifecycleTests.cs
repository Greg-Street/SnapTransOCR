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
    internal static class HistoryWindowLifecycleTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-window-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var image = Sample(180, 100, Color.Coral)) store.AddAsync(new HistoryEntry { Kind = "capture" }, image).GetAwaiter().GetResult();
                using (var image = Sample(320, 160, Color.CornflowerBlue)) store.AddAsync(new HistoryEntry { Kind = "capture" }, image).GetAwaiter().GetResult();
                using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false, PinTopmost = false }, true, persistSettings: delegate { }))
                {
                    typeof(DesktopApp).GetField("history", Private).SetValue(app, store);
                    var main = Field<MainForm>(app, "main");
                    Exception failure = null;
                    main.BeginInvoke((Action)(async delegate
                    {
                        try { await Exercise(app, store, check); }
                        catch (Exception ex) { failure = ex; }
                        finally { Invoke(app, "Exit"); }
                    }));
                    Application.Run(app);
                    if (failure != null) throw failure;
                }
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
            Invoke(app, "ShowHistory");
            var history = Field<HistoryForm>(app, "historyWindow");
            var view = history.View;
            check(Field<SettingsForm>(app, "settingsWindow") == null, "opening history creates a standalone window without opening settings");
            await view.RefreshAsync(); await Ready(view);
            var list = Field<ListBox>(view, "list"); var preview = Field<PictureBox>(view, "preview");
            var windows = Field<List<Form>>(app, "imageWindows");
            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            var first = windows.OfType<EditorForm>().Single(); Bitmap firstImage = CanvasImage(first);
            list.SelectedIndex = 1; await Ready(view);
            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            var second = windows.OfType<EditorForm>().Single(); Bitmap secondImage = CanvasImage(second);
            check(first.IsDisposed && Disposed(firstImage) && !ReferenceEquals(first, second) && secondImage.Size == new Size(180, 100),
                "clicking a different history screenshot closes the previous editor and releases its source bitmap");
            check(ReferenceEquals(Field<EditorForm>(app, "historyEditor"), second) && history.Visible && history.Enabled,
                "history tracks only its current editor while the standalone history window remains usable");

            await (Task)Invoke(app, "OpenEditor", Sample(90, 60, Color.Green), true);
            var ordinary = windows.OfType<EditorForm>().Single(item => item != second);
            list.SelectedIndex = 0; await Ready(view);
            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            var current = windows.OfType<EditorForm>().Single(item => item != ordinary); Bitmap currentImage = CanvasImage(current);
            check(second.IsDisposed && Disposed(secondImage) && ordinary.Visible && windows.OfType<EditorForm>().Count() == 2,
                "replacing a history editor leaves an unrelated image editor open");
            Field<ImageCanvas>(current, "canvas").AddMark(new Mark { Kind = Tool.Rectangle, Color = Color.Red, Width = 4, Bounds = new RectangleF(10, 10, 70, 50) });
            Button(current, "pin").PerformClick();
            var pin = windows.OfType<PinForm>().Single(); Bitmap pinned = Field<Bitmap>(pin, "image");
            check(current.IsDisposed && Disposed(currentImage) && Field<EditorForm>(app, "historyEditor") == null && pin.Visible && !Disposed(pinned),
                "pinning a historical screenshot closes its editor after the independent rendered image becomes visible");
            check(pinned.GetPixel(10, 30).R > 230 && pinned.GetPixel(10, 30).B < 30 && ordinary.Visible,
                "the pin retains edited pixels and unrelated editors survive the transfer");

            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            current = windows.OfType<EditorForm>().Single(item => item != ordinary);
            check(pin.Visible && !Disposed(pinned), "opening another historical screenshot leaves an existing pin unchanged");
            current.Dispose();
            check(Field<EditorForm>(app, "historyEditor") == null && !windows.Contains(current),
                "direct disposal releases the current history-editor reference and tracking entry");
            Invoke(preview, "OnClick", EventArgs.Empty); await Ready(view);
            current = windows.OfType<EditorForm>().Single(item => item != ordinary);
            Button(current, "history").PerformClick();
            check(current.IsDisposed && ReferenceEquals(Field<HistoryForm>(app, "historyWindow").View, view) && history.Visible,
                "returning from a replacement editor reuses the same standalone history window");
            Button(ordinary, "pin").PerformClick();
            check(ordinary.IsDisposed && windows.OfType<PinForm>().Count() == 2 && !windows.OfType<EditorForm>().Any(),
                "pinning an ordinary editor also closes only that editor");
            foreach (var window in windows.ToArray()) window.Close();
            check(windows.Count == 0 && Disposed(pinned) && (await store.LoadAsync()).Length == 2,
                "repeated preview and pin operations release images without adding duplicate history records");
        }
        private static GlyphButton Button(EditorForm editor, string glyph)
        { return editor.Controls.OfType<FlowLayoutPanel>().SelectMany(bar => bar.Controls.OfType<GlyphButton>()).Single(button => button.Glyph == glyph); }
        private static Bitmap CanvasImage(EditorForm editor) { return Field<Bitmap>(Field<ImageCanvas>(editor, "canvas"), "image"); }
        private static async Task Ready(HistoryView view)
        { while (Field<bool>(view, "busy") || Field<CancellationTokenSource>(view, "previewRequest") != null) await Task.Delay(1); }
        private static Bitmap Sample(int width, int height, Color color)
        { var image = new Bitmap(width, height); using (var graphics = Graphics.FromImage(image)) graphics.Clear(color); return image; }
        private static bool Disposed(Bitmap image) { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static object Invoke(object owner, string name, params object[] arguments) { return owner.GetType().GetMethod(name, Private).Invoke(owner, arguments); }
    }
}

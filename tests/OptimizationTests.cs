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
    internal static class OptimizationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            UnicodeFormatting(check);
            SnapshotOwnership(check);
            Canvas(check);
            RepaintParity(check);
            ContinuousScroll(check);
            History(check);
            Task.Run(() => HistoryImageWork(check)).GetAwaiter().GetResult();
            Task.Run(() => Workbook(check)).GetAwaiter().GetResult();
        }
        private static void UnicodeFormatting(Action<bool, string> check)
        {
            foreach (string fragment in new[] { "\ud83d", "\udc00", "x\ud83d", "\udc00x" })
            {
                check(TextFormatting.MergeLines(fragment + "\nword") == fragment + " word", "merging lines retains incomplete UTF16 at the previous line boundary");
                check(TextFormatting.MergeLines("word\n" + fragment) == "word " + fragment, "merging lines retains incomplete UTF16 at the next line boundary");
            }
            string hieroglyph = Char.ConvertFromUtf32(0x13002), ideograph = Char.ConvertFromUtf32(0x20000);
            check(TextFormatting.MergeLines(hieroglyph + "\nword") == hieroglyph + " word", "non-CJK supplementary characters keep word separation even when their low code unit matches CJK punctuation");
            check(TextFormatting.MergeLines(ideograph + "\n文字") == ideograph + "文字", "supplementary CJK characters retain the existing Chinese line-joining behavior");
        }
        private static void SnapshotOwnership(Action<bool, string> check)
        {
            using (var image = new Bitmap(30, 20))
            {
                image.SetPixel(3, 4, Color.CornflowerBlue);
                var original = new DataObject(); original.SetImage(image);
                using (var snapshot = ClipboardSnapshot.Capture(original))
                {
                    var copied = (Bitmap)snapshot.Data.GetData(DataFormats.Bitmap, false);
                    check(!ReferenceEquals(copied, image) && copied.GetPixel(3, 4).ToArgb() == Color.CornflowerBlue.ToArgb(), "clipboard restoration retains an independently owned image snapshot");
                }
                check(image.GetPixel(3, 4).ToArgb() == Color.CornflowerBlue.ToArgb(), "disposing a restoration snapshot preserves images supplied by its caller");
            }
        }
        private static void Canvas(Action<bool, string> check)
        {
            using (var source = new Bitmap(640, 480))
            {
                using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.White);
                using (var canvas = new ImageCanvas((Bitmap)source.Clone()) { Size = source.Size, PixelView = true })
                using (var frame = new Bitmap(source.Width, source.Height))
                using (var graphics = Graphics.FromImage(frame))
                {
                    canvas.Fit();
                    var paint = typeof(ImageCanvas).GetMethod("OnPaint", Private);
                    graphics.Clear(Color.Magenta); var clip = new Rectangle(50, 40, 16, 20); graphics.SetClip(clip);
                    paint.Invoke(canvas, new object[] { new PaintEventArgs(graphics, clip) });
                    check(frame.GetPixel(100, 100).ToArgb() == Color.Magenta.ToArgb() && frame.GetPixel(55, 45).ToArgb() == Color.White.ToArgb(), "canvas painting respects an existing dirty clip and leaves all other pixels untouched");
                    IntPtr handle = canvas.Handle;
                    Rectangle dirty = Rectangle.Empty;
                    canvas.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty = e.InvalidRect; };
                    foreach (Tool tool in new[] { Tool.Pen, Tool.Line, Tool.Arrow, Tool.Rectangle, Tool.Ellipse, Tool.Crop, Tool.Mosaic })
                    {
                        canvas.Tool = tool; canvas.Stroke = 3;
                        Mouse(canvas, "OnMouseDown", new Point(70, 80));
                        Mouse(canvas, "OnMouseMove", new Point(95, 105));
                        check(dirty.Width < canvas.Width / 2 && dirty.Height < canvas.Height / 2 && dirty.Contains(new Point(80, 90)), "drawing invalidates the affected image region, tool=" + tool);
                        canvas.Capture = false;
                    }
                }
            }
        }
        private static void Mouse(ImageCanvas canvas, string name, Point location)
        {
            typeof(ImageCanvas).GetMethod(name, Private).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, location.X, location.Y, 0) });
        }
        private static void ContinuousScroll(Action<bool, string> check)
        {
            using (var page = ScrollingOptimizationTests.Pattern(240, 900, 3))
            using (var first = page.Clone(new Rectangle(0, 0, 240, 600), page.PixelFormat))
            using (var stitcher = new Stitcher(first))
            {
                bool appended = true; string message;
                for (int offset = 1; offset <= 120; offset++)
                    using (var frame = page.Clone(new Rectangle(0, offset, 240, 600), page.PixelFormat)) appended &= stitcher.Append(frame, out message);
                check(appended && stitcher.FrameCount == 121 && stitcher.TotalHeight == 720, "continuous small scroll movements can extend beyond sixty sampled frames while staying within the image-size budget");
                using (var result = stitcher.Finish())
                using (var expected = page.Clone(new Rectangle(0, 0, 240, 720), page.PixelFormat))
                    check(CapturePerformanceTests.SamePixels(result, expected), "a long sequence of sampled scroll fragments retains every original output pixel");
            }
            using (var page = ScrollingOptimizationTests.Pattern(80, 24001, 3))
            using (var first = page.Clone(new Rectangle(0, 0, 80, 23990), page.PixelFormat))
            using (var stitcher = new Stitcher(first))
            {
                string message;
                using (var edge = page.Clone(new Rectangle(0, 10, 80, 23990), page.PixelFormat))
                    check(stitcher.Append(edge, out message) && stitcher.TotalHeight == 24000, "continuous capture accepts a final frame exactly at the output-height budget");
                bool rejected = false;
                using (var excessive = page.Clone(new Rectangle(0, 11, 80, 23990), page.PixelFormat))
                    try { stitcher.Append(excessive, out message); } catch (InvalidOperationException) { rejected = true; }
                check(rejected && stitcher.TotalHeight == 24000 && stitcher.FrameCount == 2, "exceeding the output-size budget rejects the frame and retains the last committed scrolling image");
            }
        }
        private static void RepaintParity(Action<bool, string> check)
        {
            foreach (float zoom in new[] { 0.5f, 1f, 1.4f })
            foreach (Tool tool in new[] { Tool.Pen, Tool.Line, Tool.Arrow, Tool.Rectangle, Tool.Ellipse, Tool.Crop, Tool.Mosaic })
            using (var canvas = new ImageCanvas(CaptureRefinementTests.Page(640, 480)) { Size = new Size(640, 480), Tool = tool, Stroke = 8 })
            using (var partial = new Bitmap(640, 480))
            using (var expected = new Bitmap(640, 480))
            {
                canvas.AddMark(new Mark { Kind = Tool.Crop, Bounds = new RectangleF(40, 30, 540, 400) });
                typeof(ImageCanvas).GetField("zoom", Private).SetValue(canvas, zoom);
                typeof(ImageCanvas).GetField("pan", Private).SetValue(canvas, new PointF(-8, 11));
                IntPtr handle = canvas.Handle;
                Rectangle dirty = canvas.ClientRectangle;
                canvas.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty = dirty.IsEmpty ? e.InvalidRect : Rectangle.Union(dirty, e.InvalidRect); };
                Paint(canvas, partial, canvas.ClientRectangle);
                Mouse(canvas, "OnMouseDown", new Point(70, 80));
                foreach (Point point in new[] { new Point(160, 150), new Point(290, 260), new Point(100, 250), new Point(200, 140), new Point(80, 70), new Point(430, 310) })
                {
                    dirty = Rectangle.Empty;
                    Mouse(canvas, "OnMouseMove", point);
                    Paint(canvas, partial, dirty); Paint(canvas, expected, canvas.ClientRectangle);
                    // Fractional GDI+ scaling can round a color channel by one unit at a different clip origin.
                    bool equal = CapturePerformanceTests.SamePixels(partial, expected, zoom == 1 ? 0 : 1);
                    if (!equal)
                    {
                        string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output"));
                        partial.Save(Path.Combine(output, "annotation-partial-0.2.28.png")); expected.Save(Path.Combine(output, "annotation-full-0.2.28.png"));
                    }
                    check(equal, "incremental annotation matches a full repaint without stale edges, tool=" + tool + ", zoom=" + zoom + ", point=" + point + ", dirty=" + dirty);
                }
                canvas.Capture = false;
            }
        }
        private static void Paint(ImageCanvas canvas, Bitmap target, Rectangle rectangle)
        {
            using (var graphics = Graphics.FromImage(target))
            using (var brush = new SolidBrush(canvas.BackColor))
            {
                graphics.SetClip(rectangle); graphics.FillRectangle(brush, rectangle);
                typeof(ImageCanvas).GetMethod("OnPaint", Private).Invoke(canvas, new object[] { new PaintEventArgs(graphics, rectangle) });
            }
        }
        private static void History(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-interaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var entries = Enumerable.Range(0, 120).Select(index => new HistoryEntry { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow.AddMinutes(-index), Kind = "clipboard_text", Source = "Synthetic " + index }).ToArray();
            string path = Path.Combine(directory, "history.json"); File.WriteAllText(path, Json.Write(entries));
            var store = new HistoryStore(directory);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            Exception failure = null;
            try
            {
                using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(900, 620) })
                using (var view = new HistoryView(store, true) { Dock = DockStyle.Fill })
                {
                    host.Controls.Add(view);
                    host.Shown += async delegate
                    {
                        try
                        {
                            await view.RefreshAsync();
                            var list = (ListBox)typeof(HistoryView).GetField("list", Private).GetValue(view);
                            var search = (TextBox)typeof(HistoryView).GetField("search", Private).GetValue(view);
                            var gate = (SemaphoreSlim)typeof(HistoryStore).GetField("gate", Private).GetValue(store);
                            await gate.WaitAsync(); Task refresh;
                            try
                            {
                                search.Text = "Synthetic"; search.SelectionStart = 3;
                                refresh = view.RefreshAsync();
                                check(search.Enabled && list.Enabled, "background history refresh keeps search and record navigation usable");
                                search.SelectionStart = 4;
                            }
                            finally { gate.Release(); }
                            await refresh;
                            check(search.Text == "Synthetic" && search.SelectionStart == 4, "a background refresh preserves the active query and text caret");
                            list.SelectedIndex = 35; list.TopIndex = 30;
                            string top = ((HistoryEntry)list.Items[list.TopIndex]).Id, selected = ((HistoryEntry)list.SelectedItem).Id;
                            await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "Synthetic newer" });
                            await view.RefreshAsync();
                            check(((HistoryEntry)list.Items[list.TopIndex]).Id == top && ((HistoryEntry)list.SelectedItem).Id == selected, "new clipboard entries preserve the visible history position and selected record");
                            search.Clear(); list.SelectedIndex = 2; list.TopIndex = 0;
                            await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "New latest" }); await view.RefreshAsync();
                            check(list.TopIndex == 0 && ((HistoryEntry)list.Items[0]).Source == "New latest", "history browsing at the top continues to show newly added entries");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { host.Close(); }
                    };
                    Application.Run(host);
                }
                if (failure != null) throw failure;
            }
            finally
            {
                Ui.RenderOffscreen = offscreen; store.FlushAsync().GetAwaiter().GetResult();
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory);
            }
        }
        private static async Task Workbook(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-workbook-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "识别结果.xlsx");
            byte[] original = { 80, 75, 3, 4, 1 }, updated = { 80, 75, 3, 4, 2, 3, 4 };
            try
            {
                await OcrForm.SaveWorkbookAsync(destination, original);
                check(File.ReadAllBytes(destination).SequenceEqual(original), "workbook export commits the complete payload to a new Unicode filename");
                using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool rejected = false;
                    try { await OcrForm.SaveWorkbookAsync(destination, updated); } catch (IOException) { rejected = true; }
                    check(rejected && locked.Length == original.Length && Directory.GetFiles(directory).Length == 1, "a locked workbook rejects replacement while preserving its contents and cleaning its temporary file");
                }
                check(File.ReadAllBytes(destination).SequenceEqual(original), "a failed export leaves every original workbook byte intact");
                await OcrForm.SaveWorkbookAsync(destination, updated);
                check(File.ReadAllBytes(destination).SequenceEqual(updated) && Directory.GetFiles(directory).Length == 1, "workbook overwrite atomically installs the full new payload and releases staging storage");
            }
            finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
        private static async Task HistoryImageWork(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-image-work-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(directory);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Task<Bitmap> loading = null;
                Task<Bitmap> queued = null;
                try
                {
                    using (var image = new Bitmap(200, 100)) await store.AddAsync(new HistoryEntry { Kind = "capture" }, image).ConfigureAwait(false);
                    string id = (await store.LoadAsync().ConfigureAwait(false))[0].Id;
                    Func<Image, Bitmap> decode = image => { entered.Set(); release.Wait(); return new Bitmap(image); };
                    var read = typeof(HistoryStore).GetMethod("ReadImageAsync", Private);
                    loading = (Task<Bitmap>)read.Invoke(store, new object[] { id, decode, CancellationToken.None });
                    check(entered.Wait(3000), "the image worker starts its decoder outside the UI thread");
                    Task<bool> writing = store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "Copied while preview was loading" });
                    bool responsive = await Task.WhenAny(writing, Task.Delay(1500)).ConfigureAwait(false) == writing;
                    check(responsive && await writing.ConfigureAwait(false), "history can commit new clipboard content while an image decoder is still running");
                    var nextStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Func<Image, Bitmap> nextDecode = image => { nextStarted.TrySetResult(true); return new Bitmap(image); };
                    queued = (Task<Bitmap>)read.Invoke(store, new object[] { id, nextDecode, CancellationToken.None });
                    await Task.Delay(20).ConfigureAwait(false);
                    check(!nextStarted.Task.IsCompleted && !queued.IsCompleted, "only one image decoder runs while subsequent image work waits independently of history writes");
                    using (var cancellation = new CancellationTokenSource())
                    {
                        Task<Bitmap> waiting = store.LoadThumbnailAsync(id, new Size(80, 60), cancellation.Token);
                        cancellation.Cancel(); bool cancelled = false;
                        try { using (await waiting.ConfigureAwait(false)) { } } catch (OperationCanceledException) { cancelled = true; }
                        check(cancelled && !loading.IsCompleted, "a cancelled queued preview finishes without waiting for or starting another decoder");
                    }
                    release.Set(); using (var image = await loading.ConfigureAwait(false)) check(image.Size == new Size(200, 100), "the independently running image decoder still returns the complete selected image");
                    loading = null;
                    using (var image = await queued.ConfigureAwait(false)) check(nextStarted.Task.IsCompleted && image.Size == new Size(200, 100), "queued image work resumes after the previous decoder releases its resources");
                    queued = null;
                    entered.Reset(); release.Reset();
                    loading = (Task<Bitmap>)read.Invoke(store, new object[] { id, decode, CancellationToken.None });
                    check(entered.Wait(3000), "a second image read remains open for concurrent deletion verification");
                    await store.DeleteAsync(id).ConfigureAwait(false);
                    check(!File.Exists(Path.Combine(directory, id + ".png")), "deleting history unlinks its PNG while an existing preview read is active");
                    release.Set();
                    using (var image = await loading.ConfigureAwait(false)) check(image.Size == new Size(200, 100), "a preview keeps its opened image readable after concurrent history deletion");
                    loading = null;
                    await store.ClearAsync().ConfigureAwait(false);
                    check(Directory.GetFiles(directory, "*.png").Length == 0, "image streams close before returning so subsequent history cleanup removes their PNG files");
                }
                finally
                {
                    release.Set(); if (loading != null) using (await loading.ConfigureAwait(false)) { }
                    if (queued != null) using (await queued.ConfigureAwait(false)) { }
                    await store.FlushAsync().ConfigureAwait(false);
                    if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
                }
            }
        }
    }
}

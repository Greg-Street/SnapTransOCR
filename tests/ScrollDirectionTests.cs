using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ScrollDirectionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Bitmap Crop(Bitmap image, int top) { return image.Clone(new Rectangle(0, top, image.Width, 600), PixelFormat.Format24bppRgb); }
        internal static void Run(Action<bool, string> check)
        {
            using (var page = ScrollingOptimizationTests.Pattern(240, 3200, 3))
            using (var first = Crop(page, 900))
            using (var stitcher = new Stitcher(first))
            {
                int top = 900, bottom = 1500;
                string message;
                foreach (int offset in new[] { 740, 560, 300, 40, 0, 200, 400, 700, 1000, 1300, 1600, 1900, 2200, 1700, 1300, 850, 450, 120, 0 })
                using (var frame = Crop(page, offset))
                {
                    int count = stitcher.FrameCount;
                    bool grows = offset < top || offset + 600 > bottom;
                    top = Math.Min(top, offset); bottom = Math.Max(bottom, offset + 600);
                    check(stitcher.Append(frame, out message) && stitcher.TotalHeight == bottom - top, "both scroll directions and reversal locate the correct captured range, viewport=" + offset);
                    check(stitcher.FrameCount == count + (grows ? 1 : 0), "revisiting captured content does not consume a new-frame slot, viewport=" + offset);
                }
                using (var result = stitcher.Finish()) check(Matches(page, result, top, 0, 0), "upward, downward and reversed scrolling compose every body pixel exactly once");
                int stable = stitcher.FrameCount;
                for (int i = 0; i < 70; i++)
                using (var frame = Crop(page, i % 2 == 0 ? 120 : 0)) check(stitcher.Append(frame, out message), "repeated navigation within captured content remains locatable, step=" + i);
                check(stitcher.FrameCount == stable && stitcher.TotalHeight == bottom - top, "backtracking remains bounded without adding duplicate pieces or hitting the frame limit");
            }
            using (var page = ScrollingOptimizationTests.Pattern(240, 2400, 3))
            using (var first = Crop(page, 0))
            using (var stitcher = new Stitcher(first))
            {
                string message;
                using (var fast = Crop(page, 520)) check(stitcher.Append(fast, out message) && stitcher.TotalHeight == 1120, "fast downward movement matches with eighty pixels of overlap");
                using (var gap = Crop(page, 1120)) check(!stitcher.Append(gap, out message) && message.Contains("回滚"), "a skipped viewport with no overlap is rejected with recovery guidance");
                using (var recover = Crop(page, 900)) check(stitcher.Append(recover, out message) && stitcher.TotalHeight == 1500, "recovering after an unmatched jump keeps the previous baseline and complete content");
                using (var result = stitcher.Finish()) check(Matches(page, result, 0, 0, 0), "fast scrolling and recovery introduce no missing or repeated rows");
            }
            using (var page = CaptureRefinementTests.Page(420, 2400))
            foreach (bool floating in new[] { false, true })
            using (var first = CaptureRefinementTests.Viewport(page, 900, 600, floating ? 0 : 62, floating ? 0 : 74, floating))
            using (var stitcher = new Stitcher(first))
            {
                string message;
                foreach (int offset in new[] { 700, 500, 300, 100, 0, 300, 600, 900, 1200, 1500 })
                using (var frame = CaptureRefinementTests.Viewport(page, offset, 600, floating ? 0 : 62, floating ? 0 : 74, floating))
                    check(stitcher.Append(frame, out message), "fixed decorations match while changing scroll direction, offset=" + offset + ", floating=" + floating);
                using (var result = stitcher.Finish()) check(result.Height == 2100 && Matches(page, result, 0, floating ? 0 : 62, floating ? 70 : 74), "fixed headers and bottom overlays remain at the outer edges after bidirectional capture, floating=" + floating);
            }
            Sampling(check);
        }
        private static bool Matches(Bitmap page, Bitmap result, int top, int skipTop, int skipBottom)
        {
            for (int y = skipTop; y < result.Height - skipBottom; y++)
                for (int x = 0; x < result.Width; x++) if (page.GetPixel(x, y + top) != result.GetPixel(x, y)) return false;
            return true;
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        private static void Sampling(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; var context = SynchronizationContext.Current;
            Ui.RenderOffscreen = true;
            try
            {
                using (var pump = new Control())
                using (var application = new ApplicationContext())
                {
                    IntPtr handle = pump.Handle;
                    Exception failure = null;
                    pump.BeginInvoke((Action)(async delegate
                    {
                        try { await SamplingAsync(check); }
                        catch (Exception error) { failure = error; }
                        finally { application.ExitThread(); }
                    }));
                    Application.Run(application);
                    if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = offscreen; SynchronizationContext.SetSynchronizationContext(context); }
        }
        private static async Task SamplingAsync(Action<bool, string> check)
        {
            using (var page = ScrollingOptimizationTests.Pattern(240, 3000, 3))
            using (var first = Crop(page, 0))
            {
                int offset = 0, calls = 0;
                var region = new Rectangle(5000, 5000, 240, 600);
                using (var form = new ScrollForm(region, first, bounds => { calls++; return Crop(page, offset); }))
                {
                    form.Show();
                    var timer = Field<System.Windows.Forms.Timer>(form, "settle");
                    for (int i = 0; i < 30; i++)
                    {
                        offset += 40; form.NotifyWheel(new Point(5010, 5010));
                        await Task.Delay(30);
                    }
                    var watch = Stopwatch.StartNew();
                    while (timer.Enabled || Field<bool>(form, "capturing"))
                    {
                        if (watch.ElapsedMilliseconds > 8000) throw new TimeoutException("Scroll sampling did not finish.");
                        await Task.Delay(10);
                    }
                    var stitcher = Field<Stitcher>(form, "stitcher");
                    check(calls >= 4 && stitcher.TotalHeight == 1800, "continuous wheel sampling retains intermediate frames and the final settled viewport, captures=" + calls);
                    using (var result = stitcher.Finish()) check(Matches(page, result, 0, 0, 0), "a rapid wheel sequence longer than one viewport produces a complete image");
                    offset = 1450; form.NotifyWheel(new Point(5010, 5010)); await Task.Delay(10);
                    Bitmap completed = null; form.Completed += image => completed = image;
                    await (Task)typeof(ScrollForm).GetMethod("Finish", Private).Invoke(form, null);
                    using (completed) check(completed != null && completed.Height == 2050 && Matches(page, completed, 0, 0, 0), "finishing while a wheel capture is pending includes the final viewport before composition");
                }
            }
        }
    }
}
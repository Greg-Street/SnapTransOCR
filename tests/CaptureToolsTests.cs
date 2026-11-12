using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CaptureToolsTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            SynchronizationContext context = SynchronizationContext.Current;
            try
            {
                Layouts(check);
                using (var overlay = new CaptureForm("", new AppSettings(), 0, new Bitmap(400, 300), new Rectangle(-600, -200, 400, 300)))
                {
                    overlay.PreviewSelection(new Rectangle(30, 40, 140, 140));
                    var canvas = overlay.Controls.OfType<ImageCanvas>().Single();
                    canvas.BeginText(new PointF(15, 20)); canvas.TextInput.Text = "not encoded into GIF";
                    int commits = 0; canvas.Changed += delegate { commits++; };
                    overlay.Complete(CaptureAction.Gif);
                    check(overlay.Captured == null && commits == 0 && overlay.ScreenRegion == new Rectangle(-570, -160, 140, 140), "GIF entry returns the selected physical region without rendering or committing a static screenshot");
                }
                using (var page = ScrollingOptimizationTests.Pattern(240, 1000, 3))
                using (var first = page.Clone(new Rectangle(0, 0, 240, 600), PixelFormat.Format24bppRgb))
                using (var next = page.Clone(new Rectangle(0, 240, 240, 600), PixelFormat.Format24bppRgb))
                using (var stitcher = new Stitcher(first))
                {
                    string message; stitcher.Append(next, out message);
                    using (var preview = stitcher.Preview(new Size(200, 160)))
                        check(preview.Width <= 200 && preview.Height == 160 && stitcher.TotalHeight == 840, "scroll preview stays bounded while representing the entire accumulated image height");
                    using (var preview = stitcher.Preview(new Size(1000, 1000)))
                        check(preview.Size == new Size(240, 840) && preview.GetPixel(100, 700) == page.GetPixel(100, 700), "native-size preview contains the exact appended strip and adds no upscaling");
                    using (var sample = SamplePage())
                    using (var form = new ScrollForm(new Rectangle(0, 0, sample.Width, sample.Height), sample, region => (Bitmap)sample.Clone()))
                    {
                        form.Show(); Application.DoEvents();
                        Render(form, "scroll-capture-preview.png");
                        check(form.Controls.OfType<PictureBox>().Single().Image != null && form.Controls.OfType<Label>().Any(label => label.Text.Contains("等待滚动")), "scroll session opens with an image preview, first-frame count and explicit waiting state");
                        form.Close();
                    }
                }
                using (var pump = new Control())
                using (var app = new ApplicationContext())
                {
                    IntPtr handle = pump.Handle; Exception failure = null;
                    pump.BeginInvoke((Action)(async delegate { try { await Record(check); } catch (Exception ex) { failure = ex; } finally { app.ExitThread(); } }));
                    Application.Run(app); if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = offscreen; SynchronizationContext.SetSynchronizationContext(context); }
        }
        private static async Task Record(Action<bool, string> check)
        {
            await GifQualityTests.Record(check);
            using (var page = ScrollingOptimizationTests.Pattern(240, 1000, 3))
            using (var first = page.Clone(new Rectangle(0, 0, 240, 600), PixelFormat.Format24bppRgb))
            using (var form = new ScrollForm(new Rectangle(-30000, -30000, 240, 600), first, region => page.Clone(new Rectangle(0, 240, 240, 600), PixelFormat.Format24bppRgb)))
            {
                form.Show(); form.Location = new Point(-28000, -28000);
                await (Task)typeof(ScrollForm).GetMethod("CaptureFrame", Private).Invoke(form, null);
                var labels = form.Controls.OfType<Label>().ToArray();
                check(labels.Any(label => label.Text == "2 帧 · 240 × 840 px") && labels.Any(label => label.Text.Contains("已追加 240 像素") && label.ForeColor.G > label.ForeColor.R), "a valid scroll updates the live count, dimensions and green append feedback together");
                await (Task)typeof(ScrollForm).GetMethod("CaptureFrame", Private).Invoke(form, null);
                check(labels.Any(label => label.Text.Contains("画面未变化") && label.ForeColor.R > label.ForeColor.G), "an unchanged scroll frame shows distinct feedback without increasing the accumulated height");
                Bitmap completed = null; form.Completed += image => completed = image;
                await (Task)typeof(ScrollForm).GetMethod("Finish", Private).Invoke(form, null);
                using (completed) check(form.IsDisposed && completed != null && completed.Size == new Size(240, 840) && completed.GetPixel(100, 700) == page.GetPixel(100, 700), "finishing the preview session returns the exact full stitched image and releases its window");
            }
            string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", "gif-recording-example.gif"));
            int captured = 0, saved = 0;
            Func<Rectangle, Bitmap> capture = region =>
            {
                captured++; var image = new Bitmap(region.Width, region.Height);
                using (var graphics = Graphics.FromImage(image)) { graphics.Clear(captured % 2 == 1 ? Color.Coral : Color.RoyalBlue); graphics.DrawString("GIF " + captured, Ui.Font, Brushes.White, 8, 8); }
                return image;
            };
            using (var form = new GifRecorderForm(new Rectangle(-30000, -30000, 160, 100), capture, source => { saved++; GifRecorderForm.SaveCopy(source, output); return true; }))
            {
                form.Show(); Application.DoEvents(); Render(form, "gif-recorder-preview.png");
                form.StartRecording(); StopTimer(form);
                await Pending(form); await Task.Yield();
                var gate = new TaskCompletionSource<bool>();
                typeof(GifRecorderForm).GetField("pending", Private).SetValue(form, gate.Task);
                await form.CaptureNext();
                check(captured == 1, "GIF capture skips a timer tick while the previous frame is busy instead of queuing frames");
                gate.SetResult(true); await Task.Delay(40);
                await form.CaptureNext(); await Task.Delay(40); await form.FinishRecording();
                check(form.IsDisposed && captured == 2 && saved == 1, "GIF stop waits for encoding, saves once and releases the recorder window");
                using (var gif = Image.FromFile(output))
                    check(gif.Size == new Size(160, 100) && gif.GetFrameCount(FrameDimension.Time) == 2, "recorded GIF decodes independently with original dimensions and two changed frames");
                check(typeof(GifRecorderForm).GetField("temporary", Private).GetValue(form) == null, "successful GIF save removes its recording temporary file");
            }
            using (var form = new GifRecorderForm(new Rectangle(-30000, -30000, 160, 100), capture, source => { saved++; return false; }))
            {
                IntPtr handle = form.Handle; form.StartRecording(); StopTimer(form);
                string temporary = (string)typeof(GifRecorderForm).GetField("temporary", Private).GetValue(form);
                form.Close(); await Task.Yield();
                check(form.IsDisposed && !File.Exists(temporary) && saved == 1, "cancelling immediately joins the background encoder and deletes only its unfinished temporary file");
            }
            using (var form = new GifRecorderForm(new Rectangle(-30000, -30000, 160, 100), capture, source => false))
            {
                form.Show(); form.StartRecording(); StopTimer(form); await Pending(form); await Task.Yield();
                await form.FinishRecording();
                check(!form.IsDisposed && typeof(GifRecorderForm).GetField("temporary", Private).GetValue(form) == null, "cancelling the save dialog releases recording storage and leaves a reusable recorder");
                form.StartRecording(); StopTimer(form); await Pending(form); await Task.Yield();
                check((bool)typeof(GifRecorderForm).GetField("recording", Private).GetValue(form), "a new recording can begin after cancelling a GIF save"); form.Close();
            }
            using (var form = new GifRecorderForm(new Rectangle(-30000, -30000, 160, 100), region => { throw new IOException("synthetic capture failure"); }, source => true))
            {
                IntPtr handle = form.Handle; form.StartRecording();
                check(!(bool)typeof(GifRecorderForm).GetField("recording", Private).GetValue(form) && typeof(GifRecorderForm).GetField("temporary", Private).GetValue(form) == null,
                    "a capture failure stops the timer and releases GIF resources without an unhandled async exception"); form.Close();
            }
        }
        private static Task Pending(GifRecorderForm form) { return (Task)typeof(GifRecorderForm).GetField("pending", Private).GetValue(form); }
        private static void StopTimer(GifRecorderForm form) { ((System.Windows.Forms.Timer)typeof(GifRecorderForm).GetField("timer", Private).GetValue(form)).Stop(); }
        private static void Layouts(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            {
                var fonts = new List<Font>();
                try
                {
                    using (var first = new Bitmap(240, 400))
                    using (var scroll = new ScrollForm(new Rectangle(0, 0, 240, 400), first))
                    using (var gif = new GifRecorderForm(new Rectangle(0, 0, 240, 400)))
                    {
                        foreach (Form form in new Form[] { scroll, gif })
                        {
                            SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                            form.ClientSize = new Size(Ui.Scale(form, form is ScrollForm ? 610 : 520), Ui.Scale(form, form is ScrollForm ? 310 : 240)); form.PerformLayout();
                            bool buttonsFit = form.Controls.OfType<FlowLayoutPanel>().All(row => row.Controls.Cast<Control>().All(button => row.ClientRectangle.Contains(button.Bounds)));
                            check(buttonsFit, form.GetType().Name + " capture action buttons remain fully visible at " + dpi + " DPI");
                            check(form.Controls.Cast<Control>().All(control => form.ClientRectangle.Contains(control.Bounds)), form.GetType().Name + " title, status and preview stay within the window at " + dpi + " DPI");
                            if (form is GifRecorderForm)
                            {
                                var quality = (ComboBox)typeof(GifRecorderForm).GetField("quality", Private).GetValue(form);
                                check(quality.Parent.ClientRectangle.Contains(quality.Bounds), "GIF quality selector stays within its row at " + dpi + " DPI: field=" + quality.Bounds + ", row=" + quality.Parent.ClientSize + ", font=" + quality.Font.Size);
                                var labels = form.Controls.OfType<Label>().ToArray();
                                check(labels.All(label => !label.Bounds.IntersectsWith(quality.Parent.Bounds)), "GIF quality selector does not cover the recording status at " + dpi + " DPI");
                                var actions = form.Controls.OfType<FlowLayoutPanel>().Single();
                                check(labels.All(label => label.Bottom <= actions.Top), "GIF output size and recording limits remain above the action buttons at " + dpi + " DPI, labels=" + String.Join(",", labels.Select(label => label.Bounds)) + ", actions=" + actions.Bounds + ", client=" + form.ClientSize);
                            }
                        }
                    }
                }
                finally { foreach (var font in fonts) font.Dispose(); }
            }
        }
        internal static void RunNative(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = false;
            SynchronizationContext context = SynchronizationContext.Current;
            try
            {
                using (var fixture = new Form { Text = "SnapTransOCR GIF isolated fixture", ClientSize = new Size(500, 320), BackColor = Color.Coral, StartPosition = FormStartPosition.CenterScreen, TopMost = true })
                {
                    Exception failure = null;
                    fixture.Shown += async delegate
                    {
                        try
                        {
                            Rectangle region = fixture.RectangleToScreen(new Rectangle(30, 40, 320, 200));
                            string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", "gif-native-desktop.gif"));
                            using (var form = new GifRecorderForm(region, save: source => { GifRecorderForm.SaveCopy(source, output); return true; }))
                            {
                                form.Show(); form.Location = region.Location; Native.DwmFlush(); await Task.Delay(120);
                                bool excluded = (bool)typeof(GifRecorderForm).GetField("excluded", Private).GetValue(form);
                                check(excluded, "Windows excludes the GIF control window from desktop capture even when it overlaps the recorded region");
                                form.StartRecording(); StopTimer(form); await Pending(form); await Task.Yield();
                                fixture.BackColor = Color.RoyalBlue; fixture.Refresh(); Native.DwmFlush(); await Task.Delay(80);
                                await form.CaptureNext(); await form.FinishRecording();
                                using (var gif = Image.FromFile(output))
                                {
                                    check(gif.Size == region.Size && gif.GetFrameCount(FrameDimension.Time) == 2, "real BitBlt desktop capture records both visible fixture updates into a decodable native-size GIF");
                                    using (var pixel = new Bitmap(gif.Width, gif.Height))
                                    {
                                        gif.SelectActiveFrame(FrameDimension.Time, 0); using (var graphics = Graphics.FromImage(pixel)) graphics.DrawImageUnscaled(gif, 0, 0);
                                        Color first = pixel.GetPixel(200, 150);
                                        gif.SelectActiveFrame(FrameDimension.Time, 1); using (var graphics = Graphics.FromImage(pixel)) graphics.DrawImageUnscaled(gif, 0, 0);
                                        Color second = pixel.GetPixel(200, 150);
                                        check(first.R > first.B + 50 && second.B > second.R + 50, "overlapping recorder controls and range outlines are absent from both independently decoded screen frames");
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { fixture.Close(); }
                    };
                    Application.Run(fixture); if (failure != null) throw failure;
                }
            }
            finally { Ui.RenderOffscreen = offscreen; SynchronizationContext.SetSynchronizationContext(context); }
        }
        private static void Render(Form form, string name)
        {
            using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            { form.DrawToBitmap(image, form.ClientRectangle); image.Save(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", name))); }
        }
        private static Bitmap SamplePage()
        {
            var image = new Bitmap(420, 720);
            using (var graphics = Graphics.FromImage(image))
            using (var heading = new Font("Segoe UI", 20, FontStyle.Bold))
            using (var body = new Font("Segoe UI", 11))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("SnapTransOCR", heading, Brushes.RoyalBlue, 24, 22);
                graphics.DrawString("Scrolling capture preview", body, Brushes.DimGray, 24, 68);
                for (int index = 0; index < 7; index++)
                {
                    int y = 120 + index * 80;
                    graphics.DrawString("0" + (index + 1) + "   Sample document section", body, Brushes.Black, 24, y);
                    graphics.FillRectangle(Brushes.Gainsboro, 24, y + 28, 350 - index % 3 * 45, 6);
                    graphics.FillRectangle(Brushes.Gainsboro, 24, y + 42, 310 - index % 2 * 55, 6);
                }
            }
            return image;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    // Synthetic UI only. Default mode never generates input; --auto drives its own verified edges.
    internal static class PassiveResizeProbe
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetCapture();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder value, int count);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; public Rectangle Value { get { return Rectangle.FromLTRB(L, T, R, B); } } }
        sealed class Patch { public string Name; public IntPtr Handle; public bool CompareText; public int Width, Height; public readonly List<Point> Mask = new List<Point>(); }
        sealed class NativeTiming { public int Message, WParam; public double Ms; }
        sealed class Drag
        {
            public int Number, Directions, Resizes; public Rectangle Initial, Final; public long Started = Stopwatch.GetTimestamp();
            public volatile bool Active = true; public readonly ConcurrentQueue<NativeTiming> Timings = new ConcurrentQueue<NativeTiming>();
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);
        }
        static readonly ConcurrentQueue<Drag> pending = new ConcurrentQueue<Drag>();
        static readonly ConcurrentQueue<string> inputTrace = new ConcurrentQueue<string>();
        static void Trace(string value) { inputTrace.Enqueue(Stopwatch.GetTimestamp() + " " + value); }
        static readonly ManualResetEvent ready = new ManualResetEvent(false);
        static readonly ManualResetEvent sizingEntered = new ManualResetEvent(false);
        static volatile Drag active;
        static volatile bool stopping;
        static IntPtr window, fixtureWindow;
        static Rectangle fixtureBounds;
        static List<Patch> patches;
        static string output;
        static int inset, interiorInset, nextDrag;
        static bool verify;
        static Exception failure;
        static double Milliseconds(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
        static string N(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
        static string R(Rectangle value) { return value.X + ";" + value.Y + ";" + value.Width + ";" + value.Height; }
        static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        static Form CreateProbe(string[] args)
        {
            const string text = "Resize probe 文字 ABC\n这是一段用于缩放检查的合成中文。\nEnglish source text stays selectable.";
            var settings = new AppSettings { HistoryEnabled = false, TranslateAutoHeight = false, TranslateWindowWidth = 520, TranslateWindowHeight = 650, TranslateTopmost = true, TranslateHideOnBlur = false };
            // Reflection keeps the default probe compilable against pre-Bing sources.
            if (args.Contains("--bing")) typeof(AppSettings).GetProperty("BingTranslateEnabled").SetValue(settings, true, null);
            Form result;
            if (args.Contains("--pin"))
            {
                result = new PinForm(SyntheticImage(520, 650), true, null, new Point(200, 100));
            }
            else if (args.Contains("--settings") || args.Contains("--history"))
            {
                var history = new HistoryStore(Path.Combine(output, "synthetic-history-" + Guid.NewGuid().ToString("N")));
                if (args.Contains("--history"))
                {
                    using (Bitmap image = SyntheticImage(320, 180))
                        history.AddAsync(new HistoryEntry { Kind = "image_translation", Source = text, Translation = "Synthetic history translation.\n用于历史页面缩放检查的译文。", OcrProvider = "tencent", TranslateProvider = "volc" }, image).GetAwaiter().GetResult();
                }
                var settingsForm = new SettingsForm(settings, delegate { }, history) { TopMost = true };
                settingsForm.SelectPage(args.Contains("--history") ? "历史记录" : "识别设置"); result = settingsForm;
            }
            else if (args.Contains("--ocr"))
            {
                var ocr = new OcrForm { Exiting = true, TopMost = true };
                ocr.RefreshSettings(settings); ocr.SetSource(text); result = ocr;
            }
            else
            {
                var translation = new MainForm { Exiting = true };
                translation.RefreshSettings(settings); translation.SetSource(text);
                translation.SetActiveProvider("volc"); translation.SetOutput("Synthetic translation one.\n这段译文只用于观察窗口绘制。\nResize from each edge and watch the text.");
                translation.SetActiveProvider("baidu"); translation.SetOutput("Synthetic translation two.\n第二项结果保持展开，不进行网络请求。");
                if (args.Contains("--bing")) { translation.SetActiveProvider("bing"); translation.SetOutput("Synthetic Bing translation.\n第三项合成结果，不进行网络请求。"); }
                result = translation;
            }
            result.Text = "SnapTransOCR Resize Probe"; result.StartPosition = FormStartPosition.Manual;
            if (result is SettingsForm)
            {
                int x = Math.Max(fixtureBounds.Left + 16, Math.Min(fixtureBounds.Left + 200, fixtureBounds.Right - 1080));
                int y = Math.Max(fixtureBounds.Top + 16, Math.Min(fixtureBounds.Top + 100, fixtureBounds.Bottom - 810));
                var size = new Size(Math.Min(1000, fixtureBounds.Right - x - 80), Math.Min(730, fixtureBounds.Bottom - y - 80));
                result.MinimumSize = new Size(Math.Min(result.MinimumSize.Width, size.Width), Math.Min(result.MinimumSize.Height, size.Height));
                result.Bounds = new Rectangle(new Point(x, y), size);
            }
            else result.Bounds = new Rectangle(200, 100, 520, 650);
            return result;
        }
        static Bitmap SyntheticImage(int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Color.FromArgb(211, 227, 245)))
            using (var font = new Font("Microsoft YaHei UI", 14))
            {
                graphics.Clear(Color.FromArgb(251, 246, 232));
                graphics.FillRectangle(brush, 12, 12, width - 24, height / 3);
                graphics.DrawRectangle(Pens.SteelBlue, 6, 6, width - 13, height - 13);
                graphics.DrawString("Synthetic image\n合成图像 · Resize probe", font, Brushes.Black, new RectangleF(24, 28, width - 48, height - 56));
            }
            return bitmap;
        }
        static async Task WaitForHistoryPreview(SettingsForm form)
        {
            HistoryView history = Field<HistoryView>(form, "historyView");
            PictureBox preview = Field<PictureBox>(history, "preview");
            RichTextBox source = Field<RichTextBox>(history, "source");
            while (!form.IsDisposed && (preview.Image == null || source.TextLength == 0))
            {
                string status = Field<Label>(history, "status").Text + Field<Label>(history, "captureSummary").Text;
                if (status.Contains("暂时") || status.Contains("不存在")) throw new InvalidOperationException("Synthetic history did not load: " + status);
                await Task.Delay(16);
            }
        }
        static IEnumerable<Control> All(Control root)
        {
            foreach (Control child in root.Controls) { yield return child; foreach (Control descendant in All(child)) yield return descendant; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            string label = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "passive";
            verify = args.Contains("--verify");
            output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, label + "-frames");
            Directory.CreateDirectory(output);
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Program.AppIcon = SystemIcons.Application;
            fixtureBounds = Screen.PrimaryScreen.Bounds;
            Thread sampler = null;
            using (var fixture = new Form { Text = "SnapTransOCR Resize Probe Background", FormBorderStyle = FormBorderStyle.None, Bounds = fixtureBounds, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false, BackColor = Color.Lime })
            using (var form = CreateProbe(args))
            {
                fixture.Shown += async delegate
                {
                    try
                    {
                    form.Show(fixture); form.Activate(); form.Refresh(); Native.DwmFlush();
                    if (args.Contains("--history")) { await WaitForHistoryPreview((SettingsForm)form); form.Refresh(); Native.DwmFlush(); }
                    window = form.Handle; fixtureWindow = fixture.Handle; inset = Ui.Scale(form, 12);
                    interiorInset = Math.Max(0, Ui.Scale(form, 32) - inset);
                    if (form is SettingsForm)
                    {
                        patches = new List<Patch> {
                            new Patch { Name = "page_title", Handle = Field<Label>(form, "pageTitle").Handle, CompareText = true, Width = Ui.Scale(form, 220), Height = Ui.Scale(form, 32) }
                        };
                        if (args.Contains("--history"))
                        {
                            HistoryView history = Field<HistoryView>(form, "historyView");
                            patches.Add(new Patch { Name = "history_source", Handle = Field<RichTextBox>(history, "source").Handle });
                            patches.Add(new Patch { Name = "history_preview", Handle = Field<PictureBox>(history, "preview").Handle });
                        }
                        else
                        {
                            Label description = All(form).OfType<Label>().First(control => control.Visible && control.Text == "默认识别服务");
                            patches.Add(new Patch { Name = "ocr_label", Handle = description.Handle, CompareText = true, Width = Ui.Scale(form, 200), Height = Ui.Scale(form, 24) });
                        }
                    }
                    else if (form is PinForm) patches = new List<Patch>();
                    else patches = new List<Patch> {
                            new Patch { Name = "caption", Handle = Field<Label>(form, "caption").Handle, CompareText = true, Width = Ui.Scale(form, 60), Height = Ui.Scale(form, 24) },
                            new Patch { Name = "source", Handle = Field<RichTextBox>(form, "source").Handle, CompareText = true, Width = Ui.Scale(form, 220), Height = Ui.Scale(form, 24) }
                        };
                    if (form is MainForm)
                    {
                        patches.Add(new Patch { Name = "language_from", Handle = Field<ComboBox>(form, "from").Handle });
                        patches.Add(new Patch { Name = "language_to", Handle = Field<ComboBox>(form, "to").Handle });
                    }
                    var hook = new MessageProbe(form);
                    form.FormClosed += delegate { stopping = true; hook.ReleaseHandle(); fixture.BeginInvoke(new Action(fixture.Close)); };
                    sampler = new Thread(SampleLoop) { IsBackground = true, Name = "Passive synthetic resize sampler" }; sampler.Start();
                    if (args.Contains("--auto")) _ = Task.Run(() => AutoRun(form, args.Contains("--corner")));
                    }
                    catch (Exception e) { failure = e; form.Close(); fixture.BeginInvoke(new Action(fixture.Close)); }
                };
                Application.Run(fixture);
                stopping = true;
                if (sampler != null) sampler.Join();
            }
            File.WriteAllLines(Path.Combine(output, "input-trace.log"), inputTrace.ToArray());
            if (failure != null) { File.WriteAllText(Path.Combine(output, "failure.txt"), failure.ToString()); Console.Error.WriteLine(failure); Environment.ExitCode = 1; }
            else Console.WriteLine("DONE output=" + output + " drags=" + nextDrag);
        }

        sealed class MessageProbe : NativeWindow
        {
            readonly Form form;
            public MessageProbe(Form form) { this.form = form; AssignHandle(form.Handle); }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0xA1 || m.Msg == 0xA2 || m.Msg == 0xA3 || m.Msg == 0x201 || m.Msg == 0x202 || m.Msg == 0x112 || m.Msg == 0x231 || m.Msg == 0x232 || m.Msg == 0x215 || m.Msg == 0x1f)
                    Trace("message=" + m.Msg.ToString("X") + " wparam=" + m.WParam + " capture=" + GetCapture() + " cursor=" + Cursor.Position);
                if (m.Msg == 0x231) { active = new Drag { Number = ++nextDrag, Initial = form.Bounds }; pending.Enqueue(active); }
                Drag drag = active;
                if (drag != null && drag.Active && m.Msg == 0x214) drag.Directions |= 1 << m.WParam.ToInt32();
                if (drag != null && drag.Active && m.Msg == 5) Interlocked.Increment(ref drag.Resizes);
                long start = Stopwatch.GetTimestamp(); base.WndProc(ref m);
                if (m.Msg == 0x231) sizingEntered.Set();
                if (drag != null && drag.Active && (m.Msg == 5 || m.Msg == 0x47 || m.Msg == 15 || m.Msg == 20 || m.Msg == 0x214))
                    drag.Timings.Enqueue(new NativeTiming { Message = m.Msg, WParam = m.WParam.ToInt32(), Ms = Milliseconds(Stopwatch.GetTimestamp() - start) });
                if (drag != null && m.Msg == 0x232) { drag.Final = form.Bounds; drag.Active = false; }
            }
        }

        static Rectangle Bounds(IntPtr hwnd) { Rect bounds; return GetWindowRect(hwnd, out bounds) ? bounds.Value : Rectangle.Empty; }
        static Rectangle CaptureBounds()
        {
            Rect rect; Point origin = Point.Empty;
            if (!GetClientRect(window, out rect) || !ClientToScreen(window, ref origin)) return Rectangle.Empty;
            Rectangle area = rect.Value; area.Offset(origin); area.Inflate(-inset, -inset); return area;
        }
        static bool Safe(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || !fixtureBounds.Contains(bounds) || !IsWindowVisible(window) || GetAncestor(GetForegroundWindow(), 2) != window) return false;
            // Reject occlusion by any unrelated HWND before and after every saved screen read.
            for (int y = bounds.Top; y < bounds.Bottom; y += Math.Max(1, bounds.Height / 5))
                for (int x = bounds.Left; x < bounds.Right; x += Math.Max(1, bounds.Width / 5))
                { IntPtr root = GetAncestor(WindowFromPoint(new Point(x, y)), 2); if (root != window && root != fixtureWindow) return false; }
            return true;
        }
        static Rectangle PatchBounds(Patch patch)
        {
            Rectangle b = Bounds(patch.Handle);
            if (patch.CompareText) { b.Width = Math.Min(b.Width, patch.Width); b.Height = Math.Min(b.Height, patch.Height); }
            return b;
        }
        static byte[] Pixels(Bitmap bitmap)
        {
            var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { var bytes = new byte[bitmap.Width * bitmap.Height * 4]; for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), bytes, y * bitmap.Width * 4, bitmap.Width * 4); return bytes; }
            finally { bitmap.UnlockBits(bits); }
        }
        static bool Dark(byte[] data, int index) { return data[index] < 180 && data[index + 1] < 180 && data[index + 2] < 180; }
        static bool Green(byte[] data, int index) { return data[index] == 0 && data[index + 1] == 255 && data[index + 2] == 0; }
        static void SampleLoop()
        {
            try
            {
                // The initial reference is captured on this worker, after the UI's normal first paint.
                while (!stopping)
                {
                    Rectangle area = CaptureBounds();
                    if (!Safe(area)) { Thread.Sleep(8); continue; }
                    using (Bitmap image = ScreenCapture.Desktop(area))
                    {
                        if (!Safe(area)) continue;
                        byte[] data = Pixels(image);
                        foreach (Patch patch in patches.Where(p => p.CompareText))
                        {
                            Rectangle box = PatchBounds(patch); box.Offset(-area.X, -area.Y);
                            for (int y = 0; y < box.Height; y++) for (int x = 0; x < box.Width; x++)
                            { Point pixel = new Point(box.X + x, box.Y + y); if (pixel.X >= 0 && pixel.Y >= 0 && pixel.X < image.Width && pixel.Y < image.Height && Dark(data, (pixel.Y * image.Width + pixel.X) * 4)) patch.Mask.Add(new Point(x, y)); }
                            if (patch.Mask.Count < 10) throw new InvalidOperationException("Insufficient reference pixels: " + patch.Name);
                        }
                        image.Save(Path.Combine(output, "reference.png"));
                    }
                    File.WriteAllText(Path.Combine(output, "ready.txt"), "pid=" + Process.GetCurrentProcess().Id + " hwnd=" + window + " bounds=" + R(Bounds(window)));
                    Console.WriteLine("READY " + output); ready.Set(); break;
                }
                Drag drag;
                while (!stopping || !pending.IsEmpty)
                {
                    if (pending.TryDequeue(out drag)) Record(drag); else Thread.Sleep(8);
                }
            }
            catch (Exception e)
            {
                failure = e; ready.Set();
                Drag current = active;
                if (current != null) current.Finished.Set();
            }
        }
        static void Record(Drag drag)
        {
            string prefix = Path.Combine(output, "drag-" + drag.Number.ToString("D2"));
            int frames = 0, skipped = 0, greenFrames = 0, interiorGreenFrames = 0, missingFrames = 0; double worstMissing = -1; int worstGreen = -1, worstInteriorGreen = -1;
            Bitmap worstTextImage = null, worstGreenImage = null, last = null;
            using (var csv = new StreamWriter(prefix + ".csv", false, new UTF8Encoding(false)))
            {
                csv.WriteLine("frame,elapsed_ms,sample_ms,phase,window_bounds,green_pixels,interior_green_pixels" + (patches.Count == 0 ? "" : "," + string.Join(",", patches.Select(p => p.Name + (p.CompareText ? "_missing" : "_green")))));
                do
                {
                    long start = Stopwatch.GetTimestamp(); bool during = drag.Active;
                    Rectangle area = CaptureBounds(); Rectangle actual = Bounds(window);
                    Rectangle[] boxes = patches.Select(PatchBounds).ToArray();
                    if (!Safe(area)) { skipped++; if (!during) break; Thread.Sleep(8); continue; }
                    using (Bitmap image = ScreenCapture.Desktop(area))
                    {
                        if (!Safe(area) || Bounds(window) != actual) { skipped++; continue; }
                        int pixelWidth = image.Width, pixelHeight = image.Height; byte[] data = Pixels(image); int green = 0, interiorGreen = 0; double maximumMissing = 0;
                        for (int i = 0; i < data.Length; i += 4) if (Green(data, i)) green++;
                        for (int y = interiorInset; y < pixelHeight - interiorInset; y++)
                            for (int x = interiorInset; x < pixelWidth - interiorInset; x++) if (Green(data, (y * pixelWidth + x) * 4)) interiorGreen++;
                        var values = new List<string>();
                        for (int p = 0; p < patches.Count; p++)
                        {
                            Patch patch = patches[p]; Rectangle box = boxes[p]; box.Offset(-area.X, -area.Y);
                            if (patch.CompareText)
                            {
                                int missing = 0, valid = 0;
                                foreach (Point pixel in patch.Mask)
                                {
                                    int x = box.X + pixel.X, y = box.Y + pixel.Y;
                                    if (x < 0 || y < 0 || x >= pixelWidth || y >= pixelHeight) continue;
                                    valid++; if (!Dark(data, (y * pixelWidth + x) * 4)) missing++;
                                }
                                double ratio = valid > 0 ? (double)missing / valid : 1; maximumMissing = Math.Max(maximumMissing, ratio); values.Add(N(ratio));
                            }
                            else
                            {
                                int count = 0; box.Intersect(new Rectangle(Point.Empty, image.Size));
                                for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++) if (Green(data, (y * pixelWidth + x) * 4)) count++;
                                values.Add(count.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                        frames++; if (green > 0) greenFrames++; if (interiorGreen > 0) interiorGreenFrames++; if (maximumMissing > .5) missingFrames++;
                        worstInteriorGreen = Math.Max(worstInteriorGreen, interiorGreen);
                        if (frames == 1) image.Save(prefix + "-first.png");
                        if (maximumMissing > worstMissing) { worstMissing = maximumMissing; if (worstTextImage != null) worstTextImage.Dispose(); worstTextImage = (Bitmap)image.Clone(); }
                        if (green > worstGreen) { worstGreen = green; if (worstGreenImage != null) worstGreenImage.Dispose(); worstGreenImage = (Bitmap)image.Clone(); }
                        if (last != null) last.Dispose(); last = (Bitmap)image.Clone();
                        csv.WriteLine(frames + "," + N(Milliseconds(start - drag.Started)) + "," + N(Milliseconds(Stopwatch.GetTimestamp() - start)) + "," + (during ? "drag" : "end") + "," + R(actual) + "," + green + "," + interiorGreen + (values.Count == 0 ? "" : "," + string.Join(",", values)));
                    }
                    if (!during || stopping) break;
                    int delay = Math.Max(0, 8 - (int)Milliseconds(Stopwatch.GetTimestamp() - start)); if (delay > 0) Thread.Sleep(delay);
                } while (true);
            }
            if (last != null) { last.Save(prefix + "-final.png"); last.Dispose(); }
            if (worstTextImage != null) { worstTextImage.Save(prefix + "-worst-text.png"); worstTextImage.Dispose(); }
            if (worstGreenImage != null) { worstGreenImage.Save(prefix + "-worst-green.png"); worstGreenImage.Dispose(); }
            NativeTiming[] timings = drag.Timings.ToArray();
            File.WriteAllLines(prefix + "-messages.csv", new[] { "message_hex,wparam,duration_ms" }.Concat(timings.Select(t => t.Message.ToString("X") + "," + t.WParam + "," + N(t.Ms))));
            string summary = "initial=" + R(drag.Initial) + " final=" + R(drag.Final) + " directions=" + drag.Directions + " resizes=" + drag.Resizes + " samples=" + frames + " skipped=" + skipped + " green_samples=" + greenFrames + " interior_green_samples=" + interiorGreenFrames + " missing_samples=" + missingFrames + " worst_missing=" + N(worstMissing) + " worst_green=" + worstGreen + " worst_interior_green=" + worstInteriorGreen;
            foreach (var group in timings.GroupBy(t => t.Message)) { double[] sorted = group.Select(t => t.Ms).OrderBy(t => t).ToArray(); summary += Environment.NewLine + "msg=" + group.Key.ToString("X") + " count=" + sorted.Length + " median_ms=" + N(sorted[sorted.Length / 2]) + " p95_ms=" + N(sorted[(int)((sorted.Length - 1) * .95)]) + " max_ms=" + N(sorted[sorted.Length - 1]); }
            File.WriteAllText(prefix + "-summary.txt", summary); Console.WriteLine("DRAG " + drag.Number + " " + summary);
            if (verify && (frames == 0 || missingFrames > 0 || interiorGreenFrames > 0) && failure == null)
                failure = new InvalidOperationException("Visual verification failed for drag " + drag.Number + ": samples=" + frames + " missing_samples=" + missingFrames + " interior_green_samples=" + interiorGreenFrames);
            drag.Finished.Set();
        }
        static void AutoRun(Form form, bool corner)
        {
            Point original = Cursor.Position; bool down = false;
            try
            {
                ready.WaitOne(); if (failure != null) throw new InvalidOperationException("Sampler initialization failed.", failure);
                foreach (int edge in corner ? new[] { 17 } : new[] { 11, 15 })
                {
                    Thread.Sleep(180);
                    bool horizontal = edge != 15, vertical = edge != 11;
                    int edgeInset = edge == 17 ? Math.Max(6, inset / 2) : 2;
                    Rectangle initial = Bounds(window); int x = horizontal ? initial.Right - edgeInset : initial.Left + initial.Width / 2, y = vertical ? initial.Bottom - edgeInset : initial.Top + initial.Height / 2;
                    SetCursorPos(x, y); Thread.Sleep(70);
                    int hit = Native.SendMessage(window, 0x84, IntPtr.Zero, new IntPtr((y << 16) | (x & 0xffff))).ToInt32();
                    IntPtr pointerWindow = WindowFromPoint(new Point(x, y)); var className = new StringBuilder(200); GetClassName(pointerWindow, className, className.Capacity);
                    Trace("inject_down edge=" + edge + " target=" + window + " pointerWindow=" + pointerWindow + " class=" + className + " cursor=" + Cursor.Position + " leftDown=" + GetAsyncKeyState(1) + " foreground=" + GetForegroundWindow());
                    if (GetAncestor(WindowFromPoint(new Point(x, y)), 2) != window || hit != edge) throw new InvalidOperationException("Probe edge failed ownership / hit test: expected=" + edge + " actual=" + hit);
                    int distance = Math.Min(192, Math.Min(horizontal ? fixtureBounds.Right - initial.Right - 16 : int.MaxValue, vertical ? fixtureBounds.Bottom - initial.Bottom - 16 : int.MaxValue));
                    if (form is PinForm)
                    {
                        // A plain pin preserves the image aspect ratio even when only one edge moves.
                        int coupledRoom = horizontal ? (fixtureBounds.Bottom - initial.Bottom - 16) * initial.Width / initial.Height : (fixtureBounds.Right - initial.Right - 16) * initial.Height / initial.Width;
                        distance = Math.Min(distance, coupledRoom);
                    }
                    if (distance < 48) throw new InvalidOperationException("Fixture has insufficient resize room.");
                    sizingEntered.Reset(); mouse_event(2, 0, 0, 0, UIntPtr.Zero); down = true;
                    if (!sizingEntered.WaitOne(2000)) throw new InvalidOperationException("Injected press did not enter native sizing; movement was not attempted.");
                    for (int i = 1; i <= 24; i++) { SetCursorPos(x + (horizontal ? distance * i / 24 : 0), y + (vertical ? distance * i / 24 : 0)); Thread.Sleep(16); }
                    for (int i = 23; i >= 0; i--) { SetCursorPos(x + (horizontal ? distance * i / 24 : 0), y + (vertical ? distance * i / 24 : 0)); Thread.Sleep(16); }
                    Drag drag = active; Trace("inject_up edge=" + edge + " cursor=" + Cursor.Position + " leftDown=" + GetAsyncKeyState(1)); mouse_event(4, 0, 0, 0, UIntPtr.Zero); down = false;
                    if (drag == null) throw new InvalidOperationException("No native sizing messages received.");
                    drag.Finished.WaitOne();
                    if (drag.Directions != (edge == 17 ? 256 : edge == 11 ? 4 : 64) || drag.Resizes < 10 || drag.Final != initial) throw new InvalidOperationException("Incomplete native drag: expected edge=" + edge + " initial=" + R(initial) + " directions=" + drag.Directions + " resizes=" + drag.Resizes + " final=" + R(drag.Final));
                }
            }
            catch (Exception e) { failure = e; }
            finally { if (down) mouse_event(4, 0, 0, 0, UIntPtr.Zero); Cursor.Position = original; if (!form.IsDisposed) form.BeginInvoke(new Action(form.Close)); }
        }
    }
}

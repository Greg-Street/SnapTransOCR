using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class Stitcher : IDisposable
    {
        private const int Columns = 64;
        private readonly List<Bitmap> pieces = new List<Bitmap>();
        private byte[] previous;
        private readonly int width, height;
        public int TotalHeight { get; private set; }
        public int FrameCount { get { return pieces.Count; } }
        public Stitcher(Bitmap first)
        {
            width = first.Width;
            height = first.Height;
            if (width < 80 || height < 120) throw new InvalidOperationException("长截图选区至少需要 80 × 120 像素，请扩大选区。");
            if ((long)width * height > 50000000) throw new InvalidOperationException("长截图选区过大，请缩小后重试。");
            previous = Sample(first);
            pieces.Add((Bitmap)first.Clone());
            TotalHeight = height;
        }
        private static byte[] Sample(Bitmap bitmap)
        {
            int sampleWidth = bitmap.Width, sampleHeight = bitmap.Height;
            PixelFormat format = bitmap.PixelFormat;
            if (format != PixelFormat.Format24bppRgb && format != PixelFormat.Format32bppRgb)
            {
                using (var rgb = new Bitmap(sampleWidth, sampleHeight, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(rgb)) graphics.DrawImageUnscaled(bitmap, 0, 0);
                    return Sample(rgb);
                }
            }
            int channels = Image.GetPixelFormatSize(format) / 8;
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, sampleWidth, sampleHeight), ImageLockMode.ReadOnly, format);
            try
            {
                byte[] sampled = new byte[sampleHeight * Columns * 3];
                byte[] row = new byte[Math.Abs(data.Stride)];
                int[] columns = new int[Columns];
                for (int x = 0; x < Columns; x++) columns[x] = (int)((0.04 + 0.90 * x / (Columns - 1)) * (sampleWidth - 1)) * channels;
                for (int y = 0; y < sampleHeight; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < Columns; x++)
                    {
                        int pixel = columns[x];
                        for (int c = 0; c < 3; c++) sampled[(y * Columns + x) * 3 + c] = row[pixel + c];
                    }
                }
                return sampled;
            }
            finally { bitmap.UnlockBits(data); }
        }
        public bool Append(Bitmap frame, out string message)
        {
            if (frame.Width != width || frame.Height != height) throw new InvalidOperationException("长截图期间选区尺寸发生变化，请重新开始。");
            byte[] current = Sample(frame);
            double still = Difference(previous, current, 0, 2.8, true);
            if (still < 0.18) { message = "画面未变化，未追加"; return false; }
            int minimumOverlap = Math.Max(70, height / 4);
            double best = still;
            int shift = 0;
            bool ambiguous = false;
            for (int offset = 1; offset <= height - minimumOverlap; offset++)
            {
                double score = Difference(previous, current, offset, Math.Min(best, 2.8));
                if (score < best) { best = score; shift = offset; ambiguous = false; }
                else if (score == best && score <= 2.8) ambiguous = true;
            }
            // Only append when enough textured rows match; blank/repeating backgrounds are ambiguous.
            int texture = 0;
            for (int y = 12; y < height - shift - 12; y += 3)
            {
                int row = y * Columns * 3;
                for (int x = 3; x < Columns * 3; x += 3)
                    if (Math.Abs(current[row + x] - current[row + x - 3]) > 18 ||
                        Math.Abs(current[row + x + 1] - current[row + x - 2]) > 18 ||
                        Math.Abs(current[row + x + 2] - current[row + x - 1]) > 18) texture++;
            }
            double verified = shift == 0 ? Double.MaxValue : Difference(previous, current, shift, Math.Min(still, 2.8), true);
            if (best > 2.8 || ambiguous || texture < 24 || verified > 2.8 || verified >= still)
            {
                message = "重叠不足或画面变化，未追加";
                return false;
            }
            if (pieces.Count >= 60 || TotalHeight + shift > 24000 || (long)width * (TotalHeight + shift) > 50000000)
                throw new InvalidOperationException("已达到长截图上限（60 帧 / 24000 像素高 / 5000 万像素），请先完成并保存。");
            pieces.Add(frame.Clone(new Rectangle(0, height - shift, width, shift), PixelFormat.Format24bppRgb));
            TotalHeight += shift;
            previous = current;
            message = "已追加 " + shift + " 像素，共 " + TotalHeight + " 像素高";
            return true;
        }
        private double Difference(byte[] first, byte[] second, int shift, double limit, bool everyRow = false)
        {
            int top = Math.Max(8, height / 25), bottom = height - shift - top;
            int step = everyRow ? 1 : Math.Max(1, (bottom - top) / 80);
            int samples = ((bottom - top + step - 1) / step) * Columns * 3;
            if (samples <= 0) return Double.MaxValue;
            double maximumError = Math.Ceiling(limit * samples);
            long error = 0;
            for (int y = top; y < bottom; y += step)
            {
                int a = (y + shift) * Columns * 3, b = y * Columns * 3;
                for (int i = 0; i < Columns * 3; i++) error += Math.Abs(first[a + i] - second[b + i]);
                // Remaining errors cannot be negative, so this offset can no longer qualify.
                if (error > maximumError) return Double.MaxValue;
            }
            return (double)error / samples;
        }
        public Bitmap Finish()
        {
            var result = new Bitmap(width, TotalHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(result))
            {
                int y = 0;
                foreach (var piece in pieces) { graphics.DrawImageUnscaled(piece, 0, y); y += piece.Height; }
            }
            return result;
        }
        public void Dispose() { foreach (var piece in pieces) piece.Dispose(); pieces.Clear(); previous = null; }
    }

    internal sealed class ScrollForm : Form
    {
        private readonly Rectangle region;
        private readonly Func<Rectangle, Bitmap> captureFrame;
        private readonly Stitcher stitcher;
        private readonly Timer settle;
        private readonly ScrollWheelHook hook;
        private readonly Label status;
        private bool capturing, finishing, closeRequested;
        private readonly List<int> registered = new List<int>();
        public event Action<Bitmap> Completed;
        protected override bool ShowWithoutActivation { get { return true; } }
        public ScrollForm(Rectangle rectangle, Bitmap first, Func<Rectangle, Bitmap> captureFrame = null)
        {
            Ui.Setup(this);
            region = rectangle;
            this.captureFrame = captureFrame ?? (bounds => ScreenCapture.Desktop(bounds));
            stitcher = new Stitcher(first);
            Text = "SnapTransOCR · 滚动长截图";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            TopMost = true;
            ShowInTaskbar = true;
            ClientSize = new Size(620, 104);
            var row = Ui.Row();
            row.Controls.Add(Ui.Button("追加帧 F9", async delegate { await CaptureFrame(); }));
            row.Controls.Add(Ui.Button("完成 F8", async delegate { await Finish(); }, true));
            row.Controls.Add(Ui.Button("取消 Esc", Close));
            status = Ui.Label("1 帧");
            status.Dock = DockStyle.Fill;
            status.Padding = new Padding(12, 4, 8, 0);
            status.AutoSize = false;
            Controls.Add(status);
            Controls.Add(row);
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.FromRectangle(region).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(area.Right - Width, region.Right - Width)), region.Bottom + Height < area.Bottom ? region.Bottom + 4 : region.Top - Height > area.Top ? region.Top - Height - 4 : area.Bottom - Height - 8);
            settle = new Timer { Interval = 420 };
            settle.Tick += async delegate { settle.Stop(); await CaptureFrame(); };
            hook = new ScrollWheelHook();
            hook.Wheel += delegate(Point point)
            {
                if (!region.Contains(point) || IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { if (!capturing && !finishing) { settle.Stop(); settle.Start(); } });
            };
            Shown += delegate
            {
                foreach (var key in new[] { new[] { 70, 0x78 }, new[] { 71, 0x77 }, new[] { 72, 0x1b } })
                    if (Native.RegisterHotKey(Handle, key[0], 0x4000, (uint)key[1])) registered.Add(key[0]);
                if (registered.Count != 3) status.Text = "部分按键被占用";
            };
        }
        private async Task CaptureFrame()
        {
            if (capturing || finishing || closeRequested || IsDisposed) return;
            capturing = true;
            try
            {
                bool overlaps = Bounds.IntersectsWith(region);
                if (overlaps) { Hide(); Native.DwmFlush(); await Task.Delay(100); }
                if (IsDisposed || finishing || closeRequested) return;
                using (var frame = captureFrame(region))
                {
                    string message = "";
                    await Task.Run(delegate { stitcher.Append(frame, out message); });
                    if (!IsDisposed) status.Text = stitcher.FrameCount + " 帧 · " + message;
                }
                if (overlaps && !IsDisposed && !closeRequested) Show();
            }
            catch (Exception ex) { if (!IsDisposed && !closeRequested) { Show(); Ui.Error(this, ex); } }
            finally { capturing = false; if (closeRequested && !finishing) Close(); }
        }
        private async Task Finish()
        {
            if (finishing || closeRequested || IsDisposed) return;
            finishing = true;
            settle.Stop();
            Enabled = false;
            bool completed = false;
            try
            {
                while (capturing) { await Task.Delay(30); if (IsDisposed) return; }
                if (closeRequested) return;
                Bitmap image = await Task.Run(delegate { return stitcher.Finish(); });
                if (IsDisposed || closeRequested) { image.Dispose(); return; }
                if (Completed != null) Completed(image); else image.Dispose();
                completed = true;
            }
            catch (Exception ex) { if (!IsDisposed && !closeRequested) Ui.Error(this, ex); }
            finally
            {
                finishing = false;
                if (completed || closeRequested) Close();
                else if (!IsDisposed) Enabled = true;
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x312)
            {
                int id = m.WParam.ToInt32();
                if (id == 70) { var task = CaptureFrame(); }
                else if (id == 71) { var task = Finish(); }
                else if (id == 72) Close();
            }
            base.WndProc(ref m);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Capture and composition workers own the bitmaps until their continuations return.
            if (capturing || finishing) { e.Cancel = true; closeRequested = true; settle.Stop(); Hide(); }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (IsHandleCreated) foreach (int id in registered) Native.UnregisterHotKey(Handle, id);
                registered.Clear();
                settle.Dispose(); hook.Dispose(); stitcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

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
        private int frameCount = 1;
        private int viewportTop, rangeTop;
        public int TotalHeight { get; private set; }
        public int FrameCount { get { return frameCount; } }
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
            int fixedTop = FixedEdge(previous, current, false), fixedBottom = FixedEdge(previous, current, true);
            int minimumOverlap = Math.Max(45, (height - fixedTop - fixedBottom) / 10);
            double best = still;
            int shift = 0;
            bool ambiguous = false;
            for (int offset = 1; offset <= height - fixedTop - fixedBottom - minimumOverlap; offset++)
            {
                for (int direction = -1; direction <= 1; direction += 2)
                {
                    int candidate = offset * direction;
                    double score = Difference(previous, current, candidate, Math.Min(best, 2.8), false, fixedTop, fixedBottom);
                    if (score < best) { best = score; shift = candidate; ambiguous = false; }
                    else if (score == best && score <= 2.8) ambiguous = true;
                }
            }
            // Only append when enough textured rows match; blank/repeating backgrounds are ambiguous.
            int texture = 0;
            int distance = Math.Abs(shift), currentOffset = Math.Max(0, -shift);
            for (int y = Math.Max(8, fixedTop); y < height - distance - Math.Max(8, fixedBottom); y += 3)
            {
                int row = (y + currentOffset) * Columns * 3;
                for (int x = 3; x < Columns * 3; x += 3)
                    if (Math.Abs(current[row + x] - current[row + x - 3]) > 18 ||
                        Math.Abs(current[row + x + 1] - current[row + x - 2]) > 18 ||
                        Math.Abs(current[row + x + 2] - current[row + x - 1]) > 18) texture++;
            }
            double verified = shift == 0 ? Double.MaxValue : Difference(previous, current, shift, Math.Min(still, 2.8), true, fixedTop, fixedBottom);
            if (best > 2.8 || ambiguous || texture < 24 || verified > 2.8 || verified >= still)
            {
                message = "重叠不足或画面变化，请向刚才的位置回滚一点后继续";
                return false;
            }
            int nextViewport = viewportTop + shift;
            int nextTop = Math.Min(rangeTop, nextViewport);
            int nextBottom = Math.Max(rangeTop + TotalHeight, nextViewport + height);
            int extension = nextBottom - nextTop - TotalHeight;
            if (extension == 0)
            {
                viewportTop = nextViewport; previous = current;
                message = "已定位到已捕获区域，继续向上或向下滚动可扩展长图";
                return true;
            }
            if (TotalHeight + extension > 24000 || (long)width * (TotalHeight + extension) > 50000000)
                throw new InvalidOperationException("已达到长截图上限（24000 像素高 / 5000 万像素），请先完成并保存。");
            bool prepend = nextViewport < rangeTop;
            int seam = (fixedTop + height - distance - fixedBottom) / 2 + currentOffset;
            int join = nextViewport + seam;
            int remove = prepend ? join - rangeTop : rangeTop + TotalHeight - join;
            ReplaceEdge(frame, seam, remove, prepend);
            frameCount++;
            TotalHeight += extension;
            rangeTop = nextTop; viewportTop = nextViewport; previous = current;
            message = "已追加 " + extension + " 像素（向" + (prepend ? "上" : "下") + "），共 " + TotalHeight + " 像素高";
            return true;
        }
        private void ReplaceEdge(Bitmap frame, int seam, int remove, bool prepend)
        {
            int index = prepend ? 0 : pieces.Count - 1;
            int direction = prepend ? 1 : -1;
            while (remove >= pieces[index].Height) { remove -= pieces[index].Height; index += direction; }
            Bitmap retained = null, added = null;
            try
            {
                pieces.Capacity = Math.Max(pieces.Capacity, pieces.Count + 1);
                if (remove > 0) retained = pieces[index].Clone(new Rectangle(0, prepend ? remove : 0, width, pieces[index].Height - remove), PixelFormat.Format24bppRgb);
                added = frame.Clone(new Rectangle(0, prepend ? 0 : seam, width, prepend ? seam : height - seam), PixelFormat.Format24bppRgb);
            }
            catch { if (retained != null) retained.Dispose(); if (added != null) added.Dispose(); throw; }
            int start = prepend ? 0 : index + 1, count = prepend ? index : pieces.Count - index - 1;
            for (int i = start; i < start + count; i++) pieces[i].Dispose();
            pieces.RemoveRange(start, count);
            index = prepend ? 0 : index;
            if (retained != null) { pieces[index].Dispose(); pieces[index] = retained; }
            if (prepend) pieces.Insert(0, added); else pieces.Add(added);
        }
        private int FixedEdge(byte[] first, byte[] second, bool bottom)
        {
            int count = 0;
            for (; count < height; count++)
            {
                int row = (bottom ? height - count - 1 : count) * Columns * 3, error = 0;
                for (int x = 0; x < Columns * 3; x++) error += Math.Abs(first[row + x] - second[row + x]);
                if (error > Columns * 3 / 2) break;
            }
            return count;
        }
        private double Difference(byte[] first, byte[] second, int shift, double limit, bool everyRow = false, int fixedTop = 0, int fixedBottom = 0)
        {
            int distance = Math.Abs(shift);
            int margin = Math.Min(Math.Max(8, height / 25), Math.Max(2, (height - distance) / 10));
            int top = Math.Max(margin, fixedTop), bottom = height - distance - Math.Max(margin, fixedBottom);
            int step = everyRow ? 1 : Math.Max(1, (bottom - top) / 80);
            int samples = ((bottom - top + step - 1) / step) * Columns * 3;
            if (samples <= 0) return Double.MaxValue;
            double maximumError = Math.Ceiling(limit * samples);
            long error = 0;
            int firstOffset = Math.Max(shift, 0), secondOffset = Math.Max(-shift, 0);
            for (int y = top; y < bottom; y += step)
            {
                int a = (y + firstOffset) * Columns * 3, b = (y + secondOffset) * Columns * 3;
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
        internal Bitmap Preview(Size maximum)
        {
            double scale = Math.Min(1, Math.Min((double)maximum.Width / width, (double)maximum.Height / TotalHeight));
            var result = new Bitmap(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(TotalHeight * scale)));
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.Clear(Color.White);
                int y = 0;
                foreach (var piece in pieces)
                {
                    int top = (int)Math.Round(y * scale); y += piece.Height;
                    int bottom = (int)Math.Round(y * scale);
                    if (bottom > top) graphics.DrawImage(piece, new Rectangle(0, top, result.Width, bottom - top));
                }
            }
            return result;
        }
        public void Dispose() { foreach (var piece in pieces) piece.Dispose(); pieces.Clear(); previous = null; frameCount = 0; }
    }

    internal sealed class ScrollForm : RoundedForm
    {
        private readonly Rectangle region;
        private readonly Func<Rectangle, Bitmap> captureFrame;
        private readonly Stitcher stitcher;
        private readonly Timer settle;
        private readonly ScrollWheelHook hook;
        private readonly Label status, summary, title, instructions;
        private readonly PictureBox preview;
        private readonly FlowLayoutPanel actions;
        private readonly CaptureOutline outline;
        private bool capturing, finishing, closeRequested, excluded;
        private DateTime wheelUntil;
        private int wheelVersion;
        private readonly List<int> registered = new List<int>();
        public event Action<Bitmap> Completed;
        protected override bool ShowWithoutActivation { get { return true; } }
        public ScrollForm(Rectangle rectangle, Bitmap first, Func<Rectangle, Bitmap> captureFrame = null)
        {
            region = rectangle;
            this.captureFrame = captureFrame ?? (bounds => ScreenCapture.Desktop(bounds));
            stitcher = new Stitcher(first);
            Text = "SnapTransOCR · 滚动长截图";
            ResizeEdges = false;
            TopMost = true;
            KeyPreview = true;
            ShowInTaskbar = true;
            ClientSize = new Size(610, 310);
            title = new Label { Text = "滚动长截图", AutoSize = true, ForeColor = Ui.Ink };
            title.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
            summary = new Label { Text = "1 帧 · " + region.Width + " × " + stitcher.TotalHeight + " px", ForeColor = Ui.Ink };
            status = new Label { Text = "已捕获首帧，等待滚动", ForeColor = Ui.Accent };
            instructions = new Label { Text = "在蓝框内向上或向下滚动，自动拼接。\n请保留少量重叠，F9 手动捕获。", ForeColor = Ui.Muted };
            preview = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Image = stitcher.Preview(new Size(192, 192)) };
            actions = Ui.Row(); actions.Dock = DockStyle.None; actions.AutoSize = false;
            actions.Controls.Add(Ui.Button("追加帧 F9", async delegate { await CaptureFrame(); }));
            actions.Controls.Add(Ui.Button("完成 F8", async delegate { await Finish(); }, true));
            actions.Controls.Add(Ui.Button("取消 Esc", Close));
            Controls.AddRange(new Control[] { title, summary, status, instructions, preview, actions });
            outline = new CaptureOutline(region);
            settle = new Timer { Interval = 120 };
            settle.Tick += async delegate
            {
                if (finishing || closeRequested || IsDisposed) { settle.Stop(); return; }
                if (capturing) return;
                int version = wheelVersion;
                bool final = DateTime.UtcNow >= wheelUntil;
                await CaptureFrame();
                if (final && version == wheelVersion) settle.Stop();
            };
            hook = new ScrollWheelHook();
            hook.Wheel += NotifyWheel;
            ResumeLayout(true); CaptureSessionUi.Place(this, region);
            Shown += delegate
            {
                outline.Show(); excluded = CaptureSessionUi.Exclude(this);
                if (Ui.RenderOffscreen) return;
                foreach (var key in new[] { new[] { 70, 0x78 }, new[] { 71, 0x77 }, new[] { 72, 0x1b } })
                    if (Native.RegisterHotKey(Handle, key[0], 0x4000, (uint)key[1])) registered.Add(key[0]);
                if (registered.Count != 3) status.Text = "部分按键被占用，请使用窗口按钮";
            };
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (preview == null) return;
            int gap = Ui.Scale(this, 18), side = Ui.Scale(this, 192), contentWidth = ClientSize.Width - side - gap * 3;
            title.Location = new Point(gap, Ui.Scale(this, 16));
            summary.Bounds = new Rectangle(gap, Ui.Scale(this, 58), contentWidth, Ui.Scale(this, 38));
            status.Bounds = new Rectangle(gap, Ui.Scale(this, 103), contentWidth, Ui.Scale(this, 62));
            instructions.Bounds = new Rectangle(gap, Ui.Scale(this, 176), contentWidth, Ui.Scale(this, 68));
            preview.Bounds = new Rectangle(ClientSize.Width - side - gap, Ui.Scale(this, 54), side, side);
            actions.Bounds = new Rectangle(0, ClientSize.Height - Ui.Scale(this, 65), ClientSize.Width, Ui.Scale(this, 60));
        }
        internal void NotifyWheel(Point point)
        {
            if (!region.Contains(point) || IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                if (finishing || closeRequested || IsDisposed) return;
                wheelVersion++; wheelUntil = DateTime.UtcNow.AddMilliseconds(240);
                if (!capturing) { status.Text = "检测到滚动，正在连续捕获…"; status.ForeColor = Ui.Accent; }
                settle.Start();
            });
        }
        private Task CaptureFrame() { return CaptureFrameCore(false); }
        private async Task CaptureFrameCore(bool final)
        {
            if (capturing || finishing && !final || closeRequested || IsDisposed) return;
            capturing = true;
            try
            {
                status.Text = "正在匹配并拼接画面…"; status.ForeColor = Ui.Accent;
                bool overlaps = !excluded && Bounds.IntersectsWith(region);
                if (overlaps) { Hide(); Native.DwmFlush(); await Task.Delay(100); }
                if (IsDisposed || finishing && !final || closeRequested) return;
                using (var frame = captureFrame(region))
                {
                    string message = "";
                    Bitmap thumbnail = null;
                    bool added = false;
                    Size maximum = preview.ClientSize;
                    await Task.Run(delegate
                    {
                        int height = stitcher.TotalHeight;
                        added = stitcher.Append(frame, out message);
                        if (stitcher.TotalHeight != height) thumbnail = stitcher.Preview(maximum);
                    });
                    if (!IsDisposed && !closeRequested)
                    {
                        if (thumbnail != null) { Image previous = preview.Image; preview.Image = thumbnail; if (previous != null) previous.Dispose(); }
                        summary.Text = stitcher.FrameCount + " 帧 · " + region.Width + " × " + stitcher.TotalHeight + " px";
                        status.Text = message; status.ForeColor = added ? Color.FromArgb(27, 135, 83) : Color.FromArgb(174, 103, 25);
                    }
                    else if (thumbnail != null) thumbnail.Dispose();
                }
                if (overlaps && !IsDisposed && !closeRequested) Show();
            }
            catch (Exception ex) { settle.Stop(); if (!IsDisposed && !closeRequested) { Show(); Ui.Error(this, ex); } }
            finally { capturing = false; if (closeRequested && !finishing) Close(); }
        }
        private async Task Finish()
        {
            if (finishing || closeRequested || IsDisposed) return;
            finishing = true;
            status.Text = "正在生成长截图…"; status.ForeColor = Ui.Accent;
            bool pending = settle.Enabled;
            settle.Stop();
            Enabled = false;
            bool completed = false;
            try
            {
                while (capturing) { await Task.Delay(30); if (IsDisposed) return; }
                if (closeRequested) return;
                if (pending)
                {
                    int delay = (int)Math.Max(0, (wheelUntil - DateTime.UtcNow).TotalMilliseconds);
                    if (delay > 0) await Task.Delay(Math.Min(240, delay));
                    await CaptureFrameCore(true);
                    if (IsDisposed || closeRequested) return;
                    status.Text = "正在生成长截图…";
                }
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
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.F9) { var task = CaptureFrame(); e.Handled = true; }
            else if (e.KeyCode == Keys.F8) { var task = Finish(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Capture and composition workers own the bitmaps until their continuations return.
            if (capturing || finishing) { e.Cancel = true; closeRequested = true; settle.Stop(); outline.Hide(); Hide(); }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (IsHandleCreated) foreach (int id in registered) Native.UnregisterHotKey(Handle, id);
                registered.Clear();
                settle.Dispose(); hook.Dispose(); stitcher.Dispose(); outline.Dispose();
                if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
            }
            base.Dispose(disposing);
        }
    }
}

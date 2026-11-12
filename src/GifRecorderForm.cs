using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class GifRecorderForm : RoundedForm
    {
        private readonly Rectangle region;
        private readonly Func<Rectangle, Bitmap> capture;
        private readonly Func<string, bool> save;
        private readonly Action<string> rememberQuality;
        private readonly Timer timer = new Timer { Interval = 100 };
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Label status, detail;
        private readonly Button start, finish;
        private readonly ComboBox quality;
        private GifQuality profile;
        private Size outputSize;
        private readonly List<int> registered = new List<int>();
        private readonly CaptureOutline outline;
        private GifStreamWriter writer;
        private Task pending = Task.CompletedTask;
        private string temporary;
        private int frames;
        private bool recording, finishing, closing, excluded;
        internal GifRecorderForm(Rectangle region, Func<Rectangle, Bitmap> capture = null, Func<string, bool> save = null, string initialQuality = "compact", Action<string> rememberQuality = null)
        {
            if (region.Width < 3 || region.Height < 3 || (long)region.Width * region.Height > 8294400)
                throw new InvalidOperationException("GIF 录制区域需在 3 × 3 至 829 万像素以内，请调整选区。");
            this.region = region;
            this.capture = capture ?? (bounds => ScreenCapture.Desktop(bounds, true));
            this.save = save ?? Save;
            this.rememberQuality = rememberQuality;
            Text = "SnapTransOCR · GIF 动图"; ResizeEdges = false; TopMost = true; ShowInTaskbar = true; KeyPreview = true;
            ClientSize = new Size(520, 240);
            var title = new Label { Text = "GIF 动图录制", Dock = DockStyle.Top, Height = 38, Padding = new Padding(12, 8, 0, 0), ForeColor = Ui.Ink };
            title.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } };
            var row = Ui.Row(); row.Dock = DockStyle.Bottom;
            start = Ui.Button("开始 F9", StartRecording, true);
            finish = Ui.Button("停止并保存 F8", async delegate { await FinishRecording(); }); finish.Enabled = false;
            row.Controls.Add(start); row.Controls.Add(finish); row.Controls.Add(Ui.Button("取消 Esc", Close));
            status = new Label { Text = "就绪 · " + region.Width + " × " + region.Height + " px", Dock = DockStyle.Top, Height = 32, Padding = new Padding(12, 6, 0, 0), ForeColor = Ui.Accent };
            detail = new Label { Text = "10 帧/秒 · 最长 60 秒 / 128 MiB · 逐帧写入磁盘", Dock = DockStyle.Top, Height = 32, Padding = new Padding(12, 5, 0, 0), ForeColor = Ui.Muted };
            var qualityRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Padding = new Padding(12, 3, 12, 4) };
            qualityRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            qualityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); qualityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            qualityRow.Controls.Add(new Label { Text = "画质", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
            quality = Ui.Combo(Array.ConvertAll(GifQuality.Presets, value => value.Caption), Array.IndexOf(GifQuality.Presets, GifQuality.Find(initialQuality)));
            quality.AccessibleName = "GIF 录制画质"; quality.Dock = DockStyle.Fill; quality.Margin = new Padding(4, 0, 4, 0);
            qualityRow.Controls.Add(quality, 1, 0);
            Controls.Add(detail); Controls.Add(status); Controls.Add(qualityRow); Controls.Add(title); Controls.Add(row);
            quality.SelectedIndexChanged += delegate { ApplyQuality(); };
            ApplyQuality();
            outline = new CaptureOutline(region);
            timer.Tick += async delegate { await CaptureNext(); };
            ResumeLayout(true); CaptureSessionUi.Place(this, region);
            Shown += delegate
            {
                outline.Show(); excluded = CaptureSessionUi.Exclude(this);
                if (Ui.RenderOffscreen) return;
                foreach (var key in new[] { new[] { 80, 0x78 }, new[] { 81, 0x77 }, new[] { 82, 0x1b } })
                    if (Native.RegisterHotKey(Handle, key[0], 0x4000, (uint)key[1])) registered.Add(key[0]);
                if (registered.Count != 3) detail.Text = "部分按键被占用，请使用窗口按钮 · 最长 60 秒";
            };
        }
        private void ApplyQuality()
        {
            if (recording || finishing) return;
            profile = GifQuality.Presets[Math.Max(0, quality.SelectedIndex)]; outputSize = profile.OutputSize(region.Size);
            timer.Interval = (int)Math.Round(1000.0 / profile.FramesPerSecond);
            status.Text = "就绪 · 输出 " + outputSize.Width + " × " + outputSize.Height + " px";
            detail.Text = profile.FramesPerSecond + " 帧/秒 · 最长 60 秒 / 128 MiB · 变化区域压缩";
        }
        internal void StartRecording()
        {
            if (recording || finishing || closing) return;
            try
            {
                temporary = Path.Combine(Path.GetTempPath(), "SnapTransOCR-" + Guid.NewGuid().ToString("N") + ".gif");
                writer = new GifStreamWriter(temporary, outputSize);
                recording = true; frames = 0; start.Enabled = false; finish.Enabled = true;
                quality.Enabled = false;
                if (rememberQuality != null) rememberQuality(profile.Id);
                outline.OutlineColor = Color.FromArgb(228, 65, 75); outline.Invalidate();
                if (Visible) outline.Show();
                clock.Restart(); timer.Start();
                if (!excluded && Bounds.IntersectsWith(region) && !Ui.RenderOffscreen && !registered.Contains(81))
                    throw new InvalidOperationException("F8 停止键已被占用，请将控制窗口移出选区后重新开始。");
                if (!excluded && Bounds.IntersectsWith(region)) Hide();
                var task = CaptureNext();
            }
            catch (Exception ex) { Fail(ex); }
        }
        internal async Task CaptureNext()
        {
            if (!recording || finishing || closing || !pending.IsCompleted) return;
            try
            {
                if (clock.ElapsedMilliseconds >= 60000 || writer.Length >= 128L * 1024 * 1024) { await FinishRecording(); return; }
                long timestamp = clock.ElapsedMilliseconds;
                Bitmap image = capture(region);
                pending = Task.Run(delegate
                {
                    using (image)
                    {
                        if (image.Size != region.Size) throw new InvalidOperationException("录制期间选区尺寸发生变化。");
                        if (image.Size == outputSize) writer.AddFrame(image, timestamp);
                        else using (var resized = profile.Resize(image)) writer.AddFrame(resized, timestamp);
                    }
                });
                await pending;
                if (closing || finishing) return;
                frames++;
                status.Text = "● 录制中  " + clock.Elapsed.ToString(@"mm\:ss") + "  · " + frames + " 帧";
                detail.Text = outputSize.Width + " × " + outputSize.Height + " px · " + (writer.Length / 1048576.0).ToString("0.0") + " MiB · F8 停止保存";
            }
            catch (Exception ex) { if (!closing) Fail(ex); }
        }
        internal async Task FinishRecording()
        {
            if (!recording || finishing || closing) return;
            finishing = true; timer.Stop(); clock.Stop(); finish.Enabled = false;
            status.Text = "正在整理动图…";
            try
            {
                await pending;
                if (closing) return;
                long duration = clock.ElapsedMilliseconds;
                pending = Task.Run(delegate { writer.Complete(duration); });
                await pending;
                if (closing) return;
                writer.Dispose(); writer = null; recording = false;
                outline.Hide(); Show(); Activate();
                if (save(temporary)) Close();
                else { status.Text = "未保存 · 可调整画质后重新录制"; start.Enabled = quality.Enabled = true; }
            }
            catch (Exception ex) { if (!closing) Fail(ex); }
            finally { finishing = false; if (!closing) Cleanup(); }
        }
        private bool Save(string source)
        {
            using (var dialog = new SaveFileDialog { Title = "保存 GIF 动图", Filter = "GIF 动图|*.gif", AddExtension = true, FileName = "SnapTransOCR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gif" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                SaveCopy(source, dialog.FileName); return true;
            }
        }
        internal static void SaveCopy(string source, string destination)
        {
            string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(source, staging); if (File.Exists(destination)) File.Replace(staging, destination, null); else File.Move(staging, destination); }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        private void Fail(Exception exception)
        {
            recording = false; timer.Stop(); clock.Stop(); Cleanup(); outline.Hide();
            Show(); status.Text = "录制未完成：" + exception.Message; start.Enabled = quality.Enabled = true; finish.Enabled = false;
        }
        private void Cleanup()
        {
            if (writer != null) { writer.Dispose(); writer = null; }
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
            temporary = null;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312)
            {
                int id = message.WParam.ToInt32();
                if (id == 80) StartRecording(); else if (id == 81) { var task = FinishRecording(); } else if (id == 82) Close();
            }
            base.WndProc(ref message);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.F9) { StartRecording(); e.Handled = true; }
            else if (e.KeyCode == Keys.F8) { var task = FinishRecording(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true; recording = false; timer.Stop();
                try { pending.GetAwaiter().GetResult(); } catch (Exception) { } // Worker touches only its frame and the stream, so joining cannot wait on the UI.
                if (IsHandleCreated) foreach (int id in registered) Native.UnregisterHotKey(Handle, id);
                registered.Clear(); timer.Dispose(); outline.Dispose(); Cleanup();
            }
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ClipboardRecording
    {
        private static readonly object gate = new object();
        private static readonly Queue<uint> ignored = new Queue<uint>();
        private static int depth;
        internal static event Action Released;
        internal static bool Suppressed { get { lock (gate) return depth > 0; } }
        internal static bool IsIgnored(uint sequence) { lock (gate) return ignored.Contains(sequence); }
        internal static void Ignore(uint sequence)
        {
            lock (gate) { if (ignored.Contains(sequence)) return; ignored.Enqueue(sequence); while (ignored.Count > 32) ignored.Dequeue(); }
        }
        internal static IDisposable Suppress() { lock (gate) depth++; return new Scope(); }
        private sealed class Scope : IDisposable
        {
            private bool disposed;
            public void Dispose()
            {
                Action released = null;
                lock (gate) { if (disposed) return; disposed = true; if (--depth == 0) released = Released; }
                if (released != null) released();
            }
        }
    }

    internal sealed class ClipboardContent : IDisposable
    {
        internal const int MaxTextBytes = 1024 * 1024;
        internal const long MaxImagePixels = 50000000;
        internal string Text;
        internal Bitmap Image;
        internal static ClipboardContent Read(IDataObject data)
        {
            if (data == null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) return null;
            if (data.GetDataPresent("CanIncludeInClipboardHistory", false))
            {
                object permission = data.GetData("CanIncludeInClipboardHistory", false);
                byte[] bytes = permission as byte[];
                var stream = permission as MemoryStream;
                if (stream != null) bytes = stream.ToArray();
                if (permission is int && (int)permission == 0 || bytes != null && bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 0) return null;
            }
            if (data.GetDataPresent(DataFormats.Bitmap, true))
            {
                var image = data.GetData(DataFormats.Bitmap, true) as Image;
                if (image == null || (long)image.Width * image.Height > MaxImagePixels) return null;
                var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
                try
                {
                    using (var graphics = Graphics.FromImage(copy)) { graphics.CompositingMode = CompositingMode.SourceCopy; graphics.DrawImageUnscaled(image, 0, 0); }
                    return new ClipboardContent { Image = copy };
                }
                catch { copy.Dispose(); throw; }
            }
            string text = data.GetData(DataFormats.UnicodeText, true) as string;
            if (String.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaxTextBytes) return null;
            return new ClipboardContent { Text = text };
        }
        internal string Hash()
        {
            using (var hash = SHA256.Create())
            {
                if (Image == null) return Signing.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(Text)));
                byte[] dimensions = Encoding.ASCII.GetBytes(Image.Width + "x" + Image.Height + ":");
                hash.TransformBlock(dimensions, 0, dimensions.Length, dimensions, 0);
                BitmapData pixels = Image.LockBits(new Rectangle(Point.Empty, Image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[Image.Width * 4];
                    for (int y = 0; y < Image.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(pixels.Scan0, pixels.Stride * y), row, 0, row.Length);
                        hash.TransformBlock(row, 0, row.Length, row, 0);
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    return Signing.Hex(hash.Hash);
                }
                finally { Image.UnlockBits(pixels); }
            }
        }
        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    internal sealed class ClipboardHistory : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr window);
        private const int RecheckMessage = 0x8009;
        private readonly HistoryStore history;
        private readonly Func<bool> enabled;
        private readonly Action<Exception> onError;
        private readonly SynchronizationContext context;
        private readonly System.Windows.Forms.Timer timer;
        private readonly HashSet<Task> writes = new HashSet<Task>();
        private Task writing = Task.CompletedTask;
        private bool disposed, pending;
        private uint pendingSequence;
        private int attempts;

        internal ClipboardHistory(HistoryStore history, Func<bool> enabled, Action<Exception> onError = null)
        {
            this.history = history; this.enabled = enabled; this.onError = onError;
            context = SynchronizationContext.Current;
            timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += delegate { timer.Stop(); CapturePending(false); };
            CreateHandle(new CreateParams { Caption = "SnapTransOCR.ClipboardHistory", Parent = new IntPtr(-3) });
            if (!AddClipboardFormatListener(Handle)) { timer.Dispose(); DestroyHandle(); throw new InvalidOperationException("无法监听系统剪贴板变化。"); }
            ClipboardRecording.Released += SuppressionReleased;
        }
        private void SuppressionReleased() { if (!disposed) Native.PostMessage(Handle, RecheckMessage, IntPtr.Zero, IntPtr.Zero); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x31d || message.Msg == RecheckMessage && pending) Schedule();
            base.WndProc(ref message);
        }
        private void Schedule()
        {
            if (disposed) return;
            timer.Stop();
            uint sequence = Native.GetClipboardSequenceNumber();
            if (!enabled() || ClipboardRecording.IsIgnored(sequence)) { pending = false; return; }
            pending = true; pendingSequence = sequence; attempts = 0; timer.Interval = 150;
            if (!ClipboardRecording.Suppressed) timer.Start();
        }
        internal void ResetEnabledState() { pending = false; timer.Stop(); }
        private void CapturePending(bool final)
        {
            if (disposed || !pending || !enabled()) { pending = false; return; }
            if (ClipboardRecording.Suppressed) return;
            if (!final && !writing.IsCompleted) { timer.Interval = 50; timer.Start(); return; }
            uint sequence = Native.GetClipboardSequenceNumber();
            if (sequence != pendingSequence) { if (!final) { Schedule(); return; } pendingSequence = sequence; }
            if (ClipboardRecording.IsIgnored(sequence)) { pending = false; return; }
            ClipboardContent content = null;
            try
            {
                if (Native.GetOpenClipboardWindow() != IntPtr.Zero) throw new ExternalException();
                content = ClipboardContent.Read(Clipboard.GetDataObject());
                if (Native.GetOpenClipboardWindow() != IntPtr.Zero) throw new ExternalException();
                if (Native.GetClipboardSequenceNumber() != sequence) { if (content != null) content.Dispose(); Schedule(); return; }
                pending = false; timer.Interval = 150;
                if (content == null) return;
                ClipboardContent captured = content;
                Task previousWrite = writing;
                writing = Task.Run(async delegate
                {
                    try
                    {
                        await previousWrite.ConfigureAwait(false);
                        var entry = new HistoryEntry { Kind = captured.Image == null ? "clipboard_text" : "clipboard_image", Source = captured.Text ?? "", ContentHash = captured.Hash() };
                        if (!await history.AddAsync(entry, captured.Image).ConfigureAwait(false))
                            Report(new InvalidOperationException("剪贴板内容未保存：历史文字容量已满或内容为空。"));
                    }
                    catch (Exception ex) { Report(ex); }
                    finally { captured.Dispose(); }
                });
                lock (writes) writes.Add(writing);
                writing.ContinueWith(task => { lock (writes) writes.Remove(task); }, TaskScheduler.Default);
            }
            catch (ExternalException) { if (content != null) content.Dispose(); Retry(final); }
            catch (Exception ex) { if (content != null) content.Dispose(); pending = false; Report(ex); }
        }
        private void Retry(bool final)
        {
            if (!final && ++attempts < 6) { timer.Interval = 40; timer.Start(); }
            else pending = false;
        }
        private void Report(Exception exception)
        {
            if (disposed || onError == null) return;
            if (context != null) context.Post(delegate { if (!disposed) onError(exception); }, null);
        }
        internal Task FlushAsync() { lock (writes) return Task.WhenAll(new List<Task>(writes)); }
        public void Dispose()
        {
            if (disposed) return;
            timer.Stop(); CapturePending(true); disposed = true;
            ClipboardRecording.Released -= SuppressionReleased;
            RemoveClipboardFormatListener(Handle); DestroyHandle(); timer.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ClipboardHistoryTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var plain = new DataObject(); plain.SetData(DataFormats.UnicodeText, false, "  Hello 世界\r\nline 2  ");
            using (var content = ClipboardContent.Read(plain))
            {
                check(content != null && content.Image == null && content.Text == "  Hello 世界\r\nline 2  ", "clipboard text retains whitespace, Unicode and line breaks");
                string first = content.Hash();
                using (var same = ClipboardContent.Read(plain)) check(first == same.Hash(), "clipboard text hash is stable for identical content");
                plain.SetData(DataFormats.UnicodeText, false, "different");
                using (var different = ClipboardContent.Read(plain)) check(first != different.Hash(), "clipboard text hash changes with content");
            }
            var empty = new DataObject(); empty.SetData(DataFormats.UnicodeText, false, "");
            check(ClipboardContent.Read(empty) == null, "empty clipboard text is ignored");
            var large = new DataObject(); large.SetData(DataFormats.UnicodeText, false, new string('中', ClipboardContent.MaxTextBytes / 3 + 1));
            check(ClipboardContent.Read(large) == null, "clipboard text UTF8 byte limit is enforced");
            var files = new DataObject(); files.SetData(DataFormats.FileDrop, false, new[] { "C:\\sample.txt" });
            check(ClipboardContent.Read(files) == null, "clipboard file lists do not become text history");
            var excluded = new DataObject(); excluded.SetData(DataFormats.UnicodeText, false, "sensitive test fixture");
            excluded.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new byte[] { 1 });
            check(ClipboardContent.Read(excluded) == null, "password-manager clipboard exclusion is respected");
            foreach (object permission in new object[] { 0, new byte[4], new MemoryStream(new byte[4]) })
            {
                var data = new DataObject(); data.SetData(DataFormats.UnicodeText, false, "not for history"); data.SetData("CanIncludeInClipboardHistory", false, permission);
                check(ClipboardContent.Read(data) == null, "Windows no-history DWORD is respected");
                var disposable = permission as IDisposable; if (disposable != null) disposable.Dispose();
            }
            using (var image = new Bitmap(20, 10))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Crimson);
                var data = new DataObject(); data.SetData(DataFormats.Bitmap, false, image); data.SetData(DataFormats.UnicodeText, false, "image description");
                using (var content = ClipboardContent.Read(data))
                {
                    check(content != null && content.Image != null && content.Text == null && content.Image.Size == image.Size,
                        "clipboard image has an independent bitmap snapshot and takes priority over alternate text");
                    string before = content.Hash();
                    image.SetPixel(0, 0, Color.Blue);
                    check(content.Image.GetPixel(0, 0).ToArgb() == Color.Crimson.ToArgb(), "clipboard image snapshot outlives the original pixels");
                    using (var changed = ClipboardContent.Read(data)) check(before != changed.Hash(), "clipboard image hash identifies pixel changes");
                    using (var same = new ClipboardContent { Image = (Bitmap)content.Image.Clone() }) check(before == same.Hash(), "clipboard image hash is stable for identical pixels");
                }
            }
            int releases = 0;
            Action released = delegate { releases++; };
            ClipboardRecording.Released += released;
            try
            {
                using (ClipboardRecording.Suppress())
                {
                    using (ClipboardRecording.Suppress()) check(ClipboardRecording.Suppressed, "nested clipboard suppression remains active");
                    check(ClipboardRecording.Suppressed && releases == 0, "inner suppression does not release monitor early");
                    ClipboardRecording.Ignore(0xf0000101); ClipboardRecording.Ignore(0xf0000102);
                    check(ClipboardRecording.IsIgnored(0xf0000101) && ClipboardRecording.IsIgnored(0xf0000102) && !ClipboardRecording.IsIgnored(0xf0000103),
                        "owned temporary sequences are ignored without excluding newer external changes");
                }
                check(!ClipboardRecording.Suppressed && releases == 1, "outer clipboard suppression releases exactly once");
            }
            finally { ClipboardRecording.Released -= released; }
        }

        internal static Task RunNative(Action<bool, string> check)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            using (var context = new WindowsFormsSynchronizationContext())
            {
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    Task operation = RunNativeCore(check);
                    while (!operation.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
                    operation.GetAwaiter().GetResult();
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }
            return Task.CompletedTask;
        }
        private static async Task RunNativeCore(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-clipboard-history-" + Guid.NewGuid().ToString("N"));
            ClipboardSnapshot previous = null;
            for (int i = 0; i < 6 && previous == null; i++)
            {
                try { previous = ClipboardSnapshot.Capture(); }
                catch (System.Runtime.InteropServices.ExternalException) { await Task.Delay(40); }
                catch (InvalidOperationException) { await Task.Delay(40); }
            }
            if (previous == null)
            {
                Console.WriteLine("SKIP native clipboard history fixture: existing clipboard could not be safely snapshotted; no clipboard change was made.");
                return;
            }
            uint owned = Native.GetClipboardSequenceNumber();
            var store = new HistoryStore(directory);
            bool enabled = true;
            var errors = new List<Exception>();
            ClipboardHistory monitor = null;
            try
            {
                monitor = new ClipboardHistory(store, delegate { return enabled; }, errors.Add);
                await Task.Delay(220);
                check((await store.LoadAsync()).Length == 0, "listener registration does not import existing clipboard contents");
                await WriteFixture(delegate { Clipboard.SetText("external clipboard fixture 世界"); }); owned = Native.GetClipboardSequenceNumber();
                await WaitFor(store, 1);
                var records = await store.LoadAsync();
                check(records.Length == 1 && records[0].Kind == "clipboard_text" && records[0].Source == "external clipboard fixture 世界", "native clipboard notification records externally copied Unicode text");
                await WriteFixture(delegate { Clipboard.SetText("external clipboard fixture 世界"); }); owned = Native.GetClipboardSequenceNumber();
                await Task.Delay(260); await monitor.FlushAsync();
                check((await store.LoadAsync()).Length == 1, "native repeated clipboard text is deduplicated");
                await Clip.SetText("application-owned clipboard fixture"); owned = Native.GetClipboardSequenceNumber();
                await Task.Delay(240);
                check((await store.LoadAsync()).Length == 1, "application-owned writes do not duplicate application history");
                using (ClipboardRecording.Suppress())
                {
                    await WriteFixture(delegate { Clipboard.SetText("temporary selected text"); }); owned = Native.GetClipboardSequenceNumber(); ClipboardRecording.Ignore(owned);
                    await Task.Delay(190);
                    await WriteFixture(delegate { Clipboard.SetText("restored application value"); }); owned = Native.GetClipboardSequenceNumber(); ClipboardRecording.Ignore(owned);
                }
                await Task.Delay(230);
                check((await store.LoadAsync()).Length == 1, "temporary selection and restoration are not recorded across awaited copy scopes");
                using (ClipboardRecording.Suppress())
                {
                    await WriteFixture(delegate { Clipboard.SetText("another temporary selection"); }); owned = Native.GetClipboardSequenceNumber(); ClipboardRecording.Ignore(owned);
                    await Task.Delay(50);
                    await WriteFixture(delegate { Clipboard.SetText("new external value during temporary copy"); }); owned = Native.GetClipboardSequenceNumber();
                    await Task.Delay(180);
                }
                await WaitFor(store, 2);
                records = await store.LoadAsync();
                check(records.Length == 2 && records[0].Source == "new external value during temporary copy", "newer external clipboard value survives suppression release");
                var excluded = new DataObject(); excluded.SetData(DataFormats.UnicodeText, false, "excluded native fixture"); excluded.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new byte[] { 1 });
                await WriteFixture(delegate { Clipboard.SetDataObject(excluded, true); }); owned = Native.GetClipboardSequenceNumber();
                await Task.Delay(230);
                check((await store.LoadAsync()).Length == 2, "native monitor ignores excluded clipboard formats");
                enabled = false; monitor.ResetEnabledState();
                await WriteFixture(delegate { Clipboard.SetText("paused fixture"); }); owned = Native.GetClipboardSequenceNumber();
                await Task.Delay(230);
                check((await store.LoadAsync()).Length == 2, "paused clipboard recording leaves history unchanged");
                enabled = true; monitor.ResetEnabledState();
                using (var image = new Bitmap(32, 18))
                {
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Teal);
                    await WriteFixture(delegate { Clipboard.SetImage(image); }); owned = Native.GetClipboardSequenceNumber();
                }
                await WaitFor(store, 3);
                records = await store.LoadAsync();
                Bitmap saved = records.Length > 0 ? await store.LoadImageAsync(records[0].Id) : null;
                using (saved) check(records.Length == 3 && records[0].Kind == "clipboard_image" && saved != null && saved.Size == new Size(32, 18) && saved.GetPixel(8, 8).ToArgb() == Color.Teal.ToArgb(), "native image clipboard is saved as independently readable PNG history");
                await WriteFixture(delegate { Clipboard.SetText("final pending clipboard fixture"); }); owned = Native.GetClipboardSequenceNumber();
                Application.DoEvents();
                monitor.Dispose(); await monitor.FlushAsync();
                records = await store.LoadAsync();
                check(records.Length == 4 && records[0].Source == "final pending clipboard fixture", "disposing before the debounce timer saves the final notified clipboard item and flushes writes in order");
                await WriteFixture(delegate { Clipboard.SetText("after disposal fixture"); }); owned = Native.GetClipboardSequenceNumber();
                await Task.Delay(230);
                check((await store.LoadAsync()).Length == 4 && errors.Count == 0, "disposing clipboard monitor stops notifications and finishes writes without errors");
            }
            finally
            {
                if (monitor != null) { monitor.Dispose(); await monitor.FlushAsync(); }
                if (Native.GetClipboardSequenceNumber() == owned)
                {
                    using (ClipboardRecording.Suppress())
                    {
                        if (previous.Data != null) Clipboard.SetDataObject(previous.Data, true); else Clipboard.Clear();
                        ClipboardRecording.Ignore(Native.GetClipboardSequenceNumber());
                    }
                }
                previous.Dispose();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static async Task WaitFor(HistoryStore store, int count)
        {
            for (int i = 0; i < 70; i++) { if ((await store.LoadAsync()).Length >= count) return; await Task.Delay(25); }
        }
        private static async Task WriteFixture(Action write)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { write(); return; }
                catch (System.Runtime.InteropServices.ExternalException) { if (attempt == 5) throw; await Task.Delay(45); }
            }
        }
    }
}

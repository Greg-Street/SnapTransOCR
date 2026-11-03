using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ClipboardContentionTests
    {
        internal static async Task CheckReadable(Action<bool, string> check, uint sequence, Func<bool> read, string message)
        {
            for (int i = 0; i < 13; i++)
            {
                if (Native.GetClipboardSequenceNumber() != sequence) break;
                bool valid = false;
                try { valid = read(); } catch (ExternalException) { }
                if (Native.GetClipboardSequenceNumber() != sequence) break;
                if (valid) { check(true, message); return; }
                await Task.Delay(20);
            }
            check(false, message);
        }

        internal static async Task Run(Action<bool, string> check, Form fixture, ClipboardCopyTextBox input, DataObject sentinel)
        {
            var reader = new SelectionReader();
            Clipboard.SetDataObject(sentinel, true);
            uint sequence = Native.GetClipboardSequenceNumber();
            using (var held = new ClipboardLock())
            {
                bool unreadable;
                try { unreadable = Clipboard.GetText().Length == 0; } catch (ExternalException) { unreadable = true; }
                check(unreadable && Native.GetClipboardSequenceNumber() == sequence,
                    "a held clipboard makes WinForms text unreadable without changing content");
                string text = await reader.ReadCopy(fixture.Handle);
                check(text.Length == 0 && Native.GetClipboardSequenceNumber() == sequence,
                    "persistent clipboard contention exits silently before sending Ctrl+C");
            }
            await CheckReadable(check, sequence, delegate { return Clipboard.GetText() == "clipboard sentinel"; },
                "contention does not replace the original text with an empty snapshot");

            Clipboard.SetDataObject(sentinel, true);
            using (var held = new ClipboardLock())
            {
                Task<string> pending = reader.ReadCopy(fixture.Handle);
                check(!pending.IsCompleted, "a busy snapshot yields instead of blocking the UI thread");
                await Task.Delay(50);
                held.Dispose();
                string text = await pending;
                check(text == "this selection", "a transient clipboard lock retries and reads the selection");
            }
            sequence = Native.GetClipboardSequenceNumber();
            await CheckReadable(check, sequence, delegate { return Clipboard.GetText() == "clipboard sentinel"; },
                "a transient clipboard lock still restores the original text");

            for (int replace = 0; replace < 2; replace++)
            {
                Clipboard.SetDataObject(sentinel, true);
                ClipboardLock copiedLock = null;
                input.AfterCopy = delegate { copiedLock = new ClipboardLock(); };
                try
                {
                    Task<string> pending = reader.ReadCopy(fixture.Handle);
                    for (int i = 0; i < 30 && copiedLock == null && !pending.IsCompleted; i++) await Task.Delay(10);
                    check(copiedLock != null, "synthetic native Ctrl+C reaches the locked text-read stage");
                    await Task.Delay(40);
                    check(!pending.IsCompleted, "a temporary text read lock yields while preserving the copied sequence");
                    copiedLock.Dispose();
                    if (replace != 0) Clipboard.SetText("newer clipboard sentinel");
                    uint changedSequence = Native.GetClipboardSequenceNumber();
                    string text = await pending;
                    if (replace == 0)
                    {
                        check(text == "this selection", "selected text is retried after a temporary read lock");
                        sequence = Native.GetClipboardSequenceNumber();
                        await CheckReadable(check, sequence, delegate { return Clipboard.GetText() == "clipboard sentinel"; },
                            "a recovered selected-text read restores the original clipboard");
                    }
                    else
                    {
                        check(text.Length == 0 && Native.GetClipboardSequenceNumber() == changedSequence,
                            "a newer value during selected-text retries cancels reading and restoration");
                        await CheckReadable(check, changedSequence, delegate { return Clipboard.GetText() == "newer clipboard sentinel"; },
                            "selected-text retries preserve the newer clipboard value");
                    }
                }
                finally { input.AfterCopy = null; if (copiedLock != null) copiedLock.Dispose(); }
            }

            Clipboard.SetDataObject(sentinel, true);
            using (var held = new ClipboardLock())
            {
                Task<string> pending = reader.ReadCopy(fixture.Handle);
                held.Dispose();
                Clipboard.SetText("newer clipboard sentinel");
                sequence = Native.GetClipboardSequenceNumber();
                check((await pending).Length == 0 && Native.GetClipboardSequenceNumber() == sequence,
                    "a newer clipboard change cancels the pending temporary copy");
                await CheckReadable(check, sequence, delegate { return Clipboard.GetText() == "newer clipboard sentinel"; },
                    "snapshot retries never overwrite a newer clipboard value");
            }

            var empty = new DataObject(); empty.SetData(DataFormats.UnicodeText, false, "");
            Clipboard.SetDataObject(empty, true);
            sequence = Native.GetClipboardSequenceNumber();
            check((await reader.ReadCopy(fixture.Handle)).Length == 0 && Native.GetClipboardSequenceNumber() == sequence,
                "an ambiguous empty-format snapshot is refused without changing the clipboard");
            Clipboard.SetDataObject(sentinel, true);
        }

        private sealed class ClipboardLock : IDisposable
        {
            [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
            [DllImport("user32.dll")] private static extern bool CloseClipboard();
            private readonly ManualResetEvent ready = new ManualResetEvent(false), release = new ManualResetEvent(false);
            private readonly Thread thread;
            private bool opened, disposed;
            internal ClipboardLock()
            {
                thread = new Thread(delegate()
                {
                    var window = new NativeWindow();
                    window.CreateHandle(new CreateParams { Parent = new IntPtr(-3), Caption = "SnapTransOCR synthetic clipboard lock" });
                    try
                    {
                        for (int i = 0; i < 50 && !opened; i++) { opened = OpenClipboard(window.Handle); if (!opened) Thread.Sleep(5); }
                        ready.Set();
                        if (opened) release.WaitOne();
                    }
                    finally { if (opened) CloseClipboard(); window.DestroyHandle(); }
                });
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.WaitOne();
                if (!opened) { Dispose(); throw new InvalidOperationException("Synthetic clipboard lock could not be acquired."); }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true; release.Set(); thread.Join(); ready.Dispose(); release.Dispose();
            }
        }
    }

    internal sealed class ClipboardCopyTextBox : TextBox
    {
        internal Action AfterCopy;
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x301 && AfterCopy != null) AfterCopy();
        }
    }
}

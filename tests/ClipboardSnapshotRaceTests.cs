using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ClipboardSnapshotRaceTests
    {
        internal static void Run(Action<bool, string> check)
        {
            using (var previous = ClipboardSnapshot.Capture())
            {
                try
                {
                    foreach (bool oleEmpty in new[] { false, true })
                    {
                        if (oleEmpty) Clipboard.SetDataObject(new DataObject(), true); else Clipboard.Clear();
                        System.Threading.Thread.Sleep(150);
                        uint sequence = Native.GetClipboardSequenceNumber();
                        using (var empty = ClipboardSnapshot.Capture())
                        {
                            check(empty.Data == null && Native.GetClipboardSequenceNumber() == sequence,
                                "a logically empty clipboard has a clearable snapshot without changing its native sequence, OLE=" + oleEmpty);
                            Clipboard.SetText("synthetic empty clipboard roundtrip");
                            if (empty.Data != null) Clipboard.SetDataObject(empty.Data, true); else Clipboard.Clear();
                        }
                        var restored = Clipboard.GetDataObject();
                        check(restored == null || restored.GetFormats(false).Length == 0,
                            "restoring an empty clipboard removes the temporary copied text, OLE=" + oleEmpty);
                        using (var repeated = ClipboardSnapshot.Capture())
                            check(repeated.Data == null, "an empty clipboard remains readable on the next snapshot, OLE=" + oleEmpty);
                    }
                    foreach (bool afterValue in new[] { false, true })
                    {
                        IDisposable held = null;
                        var original = new RacingData(afterValue, delegate
                        {
                            held = (IDisposable)Activator.CreateInstance(typeof(ClipboardContentionTests)
                                .GetNestedType("ClipboardLock", BindingFlags.NonPublic), true);
                        });
                        original.SetData("SnapTransOCR.RaceMarker", false, "synthetic marker");
                        var clipboard = new DataObject("SnapTransOCR.RaceMarker", "synthetic marker");
                        Clipboard.SetDataObject(clipboard, true);
                        System.Threading.Thread.Sleep(150);
                        uint sequence = Native.GetClipboardSequenceNumber();
                        original.Armed = true;
                        bool refused = false;
                        try
                        {
                            using (ClipboardSnapshot.Capture(original)) { }
                        }
                        catch (ExternalException) { refused = true; }
                        finally { original.Armed = false; if (held != null) held.Dispose(); }
                        check(held != null && refused && Native.GetClipboardSequenceNumber() == sequence,
                            "snapshot refuses a clipboard lock acquired " + (afterValue ? "during a format read" : "after format enumeration") + " without replacing content (lock=" + (held != null) + ", refused=" + refused + ")");
                        check(original.GetData("SnapTransOCR.RaceMarker", false) as string == "synthetic marker",
                            "refusing the contended snapshot leaves its source custom string usable");
                    }

                    using (var raw = new MemoryStream(new byte[62]))
                    {
                        var original = new DataObject();
                        original.SetData("SnapTransOCR.RawMarker", false, raw);
                        original.SetData("SnapTransOCR.StringMarker", false, "synthetic marker");
                        Clipboard.SetDataObject(original, false);
                        uint sequence = Native.GetClipboardSequenceNumber();
                        System.Threading.Thread.Sleep(150);
                        using (var snapshot = ClipboardSnapshot.Capture(original))
                        {
                            var copied = snapshot.Data.GetData("SnapTransOCR.RawMarker", false) as MemoryStream;
                            check(copied != null && copied.Length == 62 && Array.TrueForAll(copied.ToArray(), value => value == 0)
                                && Native.GetClipboardSequenceNumber() == sequence,
                                "an uncontended snapshot preserves a legitimate all-zero raw stream");
                            check(snapshot.Data.GetData("SnapTransOCR.StringMarker", false) as string == "synthetic marker",
                                "an uncontended snapshot preserves a materialized custom string");
                        }
                    }
                }
                finally
                {
                    if (previous.Data != null) Clipboard.SetDataObject(previous.Data, true);
                    else Clipboard.Clear();
                }
            }
        }

        private sealed class RacingData : DataObject
        {
            private readonly bool afterValue;
            private readonly Action acquire;
            internal bool Armed;
            internal RacingData(bool afterValue, Action acquire) { this.afterValue = afterValue; this.acquire = acquire; }
            private void Acquire(bool readingValue)
            {
                if (!Armed || readingValue != afterValue) return;
                Armed = false; acquire();
            }
            public override string[] GetFormats(bool autoConvert)
            {
                string[] formats = base.GetFormats(autoConvert);
                Acquire(false);
                return formats;
            }
            public override object GetData(string format, bool autoConvert)
            {
                object value = base.GetData(format, autoConvert);
                Acquire(true);
                return value;
            }
        }
    }
}

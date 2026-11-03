using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class MainScrollWorkTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private sealed class Counts
        {
            internal int SameOffsetMessages, WheelMessages, WheelLayouts, ClearLayouts, EmptyEditLayouts;
            internal bool Scrolled, SelectionPreserved, Cleared;
            internal double WheelMilliseconds, ClearMilliseconds, EmptyEditMilliseconds;
            public override string ToString()
            {
                return "sameOffset=" + SameOffsetMessages + " wheelScrollSets=" + WheelMessages + " wheelLayouts=" + WheelLayouts +
                    " clearLayouts=" + ClearLayouts + " emptyEditLayouts=" + EmptyEditLayouts + " wheel20Ms=" + Milliseconds(WheelMilliseconds) +
                    " clearMs=" + Milliseconds(ClearMilliseconds) + " emptyEditMs=" + Milliseconds(EmptyEditMilliseconds);
            }
            private static string Milliseconds(double value) { return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }
        }
        private sealed class Messages : NativeWindow, IDisposable
        {
            internal int ScrollSets;
            internal Messages(Control control) { AssignHandle(control.Handle); }
            protected override void WndProc(ref Message message) { if (message.Msg == 0x4de) ScrollSets++; base.WndProc(ref message); }
            public void Dispose() { ReleaseHandle(); }
        }
        internal static string Benchmark() { return Measure().ToString(); }
        internal static void Run(Action<bool, string> check)
        {
            Counts counts = Measure();
            check(counts.SameOffsetMessages == 0, "unchanged native document offsets do not resend EM_SETSCROLLPOS: " + counts);
            check(counts.Scrolled && counts.WheelMessages <= 20, "twenty wheel messages map the visible document at most once per message: " + counts);
            check(counts.WheelLayouts <= 20, "scrolling one visible provider does not lay out five offscreen providers: " + counts);
            check(counts.SelectionPreserved, "wheel work reduction preserves the same native result text selection");
            check(counts.Cleared && counts.ClearLayouts <= 24, "editing source clears all six populated providers with bounded card layout work: " + counts);
            check(counts.EmptyEditLayouts == 0, "further source edits do not lay out already empty result cards: " + counts);
        }
        private static Counts Measure()
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.RefreshSettings(new AppSettings {
                        TranslateWindowWidth = 360, TranslateWindowHeight = 680, TranslateAutoHeight = false, TranslateTopmost = false,
                        BingTranslateEnabled = true, ArkEnabled = true, BaiduLlmEnabled = true, BaiduDomainEnabled = true
                    });
                    form.Show(); form.SetSource("original");
                    var cards = Field<Dictionary<string, ServiceCard>>(form, "cards");
                    var panel = Field<ResultPanel>(form, "results");
                    string huge = String.Join("\n", Enumerable.Range(0, 10000).Select(index => "Line " + index)) + "\nEXTREME_END";
                    foreach (string provider in AppSettings.TranslationProviders) { form.SetActiveProvider(provider); form.SetOutput(huge); }
                    panel.ScrollBy(Int32.MinValue / 2); Application.DoEvents();
                    var text = cards["volc"].Controls.OfType<ResultTextBox>().Single();
                    var watchers = cards.Values.Select(card => new Messages(card.Controls.OfType<ResultTextBox>().Single())).ToArray();
                    int layouts = 0;
                    foreach (var card in cards.Values) card.Layout += delegate { layouts++; };
                    Action reset = delegate { layouts = 0; foreach (var watcher in watchers) watcher.ScrollSets = 0; };
                    try
                    {
                        var counts = new Counts();
                        reset();
                        for (int index = 0; index < 10; index++) text.PositionDocument(text.DocumentOffset);
                        counts.SameOffsetMessages = watchers.Sum(watcher => watcher.ScrollSets);
                        text.Select(5, 7); string selected = text.SelectedText;
                        int offset = panel.AutoScrollPosition.Y;
                        reset(); var watch = Stopwatch.StartNew();
                        for (int index = 0; index < 20; index++) Native.SendMessage(text.Handle, 0x20a, new IntPtr(unchecked((int)0xff880000)), IntPtr.Zero);
                        watch.Stop();
                        counts.WheelMilliseconds = watch.Elapsed.TotalMilliseconds;
                        counts.WheelMessages = watchers.Sum(watcher => watcher.ScrollSets); counts.WheelLayouts = layouts;
                        counts.Scrolled = panel.AutoScrollPosition.Y < offset; counts.SelectionPreserved = text.SelectedText == selected;
                        var source = Field<RichTextBox>(form, "source");
                        reset(); watch.Restart(); source.AppendText(" edited"); watch.Stop();
                        counts.ClearMilliseconds = watch.Elapsed.TotalMilliseconds;
                        counts.ClearLayouts = layouts; counts.Cleared = cards.Values.All(card => card.Value == "" && !card.Expanded);
                        reset(); watch.Restart(); source.AppendText(" again"); watch.Stop();
                        counts.EmptyEditMilliseconds = watch.Elapsed.TotalMilliseconds; counts.EmptyEditLayouts = layouts;
                        return counts;
                    }
                    finally { foreach (var watcher in watchers) watcher.Dispose(); form.Exiting = true; form.Close(); }
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class TooltipLifetimeTests
    {
        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            internal QuietForm() { StartPosition = FormStartPosition.Manual; Location = new Point(-30000, -30000); ShowInTaskbar = false; }
        }
        internal static void Run(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var first = new QuietForm())
                using (var second = new QuietForm())
                {
                    var a = new GlyphButton("copy", "first hint", delegate { });
                    var b = new GlyphButton("copy", "second hint", delegate { });
                    var c = new GlyphButton("copy", "other window", delegate { });
                    var panel = new Panel();
                    var nested = new GlyphButton("copy", "nested hint", delegate { });
                    panel.Controls.Add(nested);
                    first.Controls.AddRange(new Control[] { a, b, panel }); second.Controls.Add(c);
                    first.Show(); second.Show();
                    ToolTip firstTip = Hints(a), secondTip = Hints(c);
                    check(firstTip != null && secondTip != null && ReferenceEquals(firstTip, Hints(b)) && ReferenceEquals(firstTip, Hints(nested)) && !ReferenceEquals(firstTip, secondTip), "glyph buttons share one tooltip per window, including nested controls created before their form");
                    a.SetHint("updated hint");
                    check(firstTip.GetToolTip(a) == "updated hint" && a.AccessibleName == "updated hint", "updating a glyph hint changes its hover text and accessible name");
                    int firstDisposed = 0, secondDisposed = 0;
                    firstTip.Disposed += delegate { firstDisposed++; }; secondTip.Disposed += delegate { secondDisposed++; };
                    b.Dispose();
                    check(firstDisposed == 0 && firstTip.GetToolTip(a) == "updated hint", "disposing one button preserves its window's other tooltip registrations");
                    first.Controls.Remove(panel); second.Controls.Add(panel);
                    typeof(Control).GetMethod("OnMouseEnter", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nested, new object[] { EventArgs.Empty });
                    check(ReferenceEquals(Hints(nested), secondTip) && firstTip.GetToolTip(nested) == "" && secondTip.GetToolTip(nested) == "nested hint", "moving a nested button to another window transfers its hover hint");
                    first.Close();
                    c.SetHint("still available");
                    check(firstDisposed == 1 && secondDisposed == 0 && secondTip.GetToolTip(c) == "still available" && secondTip.GetToolTip(nested) == "nested hint", "closing one window disposes only its tooltip and preserves another window's hints");
                    second.Close();
                    check(secondDisposed == 1, "closing the final window disposes its shared tooltip once");
                }
                WeakReference[] closed = CreateAndCloseLastWindow();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                check(closed.All(reference => !reference.IsAlive), "the final closed OCR window and its tooltip can both be collected without opening another window");
            }
            finally { Ui.RenderOffscreen = previous; }
        }
        private static ToolTip Hints(GlyphButton button)
        {
            var field = typeof(GlyphButton).GetField("hints", BindingFlags.NonPublic | BindingFlags.Instance);
            return field == null ? null : (ToolTip)field.GetValue(button);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] CreateAndCloseLastWindow()
        {
            using (var form = new OcrForm { Exiting = true, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
            {
                form.Show();
                var tip = Hints(form.Controls.OfType<GlyphButton>().First());
                var result = new[] { new WeakReference(form), new WeakReference(tip) };
                form.Close(); return result;
            }
        }
    }
}

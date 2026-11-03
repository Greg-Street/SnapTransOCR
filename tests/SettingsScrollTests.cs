using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class SettingsScrollTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new SettingsForm(new AppSettings(), delegate { }))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                    form.ShowInTaskbar = false; form.Show();
                    var body = (Panel)typeof(SettingsForm).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    int layouts = 0;
                    foreach (var control in Descendants(body)) control.Layout += delegate { layouts++; };
                    foreach (string page in new[] { "翻译设置", "识别设置" })
                    {
                        form.SelectPage(page); Application.DoEvents();
                        body.AutoScrollPosition = Point.Empty; layouts = 0;
                        Wheel(body, -30);
                        check(body.AutoScrollPosition.Y == -30, page + " preserves partial wheel deltas without a full-notch jump");
                        Wheel(body, 30);
                        check(body.AutoScrollPosition == Point.Empty, page + " opposite wheel movement returns to the same top position");
                        for (int index = 0; index < 80; index++) Wheel(body, -120);
                        int bottom = body.AutoScrollPosition.Y;
                        Wheel(body, -120);
                        check(bottom < 0 && body.AutoScrollPosition.Y == bottom && !body.HorizontalScroll.Visible, page + " reaches and clamps its bottom without horizontal scrolling");
                        check(layouts == 0, page + " wheel movement does not remeasure or lay out its settings controls");
                    }
                    form.SelectPage("常规设置");
                    check(body.AutoScrollPosition == Point.Empty, "switching from scrolled settings restores the next page origin");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static void Wheel(Control control, int delta) { Native.SendMessage(control.Handle, 0x20a, new IntPtr(delta << 16), IntPtr.Zero); }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            yield return parent;
            foreach (Control child in parent.Controls) foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}

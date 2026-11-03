using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class WindowGeometryTests
    {
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Native.RECT rectangle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        internal static void Run(Action<bool, string> check)
        {
            bool wasOffscreen = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            try
            {
                using (var main = new MainForm()) Verify(main, check);
                using (var ocr = new OcrForm()) Verify(ocr, check);
                InitialScale(check);
                TranslationScrolling(check);
            }
            finally { Ui.RenderOffscreen = wasOffscreen; }
        }
        private static void InitialScale(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 144, 192, 240 })
            {
                using (var initialized = new Form()) { }
                Type helper = typeof(Control).Assembly.GetType("System.Windows.Forms.DpiHelper");
                var deviceDpi = helper.GetField("deviceDpi", BindingFlags.Static | BindingFlags.NonPublic);
                var scaling = helper.GetField("logicalToDeviceUnitsScalingFactor", BindingFlags.Static | BindingFlags.NonPublic);
                object previousDpi = deviceDpi.GetValue(null), previousScaling = scaling.GetValue(null);
                deviceDpi.SetValue(null, (double)dpi); scaling.SetValue(null, dpi / 96d);
                try
                {
                    using (var form = new MainForm())
                    {
                        var input = form.Controls.OfType<RoundedPanel>().Single(panel => panel.Controls.OfType<RichTextBox>().Any());
                        var actions = input.Controls.Cast<Control>().Where(control => control is GlyphButton || control is Button || control is CheckBox).ToArray();
                        check(form.Width == Ui.Scale(form, 520) && actions.All(control => input.ClientRectangle.Contains(control.Bounds))
                            && actions.All(control => actions.All(other => control == other || !control.Bounds.IntersectsWith(other.Bounds))),
                            "translation action bounds survive initial autoscaling at " + dpi + " DPI");
                        check(Math.Abs(input.Controls.OfType<RichTextBox>().Single().Font.Size - 11) < .2,
                            "translation preserves its font point size during startup autoscaling at " + dpi + " DPI");
                    }
                    using (var form = new OcrForm())
                    {
                        var content = form.Controls.OfType<RoundedPanel>().Single();
                        var actions = content.Controls.Cast<Control>().Where(control => (control is GlyphButton || control is Button || control is CheckBox) && control.AccessibleName != "停止识别" && control.AccessibleName != "保存识别结果为 Excel").ToArray();
                        check(form.Width == Ui.Scale(form, 560) && actions.All(control => content.ClientRectangle.Contains(control.Bounds))
                            && actions.All(control => actions.All(other => control == other || !control.Bounds.IntersectsWith(other.Bounds))),
                            "OCR action bounds survive initial autoscaling at " + dpi + " DPI");
                    }
                }
                finally { deviceDpi.SetValue(null, previousDpi); scaling.SetValue(null, previousScaling); }
            }
        }
        private static void TranslationScrolling(Action<bool, string> check)
        {
            using (var form = new MainForm())
            {
                form.RefreshSettings(new AppSettings { TranslateAutoHeight = false });
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                var source = form.Controls.OfType<RoundedPanel>().SelectMany(panel => panel.Controls.OfType<RichTextBox>()).Single();
                var results = form.Controls.OfType<Panel>().Single(panel => panel.Controls.OfType<ServiceCard>().Any());
                string longText = new string('W', 6000);
                form.SetSource(longText);
                foreach (int dpi in new[] { 96, 144, 192, 240, 96 })
                {
                    if (form.DeviceDpi != dpi) ChangeDpi(form, dpi);
                    bool aligned = true, sourceWrapped = true, resultsWrapped = true;
                    for (int i = 0; i < 8; i++)
                    {
                        form.ClientSize = new Size(Ui.Scale(form, i % 2 == 0 ? 480 : 560), Ui.Scale(form, i % 3 == 0 ? 850 : 460));
                        form.SetActiveProvider("volc"); form.SetOutput(i % 2 == 0 ? longText : "short");
                        form.SetActiveProvider("baidu"); form.SetOutput(longText);
                        if (i % 3 == 0) foreach (var card in results.Controls.OfType<ServiceCard>()) card.SetValue("", false);
                        results.AutoScrollPosition = new Point(100, 100);
                        form.SetStatus(i % 2 == 0 ? "working" : "");
                        Application.DoEvents();
                        aligned &= !results.HorizontalScroll.Visible && (GetWindowLongPtr(results.Handle, -16).ToInt64() & 0x00100000) == 0
                            && results.AutoScrollPosition.X == 0 && results.Controls.OfType<ServiceCard>().All(card => card.Left == 0 && card.Right <= results.ClientSize.Width);
                        sourceWrapped &= source.WordWrap && source.ScrollBars == RichTextBoxScrollBars.Vertical
                            && (GetWindowLongPtr(source.Handle, -16).ToInt64() & 0x00100000) == 0;
                        resultsWrapped &= results.Controls.OfType<ServiceCard>().SelectMany(card => card.Controls.OfType<RichTextBox>()).All(text => text.WordWrap
                            && text.ScrollBars == RichTextBoxScrollBars.None && (GetWindowLongPtr(text.Handle, -16).ToInt64() & 0x00300000) == 0);
                    }
                    check(aligned, "translation cards retain vertical-only scrolling through eight resize/expand/collapse cycles at " + dpi + " DPI");
                    check(sourceWrapped, "translation source retains soft-wrap and no native horizontal scrollbar at " + dpi + " DPI");
                    check(resultsWrapped, "long translation results retain soft-wrap without inner native scrollbars at " + dpi + " DPI");
                }
            }
        }
        private static void Verify(Form form, Action<bool, string> check)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            Size initial = form.ClientSize;
            int logicalWidth = form is MainForm ? 520 : 560;
            check(initial.Width == Ui.Scale(form, logicalWidth), form.GetType().Name + " opens at compact logical width");
            bool stable = true;
            for (int i = 0; i < 50; i++)
            {
                form.ClientSize = initial;
                Native.RECT client;
                GetClientRect(form.Handle, out client);
                stable &= form.Size == initial && form.ClientSize == initial && client.Right == initial.Width && client.Bottom == initial.Height;
            }
            check(stable, form.GetType().Name + " repeated client size agrees with native borderless geometry");
            int resizes = 0;
            form.Resize += delegate { resizes++; };
            for (int i = 0; i < 200; i++)
            {
                Native.SendMessage(form.Handle, 0x231, IntPtr.Zero, IntPtr.Zero); // WM_ENTERSIZEMOVE
                form.Location = new Point(-30000 + i % 20, -30000 + i / 20);
                Native.SendMessage(form.Handle, 0x232, IntPtr.Zero, IntPtr.Zero); // WM_EXITSIZEMOVE
            }
            check(form.Size == initial && form.ClientSize == initial && resizes == 0, form.GetType().Name + " 200 move loops neither grow nor relayout the window");
            for (int i = 0; i < 5; i++)
            {
                form.WindowState = FormWindowState.Minimized;
                form.WindowState = FormWindowState.Normal;
            }
            check(form.Size == initial && form.ClientSize == initial, form.GetType().Name + " minimize and restore retains window size");
            form.TopMost = true; form.TopMost = false;
            check((GetWindowLongPtr(form.Handle, -16).ToInt64() & 0x00040000) != 0, form.GetType().Name + " retains native resize frame after geometry and pin changes");
            foreach (int dpi in new[] { 144, 192, 240, 96 })
            {
                ChangeDpi(form, dpi);
                Native.RECT client;
                GetClientRect(form.Handle, out client);
                check(form.DeviceDpi == dpi && form.Width == Ui.Scale(form, logicalWidth) && form.Size == form.ClientSize
                    && client.Right == form.Width && client.Bottom == form.Height,
                    form.GetType().Name + " simulated WM_DPICHANGED " + dpi + " preserves logical width and native geometry");
            }
            form.ClientSize = new Size(form.MinimumSize.Width, form.ClientSize.Height);
            foreach (Control panel in form.Controls.OfType<RoundedPanel>()) VerifyActions(panel, check);
            var ocr = form as OcrForm;
            if (ocr != null)
            {
                ocr.Busy(true);
                VerifyActions(form.Controls.OfType<RoundedPanel>().Single(), check);
                ocr.Busy(false);
            }
        }
        private static void VerifyActions(Control panel, Action<bool, string> check)
        {
            var actions = panel.Controls.Cast<Control>().Where(control => control.Visible && (control is GlyphButton || control is Button || control is CheckBox)).ToArray();
            check(actions.All(control => panel.ClientRectangle.Contains(control.Bounds))
                && actions.All(control => actions.All(other => control == other || !control.Bounds.IntersectsWith(other.Bounds))),
                panel.FindForm().GetType().Name + " compact footer actions remain inside and do not overlap");
        }
        private static void ChangeDpi(Form form, int dpi)
        {
            double factor = (double)dpi / form.DeviceDpi;
            var suggested = new Native.RECT { Left = form.Left, Top = form.Top, Right = form.Left + (int)Math.Round(form.Width * factor), Bottom = form.Top + (int)Math.Round(form.Height * factor) };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
            try
            {
                Marshal.StructureToPtr(suggested, memory, false);
                Native.SendMessage(form.Handle, 0x2e0, new IntPtr(dpi | (dpi << 16)), memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}

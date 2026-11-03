using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class SettingsLayoutTests
    {
        internal static void Run(Action<bool, string> check)
        {
            InitialDpiLayout(check);
            AppSettings saved = null;
            var original = new AppSettings { BaiduOcrKey = "saved-credential", StartWithWindows = true };
            using (var form = new SettingsForm(original, value => saved = value))
            {
                var serviceFlags = new[] { "BaiduOcrEnabled", "TencentOcrEnabled", "VolcTranslateEnabled", "BaiduTranslateEnabled", "BingTranslateEnabled", "ArkEnabled", "BaiduLlmEnabled", "BaiduDomainEnabled" };
                var switches = Descendants(form).OfType<ToggleSwitch>().Where(control => serviceFlags.Contains(control.AccessibleName)).ToArray();
                check(switches.Length == serviceFlags.Length, "service availability uses the same switch control as other settings");
                switches.Single(control => control.AccessibleName == "BaiduOcrEnabled").Checked = false;
                switches.Single(control => control.AccessibleName == "VolcTranslateEnabled").Checked = false;
                typeof(SettingsForm).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                check(saved != null && !saved.BaiduOcrEnabled && saved.TencentOcrEnabled && !saved.VolcTranslateEnabled && saved.BaiduTranslateEnabled && !saved.BingTranslateEnabled, "saving switches retains each independent service state");
                check(saved.OcrProvider == "tencent" && saved.TranslateProvider == "baidu" && saved.BaiduOcrKey == original.BaiduOcrKey && saved.StartWithWindows, "saving switches selects available defaults and preserves credentials and startup state");
            }
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            {
                var fonts = new List<Font>();
                try
                {
                    using (var form = new SettingsForm(new AppSettings(), delegate { }))
                    {
                        SimulateDpi(form, dpi, fonts);
                        check(form.DeviceDpi == dpi, "settings receives the simulated WM_DPICHANGED at " + dpi + " DPI");
                        var navigation = Descendants(form).OfType<NavButton>().OrderBy(control => control.Top).ToArray();
                        check(navigation.Length == 7 && navigation.Zip(navigation.Skip(1), (a, b) => a.Bottom <= b.Top).All(value => value), "settings navigation rows do not overlap at " + dpi + " DPI");
                        check(navigation.All(control => TextRenderer.MeasureText(control.Text, control.Font).Width <= control.Width - Ui.Scale(control, 68)), "settings navigation captions fit at " + dpi + " DPI");
                        var save = Descendants(form).OfType<Button>().Single(control => control.Text == "保存设置");
                        var cancel = Descendants(form).OfType<Button>().Single(control => control.Text == "取消");
                        check(save.Right <= save.Parent.ClientSize.Width && cancel.Right < save.Left && save.Bottom <= save.Parent.Height, "settings footer buttons remain visible at " + dpi + " DPI");
                        foreach (string title in new[] { "常规设置", "翻译设置", "识别设置" })
                        {
                            form.SelectPage(title); form.PerformLayout();
                            var pages = (Dictionary<string, FlowLayoutPanel>)typeof(SettingsForm).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                            var page = pages[title];
                            check(page.Controls.Cast<Control>().All(control => control.Right <= page.Width) && page.Height >= page.Controls.Cast<Control>().Max(control => control.Bottom), title + " content stays inside its scrollable page at " + dpi + " DPI");
                            foreach (var card in page.Controls.OfType<RoundedPanel>())
                            {
                                var table = card.Controls.OfType<TableLayoutPanel>().Single();
                                check(table.Controls.Cast<Control>().Where(control => table.RowStyles[table.GetRow(control)].SizeType != SizeType.Absolute || table.RowStyles[table.GetRow(control)].Height > 0).All(control => control.Right <= table.ClientSize.Width && control.Bottom <= table.ClientSize.Height), title + " visible card controls fit at " + dpi + " DPI");
                            }
                        }
                        form.SelectPage("历史记录");
                        var history = Descendants(form).OfType<HistoryView>().Single();
                        var historyBody = (Panel)typeof(SettingsForm).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                        check(history.Parent.ClientRectangle.Contains(history.Bounds) && historyBody.ClientRectangle.Contains(history.Parent.Bounds) && !historyBody.HorizontalScroll.Visible, "embedded history fills the settings content without horizontal scrolling at " + dpi + " DPI");
                        check(Descendants(history).OfType<RichTextBox>().All(control => control.Parent.ClientRectangle.Contains(control.Bounds)), "embedded history text controls stay inside their cards at " + dpi + " DPI");
                        check(!Descendants(form).Any(control => control.AccessibleName == "TencentRegion"), "nearest OCR endpoint needs no regional input at " + dpi + " DPI");
                    }
                }
                finally { foreach (Font font in fonts) font.Dispose(); }
                using (var parent = new Panel { BackColor = Color.White })
                using (var toggle = new ToggleSwitch { Checked = true, Size = new Size(46 * dpi / 96, 28 * dpi / 96) })
                using (var bitmap = new Bitmap(toggle.Width, toggle.Height))
                {
                    parent.Controls.Add(toggle); toggle.DrawToBitmap(bitmap, toggle.ClientRectangle);
                    check(bitmap.GetPixel(toggle.Width / 4, toggle.Height / 2).ToArgb() == Ui.Accent.ToArgb() && bitmap.GetPixel(toggle.Width * 3 / 4, toggle.Height / 2).ToArgb() == Color.White.ToArgb(), "toggle track and thumb scale with control at " + dpi + " DPI");
                    check(bitmap.GetPixel(toggle.Width - 1, toggle.Height - 1).ToArgb() == Color.White.ToArgb(), "toggle clears the checkbox background at " + dpi + " DPI");
                }
            }
            var transitionFonts = new List<Font>();
            bool previousOffscreen = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            try
            {
                using (var form = new SettingsForm(new AppSettings(), delegate { }))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.ShowInTaskbar = false;
                    form.Show(); Application.DoEvents();
                    Point edge = new Point(2, form.ClientSize.Height / 2);
                    check(ChildWindowFromPointEx(form.Handle, edge, 7) == form.Handle, "settings resize edge is not covered by a child window");
                    Point desktopEdge = form.PointToScreen(edge);
                    var packedEdge = new IntPtr(unchecked((desktopEdge.Y << 16) | (desktopEdge.X & 0xffff)));
                    check(Native.SendMessage(form.Handle, 0x84, IntPtr.Zero, packedEdge).ToInt32() == 10, "uncovered settings edge uses the native horizontal resize hit test");
                    var body = (Panel)typeof(SettingsForm).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    var pages = (Dictionary<string, FlowLayoutPanel>)typeof(SettingsForm).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    foreach (int dpi in new[] { 96, 240, 96 })
                    {
                        SimulateDpi(form, dpi, transitionFonts);
                        form.SelectPage("翻译设置"); body.AutoScrollPosition = new Point(0, pages["翻译设置"].Height);
                        form.SelectPage("识别设置"); form.SelectPage("关于"); form.SelectPage("翻译设置");
                        Application.DoEvents();
                        check(body.AutoScrollPosition == Point.Empty && pages["翻译设置"].Left == 0 && !body.HorizontalScroll.Visible, "scroll resets when switching hidden pages after " + dpi + " DPI transition");
                        check((GetWindowLong(body.Handle, -16).ToInt64() & 0x00100000) == 0, "DPI transition leaves no native horizontal scrollbar at " + dpi + " DPI");
                    }
                }
            }
            finally { Ui.RenderOffscreen = previousOffscreen; foreach (Font font in transitionFonts) font.Dispose(); }
        }
        private static void InitialDpiLayout(Action<bool, string> check)
        {
            // Recreate launch on a high-DPI display before constructing controls; no DPI message or font patch.
            using (var initialize = new Form()) { }
            Type helper = typeof(Control).Assembly.GetType("System.Windows.Forms.DpiHelper");
            FieldInfo dpiField = helper.GetField("deviceDpi", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo factorField = helper.GetField("logicalToDeviceUnitsScalingFactor", BindingFlags.NonPublic | BindingFlags.Static);
            object originalDpi = dpiField.GetValue(null), originalFactor = factorField.GetValue(null);
            try
            {
                foreach (int dpi in new[] { 144, 192, 240 })
                {
                    dpiField.SetValue(null, (double)dpi); factorField.SetValue(null, dpi / 96.0);
                    using (var form = new SettingsForm(new AppSettings(), delegate { }))
                    {
                        var sidebar = (Panel)typeof(SettingsForm).GetField("sidebar", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                        var brand = (Label)typeof(SettingsForm).GetField("brand", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                        var navigation = Descendants(form).OfType<NavButton>().OrderBy(control => control.Top).ToArray();
                        check(form.DeviceDpi == dpi && sidebar.Width == 208 * dpi / 96, "settings sidebar uses initial display DPI before its first handle at " + dpi);
                        Size limit = SystemInformation.MaxWindowTrackSize;
                        check(form.ClientSize.Width == Math.Min(1000 * dpi / 96, limit.Width) && form.ClientSize.Height == Math.Min(730 * dpi / 96, limit.Height), "settings initial size scales once within native window-track limits at " + dpi + " DPI");
                        check(brand.Right < sidebar.ClientSize.Width && !sidebar.HorizontalScroll.Visible, "settings brand fits the initial high-DPI sidebar at " + dpi);
                        check(navigation.All(control => control.Height == (int)Math.Round(47 * dpi / 96.0)) && navigation.Zip(navigation.Skip(1), (a, b) => a.Bottom <= b.Top).All(value => value), "settings navigation has scaled spacing on initial launch at " + dpi);
                    }
                }
            }
            finally { dpiField.SetValue(null, originalDpi); factorField.SetValue(null, originalFactor); }
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point point, uint flags);
        internal static void SimulateDpi(SettingsForm form, int dpi, List<Font> fonts)
        {
            form.PerformAutoScale();
            var controls = new[] { (Control)form }.Concat(Descendants(form)).ToArray();
            foreach (Control control in controls) { IntPtr handle = control.Handle; }
            var originals = controls.Select(control => control.Font).ToArray();
            float scale = dpi / (float)form.DeviceDpi;
            var deviceDpi = typeof(Control).GetField("deviceDpi", BindingFlags.Instance | BindingFlags.NonPublic);
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
            try
            {
                var suggested = new Native.RECT { Left = form.Left, Top = form.Top, Right = form.Left + (int)Math.Round(form.Width * scale), Bottom = form.Top + (int)Math.Round(form.Height * scale) };
                Marshal.StructureToPtr(suggested, pointer, false);
                Native.SendMessage(form.Handle, 0x02e0, new IntPtr(dpi | (dpi << 16)), pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
            // Child native windows remain on the test display; model their effective target DPI for custom painting.
            foreach (Control control in controls.Skip(1)) deviceDpi.SetValue(control, dpi);
            // Explicit child fonts need the OS child-DPI notifications, unavailable on the unchanged test display.
            for (int i = 1; i < controls.Length; i++)
                if (scale != 1 && Math.Abs(controls[i].Font.Size - originals[i].Size) < .01f)
                {
                    var font = new Font(originals[i].FontFamily, originals[i].Size * scale, originals[i].Style);
                    fonts.Add(font); controls[i].Font = font;
                }
            form.PerformLayout(); form.SelectPage("常规设置");
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
    }
}

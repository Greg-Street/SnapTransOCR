using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class WindowMotionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new SettingsForm(new AppSettings(), delegate { }))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false; form.Show();
                    var pages = Field<Dictionary<string, FlowLayoutPanel>>(form, "pages");
                    var containers = new[] { Field<Panel>(form, "header"), Field<Panel>(form, "body"), Field<Panel>(form, "sidebar"), Field<Panel>(form, "footer"), pages["常规设置"] }
                        .Concat(Descendants(pages["常规设置"]).OfType<Panel>());
                    check(containers.All(DefersBackgroundErase), "settings containers keep the previous frame intact until their buffered paint is ready");
                    int hiddenLayouts = 0, tableLayouts = 0;
                    foreach (var page in pages.Where(pair => pair.Key != "常规设置").Select(pair => pair.Value))
                        foreach (var control in new[] { (Control)page }.Concat(Descendants(page))) control.Layout += delegate { hiddenLayouts++; };
                    foreach (var table in Descendants(pages["常规设置"]).OfType<TableLayoutPanel>()) table.Layout += delegate { tableLayouts++; };
                    form.ClientSize = new Size(form.ClientSize.Width + 31, form.ClientSize.Height + 17);
                    check(hiddenLayouts == 0, "resizing settings lays out only its selected page, without work in hidden pages");
                    tableLayouts = 0; form.ClientSize = new Size(form.ClientSize.Width, form.ClientSize.Height + 23);
                    check(tableLayouts == 0, "height-only settings resize does not remeasure unchanged table widths");
                    Region region = form.Region; form.Location = new Point(-29980, -29970);
                    check(ReferenceEquals(region, form.Region), "moving a window preserves its existing native corner region");
                    form.SelectPage("翻译设置");
                    var pageShown = pages["翻译设置"];
                    check(pageShown.Controls.Cast<Control>().All(control => control.Right <= pageShown.ClientSize.Width) &&
                        pageShown.Height >= pageShown.Controls.Cast<Control>().Max(control => control.Bottom), "a previously hidden settings page uses the current width when opened");
                }
                foreach (int dpi in new[] { 96, 144, 192, 240 })
                using (var form = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.RefreshSettings(new AppSettings { TranslateWindowWidth = 360, TranslateWindowHeight = 560, TranslateAutoHeight = false, BingTranslateEnabled = true });
                    form.Show(); ChangeDpi(form, dpi);
                    form.SetSource("Synthetic original content"); form.SetOutput("Synthetic translation 用于窄窗口布局检查。");
                    int width = 360 * dpi / 96;
                    check(form.ClientSize.Width == width && form.MinimumSize.Width == width, "translation can restore a 360-logical-pixel window at " + dpi + " DPI");
                    var input = Field<RoundedPanel>(form, "input"); var language = Field<RoundedPanel>(form, "language"); var results = Field<Panel>(form, "results");
                    var cards = Field<Dictionary<string, ServiceCard>>(form, "cards");
                    check(input.Left == language.Left && input.Left == results.Left && language.Width == input.Width && cards.Values.All(card => card.Width == input.Width), "input, language and translated result cards share both horizontal edges at " + dpi + " DPI");
                    check(input.Controls.Cast<Control>().All(control => input.ClientRectangle.Contains(control.Bounds)) && language.Controls.Cast<Control>().All(control => language.ClientRectangle.Contains(control.Bounds)), "narrow translation input actions and language controls remain inside their panels at " + dpi + " DPI");
                    var actions = input.Controls.OfType<GlyphButton>().ToArray();
                    check(actions.SelectMany((first, index) => actions.Skip(index + 1).Select(second => !first.Bounds.IntersectsWith(second.Bounds))).All(value => value), "narrow translation action buttons do not overlap at " + dpi + " DPI");
                    check(!results.HorizontalScroll.Visible && Descendants(form).OfType<RichTextBox>().All(text => text.WordWrap && text.ScrollBars == (text == Field<RichTextBox>(form, "source") ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None)), "narrow translation content stays vertically scrollable without a horizontal scrollbar at " + dpi + " DPI");
                    check(MainForm.RestoredSize(360, 560, dpi, new Size(5000, 5000)).Width == width, "remembered narrow translation width survives recreation at " + dpi + " DPI");
                    var before = Descendants(form).ToDictionary(control => control, control => control.Bounds);
                    float sourceFont = Field<RichTextBox>(form, "source").Font.Size;
                    Size buttonSize = actions[0].Size, initialSize = form.ClientSize;
                    form.ClientSize = new Size(initialSize.Width + 240 * dpi / 96, initialSize.Height + 160 * dpi / 96);
                    check(actions.All(button => button.Size == buttonSize || button == Field<GlyphButton>(form, "translate")) && Field<RichTextBox>(form, "source").Font.Size == sourceFont, "translation resize preserves readable font and action sizes at " + dpi + " DPI");
                    form.ClientSize = initialSize;
                    check(before.All(pair => pair.Key.Bounds == pair.Value), "translation narrow-wide-narrow round trip has no accumulated layout drift at " + dpi + " DPI");
                }
                foreach (int dpi in new[] { 96, 144, 192, 240 })
                using (var form = new SettingsForm(new AppSettings(), delegate { }) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.Show(); ChangeDpi(form, dpi);
                    form.ClientSize = new Size(860 * dpi / 96, 580 * dpi / 96);
                    var pages = Field<Dictionary<string, FlowLayoutPanel>>(form, "pages");
                    var body = Field<Panel>(form, "body");
                    foreach (string title in new[] { "常规设置", "热键设置", "截图设置", "翻译设置", "识别设置", "关于" })
                    {
                        form.SelectPage(title);
                        var page = pages[title];
                        string context = title + " at " + dpi + " DPI";
                        check(DescriptionsFit(page), "settings titles and descriptions fit when first selecting a narrow page: " + context);
                        var before = Descendants(page).ToDictionary(control => control, control => control.Bounds);
                        var labels = Descendants(page).OfType<Label>().ToDictionary(label => label, label => label.Font.Size);
                        var buttons = Descendants(page).OfType<ToggleSwitch>().ToDictionary(button => button, button => button.Size);
                        form.ClientSize = new Size(1200 * dpi / 96, 820 * dpi / 96);
                        check(DescriptionsFit(page), "settings titles and descriptions remain fully laid out after widening: " + context);
                        check(labels.All(pair => pair.Key.Font.Size == pair.Value) && buttons.All(pair => pair.Key.Size == pair.Value), "settings resize preserves text and toggle sizes: " + context);
                        form.ClientSize = new Size(860 * dpi / 96, 580 * dpi / 96);
                        check(DescriptionsFit(page) && before.All(pair => pair.Key.Bounds == pair.Value) && !body.HorizontalScroll.Visible,
                            "settings narrow-wide-narrow round trip retains complete text and layout without horizontal scrolling: " + context + ": body=" + body.Bounds + " client=" + body.ClientSize + " display=" + body.DisplayRectangle + " page=" + page.Bounds + " " + String.Join("; ", before.Where(pair => pair.Key.Bounds != pair.Value).Select(pair => pair.Key.GetType().Name + " " + pair.Value + " -> " + pair.Key.Bounds).Take(8)));
                    }
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static bool DescriptionsFit(Control page)
        {
            return Descendants(page).OfType<TableLayoutPanel>().All(table => Enumerable.Range(0, table.RowCount).All(row =>
            {
                if (table.RowStyles[row].SizeType == SizeType.Absolute && table.RowStyles[row].Height == 0) return true;
                var text = table.GetControlFromPosition(0, row);
                return text.Right <= table.GetColumnWidths()[0] && text.Controls.OfType<Label>().All(label => text.ClientRectangle.Contains(label.Bounds)
                    && label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height);
            }));
        }
        private static bool DefersBackgroundErase(Control control)
        {
            using (var bitmap = new Bitmap(4, 4))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Magenta);
                IntPtr dc = graphics.GetHdc();
                try { Native.SendMessage(control.Handle, 0x14, dc, IntPtr.Zero); }
                finally { graphics.ReleaseHdc(dc); }
                return bitmap.GetPixel(2, 2).ToArgb() == Color.Magenta.ToArgb();
            }
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (var control in Descendants(child)) yield return control; }
        }
        private static void ChangeDpi(Form form, int dpi)
        {
            if (form.DeviceDpi == dpi) return;
            double factor = dpi / (double)form.DeviceDpi;
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new[] { form.Left, form.Top, form.Left + (int)Math.Round(form.Width * factor), form.Top + (int)Math.Round(form.Height * factor) }, 0, memory, 4);
                Native.SendMessage(form.Handle, 0x02e0, new IntPtr(dpi | dpi << 16), memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}

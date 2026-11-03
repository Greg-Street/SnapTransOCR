using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ResultScrollingTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
        internal static void Run(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                foreach (int dpi in new[] { 96, 192 })
                using (var form = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.RefreshSettings(new AppSettings { TranslateWindowWidth = 360, TranslateWindowHeight = 680, TranslateAutoHeight = false, BingTranslateEnabled = true, TranslateTopmost = false });
                    form.Show(); ChangeDpi(form, dpi);
                    var cards = Field<Dictionary<string, ServiceCard>>(form, "cards");
                    var panel = Field<ResultPanel>(form, "results");
                    string normal = String.Join("\n", Enumerable.Range(0, 85).Select(i => "Line " + i + " 中文译文 with wrapping and punctuation.")) + "\nNORMAL_END";
                    string huge = String.Join("\n", Enumerable.Range(0, 10000).Select(i => "Line " + i)) + "\nEXTREME_END";
                    foreach (var pair in cards) { form.SetActiveProvider(pair.Key); form.SetOutput(pair.Key == "baidu" ? huge : normal); }
                    check(cards["volc"].MeasureHeight(cards["volc"].Width) > Ui.Scale(form, 310) && cards["baidu"].MeasureHeight(cards["baidu"].Width) > 65535, "results retain complete normal and extreme document heights at " + dpi + " DPI");
                    check(cards.Values.All(card => Text(card).ScrollBars == RichTextBoxScrollBars.None && Text(card).WordWrap) && panel.VerticalScroll.Visible && !panel.HorizontalScroll.Visible, "three providers share one outer vertical scrollbar at " + dpi + " DPI: V=" + panel.VerticalScroll.Visible + " H=" + panel.HorizontalScroll.Visible + " client=" + panel.ClientSize + " min=" + panel.AutoScrollMinSize);
                    check(cards.Values.All(card => card.Width == Field<RoundedPanel>(form, "input").Width && card.Height <= panel.ClientSize.Height), "viewport-sized cards preserve input alignment and native size limits at " + dpi + " DPI");
                    var text = Text(cards["volc"]);
                    check(cards.Values.All(card => (Native.SendMessage(Text(card).Handle, 0x44e, IntPtr.Zero, IntPtr.Zero).ToInt64() & 1) == 0), "native result editors disable automatic whole-word expansion at " + dpi + " DPI");
                    int before = -panel.AutoScrollPosition.Y;
                    Native.SendMessage(text.Handle, 0x20a, new IntPtr(unchecked((int)0xff880000)), IntPtr.Zero);
                    check(-panel.AutoScrollPosition.Y > before, "wheel over a result scrolls the shared result viewport at " + dpi + " DPI");
                    foreach (string provider in new[] { "volc", "baidu", "bing" })
                    {
                        var card = cards[provider]; text = Text(card);
                        panel.ScrollBy(card.LogicalTop + Ui.Scale(form, 46) + panel.AutoScrollPosition.Y);
                        text.Select(text.TextLength, 0); text.ScrollToCaret(); Application.DoEvents();
                        Point tail = text.GetPositionFromCharIndex(text.TextLength);
                        int screenY = card.Top + text.Top + tail.Y;
                        check(card.Visible && text.Visible && screenY >= 0 && screenY + text.Font.Height <= panel.ClientSize.Height + 2, "native caret navigation exposes the final line for " + provider + " at " + dpi + " DPI: " + screenY + "/" + panel.ClientSize.Height);
                        string ending = provider == "baidu" ? "EXTREME_END" : "NORMAL_END";
                        text.Select(text.TextLength - ending.Length, ending.Length);
                        check(text.SelectedText == ending && text.ReadOnly, "tail remains directly selectable in the full native document for " + provider + " at " + dpi + " DPI");
                        Key(text, Keys.A, true);
                        check(text.SelectionStart == 0 && text.SelectionLength == text.TextLength, "Ctrl+A selects the complete result across clipped viewports for " + provider + " at " + dpi + " DPI");
                        text.Select(0, 0); text.ScrollToCaret(); Application.DoEvents();
                        check(text.GetPositionFromCharIndex(0).Y >= -1 && card.Top + text.Top >= 0, "returning to the first character restores the shared viewport for " + provider + " at " + dpi + " DPI");
                    }
                    var extreme = cards["baidu"]; text = Text(extreme);
                    foreach (int offset in new[] { 0, 10, 45, 50, 71, 1000, 65530, 65536, 131100, extreme.MeasureHeight(extreme.Width) - Ui.Scale(form, 100) })
                    {
                        panel.ScrollBy(extreme.LogicalTop + offset + panel.AutoScrollPosition.Y);
                        int origin = extreme.Top + text.Top + text.GetPositionFromCharIndex(0).Y;
                        check(origin == extreme.LogicalTop + Ui.Scale(form, 46) + panel.AutoScrollPosition.Y, "full document pixel origin stays continuous at offset " + offset + " and " + dpi + " DPI: " + origin);
                    }
                    panel.ScrollBy(extreme.LogicalTop + panel.AutoScrollPosition.Y);
                    text.Focus(); Key(text, Keys.End, true);
                    check(text.SelectionStart == text.TextLength && -panel.AutoScrollPosition.Y > extreme.LogicalTop + 65535, "Ctrl+End synchronizes the outer scroll beyond native child size limits at " + dpi + " DPI");
                    int endPosition = -panel.AutoScrollPosition.Y;
                    Key(text, Keys.Home, true, true);
                    check(text.SelectionStart == 0 && text.SelectionLength == text.TextLength && -panel.AutoScrollPosition.Y < endPosition, "Ctrl+Shift+Home selects backwards and scrolls to the active caret at " + dpi + " DPI");
                    text = Text(cards["baidu"]); panel.ScrollBy(cards["baidu"].LogicalTop + panel.AutoScrollPosition.Y);
                    text.Select(text.TextLength - 11, 11);
                    int selection = text.SelectionStart;
                    Size original = form.ClientSize;
                    form.ClientSize = new Size(original.Width + Ui.Scale(form, 250), original.Height + Ui.Scale(form, 90));
                    form.ClientSize = original;
                    check(text.SelectionStart == selection && text.SelectedText == "EXTREME_END" && !panel.HorizontalScroll.Visible, "narrow-wide-narrow resize preserves the complete document selection at " + dpi + " DPI: selected=" + text.SelectedText + " index=" + text.SelectionStart + "/" + selection + " horizontal=" + panel.HorizontalScroll.Visible);
                    panel.ScrollBy(Int32.MinValue / 2);
                    form.SetActiveProvider("volc"); form.SetOutput("replacement");
                    check(cards["volc"].Expanded && Text(cards["volc"]).Text == "replacement", "replacing a long translation invalidates its measurement at " + dpi + " DPI");
                    cards["volc"].SetValue("replacement", false);
                    check(cards["volc"].Expanded, "nonempty results stay fully expanded at " + dpi + " DPI");
                    form.Exiting = true; form.Close();
                }
            }
            finally { Ui.RenderOffscreen = previous; }
        }
        private static ResultTextBox Text(ServiceCard card) { return card.Controls.OfType<ResultTextBox>().Single(); }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static void Key(Control control, Keys key, bool controlDown, bool shiftDown = false)
        {
            var previous = new byte[256]; GetKeyboardState(previous);
            var state = (byte[])previous.Clone(); state[(int)Keys.ControlKey] = controlDown ? (byte)0x80 : (byte)0;
            state[(int)Keys.ShiftKey] = shiftDown ? (byte)0x80 : (byte)0;
            try { SetKeyboardState(state); Native.SendMessage(control.Handle, 0x100, new IntPtr((int)key), IntPtr.Zero); }
            finally { SetKeyboardState(previous); }
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

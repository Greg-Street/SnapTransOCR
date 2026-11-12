using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class DropdownTests
    {
        internal static void Run(Action<bool, string> check)
        {
            SelectionCancellation(check);
            EditableSelection(check);
            HeightConsistency(check);
            using (var form = new SettingsForm(new AppSettings(), delegate { }))
                check(Descendants(form).OfType<ComboBox>().Count() == 12 && Descendants(form).OfType<ComboBox>().All(control => control is StyledComboBox), "all twelve settings dropdowns, including capture border width, use the shared native styled control");
            using (var form = new MainForm())
                check(Descendants(form).OfType<ComboBox>().Count() == 2 && Descendants(form).OfType<ComboBox>().All(control => control is StyledComboBox), "both translation language dropdowns use the shared style");
            using (var parent = new RoundedPanel { Fill = Color.White, Size = new Size(300, 100) })
            using (var combo = Ui.Combo(new[] { "细 · 2 像素", "中 · 4 像素", "粗 · 8 像素" }, 0))
            {
                parent.Controls.Add(combo);
                IntPtr handle = combo.Handle;
                check(combo.DropDownStyle == ComboBoxStyle.DropDownList && combo.AccessibilityObject.Role == AccessibleRole.ComboBox, "styled dropdown retains native list and accessibility semantics");
                int changes = 0; combo.SelectedIndexChanged += delegate { changes++; };
                Native.SendMessage(handle, 0x0100, new IntPtr((int)Keys.Down), IntPtr.Zero);
                check(combo.SelectedIndex == 1 && changes == 1, "native down arrow changes the selection exactly once");
                using (var bitmap = new Bitmap(combo.Width, combo.Height))
                {
                    combo.DrawToBitmap(bitmap, combo.ClientRectangle);
                    check(bitmap.GetPixel(combo.Width - 8, combo.Height / 2).ToArgb() == Color.White.ToArgb(), "dropdown arrow has a white surface without the native gray button");
                    check(bitmap.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "rounded dropdown corners use the surrounding surface color");
                }
                using (var bitmap = new Bitmap(200, combo.ItemHeight))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var paint = typeof(StyledComboBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic);
                    paint.Invoke(combo, new object[] { new DrawItemEventArgs(graphics, combo.Font, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 1, DrawItemState.Selected) });
                    check(bitmap.GetPixel(190, bitmap.Height / 2).ToArgb() == Color.FromArgb(232, 238, 255).ToArgb(), "dropdown selected rows use pale blue without the native dark selection fill");
                    combo.Items.Clear();
                    paint.Invoke(combo, new object[] { new DrawItemEventArgs(graphics, combo.Font, new Rectangle(0, 0, bitmap.Width, bitmap.Height), -1, DrawItemState.None) });
                    check(combo.SelectedIndex == -1, "empty provider lists remain safe to paint");
                }
            }
            using (var initialize = new Form()) { }
            Type helper = typeof(Control).Assembly.GetType("System.Windows.Forms.DpiHelper");
            FieldInfo dpiField = helper.GetField("deviceDpi", BindingFlags.NonPublic | BindingFlags.Static), factorField = helper.GetField("logicalToDeviceUnitsScalingFactor", BindingFlags.NonPublic | BindingFlags.Static);
            object oldDpi = dpiField.GetValue(null), oldFactor = factorField.GetValue(null);
            try
            {
                foreach (int dpi in new[] { 144, 192, 240 })
                {
                    dpiField.SetValue(null, (double)dpi); factorField.SetValue(null, dpi / 96.0);
                    using (var form = new MainForm())
                        check(Descendants(form).OfType<ComboBox>().All(control => control.Bottom <= control.Parent.ClientSize.Height), "language dropdowns fit their initial scaled toolbar at " + dpi + " DPI");
                    using (var combo = Ui.Combo(new[] { "细 · 2 像素", "中 · 4 像素", "粗 · 8 像素" }, 0))
                    {
                        check(!combo.IsHandleCreated, "dropdown construction preserves the initial DPI before native handle creation at " + dpi);
                        check(combo.DeviceDpi == dpi && combo.ItemHeight >= 28 * dpi / 96 && combo.ItemHeight >= combo.Font.Height + 8 * dpi / 96, "dropdown initial row height fits text and padding at " + dpi + " DPI");
                        typeof(StyledComboBox).GetMethod("OnDropDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(combo, new object[] { EventArgs.Empty });
                        check(combo.DropDownHeight >= combo.ItemHeight * 3 && combo.DropDownWidth >= combo.Width, "native dropdown dimensions include complete rows at " + dpi + " DPI");
                        using (var larger = new Font(combo.Font.FontFamily, 20))
                        {
                            combo.Font = larger;
                            check(combo.ItemHeight >= larger.Height + 8 * dpi / 96, "dropdown row height follows font changes at " + dpi + " DPI");
                        }
                    }
                }
            }
            finally { dpiField.SetValue(null, oldDpi); factorField.SetValue(null, oldFactor); }
        }
        private static void HeightConsistency(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            var fonts = new List<Font>();
            try
            {
                using (var form = new SettingsForm(new AppSettings { HistoryEnabled = false }, delegate { })
                    { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.Show();
                    foreach (int dpi in new[] { 96, 144, 192, 240, 96 })
                    {
                        SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                        form.SelectPage("翻译设置");
                        var combos = Descendants(form).OfType<ComboBox>().Where(combo => combo.Visible).ToArray();
                        check(combos.All(HeightMatches), "native, managed and preferred dropdown heights agree after the " + dpi + " DPI transition");
                        bool fontHeightsMatch = true;
                        foreach (var combo in combos)
                        {
                            Font original = combo.Font;
                            using (var larger = new Font(original.FontFamily, original.Size + 3, original.Style))
                            {
                                combo.Font = larger; form.PerformLayout();
                                fontHeightsMatch &= HeightMatches(combo);
                                combo.Font = original; form.PerformLayout();
                                fontHeightsMatch &= HeightMatches(combo);
                            }
                        }
                        check(fontHeightsMatch, "dropdown height remains consistent when its font grows and is restored at " + dpi + " DPI");
                        var observers = combos.Select(combo => new HeightObserver(combo)).ToArray();
                        try
                        {
                            for (int step = 0; step < 20; step++) { form.Width += step % 2 == 0 ? 2 : -2; form.Update(); }
                            check(observers.All(observer => !observer.Oscillated) && combos.All(HeightMatches), "horizontal settings resizing does not oscillate native dropdown heights at " + dpi + " DPI");
                        }
                        finally { foreach (var observer in observers) observer.ReleaseHandle(); }
                    }
                }
            }
            finally { Ui.RenderOffscreen = offscreen; foreach (var font in fonts) font.Dispose(); }
        }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Native.RECT rectangle);
        private static bool HeightMatches(ComboBox combo)
        {
            Native.RECT rectangle;
            return GetWindowRect(combo.Handle, out rectangle) && rectangle.Bottom - rectangle.Top == combo.Height && combo.Height == combo.PreferredHeight;
        }
        private sealed class HeightObserver : NativeWindow
        {
            private readonly int expected;
            internal bool Oscillated;
            internal HeightObserver(ComboBox combo) { expected = combo.Height; AssignHandle(combo.Handle); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0005 && ((message.LParam.ToInt64() >> 16) & 0xffff) != expected) Oscillated = true;
                base.WndProc(ref message);
            }
        }
        private static void SelectionCancellation(Action<bool, string> check)
        {
            using (var combo = Ui.Combo(new[] { "First", "Second", "Third" }, 0))
            {
                int commits = 0;
                combo.SelectionChangeCommitted += delegate { commits++; };
                Raise(combo, "OnDropDown");
                combo.SelectedIndex = 1;
                Raise(combo, "OnDropDownClosed");
                check(combo.SelectedIndex == 0 && commits == 0, "cancelled native dropdown restores the original selection without committing");

                foreach (int index in new[] { 1, 2 })
                {
                    Raise(combo, "OnDropDown");
                    combo.SelectedIndex = index;
                    // Enter and mouse selection both deliver SelectionChangeCommitted before closing.
                    Raise(combo, "OnSelectionChangeCommitted");
                    Raise(combo, "OnDropDownClosed");
                    check(combo.SelectedIndex == index && commits == index, "confirmed native dropdown keeps selection " + index + " after closing");
                }
                Raise(combo, "OnDropDown");
                combo.SelectedIndex = 0;
                Raise(combo, "OnDropDownClosed");
                check(combo.SelectedIndex == 2 && commits == 2, "reopened dropdown cancellation returns to the last confirmed selection");

                combo.SelectedIndex = 2;
                IntPtr handle = combo.Handle;
                Native.SendMessage(handle, 0x0100, new IntPtr((int)Keys.Up), IntPtr.Zero);
                check(combo.SelectedIndex == 1, "closed dropdown arrow navigation remains usable after a cancelled opening");

                Raise(combo, "OnDropDown");
                combo.Items.Clear();
                Raise(combo, "OnDropDownClosed");
                check(combo.Items.Count == 0 && combo.SelectedIndex == -1, "clearing an open dropdown leaves an empty selection without restoring an invalid index");

                combo.Items.AddRange(new object[] { "First", "Second", "Third" });
                combo.SelectedIndex = 0;
                Raise(combo, "OnDropDown");
                combo.Items.Clear();
                combo.Items.AddRange(new object[] { "Third", "Second", "First" });
                combo.SelectedIndex = 1;
                Raise(combo, "OnDropDownClosed");
                check((string)combo.SelectedItem == "First" && combo.SelectedIndex == 2, "cancel after refreshing item order restores the original item at its new index");

                Raise(combo, "OnDropDown");
                combo.Items.Remove("First");
                combo.SelectedIndex = 0;
                Raise(combo, "OnDropDownClosed");
                check((string)combo.SelectedItem == "Third" && combo.SelectedIndex == 0, "cancel after removing the original item preserves the refreshed valid selection");
            }
        }
        private static void EditableSelection(Action<bool, string> check)
        {
            using (var combo = new StyledComboBox(true))
            {
                combo.Items.AddRange(new object[] { "Seed model", "Chat model" });
                combo.Text = "ep-custom-text-model";
                IntPtr handle = combo.Handle;
                check(combo.Text == "ep-custom-text-model" && combo.SelectedIndex == -1 && HeightMatches(combo), "editable model ID survives native handle creation with consistent height");
                Raise(combo, "OnDropDown"); combo.SelectedIndex = 1; Raise(combo, "OnDropDownClosed");
                check(combo.Text == "ep-custom-text-model" && combo.SelectedIndex == -1, "cancelling preset selection restores a custom model ID");
                Raise(combo, "OnDropDown"); combo.SelectedIndex = 0; Raise(combo, "OnSelectionChangeCommitted"); Raise(combo, "OnDropDownClosed");
                check(combo.Text == "Seed model" && combo.SelectedIndex == 0, "confirming a model preset replaces the custom ID");
                combo.Text = "ep-second-custom-model";
                typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(combo, null);
                check(combo.Text == "ep-second-custom-model" && HeightMatches(combo), "custom model text and height survive handle recreation");
            }
        }
        private static void Raise(ComboBox combo, string method)
        {
            typeof(StyledComboBox).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(combo, new object[] { EventArgs.Empty });
        }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
    }
}

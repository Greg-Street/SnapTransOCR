using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class SettingsSaveTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);

        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            var initial = new AppSettings();
            AppSettings live = initial.Copy(), baseline = initial.Copy();
            var saves = new List<AppSettings>();
            bool reject = false;
            try
            {
                using (var form = new SettingsForm(initial, delegate(AppSettings next)
                {
                    next.PreserveLiveLanguages(baseline, live);
                    next.PreserveLiveWindowSize(live);
                    if (next.HistoryEnabled == baseline.HistoryEnabled) next.HistoryEnabled = live.HistoryEnabled;
                    if (reject) throw new IOException("Synthetic settings write failure");
                    live = next; baseline = next.Copy(); saves.Add(next);
                }) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.Show(); form.SelectPage("识别设置");
                    var fields = Field<Dictionary<string, TextBox>>(form, "fields");
                    var body = Field<Panel>(form, "body");
                    fields["TencentId"].Text = "synthetic first";
                    body.AutoScrollPosition = new Point(0, 80);
                    Point scroll = body.AutoScrollPosition;
                    Save(form);
                    check(form.Visible && !form.IsDisposed && form.DialogResult == DialogResult.None, "saving applies settings without closing or completing the modeless settings window");
                    check(Field<Label>(form, "pageTitle").Text == "识别设置" && body.AutoScrollPosition == scroll, "saving keeps the current settings page and scroll position");
                    check(saves.Count == 1 && live.TencentId == "synthetic first" && Field<Label>(form, "saveStatus").Text == "设置已保存", "successful settings save invokes its callback once and displays inline feedback");
                    check(!ReferenceEquals(form.Value, live) && !ReferenceEquals(form.Value.Hotkeys, live.Hotkeys) && !ReferenceEquals(form.Value.OcrServiceOrder, live.OcrServiceOrder), "saved form state and the applied configuration own independent mutable objects");
                    fields["TencentId"].Text = "synthetic second";
                    form.RefreshHistory(false);
                    check(live.TencentId == "synthetic first" && live.HistoryEnabled && Field<Label>(form, "saveStatus").Text == "", "subsequent draft edits do not mutate the applied configuration and clear the success feedback");
                    Save(form);
                    check(saves.Count == 2 && live.TencentId == "synthetic second" && saves[0].TencentId == "synthetic first" && !live.HistoryEnabled, "the same settings window can apply another independent save");

                    live.SourceLanguage = "en"; live.TargetLanguage = "fr";
                    Save(form);
                    check(live.SourceLanguage == "en" && live.TargetLanguage == "fr" && form.Value.SourceLanguage == "en" && form.Value.TargetLanguage == "fr", "saving unrelated fields preserves newer translation-window languages after previous saves");
                    check(Field<ComboBox>(form, "source").SelectedIndex == 2 && Field<ComboBox>(form, "target").SelectedIndex == 5, "settings language choices reflect the values actually applied by the save callback");
                    live.TargetLanguage = "de";
                    Save(form);
                    check(live.TargetLanguage == "de" && Field<ComboBox>(form, "target").SelectedIndex == 6, "repeated saves use the last successful baseline when preserving a later language change");
                    Field<ComboBox>(form, "target").SelectedIndex = 3;
                    Save(form);
                    check(live.TargetLanguage == "ja", "an explicit settings language edit still takes precedence over a live value");

                    int committed = saves.Count;
                    fields["TencentId"].Text = "synthetic unsaved";
                    reject = true;
                    using (var dismiss = new Timer { Interval = 10 })
                    {
                        // Only dismiss this test form's owned error dialog on this UI thread.
                        dismiss.Tick += delegate
                        {
                            EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr window, IntPtr parameter)
                            {
                                var name = new StringBuilder(32);
                                GetClassName(window, name, name.Capacity);
                                if (GetWindow(window, 4) == form.Handle && name.ToString() == "#32770") PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                                return true;
                            }, IntPtr.Zero);
                        };
                        dismiss.Start(); Save(form); dismiss.Stop();
                    }
                    check(form.Visible && !form.IsDisposed && fields["TencentId"].Text == "synthetic unsaved" && saves.Count == committed && live.TencentId == "synthetic second", "a failed save retains edited input and the last successfully applied configuration");
                    check(Field<Label>(form, "saveStatus").Text == "保存未完成", "a failed save replaces the previous success feedback");
                    reject = false; Save(form);
                    check(saves.Count == committed + 1 && live.TencentId == "synthetic unsaved" && form.Visible, "a failed save can be retried in the same open settings window");
                    form.Close();
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static void Save(SettingsForm form) { typeof(SettingsForm).GetMethod("Save", Private).Invoke(form, null); }
    }
}

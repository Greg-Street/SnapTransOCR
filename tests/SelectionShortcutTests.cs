using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class SelectionShortcutTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            var legacy = Json.Read<AppSettings>("{\"SelectionButton\":true,\"SourceLanguage\":\"en\",\"TargetLanguage\":\"zh\",\"Hotkeys\":[\"Alt+1\",\"\",\"\",\"Ctrl+Shift+A\"]}");
            check(legacy.SourceLanguage == "en" && legacy.TargetLanguage == "zh" && legacy.Hotkeys[3] == "Ctrl+Shift+A" && !Json.Write(legacy).Contains("SelectionButton"), "legacy selection-button settings are ignored while explicit translation hotkeys and languages survive migration");
            check(typeof(DesktopApp).Assembly.GetType("SnapTransOCR.SelectionPopup") == null && typeof(DesktopApp).Assembly.GetType("SnapTransOCR.SelectionHook") == null, "the automatic selection popup and mouse-selection hook are absent from the application");
            check(typeof(DesktopApp).GetFields(Private).All(field => field.FieldType != typeof(ScrollWheelHook)) && typeof(DesktopApp).GetMethod("SetHook", Private) == null, "the tray application has no resident mouse hook or automatic hook activation path");
            check(typeof(ScrollForm).GetField("hook", Private).FieldType == typeof(ScrollWheelHook) && typeof(ScrollWheelHook).GetEvent("MouseAction") == null, "long screenshots retain a dedicated wheel-only hook without drag-selection detection");
            using (var form = new SettingsForm(new AppSettings(), delegate { }))
            {
                var pages = (Dictionary<string, FlowLayoutPanel>)typeof(SettingsForm).GetField("pages", Private).GetValue(form);
                var toggles = (Dictionary<string, ToggleSwitch>)typeof(SettingsForm).GetField("toggles", Private).GetValue(form);
                check(pages.Count == 7 && !pages.ContainsKey("划词助手") && !toggles.ContainsKey("SelectionButton"), "settings has seven pages and no automatic selection-assistant switch");
                check(Descendants(pages["热键设置"]).OfType<Label>().Any(label => label.Text.Contains("未选中文字时不操作") && label.Text.Contains("恢复剪贴板")), "manual shortcut instructions retain the empty-selection and clipboard-restoration behavior");
            }
            using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false }, true, persistSettings: delegate { }))
                check(typeof(DesktopApp).GetField("popup", Private) == null && typeof(DesktopApp).GetField("hook", Private) == null, "tray startup creates no selection-popup window or mouse-selection listener");
        }
        internal static void RunEmptySelection(Action<bool, string> check)
        {
            Exception failure = null;
            using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false }, true, persistSettings: delegate { }))
            using (var fixture = new Form { Text = "SnapTransOCR manual selection verification", ClientSize = new Size(450, 100) })
            {
                var input = new TextBox { Text = "No text is selected", Dock = DockStyle.Top };
                fixture.Controls.Add(input);
                fixture.Shown += async delegate
                {
                    try
                    {
                        Native.ShowWindow(fixture.Handle, 9); Native.SetForegroundWindow(fixture.Handle); input.Focus(); input.Select(0, 0);
                        await Task.Delay(180);
                        check(Native.GetForegroundWindow() == fixture.Handle, "manual shortcut fixture owns foreground focus");
                        uint clipboard = Native.GetClipboardSequenceNumber();
                        var main = (MainForm)typeof(DesktopApp).GetField("main", Private).GetValue(app);
                        await (Task)typeof(DesktopApp).GetMethod("SelectedTranslation", Private).Invoke(app, null);
                        check(!main.Visible && typeof(DesktopApp).GetField("job", Private).GetValue(app) == null && !(bool)typeof(DesktopApp).GetField("reading", Private).GetValue(app), "the explicit translation shortcut with no selected text leaves the translator hidden and starts no request");
                        check(Native.GetClipboardSequenceNumber() == clipboard, "an empty explicit selection does not modify clipboard contents");
                    }
                    catch (Exception error) { failure = error; }
                    finally { fixture.Close(); }
                };
                Application.Run(fixture);
                if (failure != null) throw failure;
            }
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
        }
    }
}

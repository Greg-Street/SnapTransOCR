using System;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class TraySettingsTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" } }, true, persistSettings: delegate { }))
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var tray = (NotifyIcon)typeof(DesktopApp).GetField("tray", flags).GetValue(app);
                    var click = typeof(NotifyIcon).GetMethod("OnDoubleClick", flags);
                    click.Invoke(tray, new object[] { EventArgs.Empty });
                    var settings = (SettingsForm)typeof(DesktopApp).GetField("settingsWindow", flags).GetValue(app);
                    var main = (MainForm)typeof(DesktopApp).GetField("main", flags).GetValue(app);
                    check(settings != null && settings.Visible && !main.Visible, "double-clicking the tray opens settings without opening translation");
                    settings.SelectPage("识别设置");
                    click.Invoke(tray, new object[] { EventArgs.Empty });
                    check(ReferenceEquals(settings, typeof(DesktopApp).GetField("settingsWindow", flags).GetValue(app)), "a second tray double-click reuses the existing settings draft");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
    }
}

using System;
using System.IO;

namespace SnapTransOCR
{
    internal static class ClipboardShortcutSettingsTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-history-shortcut-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.json");
            try
            {
                var settings = new AppSettings();
                var legacy = Json.Object(Json.Write(settings)); legacy.Remove("HistoryShortcutVersion");
                settings.Hotkeys[7] = ""; legacy["Hotkeys"] = settings.Hotkeys;
                File.WriteAllText(path, Json.Write(legacy));
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Alt+V", "upgrade enables the requested history shortcut when previously unassigned");
                settings.Hotkeys[7] = "Ctrl+Alt+H"; legacy["Hotkeys"] = settings.Hotkeys;
                File.WriteAllText(path, Json.Write(legacy));
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Alt+H", "upgrade preserves a customized history shortcut");
                settings.Hotkeys[7] = ""; settings.Hotkeys[6] = "Alt+Ctrl+V"; legacy["Hotkeys"] = settings.Hotkeys;
                File.WriteAllText(path, Json.Write(legacy));
                check(SettingsStore.Load(path).Hotkeys[7] == "", "upgrade does not duplicate an equivalent shortcut assigned elsewhere");
                settings.Hotkeys[6] = "";
                SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "", "explicitly clearing the history shortcut stays cleared after restart");
                check(SettingsStore.Load(path).HistoryEnabled, "history and clipboard recording share the saved recording switch");
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
        }
    }
}

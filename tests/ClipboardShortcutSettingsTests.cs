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
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Shift+S", "upgrade enables the requested history shortcut when previously unassigned");
                settings.Hotkeys[7] = "Ctrl+Alt+H"; legacy["Hotkeys"] = settings.Hotkeys;
                File.WriteAllText(path, Json.Write(legacy));
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Alt+H", "upgrade preserves a customized history shortcut");
                settings.Hotkeys[7] = ""; settings.Hotkeys[6] = "Shift+Ctrl+S"; legacy["Hotkeys"] = settings.Hotkeys;
                File.WriteAllText(path, Json.Write(legacy));
                check(SettingsStore.Load(path).Hotkeys[7] == "", "upgrade does not duplicate an equivalent shortcut assigned elsewhere");
                settings.Hotkeys[6] = "";
                SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "", "explicitly clearing the history shortcut stays cleared after restart");
                check(SettingsStore.Load(path).HistoryEnabled, "history and clipboard recording share the saved recording switch");
                settings.HistoryShortcutVersion = 1; settings.Hotkeys[7] = "Ctrl+Alt+V";
                SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Shift+S" && SettingsStore.Load(path).HistoryShortcutVersion == 2,
                    "the previous managed Ctrl+Alt+V default upgrades to Ctrl+Shift+S");
                settings.Hotkeys[6] = "Ctrl+Shift+S"; SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Alt+V", "a conflicting new default preserves the previous working shortcut");
                settings.Hotkeys[6] = ""; settings.Hotkeys[7] = "Ctrl+Alt+H"; SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Alt+H", "the standalone-window upgrade preserves custom history shortcuts");
                settings.Hotkeys[7] = ""; SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "", "the standalone-window upgrade preserves an intentionally disabled shortcut");
                settings.HistoryShortcutVersion = 2; settings.Hotkeys[7] = "Ctrl+Alt+V"; SettingsStore.Save(settings, path);
                check(SettingsStore.Load(path).Hotkeys[7] == "Ctrl+Alt+V", "a later deliberate Ctrl+Alt+V choice is not repeatedly migrated");
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
        }
    }
}

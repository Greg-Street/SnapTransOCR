using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class HistoryRetentionSettingsTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-retention-settings-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            try
            {
                var initial = new AppSettings();
                check(initial.HistoryRetentionDays == 7, "retention defaults to seven days for existing users");
                initial.HistoryRetentionDays = 23;
                SettingsStore.Save(initial, path);
                check(SettingsStore.Load(path).HistoryRetentionDays == 23, "custom typed retention persists without rounding to a preset");
                initial.HistoryRetentionDays = 0; SettingsStore.Save(initial, path);
                check(SettingsStore.Load(path).HistoryRetentionDays == 7, "invalid persisted retention falls back to seven days");
                initial.HistoryRetentionDays = 7;
                AppSettings saved = null;
                using (var form = new SettingsForm(initial, next => saved = next.Copy(), new HistoryStore(Path.Combine(directory, "history"))))
                {
                    var input = (NumericUpDown)typeof(SettingsForm).GetField("historyRetention", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    check(input.Minimum == 1 && input.Maximum == 3650 && input.Value == 7, "retention control permits direct input from one to 3650 days");
                    check(input.Parent is RetentionInput && input.BorderStyle == BorderStyle.None, "retention input uses the shared rounded style without a native rectangle border");
                    input.Value = 23;
                    GlyphButton more = null, less = null;
                    foreach (Control control in input.Parent.Controls)
                    {
                        if (control is GlyphButton && control.AccessibleName == "增加一天") more = (GlyphButton)control;
                        if (control is GlyphButton && control.AccessibleName == "减少一天") less = (GlyphButton)control;
                    }
                    more.PerformClick();
                    check(input.Value == 24, "styled plus icon increases the typed day count");
                    less.PerformClick();
                    check(input.Value == 23, "styled minus icon decreases the typed day count");
                    input.Value = 1; less.PerformClick(); bool minimumKept = input.Value == 1;
                    input.Value = 3650; more.PerformClick();
                    check(minimumKept && input.Value == 3650, "styled icons preserve both retention limits");
                    input.Value = 23;
                    check(saved == null && initial.HistoryRetentionDays == 7, "editing retention alone does not save or prematurely prune history");
                    typeof(SettingsForm).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                    check(saved != null && saved.HistoryRetentionDays == 23 && form.Value.HistoryRetentionDays == 23, "saving settings applies exactly the typed retention value");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}

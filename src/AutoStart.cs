using System;
using Microsoft.Win32;

namespace SnapTransOCR
{
    internal static class AutoStart
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SnapTransOCR";
        private const string LegacyValueName = "QingSnap";
        internal static string Command(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable) || executable.Contains("\"")) throw new ArgumentException("程序路径不正确。");
            string command = "\"" + executable + "\" --tray";
            if (command.Length > 260) throw new InvalidOperationException("程序路径过长，请将SnapTransOCR移至较短路径后启用自启动。");
            return command;
        }
        public static string Read() { using (var key = Registry.CurrentUser.OpenSubKey(RunPath)) return key == null ? null : key.GetValue(ValueName) as string; }
        public static bool Enabled(string executable) { return String.Equals(Read(), Command(executable), StringComparison.OrdinalIgnoreCase); }
        public static void Restore(string value) { using (var key = Registry.CurrentUser.CreateSubKey(RunPath)) Write(key, value); }
        public static void Set(bool enabled, string executable) { Restore(enabled ? Command(executable) : null); }
        public static void Migrate(string executable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath))
                if (key == null || key.GetValue(LegacyValueName) == null) return;
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath, true))
            {
                if (key == null) return;
                string legacy = key.GetValue(LegacyValueName) as string;
                if (legacy == null) return;
                if (key.GetValue(ValueName) == null) Write(key, Command(executable));
                key.DeleteValue(LegacyValueName, false);
            }
        }
        internal static void Write(RegistryKey key, string command)
        {
            if (command == null) key.DeleteValue(ValueName, false);
            else key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }
}

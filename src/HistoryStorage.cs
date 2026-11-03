using System;
using System.IO;
using System.Security;

namespace SnapTransOCR
{
    internal static class HistoryStorage
    {
        internal static string Resolve(string installationDirectory, string fallbackDirectory)
        {
            string preferred = Path.Combine(Path.GetFullPath(installationDirectory), "data", "history");
            try { Probe(preferred); return preferred; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            string fallback = Path.GetFullPath(fallbackDirectory);
            Probe(fallback);
            return fallback;
        }
        private static void Probe(string path)
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, "write-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }
}

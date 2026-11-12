using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class HistorySnapshotTests
    {
        internal static async Task Run(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-snapshot-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "history.json");
            DateTime now = new DateTime(2026, 11, 5, 4, 0, 0, DateTimeKind.Utc).AddTicks(1234);
            var store = new HistoryStore(directory, () => now);
            try
            {
                await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "first" });
                var onDisk = Json.Read<HistoryEntry[]>(File.ReadAllText(path));
                check((await store.LoadAsync())[0].CreatedUtc == onDisk[0].CreatedUtc, "a warm history snapshot has the same timestamp precision as the committed JSON and a cold reload");
                var first = await store.LoadAsync(); first[0].Source = "caller mutation";
                check((await store.LoadAsync())[0].Source == "first", "history snapshot gives callers independent records instead of exposing cached mutable objects");
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                long length = new FileInfo(path).Length;
                File.WriteAllText(path, File.ReadAllText(path).Replace("first", "other"), new UTF8Encoding(false)); File.SetLastWriteTimeUtc(path, stamp);
                check(new FileInfo(path).Length == length && (await store.LoadAsync())[0].Source == "other", "history notices external edits even when file length and timestamp are unchanged");
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool rejected = false; try { await store.LoadAsync(); } catch (IOException) { rejected = true; }
                    check(rejected, "a cached history does not bypass an exclusive file lock or act on an unreadable index");
                }
                byte[] committed = File.ReadAllBytes(path);
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var image = new Bitmap(40, 30))
                {
                    bool failed = false; try { await store.AddAsync(new HistoryEntry { Kind = "capture" }, image); } catch (IOException) { failed = true; }
                    check(failed && Directory.GetFiles(directory, "*.png").Length == 0, "failed history replacement rolls back its image and does not publish a new snapshot");
                }
                check((await store.LoadAsync()).Length == 1 && (await store.LoadAsync())[0].Source == "other", "history remains readable with its committed content after a failed write");
                string orphan = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
                using (var image = new Bitmap(4, 4)) image.Save(orphan);
                File.WriteAllText(path, "broken index");
                bool malformed = false; try { await store.LoadAsync(); } catch (InvalidOperationException) { malformed = true; }
                check(malformed && File.Exists(orphan), "corrupted external history is reported before cleanup and cannot be hidden by the snapshot cache");
                File.WriteAllBytes(path, committed); now = now.AddDays(8);
                check((await store.LoadAsync()).Length == 0 && !File.Exists(orphan), "retention still expires cached records and cleans their files as the clock advances");
                await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = new string('x', 4 * 1024 * 1024 + 1) });
                check((await store.LoadAsync())[0].Source.Length == 4 * 1024 * 1024 + 1 && typeof(HistoryStore).GetField("cachedEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store) == null,
                    "indexes above the four MiB cache bound remain usable without retaining a permanent snapshot");
                await store.ClearAsync();
                check((await store.LoadAsync()).Length == 0, "clearing history also replaces the cached snapshot with the committed empty index");
            }
            finally
            {
                await store.FlushAsync();
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        }
    }
}

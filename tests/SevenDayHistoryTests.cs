using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class SevenDayHistoryTests
    {
        internal static async Task Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "SnapTransOCR-week-history-" + Guid.NewGuid().ToString("N"));
            DateTime now = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
            var store = new HistoryStore(Path.Combine(root, "records"), () => now);
            try
            {
                int notifications = 0; bool callbackQueriedStorage = false;
                store.Changed += delegate { notifications++; callbackQueriedStorage = store.DirectoryPath.EndsWith("records", StringComparison.Ordinal); };
                using (var image = new Bitmap(8, 6))
                {
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Teal);
                    await store.AddAsync(new HistoryEntry { Kind = "clipboard_image", ContentHash = "image-1" }, image);
                    HistoryEntry first = (await store.LoadAsync()).Single();
                    now = now.AddHours(1);
                    await store.AddAsync(new HistoryEntry { Kind = "clipboard_image", ContentHash = "image-1" }, image);
                    HistoryEntry recopied = (await store.LoadAsync()).Single();
                    check(recopied.Id == first.Id && recopied.CreatedUtc == now && Directory.GetFiles(store.DirectoryPath, "*.png").Length == 1, "consecutive identical clipboard images refresh their timestamp without another PNG");
                    now = now.AddDays(7).AddTicks(-1);
                    await store.PruneAsync();
                    check((await store.LoadAsync()).Length == 1, "a recopied clipboard item survives until its last copy is seven days old");
                    now = now.AddTicks(1);
                    await store.PruneAsync();
                    check((await store.LoadAsync()).Length == 0 && Directory.GetFiles(store.DirectoryPath, "*.png").Length == 0, "the exact seven-day boundary removes the image and index entry");
                }
                check(notifications == 3, "add, refreshed duplicate and expiration notify while unchanged loads and pruning do not");
                check(callbackQueriedStorage, "history changed callback can query storage after the serialization gate is released");
                await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "external copy", ContentHash = "text-1" });
                HistoryEntry text = (await store.LoadAsync()).Single();
                now = now.AddMinutes(1);
                await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "external copy", ContentHash = "text-1" });
                check((await store.LoadAsync()).Single().Id == text.Id, "consecutive identical clipboard text is deduplicated");
                await store.AddAsync(new HistoryEntry { Kind = "translation", Source = "app request", Translation = "软件译文" });
                await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "external copy", ContentHash = "text-1" });
                check((await store.LoadAsync()).Length == 3, "a distinct intervening record does not merge separate clipboard actions");
                await store.ClearAsync();
                now = now.AddDays(1);
                for (int i = 0; i < 65; i++) await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "item " + i, ContentHash = i.ToString() });
                check((await store.LoadAsync()).Length == 65, "a busy day's clipboard history is not cut off at the previous fifty-record limit");
                now = now.AddDays(7);
                check((await store.LoadAsync()).Length == 0, "opening history also expires ordinary text without waiting for a new copy");
                await ExpiryCommit(root, now, check);
                await Locations(root, check);
                await Migration(root, now, check);
                await ClearFailedMigration(root, now, check);
            }
            finally
            {
                string full = Path.GetFullPath(root), temp = Path.GetFullPath(Path.GetTempPath());
                if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe temporary history cleanup.");
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
        private static async Task ExpiryCommit(string root, DateTime now, Action<bool, string> check)
        {
            string directory = Path.Combine(root, "expiry-commit");
            var store = new HistoryStore(directory, () => now);
            using (var image = new Bitmap(4, 4)) await store.AddAsync(new HistoryEntry { Kind = "clipboard_image", ContentHash = "locked-index" }, image);
            HistoryEntry entry = (await store.LoadAsync()).Single();
            string index = Path.Combine(directory, "history.json"), path = Path.Combine(directory, entry.ImageFile);
            string before = File.ReadAllText(index); now = now.AddDays(7);
            bool rejected = false;
            using (var locked = File.Open(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await store.PruneAsync(); } catch (IOException) { rejected = true; }
                check(rejected && File.Exists(path) && File.ReadAllText(index) == before, "failed expiration index commits preserve both original metadata and its screenshot");
            }
            await store.PruneAsync();
            check((await store.LoadAsync()).Length == 0 && !File.Exists(path), "expiration cleanup succeeds after the index write lock is released");
        }
        private static Task Locations(string root, Action<bool, string> check)
        {
            string installation = Path.Combine(root, "portable"), fallback = Path.Combine(root, "fallback");
            string selected = HistoryStorage.Resolve(installation, fallback);
            check(selected == Path.Combine(installation, "data", "history") && !Directory.GetFiles(selected).Any(), "a writable installation stores history in data/history without leaving probe files");
            string blocked = Path.Combine(root, "protected"); Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "data"), "a file blocks the install data directory", Encoding.UTF8);
            check(HistoryStorage.Resolve(blocked, fallback) == fallback, "an unwritable or blocked installation falls back to local user storage");
            return Task.CompletedTask;
        }
        private static async Task Migration(string root, DateTime now, Action<bool, string> check)
        {
            string legacy = Path.Combine(root, "legacy"), target = Path.Combine(root, "migrated");
            var previous = new HistoryStore(legacy, () => now.AddDays(-1));
            using (var image = new Bitmap(5, 4))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Tomato);
                await previous.AddAsync(new HistoryEntry { Kind = "capture", Source = "retained legacy" }, image);
            }
            HistoryEntry old = (await previous.LoadAsync()).Single();
            byte[] indexBefore = File.ReadAllBytes(Path.Combine(legacy, "history.json"));
            byte[] imageBefore = File.ReadAllBytes(Path.Combine(legacy, old.ImageFile));
            var migrated = new HistoryStore(target, () => now);
            migrated.Migrate(legacy);
            check((await migrated.LoadAsync()).Single().Id == old.Id && File.ReadAllBytes(Path.Combine(legacy, "history.json")).SequenceEqual(indexBefore) && File.ReadAllBytes(Path.Combine(legacy, old.ImageFile)).SequenceEqual(imageBefore), "portable migration preserves legacy metadata and images byte for byte");
            using (var loaded = await migrated.LoadImageAsync(old.Id)) check(loaded.Size == new Size(5, 4) && loaded.GetPixel(0, 0).ToArgb() == Color.Tomato.ToArgb(), "migrated screenshots remain independently readable");
            await migrated.ClearAsync(); migrated.Migrate(legacy);
            check((await migrated.LoadAsync()).Length == 0 && File.Exists(Path.Combine(target, "history.json")), "clearing migrated history keeps an empty index and prevents re-import on next launch");
            string failed = Path.Combine(root, "failed-migration"); Directory.CreateDirectory(Path.Combine(failed, "history.json"));
            var failedStore = new HistoryStore(failed, () => now);
            bool rejected = false;
            try { failedStore.Migrate(legacy); } catch (IOException) { rejected = true; }
            check(rejected && Directory.GetFiles(failed).Length == 0 && File.Exists(Path.Combine(legacy, old.ImageFile)) && File.ReadAllBytes(Path.Combine(legacy, "history.json")).SequenceEqual(indexBefore), "failed migration commits leave no staged or copied image and never remove legacy data");
            var expired = new HistoryStore(Path.Combine(root, "expired-migration"), () => now.AddDays(8));
            expired.Migrate(legacy);
            check((await expired.LoadAsync()).Length == 0 && Directory.GetFiles(expired.DirectoryPath, "*.png").Length == 0, "migration does not copy expired legacy screenshots");
        }
        private static async Task ClearFailedMigration(string root, DateTime now, Action<bool, string> check)
        {
            string legacy = Path.Combine(root, "corrupt-legacy"), target = Path.Combine(root, "cleared-migration");
            Directory.CreateDirectory(legacy);
            string index = Path.Combine(legacy, "history.json"), malformed = "{ invalid legacy history";
            File.WriteAllText(index, malformed, Encoding.UTF8);
            var store = new HistoryStore(target, () => now, legacy);
            bool rejected = false;
            try { await store.LoadAsync(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && !File.Exists(Path.Combine(target, "history.json")), "a failed legacy parse does not commit a portable history index");
            await store.ClearAsync();
            check((await store.LoadAsync()).Length == 0 && File.ReadAllText(index, Encoding.UTF8) == malformed, "explicit clear recovers failed migration without reading or modifying the malformed legacy index");
            var reopened = new HistoryStore(target, () => now, legacy);
            check((await reopened.LoadAsync()).Length == 0, "the cleared target suppresses failed legacy re-import after restarting");
        }
    }
}

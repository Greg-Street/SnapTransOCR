using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class RetentionHistoryTests
    {
        internal static async Task Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "SnapTransOCR-custom-retention-" + Guid.NewGuid().ToString("N"));
            DateTime now = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
            try
            {
                foreach (int days in new[] { 1, 2, 3, 7, 9, 14, 30, 45, 3650 })
                {
                    DateTime clock = now;
                    var store = new HistoryStore(Path.Combine(root, "days-" + days), () => clock, retentionDays: () => days);
                    check(store.ActiveRetentionDays == days, "history accepts the typed retention duration " + days + " days");
                    using (var image = new Bitmap(4, 3)) await store.AddAsync(new HistoryEntry { Kind = "clipboard_image", ContentHash = "duration" }, image);
                    clock = now.AddDays(days).AddTicks(-1);
                    check((await store.LoadAsync()).Length == 1, "history preserves data just before its " + days + "-day cutoff");
                    clock = clock.AddTicks(1); await store.PruneAsync();
                    check((await store.LoadAsync()).Length == 0 && Directory.GetFiles(store.DirectoryPath, "*.png").Length == 0, "history removes metadata and screenshots at the exact " + days + "-day cutoff");
                }
                var defaultStore = new HistoryStore(Path.Combine(root, "default"), () => now);
                check(defaultStore.ActiveRetentionDays == 7, "history defaults to seven days without a configured retention delegate");
                foreach (int invalid in new[] { -1, 0, 3651, Int32.MinValue, Int32.MaxValue })
                {
                    var store = new HistoryStore(Path.Combine(root, "invalid-" + invalid), () => now, retentionDays: () => invalid);
                    check(store.ActiveRetentionDays == 7, "invalid history retention falls back to seven days for " + invalid);
                }
                await ChangingDuration(root, now, check);
                await MigrationDuration(root, now, check);
            }
            finally
            {
                string full = Path.GetFullPath(root), temp = Path.GetFullPath(Path.GetTempPath());
                if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe temporary retention cleanup.");
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
        private static async Task ChangingDuration(string root, DateTime now, Action<bool, string> check)
        {
            int days = 7;
            DateTime clock = now;
            var store = new HistoryStore(Path.Combine(root, "changing"), () => clock, retentionDays: () => days);
            await store.AddAsync(new HistoryEntry { Kind = "clipboard_text", Source = "Keep the recent copy", ContentHash = "text" });
            clock = now.AddDays(6); days = 9; await store.PruneAsync();
            clock = now.AddDays(8);
            check((await store.LoadAsync()).Single().Source == "Keep the recent copy", "increasing retention applies to already saved records before the old cutoff");
            days = 2; await store.PruneAsync();
            check((await store.LoadAsync()).Length == 0, "shortening custom retention removes records outside the new duration");
            days = 45; await store.PruneAsync();
            check((await store.LoadAsync()).Length == 0, "increasing retention does not recreate records already removed");
            await store.AddAsync(new HistoryEntry { Kind = "translation", Source = "fallback duration" });
            days = 0; clock = clock.AddDays(7);
            check((await store.LoadAsync()).Length == 0, "a corrupted live duration prunes using the seven-day fallback rather than disabling cleanup");
        }
        private static async Task MigrationDuration(string root, DateTime now, Action<bool, string> check)
        {
            string legacy = Path.Combine(root, "legacy");
            var previous = new HistoryStore(legacy, () => now.AddDays(-8));
            await previous.AddAsync(new HistoryEntry { Kind = "translation", Source = "Eight days old" });
            var longer = new HistoryStore(Path.Combine(root, "migration-nine"), () => now, legacy, () => 9);
            check((await longer.LoadAsync()).Single().Source == "Eight days old", "legacy migration uses the selected nine-day duration rather than a fixed week");
            var shorter = new HistoryStore(Path.Combine(root, "migration-two"), () => now, legacy, () => 2);
            check((await shorter.LoadAsync()).Length == 0, "legacy migration omits records older than the selected two-day duration");
            int reads = 0;
            var snapshot = new HistoryStore(Path.Combine(root, "migration-snapshot"), () => now, legacy, () => ++reads == 1 ? 9 : 2);
            HistoryEntry[] entries = await snapshot.LoadAsync();
            check(reads == 1 && entries.Length == 1, "first migration and read share one retention snapshot within their serialized operation");
        }
    }
}

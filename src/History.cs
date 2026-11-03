using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal sealed class HistoryEntry
    {
        public string Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Kind { get; set; }
        public string OcrProvider { get; set; }
        public string TranslateProvider { get; set; }
        public string Source { get; set; }
        public string Translation { get; set; }
        public string ImageFile { get; set; }
        public string ContentHash { get; set; }
    }

    internal sealed class HistoryStore
    {
        internal const int RetentionDays = 7, MaxTextBytes = 16 * 1024 * 1024;
        private string directory, indexPath;
        private readonly string legacyDirectory;
        private readonly Func<DateTime> utcNow;
        private readonly Func<int> retentionDays;
        private DateTime? operationCutoff;
        private bool initialized, changed;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        internal event Action Changed;

        internal HistoryStore(string directory = null, Func<DateTime> utcNow = null, string legacyDirectory = null, Func<int> retentionDays = null)
        {
            this.legacyDirectory = legacyDirectory ?? (directory == null ? Path.Combine(SettingsStore.DirectoryPath, "history") : null);
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.retentionDays = retentionDays ?? (() => RetentionDays);
            if (directory != null) { this.directory = Path.GetFullPath(directory); indexPath = Path.Combine(this.directory, "history.json"); }
        }
        internal string DirectoryPath
        {
            get { if (directory != null) return directory; gate.Wait(); try { ResolveLocation(); return directory; } finally { gate.Release(); } }
        }
        internal int ActiveRetentionDays
        {
            get { int value = retentionDays(); return value >= 1 && value <= 3650 ? value : RetentionDays; }
        }

        internal Task<bool> AddAsync(HistoryEntry value, Bitmap image = null)
        {
            var entry = new HistoryEntry { Id = Guid.NewGuid().ToString("N"), CreatedUtc = utcNow().ToUniversalTime(),
                Kind = value.Kind, OcrProvider = value.OcrProvider, TranslateProvider = value.TranslateProvider,
                Source = value.Source ?? "", Translation = value.Translation ?? "", ContentHash = value.ContentHash };
            entry.ImageFile = image == null ? "" : entry.Id + ".png";
            return Execute(delegate
            {
                if (image == null && String.IsNullOrWhiteSpace(entry.Source) && String.IsNullOrWhiteSpace(entry.Translation)) return false;
                HistoryEntry[] previous = Read();
                if (previous.Length > 0 && IsClipboard(entry) && previous[0].Kind == entry.Kind &&
                    !String.IsNullOrEmpty(entry.ContentHash) && previous[0].ContentHash == entry.ContentHash)
                {
                    if (previous[0].CreatedUtc != entry.CreatedUtc) { previous[0].CreatedUtc = entry.CreatedUtc; Write(previous); changed = true; }
                    CleanupImages(previous);
                    return true;
                }
                int bytes = 2 + Encoding.UTF8.GetByteCount(Json.Write(entry));
                if (bytes > MaxTextBytes) return false;
                var entries = new List<HistoryEntry> { entry };
                foreach (HistoryEntry item in previous)
                {
                    int next = Encoding.UTF8.GetByteCount(Json.Write(item)) + 1;
                    // Preserve records within the selected retention period rather than silently evicting recent copies.
                    if (bytes + next > MaxTextBytes) return false;
                    entries.Add(item); bytes += next;
                }
                Directory.CreateDirectory(directory);
                string imagePath = ImagePath(entry), temporary = imagePath == null ? null : imagePath + ".tmp";
                try
                {
                    if (image != null) { image.Save(temporary, ImageFormat.Png); File.Move(temporary, imagePath); }
                    Write(entries.ToArray());
                    changed = true;
                }
                catch
                {
                    if (temporary != null) File.Delete(temporary);
                    if (imagePath != null) File.Delete(imagePath);
                    throw;
                }
                CleanupImages(entries.ToArray());
                return true;
            });
        }

        internal Task<HistoryEntry[]> LoadAsync()
        {
            return Execute(delegate { HistoryEntry[] entries = Read(); CleanupImages(entries); return entries; });
        }
        internal Task FlushAsync() { return Execute(delegate { return true; }); }
        internal Task PruneAsync() { return Execute(delegate { HistoryEntry[] entries = Read(); CleanupImages(entries); return true; }); }
        internal Task DeleteAsync(string id)
        {
            return Execute(delegate
            {
                HistoryEntry[] previous = Read();
                HistoryEntry[] entries = previous.Where(item => item.Id != id).ToArray();
                if (entries.Length != previous.Length) { Write(entries); changed = true; }
                CleanupImages(entries);
                return true;
            });
        }
        internal Task ClearAsync()
        {
            return Execute(delegate
            {
                ResolveLocation();
                // Keep an empty committed index so clearing portable history cannot re-import the legacy copy next launch.
                Write(new HistoryEntry[0]); initialized = true; changed = true;
                CleanupImages(new HistoryEntry[0]);
                return true;
            }, initialize: false);
        }
        internal Task<Bitmap> LoadImageAsync(string id)
        {
            return Execute(delegate
            {
                HistoryEntry entry = Array.Find(Read(), item => item.Id == id);
                string path = entry == null ? null : ImagePath(entry);
                if (path == null || !File.Exists(path)) return null;
                using (var stream = File.OpenRead(path))
                using (var loaded = Image.FromStream(stream)) return new Bitmap(loaded);
            });
        }
        internal Task<Bitmap> LoadThumbnailAsync(string id, Size maximum, CancellationToken cancellation)
        {
            return Execute(delegate
            {
                cancellation.ThrowIfCancellationRequested();
                HistoryEntry entry = Array.Find(Read(), item => item.Id == id);
                string path = entry == null ? null : ImagePath(entry);
                if (path == null || !File.Exists(path)) return null;
                using (var stream = File.OpenRead(path))
                using (var loaded = Image.FromStream(stream))
                {
                    double scale = Math.Min(1, Math.Min(maximum.Width / (double)loaded.Width, maximum.Height / (double)loaded.Height));
                    var thumbnail = new Bitmap(Math.Max(1, (int)Math.Round(loaded.Width * scale)), Math.Max(1, (int)Math.Round(loaded.Height * scale)), PixelFormat.Format32bppPArgb);
                    try
                    {
                        using (var graphics = Graphics.FromImage(thumbnail))
                        {
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.DrawImage(loaded, new Rectangle(Point.Empty, thumbnail.Size));
                        }
                        cancellation.ThrowIfCancellationRequested();
                        return thumbnail;
                    }
                    catch { thumbnail.Dispose(); throw; }
                }
            }, cancellation);
        }
        private async Task<T> Execute<T>(Func<T> operation, CancellationToken cancellation = default(CancellationToken), bool initialize = true)
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await Task.Run(delegate
                {
                    operationCutoff = utcNow().ToUniversalTime().AddDays(-ActiveRetentionDays);
                    if (initialize) Initialize();
                    return operation();
                }, cancellation).ConfigureAwait(false);
            }
            finally
            {
                bool notify = changed; changed = false; operationCutoff = null; gate.Release();
                var handler = Changed; if (notify && handler != null) handler();
            }
        }
        private static bool IsClipboard(HistoryEntry entry) { return entry.Kind == "clipboard_text" || entry.Kind == "clipboard_image"; }
        private void ResolveLocation()
        {
            if (directory != null) return;
            directory = HistoryStorage.Resolve(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(SettingsStore.DirectoryPath, "history"));
            indexPath = Path.Combine(directory, "history.json");
        }
        private void Initialize()
        {
            if (initialized) return;
            ResolveLocation();
            if (legacyDirectory != null) Migrate(legacyDirectory);
            initialized = true;
        }
        internal void Migrate(string legacyDirectory)
        {
            string legacy = Path.GetFullPath(legacyDirectory), legacyIndex = Path.Combine(legacy, "history.json");
            if (String.Equals(legacy, directory, StringComparison.OrdinalIgnoreCase) || File.Exists(indexPath) || !File.Exists(legacyIndex)) return;
            DateTime cutoff = operationCutoff ?? utcNow().ToUniversalTime().AddDays(-ActiveRetentionDays);
            HistoryEntry[] entries = ReadFile(legacyIndex).Where(item => item.CreatedUtc > cutoff).ToArray();
            Directory.CreateDirectory(directory);
            var copied = new List<string>();
            try
            {
                foreach (HistoryEntry entry in entries)
                {
                    string target = ImagePath(entry);
                    if (target == null || File.Exists(target)) continue;
                    string source = Path.Combine(legacy, entry.ImageFile);
                    if (!File.Exists(source)) continue;
                    string staged = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.Copy(source, staged); File.Move(staged, target); copied.Add(target); }
                    finally { if (File.Exists(staged)) File.Delete(staged); }
                }
                Write(entries); changed = true;
            }
            catch { foreach (string path in copied) File.Delete(path); throw; }
        }
        private HistoryEntry[] Read()
        {
            HistoryEntry[] previous = ReadFile(indexPath);
            DateTime cutoff = operationCutoff ?? utcNow().ToUniversalTime().AddDays(-ActiveRetentionDays);
            HistoryEntry[] entries = previous.Where(item => item.CreatedUtc > cutoff).ToArray();
            if (entries.Length != previous.Length) { Write(entries); changed = true; CleanupImages(entries); }
            return entries;
        }
        private static HistoryEntry[] ReadFile(string path)
        {
            if (!File.Exists(path)) return new HistoryEntry[0];
            if (new FileInfo(path).Length > MaxTextBytes) throw new InvalidOperationException("历史记录文件超过文字容量限制。请清空历史记录后重试。");
            string text = File.ReadAllText(path, Encoding.UTF8);
            try { return (Json.Read<HistoryEntry[]>(text) ?? new HistoryEntry[0]).Where(item => item != null).ToArray(); }
            catch (ArgumentException) { throw new InvalidOperationException("历史记录文件无法读取。可清空历史记录后重新使用。"); }
            catch (InvalidOperationException) { throw new InvalidOperationException("历史记录文件无法读取。可清空历史记录后重新使用。"); }
        }
        private void Write(HistoryEntry[] entries)
        {
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "history-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(entries));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(indexPath)) File.Replace(temporary, indexPath, null); else File.Move(temporary, indexPath);
            }
            finally { File.Delete(temporary); }
        }
        private string ImagePath(HistoryEntry entry)
        {
            Guid id;
            return Guid.TryParseExact(entry.Id, "N", out id) && entry.ImageFile == entry.Id + ".png" ? Path.Combine(directory, entry.ImageFile) : null;
        }
        private static bool ManagedImage(string path)
        {
            Guid id;
            return Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out id);
        }
        private void CleanupImages(HistoryEntry[] entries)
        {
            string[] files;
            try { files = Directory.GetFiles(directory, "*.png"); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            var retained = new HashSet<string>(entries.Where(entry => ImagePath(entry) != null).Select(entry => entry.ImageFile), StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                if (!ManagedImage(path) || retained.Contains(Path.GetFileName(path))) continue;
                // The index is already committed. A locked old image can be retried on the next add/load.
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

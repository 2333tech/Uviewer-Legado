using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services
{
    internal sealed class ThumbnailDiskCache
    {
        private const int MaxEntryBytes = 2 * 1024 * 1024;
        private const long MaxBytes = 512 * 1024 * 1024;
        private const int MaxEntries = 8192;
        private static readonly SemaphoreSlim Gate = new(1);
        private readonly string _folder;
        private readonly long _maxBytes;
        private readonly int _maxEntries;
        private bool _sizeKnown;
        private long _estimatedBytes;
        private int _estimatedCount;
        private int _writesSinceScan;

        public ThumbnailDiskCache(string? folder = null, long maxBytes = MaxBytes, int maxEntries = MaxEntries)
        {
            _maxBytes = maxBytes;
            _maxEntries = maxEntries;
            // Keep the location of existing archive thumbnails when extending it to other files.
            _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Uviewer", "Cache", "ArchiveThumbnails");
        }

        public static string? CreateKey(string archivePath, int pixelWidth)
        {
            try
            {
                var info = new FileInfo(archivePath);
                string identity = $"v1\n{info.FullName.ToUpperInvariant()}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{pixelWidth}";
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public async Task<byte[]?> ReadAsync(string key, CancellationToken token)
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string path = GetPath(key);
                // Retain PNG entries written by the earlier archive-only cache.
                if (!File.Exists(path)) path = Path.Combine(_folder, key + ".png");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > MaxEntryBytes) return null;
                var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return bytes;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { Gate.Release(); }
        }

        public async Task WriteAsync(string key, byte[] bytes, CancellationToken token)
        {
            if (bytes.Length == 0 || bytes.Length > MaxEntryBytes) return;
            await Gate.WaitAsync(token).ConfigureAwait(false);
            string? partial = null;
            try
            {
                Directory.CreateDirectory(_folder);
                partial = GetPath(key) + "." + Guid.NewGuid().ToString("N") + ".partial";
                await File.WriteAllBytesAsync(partial, bytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(partial, GetPath(key), overwrite: true);
                partial = null;
                Delete(Path.Combine(_folder, key + ".png"));
                // Overwrites overestimate usage, so these counters cannot defer necessary eviction.
                _estimatedBytes += bytes.Length;
                _estimatedCount++;
                _writesSinceScan++;
                if (!_sizeKnown || _estimatedBytes > _maxBytes || _estimatedCount > _maxEntries || _writesSinceScan >= 256)
                    Trim();
            }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally
            {
                if (partial != null) Delete(partial);
                Gate.Release();
            }
        }

        private string GetPath(string key) => Path.Combine(_folder, key + ".thumb");

        private void Trim()
        {
            var files = new DirectoryInfo(_folder).GetFiles()
                .Where(f => f.Extension is ".thumb" or ".png")
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            long bytes = files.Sum(f => f.Length);
            int count = files.Count;
            // Leave room for new entries to avoid scanning a full cache on every write.
            long targetBytes = bytes > _maxBytes ? _maxBytes * 9 / 10 : _maxBytes;
            int targetCount = count > _maxEntries ? _maxEntries * 9 / 10 : _maxEntries;
            foreach (var file in files)
            {
                if (bytes <= targetBytes && count <= targetCount) break;
                if (Delete(file.FullName))
                {
                    bytes -= file.Length;
                    count--;
                }
            }
            _estimatedBytes = bytes;
            _estimatedCount = count;
            _sizeKnown = true;
            _writesSinceScan = 0;
            foreach (var file in new DirectoryInfo(_folder).GetFiles("*.partial"))
            {
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) Delete(file.FullName);
            }
        }

        private static bool Delete(string path)
        {
            try { File.Delete(path); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}

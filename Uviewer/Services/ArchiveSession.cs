using SharpCompress.Archives;
using SevenZipExtractor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;

namespace Uviewer.Services
{
    public sealed class ArchiveSession : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private SevenZipImageCache? _sevenZipCache;

        public IArchive? CurrentArchive { get; set; }
        public ArchiveFile? Current7zArchive { get; set; }
        public string? CurrentPath { get; set; }
        public SemaphoreSlim Lock => _lock;

        public bool HasArchive => CurrentArchive != null || Current7zArchive != null;
        public bool IsSevenZipArchive => Current7zArchive != null;

        public static Task<bool> ContainsSupportedImageAsync(string archivePath)
        {
            return Task.Run(() =>
            {
                try
                {
                    string extension = Path.GetExtension(archivePath).ToLowerInvariant();
                    if (extension == ".7z")
                    {
                        string libraryPath = Path.Combine(AppContext.BaseDirectory, "Libs", "7z.dll");
                        using var archive = new ArchiveFile(archivePath, libraryPath);
                        return archive.Entries.Any(entry =>
                            !entry.IsFolder &&
                            FileExplorerService.GetSupportedFileKind(entry.FileName) == SupportedFileKind.Image);
                    }

                    using var sharpCompressArchive = ArchiveFactory.OpenArchive(archivePath);
                    return sharpCompressArchive.Entries.Any(entry =>
                        !entry.IsDirectory &&
                        FileExplorerService.GetSupportedFileKind(entry.Key) == SupportedFileKind.Image);
                }
                catch
                {
                    // An unreadable archive cannot be the automatic folder-open target.
                    return false;
                }
            });
        }

        // 7z's COM interfaces do not support STA-to-MTA marshalling. Create and
        // enumerate the archive in the same MTA as the background entry readers.
        public Task<IReadOnlyList<ImageEntry>> OpenLocalAsync(string archivePath)
            => Task.Run(() => OpenLocalCoreAsync(archivePath));

        private async Task<IReadOnlyList<ImageEntry>> OpenLocalCoreAsync(string archivePath)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                CloseOpenHandles();
                CurrentPath = archivePath;

                string extension = Path.GetExtension(archivePath).ToLowerInvariant();
                if (extension == ".7z")
                {
                    string libraryPath = Path.Combine(AppContext.BaseDirectory, "Libs", "7z.dll");
                    Current7zArchive = new ArchiveFile(archivePath, libraryPath);
                    return BuildSevenZipEntries(Current7zArchive);
                }

                CurrentArchive = ArchiveFactory.OpenArchive(archivePath);
                return BuildSharpCompressEntries(CurrentArchive);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<ImageEntry>> OpenStreamAsync(string displayPath, Stream stream)
        {
            await _lock.WaitAsync();
            try
            {
                CloseOpenHandles();
                CurrentPath = displayPath;
                CurrentArchive = ArchiveFactory.OpenArchive(stream);
                return BuildSharpCompressEntries(CurrentArchive);
            }
            finally
            {
                _lock.Release();
            }
        }

        public bool Close(TimeSpan timeout)
        {
            if (!HasArchive) return true;

            if (!_lock.Wait(timeout))
            {
                Debug.WriteLine("Archive lock timeout - cleanup deferred to avoid disposing while in use");
                return false;
            }

            try
            {
                CloseOpenHandles();
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> CloseAsync(TimeSpan timeout)
        {
            if (!HasArchive) return true;

            if (!await _lock.WaitAsync(timeout))
            {
                Debug.WriteLine("Archive lock timeout - aborting format switch to avoid unsafe dispose");
                return false;
            }

            try
            {
                CloseOpenHandles();
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public void CloseOpenHandles()
        {
            if (Current7zArchive != null && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Task.Run(CloseOpenHandles).GetAwaiter().GetResult();
                return;
            }

            _sevenZipCache?.Cancel();
            _sevenZipCache = null;
            CurrentArchive?.Dispose();
            CurrentArchive = null;

            Current7zArchive?.Dispose();
            Current7zArchive = null;

            DeleteWebDavTempArchiveIfNeeded(CurrentPath);
            CurrentPath = null;
        }

        public Task<byte[]?> ReadEntryBytesAsync(string entryKey, CancellationToken token)
            // SevenZip Extract and archive stream decompression can block even
            // through an async caller. Never run them on the UI dispatcher.
            => Task.Run(() => ReadEntryBytesCoreAsync(entryKey, token), token);

        private async Task<byte[]?> ReadEntryBytesCoreAsync(string entryKey, CancellationToken token)
        {
            var cache = _sevenZipCache;
            if (cache != null && cache.TryGetImage(entryKey, out var ready))
            {
                // Await just this image outside the archive lock. Preloads must
                // not queue repeated solid-block decodes ahead of the visible page.
                string? path = await ready.WaitAsync(token).ConfigureAwait(false);
                if (path != null)
                {
                    try { return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
                    catch (IOException) { } // Re-read from the archive if the cache disappeared.
                    catch (UnauthorizedAccessException) { }
                }
            }

            await _lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (CurrentArchive != null)
                {
                    var archiveEntry = CurrentArchive.Entries.FirstOrDefault(e => e.Key == entryKey);
                    if (archiveEntry == null) return null;

                    using var memoryStream = new MemoryStream();
                    using var entryStream = archiveEntry.OpenEntryStream();
                    await entryStream.CopyToAsync(memoryStream, token).ConfigureAwait(false);
                    return memoryStream.ToArray();
                }

                if (Current7zArchive != null)
                {
                    var archiveEntry = Current7zArchive.Entries.FirstOrDefault(e => e.FileName == entryKey);
                    if (archiveEntry == null) return null;

                    using var memoryStream = new MemoryStream();
                    archiveEntry.Extract(memoryStream);
                    return memoryStream.ToArray();
                }

                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public bool ContainsEntryLoose(string entryKey)
        {
            if (!_lock.Wait(TimeSpan.FromMilliseconds(50)))
            {
                return true;
            }

            try
            {
                return FindEntryKeyLoose(entryKey) != null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task<byte[]?> ReadEntryBytesLooseAsync(string entryKey, CancellationToken token = default)
            => Task.Run(() => ReadEntryBytesLooseCoreAsync(entryKey, token), token);

        private async Task<byte[]?> ReadEntryBytesLooseCoreAsync(string entryKey, CancellationToken token)
        {
            string? resolvedKey;
            await _lock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                resolvedKey = FindEntryKeyLoose(entryKey);
            }
            finally
            {
                _lock.Release();
            }
            return resolvedKey == null ? null : await ReadEntryBytesCoreAsync(resolvedKey, token).ConfigureAwait(false);
        }

        public async Task PrepareSevenZipImageCacheAsync(
            string archivePath,
            IReadOnlyList<ImageEntry> imageEntries,
            Func<int> getCurrentIndex,
            SevenZipExtractionCoordinator extraction,
            CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            extraction.TrackExtraction(completion.Task);
            try
            {
                // Folder creation may wait behind startup scavenging. Keep that
                // wait, native enumeration and cache preparation off the dispatcher.
                string tempFolder = await extraction.CreateTempFolderAsync(token).ConfigureAwait(false);
                SevenZipImageCache cache;
                await _lock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (Current7zArchive == null || CurrentPath != archivePath)
                    {
                        completion.TrySetResult();
                        return;
                    }
                    cache = new SevenZipImageCache(Current7zArchive.Entries, imageEntries, tempFolder, token);
                    _sevenZipCache?.Cancel();
                    _sevenZipCache = cache;
                }
                finally { _lock.Release(); }
                _ = CompleteExtractionAsync(cache.ExtractAsync(archivePath, getCurrentIndex), completion);
                return;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"7z background extraction failed: {ex}"); }
            completion.TrySetResult();
        }

        private static async Task CompleteExtractionAsync(Task task, TaskCompletionSource completion)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"7z background extraction failed: {ex}"); }
            finally { completion.TrySetResult(); }
        }

        public void Dispose()
        {
            CloseOpenHandles();
            _lock.Dispose();
        }

        private static List<ImageEntry> BuildSevenZipEntries(ArchiveFile archive)
        {
            return archive.Entries
                .Where(e => !e.IsFolder &&
                    FileExplorerService.GetSupportedFileKind(e.FileName) == SupportedFileKind.Image)
                .OrderBy(e => e.FileName, NaturalSortComparer.Default)
                .Select(e => new ImageEntry
                {
                    DisplayName = Path.GetFileName(e.FileName ?? "Unknown"),
                    ArchiveEntryKey = e.FileName
                })
                .ToList();
        }

        private static List<ImageEntry> BuildSharpCompressEntries(IArchive archive)
        {
            return archive.Entries
                .Where(e => !e.IsDirectory &&
                    FileExplorerService.GetSupportedFileKind(e.Key) == SupportedFileKind.Image)
                .OrderBy(e => e.Key, NaturalSortComparer.Default)
                .Select(e => new ImageEntry
                {
                    DisplayName = Path.GetFileName(e.Key ?? "Unknown"),
                    ArchiveEntryKey = e.Key
                })
                .ToList();
        }

        private static void DeleteWebDavTempArchiveIfNeeded(string? archivePath)
        {
            if (archivePath == null ||
                !archivePath.Contains(Path.Combine("Uviewer", "WebDav")))
            {
                return;
            }

            TryDeleteFile(archivePath);
        }

        private static void TryDeleteFile(string? path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        private string? FindEntryKeyLoose(string entryKey)
        {
            string normalizedTarget = NormalizeEntryKey(entryKey);

            if (CurrentArchive != null)
            {
                return CurrentArchive.Entries
                    .Select(e => e.Key)
                    .FirstOrDefault(key => key != null && NormalizeEntryKey(key) == normalizedTarget)
                    ?? CurrentArchive.Entries
                        .Select(e => e.Key)
                        .FirstOrDefault(key => key != null &&
                            string.Equals(NormalizeEntryKey(key), normalizedTarget, StringComparison.OrdinalIgnoreCase));
            }

            if (Current7zArchive != null)
            {
                return Current7zArchive.Entries
                    .Select(e => e.FileName)
                    .FirstOrDefault(key => key != null && NormalizeEntryKey(key) == normalizedTarget)
                    ?? Current7zArchive.Entries
                        .Select(e => e.FileName)
                        .FirstOrDefault(key => key != null &&
                            string.Equals(NormalizeEntryKey(key), normalizedTarget, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static string NormalizeEntryKey(string entryKey)
        {
            return entryKey.Replace('\\', '/').TrimStart('/');
        }
    }
}

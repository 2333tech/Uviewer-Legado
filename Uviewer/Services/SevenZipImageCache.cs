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
    internal sealed class SevenZipImageCache
    {
        private sealed class Image
        {
            public required ImageEntry Entry { get; init; }
            public required int DisplayIndex { get; init; }
            public required string Path { get; init; }
            public DateTime LastWriteTime { get; init; }
            public TaskCompletionSource<string?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly Dictionary<uint, Image> _images = new();
        private readonly Dictionary<string, Image> _keys = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _cancellation;
        private int _activeWorkers;
        private int _peakActiveWorkers;
        internal int BlockCount { get; private set; }
        internal int WorkerCount { get; private set; }
        internal int PeakActiveWorkers => _peakActiveWorkers;

        public SevenZipImageCache(IList<Entry> entries, IReadOnlyList<ImageEntry> images, string folder, CancellationToken token)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var indices = entries.Select((entry, index) => (entry.FileName, Index: (uint)index))
                .ToDictionary(entry => entry.FileName, entry => entry.Index, StringComparer.Ordinal);
            for (int index = 0; index < images.Count; index++)
            {
                var entry = images[index];
                if (entry.ArchiveEntryKey == null || !indices.TryGetValue(entry.ArchiveEntryKey, out uint nativeIndex)) continue;
                var image = new Image
                {
                    Entry = entry,
                    DisplayIndex = index,
                    Path = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(entry.ArchiveEntryKey)),
                    LastWriteTime = entries[(int)nativeIndex].LastWriteTime
                };
                _images.Add(nativeIndex, image);
                _keys.Add(entry.ArchiveEntryKey, image);
            }
        }

        public bool TryGetImage(string key, out Task<string?> ready)
        {
            if (_keys.TryGetValue(key, out var image))
            {
                ready = image.Ready.Task;
                return true;
            }
            ready = Task.FromResult<string?>(null);
            return false;
        }

        public void Cancel()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public Task ExtractAsync(string archivePath, Func<int> getCurrentIndex) => Task.Run(async () =>
        {
            var token = _cancellation.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                using var first = new SevenZipBatchExtractor(archivePath);
                var blocks = first.GetBlocks(_images.Keys);
                int workers = Math.Min(blocks.Count, Math.Min(Environment.ProcessorCount, 6));
                BlockCount = blocks.Count;
                WorkerCount = workers;
                Debug.WriteLine($"7z image cache: {_images.Count} images, {blocks.Count} blocks, {workers} workers");
                var gate = new object();

                void ExtractBlocks(SevenZipBatchExtractor extractor)
                {
                    while (!token.IsCancellationRequested)
                    {
                        uint[] block;
                        lock (gate)
                        {
                            if (blocks.Count == 0) return;
                            int current = getCurrentIndex();
                            int next = 0;
                            int distance = int.MaxValue;
                            for (int i = 0; i < blocks.Count; i++)
                            {
                                int candidate = blocks[i].Min(index => Math.Abs(_images[index].DisplayIndex - current));
                                if (candidate < distance) (next, distance) = (i, candidate);
                            }
                            block = blocks[next];
                            blocks.RemoveAt(next);
                        }
                        try
                        {
                            int active = Interlocked.Increment(ref _activeWorkers);
                            int peak;
                            do
                            {
                                peak = _peakActiveWorkers;
                                if (active <= peak) break;
                            } while (Interlocked.CompareExchange(ref _peakActiveWorkers, active, peak) != peak);
                            extractor.Extract(block, index => _images[index].Path + ".tmp", (index, path) =>
                            {
                                token.ThrowIfCancellationRequested();
                                var image = _images[index];
                                if (image.LastWriteTime != default)
                                {
                                    try { File.SetLastWriteTime(path, image.LastWriteTime); }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                File.Move(path, image.Path);
                                image.Entry.FilePath = image.Path;
                                image.Ready.TrySetResult(image.Path);
                            }, token);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"7z block extraction failed: {ex}");
                            // A damaged/unsupported entry can still use the ordinary reader.
                            foreach (uint index in block) _images[index].Ready.TrySetResult(null);
                        }
                        finally { Interlocked.Decrement(ref _activeWorkers); }
                    }
                }

                // Each worker owns its native archive in the MTA. Parallelize
                // independent blocks, never repeated decodes of one solid block.
                var tasks = new List<Task>();
                for (int i = 1; i < workers; i++)
                    tasks.Add(Task.Run(() =>
                    {
                        using var extractor = new SevenZipBatchExtractor(archivePath);
                        ExtractBlocks(extractor);
                    }));
                tasks.Add(Task.Run(() => ExtractBlocks(first)));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"7z image cache failed: {ex}"); }
            finally
            {
                foreach (var image in _images.Values)
                {
                    if (token.IsCancellationRequested) image.Ready.TrySetCanceled(token);
                    else image.Ready.TrySetResult(null);
                }
                _cancellation.Dispose();
            }
        });
    }
}

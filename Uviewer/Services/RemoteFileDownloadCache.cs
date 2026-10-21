using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services
{
    internal sealed class RemoteFileDownloadCache
    {
        private sealed record Entry(long Length, string? ETag, DateTimeOffset? LastModified);

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1)).ToArray();

        public async Task<string> DownloadAsync(
            HttpClient client,
            Uri remoteUri,
            string path,
            Func<string, FileStream> createStream,
            Action<string, string> publish,
            CancellationToken token)
        {
            var gate = _gates[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)_gates.Length)];
            await gate.WaitAsync(token).ConfigureAwait(false);
            string? partialPath = null;
            try
            {
                token.ThrowIfCancellationRequested();
                _entries.TryGetValue(path, out var cached);
                if (cached != null && (!File.Exists(path) || new FileInfo(path).Length != cached.Length))
                {
                    _entries.TryRemove(path, out _);
                    cached = null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
                if (cached?.ETag != null)
                    request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
                else if (cached?.LastModified != null)
                    request.Headers.IfModifiedSince = cached.LastModified;

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
                {
                    token.ThrowIfCancellationRequested();
                    return path;
                }

                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.PartialContent)
                    throw new IOException("A full download was requested but the server returned partial content.");

                partialPath = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                long length;
                await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var target = createStream(partialPath))
                {
                    await source.CopyToAsync(target, token).ConfigureAwait(false);
                    await target.FlushAsync(token).ConfigureAwait(false);
                    length = target.Length;
                    if (response.Content.Headers.ContentLength is long expected && length != expected)
                        throw new IOException($"Incomplete download: expected {expected} bytes, received {length}.");
                }

                token.ThrowIfCancellationRequested();
                publish(partialPath, path);
                partialPath = null;
                _entries[path] = new Entry(length, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
                return path;
            }
            finally
            {
                if (partialPath != null)
                {
                    try { File.Delete(partialPath); }
                    catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
                    catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }
                gate.Release();
            }
        }
    }
}

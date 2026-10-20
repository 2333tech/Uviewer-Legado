using System;
using System.Collections.Generic;

namespace Uviewer.Services
{
    internal sealed class ThumbnailMemoryCache<T> where T : class
    {
        private sealed record Entry(string Key, T Value, long Bytes);
        private readonly object _gate = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _recent = new();
        private readonly long _maxBytes;
        private readonly int _maxEntries;
        private long _bytes;

        public ThumbnailMemoryCache(long maxBytes = 128 * 1024 * 1024, int maxEntries = 1024)
            => (_maxBytes, _maxEntries) = (maxBytes, maxEntries);

        public bool TryGet(string key, out T? value)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var node))
                {
                    value = null;
                    return false;
                }
                _recent.Remove(node);
                _recent.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        public void Add(string key, T value, long bytes)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var previous)) Remove(previous);
                if (bytes <= 0 || bytes > _maxBytes) return;
                var node = _recent.AddFirst(new Entry(key, value, bytes));
                _entries.Add(key, node);
                _bytes += bytes;
                while (_bytes > _maxBytes || _entries.Count > _maxEntries)
                    Remove(_recent.Last!);
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _recent.Remove(node);
            _bytes -= node.Value.Bytes;
        }
    }
}

using System;
using System.IO;

namespace Uviewer.Services
{
    // Bound both advertised entry sizes and actual native output, including .tmp files.
    internal sealed class SevenZipDiskBudget
    {
        internal const long MaximumCacheBytes = 2L * 1024 * 1024 * 1024;
        internal const long MinimumFreeBytes = 512L * 1024 * 1024;
        private readonly object _gate = new();
        private readonly Func<long> _getFreeBytes;
        private readonly long _maximumBytes;
        private readonly long _minimumFreeBytes;
        private long _admitted;
        private long _written;
        private long _bytesUntilCheck;
        private long _freeBytes;

        internal SevenZipDiskBudget(string folder)
            : this(() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace,
                MaximumCacheBytes, MinimumFreeBytes) { }

        internal SevenZipDiskBudget(Func<long> getFreeBytes, long maximumBytes, long minimumFreeBytes)
            => (_getFreeBytes, _maximumBytes, _minimumFreeBytes) = (getFreeBytes, maximumBytes, minimumFreeBytes);

        internal bool TryAdmit(ulong size)
        {
            lock (_gate)
            {
                if (size > (ulong)(_maximumBytes - _admitted)) return false;
                if (_getFreeBytes() - _minimumFreeBytes < (long)size) return false;
                _admitted += (long)size;
                return true;
            }
        }

        internal void BeforeWrite(int count)
        {
            lock (_gate)
            {
                if (count > _maximumBytes - _written) throw new IOException("7z temporary cache size limit reached.");
                if (_bytesUntilCheck <= 0)
                {
                    _freeBytes = _getFreeBytes();
                    _bytesUntilCheck = 1024 * 1024;
                }
                // Decrement between probes, accounting for every parallel worker.
                if (_freeBytes - _minimumFreeBytes < count) throw new IOException("Insufficient free space for 7z temporary cache.");
                _freeBytes -= count;
                _bytesUntilCheck -= count;
                _written += count;
            }
        }
    }
}

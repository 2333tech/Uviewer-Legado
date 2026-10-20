using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Uviewer.Services
{
    internal static class ExclusiveFileLock
    {
        // Keep the lock file: deleting it after release lets waiters lock different files.
        internal static FileStream Acquire(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
            }
        }
    }
}

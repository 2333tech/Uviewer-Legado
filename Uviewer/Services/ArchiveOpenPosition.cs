using System;
using System.Collections.Generic;
using Uviewer.Models;

namespace Uviewer.Services
{
    internal static class ArchiveOpenPosition
    {
        internal static int Resolve(IReadOnlyList<ImageEntry> entries, string? entryKey)
        {
            if (entries.Count == 0) return -1;
            if (!string.IsNullOrEmpty(entryKey))
            {
                for (int i = 0; i < entries.Count; i++)
                    if (string.Equals(entries[i].ArchiveEntryKey, entryKey, StringComparison.Ordinal)) return i;
                string normalized = entryKey.Replace('\\', '/');
                for (int i = 0; i < entries.Count; i++)
                    if (string.Equals(entries[i].ArchiveEntryKey?.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }
    }
}

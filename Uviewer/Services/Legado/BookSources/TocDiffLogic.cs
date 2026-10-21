using System;
using System.Collections.Generic;
using System.Globalization;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 目录差分结果（在线更新的核心中间态）。
/// <para>键口径：<b>章节 URL</b> 为主键（同一章再版不改 URL）；URL 为空的条目（卷标题等）
/// 退化为「章名」做键。两侧目录都按<strong>原顺序</strong>比较。</para>
/// </summary>
internal sealed record TocDiff(
    /// <summary>新目录中「旧目录没有」的条目下标（需要下载）。</summary>
    IReadOnlyList<int> AddedIndexes,
    /// <summary>新目录中「URL 已存在但章名变了」的条目下标（需要重新下载：章名变 = 站点改版该章）。</summary>
    IReadOnlyList<int> ChangedIndexes,
    /// <summary>旧目录中有、新目录中没有的条目数（被删章）。</summary>
    int RemovedCount,
    /// <summary>完全一致的条目数。</summary>
    int UnchangedCount)
{
    /// <summary>需要联网下载的章总数。</summary>
    public int DownloadCount => AddedIndexes.Count + ChangedIndexes.Count;

    /// <summary>被删章 + 被改章（内容层面被动过的规模）。</summary>
    public int MutatedCount => RemovedCount + ChangedIndexes.Count;
}

/// <summary>
/// 在线更新的差分纯逻辑（全部无 IO，可离线单测）。
/// </summary>
internal static class TocDiffLogic
{
    /// <summary>
    /// 对比新旧目录。
    /// </summary>
    public static TocDiff Compare(
        IReadOnlyList<DownloadedBookMarker.SidecarTocItem> oldToc,
        IReadOnlyList<DownloadedBookMarker.SidecarTocItem> newToc)
    {
        var oldKeys = new HashSet<string>(oldToc.Count, StringComparer.Ordinal);
        foreach (var item in oldToc)
        {
            oldKeys.Add(KeyOf(item));
        }

        // 同 URL 出现两次（目录重复项）只在第一次计入，避免重复下载同一章
        var seen = new HashSet<string>(newToc.Count, StringComparer.Ordinal);
        var added = new List<int>();
        var changed = new List<int>();

        for (var i = 0; i < newToc.Count; i++)
        {
            var item = newToc[i];
            var key = KeyOf(item);
            if (!seen.Add(key))
            {
                continue;
            }

            if (!oldKeys.Contains(key))
            {
                // URL 不在旧目录 → 新章；但可能是"URL 在、键退化到章名"的重命名，
                // 用 URL 单独再查一次（URL 相同名不同 = 改章名，需要重下取新章名）
                added.Add(i);
            }
            else if (!string.Equals(
                         FindName(oldToc, key), item.Name, StringComparison.Ordinal))
            {
                changed.Add(i);
            }
        }

        var removed = 0;
        var newKeys = new HashSet<string>(newToc.Count, StringComparer.Ordinal);
        foreach (var item in newToc)
        {
            newKeys.Add(KeyOf(item));
        }

        foreach (var item in oldToc)
        {
            if (!newKeys.Contains(KeyOf(item)))
            {
                removed++;
            }
        }

        var unchanged = CountUnchanged(oldToc, newToc);
        return new TocDiff(added, changed, removed, unchanged);
    }

    /// <summary>
    /// 是否"大规模修改"（另存保护阈值）。
    /// <para>口径：被删 + 被改 ≥ max(10, 旧目录 20%)。低于阈值走差分合并（覆盖原文件），
    /// 达到阈值说明站点对这本书动了大手术——旧文件的正文对新目录已基本对不上号，
    /// 此时<b>另存新文件</b>而不是覆盖：用户辛苦下载的旧版本不该被悄悄换掉。</para>
    /// </summary>
    public static bool IsMassiveChange(TocDiff diff, int oldCount)
    {
        if (oldCount <= 0)
        {
            return diff.MutatedCount > 0;
        }

        var threshold = Math.Max(10, (int)Math.Ceiling(oldCount * 0.2));
        return diff.MutatedCount >= threshold;
    }

    /// <summary>
    /// 从旧 TXT 中按序提取已下载章的正文。
    /// <para><b>格式契约</b>（与 <c>BuildChapter</c> 一致）：章名单独成行写在每章最前面。
    /// 按「顺序游标」匹配：从头扫行，行.Trim() 等于游标指向的章名 → 记章起点并前进游标；
    /// 章正文 = 本起点到下一章名行/EOF。章名找不到 → 该章返回 null（调用方标为需重新下载），
    /// 游标不前进继续找后续章名。</para>
    /// <para>返回：<c>提取的正文[旧目录下标]</c>；提取失败的章不在字典里。</para>
    /// </summary>
    public static Dictionary<int, string> ExtractOldChapters(
        string oldRaw, IReadOnlyList<DownloadedBookMarker.SidecarTocItem> oldToc)
    {
        var result = new Dictionary<int, string>(oldToc.Count);
        if (string.IsNullOrEmpty(oldRaw) || oldToc.Count == 0)
        {
            return result;
        }

        var lines = oldRaw.Split('\n');
        var cursor = 0;
        var currentStart = -1;
        var currentIndex = -1;

        void Flush(int lineEnd)
        {
            if (currentStart >= 0 && currentIndex >= 0)
            {
                // 章正文从章名行下一行到本行前。BuildChapter 在章名行下写一条
                // "--------" 分隔线（32 个 '-'），提取时连同空行一起剥掉；
                result[currentIndex] = JoinTrimmed(lines, currentStart + 1, lineEnd);
            }
        }

        for (var i = 0; i < lines.Length && cursor < oldToc.Count; i++)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 顺序匹配：从游标处向后找第一个等于本行的章名（跳过提取失败的章）
            var matched = -1;
            for (var j = cursor; j < oldToc.Count; j++)
            {
                if (string.Equals(oldToc[j].Name.Trim(), line, StringComparison.Ordinal))
                {
                    matched = j;
                    break;
                }
            }

            if (matched < 0)
            {
                continue;
            }

            Flush(i);
            currentStart = i;
            currentIndex = matched;
            cursor = matched + 1;
        }

        Flush(lines.Length);
        return result;
    }

    private static string JoinTrimmed(string[] lines, int start, int endExclusive)
    {
        var from = start;
        var to = endExclusive;
        while (from < to && IsSkippable(lines[from]))
        {
            from++;
        }

        while (to > from && lines[to - 1].Trim().Length == 0)
        {
            to--;
        }

        return string.Join('\n', lines[from..to]);
    }

    /// <summary>章首可跳过行：空行或 BuildChapter 的 "----" 分隔线。</summary>
    private static bool IsSkippable(string line)
    {
        var text = line.Trim();
        return text.Length == 0
            || (text.Length >= 8 && text.All(c => c == '-'));
    }

    /// <summary>差分主键：章节 URL；URL 为空（卷标题等）退化为章名。</summary>
    internal static string KeyOf(DownloadedBookMarker.SidecarTocItem item) =>
        string.IsNullOrEmpty(item.Url) ? "n:" + item.Name : item.Url;

    private static string? FindName(IReadOnlyList<DownloadedBookMarker.SidecarTocItem> toc, string key)
    {
        foreach (var item in toc)
        {
            if (string.Equals(KeyOf(item), key, StringComparison.Ordinal))
            {
                return item.Name;
            }
        }

        return null;
    }

    private static int CountUnchanged(
        IReadOnlyList<DownloadedBookMarker.SidecarTocItem> oldToc,
        IReadOnlyList<DownloadedBookMarker.SidecarTocItem> newToc)
    {
        var map = new Dictionary<string, string>(oldToc.Count, StringComparer.Ordinal);
        foreach (var item in oldToc)
        {
            map.TryAdd(KeyOf(item), item.Name);
        }

        var unchanged = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in newToc)
        {
            var key = KeyOf(item);
            if (seen.Add(key)
                && map.TryGetValue(key, out var oldName)
                && string.Equals(oldName, item.Name, StringComparison.Ordinal))
            {
                unchanged++;
            }
        }

        return unchanged;
    }
}

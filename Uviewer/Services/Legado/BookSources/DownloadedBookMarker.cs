using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源下载 TXT 的<b>文件内标记</b>（方案 A 的"文件内存简单信息"部分）。
/// <para><b>为什么放文件头</b>：标记必须跟随文件本身——改名/移动/拷贝后依然能
/// 「认出这是书源下载的书、它来自哪里」。大规模内容（目录快照等）放
/// <see cref="DownloadedBookSidecar"/>（同目录 sidecar JSON），文件头只留几行索引信息。</para>
/// <para><b>格式</b>（全部 LF、UTF-8 无 BOM，与下载器写文件口径一致）：</para>
/// <code>
/// [Uviewer-Legado]
/// UUID: 6f1c…            ← 唯一身份：换文件名/位置后凭它与本地章节缓存关联
/// 书源URL: https://…      ← 在线更新的来源书源
/// 书籍URL: https://…      ← 在线更新的目标书（tocUrl/bookUrl）
/// 导出时间: 2026-10-21 18:00:00
/// </code>
/// </summary>
internal static class DownloadedBookMarker
{
    /// <summary>标记块起始行（方括号形式，标题/分章规则不会误认）。</summary>
    public const string HeaderTag = "[Uviewer-Legado]";

    /// <summary>头部扫描上限（行）：标记块只出现在文件最前面，扫到就停。</summary>
    private const int MaxScanLines = 40;

    /// <summary>文件头解析结果（全部字段可为空：本地缓存文件只有 UUID 一项）。</summary>
    public sealed record MarkerInfo(string Uuid, string? SourceUrl, string? BookUrl, string? ExportedAt);

    /// <summary>
    /// 从文本（文件头部）解析标记。返回 null = 不是带标记的文件。
    /// <para>容错：标记块允许出现在前 <see cref="MaxScanLines"/> 行内任意位置
    /// （下载文件固定在头部；本地缓存文件插入的块也在头部）。</para>
    /// </summary>
    public static MarkerInfo? TryParseHeader(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        // 只扫头部一小段（标记块固定在文件最前面）；分段小，Split 开销可忽略
        var head = raw.AsSpan(0, Math.Min(raw.Length, 8_192)).ToString();
        string? uuid = null, sourceUrl = null, bookUrl = null, exportedAt = null;
        var inBlock = false;

        foreach (var rawLine in head.Split('\n'))
        {
            var text = rawLine.TrimEnd('\r').Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.StartsWith(HeaderTag, StringComparison.Ordinal))
            {
                inBlock = true;
                continue;
            }

            if (!inBlock)
            {
                continue;
            }

            // 标记块以"键: 值"行组成；遇到第一个不认识的非空行即视为块结束
            if (TryReadValue(text, "UUID:", ref uuid)
                || TryReadValue(text, "书源URL:", ref sourceUrl)
                || TryReadValue(text, "书籍URL:", ref bookUrl)
                || TryReadValue(text, "导出时间:", ref exportedAt))
            {
                continue;
            }

            break;
        }

        return uuid is null ? null : new MarkerInfo(uuid, sourceUrl, bookUrl, exportedAt);
    }

    /// <summary>从磁盘文件读标记（只读头部一小段，不整本载入）。</summary>
    public static MarkerInfo? TryReadFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[8_192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return TryParseHeader(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>是否为书源下载文件（有完整来源标记：UUID + 书源URL + 书籍URL）。</summary>
    public static bool IsDownloadedFile(string filePath) =>
        TryReadFile(filePath) is { } m
        && !string.IsNullOrEmpty(m.SourceUrl)
        && !string.IsNullOrEmpty(m.BookUrl);

    /// <summary>生成标记块文本（调用方决定插到哪；结尾自带换行）。</summary>
    public static string BuildHeaderBlock(string uuid, string? sourceUrl, string? bookUrl, string? exportedAt)
    {
        var sb = new StringBuilder(160);
        sb.Append(HeaderTag).Append('\n');
        sb.Append("UUID: ").Append(uuid).Append('\n');
        if (!string.IsNullOrEmpty(sourceUrl))
        {
            sb.Append("书源URL: ").Append(sourceUrl).Append('\n');
        }

        if (!string.IsNullOrEmpty(bookUrl))
        {
            sb.Append("书籍URL: ").Append(bookUrl).Append('\n');
        }

        if (!string.IsNullOrEmpty(exportedAt))
        {
            sb.Append("导出时间: ").Append(exportedAt).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把标记块插到文本最前面（已带同 UUID 标记时原样返回 —— 幂等，不重复插）。
    /// </summary>
    public static (string Content, bool Inserted) InsertHeaderBlock(string raw, string block)
    {
        var existing = TryParseHeader(raw);
        var newUuid = ExtractValue(block, "UUID:");
        if (existing is not null && (newUuid is null || string.Equals(existing.Uuid, newUuid, StringComparison.Ordinal)))
        {
            return (raw, false);
        }

        return (block + raw, true);
    }

    // ============================================================
    // Sidecar：方案 A 的"大规模内容"部分（目录快照 + 身份冗余）
    // ============================================================

    /// <summary>sidecar 文件名：紧跟原文件（<c>&lt;txt 全路径&gt;.legado.json</c>）。</summary>
    public static string SidecarPathFor(string txtPath) => txtPath + ".legado.json";

    /// <summary>sidecar 目录条目（章节名 + 章节 URL；URL 为差分主键）。</summary>
    public sealed record SidecarTocItem(string Name, string Url);

    /// <summary>sidecar 内容：下载时的目录快照 + 身份信息（供在线更新做差分）。</summary>
    public sealed record SidecarData(
        string Uuid,
        string SourceUrl,
        string BookUrl,
        string BookName,
        string ExportedAt,
        List<SidecarTocItem> Toc);

    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// 读 sidecar：先找 <c>&lt;txt&gt;.legado.json</c>；找不到（用户改过文件名）则
    /// 扫描同目录 <c>*.legado.json</c> 按 UUID 匹配 —— 改名后标记还在文件里，sidecar 跟着 UUID 走。
    /// </summary>
    public static SidecarData? LoadSidecar(string txtPath, string? uuid = null)
    {
        var primary = SidecarPathFor(txtPath);
        var data = TryLoadSidecarFile(primary);
        if (data is not null)
        {
            return data;
        }

        if (string.IsNullOrEmpty(uuid))
        {
            var parsed = TryReadFile(txtPath);
            uuid = parsed?.Uuid;
            if (string.IsNullOrEmpty(uuid))
            {
                return null;
            }
        }

        // 同目录扫描：sidecar 文件都很小，逐个读 UUID 字段代价可忽略（目录内文件数有限）
        var dir = Path.GetDirectoryName(txtPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        foreach (var candidate in Directory.EnumerateFiles(dir, "*.legado.json"))
        {
            if (string.Equals(candidate, primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sidecar = TryLoadSidecarFile(candidate);
            if (sidecar is not null && string.Equals(sidecar.Uuid, uuid, StringComparison.Ordinal))
            {
                return sidecar;
            }
        }

        return null;
    }

    /// <summary>写 sidecar（原子替换：tmp + File.Move；一律 LF）。</summary>
    public static void SaveSidecar(string txtPath, SidecarData data)
    {
        var path = SidecarPathFor(txtPath);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(data, SidecarJsonOptions);
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>删除 sidecar（差分更新重组成功后旧 sidecar 被覆盖，无需删除；预留给失败回滚）。</summary>
    public static void DeleteSidecar(string txtPath)
    {
        try
        {
            File.Delete(SidecarPathFor(txtPath));
        }
        catch
        {
            // 删除失败不影响主流程
        }
    }

    private static SidecarData? TryLoadSidecarFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SidecarData>(File.ReadAllText(path), SidecarJsonOptions);
        }
        catch
        {
            // 损坏 sidecar：当作没有（在线更新退化为全量或提示重新下载）
            return null;
        }
    }

    private static bool TryReadValue(string line, string prefix, ref string? target)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = line[prefix.Length..].Trim();
        if (value.Length > 0)
        {
            target = value;
        }

        return true;
    }

    private static string? ExtractValue(string block, string prefix)
    {
        foreach (var line in block.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text[prefix.Length..].Trim();
            }
        }

        return null;
    }
}

/// <summary>
/// 差分时间戳（文件名安全格式，另存大改副本时用）。
/// </summary>
internal static class MarkerTimestamp
{
    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ForFileName() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}

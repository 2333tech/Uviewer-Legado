using System;
using System.Text.Json.Serialization;

namespace Uviewer.Services.Legado.Models;

/// <summary>
/// 阅读位置（断点续读的持久化单元，对标 legado 的 readRecord / reader 前端的阅读进度）。
/// <para>键 = (<see cref="BookSourceUrl"/>, <see cref="BookUrl"/>)：同一本书在同一书源下只有一条记录，
/// 换书源或换书籍入口（如不同搜索页 URL）各记各的。</para>
/// <para>不缓存章节内容本身：章节正文可能很长且会随书源更新过期，
/// 内容缓存由宿主（阅读器）按 <c>BookSourceReadingStore</c> 的位置记录自行落盘管理。</para>
/// </summary>
public sealed record ReadingPosition
{
    /// <summary>书源 URL（<c>bookSourceUrl</c>，书源主键）。</summary>
    public string BookSourceUrl { get; init; } = string.Empty;

    /// <summary>书籍 URL（<c>book.url</c>，书籍主键）。</summary>
    public string BookUrl { get; init; } = string.Empty;

    /// <summary>书名（展示用；书源更新书名后不强制刷新旧记录）。</summary>
    public string BookName { get; init; } = string.Empty;

    /// <summary>作者（展示用）。</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>当前章节下标（0 基，对应目录列表序号）。</summary>
    public int ChapterIndex { get; init; }

    /// <summary>当前章节标题（展示用；目录刷新后按下标重新取题）。</summary>
    public string ChapterTitle { get; init; } = string.Empty;

    /// <summary>最近阅读时刻（UTC ticks；用 ticks 存储避免时区/格式往返差异）。</summary>
    public long LastReadTicks { get; init; }

    /// <summary>最近阅读时刻（UTC）。</summary>
    [JsonIgnore]
    public DateTime LastReadUtc => new(LastReadTicks, DateTimeKind.Utc);

    /// <summary>用当前 UTC 时刻刷新时间戳，返回新记录（记录不可变，避免共享引用被就地改）。</summary>
    public ReadingPosition Touch() => this with { LastReadTicks = DateTime.UtcNow.Ticks };
}

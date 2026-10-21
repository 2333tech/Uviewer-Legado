namespace Uviewer.Services.Legado.Models;

/*
 * 运行时输出 DTO：书源编排（搜索/详情/目录/正文）产生的对象。
 * 它们复用与书源规则相同的字段语义，便于后续阶段（阅读器/缓存）直接消费。
 * 这些类型也会被序列化进章节缓存，故登记到 BookSourceJsonContext。
 */

/// <summary>
/// 搜索结果 / 书籍条目（搜索列表或发现列表的一行）。
/// <para>所有 URL 字段在产出时已被 <see cref="Services.BookSources.UrlUtil.ResolveUrl"/> 绝对化；
/// 文本字段保留规则原始输出（未清洗，清洗在阶段 3 管线）。</para>
/// </summary>
public class SearchResult
{
    /// <summary>书名。</summary>
    public string? Name { get; set; }

    /// <summary>书籍详情页 URL（已绝对化）。</summary>
    public string? BookUrl { get; set; }

    /// <summary>作者。</summary>
    public string? Author { get; set; }

    /// <summary>简介。</summary>
    public string? Intro { get; set; }

    /// <summary>分类/标签。</summary>
    public string? Kind { get; set; }

    /// <summary>最新章节名。</summary>
    public string? LastChapter { get; set; }

    /// <summary>更新时间。</summary>
    public string? UpdateTime { get; set; }

    /// <summary>封面图 URL（已绝对化；data: URL 原样保留）。</summary>
    public string? CoverUrl { get; set; }

    /// <summary>字数。</summary>
    public string? WordCount { get; set; }
}

/// <summary>书籍详情（在 <see cref="SearchResult"/> 基础上增加目录 URL）。</summary>
public sealed class BookInfo : SearchResult
{
    /// <summary>目录页 URL（已绝对化；详情页无 tocUrl 规则时回退为详情页 URL 自身）。</summary>
    public string? TocUrl { get; set; }
}

/// <summary>
/// 目录条目（一章或一个分卷标题）。
/// <para>去重键是 <see cref="ChapterUrl"/>（绝对化后的形式）：reader 按绝对 URL 去重，
/// 重复章节直接丢弃而不是合并。</para>
/// </summary>
public sealed class TocItem
{
    /// <summary>全局序号（0-based，合并多页目录后连续重排；对齐 reader BookChapter.index）。</summary>
    public int Index { get; set; }

    /// <summary>章节名。</summary>
    public string? ChapterName { get; set; }

    /// <summary>章节 URL（已绝对化；无 URL 的卷走 title+index 伪 URL）。</summary>
    public string? ChapterUrl { get; set; }

    /// <summary>是否分卷标题（非正文章节）。</summary>
    public bool IsVolume { get; set; }

    /// <summary>是否 VIP 章节。</summary>
    public bool IsVip { get; set; }

    /// <summary>是否付费章节。</summary>
    public bool IsPay { get; set; }

    /// <summary>章节更新时间。</summary>
    public string? UpdateTime { get; set; }
}

/// <summary>
/// 正文章节内容（HTML 文本 + 内联图片 URL 列表）。
/// </summary>
public sealed class ChapterContent
{
    /// <summary>章节标题（来自 ruleContent.title；为空时批量路径回退目录章节名）。</summary>
    public string? Title { get; set; }

    /// <summary>正文 HTML（含 &lt;img&gt; 标签，由后续阶段下载/渲染；开头重复标题已被剥离）。</summary>
    public string? Content { get; set; }

    /// <summary>下一页正文 URL（分页跟随用；已绝对化）。</summary>
    public string? NextContentUrl { get; set; }

    /// <summary>正文中抽取出的图片 URL（绝对化 + 去重，保序；供 ImageResourceService 渲染）。</summary>
    public List<string> ImageUrls { get; set; } = new();
}

namespace Uviewer.Services.Legado.Models;

/*
 * 规则子结构，逐字段对齐 reader/src/model/rule.rs（该文件即 Legado 规则语法的权威映射）。
 * 属性 PascalCase + CamelCase 序列化策略 => 输出 bookList / checkKeyWord / nextContentUrl 等。
 * reader 中 ExploreRule 直接等于 SearchRule，此处同样复用 SearchRule。
 */

/// <summary>
/// 搜索结果列表 / 书籍列表 / 发现列表共用的字段规则（对齐 reader BookListRule + SearchRule）。
/// <para>用法：<see cref="BookList"/> 是"列表容器"规则（取到 N 个片段），
/// 其余字段规则在**每个片段内部**求值（见 RuleEngine 的片段机制）。</para>
/// </summary>
public class SearchRule
{
    /// <summary>可用性检测关键词（缺省回退 "我的"；探测书源是否存活时用）。</summary>
    public string? CheckKeyWord { get; set; }

    /// <summary>书籍列表容器选择器（如 data.books[*] 或 @css:.book-list&gt;li）。</summary>
    public string? BookList { get; set; }

    /// <summary>书名。</summary>
    public string? Name { get; set; }

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

    /// <summary>书籍详情页 URL（相对地址，由引擎绝对化）。</summary>
    public string? BookUrl { get; set; }

    /// <summary>封面图 URL（相对地址，由引擎绝对化）。</summary>
    public string? CoverUrl { get; set; }

    /// <summary>字数。</summary>
    public string? WordCount { get; set; }
}

/// <summary>发现规则，结构与搜索规则完全一致（对齐 reader `pub type ExploreRule = SearchRule`）。</summary>
public sealed class ExploreRule : SearchRule;

/// <summary>
/// 书籍详情规则（对齐 reader BookInfoRule）。
/// <para><see cref="Init"/> 的特殊性：它是一条"预处理"规则，输出会**整体替换**待解析的原文
/// （AllInOne 模式，如 init = data.book 把作用域下移到 JSON 子树）；
/// CSS/XPath 形态的 init 取首个匹配元素的 OuterHtml 作为新原文。</para>
/// </summary>
public sealed class BookInfoRule
{
    /// <summary>预处理 JS / AllInOne（输出替换原文，见类型注释）。</summary>
    public string? Init { get; set; }

    /// <summary>书名（空结果时由编排层兜底：书架名 → URL 末段）。</summary>
    public string? Name { get; set; }

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

    /// <summary>封面图 URL（取绝对地址）。</summary>
    public string? CoverUrl { get; set; }

    /// <summary>字数。</summary>
    public string? WordCount { get; set; }

    /// <summary>目录页 URL（取绝对地址；为空时回退详情页 URL，见 RuleEngine.ParseBookInfo）。</summary>
    public string? TocUrl { get; set; }

    /// <summary>是否允许重命名（reader can_re_name）。</summary>
    public string? CanReName { get; set; }

    /// <summary>下载地址规则（reader download_urls）。</summary>
    public string? DownloadUrls { get; set; }
}

/// <summary>
/// 目录规则（对齐 reader TocRule）。
/// <para><see cref="ChapterList"/> 取到 N 个章节片段；<see cref="NextTocUrl"/> 支持三种形态：
/// 单 URL（链式跟随）/ URL 数组（有界并行抓取后按顺序合并）/ 正则与 JS 形态（恒返回空）。</para>
/// </summary>
public sealed class TocRule
{
    /// <summary>目录预处理 JS（输出非空则替换原文，对标 reader prepare_toc_body）。</summary>
    public string? PreUpdateJs { get; set; }

    /// <summary>目录页 init（文档级执行一次，仅取副作用如 java.put，返回值丢弃）。</summary>
    public string? Init { get; set; }

    /// <summary>章节列表容器选择器。</summary>
    public string? ChapterList { get; set; }

    /// <summary>章节名。</summary>
    public string? ChapterName { get; set; }

    /// <summary>章节 URL（相对地址；为空时按"卷/普通"走伪 URL 兜底，见 RuleEngine.FinalizeChapterUrl）。</summary>
    public string? ChapterUrl { get; set; }

    /// <summary>章节标题格式化 JS（每章执行一次，绑定 index/title/chapter，对标 reader apply_toc_format_js）。</summary>
    public string? FormatJs { get; set; }

    /// <summary>是否分卷（"true"/JS 表达式；无 URL 的卷走 title+index 伪 URL）。</summary>
    public string? IsVolume { get; set; }

    /// <summary>是否 VIP 章节。</summary>
    public string? IsVip { get; set; }

    /// <summary>是否付费章节。</summary>
    public string? IsPay { get; set; }

    /// <summary>章节更新时间。</summary>
    public string? UpdateTime { get; set; }

    /// <summary>下一页目录 URL（单 URL / 数组 / 并发）。</summary>
    public string? NextTocUrl { get; set; }
}

/// <summary>
/// 正文规则（对齐 reader ContentRule）。
/// <para>求值顺序（RuleEngine.ParseContentPage 严格按此顺序，与 reader 一致）：
/// SourceRegex 裁剪响应体 → WebJs → 前导 JS 循环 → 选择器（为空则整份原文）→ 尾随 JS → ReplaceRegex。</para>
/// </summary>
public sealed class ContentRule
{
    /// <summary>正文选择器/规则。注意 null（缺失）与 ""（显式空串）语义不同：null → 返回空串；"" → 整个原文作为正文（reader Some("") 语义）。</summary>
    public string? Content { get; set; }

    /// <summary>章节标题。</summary>
    public string? Title { get; set; }

    /// <summary>下一页正文 URL（分页跟随；受编排层闸门约束，见 BookSourceEngine.ShouldFollowContentPage）。</summary>
    public string? NextContentUrl { get; set; }

    /// <summary>后处理 JS（需在真实浏览器执行时交 WebView2；阶段 4 消费）。</summary>
    public string? WebJs { get; set; }

    /// <summary>提取前按此正则裁剪响应体（legado sourceRegex）。</summary>
    public string? SourceRegex { get; set; }

    /// <summary>提取后按此正则清洗（legado replaceRegex）。</summary>
    public string? ReplaceRegex { get; set; }

    /// <summary>图片样式：full / center / 等（reader image_style）。</summary>
    public string? ImageStyle { get; set; }

    /// <summary>图片解密 JS（正文内联图；阶段 3 图片管线消费）。</summary>
    public string? ImageDecode { get; set; }

    /// <summary>付费章节处理动作（reader pay_action）。</summary>
    public string? PayAction { get; set; }
}

/// <summary>段评规则（legado 已弱化，仅建模备用）。</summary>
public sealed class ReviewRule
{
    /// <summary>段评列表 URL。</summary>
    public string? ReviewUrl { get; set; }

    /// <summary>头像规则。</summary>
    public string? AvatarRule { get; set; }

    /// <summary>段评内容规则。</summary>
    public string? ContentRule { get; set; }

    /// <summary>段评时间规则。</summary>
    public string? PostTimeRule { get; set; }

    /// <summary>引用段评 URL。</summary>
    public string? ReviewQuoteUrl { get; set; }

    /// <summary>点赞规则。</summary>
    public string? VoteUpRule { get; set; }

    /// <summary>点踩规则。</summary>
    public string? VoteDownRule { get; set; }

    /// <summary>发布段评 URL。</summary>
    public string? PostReviewUrl { get; set; }

    /// <summary>引用发布段评 URL。</summary>
    public string? PostQuoteUrl { get; set; }

    /// <summary>删除段评 URL。</summary>
    public string? DeleteRule { get; set; }
}

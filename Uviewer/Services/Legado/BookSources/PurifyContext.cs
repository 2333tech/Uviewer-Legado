namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 净化管线的书籍上下文（对标 reader 前端 applyReplaceRules 里的 currentBook 三要素 +
/// readConfig.use_replace_rule 开关）。
/// <para>三个标识全部可空——空上下文中只有"全部书籍"作用域（空/'*'）的规则会命中，
/// 与 reader 的 <c>if (!currentBook) return false</c> 语义一致。</para>
/// <para><paramref name="UseReplaceRule"/> 用**可空bool**（对标 reader 的
/// <c>use_replace_rule: Option&lt;bool&gt;</c>）：null = 未配置 = 启用；false = 显式关闭旁路；
/// true = 显式启用。不用非空 bool 是因为 struct 的零初始化（default / new PurifyContext()）
/// 会得到 false，那会让"忘传参"静默变成"关闭净化"——与 reader 的缺省启用相反。</para>
/// </summary>
/// <param name="BookName">书名（scope 的 "&lt;书名&gt;[;&lt;书籍URL&gt;]" 形态匹配用）。</param>
/// <param name="BookUrl">书籍 URL（scope 的 "book:&lt;url&gt;" 与 name;url 第二段匹配用）。</param>
/// <param name="Origin">书源 URL（scope 的 "source:&lt;origin&gt;" 匹配用）。</param>
/// <param name="UseReplaceRule">是否启用净化：null（默认）/true = 启用；false = 旁路。</param>
public readonly record struct PurifyContext(
    string? BookName = null,
    string? BookUrl = null,
    string? Origin = null,
    bool? UseReplaceRule = null)
{
    /// <summary>无书籍上下文 + 启用净化的常用实例（零初始化即此语义，显式声明更可读）。</summary>
    public static PurifyContext Default { get; } = new(null, null, null, null);
}

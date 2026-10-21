using System;
using Uviewer.Services.Legado.Models;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 一次解析的"书籍上下文"（阶段 4-2 新增）：脚本里 <c>book.*</c> 的取值来源。
/// <para>为什么单独建模而不是直接传 <see cref="BookInfo"/>：</para>
/// <list type="bullet">
/// <item><see cref="BookInfo"/> 是"详情页解析的产出"，字段随规则可能为空，而脚本要的是
///   legado <c>Book</c> 的读写面（含 <c>durChapterIndex</c>/<c>durChapterTitle</c> 这类阅读进度）；</item>
/// <item>目录链、正文链、formatJs 都需要同一份上下文，但各自只需要"只读快照"，
///   用不可变 record 避免并行正文时互相踩写。</item>
/// </list>
/// <para>字段命名对齐 legado <c>Book</c> / reader <c>book_ctx</c>，故 <c>book.durChapterIndex</c>
/// 这类规则在两端都能直接跑。</para>
/// </summary>
public sealed record JsBookState
{
    /// <summary>书名（<c>book.name</c>）。正文链里最常见的用途：缓存键、请求签名。</summary>
    public string? Name { get; init; }

    /// <summary>作者（<c>book.author</c>）。</summary>
    public string? Author { get; init; }

    /// <summary>书籍详情页 URL（<c>book.bookUrl</c>，也是书籍变量的作用域键）。</summary>
    public string? BookUrl { get; init; }

    /// <summary>目录页 URL（<c>book.tocUrl</c>）。</summary>
    public string? TocUrl { get; init; }

    /// <summary>简介（<c>book.intro</c>）。</summary>
    public string? Intro { get; init; }

    /// <summary>分类/标签（<c>book.kind</c>）。</summary>
    public string? Kind { get; init; }

    /// <summary>封面 URL（<c>book.coverUrl</c>）。</summary>
    public string? CoverUrl { get; init; }

    /// <summary>当前阅读到的章节下标（<c>book.durChapterIndex</c>；未读为 0，对齐 legado 默认值）。</summary>
    public int DurChapterIndex { get; init; }

    /// <summary>当前章节名（<c>book.durChapterTitle</c>）。</summary>
    public string? DurChapterTitle { get; init; }

    /// <summary>总章数（<c>book.totalChapterNum</c>；未知为 -1）。</summary>
    public int TotalChapterNum { get; init; } = -1;

    /// <summary>该书的"替换净化"开关（<c>book.getUseReplaceRule()</c>；null = 跟随总开关）。</summary>
    public bool? UseReplaceRule { get; init; }

    /// <summary>只有书籍 URL 的最小上下文（正文/目录链即使没传完整详情也能拿到 <c>book.bookUrl</c>）。</summary>
    public static JsBookState FromBookUrl(string? bookUrl) =>
        string.IsNullOrWhiteSpace(bookUrl) ? new JsBookState() : new JsBookState { BookUrl = bookUrl };

    /// <summary>由详情解析结果构造（宿主 <c>BookSourceReadingSession</c> 在拿到 <see cref="BookInfo"/> 后调用）。</summary>
    public static JsBookState FromBookInfo(
        BookInfo? info, string? bookUrl, string? fallbackName = null, int chapterCount = -1)
    {
        return new JsBookState
        {
            Name = FirstNonEmpty(info?.Name, fallbackName),
            Author = info?.Author,
            BookUrl = FirstNonEmpty(info?.BookUrl, bookUrl),
            TocUrl = info?.TocUrl,
            Intro = info?.Intro,
            Kind = info?.Kind,
            CoverUrl = info?.CoverUrl,
            TotalChapterNum = chapterCount,
        };
    }

    /// <summary>补上"当前章"信息（阅读进度），返回新快照（并行正文时各自持有自己的章号）。</summary>
    public JsBookState WithChapter(int index, string? title, int totalChapterNum = -1) =>
        this with
        {
            DurChapterIndex = index < 0 ? 0 : index,
            DurChapterTitle = title,
            TotalChapterNum = totalChapterNum >= 0 ? totalChapterNum : TotalChapterNum,
        };

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// 一条解析链的 JS 作用域（阶段 4-2 新增）：把"哪个书源、哪本书、哪一章、状态存哪、诊断报哪"
/// 打包成一个对象，交给 <see cref="JsBridge"/> 绑定 <c>source/book/cache/cookie</c>。
///
/// <para><b>生命周期</b>：<see cref="RuleEngine"/> 在每个公开入口（search / bookInfo / toc / content）
/// 的第一步新建一个实例，链结束即弃。实例本身不可变（除存储内容），因此并行正文里
/// 每个线程持有的 scope 互不干扰。</para>
///
/// <para><b>键命名空间</b>：所有键都拼成 <c>前缀:…</c> 形式交给 <see cref="SourceStateStore"/>；
/// 前缀语义见 <see cref="SourceStateStore"/> 的类注释。集中在这里拼键，避免
/// <c>ContextObjects</c> 里到处散落字符串常量。</para>
/// </summary>
public sealed class JsScope
{
    /// <summary>建一个作用域。</summary>
    /// <param name="sourceKey">书源主键（<c>bookSourceUrl</c>）。</param>
    /// <param name="source">书源模型（<c>source.*</c> 的数据来源；可为 null，此时字段取空串）。</param>
    /// <param name="book">书籍上下文（<c>book.*</c>；可为 null）。</param>
    /// <param name="chapterUrl">当前章节 URL（<c>chapter.*</c> 变量的作用域键；搜索/详情链为 null）。</param>
    /// <param name="store">状态仓库（默认进程级内存实例）。</param>
    /// <param name="diagnostics">诊断接收器（默认进程级单例）。</param>
    /// <param name="fetcher">同步网络原语的取数管线（阶段 4-3；null = <c>java.ajax</c> 不可用）。</param>
    /// <param name="cookies">Cookie 仓库（阶段 4-3；null = 进程级内存实例）。</param>
    /// <param name="webView">WebView 宿主（阶段 4-7；null = <c>startBrowser/startBrowserAwait/webView</c> 降级 + 诊断）。</param>
    public JsScope(
        string? sourceKey,
        BookSourceModel? source = null,
        JsBookState? book = null,
        string? chapterUrl = null,
        SourceStateStore? store = null,
        JsDiagnostics? diagnostics = null,
        IBookSourceFetcher? fetcher = null,
        ISourceCookieStore? cookies = null,
        IWebViewHost? webView = null)
    {
        SourceKey = sourceKey ?? string.Empty;
        StorageKey = SourceStateStore.Normalize(sourceKey);
        Source = source;
        Book = book;
        ChapterUrl = string.IsNullOrWhiteSpace(chapterUrl) ? null : chapterUrl;
        Store = store ?? SourceStateStore.InMemory;
        Diagnostics = diagnostics ?? JsDiagnostics.Shared;
        Fetcher = fetcher;
        Cookies = cookies ?? SourceCookieStore.Memory;
        WebView = webView;
    }

    /// <summary>书源主键（原样，用于 <c>source.key</c> 返回值与诊断归组）。</summary>
    public string SourceKey { get; }

    /// <summary>状态存储键（去尾斜杠的归一化形式，<c>https://a.com/</c> 与 <c>https://a.com</c> 视为同一源）。</summary>
    public string StorageKey { get; }

    /// <summary>书源模型（可能为 null）。</summary>
    public BookSourceModel? Source { get; }

    /// <summary>书籍上下文（可能为 null：搜索链没有书）。</summary>
    public JsBookState? Book { get; }

    /// <summary>当前章节 URL（可能为 null：目录链/搜索链没有章节）。</summary>
    public string? ChapterUrl { get; }

    /// <summary>状态仓库。</summary>
    public SourceStateStore Store { get; }

    /// <summary>诊断接收器。</summary>
    public JsDiagnostics Diagnostics { get; }

    /// <summary>
    /// 同步网络原语的取数管线（阶段 4-3）：<c>java.ajax/post/connect/head/ajaxAll</c> 用它。
    /// <para>null = 未接线（纯脚本单测）：原语只记诊断并返回空，不会假装成功。</para>
    /// </summary>
    public IBookSourceFetcher? Fetcher { get; }

    /// <summary>
    /// Cookie 仓库（阶段 4-3）：<c>cookie.*</c>、<c>java.getCookie</c> 与 HTTP 静态请求共用同一份。
    /// </summary>
    public ISourceCookieStore Cookies { get; }

    /// <summary>
    /// WebView 宿主（阶段 4-7）：<c>java.startBrowser/startBrowserAwait/webView</c> 用。
    /// <para>null = 宿主未接线（测试/无 WebView2 运行时）：原语记诊断降级，不假装拿到页面。</para>
    /// </summary>
    public IWebViewHost? WebView { get; }

    // ==================== 键命名空间 ====================

    /// <summary>书源级元数据键（<c>variable</c> / <c>loginInfo</c> / <c>loginHeader</c>）。</summary>
    public string SourceMetaKey(string name) => "sv:" + name;

    /// <summary><c>source.put/get</c> 的键。</summary>
    public string SourcePutKey(string key) => "sp:" + key;

    /// <summary>书籍变量键（<c>book.getVariable</c> / 无章节时的 <c>java.put</c>）。</summary>
    public string BookKey(string key) => "bv:" + (Book?.BookUrl ?? string.Empty) + ":" + key;

    /// <summary>章节变量键（<c>chapter.getVariable</c> / 正文链的 <c>java.put</c>）。</summary>
    public string ChapterKey(string key) => "cv:" + (ChapterUrl ?? string.Empty) + ":" + key;

    /// <summary><c>cache.get/put/delete</c> 的键。</summary>
    public string CacheKey(string key) => "c:" + key;

    /// <summary><c>cache.getFile/putFile</c> 的键（与普通缓存分开，对齐 reader 的 <c>file:</c> 前缀）。</summary>
    public string FileCacheKey(string key) => "file:" + key;

    /// <summary><c>book.setUseReplaceRule</c> 的键（按书，对齐 reader 的 <c>brc:</c> 前缀）。</summary>
    public string ReadConfigKey() => "brc:" + (Book?.BookUrl ?? string.Empty);

    // ==================== 状态读写（已绑定源键） ====================

    /// <summary>读一个键（源键已绑定）。</summary>
    public string? Read(string key) => Store.Get(StorageKey, key);

    /// <summary>写一个键（源键已绑定）。</summary>
    public bool Write(string key, string? value) => Store.Set(StorageKey, key, value);

    /// <summary>删一个键（源键已绑定）。</summary>
    public bool Delete(string key) => Store.Remove(StorageKey, key);
}

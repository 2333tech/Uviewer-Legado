using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Uviewer.Services.Legado.Models;
// 根命名空间为 Uviewer.Services.Legado，类型 BookSource 不能作简单名（CS0118），用别名。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using HtmlAgilityPack;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 2 编排：书籍详情 / 目录 / 正文 / 图片；阶段 3 在正文出口接入全局净化管线。
/// 对标 reader <c>service/book_service.rs</c> 的 <c>get_book_info</c> / <c>get_chapter_list_with_pagination</c> / <c>get_content_with_stage</c>
/// （净化应用点对标 reader 前端 <c>stores/reader.ts</c> 的 <c>processContentForDisplay</c>）。
/// <para>与源项目的并行化差异（结果等价，见各类注释）：</para>
/// <list type="number">
/// <item>目录「多个下一页 URL」由串行 for 改为**有界并行**抓取，再按 URL 顺序合并
/// （结果与串行完全一致——页与页之间无依赖，章节按 URL 顺序拼接）；</item>
/// <item>批量正文用 <see cref="Parallel.ForEachAsync"/> 有界并行（默认 16，引擎池化租借）。</item>
/// </list>
/// 其余语义（去重终止条件、拼接方式、兜底规则、分页闸门）与源项目保持一致。
/// <para>线程约定：<see cref="_engine"/> 只服务串行链路（详情/目录首页/目录链/formatJs）；
/// 并行链路（多页目录/批量正文）各自从按调用划分的引擎池租借——Jint 非线程安全，
/// 且池不跨调用，不会把 A 书源的 java.put/get 状态泄漏给 B 书源。</para>
/// </summary>
public sealed class BookSourceEngine
{
    /// <summary>目录分页安全阀：异常书源（next 永远指向新 URL）的长循环上限。</summary>
    private const int MaxTocPages = 200;

    /// <summary>
    /// 正文分页的"页基"判定：把 <c>/1_2.html</c>、<c>/1-2.html</c> 还原为 <c>/1.html</c>。
    /// 用于区分"同一章的第 2 页"（要跟随）与"下一章"（不跟随）。
    /// </summary>
    private static readonly Regex PageBaseRegex = new(@"[-_]\d+(\.\w+)?$", RegexOptions.Compiled);

    private readonly IBookSourceFetcher _fetcher;

    /// <summary>串行链路复用的规则引擎（Jint 引擎构造昂贵，不每次解析都新建）。</summary>
    private readonly RuleEngine _engine = new();

    /// <summary>
    /// 书源 JS 状态仓库（阶段 4-2）：<c>java.put/get</c>、<c>source/book/chapter/cache/cookie</c> 的落点。
    /// <para>默认内存态（<see cref="SourceStateStore.InMemory"/>）——单测不写用户目录；
    /// 生产由 <c>BookSourceService</c> 按数据目录构造持久实例并注入（<c>legado/sources</c>）。
    /// 并行正文租借的引擎也指向同一个仓库，故"目录链 put、正文链 get"跨引擎可见。</para>
    /// </summary>
    private readonly SourceStateStore _store;

    /// <summary>
    /// 正文净化管线（可选注入，对标 reader 前端在内容展示前应用替换规则）。
    /// null = 不净化（向后兼容：既有调用方无感）。
    /// </summary>
    private readonly ContentPurifier? _purifier;

    /// <summary>
    /// Cookie 仓库（阶段 4-3）：与本引擎所有取数请求、<c>java.ajax</c>、脚本 <c>cookie.*</c> 共用。
    /// <para>默认进程级内存实例；生产由 <see cref="LegadoRuntime"/> 注入（落盘 + 跨链共享）。</para>
    /// </summary>
    private readonly ISourceCookieStore _cookies;

    /// <summary>
    /// WebView 宿主（阶段 4-7）：<c>java.startBrowser/startBrowserAwait/webView</c> 的落点。
    /// <para>默认 null = 未接线（单测/无 WebView2 运行时）：原语记诊断降级，不假装拿到页面。
    /// 由 <see cref="LegadoRuntime.CreateEngine"/> 从运行时透传；**透传到串行引擎**
    /// （属性直通 <see cref="_engine"/>，构造后的赋值也生效）。</para>
    /// </summary>
    public IWebViewHost? WebViewHost
    {
        get => _engine.WebViewHost;
        set => _engine.WebViewHost = value;
    }

    /// <param name="fetcher">取数实现（默认 HTTP）。</param>
    /// <param name="purifier">净化管线（可选；宿主通常持 ReplaceRuleManager，规则变更时把
    /// 新快照赋给 <see cref="ContentPurifier.Rules"/> 即可，引擎无需重建）。</param>
    /// <param name="store">JS 状态仓库（可选；默认内存态，生产传入按数据目录构造的持久实例）。</param>
    /// <param name="cookies">Cookie 仓库（可选；默认进程级内存实例，生产传入运行时的共享实例）。</param>
    public BookSourceEngine(
        IBookSourceFetcher? fetcher = null,
        ContentPurifier? purifier = null,
        SourceStateStore? store = null,
        ISourceCookieStore? cookies = null)
    {
        _fetcher = fetcher ?? new HttpBookSourceFetcher();
        _purifier = purifier;
        _store = store ?? SourceStateStore.InMemory;
        _cookies = cookies ?? SourceCookieStore.Memory;
        _engine.Store = _store;
        _engine.Fetcher = _fetcher;
        _engine.Cookies = _cookies;
    }

    // ==================== 详情 ====================

    /// <summary>
    /// 拉取并解析书籍详情。步骤：
    /// ① 入参校验（空 URL / 无 ruleBookInfo → null）；② 抓取详情页（空响应 → null，不进解析）；
    /// ③ CPU 密集的解析放线程池（对标 reader spawn_blocking，避免阻塞 UI 线程）；
    /// ④ name 三层兜底：规则值 → 书架名 → URL 末段（reader book_service:772-790）。
    /// </summary>
    /// <param name="shelfName">书架已有书名，用于 name 为空时的兜底（reader book_service:772）。</param>
    public async Task<BookInfo?> GetBookInfoAsync(
        BookSourceModel source,
        string bookUrl,
        string? shelfName = null,
        CancellationToken cancellationToken = default,
        JsBookState? book = null)
    {
        if (string.IsNullOrWhiteSpace(bookUrl) || source.RuleBookInfo is null)
        {
            return null;
        }

        var (body, finalUrl) = await FetchSourceAsync(source, bookUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            // reader：空响应直接失败，不进解析
            return null;
        }

        var info = await ParseAsync(
            () => _engine.ParseBookInfo(source, body, finalUrl, bookUrl, book), cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        // name 三层兜底：书架名 → URL 末段（reader book_service:772-790）
        if (string.IsNullOrWhiteSpace(info.Name))
        {
            if (!string.IsNullOrWhiteSpace(shelfName))
            {
                info.Name = shelfName;
            }
            else
            {
                info.Name = NameFromUrl(bookUrl);
            }
        }

        return info;
    }

    // ==================== 目录 ====================

    /// <summary>
    /// 拉取目录（含多页跟随）。
    /// <para>步骤：① 抓首页并解析（章节 + nextUrls）；② 首页章节 URL 进 seen 去重集合；
    /// ③ 剔除与目录页自身相同的 next（自环防护）；④ 分分支跟随：</para>
    /// <list type="bullet">
    /// <item>多个 next URL（下拉卷页）→ 有界并行抓取（TocPageConcurrency=4）后**按 URL 顺序**合并
    /// （reader 是串行 for；页间无依赖故并行不改变结果）；</item>
    /// <item>单个 next URL → 链式跟随（while 循环，遇自环/无新链/MaxTocPages 停止）；</item>
    /// </list>
    /// ⑤ 合并后连续重排 Index；⑥ formatJs 统一应用（reader 只在首页应用，这里对全部章节生效）。
    /// <para>终止条件与 reader 一致：已访问页 URL、已见章节 URL；MaxTocPages 是额外安全阀。</para>
    /// </summary>
    public async Task<List<TocItem>> GetTocAsync(
        BookSourceModel source,
        string tocUrl,
        CancellationToken cancellationToken = default,
        JsBookState? book = null)
    {
        if (string.IsNullOrWhiteSpace(tocUrl))
        {
            return new List<TocItem>();
        }

        var (body, finalUrl) = await FetchSourceAsync(source, tocUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return new List<TocItem>();
        }

        var (firstPage, nextUrls) = await ParseAsync(
            () => _engine.ParseTocPage(source, body, finalUrl, book), cancellationToken).ConfigureAwait(false);

        var chapters = new List<TocItem>(firstPage);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in firstPage)
        {
            seen.Add(c.ChapterUrl ?? string.Empty);
        }

        // reader：剔除与目录页自身相同的项
        var visited = new HashSet<string>(StringComparer.Ordinal) { finalUrl, tocUrl };
        var pending = nextUrls
            .Where(u => u.Length > 0 && !visited.Contains(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (pending.Count > 1)
        {
            // 多个下一页 URL（典型：下拉卷页）——reader 是串行 for，这里有界并行后按 URL 顺序合并
            var pages = await FetchTocPagesAsync(source, pending, book, cancellationToken).ConfigureAwait(false);
            foreach (var (page, _, pageUrl) in pages)
            {
                if (page is null)
                {
                    continue;
                }

                Append(chapters, page, seen);
                if (pageUrl.Length > 0)
                {
                    visited.Add(pageUrl);
                }
            }
        }
        else if (pending.Count == 1)
        {
            // 单链接：链式跟随（reader：while 循环，遇自环/无新链停止）
            var current = pending[0];
            for (var guard = 0; guard < MaxTocPages && !visited.Contains(current); guard++)
            {
                visited.Add(current);
                cancellationToken.ThrowIfCancellationRequested();

                var (pageBody, pageUrl) = await FetchSourceAsync(source, current, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(pageBody))
                {
                    break;
                }

                // 链式跟随是串行的：复用实例引擎（与首页、formatJs 同一份 JS 作用域，对标 reader 每源共享）
                var (pageChapters, pageNext) = await ParseAsync(
                    () => _engine.ParseTocPage(source, pageBody, pageUrl, book), cancellationToken).ConfigureAwait(false);
                Append(chapters, pageChapters, seen);

                var next = pageNext.FirstOrDefault(u => u.Length > 0 && !visited.Contains(u));
                if (next is null)
                {
                    break;
                }

                current = next;
            }
        }

        // 合并后连续重排全局序号（阅读器目录定位用）
        for (var i = 0; i < chapters.Count; i++)
        {
            chapters[i].Index = i;
        }

        // formatJs 在所有页合并后统一应用（reader 只在首页应用，这里对全部章节生效）
        ApplyFormatJs(source, chapters, finalUrl, book, cancellationToken);
        return chapters;
    }

    // ==================== 正文 ====================

    /// <summary>
    /// 拉取单章正文（含分页跟随）。拼接用 '\n'、跟随闸门用 reader 的 should_follow_content_page。
    /// <para>循环：visited 防自环 → 抓取 → 解析（正文/标题/下一页一次出）→ 追加正文 →
    /// 闸门判定是否跟随（"下一页"实为下一章时拦住）。</para>
    ///
    /// <para><b>本方法不做净化</b>：只负责"抓 + 解析 + 剥标题抽图"，
    /// 返回的是<b>原始正文</b>。净化统一由宿主在<b>送去渲染之前</b>执行
    /// （见 <c>BookSourceReadingSession.ConvertToPlainText</c>），这样才能：
    /// <list type="bullet">
    /// <item>让"替换净化总开关"真正可逆（缓存里是原文，关掉即刻还原，无需重抓网络）；</item>
    /// <item>把净化时机与"渲染"对齐，而不是与"抓取"耦合。</item>
    /// </list></para>
    /// </summary>
    public Task<ChapterContent?> GetContentAsync(
        BookSourceModel source,
        string chapterUrl,
        string? bookUrl = null,
        CancellationToken cancellationToken = default,
        JsBookState? book = null) =>
        // 与批量链路共用同一实现（分页跟随/拼接/剥标题抽图的逻辑只此一份）；
        // 差异仅两点：用实例引擎（串行链路复用 Jint 引擎）、标题无目录章名可兜底。
        // book：书籍上下文（book.name/author/durChapterIndex…），缺省时用 bookUrl 兜底成最小上下文。
        LoadContentCoreAsync(
            _engine,
            source,
            chapterUrl,
            titleFallback: null,
            book ?? JsBookState.FromBookUrl(bookUrl),
            cancellationToken);

    /// <summary>
    /// 批量并行拉取正文（对标 reader 批量缓存章节的并发窗口，默认 16）。
    /// <para>引擎池化：每章从池中租借规则引擎（Jint 引擎构造昂贵，非线程安全，不能跨章共享同一实例）；
    /// 池按调用划分（ConcurrentBag，容量 ≤ 并发度），不跨书源泄漏 java.put/get 状态。</para>
    /// <para>结果与串行一致：按输入下标写定长数组（并行写互不冲突），完成后按序过滤 null 输出。
    /// progress 按完成数原子递增汇报（线程安全）。</para>
    /// </summary>
    public async Task<List<ChapterContent>> GetContentsAsync(
        BookSourceModel source,
        IReadOnlyList<TocItem> chapters,
        string? bookUrl = null,
        int? maxConcurrency = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        JsBookState? book = null)
    {
        if (chapters.Count == 0)
        {
            return new List<ChapterContent>();
        }

        var results = new ChapterContent?[chapters.Count];
        // 并发度同时是引擎池容量上限：并行度越高需要的独立 Jint 引擎越多
        var limit = Math.Clamp(maxConcurrency ?? BookSourceLimits.DefaultConcurrency, 1, BookSourceLimits.MaxConcurrency);
        var done = 0;
        // 书籍上下文按章派生：每章带上自己的"当前章"，并行线程各自持有（互不干扰）
        var bookContext = book ?? JsBookState.FromBookUrl(bookUrl);

        // 并发度即池容量上限：租借为空才新建，用完归还（池本身有界，随调用结束被回收）
        var pool = new ConcurrentBag<RuleEngine>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, chapters.Count),
            new ParallelOptions { MaxDegreeOfParallelism = limit, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var item = chapters[i];
                var engine = RentEngine(pool);
                try
                {
                    var content = await LoadContentCoreAsync(
                        engine, source, item.ChapterUrl, item.ChapterName, ChapterBook(bookContext, item), ct)
                        .ConfigureAwait(false);
                    results[i] = content;
                }
                finally
                {
                    pool.Add(engine);
                }

                if (progress is not null)
                {
                    progress.Report(Interlocked.Increment(ref done));
                }
            }).ConfigureAwait(false);

        var list = new List<ChapterContent>(results.Length);
        foreach (var r in results)
        {
            if (r is not null)
            {
                list.Add(r);
            }
        }

        return list;
    }

    /// <summary>
    /// 批量并行拉取正文，<b>按下标对齐</b>（失败章留 null，不压缩数组）。
    /// <para>与 <see cref="GetContentsAsync"/> 的区别：后者会过滤掉 null，导致返回列表与
    /// 输入目录<b>失去下标对应</b>（第 3 章失败会让后面的章名错位一格）。整本下载需要
    /// "第 i 章正文配第 i 章标题"，故提供这个保留空洞的版本。</para>
    /// <para>引擎池化策略与 <see cref="GetContentsAsync"/> 完全一致。</para>
    /// </summary>
    public async Task<ChapterContent?[]> GetContentsIndexedAsync(
        BookSourceModel source,
        IReadOnlyList<TocItem> chapters,
        string? bookUrl = null,
        int? maxConcurrency = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        JsBookState? book = null)
    {
        if (chapters.Count == 0)
        {
            return Array.Empty<ChapterContent?>();
        }

        var results = new ChapterContent?[chapters.Count];
        var limit = Math.Clamp(maxConcurrency ?? BookSourceLimits.DefaultConcurrency, 1, BookSourceLimits.MaxConcurrency);
        var done = 0;
        var bookContext = book ?? JsBookState.FromBookUrl(bookUrl);

        var pool = new ConcurrentBag<RuleEngine>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, chapters.Count),
            new ParallelOptions { MaxDegreeOfParallelism = limit, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var item = chapters[i];
                var engine = RentEngine(pool);
                try
                {
                    results[i] = await LoadContentCoreAsync(
                        engine, source, item.ChapterUrl, item.ChapterName, ChapterBook(bookContext, item), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单章失败留 null：整本下载不该因一章 404 而中断（与批量搜索同策略）
                    results[i] = null;
                }
                finally
                {
                    pool.Add(engine);
                }

                if (progress is not null)
                {
                    progress.Report(Interlocked.Increment(ref done));
                }
            }).ConfigureAwait(false);

        return results;
    }

    // ==================== 图片 ====================

    /// <summary>
    /// 下载封面/图片字节（自动补 Referer 防盗链，对标 reader get_cover）。步骤：
    /// ① data: URL → 本地解码（不走网络）；② 推导 Referer（图片 URL 的 origin）；
    /// ③ 图片 URL 也过 UrlAnalyzer 编译（可能带取图选项 ,{...}）+ 绝对化；
    /// ④ 交 fetcher 取字节（图片 Accept + Referer + 每源 Cookie）。
    /// </summary>
    public async Task<byte[]?> GetImageAsync(
        BookSourceModel source, string imageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUrl(imageUrl);
        }

        var referer = UrlUtil.OriginOf(imageUrl);
        var spec = new UrlAnalyzer().BuildSpec(imageUrl, string.Empty, 1, source.BookSourceUrl);
        var url = UrlUtil.AbsoluteUrl(source.BookSourceUrl, spec.Url);
        if (url.Length == 0)
        {
            return null;
        }

        return await _fetcher
            .FetchBytesAsync(url, source.BookSourceUrl, referer, cancellationToken).ConfigureAwait(false);
    }

    // ---- 内部 ----

    /// <summary>
    /// 单章正文的<b>唯一</b>实现（串行 <see cref="GetContentAsync"/> 与并行批量共用）。
    /// <para>流程（顺序不可调换）：</para>
    /// <list type="number">
    /// <item>分页跟随状态机：current 逐页前进，builder 累积正文，visited 防自环；
    /// 注意 baseUrl 取<b>最后一页</b>的解析基地址——相对图片地址要按最后一页解析才算得对；</item>
    /// <item>每页"抓取 → 一次解析同时得到正文/标题/下一页"（reader 是三次独立调用，这里共用一份片段上下文）；</item>
    /// <item>页与页之间补一个 <c>\n</c>，否则两页会粘成一行；</item>
    /// <item>标题只取第一页（后续页标题通常是"第N章 xxx(2/3)"这类噪音）；</item>
    /// <item>跟随闸门 <see cref="ShouldFollowContentPage"/>（"下一页"实为下一章时停）；</item>
    /// <item><b>分页全部拼完才剥标题/抽图</b>：只剥文档开头那一处，逐页剥会误删第 2 页开头的正文。</item>
    /// </list>
    /// <para>不在此处净化：抓取阶段返回原文，净化由宿主在渲染前执行（总开关才可逆）。</para>
    /// </summary>
    /// <param name="engine">规则引擎（串行链路传实例引擎，并行链路传池化租借的实例）。</param>
    /// <param name="chapterUrl">章节首页 URL（null/空白 → 直接返回 null）。</param>
    /// <param name="titleFallback">页面标题缺失时的兜底（批量场景用目录章名）。</param>
    /// <param name="book">书籍上下文（<c>book.*</c>；可为 null）。</param>
    private async Task<ChapterContent?> LoadContentCoreAsync(
        RuleEngine engine,
        BookSourceModel source,
        string? chapterUrl,
        string? titleFallback,
        JsBookState? book,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chapterUrl))
        {
            return null;
        }

        var builder = new StringBuilder();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = chapterUrl;
        string? title = null;
        var baseUrl = chapterUrl;

        while (visited.Add(current))
        {
            ct.ThrowIfCancellationRequested();

            // ① 抓取（URL 会再过一次 analyze_request，可能带 POST/编码等选项；
            //    finalUrl 是重定向后的真实地址，用于相对地址解析）
            var (body, finalUrl) = await FetchSourceAsync(source, current, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                // 空响应 = 这一页拿不到内容：直接收尾（已抓到的页仍然返回，不整章作废）
                break;
            }

            // ② 一次解析同时得到正文 / 标题 / 下一页
            //    章节上下文固定用"章节首页 URL"（chapterUrl）：分页跟随的第 2/3 页共享同一章的
            //    chapter 变量作用域，而 baseUrl 仍按当前页解析相对地址（两者的差异是有意的）。
            var parseBase = current.Length > 0 ? current : finalUrl;
            var (text, pageTitle, next) = await ParseAsync(
                () => engine.ParseContentPage(source, body, parseBase, book, chapterUrl), ct).ConfigureAwait(false);

            // ③ 追加本页正文
            if (text.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(text);
            }

            // ④ 标题取第一页的
            title ??= pageTitle;
            baseUrl = parseBase;

            // ⑤ 跟随闸门：next 为空，或"下一页"实际是下一章 → 停
            if (next is null || !ShouldFollowContentPage(chapterUrl, current, next))
            {
                break;
            }

            current = next;
        }

        // ⑥ 分页全部拼完才剥标题/抽图（一次解析同时完成两件事）
        var (html, images) = StripTitleAndExtractImages(builder.ToString(), baseUrl);

        // ⑦ 抓取阶段不净化：交给宿主在渲染前处理
        return new ChapterContent
        {
            Title = title is { Length: > 0 } ? title : (titleFallback ?? string.Empty),
            Content = html,
            ImageUrls = images,
        };
    }

    /// <summary>
    /// <b>供宿主在"读取 → 渲染"之间</b>调用的净化入口。
    ///
    /// <para><b>抓取链路一律不净化</b>（<see cref="GetContentAsync"/> /
    /// <see cref="GetContentsIndexedAsync"/> 只返回原文）；净化统一在送去渲染前
    /// 由宿主执行，以便被一级菜单的"替换净化（总开关）"控制——
    /// <b>源头是原文，总开关才可逆</b>（关掉即刻还原，无需重抓网络）。</para>
    /// </summary>
    public string PurifyForDisplay(string html, string? bookUrl = null, string? sourceUrl = null)
    {
        // 快路径：无 purifier / 无可用规则 / 空正文 → 原样返回
        //（总开关关闭时 Purifier.Rules 为 null，HasRules 为 false，即走这里，零开销）
        if (_purifier is null || !_purifier.HasRules || string.IsNullOrEmpty(html))
        {
            return html;
        }

        var ctx = new PurifyContext(
            BookName: null,
            BookUrl: bookUrl,
            Origin: sourceUrl,
            UseReplaceRule: null);

        return _purifier.Apply(html, ctx);
    }

    /// <summary>
    /// 抓取书源页面（详情/目录/正文共用）。要点：
    /// ① URL 同样要过 analyze_request 编译（可能带 ,{json} 选项，如 POST 目录页）；
    /// ② 请求地址用 absolute_url（去 query+fragment）；③ 最终 URL 回退请求 URL
    /// （重定向后的地址用于相对地址解析，fetcher 必须返回它）；
    /// ④ 带上书源限速（<see cref="RequestSpec.ConcurrentRate"/>），由 <see cref="RateLimitedFetcher"/> 消费。
    /// </summary>
    private async Task<(string Body, string FinalUrl)> FetchSourceAsync(
        BookSourceModel source, string url, CancellationToken cancellationToken)
    {
        // reader：详情/目录/正文的 URL 同样要经过 analyze_request（可能带 ,{json} 选项）
        var spec = new UrlAnalyzer().BuildSpec(url, string.Empty, 1, source.BookSourceUrl);
        var requestUrl = UrlUtil.AbsoluteUrl(source.BookSourceUrl, spec.Url);
        if (requestUrl.Length == 0)
        {
            return (string.Empty, url);
        }

        // 阶段 4-3：限速随请求走（同一书源的搜索/目录/正文/java.ajax 共用一把闸门）
        spec = spec with { ConcurrentRate = source.ConcurrentRate };

        var (body, finalUrl) = await _fetcher
            .FetchAsync(requestUrl, spec, source.BookSourceUrl, cancellationToken).ConfigureAwait(false);
        return (body, finalUrl.Length > 0 ? finalUrl : requestUrl);
    }

    /// <summary>
    /// 有界并行抓取多个目录分页（默认并发 4，见 BookSourceLimits.TocPageConcurrency）。
    /// <para>结果与串行一致：按下标写定长数组，调用方按 URL 顺序（= 数组顺序）合并章节；
    /// 每页解析用池化引擎（同 GetContentsAsync 的池化理由）。</para>
    /// </summary>
    private async Task<(List<TocItem>? Chapters, List<string> Next, string Url)[]> FetchTocPagesAsync(
        BookSourceModel source, List<string> urls, JsBookState? book, CancellationToken cancellationToken)
    {
        var results = new (List<TocItem>?, List<string>, string)[urls.Count];
        var limit = Math.Min(BookSourceLimits.TocPageConcurrency, urls.Count);

        // 与批量正文同理：按调用划分的引擎池，租借/归还，避免每页新建 Jint 引擎
        var pool = new ConcurrentBag<RuleEngine>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, urls.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, limit), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var (body, finalUrl) = await FetchSourceAsync(source, urls[i], ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                {
                    return;
                }

                var engine = RentEngine(pool);
                try
                {
                    var (chapters, next) = await ParseAsync(
                        () => engine.ParseTocPage(source, body, finalUrl, book), ct).ConfigureAwait(false);
                    results[i] = (chapters, next, finalUrl);
                }
                finally
                {
                    pool.Add(engine);
                }
            }).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// formatJs 统一应用。它改章节名，执行它的 Jint 引擎必须串行——
    /// 故放在所有并发抓取之后，用实例引擎（_engine）统一做。
    /// </summary>
    private void ApplyFormatJs(
        BookSourceModel source, List<TocItem> chapters, string baseUrl, JsBookState? book, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.RuleToc?.FormatJs) || chapters.Count == 0)
        {
            return;
        }

        // formatJs 会改章节名：与其执行的 Jint 引擎必须串行，放在并发抓取之后统一做
        _engine.ApplyFormatJs(source, chapters, baseUrl, book);
    }

    /// <summary>
    /// 从池里取一个规则引擎（空池才新建），并把状态仓库接上。
    /// <para>池化引擎同样要指向 <see cref="_store"/>：否则并行正文里的
    /// <c>java.get('bid')</c> 读不到目录链写下的值。</para>
    /// </summary>
    private RuleEngine RentEngine(ConcurrentBag<RuleEngine> pool)
    {
        var engine = pool.TryTake(out var taken) ? taken : new RuleEngine();
        engine.Store = _store;
        engine.Fetcher = _fetcher;
        engine.Cookies = _cookies;
        engine.WebViewHost = WebViewHost;
        return engine;
    }

    /// <summary>给一本书的某一章派生书籍上下文（补 <c>durChapterIndex</c>/<c>durChapterTitle</c>）。</summary>
    private static JsBookState ChapterBook(JsBookState book, TocItem chapter) =>
        book.WithChapter(chapter.Index, chapter.ChapterName);

    /// <summary>把解析（CPU 密集）放到线程池，避免阻塞调用方线程（对标 reader 的 spawn_blocking）。</summary>
    private static Task<T> ParseAsync<T>(Func<T> parse, CancellationToken cancellationToken) =>
        Task.Run(parse, cancellationToken);

    /// <summary>把一页章节按 seen（绝对 URL 去重）合并进总列表，保持页内顺序。</summary>
    private static void Append(List<TocItem> target, List<TocItem> page, HashSet<string> seen)
    {
        foreach (var c in page)
        {
            if (seen.Add(c.ChapterUrl ?? string.Empty))
            {
                target.Add(c);
            }
        }
    }

    /// <summary>从 URL 末段推书名（name 兜底第三层，reader book_service:790）。</summary>
    private static string NameFromUrl(string url)
    {
        // 先去 query/fragment 再去尾斜杠：/book/123/?from=x → "123"
        var cut = url.AsSpan();
        var q = cut.IndexOfAny('?', '#');
        if (q >= 0)
        {
            cut = cut[..q];
        }

        cut = cut.TrimEnd('/');
        var slash = cut.LastIndexOf('/');
        return (slash >= 0 ? cut[(slash + 1)..] : cut).ToString();
    }

    /// <summary>
    /// 正文下一页跟随闸门（对标 reader should_follow_content_page，防止"下一页"实为下一章）。
    /// <para>判定（全部满足才跟随）：① next 非空且不等于当前页/章节首页；② 三者同源
    /// （scheme://host 一致，防跨站"下一页"）；③ next 的 path 等于章节页或当前页的 path，
    /// 或 next 的"页基"（去掉 _N/-N 页码后缀）等于二者之一——即 /1_2.html 是 /1.html 的续页，
    /// 而 /2.html 是下一章。</para>
    /// </summary>
    internal static bool ShouldFollowContentPage(string chapterUrl, string currentUrl, string nextUrl)
    {
        var next = UrlUtil.StripFragment(nextUrl);
        if (next.Length == 0 || next == currentUrl || next == chapterUrl)
        {
            return false;
        }

        if (!TryOrigin(chapterUrl, out var o1) || !TryOrigin(currentUrl, out var o2) || !TryOrigin(next, out var o3))
        {
            return false;
        }

        if (o1 != o3 || o2 != o3)
        {
            return false;
        }

        var nextExact = PathOf(next);
        var nextBase = PageBase(nextExact);
        var chapterExact = PathOf(chapterUrl);
        var currentExact = PathOf(currentUrl);

        return nextExact == chapterExact
            || nextExact == currentExact
            || nextBase == chapterExact
            || nextBase == currentExact;
    }

    /// <summary>取 scheme://host（不可解析 → false，闸门按"不跟随"处理）。</summary>
    private static bool TryOrigin(string url, out string origin)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            origin = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        origin = string.Empty;
        return false;
    }

    /// <summary>取 URL 的 path（不可解析 → 原串）。</summary>
    private static string PathOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;

    /// <summary>页基还原：/1_2.html → /1.html（配合 ShouldFollowContentPage 判续页）。</summary>
    private static string PageBase(string path) => PageBaseRegex.Replace(path, "$1");

    /// <summary>
    /// 剥标题 + 抽图片一次完成（对标 reader strip_leading_content_title + Uviewer 图片需求）。
    /// <para>合并意义：两者本需要对同一份正文 HTML 各解析一遍；这里一次解析、
    /// 图片在剥标题后的同一文档树上收集，结果与分开做完全一致（有逐字节回归测试）。</para>
    /// <para>剥标题规则：文档第一个元素是标题元素（h1~h6 / class 含 title 的 p）→ 移除；
    /// 或第一个元素是 div/section/article 且其第一个子元素是标题 → 移除该子元素。</para>
    /// </summary>
    internal static (string Html, List<string> ImageUrls) StripTitleAndExtractImages(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return (html, new List<string>());
        }

        var doc = Selectors.Load(html);
        var first = FirstElement(doc.DocumentNode);
        var stripped = false;

        if (first is not null && IsTitleElement(first))
        {
            first.Remove();
            stripped = true;
        }
        else if (first is not null && first.Name is "div" or "section" or "article")
        {
            var inner = FirstElement(first);
            if (inner is not null && IsTitleElement(inner))
            {
                inner.Remove();
                stripped = true;
            }
        }

        var resultHtml = stripped ? doc.DocumentNode.InnerHtml : html;
        return (resultHtml, RuleEngine.CollectImages(doc, baseUrl, new List<string>()));
    }

    /// <summary>
    /// 剥掉正文开头的重复标题（对标 reader strip_leading_content_title）。
    /// 保留此独立入口供只需要剥标题的调用方；主链路用合并版
    /// <see cref="StripTitleAndExtractImages"/>（一次解析同时抽图）。
    /// </summary>
    internal static string StripLeadingContentTitle(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        var doc = Selectors.Load(html);
        var first = FirstElement(doc.DocumentNode);
        if (first is null)
        {
            return html;
        }

        if (IsTitleElement(first))
        {
            first.Remove();
            return doc.DocumentNode.InnerHtml;
        }

        if (first.Name is "div" or "section" or "article")
        {
            var inner = FirstElement(first);
            if (inner is not null && IsTitleElement(inner))
            {
                inner.Remove();
                return doc.DocumentNode.InnerHtml;
            }
        }

        return html;
    }

    /// <summary>取节点的第一个**元素**子节点（跳过文本/注释节点）。</summary>
    private static HtmlNode? FirstElement(HtmlNode node) =>
        node.ChildNodes.FirstOrDefault(n => n.NodeType == HtmlNodeType.Element);

    /// <summary>
    /// 是否标题元素（对标 reader 的判定）：h1~h6；或 p 且 class 含
    /// chapter-title / chapter_title / title（大小写不敏感）。
    /// </summary>
    private static bool IsTitleElement(HtmlNode node)
    {
        if (node.Name.Length == 2 && node.Name[0] == 'h' && node.Name[1] is >= '1' and <= '6')
        {
            return true;
        }

        if (node.Name == "p")
        {
            var cls = node.GetAttributeValue("class", string.Empty);
            return cls.Contains("chapter-title", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("chapter_title", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("title", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// 解码 data: URL（内联图片，不走网络）。
    /// <c>;base64</c> 形态走 Base64，其余按 Hex 解码；任何解析失败 → null（图片失败不影响阅读）。
    /// <para>注意：<c>IndexOf(',')</c> 为 -1（如 <c>data:image/png</c> 缺 payload）时
    /// 旧实现会拿 <c>AsSpan(0, -1)</c> 直接抛 <see cref="ArgumentOutOfRangeException"/>——
    /// 那不是"图片失败"该有的表现，必须在这里判掉。</para>
    /// </summary>
    private static byte[]? DecodeDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var isBase64 = url.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var payload = url.AsSpan(comma + 1);
        try
        {
            // span 重载：省掉 payload 的中间字符串（封面常见几十 KB 的 base64）
            return isBase64
                ? Convert.FromBase64String(url.Substring(comma + 1))
                : Convert.FromHexString(payload);
        }
        catch
        {
            return null;
        }
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Uviewer.Services.Legado.Models;
using HtmlAgilityPack;
// 根命名空间为 Uviewer.Services.Legado，全局隐式存在命名空间 "BookSource"，
// 不能再把类型 BookSource 当简单名使用（CS0118），改用别名。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源规则引擎（阶段 1 搜索 + 阶段 2 详情/目录/正文，同构移植 reader rule_engine.rs）。
/// <para>统一求值管线（所有字段规则都走这条，对标 reader extract_content）：</para>
/// <list type="number">
/// <item>模式识别（RuleAnalyzer.DetectMode：显式前缀 > 形态前缀 > 内容嗅探 > 默认 CSS）；</item>
/// <item>组合符拆分（SplitTopLevel：在括号/引号/&lt;js&gt; 外按 &amp;&amp; / || / %% 切段，
/// 每段独立判定模式——组合段可以是不同模式，如 @href||$.url）；</item>
/// <item>模式派发（CSS / XPath / JSONPath / Regex / Js）；</item>
/// <item>## 行内替换（对组合后的整体生效，组合符只在 ## 之前有效）；</item>
/// <item>URL 绝对化（仅 BookUrl / CoverUrl 等 URL 字段，走 resolve_url 保留 query）。</item>
/// </list>
/// <para>性能骨架（优化后，务必保持）：整份 body 只建一个 <see cref="FragmentContext"/>
/// （HTML/JSON 解析一次，组合符各段、JS 分支后续规则、nextTocUrl 全部复用）；
/// 列表片段用活节点形态（见 <see cref="FragmentContext"/> 注释）。</para>
/// <para>线程约定：一个实例同时只被一个线程使用（JsBridge/Jint 非线程安全）。
/// 并行路径（批量正文/多页目录）由 BookSourceEngine 为每个并发单元新建或池化租借实例。</para>
/// </summary>
public sealed class RuleEngine
{
    private readonly JsBridge _js = new();

    /// <summary>
    /// 当前解析链的书源（每个公开入口首步设置）。唯一用途：让 <see cref="RunJs"/>
    /// 把 <c>source.JsLib</c> 透传给 <see cref="JsBridge"/>（jsLib 注入）。
    /// 线程约定内使用：一个实例同时只被一个线程驱动，不存在并发改写。
    /// </summary>
    private BookSourceModel? _jsSource;

    /// <summary>
    /// 诊断接收器（默认进程级单例 <see cref="JsDiagnostics.Shared"/>）。
    /// <para>阶段 4-1 起，脚本里"缺哪个原语 / 调了哪个空桩 / 为什么抛错"全部落到这里；
    /// 编排层/界面只读它即可解释"这个源为什么读不出正文"，不必再靠猜。</para>
    /// </summary>
    public JsDiagnostics Diagnostics
    {
        get => _js.Diagnostics;
        set => _js.Diagnostics = value ?? JsDiagnostics.Shared;
    }

    /// <summary>
    /// 脚本状态仓库（<c>java.put/get</c>、<c>source/book/chapter/cache/cookie</c> 的落点，阶段 4-2）。
    /// <para>默认 <see cref="SourceStateStore.InMemory"/>：<b>刻意不落盘</b>，避免引擎单测污染
    /// 用户的 <c>%LocalAppData%</c>；生产路径由 <c>BookSourceService</c> 按数据目录构造持久实例并注入
    /// （落 <c>legado/sources</c>）。</para>
    /// <para>必须在第一次解析前设置：链入口会用它构造 <see cref="JsScope"/>。</para>
    /// </summary>
    public SourceStateStore Store { get; set; } = SourceStateStore.InMemory;

    /// <summary>
    /// 同步网络原语的取数管线（阶段 4-3）：<c>java.ajax/post/connect/head/ajaxAll</c> 用。
    /// <para>默认 null = 未接线（纯脚本单测）：原语记诊断并返回空，不会假装成功。
    /// <see cref="BookSourceEngine"/> / <see cref="SearchService"/> 在构造时注入同一条管线
    /// （生产是 <see cref="RateLimitedFetcher"/> 套 HTTP），从而与静态请求共享 Cookie（D3）。</para>
    /// </summary>
    public IBookSourceFetcher? Fetcher { get; set; }

    /// <summary>
    /// Cookie 仓库（阶段 4-3）：<c>cookie.*</c>/<c>java.getCookie</c> 与取数管线共用。
    /// <para>默认进程级内存实例（单测隔离）；生产由宿主注入 <see cref="SourceCookieStore"/>。</para>
    /// </summary>
    public ISourceCookieStore Cookies { get; set; } = SourceCookieStore.Memory;

    /// <summary>
    /// WebView 宿主（阶段 4-7）：<c>java.startBrowser/startBrowserAwait/webView</c> 的落点。
    /// <para>默认 null = 未接线（纯脚本单测/无 WebView2 运行时）：原语记诊断降级，不假装成功。
    /// <see cref="BookSourceEngine"/> / <see cref="SearchService"/> 从 <see cref="LegadoRuntime.WebViewHost"/> 透传。</para>
    /// </summary>
    public IWebViewHost? WebViewHost { get; set; }

    /// <summary>
    /// 记录"当前在跑哪个书源的哪条链"（诊断归组用），并装配本链的 JS 作用域。
    /// <para>链名固定为 search / bookInfo / toc / content：粒度到链而不是到字段，
    /// 因为 <see cref="RunJs"/> 是唯一出口，逐字段传参会把签名污染到十几处。</para>
    /// <para><b>作用域每链新建一个</b>：<see cref="JsScope"/> 里带的是"这一链的书/章"快照，
    /// 并行正文时每个线程持有自己的那份（互不干扰）；状态本身由
    /// <see cref="SourceStateStore"/> 按书源共享。</para>
    /// </summary>
    private void BeginChain(
        BookSourceModel source, string field, JsBookState? book = null, string? chapterUrl = null)
    {
        _jsSource = source;
        _js.SourceKey = source.BookSourceUrl;
        _js.Field = field;
        _js.Scope = new JsScope(
            source.BookSourceUrl,
            source,
            book,
            chapterUrl,
            Store,
            _js.Diagnostics,
            Fetcher,
            Cookies,
            WebViewHost);
    }

    // 模板正则常量：每次展开 {{...}} 都会用到，缓存并预编译。
    // [\s\S] 而非 .：规则文本可能跨行（书源 JSON 里写 "\n" 的模板），
    // . 不含 \n 会让多行 {{}} 静默不展开（UrlAnalyzer 的同类正则同样用 [\w\W]）。
    private static readonly Regex TemplateRegex = new(@"\{\{([\s\S]*?)\}\}", RegexOptions.Compiled);

    /// <summary><c>@get:{key}</c>（规则变量取值；legado evalPattern 之一）。</summary>
    private static readonly Regex GetRuleVarRegex = new(@"@get:\{([^{}]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>{$.路径}</c> 字面量内嵌取值（legado <c>innerRule("{$.")</c>）。</summary>
    private static readonly Regex InnerRuleRegex = new(@"\{(\$[^{}]*)\}", RegexOptions.Compiled);

    /// <summary>裸字段名（JSON 对象条目上的直接键访问；不含 CSS/XPath 语法字符）。</summary>
    private static readonly Regex BareKeyRegex = new(@"^[A-Za-z_][A-Za-z0-9_\-]*$", RegexOptions.Compiled);

    /// <summary>
    /// 搜索入口（对标 reader search_books）。步骤：
    /// ① 无 ruleSearch → 空列表；② 列表规则归一化（- 前缀 = 倒序）；
    /// ③ 走列表解析；④ 按需倒序（reader：reverse 在拼接后生效）。
    /// <para>搜索链没有书籍上下文（<c>book</c> 对象字段为空），与 legado/reader 一致。</para>
    /// </summary>
    public List<SearchResult> SearchBooks(BookSourceModel source, string body, string baseUrl)
    {
        // 记录当前书源与链名：RunJs 需要它的 jsLib（jsLib 注入）+ 诊断归组 + 全局对象作用域
        BeginChain(source, "search");
        if (source.RuleSearch is null)
        {
            return new List<SearchResult>();
        }

        var rule = source.RuleSearch;
        var (listRule, reverse) = RuleAnalyzer.NormalizeListRule(rule.BookList ?? string.Empty);
        var results = ParseSearchList(source, body, baseUrl, rule, listRule);
        if (reverse)
        {
            results.Reverse();
        }

        return results;
    }

    /// <summary>列表解析入口（文本入参）：为整份 body 建一份片段上下文后进入 ctx 重载。</summary>
    private List<SearchResult> ParseSearchList(BookSourceModel source, string body, string baseUrl, SearchRule rule, string listRule)
        => ParseSearchList(source, new FragmentContext(body), baseUrl, rule, listRule);

    /// <summary>
    /// 列表解析核心（对标 reader parse_search_list）。步骤：
    /// ① 整条列表规则是 JS → 先跑脚本，用输出当新 body 递归（SearchBooksJs）；
    /// ② 有顶层组合符 → 按语义分发：
    ///    || = 逐段取第一个非空结果；&amp;&amp; = 各段结果顺序拼接；%% = 各段结果按索引交错；
    /// ③ 无组合符 → 按模式派发到 Json/XPath(Css)/Regex/Js 四个列表解析器。
    /// <para>注意 ② 的递归也传同一个 ctx：组合符各段共享同一份 body 解析
    ///（每段各建上下文会把大 JSON body 重新解析 N 遍）。</para>
    /// </summary>
    private List<SearchResult> ParseSearchList(BookSourceModel source, FragmentContext ctx, string baseUrl, SearchRule rule, string listRule)
    {
        // 开头是 JS 段：先跑脚本拿到后续段的内容（如 graphql 响应），再按剩余规则解析。
        if (RuleAnalyzer.IsJsRule(listRule))
        {
            return SearchBooksJs(source, ctx, baseUrl, rule, listRule);
        }

        var combo = RuleAnalyzer.SplitTopLevel(listRule, new[] { "&&", "||", "%%" });
        if (combo.Delimiter is not null)
        {
            switch (combo.Delimiter)
            {
                case "||":
                    // 首个非空段获胜（典型：主选择器失败时换备用选择器）
                    foreach (var part in combo.Parts)
                    {
                        if (string.IsNullOrWhiteSpace(part))
                        {
                            continue;
                        }

                        var books = ParseSearchList(source, ctx, baseUrl, rule, part);
                        if (books.Count > 0)
                        {
                            return books;
                        }
                    }

                    return new List<SearchResult>();
                case "&&":
                    // 各段结果顺序拼接（典型：页面上有两个列表容器）
                    var outList = new List<SearchResult>();
                    foreach (var part in combo.Parts)
                    {
                        if (string.IsNullOrWhiteSpace(part))
                        {
                            continue;
                        }

                        outList.AddRange(ParseSearchList(source, ctx, baseUrl, rule, part));
                    }

                    return outList;
                case "%%":
                    // 按索引交错合并（典型：多列布局的书列表，读者期待按行交错）
                    var lists = combo.Parts
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => ParseSearchList(source, ctx, baseUrl, rule, p))
                        .ToList();
                    var maxLen = lists.Count > 0 ? lists.Max(l => l.Count) : 0;
                    var interleaved = new List<SearchResult>();
                    for (var i = 0; i < maxLen; i++)
                    {
                        foreach (var list in lists)
                        {
                            if (i < list.Count)
                            {
                                interleaved.Add(list[i]);
                            }
                        }
                    }

                    return interleaved;
            }
        }

        var mode = RuleAnalyzer.DetectMode(listRule, ctx);
        return mode switch
        {
            RuleMode.JsonPath => SearchBooksJson(source, ctx, baseUrl, rule, listRule),
            RuleMode.XPath => SearchBooksHtml(source, ctx, baseUrl, rule, listRule),
            RuleMode.Regex => SearchBooksRegex(source, ctx.Raw, baseUrl, rule, listRule),
            RuleMode.Js => SearchBooksJs(source, ctx, baseUrl, rule, listRule),
            _ => SearchBooksHtml(source, ctx, baseUrl, rule, listRule),
        };
    }

    /// <summary>
    /// HTML 列表解析：列表选择器命中若干节点 → 每节点一个片段 → 逐片段 BuildBook。
    /// 活节点形态：字段求值时才惰性克隆进独立文档，省掉逐片段 OuterHtml 序列化 + 重解析。
    /// </summary>
    private List<SearchResult> SearchBooksHtml(BookSourceModel source, FragmentContext ctx, string baseUrl, SearchRule rule, string listRule)
    {
        var frags = Selectors.ListFragmentContexts(ctx, listRule);
        return frags.Select(f => BuildBook(source, rule, f, baseUrl)).ToList();
    }

    /// <summary>
    /// JSON 列表解析：JSONPath 命中若干节点 → 每节点一个片段（Raw 惰性序列化）。
    /// 复用整份 body 已解析的根节点（ctx.Json），不二次全量解析。
    /// </summary>
    private List<SearchResult> SearchBooksJson(BookSourceModel source, FragmentContext ctx, string baseUrl, SearchRule rule, string listRule)
    {
        var stripped = RuleAnalyzer.StripModePrefix(listRule);
        var nodes = JsonPathEval.QueryNodes(ctx.Json, stripped);
        return nodes
            .Select(n => BuildBook(source, rule, n, baseUrl))
            .ToList();
    }

    /// <summary>
    /// 正则列表解析（对标 reader search_books_regex）：每个匹配的**整个匹配文本**
    /// 作为一个片段（可选替换段先作用于匹配值），再 BuildBook。
    /// </summary>
    private List<SearchResult> SearchBooksRegex(BookSourceModel source, string body, string baseUrl, SearchRule rule, string listRule)
    {
        var raw = RuleAnalyzer.StripModePrefix(listRule);
        var (pattern, replacePart) = SplitRegexRule(raw);
        if (string.IsNullOrEmpty(pattern))
        {
            return new List<SearchResult>();
        }

        var re = RuleAnalyzer.GetRegex(pattern);
        if (re is null)
        {
            // 正则非法：返回空
            return new List<SearchResult>();
        }

        var results = new List<SearchResult>();
        foreach (Match m in re.Matches(body))
        {
            var value = m.Value;
            if (replacePart is not null)
            {
                value = RuleAnalyzer.ApplyLegadoRegex(value, replacePart);
            }

            results.Add(BuildBook(source, rule, value, baseUrl));
        }

        return results;
    }

    /// <summary>
    /// JS 列表解析（对标 reader search_books_js）：先跑前导 JS 段，输出作为新 body：
    /// ① 输出像 JSON → 按剩余规则解析（剩余为空时按 $ 根解析，即输出本身是条目数组）；
    /// ② 输出非 JSON → 按剩余规则解析（剩余为空时按 body，即整段输出作单片段）。
    /// </summary>
    private List<SearchResult> SearchBooksJs(BookSourceModel source, FragmentContext ctx, string baseUrl, SearchRule rule, string listRule)
    {
        var (script, remaining) = SplitLeadingJs(listRule);

        // 纯 JS 列表规则（后面没有选择器）：脚本可以直接返回条目数组
        // （`java.getElements(...)` / `list.push({name, author, bookUrl})`）。
        // 走 RunJs 只能拿到 "a,b,c" / "[object Object],…" —— 条目信息已丢，
        // 必须按 legado getElements 语义先取列表结构（与目录链同一套口径）。
        if (script.Length > 0 && remaining.Trim().Length == 0)
        {
            var items = RunJsElements(script, ctx.Raw, baseUrl);
            if (items is { Count: > 0 })
            {
                var books = new List<SearchResult>(items.Count);
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        books.Add(BuildBook(source, rule, item, baseUrl));
                    }
                }

                return books;
            }
        }

        var output = RunJs(script, ctx.Raw, baseUrl);

        var next = string.IsNullOrWhiteSpace(remaining) ? "$" : remaining;
        if (IsJsonLike(output))
        {
            return ParseSearchList(source, output, baseUrl, rule, next);
        }

        return ParseSearchList(source, output, baseUrl, rule, string.IsNullOrWhiteSpace(remaining) ? "body" : remaining);
    }

    /// <summary>文本片段 → SearchResult（片段解析推迟到字段需要时，9 个字段复用同一份解析）。</summary>
    private SearchResult BuildBook(BookSourceModel source, SearchRule rule, string fragment, string baseUrl)
    {
        return BuildBook(source, rule, new FragmentContext(fragment), baseUrl);
    }

    /// <summary>JSON 节点片段 → SearchResult（Raw 惰性：纯 JSONPath 字段不触发 ToJsonString）。</summary>
    private SearchResult BuildBook(BookSourceModel source, SearchRule rule, JsonNode? node, string baseUrl)
    {
        return BuildBook(source, rule, new FragmentContext(node), baseUrl);
    }

    /// <summary>
    /// 在片段上求值全部搜索字段（对标 reader build_search_book）。
    /// 7 个文本字段直接取；两个 URL 字段最后绝对化（resolve_url 保留 query、去 fragment）。
    /// </summary>
    private SearchResult BuildBook(BookSourceModel source, SearchRule rule, FragmentContext ctx, string baseUrl)
    {
        var book = new SearchResult
        {
            Name = ExtractField(rule.Name, ctx, baseUrl),
            Author = ExtractField(rule.Author, ctx, baseUrl),
            Intro = ExtractField(rule.Intro, ctx, baseUrl),
            Kind = ExtractField(rule.Kind, ctx, baseUrl),
            LastChapter = ExtractField(rule.LastChapter, ctx, baseUrl),
            UpdateTime = ExtractField(rule.UpdateTime, ctx, baseUrl),
            WordCount = ExtractField(rule.WordCount, ctx, baseUrl),
        };

        var bookUrl = ExtractUrl(rule.BookUrl, ctx, baseUrl);
        var coverUrl = ExtractUrl(rule.CoverUrl, ctx, baseUrl);
        book.BookUrl = bookUrl.Length > 0 ? bookUrl : null;
        book.CoverUrl = coverUrl.Length > 0 ? coverUrl : null;
        return book;
    }

    /// <summary>
    /// URL 字段求值（对标 legado <c>getString(rule, isUrl = true)</c> → jsoup <c>getString0</c>）。
    /// <para><b>与文本字段的唯一差别</b>：多命中时只取<b>第一个</b>非空值。
    /// legado 对 <c>bookUrl/coverUrl/tocUrl/chapterUrl/nextContentUrl</c> 走
    /// <c>AnalyzeByJSoup.getString0</c>（<c>list[0]</c>），文本字段才走 <c>getString</c>
    /// （多命中 <c>\n</c> 拼接）。按拼接结果绝对化会得到
    /// <c>https://站点/a&lt;换行&gt;/b</c> 这种畸形 URL —— 目录条目全被带去 404 页。
    /// 本质因是样本里大量 <c>tag.a@href</c> 会同时命中卡片里外两层链接。</para>
    /// </summary>
    private string ExtractUrlField(string? rule, FragmentContext ctx, string baseUrl)
    {
        var value = ExtractField(rule, ctx, baseUrl);
        if (value.Length == 0 || value.IndexOf('\n') < 0)
        {
            return value;
        }

        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>URL 字段 + 绝对化（空值 → 空串，由调用方决定兜底）。</summary>
    private string ExtractUrl(string? rule, FragmentContext ctx, string baseUrl)
    {
        var raw = ExtractUrlField(rule, ctx, baseUrl);
        return raw.Length == 0 ? string.Empty : UrlUtil.ResolveUrl(baseUrl, raw);
    }

    /// <summary>
    /// 字段规则入口（一切字段求值的总入口）：
    /// ① 空规则 → 空串（reader：空规则返回空值，不落入"纯 JS 规则"分支拿 outerHtml）；
    /// ② 含 <c>{{...}}</c> 结果模板 → 走模板展开（内嵌规则求值后内联回字面量），
    /// 不再走选择器派发——否则 /novel/{{$.id}} 这类会被误判为 XPath（项目踩坑点 7）；
    /// ③ 其余 → 组合规则求值（ExtractContent）。
    /// </summary>
    private string ExtractField(string? rule, FragmentContext ctx, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return string.Empty;
        }

        // 字面量模板（{{规则}} / @get:{变量} / {$.路径}）→ 先把内嵌规则替换成字面量，
        // 整串不再当选择器求值。漏了这两支的后果：
        //   · 吉站漫画 chapterUrl `…?m=@get:{bid}&c={{$..id}}` → @get 原样留在 URL 上；
        //   · 快看漫画（api.kkmh.com）chapterUrl `…/v2/comic/{$.id}?is_preview=0` → 无前缀，
        //     被当成 CSS 选择器，0 命中。
        if (rule.Contains("{{", StringComparison.Ordinal)
            || rule.IndexOf("@get:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // 两种模板可以混在同一条规则里（吉站漫画：/chapter?m=@get:{bid}&c={{$..id}}），
            // 也可能与 {$.路径} 混用（快看漫画 api.kkmh：/v2/comic/{$.id}?is_preview=0）。
            // 因此展开要<b>串起来</b>：@get/{{}} 先变字面量，再把剩下的 {$.路径} 求值。
            // 只做前者会把 "{$.id}" 原样交给 ResolveUrl（花括号被当非法字符剥掉），
            // 章节地址最终变成 ".../v2/comic/$.id"。
            return ExpandInnerRules(ExpandTemplates(rule, ctx), ctx);
        }

        if (rule.Contains("{$.", StringComparison.Ordinal))
        {
            return ExpandInnerRules(rule, ctx);
        }

        return ExtractContent(rule, ctx);
    }

    /// <summary>
    /// 展开 legado 结果模板 <c>{{rule}}</c>：把内部规则在当前片段上求值后内联回字符串。
    /// 例如 bookUrl <c>/novel/{{$.novelId}}?isSearch=1</c> 会把 <c>$.novelId</c> 替换为当前条目的值。
    /// <para><b>同时处理 <c>@get:{key}</c></b>（legado <c>evalPattern</c> 把
    /// <c>@get:{…}</c> 与 <c>{{…}}</c> 归为同一类"内嵌求值"，都在 SourceRule 初始化时展开）：
    /// 吉站漫画的章节 URL 就是 <c>…&amp;c={{$..id}}</c> 与 <c>?m=@get:{bid}</c> 混用。</para>
    /// </summary>
    private string ExpandTemplates(string value, FragmentContext ctx)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var expanded = value.IndexOf("@get:", StringComparison.OrdinalIgnoreCase) >= 0
            ? GetRuleVarRegex.Replace(value, m =>
            {
                var key = m.Groups[1].Value.Trim();
                return _ruleVars.TryGetValue(key, out var stored) ? stored : string.Empty;
            })
            : value;

        if (!expanded.Contains("{{", StringComparison.Ordinal))
        {
            return expanded;
        }

        return TemplateRegex.Replace(expanded, m =>
        {
            var inner = m.Groups[1].Value.Trim();
            if (inner.Length == 0)
            {
                // {{}} 空模板：原样保留
                return m.Value;
            }

            var sub = ExtractContent(inner, ctx);
            return sub ?? string.Empty;
        });
    }

    /// <summary>
    /// 展开字面量里的 <c>{$.路径}</c> 内嵌取值（legado <c>AnalyzeByJSonPath.getString</c> 的
    /// <c>innerRule("{$.")</c>）。例：快看漫画（api.kkmh.com）
    /// <c>chapterUrl = https://api.kkmh.com/v2/comic/{$.id}?is_preview=0</c>。
    /// <para>只在"整条不是 JSONPath 规则"时才走这里（规则以 <c>$</c> 开头说明它本身就是路径）；
    /// 花括号内按平衡组取值，值里含 <c>}</c> 不会把后面的字面量吃掉。</para>
    /// </summary>
    private string ExpandInnerRules(string value, FragmentContext ctx)
    {
        return InnerRuleRegex.Replace(value, m =>
        {
            var inner = m.Groups[1].Value.Trim();
            return inner.Length == 0 ? string.Empty : ExtractContent(inner, ctx);
        });
    }

    /// <summary>
    /// 组合规则求值（对标 reader extract_content，一切非模板字段的核心）。
    /// <para>步骤（顺序即项目铁律）：</para>
    /// ① 整条是 JS → 直接单规则求值（JS 里的 || 是逻辑或，拆不得）；
    /// ② 先拆 <c>##</c>（组合符只在 ## 之前生效，## 里的 | 是正则交替符）；
    /// ③ 按 &amp;&amp; / || / %% 拆段，每段独立判定模式后求值；
    /// ④ 组合：&amp;&amp;/%% = 非空值顺序拼接，|| = 第一个非空值；
    /// ⑤ 替换段作用于**组合后的整体**（legado：对每个 SourceRule 的最终结果做替换）；
    /// ⑥ 无组合符 → 单规则求值（传整条，内部自己处理 ##）。
    /// </summary>
    private string ExtractContent(string rule, FragmentContext ctx)
    {
        // 顺序即铁律（红线 14）：**先拆 ## 替换段，再碰脚本段**。
        // 脚本段若把 "##pat##rep" 一起吃进去 → JS 语法错误整段回退原文 / 替换被静默丢掉。
        var (combinable, replacePart) = RuleAnalyzer.SplitLegadoRegex(rule);

        // @put:{k:rule} 先落变量并从规则里剥掉（legado splitPutRule：在 SourceRule 初始化时做）
        var core = ExtractContentCore(ApplyPutRule(combinable, ctx), ctx);
        return replacePart is not null ? RuleAnalyzer.ApplyLegadoRegex(core, replacePart) : core;
    }

    /// <summary>
    /// 字段规则的核心求值（不含 <c>##</c> 替换段）。步骤：
    /// ① 含脚本段（文本 + <c>&lt;js&gt;</c>/<c>@js:</c> 的任意组合）→ 按 legado
    ///    <c>splitSourceRule</c> 的顺序**逐段**求值（见 <see cref="EvalJsChain"/>）；
    /// ② 整条是 JS → 单规则求值；
    /// ③ 顶层 <c>&amp;&amp;</c>/<c>||</c>/<c>%%</c> 拆分合并；
    /// ④ 单段求值。
    /// <para><b>为什么脚本段要先于组合符处理</b>：<c>$.id||$.topic_id@js:"…/mobile/"+result+"/list/"</c>
    /// （快看漫画 bookUrl）语义是"先按 || 取 id，再把**结果**拼成 URL"。若先按 || 切段，
    /// 第二段变成 <c>$.topic_id@js:…</c>：选择器空 → 脚本拿到空串 → 拼出 <c>/mobile//list/</c>，
    /// 而且 || 取"首个非空"会直接返回纯数字 id（bookUrl 变成 <c>…/search/mini/topic</c> 之类的错值，
    /// 详情页抓到的其实是搜索接口 JSON —— 快看漫画"目录为空 + 详情字段是 JSON"的根因）。</para>
    /// </summary>
    private string ExtractContentCore(string rule, FragmentContext ctx)
    {
        // 脚本段判定**必须先于 IsJsRule**：规则以 `<js>` 开头但后面还跟着选择器/JSONPath
        // （快看漫画 author/kind/lastChapter：`<js>…JSON.stringify(__NUXT__)</js>\n$.data[*].topicInfo…`）
        // 时，IsJsRule 也是 true。先按"纯 JS"处理会把脚本输出（整份 NUXT JSON）当成字段值返回，
        // 后面的取值段被整个丢掉——详情页每个字段都变成一大坨 JSON。
        var head = RuleAnalyzer.SplitFirstJs(rule);
        if (head.Script is not null)
        {
            if (head.Before.Trim().Length == 0 && head.After.Trim().Length == 0)
            {
                return ExtractContentSingle(rule, ctx);
            }

            return EvalJsChain(rule, ctx);
        }

        var combo = RuleAnalyzer.SplitTopLevel(rule, new[] { "&&", "||", "%%" });
        if (combo.Delimiter is not null)
        {
            var values = combo.Parts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => ExtractContentSingle(p, ctx))
                .ToList();

            return combo.Delimiter switch
            {
                "&&" => string.Concat(values.Where(v => v.Length > 0)),
                "||" => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty,
                "%%" => string.Concat(values.Where(v => v.Length > 0)),
                _ => ExtractContentSingle(rule, ctx),
            };
        }

        return ExtractContentSingle(rule, ctx);
    }

    /// <summary>
    /// 文本段 ↔ 脚本段顺序求值（legado <c>getString/getElements</c> 的 <c>ruleList</c> 循环）：
    /// <c>value</c> 初值 = 原文，每遇文本段就用当前值求值，每遇脚本段就跑脚本（<c>result</c> = 当前值、
    /// <c>src</c> = <b>原文</b>），脚本输出成为新的当前值。
    /// <para>典型形态：<c>$.data.chapters\n@js: JSON.parse(result).map(…)</c>（光社漫畫目录）、
    /// <c>&lt;js&gt;…&lt;/js&gt;\n$.data[*].comicList[*]</c>（快看漫画目录）。</para>
    /// </summary>
    private string EvalJsChain(string rule, FragmentContext rootCtx)
    {
        var ctx = rootCtx;
        var value = rootCtx.Raw;
        var rest = rule;
        var consumed = false;

        while (true)
        {
            var (before, script, after) = RuleAnalyzer.SplitFirstJs(rest);

            if (script is null)
            {
                var tail = rest.Trim();
                if (!consumed)
                {
                    // 没有任何脚本段被消费（理论不可达：调用方已判 HasJsSegment）
                    return ExtractContentSingle(tail, ctx);
                }

                return tail.Length == 0 ? value : ExtractContentCore(tail, ctx);
            }

            var pre = before.Trim();
            if (pre.Length > 0)
            {
                value = ExtractContentCore(pre, ctx);
                ctx = new FragmentContext(value);
            }

            // src 恒为**本条规则的原文**（legado：整个 AnalyzeRule.content，不随分段变化）
            value = RunJs(script, value, string.Empty, null, rootCtx.Raw);
            ctx = new FragmentContext(value);
            consumed = true;

            rest = after;
            if (rest.Trim().Length == 0)
            {
                return value;
            }
        }
    }

    /// <summary>
    /// <c>@put:{key:rule,…}</c>：在当前内容上求值各条规则并落进规则变量表，返回剥掉该标记的规则。
    /// 对标 legado <c>splitPutRule</c> + <c>putRule</c>（后者把值写进 chapter/book 变量）。
    /// <para>典型用法（吉站漫画目录）：<c>$..chapters[*]@put:{bid:$.data.id}</c> 先把书籍 id 存下来，
    /// 逐章规则再用 <c>…?m=@get:{bid}&amp;c={{$..id}}</c> 取值。没有这一层，章节 URL 里的
    /// <c>@get:{bid}</c> 原样留在 URL 上 → 整份目录 0 章。</para>
    /// </summary>
    private string ApplyPutRule(string rule, FragmentContext ctx)
    {
        if (rule.IndexOf("@put:", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return rule;
        }

        var sb = new System.Text.StringBuilder(rule.Length);
        var pos = 0;
        while (true)
        {
            var start = rule.IndexOf("@put:", pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                sb.Append(rule, pos, rule.Length - pos);
                break;
            }

            sb.Append(rule, pos, start - pos);

            var braceStart = rule.IndexOf('{', start);
            var braceEnd = braceStart < 0 ? -1 : rule.IndexOf('}', braceStart);
            if (braceEnd < 0)
            {
                // 不成对：原样保留（交给后续求值自然失败，不静默吞掉用户规则）
                sb.Append(rule, start, rule.Length - start);
                break;
            }

            var body = rule.Substring(braceStart + 1, braceEnd - braceStart - 1);
            foreach (var entry in RuleAnalyzer.SplitTopLevel(body, new[] { "," }).Parts)
            {
                var colon = entry.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = entry.Substring(0, colon).Trim().Trim('"', '\'');
                var valueRule = entry.Substring(colon + 1).Trim();
                if (key.Length == 0 || valueRule.Length == 0)
                {
                    continue;
                }

                _ruleVars[key] = ExtractContent(valueRule, ctx);
            }

            pos = braceEnd + 1;
        }

        return sb.ToString().Trim();
    }

    /// <summary>规则变量表（<c>@put:/@get:</c>；legado 存在 book/chapter 变量里，这里按引擎实例存）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _ruleVars =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 求值书源级 <c>header</c> 规则，返回 JSON 文本（供 <see cref="UrlAnalyzer.ParseHeaderJson"/> 解析）。
    /// <para>静态 JSON 原样返回；<c>@js:</c> / <c>&lt;js&gt;</c> 形态交脚本求值
    /// （样本写法：<c>@js: JSON.stringify({’User-Agent’: java.getWebViewUA(), …})</c>，
    /// 需要 <c>java</c> 与 <c>baseUrl</c> 绑定，故不能在下层抓取器里做）。</para>
    /// <para>求值失败返回 null（调用方按"无书源头"处理，不比没有更差）。</para>
    /// </summary>
    internal string? EvalSourceHeaderJson(string baseUrl, BookSourceModel? source = null)
    {
        var owner = source ?? _jsSource;
        var raw = owner?.Header;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith("@js:", StringComparison.Ordinal) || text.StartsWith("<js>", StringComparison.Ordinal))
        {
            // 脚本里的 baseUrl 指书源主页；java.* 走本引擎实例（每个引擎各自持有 Jint，天然线程隔离）
            var previous = _jsSource;
            try
            {
                if (source is not null)
                {
                    _jsSource = source;
                }

                var json = RunJs(RuleAnalyzer.StripModePrefix(text), string.Empty, baseUrl, null, string.Empty);
                return string.IsNullOrWhiteSpace(json) ? null : json;
            }
            catch
            {
                return null;
            }
            finally
            {
                _jsSource = previous;
            }
        }

        return text;
    }

    /// <summary>
    /// 给请求描述挂上书源级请求头（书源头为默认值，<paramref name="spec"/> 自带的头优先）。
    /// 引擎内所有"对书源发起请求"的地方（搜索/详情/目录/正文/图片）都应先过这一层。
    /// </summary>
    private RequestSpec WithSourceHeaders(RequestSpec spec, string baseUrl)
    {
        var sourceHeaders = UrlAnalyzer.ParseHeaderJson(EvalSourceHeaderJson(baseUrl));
        return sourceHeaders is null
            ? spec
            : spec with { Headers = UrlAnalyzer.MergeHeaders(sourceHeaders, spec.Headers) };
    }

    /// <summary>
    /// 单段规则求值（对标 reader extract_content_single）。步骤：
    /// ① 拆脚本段（&lt;js&gt;…&lt;/js&gt; 尾随形态 / @js:）：有脚本 → 先取选择器值（空选择器 = 整片段 Raw）
    ///    再交脚本处理；
    /// ② 拆 ## 替换段；③ 按模式求值（每段独立判定，组合段可以是不同模式）：
    ///    JsonPath 多值 \n 拼接 / XPath 多值 \n 拼接 / Regex 取首个匹配 / Js 整条是脚本；
    /// ④ 替换段作用于本段结果。
    /// </summary>
    private string ExtractContentSingle(string rule, FragmentContext ctx)
    {
        // 顺序与 reader eval_field_html_single_with_ctx 一致：**先拆 ## 替换段，
        // 再拆脚本段**。反过来的话 @js: 形态的 script 会把 "##pat##rep" 一起吃进去
        // 当 JS 执行（语法错误整段回退），<js>…</js>##pat##rep 的替换也被吞掉。
        var (ruleWithoutReplace, replacePart) = RuleAnalyzer.SplitLegadoRegex(rule);

        // 选择器 + 脚本段：先按选择器取值，再交给脚本处理。
        var (selectorRule, script) = RuleAnalyzer.ExtractJs(ruleWithoutReplace);
        if (script is not null)
        {
            // 选择器结果是脚本的 result：JSONPath 命中对象/数组给 JSON 文本，
            // 纯标量列表按 \n 拼接（legado getString 的两种形态，见 EvalSegmentValue）
            var value = string.IsNullOrEmpty(selectorRule)
                ? ctx.Raw
                : EvalSegmentValue(selectorRule, ctx);
            var output = RunJs(script, value, string.Empty);
            // 替换段作用于脚本输出（legado：JS 也是 SourceRule，replaceRegex 对最终结果生效）
            return replacePart is not null ? RuleAnalyzer.ApplyLegadoRegex(output, replacePart) : output;
        }

        var combinable = ruleWithoutReplace;

        // 裸字段名 + JSON 对象条目 → 直接取属性（legado AnalyzeRule.getString 的
        // NativeObject / LinkedTreeMap 分支：`result[sourceRule.rule]`）。
        // 典型：光社漫畫目录脚本返回 [{text,url}]，chapterName="text"、chapterUrl="url"。
        // 缺这一支时裸名会落到 CSS 选择器 → 0 命中 → 条目虽有但标题全空 →
        // 被"无标题跳过"过滤干净 → 用户看到的就是"无法获取目录"。
        if (RuleAnalyzer.TryDetectModeFromRule(combinable) is null
            && BareKeyRegex.IsMatch(combinable)
            && ctx.Json is System.Text.Json.Nodes.JsonObject jsonObject)
        {
            var picked = jsonObject.TryGetPropertyValue(combinable, out var prop) && prop is not null
                ? (prop is System.Text.Json.Nodes.JsonValue v ? v.ToString() : prop.ToJsonString())
                : string.Empty;
            return replacePart is not null ? RuleAnalyzer.ApplyLegadoRegex(picked, replacePart) : picked;
        }

        var mode = RuleAnalyzer.DetectMode(combinable, ctx);
        var extracted = mode switch
        {
            RuleMode.JsonPath => string.Join("\n", JsonPathEval.Query(ctx.Json, RuleAnalyzer.StripModePrefix(combinable))),
            RuleMode.XPath => string.Join("\n", Selectors.FieldValues(ctx, combinable)),
            RuleMode.Regex => EvaluateRegexRule(combinable, ctx.Raw),
            RuleMode.Js => RunJs(RuleAnalyzer.StripModePrefix(combinable), ctx.Raw, string.Empty),
            _ => string.Join("\n", Selectors.FieldValues(ctx, combinable)),
        };

        return replacePart is not null ? RuleAnalyzer.ApplyLegadoRegex(extracted, replacePart) : extracted;
    }

    /// <summary>
    /// 正则字段规则求值（对标 reader 的 Regex 分支）：剥前缀 → 切 pattern/替换段 →
    /// 取**首个匹配**（reader regex_extract_first）→ 应用替换段。
    /// </summary>
    private string EvaluateRegexRule(string combinable, string content)
    {
        var raw = RuleAnalyzer.StripModePrefix(combinable);
        var (pattern, replacePart) = SplitRegexRule(raw);
        if (string.IsNullOrEmpty(pattern))
        {
            return string.Empty;
        }

        var re = RuleAnalyzer.GetRegex(pattern);
        if (re is null)
        {
            return string.Empty;
        }

        var m = re.Match(content);
        var matched = m.Success ? m.Value : string.Empty;
        return replacePart is not null ? RuleAnalyzer.ApplyLegadoRegex(matched, replacePart) : matched;
    }

    /// <summary>
    /// 脚本执行（一切 JS 求值的总出口）。
    /// <para>java.getString 的语义锚点：内嵌规则必须作用于**当前 result**（即 value）。
    /// 故用 value 现建一个片段上下文，把 EvaluateRule 闭包指向它——脚本内嵌规则与外层
    /// 共用同一份惰性解析结果（同一段 HTML/JSON 不会被内嵌规则再解析一遍）。</para>
    /// <para>阶段 4-5：同闭包一并注入 list/elements 求值（<c>java.getStringList/getElements</c>），
    /// 并支持 <c>java.setContent(x)</c> 的内容替换——覆盖内容按**引用相等**缓存片段上下文，
    /// 循环里逐项 setContent（样本真实写法）时同一字符串不重解析、换内容立即重建。</para>
    /// </summary>
    private string RunJs(
        string script,
        string value,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? globals = null,
        string? src = null)
    {
        var ctx = new FragmentContext(value);

        // setContent 的覆盖内容 → 片段上下文（引用相等缓存：同串不重解析）
        var overrideRaw = value;
        var overrideCtx = ctx;
        FragmentContext Ctx(string? content)
        {
            if (content is null || ReferenceEquals(content, overrideRaw))
            {
                return overrideCtx;
            }

            overrideRaw = content;
            return overrideCtx = new FragmentContext(content);
        }

        // 重入保护：脚本内 java.getString("sel<js>…</js>") 会经 EvaluateRule 回调
        // 再次进入本方法（同引擎嵌套）。旧实现无条件覆写三个委托且不恢复——
        // 嵌套返回后，外层脚本后续的 java.getString/getStringList/getElements
        // 全部作用到内层片段上，静默产出错误字段值。这里保存旧委托，finally 恢复。
        // （引擎实例按"租借"语义使用：ConcurrentBag 池保证同一时刻只有一个线程
        // 持有本实例，故只需防嵌套重入，不需防并行。）
        var prevRule = _js.EvaluateRule;
        var prevList = _js.EvaluateList;
        var prevElements = _js.EvaluateElements;
        try
        {
            _js.EvaluateRule = (r, content) => ExtractContent(r, Ctx(content));
            _js.EvaluateList = (r, content) => ExtractListValues(r, Ctx(content));
            _js.EvaluateElements = (r, content) => ExtractElementValues(r, Ctx(content));

            // jsLib 透传（便宜：字符串引用赋值；JsBridge 内部按文本决定是否重新加载库）
            _js.JsLib = _jsSource?.JsLib;
            return _js.Run(script, value, baseUrl, globals, src);
        }
        finally
        {
            _js.EvaluateRule = prevRule;
            _js.EvaluateList = prevList;
            _js.EvaluateElements = prevElements;
        }
    }

    /// <summary>
    /// 列表脚本求值（对标 legado <c>getElements</c> 的 <c>Mode.Js</c> 分支）：保留脚本返回的
    /// <b>数组结构</b>，逐项给一个条目片段；返回 null 表示"脚本结果不是数组"，
    /// 调用方按原来的字符串路径处理（脚本改写原文，输出仍是整份 HTML/JSON）。
    /// <para>闭包绑定与 <see cref="RunJs"/> 完全一致（java.getString/getElements/setContent
    /// 都作用于本次内容），否则脚本里的 <c>java.getElements(...)</c> 会求值到别处。</para>
    /// </summary>
    private IReadOnlyList<string>? RunJsElements(string script, string value, string baseUrl, string? src = null)
    {
        var ctx = new FragmentContext(value);
        var overrideRaw = value;
        var overrideCtx = ctx;
        FragmentContext Ctx(string? content)
        {
            if (content is null || ReferenceEquals(content, overrideRaw))
            {
                return overrideCtx;
            }

            overrideRaw = content;
            return overrideCtx = new FragmentContext(content);
        }

        var prevRule = _js.EvaluateRule;
        var prevList = _js.EvaluateList;
        var prevElements = _js.EvaluateElements;
        try
        {
            _js.EvaluateRule = (r, content) => ExtractContent(r, Ctx(content));
            _js.EvaluateList = (r, content) => ExtractListValues(r, Ctx(content));
            _js.EvaluateElements = (r, content) => ExtractElementValues(r, Ctx(content));
            _js.JsLib = _jsSource?.JsLib;
            return _js.RunElements(script, value, baseUrl, src);
        }
        finally
        {
            _js.EvaluateRule = prevRule;
            _js.EvaluateList = prevList;
            _js.EvaluateElements = prevElements;
        }
    }

    /// <summary>
    /// 目录条目片段集合（对标 legado <c>AnalyzeRule.getElements</c>）：把 chapterList 规则
    /// 求值成"每章一个片段"。三种形态统一到片段：
    /// ① JS 段（<c>&lt;js&gt;…&lt;/js&gt;</c> 纯脚本）→ 脚本返回的数组逐项（<see cref="RunJsElements"/>）；
    /// ② JSONPath → 命中节点各一片段；
    /// ③ CSS → 活节点片段。
    /// <para>顶层 <c>&amp;&amp;</c>/<c>||</c>/<c>%%</c> 按 legado 语义合并（顺序并集 / 首个非空 / 交叉）。
    /// 缺了组合符处理，<c>class.pure-g[3,4]@class.comics-chapters__item||class.pure-g.2@…[-1:0]</c>
    /// 这种"主选择器 + 备用选择器"的目录规则会被当成一个 CSS 选择器 → 0 章。</para>
    /// </summary>
    private List<FragmentContext> TocFragments(string selector, FragmentContext ctx, string baseUrl)
    {
        var rule = (selector ?? string.Empty).Trim();
        if (rule.Length == 0)
        {
            return new List<FragmentContext>();
        }

        var combo = RuleAnalyzer.SplitTopLevel(rule, new[] { "&&", "||", "%%" });
        if (combo.Delimiter is null)
        {
            return PartFragments(rule, ctx, baseUrl);
        }

        var lists = new List<List<FragmentContext>>();
        foreach (var part in combo.Parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var one = PartFragments(part.Trim(), ctx, baseUrl);
            if (combo.Delimiter == "||" && one.Count > 0)
            {
                return one;
            }

            lists.Add(one);
        }

        if (combo.Delimiter == "%%")
        {
            var maxLen = lists.Count > 0 ? lists.Max(l => l.Count) : 0;
            var interleaved = new List<FragmentContext>();
            for (var i = 0; i < maxLen; i++)
            {
                foreach (var list in lists)
                {
                    if (i < list.Count)
                    {
                        interleaved.Add(list[i]);
                    }
                }
            }

            return interleaved;
        }

        var unioned = new List<FragmentContext>();
        foreach (var list in lists)
        {
            unioned.AddRange(list);
        }

        return unioned;
    }

    /// <summary>单段 chapterList 规则 → 片段列表（JS / JSONPath / CSS 三态）。</summary>
    private List<FragmentContext> PartFragments(string part, FragmentContext ctx, string baseUrl)
    {
        var fragments = new List<FragmentContext>();

        if (RuleAnalyzer.IsJsRule(part))
        {
            var (script, remaining) = SplitLeadingJs(part);
            if (remaining.Trim().Length > 0)
            {
                // JS + 后续选择器：先字符串化再按后续规则取片段（与原有语义一致）
                var output = RunJs(script, ctx.Raw, baseUrl);
                return PartFragments(remaining, new FragmentContext(output), baseUrl);
            }

            var items = RunJsElements(script, ctx.Raw, baseUrl);
            if (items is not null)
            {
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        fragments.Add(new FragmentContext(item));
                    }
                }

                return fragments;
            }

            // 脚本结果不是数组（只是"改写原文"）：沿用原兜底语义——输出像 JSON 用 $[*]，否则 body > *
            var raw = RunJs(script, ctx.Raw, baseUrl);
            var fallback = IsJsonLike(raw) ? "$[*]" : "body > *";
            return PartFragments(fallback, new FragmentContext(raw), baseUrl);
        }

        if (RuleAnalyzer.DetectMode(part, ctx) == RuleMode.JsonPath)
        {
            foreach (var node in JsonPathEval.QueryNodes(ctx.Json, RuleAnalyzer.StripModePrefix(part)))
            {
                fragments.Add(new FragmentContext(node));
            }

            return fragments;
        }

        return Selectors.ListFragmentContexts(ctx, part);
    }

    /// <summary>
    /// 逐片段构造章节：init 文档级副作用一次、无标题跳过、按绝对 URL 去重
    /// （与 <see cref="ParseTocHtml"/> 同规则，两者共用这一份判定）。
    /// </summary>
    private List<TocItem> BuildChaptersFromFragments(
        BookSourceModel source, TocRule rule, IReadOnlyList<FragmentContext> fragments, string baseUrl, FragmentContext docCtx)
    {
        var result = new List<TocItem>();
        if (fragments.Count == 0)
        {
            return result;
        }

        // init 在文档级执行一次（仅副作用，与 ParseTocHtml 对齐）
        if (!string.IsNullOrWhiteSpace(rule.Init))
        {
            ExtractField(rule.Init, docCtx, baseUrl);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fragment in fragments)
        {
            var chapter = BuildChapter(source, rule, fragment, baseUrl, seen, result.Count);
            if (chapter is not null)
            {
                result.Add(chapter);
            }
        }

        return result;
    }

    /// <summary>
    /// 列表求值（阶段 4-5，<c>java.getStringList</c>）：与字段求值同一套模式派发，
    /// 但保留多值形态（JSONPath 逐节点 / 字段规则逐命中 / 正则逐匹配）。
    /// </summary>
    private IReadOnlyList<object?> ExtractListValues(string rule, FragmentContext ctx)
    {
        // 同 ExtractContentSingle：先拆 ## 再拆脚本（@js: 的 script 不吃替换段）
        var (ruleWithoutReplace, replacePart) = RuleAnalyzer.SplitLegadoRegex(rule);
        var (selectorRule, script) = RuleAnalyzer.ExtractJs(ruleWithoutReplace);
        if (script is not null)
        {
            // 前导脚本：输出若像 JSON 数组则逐项，否则整段单值
            var value = string.IsNullOrEmpty(selectorRule)
                ? ctx.Raw
                : ExtractContentSingle(selectorRule, ctx);
            var output = RunJs(script, value, string.Empty);

            List<object?> scriptValues;
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(output);
                scriptValues = node is System.Text.Json.Nodes.JsonArray list
                    ? list.Select(n => (object?)(n?.ToString() ?? string.Empty)).ToList()
                    : new List<object?> { output };
            }
            catch
            {
                // 非 JSON：整段作单值
                scriptValues = new List<object?> { output };
            }

            if (replacePart is not null)
            {
                scriptValues = scriptValues
                    .Select(v => (object?)RuleAnalyzer.ApplyLegadoRegex(v?.ToString() ?? string.Empty, replacePart))
                    .ToList();
            }

            return scriptValues;
        }

        var combinable = ruleWithoutReplace;
        var mode = RuleAnalyzer.DetectMode(combinable, ctx);
        var values = mode switch
        {
            RuleMode.JsonPath => JsonPathEval.Query(ctx.Json, RuleAnalyzer.StripModePrefix(combinable))
                .Select(v => (object?)v).ToList(),
            RuleMode.Regex => RegexMatches(combinable, ctx.Raw),
            _ => Selectors.FieldValues(ctx, combinable).Select(v => (object?)v).ToList(),
        };

        if (replacePart is not null)
        {
            values = values
                .Select(v => (object?)RuleAnalyzer.ApplyLegadoRegex(v?.ToString() ?? string.Empty, replacePart))
                .ToList();
        }

        return values;
    }

    /// <summary>正则逐匹配（<c>java.getStringList</c> / <c>getElements</c> 的 Regex 分支共用）。</summary>
    private List<object?> RegexMatches(string combinable, string content)
    {
        var raw = RuleAnalyzer.StripModePrefix(combinable);
        var (pattern, _) = SplitRegexRule(raw);
        var re = RuleAnalyzer.GetRegex(pattern);
        if (re is null)
        {
            return new List<object?>();
        }

        var result = new List<object?>();
        foreach (Match m in re.Matches(content))
        {
            result.Add(m.Value);
        }

        return result;
    }

    /// <summary>
    /// 元素求值（阶段 4-5，<c>java.getElements/getElement</c>）：按模式派发，
    /// 保留"活"形态——HTML 命中给 <see cref="HtmlNode"/>、JSON 命中给 <see cref="JsonNode"/>、
    /// 正则命中给匹配文本；JS 侧包装（JSoup 风格方法）见 <see cref="RuleJavaApi"/>。
    /// <para>模式派发与列表解析同构（对标 legado <c>AnalyzeRule.getElements</c> 的
    /// Regex/Js/Json/XPath/JSoup 五分支；XPath/CSS 共用列表选择器链）。</para>
    /// </summary>
    private IReadOnlyList<object?> ExtractElementValues(string rule, FragmentContext ctx)
    {
        var (selectorRule, script) = RuleAnalyzer.ExtractJs(rule);
        if (script is not null)
        {
            // 前导脚本：输出像 JSON 数组 → 逐节点；否则整段作为单个文本快照
            var value = string.IsNullOrEmpty(selectorRule)
                ? ctx.Raw
                : ExtractContentSingle(selectorRule, ctx);
            var output = RunJs(script, value, string.Empty);
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(output);
                if (node is System.Text.Json.Nodes.JsonArray list)
                {
                    return list.Cast<object?>().ToList();
                }

                return new List<object?> { node };
            }
            catch
            {
                return new List<object?> { output };
            }
        }

        var (combinable, _) = RuleAnalyzer.SplitLegadoRegex(rule);

        // 顶层组合符（&& / || / %%）：legado 的元素列表也按这三种语义合并
        // （AnalyzeByJSoup.getElements：&& 顺序并集、|| 首个非空即停、%% 按索引交错）。
        // 缺了这一段，`java.getElements("id.a@class.x@a&&id.b@class.x@a")` 会把整条
        // 当成一个 CSS 选择器 → 0 命中 → 目录脚本拿到空数组（包子漫画类源的典型写法）。
        var combo = RuleAnalyzer.SplitTopLevel(combinable, new[] { "&&", "||", "%%" });
        if (combo.Delimiter is not null)
        {
            var lists = new List<IReadOnlyList<object?>>();
            foreach (var part in combo.Parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var value = ExtractElementValues(part.Trim(), ctx);
                if (combo.Delimiter == "||" && value.Count > 0)
                {
                    return value;
                }

                lists.Add(value);
            }

            if (combo.Delimiter == "%%")
            {
                var maxLen = lists.Count > 0 ? lists.Max(l => l.Count) : 0;
                var interleaved = new List<object?>();
                for (var i = 0; i < maxLen; i++)
                {
                    foreach (var list in lists)
                    {
                        if (i < list.Count)
                        {
                            interleaved.Add(list[i]);
                        }
                    }
                }

                return interleaved;
            }

            var unioned = new List<object?>();
            foreach (var list in lists)
            {
                unioned.AddRange(list);
            }

            return unioned;
        }

        var mode = RuleAnalyzer.DetectMode(combinable, ctx);
        switch (mode)
        {
            case RuleMode.JsonPath:
                return JsonPathEval.QueryNodes(ctx.Json, RuleAnalyzer.StripModePrefix(combinable))
                    .Select(n => (object?)n).ToList();

            case RuleMode.Regex:
                return RegexMatches(combinable, ctx.Raw);

            case RuleMode.Js:
                // 整条是脚本（<js>…</js> 已被 ExtractJs 拆走；js: 前缀形态在这里兜底）
                var jsOutput = RunJs(RuleAnalyzer.StripModePrefix(combinable), ctx.Raw, string.Empty);
                return new List<object?> { jsOutput };

            default:
            {
                // CSS（含 legado 缩写链）与 XPath：复用列表选择器（含 body/空规则 = 整段单片段语义）
                var frags = Selectors.ListFragmentContexts(ctx, combinable);
                var result = new List<object?>(frags.Count);
                foreach (var frag in frags)
                {
                    // 活节点形态给节点（快照带 attr/html）；文本特例（body/空规则）给原文
                    result.Add(frag.Node as object ?? frag.Raw);
                }

                return result;
            }
        }
    }

    /// <summary>切分正则规则的 pattern 与替换段（## 起，含 ## 本身）。无 ## 时替换段为 null。</summary>
    private static (string Pattern, string? ReplacePart) SplitRegexRule(string raw)
    {
        var i = raw.IndexOf("##", StringComparison.Ordinal);
        if (i < 0)
        {
            return (raw, null);
        }

        return (raw.Substring(0, i), raw.Substring(i));
    }

    /// <summary>
    /// 切分前导 JS 段（列表规则/正文规则的开头脚本）：返回 (脚本, 剩余规则)。
    /// 三种形态：&lt;js&gt;…&lt;/js&gt;剩余 / @js:全部（无剩余）/ js:全部（无剩余）；
    /// 都不是 → 整条当"剩余"、脚本为空调用方不会走这条路（调用前已判过模式）。
    /// </summary>
    private static (string Script, string Remaining) SplitLeadingJs(string rule)
    {
        var r = rule.Trim();
        if (r.StartsWith("<js>", StringComparison.Ordinal))
        {
            var end = r.IndexOf("</js>", StringComparison.Ordinal);
            if (end > 0)
            {
                return (r.Substring(4, end - 4), r.Substring(end + 5).Trim());
            }
        }

        if (r.StartsWith("@js:", StringComparison.Ordinal))
        {
            return (RuleAnalyzer.StripModePrefix(r), string.Empty);
        }

        if (r.StartsWith("js:", StringComparison.Ordinal))
        {
            return (r.Substring(3).Trim(), string.Empty);
        }

        return (RuleAnalyzer.StripModePrefix(r), string.Empty);
    }

    /// <summary>输出是否像 JSON（首字符 [ 或 {）——决定 JS 输出后按 JSON 还是 HTML 规则解析。</summary>
    private static bool IsJsonLike(string s)
    {
        var t = s.Trim();
        return t.Length > 0 && (t[0] == '[' || t[0] == '{');
    }

    // ==================== 阶段 2：详情 / 目录 / 正文 ====================

    /// <summary>
    /// 书籍详情解析（对标 reader rule_engine::book_info + parse_book_info_*）。
    /// <para>步骤：① 无 ruleBookInfo → null；② init 预处理（AllInOne：输出整体替换原文，
    /// 含递归链式 &lt;js&gt;…&lt;/js&gt;剩余规则，见 <see cref="PrepareRuleBody"/>）；
    /// ③ 逐字段求值（空规则或空结果 → null，但 name/author 兜底为 ""）；
    /// ④ cover/toc 两个 URL 字段绝对化；tocUrl 为空时回退详情页 URL（reader 语义）。</para>
    /// <para>name 的三层兜底（书架名 → URL 末段）在编排层 BookSourceEngine.GetBookInfoAsync，
    /// 因为需要 shelfName 入参。</para>
    /// </summary>
    public BookInfo? ParseBookInfo(
        BookSourceModel source, string body, string baseUrl, string? bookUrl = null, JsBookState? book = null)
    {
        // 详情链的书籍上下文：入参优先，其次用 bookUrl 兜底（至少让 book.bookUrl 可用）
        BeginChain(source, "bookInfo", book ?? JsBookState.FromBookUrl(bookUrl));
        var rule = source.RuleBookInfo;
        if (rule is null)
        {
            return null;
        }

        var working = body;
        // 详情页上下文先建：init 的 CSS/XPath 分支与主字段解析复用同一份 HTML/JSON 解析
        //（init 未改写原文时省掉一次整份解析；改写了再围绕新原文建上下文）。
        var ctx = new FragmentContext(body);
        if (!string.IsNullOrWhiteSpace(rule.Init))
        {
            var prepared = PrepareRuleBody(rule.Init, ctx, baseUrl);
            if (prepared.Length > 0)
            {
                working = prepared;
                ctx = new FragmentContext(working);
            }
        }

        // reader：字段规则为空或结果为空串 → None（name/author 兜底为 ""，其余保持缺失）
        string? Field(string? r)
        {
            var value = ExtractField(r, ctx, baseUrl);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        var info = new BookInfo
        {
            Name = Field(rule.Name) ?? string.Empty,
            Author = Field(rule.Author) ?? string.Empty,
            Intro = Field(rule.Intro),
            Kind = Field(rule.Kind),
            LastChapter = Field(rule.LastChapter),
            UpdateTime = Field(rule.UpdateTime),
            WordCount = Field(rule.WordCount),
            BookUrl = bookUrl ?? baseUrl,
        };

        // URL 字段走"多命中取首个"（legado getString(isUrl=true)）：封面规则
        // tag.amp-img.0@src 之类若命中多个，拼接结果不是合法 URL。
        var cover = ExtractUrl(rule.CoverUrl, ctx, baseUrl);
        info.CoverUrl = cover.Length > 0 ? cover : null;

        var toc = ExtractUrlField(rule.TocUrl, ctx, baseUrl);
        // reader：tocUrl 为空时回退详情页 URL
        info.TocUrl = toc.Length > 0 ? UrlUtil.ResolveUrl(baseUrl, toc) : (bookUrl ?? baseUrl);
        return info;
    }

    /// <summary>
    /// 目录单页解析（对标 reader rule_engine::chapter_list）。
    /// 返回 (章节列表, 下一页 URL 列表)。<c>preUpdateJs</c> 的输出替换原文，<c>init</c> 仅取副作用。
    /// <para>章节列表与下一页 URL 共用同一份片段上下文：整份 body 的 JSON/HTML 只解析一次
    ///（优化前 DetectMode 嗅探、FragmentContext、QueryNodes 各解析一次，大目录页要被解析 3~4 遍）。</para>
    /// </summary>
    public (List<TocItem> Chapters, List<string> NextUrls) ParseTocPage(
        BookSourceModel source, string body, string baseUrl, JsBookState? book = null)
    {
        // 目录链有书籍上下文（book.bookUrl / book.name / book.durChapterIndex 常用），但没有"当前章"
        BeginChain(source, "toc", book);
        var rule = source.RuleToc;
        if (rule is null)
        {
            return (new List<TocItem>(), new List<string>());
        }

        var working = PrepareTocBody(body, baseUrl, rule);
        var (listRule, reverse) = RuleAnalyzer.NormalizeListRule(rule.ChapterList ?? string.Empty);
        var (chapters, ctx) = ParseTocList(source, rule, working, baseUrl, listRule);
        var nextUrls = ParseNextTocUrls(rule, ctx, baseUrl);
        if (reverse)
        {
            chapters.Reverse();
        }

        return (chapters, nextUrls);
    }

    /// <summary>
    /// 正文解析（对标 reader rule_engine::content）：
    /// sourceRegex 裁响应体 → webJs → 前导 JS 循环 → 选择器（为空则整份 body）→ 尾随 JS → replaceRegex。
    /// </summary>
    public string ParseContent(
        BookSourceModel source, string body, string baseUrl, JsBookState? book = null, string? chapterUrl = null) =>
        ParseContentPage(source, body, baseUrl, book, chapterUrl).Text;

    /// <summary>
    /// 正文页一次性解析（正文 + 标题 + 下一页），三者共用同一份片段解析结果，
    /// 避免同一份 HTML 被解析三遍（reader 里 content/title/next 是三次独立调用）。
    /// <para>步骤（顺序即 reader 的 content 管线，不可调换）：</para>
    /// ① SourceRegex 裁剪响应体；② WebJs 后处理（输出非空才替换）；
    /// ③ content 缺失（null）→ 直接返回空正文（与"显式空串 → 整个原文"语义不同，项目踩坑点 17）；
    /// ④ 前导 JS 循环：&lt;js&gt;…&lt;/js&gt; / @js: 的输出作为新原文，直到剩余规则不再是 JS；
    /// ⑤ 选择器求值（选择器为空 → 整个原文作为正文；先展开 {{}} 模板再求值）；
    /// ⑥ 尾随 JS 处理；⑦ ReplaceRegex 清洗（JS 形态的 replaceRegex 先跑脚本）。
    /// </summary>
    internal (string Text, string Title, string? Next) ParseContentPage(
        BookSourceModel source, string body, string baseUrl, JsBookState? book = null, string? chapterUrl = null)
    {
        // 正文链作用域：有书籍上下文（book.*）+ 当前章 URL（chapter 变量 / java.put 的落点）。
        // chapterUrl 缺省用 baseUrl：正文首页 URL 就是章节 URL（分页跟随的后续页会换 baseUrl，
        // 但章节变量应挂在"这一章"，故以首次传入的 chapterUrl 为准）。
        BeginChain(source, "content", book, chapterUrl ?? baseUrl);
        var rule = source.RuleContent;
        if (rule is null)
        {
            return (string.Empty, string.Empty, null);
        }

        // ① SourceRegex：先按正则裁出正文相关区块
        var working = body;
        if (!string.IsNullOrWhiteSpace(rule.SourceRegex))
        {
            working = RuleAnalyzer.ApplyLegadoRegex(working, rule.SourceRegex);
        }

        // ② WebJs：脚本后处理（输出非空才替换原文）
        if (!string.IsNullOrWhiteSpace(rule.WebJs))
        {
            var processed = RunJs(RuleAnalyzer.StripModePrefix(rule.WebJs), working, baseUrl);
            if (processed.Trim().Length > 0)
            {
                working = processed;
            }
        }

        // ③ content 缺失（None）→ 空正文；注意与显式空串（Some("")）语义不同
        if (rule.Content is null)
        {
            return (string.Empty, ParseTitle(rule, working, baseUrl), ParseNext(rule, working, baseUrl));
        }

        var contentRule = rule.Content!;

        // ④ 前导 JS 循环：<js>…</js> / @js: 的输出作为新原文，直到剩余规则不再是 JS
        while (RuleAnalyzer.IsJsRule(contentRule))
        {
            var (output, remaining) = EvalLeadingJs(contentRule, working, baseUrl);
            working = output;
            contentRule = remaining;
            if (contentRule.Length == 0)
            {
                break;
            }
        }

        // 正文/标题/下一页共用一份片段解析上下文
        var ctx = new FragmentContext(working);

        // 先拆 ## 再拆脚本段（reader eval_field_html_single_with_ctx / ExtractContentSingle 同序）：
        // 反过来的话 <js>…</js>##pat##rep 的替换段在 ⑤⑥ 两步里被整个丢掉——
        // ExtractJs 只取到 </js> 为止，尾随的 ## 既不进脚本也不进选择器。
        var (contentRulePure, contentReplace) = RuleAnalyzer.SplitLegadoRegex(contentRule);
        var (selectorRule, script) = RuleAnalyzer.ExtractJs(contentRulePure);
        string content;
        if (selectorRule.Trim().Length == 0)
        {
            // reader：content 规则为空 → 整个原文作为正文
            content = working;
        }
        else
        {
            selectorRule = ExpandTemplates(selectorRule, ctx);
            // 选择器后面还跟着脚本时，选择器结果是脚本的 `result`：
            // JSONPath 命中对象/数组必须给**合法 JSON 文本**（光社漫畫正文
            // `$..images.images\n@js: JSON.parse(result)`、吉站 `…[*]url\n<js> result.split("\n")`）。
            content = script is not null
                ? EvalSegmentValue(selectorRule, ctx)
                : ExtractContent(selectorRule, ctx);
        }

        // ⑥ 尾随 JS：对提取结果再跑脚本
        if (script is not null && script.Trim().Length > 0)
        {
            content = RunJs(script, content, baseUrl);
        }

        // ⑥b 尾随替换段：作用于本段最终结果（legado：每个 SourceRule 的最终结果都过 replaceRegex）
        if (contentReplace is not null)
        {
            content = RuleAnalyzer.ApplyLegadoRegex(content, contentReplace);
        }

        // ⑦ ReplaceRegex：正则清洗；JS 形态的先跑脚本
        if (!string.IsNullOrWhiteSpace(rule.ReplaceRegex))
        {
            content = RuleAnalyzer.IsJsRule(rule.ReplaceRegex)
                ? RunJs(RuleAnalyzer.StripModePrefix(rule.ReplaceRegex), content, baseUrl)
                : RuleAnalyzer.ApplyLegadoRegex(content, rule.ReplaceRegex);
        }

        return (content, ParseTitle(rule, working, baseUrl, ctx), ParseNext(rule, working, baseUrl, ctx));
    }

    /// <summary>标题求值（复用正文的片段上下文；无上下文时按 body 现建）。</summary>
    private string ParseTitle(
        ContentRule rule, string body, string baseUrl, FragmentContext? ctx = null)
    {
        if (string.IsNullOrWhiteSpace(rule.Title))
        {
            return string.Empty;
        }

        return ExtractContent(rule.Title, ctx ?? new FragmentContext(body));
    }

    /// <summary>下一页 URL 求值（复用正文的片段上下文；空值 → null，非空 → 绝对化）。</summary>
    private string? ParseNext(ContentRule rule, string body, string baseUrl, FragmentContext? ctx = null)
    {
        if (string.IsNullOrWhiteSpace(rule.NextContentUrl))
        {
            return null;
        }

        // URL 字段多命中取首个（legado getString0）
        var value = ExtractUrlField(rule.NextContentUrl, ctx ?? new FragmentContext(body), baseUrl);
        return string.IsNullOrWhiteSpace(value) ? null : UrlUtil.ResolveUrl(baseUrl, value.Trim());
    }

    /// <summary>
    /// formatJs：对每条章节名再跑一次脚本（对标 reader apply_toc_format_js）。
    /// 绑定 <c>index</c>（1-based）、<c>title</c>、<c>chapter</c>；脚本输出为空则保留原名。
    /// <para>注意：formatJs 会改章节名，执行它的 Jint 引擎必须串行——编排层把它放在
    /// 所有并发抓取之后统一做（见 BookSourceEngine.ApplyFormatJs）。</para>
    /// </summary>
    public void ApplyFormatJs(
        BookSourceModel source, List<TocItem> chapters, string baseUrl, JsBookState? book = null)
    {
        var script = source.RuleToc?.FormatJs;
        if (chapters.Count == 0 || string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        // formatJs 属于目录链：同样要带 jsLib 与诊断归组（否则会沿用上一条链的 _jsSource）。
        // 章节上下文不逐条切换：每章重建一次作用域 = 每章重绑五个全局对象（2000 章会明显变慢），
        // 而 formatJs 里读 chapter 变量的写法在原始样本里为 0 例（只有 index/title 两个 globals）。
        BeginChain(source, "toc", book);

        var globals = new Dictionary<string, object?>();
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            globals["index"] = i + 1;
            globals["title"] = chapter.ChapterName ?? string.Empty;
            globals["chapter"] = chapter;
            var result = RunJs(script, chapter.ChapterName ?? string.Empty, baseUrl, globals);
            if (result.Trim().Length > 0)
            {
                chapter.ChapterName = result;
            }
        }
    }

    /// <summary>
    /// 正文图片绝对地址抽取（reader 未实现，Uviewer 阅读器需要：正文里的图要交给 ImageResourceService 渲染）。
    /// 取 src / data-src / data-original / data-url 中最先非空者。
    /// </summary>
    public static List<string> ExtractImageUrls(string html, string baseUrl)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        return CollectImages(Selectors.Load(html), baseUrl, result);
    }

    /// <summary>
    /// 在已解析的文档上收集图片 URL（去重用 HashSet，避免 &lt;img&gt; 多时 O(n²) 的 Contains 扫描）。
    /// 属性优先级：src → data-src → data-original → data-url（懒加载站点常见写法）；
    /// 每个 URL 绝对化后按首次出现顺序去重入列。
    /// </summary>
    /// <summary>
    /// 剥掉图片地址尾部行内选项（<c>,{…}</c>）。legado 用 <c>Regex("\\s*,\\s*(?=\\{)")</c> 切分
    /// 地址与请求选项；这里图片只要地址（Referer 等选项在图片下载链路上不消费）。
    /// </summary>
    private static string StripImageOptions(string url)
    {
        var idx = url.IndexOf(",{", StringComparison.Ordinal);
        return idx > 0 ? url[..idx].TrimEnd() : url;
    }

    internal static List<string> CollectImages(HtmlDocument doc, string baseUrl, List<string> result)
    {
        var nodes = doc.DocumentNode.SelectNodes("//img");
        if (nodes is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            // 属性值统一走 Selectors.AttrValue：HAP 保留源文本实体，
            // 图片 CDN 常带 query（?v=1&type=webp），不解码 &amp; 会请求到错误地址。
            var raw = FirstNonEmpty(
                Selectors.AttrValue(node, "src"),
                Selectors.AttrValue(node, "data-src"),
                Selectors.AttrValue(node, "data-original"),
                Selectors.AttrValue(node, "data-url"));
            if (raw.Length == 0)
            {
                continue;
            }

            // 图片地址允许带 legado 的行内选项块（`地址,{"headers":{...}}`，包子漫画类源就这么写）。
            // 图片是"直接取字节"，不经过 RequestSpec，故这里剥掉选项后只留纯 URL。
            var url = UrlUtil.ResolveUrl(baseUrl, StripImageOptions(raw.Trim()));
            if (url.Length > 0 && seen.Add(url))
            {
                result.Add(url);
            }
        }

        return result;

        static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v;
                }
            }

            return string.Empty;
        }
    }

    // ---- 阶段 2 内部实现 ----

    /// <summary>
    /// AllInOne 预处理（对标 reader prepare_rule_body）：
    /// ① JS 形态 → 输出替换原文并**递归**处理剩余规则（链式 &lt;js&gt;…&lt;/js&gt;剩余）；
    /// ② CSS/XPath 形态 → 取首个匹配元素的 OuterHtml 作为新原文（容器下移）；
    /// ③ 其余 → 按普通规则求值。
    /// 传入的上下文即详情页原文的解析结果，CSS/XPath 分支直接复用（不重新解析）。
    /// </summary>
    private string PrepareRuleBody(string rule, FragmentContext ctx, string baseUrl)
    {
        var mode = RuleAnalyzer.DetectMode(rule, ctx);
        if (mode == RuleMode.Js)
        {
            var (output, remaining) = EvalLeadingJs(rule, ctx.Raw, baseUrl);
            return remaining.Length == 0 ? output : PrepareRuleBody(remaining, new FragmentContext(output), baseUrl);
        }

        if (mode == RuleMode.Css || mode == RuleMode.XPath)
        {
            var frags = Selectors.ListFragmentContexts(ctx, rule);
            return frags.Count > 0 ? frags[0].Raw : string.Empty;
        }

        return ExtractContent(rule, ctx);
    }

    /// <summary>
    /// <c>tocRule.preUpdateJs</c>：<b>只取副作用，不替换原文</b>（legado <c>WebBook.runPreUpdateJs</c>：
    /// <c>AnalyzeRule(book, source, preUpdateJs = true).evalJS(preUpdateJs)</c>，返回值丢弃）。
    /// <para><b>曾经的错误</b>：把脚本输出当新原文（"输出非空就替换"）。样本里的写法全是
    /// <c>book.canUpdate=false</c>、<c>java.put(...)</c> 这类赋值，脚本值 "false" 非空 →
    /// 整份目录页被替换成 5 个字符 → 目录恒为空。包子漫画（优+）就是这么"无法获取目录"的。</para>
    /// </summary>
    private string PrepareTocBody(string body, string baseUrl, TocRule rule)
    {
        var script = rule.PreUpdateJs;
        if (string.IsNullOrWhiteSpace(script))
        {
            return body;
        }

        RunJs(RuleAnalyzer.StripModePrefix(script), body, baseUrl);
        return body;
    }

    /// <summary>
    /// 目录列表解析（对标 reader chapter_list）。步骤：
    /// ① 前导 JS 循环（仅当列表规则是 JS 形态）：输出替换原文，直到剩余规则不是 JS；
    /// ② JS 分支且剩余为空 → 兜底选择器：输出像 JSON 用 $[*]，否则 body > *
    ///    （reader 语义；注意仅 JS 分支兜底，空 chapterList 不做兜底）；
    /// ③ body 不再变化后建**一份**上下文，供章节解析与 nextTocUrl 共用；
    /// ④ 按模式派发：Json / Regex / Html（默认含 Css）。
    /// </summary>
    private (List<TocItem> Chapters, FragmentContext Ctx) ParseTocList(
        BookSourceModel source, TocRule rule, string body, string baseUrl, string listRule)
    {
        var working = body;
        var selector = listRule;

        // ① 逐段消解脚本段（legado splitSourceRule：文本段 ↔ JS 段顺序求值）。
        //    两个方向都要支持：
        //      · 脚本在前：<js>…</js>\n$.data[*].comicList[*]（快看漫画）→ 脚本改写原文，后续选择器再取条目；
        //      · 脚本在后：$.data.chapters\n@js: …map(…).返回[{text,url}]（光社漫畫）→ 文本段求值结果
        //        作为脚本的 result，脚本直接返回条目数组。
        //    旧实现只认"整条规则以 JS 开头"，后一种形态会被当成一条普通 JSONPath，
        //    路径里带着 @js: 文本 → 0 命中（"无法加载目录"的第三种形态）。
        while (true)
        {
            var (before, script, after) = RuleAnalyzer.SplitFirstJs(selector);
            if (script is null)
            {
                break;
            }

            if (before.Trim().Length == 0)
            {
                // ⓪ 纯 JS 列表规则（脚本后面没有选择器）：先按 legado getElements 语义
                //    保留脚本返回值的**列表结构**——条目可以是 java.getElements 的元素快照
                //    （包子漫画类源的 `<js>java.getElements("id.chapter-items@class.comics-chapters@a")</js>`），
                //    也可以是脚本自己 push 出来的对象数组。这一支必须先试：数组一旦被 ToString()
                //    成 "[object Object],…" 或 "a,b,c" 就不可逆，只能退化成 "body > *" 全空兜底。
                if (after.Trim().Length == 0)
                {
                    var items = RunJsElements(script, working, baseUrl);
                    if (items is { Count: > 0 })
                    {
                        // nextTocUrl 仍以"原文"为上下文（脚本可能只负责给出条目）
                        var nodeCtx = new FragmentContext(working);
                        return (BuildChaptersFromFragments(source, rule, ToFragments(items), baseUrl, nodeCtx), nodeCtx);
                    }
                }

                working = RunJs(script, working, baseUrl);
                selector = after;
                if (selector.Trim().Length == 0)
                {
                    // reader：JS 输出非 JSON 时用 body > * 兜底（仅 JS 分支，空 chapterList 不兜底）
                    selector = IsJsonLike(working) ? "$[*]" : "body > *";
                }

                continue;
            }

            // 文本段在前：它的求值结果就是脚本的 result（src 仍是原文）
            var pre = EvalSegmentValue(before.Trim(), new FragmentContext(working));
            if (after.Trim().Length == 0)
            {
                var items = RunJsElements(script, pre, baseUrl, working);
                if (items is { Count: > 0 })
                {
                    var nodeCtx = new FragmentContext(working);
                    return (BuildChaptersFromFragments(source, rule, ToFragments(items), baseUrl, nodeCtx), nodeCtx);
                }

                // 脚本只是改写原文（没返回数组）：按改写后的内容重新走一遍
                var rewritten = RunJs(script, pre, baseUrl, null, working);
                return ParseTocList(
                    source, rule, rewritten, baseUrl, IsJsonLike(rewritten) ? "$[*]" : "body > *");
            }

            working = RunJs(script, pre, baseUrl, null, working);
            selector = after;
        }

        // JS 循环结束后 body 不再变化：建一份上下文供章节解析与 nextTocUrl 共用
        var ctx = new FragmentContext(working);

        // @put:{k:rule} 先落变量并从选择器里剥掉（吉站漫画 `$..chapters[*]@put:{bid:$.data.id}`）
        selector = ApplyPutRule(selector.Trim(), ctx);

        var mode = RuleAnalyzer.DetectMode(selector, ctx);
        var chapters = mode switch
        {
            RuleMode.JsonPath => ParseTocJson(source, rule, ctx, baseUrl, selector),
            RuleMode.Regex => ParseTocRegex(source, rule, working, baseUrl, selector),
            // CSS/HTML：走片段合并（支持 chapterList 里的 && / || / %% 组合符）
            _ => BuildChaptersFromFragments(source, rule, TocFragments(selector, ctx, baseUrl), baseUrl, ctx),
        };

        return (chapters, ctx);
    }

    /// <summary>脚本条目字符串 → 片段（跳过空白项）。</summary>
    private static List<FragmentContext> ToFragments(IReadOnlyList<string> items)
    {
        var frags = new List<FragmentContext>(items.Count);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                frags.Add(new FragmentContext(item));
            }
        }

        return frags;
    }

    /// <summary>
    /// 多段规则里"文本段"的求值（供后续脚本当 <c>result</c> 用）：
    /// JSONPath 命中**对象/数组**时返回合法 JSON 文本（脚本里 <c>JSON.parse(result)</c> 才成立），
    /// 命中的全是字符串/数字时按 legado <c>getString</c> 语义用 <c>\n</c> 拼接
    /// （<c>result.split("\n")</c> 这类写法才成立）；非 JSONPath 走常规求值。
    /// </summary>
    private string EvalSegmentValue(string rule, FragmentContext ctx)
    {
        var declared = RuleAnalyzer.TryDetectModeFromRule(rule);
        var isJsonPath = declared == RuleMode.JsonPath
            || (declared is null && ctx.IsJson);
        if (!isJsonPath || ctx.Json is null)
        {
            return ExtractContent(rule, ctx);
        }

        var nodes = JsonPathEval.QueryNodes(ctx.Json, RuleAnalyzer.StripModePrefix(rule));
        if (nodes.Count == 0)
        {
            return string.Empty;
        }

        if (nodes.All(n => n is System.Text.Json.Nodes.JsonValue))
        {
            return string.Join("\n", nodes.Select(n => n?.ToString() ?? string.Empty));
        }

        if (nodes.Count == 1 && nodes[0] is System.Text.Json.Nodes.JsonArray single)
        {
            return single.ToJsonString();
        }

        return "[" + string.Join(",", nodes.Select(n => n?.ToJsonString() ?? "null")) + "]";
    }

    /// <summary>
    /// HTML 目录解析。步骤：
    /// ① chapterList 为空 → 直接空（reader：HTML 模式不做整文档兜底）；
    /// ② init 在文档级执行一次（仅副作用，如 java.put，返回值丢弃）；
    /// ③ 列表选择器取活节点片段；④ 逐片段 BuildChapter；⑤ 无标题条目跳过、按 URL 去重。
    /// </summary>
    private List<TocItem> ParseTocHtml(
        BookSourceModel source, TocRule rule, FragmentContext ctx, string baseUrl, string listRule)
    {
        var result = new List<TocItem>();
        if (listRule.Trim().Length == 0)
        {
            // reader：HTML 模式 chapterList 为空直接返回空，不做整文档兜底
            return result;
        }

        // init 在文档级执行一次（仅副作用，返回值丢弃）
        if (!string.IsNullOrWhiteSpace(rule.Init))
        {
            ExtractField(rule.Init, ctx, baseUrl);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Selectors.ListFragmentContexts(ctx, listRule))
        {
            var chapter = BuildChapter(source, rule, f, baseUrl, seen, result.Count);
            if (chapter is not null)
            {
                result.Add(chapter);
            }
        }

        return result;
    }

    /// <summary>
    /// JSON 目录解析。步骤：
    /// ① body 不是 JSON → 空；② init 文档级执行一次（仅副作用）；
    /// ③ chapterList 为空或 $ → 根数组逐元素 / 根本身单条目；
    /// ④ 否则 JSONPath 取节点列表（复用已解析根节点，不二次解析）；
    /// ⑤ 逐节点 BuildChapter（Raw 惰性，纯 JSONPath 字段不付序列化开销）。
    /// </summary>
    private List<TocItem> ParseTocJson(
        BookSourceModel source, TocRule rule, FragmentContext ctx, string baseUrl, string listRule)
    {
        var result = new List<TocItem>();
        if (ctx.Json is null)
        {
            return result;
        }

        // init 在文档级执行一次（仅副作用）
        if (!string.IsNullOrWhiteSpace(rule.Init))
        {
            ExtractField(rule.Init, ctx, baseUrl);
        }

        List<JsonNode?> nodes;
        var path = RuleAnalyzer.StripModePrefix(listRule);
        if (path.Trim().Length == 0 || path == "$")
        {
            nodes = ctx.Json is JsonArray array
                ? array.Select(n => (JsonNode?)n).ToList()
                : new List<JsonNode?> { ctx.Json };
        }
        else
        {
            // 复用整份 body 已解析的 JSON 根节点，不再二次解析
            nodes = JsonPathEval.QueryNodes(ctx.Json, path);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null)
            {
                continue;
            }

            var chapter = BuildChapter(source, rule, new FragmentContext(node), baseUrl, seen, result.Count);
            if (chapter is not null)
            {
                result.Add(chapter);
            }
        }

        return result;
    }

    /// <summary>
    /// 正则目录解析：每个匹配的整个匹配文本作为章节片段（可选替换段先作用），
    /// 再逐片段 BuildChapter。与 <see cref="SearchBooksRegex"/> 同构，仅产物是 TocItem。
    /// </summary>
    private List<TocItem> ParseTocRegex(
        BookSourceModel source, TocRule rule, string body, string baseUrl, string listRule)
    {
        var result = new List<TocItem>();
        var (pattern, replacePart) = SplitRegexRule(RuleAnalyzer.StripModePrefix(listRule));
        var regex = RuleAnalyzer.GetRegex(pattern);
        if (regex is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in regex.Matches(body))
        {
            var value = m.Value;
            if (replacePart is not null)
            {
                value = RuleAnalyzer.ApplyLegadoRegex(value, replacePart);
            }

            var chapter = BuildChapter(source, rule, new FragmentContext(value), baseUrl, seen, result.Count);
            if (chapter is not null)
            {
                result.Add(chapter);
            }
        }

        return result;
    }

    /// <summary>
    /// 单章节构建（对标 reader build_chapter）。步骤：
    /// ① 章节名（空 → 跳过该条目，reader 语义）；② 章节 URL（空 → 走伪 URL 兜底）；
    /// ③ isVolume 真值判定；④ 绝对化 URL 后按 URL 去重（reader：按绝对 URL 去重）；
    /// ⑤ isVip / isPay 真值判定 + updateTime 取文本。
    /// </summary>
    private TocItem? BuildChapter(
        BookSourceModel source, TocRule rule, FragmentContext ctx, string baseUrl, HashSet<string> seen, int index)
    {
        var title = ExtractField(rule.ChapterName, ctx, baseUrl).Trim();
        if (title.Length == 0)
        {
            // reader：无标题的条目直接跳过
            return null;
        }

        // URL 字段多命中取首个（legado getString0）：`tag.a@href` 常同时命中卡片外层与内层
        var rawUrl = ExtractUrlField(rule.ChapterUrl, ctx, baseUrl).Trim();
        var isVolume = RuleAnalyzer.IsTruthy(ExtractField(rule.IsVolume, ctx, baseUrl));
        var url = FinalizeChapterUrl(baseUrl, rawUrl, title, isVolume, index);
        if (!seen.Add(url))
        {
            // reader：按绝对 URL 去重
            return null;
        }

        return new TocItem
        {
            ChapterName = title,
            ChapterUrl = url,
            IsVolume = isVolume,
            IsVip = RuleAnalyzer.IsTruthy(ExtractField(rule.IsVip, ctx, baseUrl)),
            IsPay = RuleAnalyzer.IsTruthy(ExtractField(rule.IsPay, ctx, baseUrl)),
            UpdateTime = ExtractField(rule.UpdateTime, ctx, baseUrl),
        };
    }

    /// <summary>
    /// 章节 URL 兜底（对标 reader finalize_chapter_url）：
    /// 有原始 URL → 绝对化；无 URL 的卷 → title+index 伪 URL（保证去重键唯一）；
    /// 无 URL 的普通章节 → 目录页 URL 自身（reader 语义）。
    /// </summary>
    private static string FinalizeChapterUrl(
        string baseUrl, string rawUrl, string title, bool isVolume, int index)
    {
        if (rawUrl.Length > 0)
        {
            return UrlUtil.ResolveUrl(baseUrl, rawUrl);
        }

        return isVolume ? title + index.ToString(CultureInfo.InvariantCulture) : baseUrl;
    }

    /// <summary>
    /// 下一页目录 URL 解析（对标 reader next_toc_url）。要点：
    /// ① 未配 nextTocUrl → 必须给空（空选择器会凭空产出一个 next，reader 特意规避）；
    /// ② JS / Regex 模式 → 恒返回空（reader 语义：这两种模式的 next 无意义）；
    /// ③ JSONPath / CSS 取值 → 逐值绝对化；④ 空值跳过。
    /// </summary>
    private List<string> ParseNextTocUrls(TocRule rule, FragmentContext ctx, string baseUrl)
    {
        var nextRule = (rule.NextTocUrl ?? string.Empty).Trim();
        if (nextRule.Length == 0)
        {
            // reader：没配 nextTocUrl 必须给空，否则空选择器会凭空产出一个 next
            return new List<string>();
        }

        var mode = RuleAnalyzer.DetectMode(nextRule, ctx);
        if (mode is RuleMode.Js or RuleMode.Regex)
        {
            // reader：JS / Regex 模式的 nextTocUrl 恒返回空
            return new List<string>();
        }

        var values = mode == RuleMode.JsonPath
            ? JsonPathEval.Query(ctx.Json, RuleAnalyzer.StripModePrefix(nextRule))
            : Selectors.FieldValues(ctx, nextRule);

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result.Add(UrlUtil.ResolveUrl(baseUrl, value.Trim()));
        }

        return result;
    }

    /// <summary>前导 JS 段求值：切分 → 跑脚本 → 返回 (输出, 剩余规则)。</summary>
    private (string Output, string Remaining) EvalLeadingJs(string rule, string body, string baseUrl)
    {
        // 先拆 ## 再拆脚本段（reader eval_field_html_single_with_ctx 同序）：
        // 反过来的话 @js: 形态会把 "##pat##rep" 一起吃进脚本文本 → JS 语法错误 →
        // RunJs 兜底返回原始 body → 整条正文规则静默退化成原文（旧实现就是这么死的）。
        var (ruleWithoutReplace, replacePart) = RuleAnalyzer.SplitLegadoRegex(rule);
        var (script, remaining) = SplitLeadingJs(ruleWithoutReplace);
        var output = RunJs(script, body, baseUrl);

        if (replacePart is null)
        {
            return (output, remaining);
        }

        if (remaining.Length == 0)
        {
            // 整条规则 = "脚本 + 替换"：替换作用于脚本输出（legado：JS 也是 SourceRule，
            // replaceRegex 对最终结果生效）
            return (RuleAnalyzer.ApplyLegadoRegex(output, replacePart), remaining);
        }

        // 脚本后面还有选择器：替换段拼回剩余规则，由后续求值在**最终结果**上结算
        // （提前应用会让选择器跑在已替换的文本上，顺序错）
        return (output, remaining + replacePart);
    }

    /// <summary>将相对 URL 转为绝对地址（对标 reader resolve_url：保留 query、去 fragment）。</summary>
    private static string MakeAbsolute(string url, string baseUrl) => UrlUtil.ResolveUrl(baseUrl, url);
}

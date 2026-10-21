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
    /// 记录"当前在跑哪个书源的哪条链"（诊断归组用）。
    /// <para>链名固定为 search / bookInfo / toc / content：粒度到链而不是到字段，
    /// 因为 <see cref="RunJs"/> 是唯一出口，逐字段传参会把签名污染到十几处。</para>
    /// </summary>
    private void BeginChain(BookSourceModel source, string field)
    {
        _jsSource = source;
        _js.SourceKey = source.BookSourceUrl;
        _js.Field = field;
    }

    // 模板正则常量：每次展开 {{...}} 都会用到，缓存并预编译。
    private static readonly Regex TemplateRegex = new(@"\{\{(.*?)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// 搜索入口（对标 reader search_books）。步骤：
    /// ① 无 ruleSearch → 空列表；② 列表规则归一化（- 前缀 = 倒序）；
    /// ③ 走列表解析；④ 按需倒序（reader：reverse 在拼接后生效）。
    /// </summary>
    public List<SearchResult> SearchBooks(BookSourceModel source, string body, string baseUrl)
    {
        // 记录当前书源与链名：RunJs 需要它的 jsLib（jsLib 注入）+ 诊断归组
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

        var bookUrl = ExtractField(rule.BookUrl, ctx, baseUrl);
        var coverUrl = ExtractField(rule.CoverUrl, ctx, baseUrl);
        book.BookUrl = MakeAbsolute(bookUrl, baseUrl);
        book.CoverUrl = MakeAbsolute(coverUrl, baseUrl);
        return book;
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

        return rule.Contains("{{", StringComparison.Ordinal)
            ? ExpandTemplates(rule, ctx)
            : ExtractContent(rule, ctx);
    }

    /// <summary>
    /// 展开 legado 结果模板 <c>{{rule}}</c>：把内部规则在当前片段上求值后内联回字符串。
    /// 例如 bookUrl <c>/novel/{{$.novelId}}?isSearch=1</c> 会把 <c>$.novelId</c> 替换为当前条目的值。
    /// 仅当字符串含 <c>{{</c> 时生效，避免无谓开销。
    /// </summary>
    private string ExpandTemplates(string value, FragmentContext ctx)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("{{", StringComparison.Ordinal))
        {
            return value;
        }

        return TemplateRegex.Replace(value, m =>
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
        if (RuleAnalyzer.IsJsRule(rule))
        {
            return ExtractContentSingle(rule, ctx);
        }

        var (combinable, replacePart) = RuleAnalyzer.SplitLegadoRegex(rule);
        var combo = RuleAnalyzer.SplitTopLevel(combinable, new[] { "&&", "||", "%%" });
        if (combo.Delimiter is not null)
        {
            var values = combo.Parts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => ExtractContentSingle(p, ctx))
                .ToList();

            var joined = combo.Delimiter switch
            {
                "&&" => string.Concat(values.Where(v => v.Length > 0)),
                "||" => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty,
                "%%" => string.Concat(values.Where(v => v.Length > 0)),
                _ => ExtractContentSingle(rule, ctx),
            };

            return replacePart is not null ? RuleAnalyzer.ApplyLegadoRegex(joined, replacePart) : joined;
        }

        return ExtractContentSingle(rule, ctx);
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
        // 选择器 + 脚本段：先按选择器取值，再交给脚本处理。
        var (selectorRule, script) = RuleAnalyzer.ExtractJs(rule);
        if (script is not null)
        {
            var value = string.IsNullOrEmpty(selectorRule)
                ? ctx.Raw
                : ExtractContentSingle(selectorRule, ctx);
            return RunJs(script, value, string.Empty);
        }

        var (combinable, replacePart) = RuleAnalyzer.SplitLegadoRegex(rule);
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
    /// </summary>
    private string RunJs(
        string script,
        string value,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? globals = null)
    {
        var ctx = new FragmentContext(value);
        _js.EvaluateRule = r => ExtractContent(r, ctx);

        // jsLib 透传（便宜：字符串引用赋值；JsBridge 内部按文本决定是否重新加载库）
        _js.JsLib = _jsSource?.JsLib;
        return _js.Run(script, value, baseUrl, globals);
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
    public BookInfo? ParseBookInfo(BookSourceModel source, string body, string baseUrl, string? bookUrl = null)
    {
        BeginChain(source, "bookInfo");
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

        var cover = Field(rule.CoverUrl);
        if (cover is not null)
        {
            info.CoverUrl = UrlUtil.ResolveUrl(baseUrl, cover);
        }

        var toc = Field(rule.TocUrl);
        // reader：tocUrl 为空时回退详情页 URL
        info.TocUrl = toc is not null ? UrlUtil.ResolveUrl(baseUrl, toc) : (bookUrl ?? baseUrl);
        return info;
    }

    /// <summary>
    /// 目录单页解析（对标 reader rule_engine::chapter_list）。
    /// 返回 (章节列表, 下一页 URL 列表)。<c>preUpdateJs</c> 的输出替换原文，<c>init</c> 仅取副作用。
    /// <para>章节列表与下一页 URL 共用同一份片段上下文：整份 body 的 JSON/HTML 只解析一次
    ///（优化前 DetectMode 嗅探、FragmentContext、QueryNodes 各解析一次，大目录页要被解析 3~4 遍）。</para>
    /// </summary>
    public (List<TocItem> Chapters, List<string> NextUrls) ParseTocPage(
        BookSourceModel source, string body, string baseUrl)
    {
        BeginChain(source, "toc");
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
    public string ParseContent(BookSourceModel source, string body, string baseUrl) =>
        ParseContentPage(source, body, baseUrl).Text;

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
        BookSourceModel source, string body, string baseUrl)
    {
        BeginChain(source, "content");
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
        var (selectorRule, script) = RuleAnalyzer.ExtractJs(contentRule);
        string content;
        if (selectorRule.Trim().Length == 0)
        {
            // reader：content 规则为空 → 整个原文作为正文
            content = working;
        }
        else
        {
            selectorRule = ExpandTemplates(selectorRule, ctx);
            content = ExtractContent(selectorRule, ctx);
        }

        // ⑥ 尾随 JS：对提取结果再跑脚本
        if (script is not null && script.Trim().Length > 0)
        {
            content = RunJs(script, content, baseUrl);
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

        var value = ExtractContent(rule.NextContentUrl, ctx ?? new FragmentContext(body));
        return string.IsNullOrWhiteSpace(value) ? null : UrlUtil.ResolveUrl(baseUrl, value.Trim());
    }

    /// <summary>
    /// formatJs：对每条章节名再跑一次脚本（对标 reader apply_toc_format_js）。
    /// 绑定 <c>index</c>（1-based）、<c>title</c>、<c>chapter</c>；脚本输出为空则保留原名。
    /// <para>注意：formatJs 会改章节名，执行它的 Jint 引擎必须串行——编排层把它放在
    /// 所有并发抓取之后统一做（见 BookSourceEngine.ApplyFormatJs）。</para>
    /// </summary>
    public void ApplyFormatJs(BookSourceModel source, List<TocItem> chapters, string baseUrl)
    {
        var script = source.RuleToc?.FormatJs;
        if (chapters.Count == 0 || string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        // formatJs 属于目录链：同样要带 jsLib 与诊断归组（否则会沿用上一条链的 _jsSource）
        BeginChain(source, "toc");

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
            var raw = FirstNonEmpty(
                node.GetAttributeValue("src", string.Empty),
                node.GetAttributeValue("data-src", string.Empty),
                node.GetAttributeValue("data-original", string.Empty),
                node.GetAttributeValue("data-url", string.Empty));
            if (raw.Length == 0)
            {
                continue;
            }

            var url = UrlUtil.ResolveUrl(baseUrl, raw.Trim());
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

    /// <summary>preUpdateJs：输出非空则替换原文（对标 reader prepare_toc_body）。</summary>
    private string PrepareTocBody(string body, string baseUrl, TocRule rule)
    {
        var script = rule.PreUpdateJs;
        if (string.IsNullOrWhiteSpace(script))
        {
            return body;
        }

        var result = RunJs(RuleAnalyzer.StripModePrefix(script), body, baseUrl);
        return result.Trim().Length > 0 ? result : body;
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

        // 前导 JS 循环：JS 输出替换原文（此阶段还没有可复用的解析结果，按字符串判定）
        var jsLeading = RuleAnalyzer.IsJsRule(selector);
        if (jsLeading)
        {
            while (RuleAnalyzer.IsJsRule(selector))
            {
                var (output, remaining) = EvalLeadingJs(selector, working, baseUrl);
                working = output;
                selector = remaining;
                if (selector.Length == 0)
                {
                    break;
                }
            }

            if (selector.Trim().Length == 0)
            {
                // reader：JS 输出非 JSON 时用 body > * 兜底（仅 JS 分支，空 chapterList 不兜底）
                selector = IsJsonLike(working) ? "$[*]" : "body > *";
            }
        }

        // JS 循环结束后 body 不再变化：建一份上下文供章节解析与 nextTocUrl 共用
        var ctx = new FragmentContext(working);

        var chapters = RuleAnalyzer.DetectMode(selector, ctx) switch
        {
            RuleMode.JsonPath => ParseTocJson(source, rule, ctx, baseUrl, selector),
            RuleMode.Regex => ParseTocRegex(source, rule, working, baseUrl, selector),
            _ => ParseTocHtml(source, rule, ctx, baseUrl, selector),
        };

        return (chapters, ctx);
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

        var rawUrl = ExtractField(rule.ChapterUrl, ctx, baseUrl).Trim();
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
        var (script, remaining) = SplitLeadingJs(rule);
        return (RunJs(script, body, baseUrl), remaining);
    }

    /// <summary>将相对 URL 转为绝对地址（对标 reader resolve_url：保留 query、去 fragment）。</summary>
    private static string MakeAbsolute(string url, string baseUrl) => UrlUtil.ResolveUrl(baseUrl, url);
}

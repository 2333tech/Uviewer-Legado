using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 规则解析模式（对标 reader ParseMode）。
/// Default 在 detect 阶段即归并为 Css（reader 同：无前缀规则按 CSS 处理）。
/// </summary>
internal enum RuleMode
{
    /// <summary>CSS 选择器（@css: 或无前缀的默认形态）。</summary>
    Css,

    /// <summary>XPath（@xpath: 或 / ./ 开头的规则）。</summary>
    XPath,

    /// <summary>JSONPath（@json: 或 $. 开头，或内容为 JSON 时的无前缀规则）。</summary>
    JsonPath,

    /// <summary>正则（@regex: 或 : 开头的规则）。</summary>
    Regex,

    /// <summary>JS 脚本（js: / @js: / &lt;js&gt; 开头）。</summary>
    Js,

    /// <summary>未显式指定（detect 时归并，不会作为最终结果出现）。</summary>
    Default,
}

/// <summary>
/// 规则分析静态工具：模式识别、组合符拆分、<c>##</c> 行内替换、模式前缀剥离、JS 段提取。
/// 逐方法同构移植 reader/src/parser/rule_analyzer.rs 与 rule_engine.rs。
/// <para>本类是规则求值的热路径：每条字段规则 × 每个片段都会走
/// DetectMode / SplitTopLevel / SplitLegadoRegex，故内部实现需避免逐字符堆分配
/// （见 FindNextDelimiter 的 span 化）。</para>
/// </summary>
internal static class RuleAnalyzer
{
    /// <summary>
    /// 模式识别（对标 reader detect_mode）。
    /// 判定优先级：显式前缀 &gt; 规则前缀（/ ./ $. $[ :）&gt; 内容前缀（JSON）&gt; 默认 Css。
    /// <para><b>内容嗅探是惰性的</b>：绝大多数规则带前缀（<c>@css:</c>/<c>/</c>/<c>$.</c>），
    /// 此时模式在规则文本上已经确定，<b>完全不碰内容</b>。这一点很关键——
    /// 内容嗅探要 Trim + 尝试解析整份 body，几百 KB 的大目录页会被白白复制/解析一遍。
    /// 只有"无前缀规则"才需要嗅探，且嗅探本身用 span 首字符 O(1) 拒绝 HTML。</para>
    /// </summary>
    public static RuleMode DetectMode(string rule, string content) =>
        TryDetectModeFromRule(rule) ?? (LooksLikeJson(content) ? RuleMode.JsonPath : RuleMode.Css);

    /// <summary>
    /// 热路径重载：内容是否为 JSON 由片段上下文缓存（整个片段最多解析一次），
    /// 省掉每条字段重复的 Trim + JsonDocument.Parse 嗅探。
    /// </summary>
    internal static RuleMode DetectMode(string rule, FragmentContext ctx) =>
        DetectModeCore(rule, ctx.IsJson);

    /// <summary>
    /// 模式识别核心（contentIsJson 由调用方提供，见两个重载）。
    /// 规则自身能判定时走 <see cref="TryDetectModeFromRule"/>（不读内容）。
    /// </summary>
    internal static RuleMode DetectModeCore(string rule, bool contentIsJson) =>
        TryDetectModeFromRule(rule) ?? (contentIsJson ? RuleMode.JsonPath : RuleMode.Css);

    /// <summary>
    /// 仅凭规则文本判定模式；无法判定（无任何前缀）时返回 <c>null</c>，交由调用方嗅探内容。
    /// <para>顺序即优先级，不可调换：</para>
    /// ① 空规则 → Css（reader 同；调用方多数会先判空，这里是防御）；
    /// ② 显式前缀 @css:/@xpath:/@json:/@regex:/js:/@js:/&lt;js&gt;（大小写不敏感）；
    /// ③ 形态前缀：/ 或 ./ → XPath；$. 或 $[ → JsonPath；: → Regex；
    /// ④ 都不命中 → null（需要内容嗅探）。
    /// </summary>
    internal static RuleMode? TryDetectModeFromRule(string rule)
    {
        var r = rule.AsSpan().Trim();
        if (r.Length == 0)
        {
            return RuleMode.Css;
        }

        // ② 显式前缀（OrdinalIgnoreCase：@CSS: 等大小写变体都认）
        if (r.StartsWith("@css:", StringComparison.OrdinalIgnoreCase)) return RuleMode.Css;
        if (r.StartsWith("@xpath:", StringComparison.OrdinalIgnoreCase)) return RuleMode.XPath;
        if (r.StartsWith("@json:", StringComparison.OrdinalIgnoreCase)) return RuleMode.JsonPath;
        if (r.StartsWith("@regex:", StringComparison.OrdinalIgnoreCase)) return RuleMode.Regex;
        if (r.StartsWith("js:", StringComparison.Ordinal)
            || r.StartsWith("@js:", StringComparison.Ordinal)
            || r.StartsWith("<js>", StringComparison.Ordinal))
        {
            return RuleMode.Js;
        }

        // ③ 形态前缀（Ordinal：legado 规则大小写敏感）
        if (r.StartsWith("/", StringComparison.Ordinal) || r.StartsWith("./", StringComparison.Ordinal)) return RuleMode.XPath;
        if (r.StartsWith("$.", StringComparison.Ordinal) || r.StartsWith("$[", StringComparison.Ordinal)) return RuleMode.JsonPath;
        if (r.StartsWith(":", StringComparison.Ordinal)) return RuleMode.Regex;

        // ④ 无前缀：需要内容嗅探
        return null;
    }

    /// <summary>
    /// 规则是否为 JS 形态（<c>js:</c> / <c>@js:</c> / <c>&lt;js&gt;</c> 开头）。
    /// <para><b>只看规则文本，不看内容</b>：JS 模式<b>只能</b>由前缀判定——
    /// 内容嗅探的两种产物只有 JsonPath 与 Css，永远不会得到 Js。
    /// 因此凡是"判断是不是 JS"的地方都不该为此去嗅探内容（旧实现因此在
    /// 每一章正文上多做一次整串 JSON 解析）。</para>
    /// </summary>
    public static bool IsJsRule(string rule) => TryDetectModeFromRule(rule) == RuleMode.Js;

    /// <summary>
    /// 内容是否为 JSON：首字符嗅探（O(1)、<b>零分配</b>）+ 全量解析验证。
    /// 形似 JSON 但解析失败（如截断的响应体）按非 JSON 处理——调用方随后按 HTML/文本规则求值。
    /// <para>与 <see cref="FragmentContext"/> 的 JSON 探测同源：都先看首个非空白字符，
    /// 避免对大文本先做一次 <c>Trim()</c> 复制（旧实现每条无前缀规则都会复制整份 body）。</para>
    /// </summary>
    private static bool LooksLikeJson(string content)
    {
        var head = content.AsSpan().Trim();
        if (head.Length == 0 || (head[0] != '{' && head[0] != '['))
        {
            return false;
        }

        try
        {
            // JSON 允许前后空白，直接解析原串（不再为 Trim 复制一份副本）
            using var _ = System.Text.Json.JsonDocument.Parse(content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 正则缓存：同一条书源规则会在 N 个片段（书/章节）上反复求值，
    /// 逐次 new Regex(...) 的编译开销远大于命中缓存的字典查找。
    /// 有界（上限 RegexCacheLimit）防止不同书源的海量规则把缓存撑爆（内存护栏）。
    /// ConcurrentDictionary 保证并行搜索下的线程安全；重复 TryAdd 的竞态无害。
    /// </summary>
    private const int RegexCacheLimit = 512;

    private static readonly ConcurrentDictionary<string, Regex> RegexCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 取缓存的正则；非法正则返回 null（调用方按原样返回内容，不中断解析）。
    /// <para>缓存未命中时编译（Singleline：legado 规则里的 . 要匹配换行，正文提取依赖此点），
    /// 入缓存受 <see cref="RegexCacheLimit"/> 约束——超限后仍返回可用实例，只是不再缓存。</para>
    /// </summary>
    internal static Regex? GetRegex(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            // 空 pattern 在 .NET 是合法正则且在每个位置命中空串：规则写成
            // "@regex:" / "@regex:##x##"（前缀剥完就没了）时，一次求值会产生
            // N+1 个空匹配——大目录页上瞬间分配数十万个 FragmentContext，
            // java.getStringList 也会拿到 N+1 个空串。按"无规则"处理返回 null
            // （EvaluateRegexRule/SearchBooksRegex 早有同样的空判，这里统一到入口）。
            return null;
        }

        if (RegexCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        Regex compiled;
        try
        {
            compiled = new Regex(pattern, RegexOptions.Singleline);
        }
        catch
        {
            // 非法正则：返回 null（reader 的 regex 编译失败只记日志）
            return null;
        }

        if (RegexCache.Count < RegexCacheLimit)
        {
            RegexCache[pattern] = compiled;
        }

        return compiled;
    }

    // 模式前缀表：StripModePrefix 在每条规则/每个字段上都会走，
    // 表本身缓存为静态只读（每次调用 new 一份数组的开销虽小，但这是热路径）。
    // 大小写不敏感（与 TryDetectModeFromRule 的前缀判定口径一致）：
    // @CSS:/@Json: 等变体既被识别为对应模式，就必须能被剥掉——否则前缀残留
    // 在选择器里，QuerySelectorAll 抛异常被 catch 吞成空结果，表现为
    // "模式识别成功但规则静默失效"。旧实现用 Ordinal + 人工变体表，
    // 表外的大小写组合（@Css:/@Xpath:/@REGEX:）就会漏剥。
    private static readonly string[] ModePrefixesIgnoreCase =
    {
        "@css:", "@xpath:", "@json:", "@regex:",
    };

    // js:/@js: 保持 Ordinal：TryDetectModeFromRule 的 JS 分支就是大小写敏感的
    // （@JS: 在 legado 与 reader 都不算 JS 前缀），剥不剥必须与识别保持同一口径。
    private static readonly string[] ModePrefixesOrdinal =
    {
        "@js:", "js:",
    };

    /// <summary>
    /// 剥离模式前缀（<c>@css:</c> / <c>&lt;js&gt;…&lt;/js&gt;</c> 等），返回纯规则文本。
    /// <para>两种特殊形态优先处理：① 整条被 &lt;js&gt;…&lt;/js&gt; 包裹 → 取内部脚本；
    /// ② 命中前缀表任一前缀 → 剥掉。都不命中 → 原样返回（已 Trim）。</para>
    /// </summary>
    public static string StripModePrefix(string rule)
    {
        var r = rule.Trim();
        if (r.StartsWith("<js>", StringComparison.Ordinal) && r.EndsWith("</js>", StringComparison.Ordinal))
        {
            return r.Substring(4, r.Length - 9).Trim();
        }

        foreach (var p in ModePrefixesIgnoreCase)
        {
            if (r.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return r.Substring(p.Length).Trim();
            }
        }

        foreach (var p in ModePrefixesOrdinal)
        {
            if (r.StartsWith(p, StringComparison.Ordinal))
            {
                return r.Substring(p.Length).Trim();
            }
        }

        return r;
    }

    /// <summary>
    /// 顶层组合符拆分（对标 split_top_level + find_next_delimiter）。
    /// 在括号/引号/&lt;js&gt; 段内部不拆分；返回各段与使用的分隔符。
    /// <para>算法：① 全文找**第一个**顶层分隔符，确定分隔符类型；
    /// ② 按该分隔符逐段切（每段 Trim）。找不到分隔符时返回"整条为一段 + delimiter=null"，
    /// 调用方据此走单规则分支。</para>
    /// </summary>
    public static (string[] Parts, string? Delimiter) SplitTopLevel(string rule, string[] delimiters)
    {
        var (idx, delim) = FindNextDelimiter(rule, delimiters, 0);
        if (delim is null)
        {
            return (new[] { rule.Trim() }, null);
        }

        // 预分配容量：段数 ≈ 分隔符出现次数 + 1（上限值，宁多勿少）
        var parts = new List<string>(8);
        var start = 0;
        var searchFrom = 0;
        var single = new[] { delim };
        while (FindNextDelimiter(rule, single, searchFrom) is var t && t.Item1 >= 0)
        {
            var (i, _) = t;
            parts.Add(rule.Substring(start, i - start).Trim());
            start = i + delim.Length;
            searchFrom = start;
        }

        parts.Add(rule.Substring(start).Trim());
        return (parts.ToArray(), delim);
    }

    /// <summary>
    /// 从 <paramref name="from"/> 起找第一个顶层分隔符（对标 find_next_delimiter）。
    /// <para>"顶层"判定（四个计数器 + 两个状态位）：</para>
    /// <list type="number">
    /// <item>square/paren/brace：[]、()、{} 深度，非 0 时在嵌套内，不切；</item>
    /// <item>quote：引号内不切（支持 \ 转义）；</item>
    /// <item>inJs：&lt;js&gt;…&lt;/js&gt; 段内不切（JS 源码里的 || 是逻辑或，不是组合符）；</item>
    /// <item>@ 特判：分隔符 "@" 不能切出 "@@"（链式选择器 @@class.tag@text 是单一形态）。</item>
    /// </list>
    /// <para>实现要点：逐字符状态机 + span 前缀比较（原实现的逐字符 Substring
    /// 是每条字段 × 每个片段上一次堆分配，已 span 化）。</para>
    /// </summary>
    private static (int Index, string? Delimiter) FindNextDelimiter(string rule, string[] delimiters, int from)
    {
        var square = 0;
        var paren = 0;
        var brace = 0;
        char? quote = null;
        var escaped = false;
        var inJs = false;
        var span = rule.AsSpan(from);

        for (var idx = 0; idx < span.Length; idx++)
        {
            var c = span[idx];

            // ① <js> 段内：只跟踪 </js> 闭合，其余一切（含引号括号）都是脚本内容
            if (inJs)
            {
                if (span.Slice(idx).StartsWith("</js>", StringComparison.Ordinal))
                {
                    inJs = false;
                }

                continue;
            }

            if (span.Slice(idx).StartsWith("<js>", StringComparison.Ordinal))
            {
                inJs = true;
                quote = null;
                escaped = false;
                continue;
            }

            // ② 引号内：跟踪转义与闭合引号
            if (quote.HasValue)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == quote.Value)
                {
                    quote = null;
                }

                continue;
            }

            // ③ 引号外：更新括号深度与转义状态
            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    continue;
                case '[':
                    square++;
                    continue;
                case ']':
                    square--;
                    continue;
                case '(':
                    paren++;
                    continue;
                case ')':
                    paren--;
                    continue;
                case '{':
                    brace++;
                    continue;
                case '}':
                    brace--;
                    continue;
                case '\\':
                    escaped = true;
                    continue;
            }

            // ④ 深度归零才是"顶层"：尝试匹配各分隔符（@ 不吃 @@）
            if (square == 0 && paren == 0 && brace == 0)
            {
                foreach (var d in delimiters)
                {
                    if (span.Slice(idx).StartsWith(d, StringComparison.Ordinal)
                        && !(d == "@" && span.Slice(idx).StartsWith("@@", StringComparison.Ordinal)))
                    {
                        return (from + idx, d);
                    }
                }
            }
        }

        return (-1, null);
    }

    /// <summary>
    /// legado 真值判定（对标 reader rule_engine::is_truthy）：
    /// 空串 / 0 / false / null / none / no / off = false，其余非空串 = true。
    /// 用于 isVolume / isVip / isPay 等"字符串当布尔"的字段。
    /// </summary>
    public static bool IsTruthy(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0)
        {
            return false;
        }

        return v.ToLowerInvariant() switch
        {
            "0" or "false" or "null" or "none" or "no" or "off" => false,
            _ => true,
        };
    }

    /// <summary>
    /// 切出 <c>##</c> 之后的替换段（含 <c>##</c> 本身）。无则返回 null。
    /// 对标 reader split_legado_regex。
    /// <para>语义要点（项目铁律）：组合符只在 <c>##</c> **之前**生效——
    /// ## 之后是替换正则，里面的 | / || 是正则交替符，不是字段组合符。
    /// 故一切组合符拆分必须先过本函数。</para>
    /// </summary>
    public static (string Pure, string? RegexPart) SplitLegadoRegex(string rule)
    {
        var i = rule.IndexOf("##", StringComparison.Ordinal);
        if (i < 0)
        {
            return (rule, null);
        }

        return (rule.Substring(0, i).Trim(), rule.Substring(i));
    }

    /// <summary>
    /// 应用 legado 行内替换：<c>##pattern##replacement</c>（replacement 缺省=删除匹配）；
    /// 末尾 <c>###</c> 或三段形态 = 仅替换首个匹配。对标 reader apply_legado_regex。
    /// <para>解析步骤：</para>
    /// ① 判 <c>###</c> 结尾（首替标记）并剥掉；
    /// ② 剥开头的 <c>##</c>；
    /// ③ 按 <c>##</c> 严格切三段（splitn(3)）：第 3 段存在即代表首替语义
    /// （即 pattern##replacement## 这种"有三段"的写法等价于 ###）；
    /// ④ 空 pattern 原样返回；⑤ 非法正则原样返回；
    /// ⑥ 按首替/全替执行 Replace。
    /// </summary>
    public static string ApplyLegadoRegex(string text, string regexPart)
    {
        var rp = regexPart.Trim();
        if (rp.Length == 0)
        {
            return text;
        }

        // ① 末尾 ### = 仅替换首个匹配
        var firstOnly = rp.EndsWith("###", StringComparison.Ordinal);
        if (firstOnly)
        {
            rp = rp.Substring(0, rp.Length - 3);
        }

        // ② 剥掉开头的 ##
        if (rp.StartsWith("##", StringComparison.Ordinal))
        {
            rp = rp.Substring(2);
        }

        // ③ 严格取前两段（splitn(3)）；第 3 段存在即代表 ### 首次替换语义
        var segs = rp.Split(new[] { "##" }, 3, StringSplitOptions.None);
        var pattern = segs.Length > 0 ? segs[0] : string.Empty;
        var replacement = segs.Length > 1 ? segs[1] : string.Empty;
        if (segs.Length > 2)
        {
            firstOnly = true;
        }

        // ④ 空 pattern：无匹配可做，原样返回
        if (pattern.Length == 0)
        {
            return text;
        }

        // ⑤ 非法正则：原样返回（reader：编译失败只记日志）
        var re = GetRegex(pattern);
        if (re is null)
        {
            return text;
        }

        // ⑥ 执行替换（首替 count=1）
        return firstOnly ? re.Replace(text, replacement, 1) : re.Replace(text, replacement);
    }

    /// <summary>
    /// 列表规则前缀 <c>-</c>/<c>+</c> 表示倒序/正序（对标 reader normalize_list_rule）。
    /// 目录场景常见：`-@css:#list>li` 表示倒序取章节。
    /// </summary>
    public static (string Rule, bool Reverse) NormalizeListRule(string rule)
    {
        var r = rule.Trim();
        if (r.StartsWith("-", StringComparison.Ordinal))
        {
            return (r.Substring(1).Trim(), true);
        }

        if (r.StartsWith("+", StringComparison.Ordinal))
        {
            return (r.Substring(1).Trim(), false);
        }

        return (r, false);
    }

    /// <summary>
    /// 提取脚本段：返回 (选择器段, 脚本或 null)（对标 reader extract_js）。
    /// <para>支持三种形态（按优先级）：</para>
    /// <list type="number">
    /// <item>&lt;js&gt;…&lt;/js&gt;：取第一个 &lt;js&gt; 到**最后一个** &lt;/js&gt; 之间的内容为脚本，
    /// 其前为选择器段（尾随 JS 形态，如 <c>@css:#content@html&lt;js&gt;…&lt;/js&gt;</c>）；</item>
    /// <item>@js:：@js: 之后全部为脚本，其前为选择器段；</item>
    /// <item>裸 js:（仅整条以 js: 开头时）：选择器段为空，整条是脚本。</item>
    /// </list>
    /// </summary>
    public static (string SelectorRule, string? Script) ExtractJs(string rule)
    {
        // ① <js>…</js>：LastIndexOf 取配对（脚本里可能再含字符串 "</js>" 字面量时以最后一个为准）
        var i = rule.IndexOf("<js>", StringComparison.Ordinal);
        if (i >= 0)
        {
            var end = rule.LastIndexOf("</js>", StringComparison.Ordinal);
            if (end > i)
            {
                return (rule.Substring(0, i).Trim(), rule.Substring(i + 4, end - (i + 4)));
            }
        }

        // ② @js:：其前是选择器，其后全部是脚本
        var j = rule.IndexOf("@js:", StringComparison.Ordinal);
        if (j >= 0)
        {
            return (rule.Substring(0, j).Trim(), rule.Substring(j + 4));
        }

        // ③ 裸 js:（仅允许出现在整条开头）
        if (rule.Trim().StartsWith("js:", StringComparison.Ordinal))
        {
            return (string.Empty, rule.Trim().Substring(3));
        }

        // 无脚本段
        return (rule, null);
    }
}

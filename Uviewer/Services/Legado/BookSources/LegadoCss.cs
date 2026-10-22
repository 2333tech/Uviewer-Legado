using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// legado 选择器语法的解析（对标 reader <c>parser/html.rs</c> 的 <c>legado_to_css</c> /
/// <c>split_top_level(rule, &amp;["@"])</c> / <c>parse_legacy_index_spec</c>）。
///
/// <para><b>为什么必须单独一套</b>：书源的正文规则大量写成
/// <c>class.content@p@textNodes</c>、<c>class.pic.0@tag.img@src</c>、<c>body@textNodes</c> 这类
/// legado 专有形态。它们在标准 CSS 里全是非法选择器：<c>class.content</c> 会被当成
/// "标签名 class + 类名 content"，多段 <c>@</c> 链更是无法解析——结果就是
/// <b>请求成功但正文恒为空</b>（目录规则往往只用单段选择器，于是"目录正常、正文空"）。</para>
///
/// <para><b>三条规则（逐条对照 reader）</b>：</para>
/// <list type="number">
/// <item>缩写：<c>class.a b</c> → <c>.a.b</c>（多类同一元素）、<c>class.a</c> → <c>.a</c>、
/// <c>id.x</c> → <c>#x</c>、<c>tag.div</c> → <c>div</c>；已是 <c>.</c>/<c>#</c>/<c>[</c> 开头的原样保留。</item>
/// <item>链式：按<b>顶层</b> <c>@</c> 切段，前 N-1 段是选择器逐级下钻，末段是取值轴
/// （text / textNodes / ownText / html / all / outerHtml / attr 名）。</item>
/// <item>索引后缀：<c>class.pic.0</c> 取第 0 个、<c>class.pic!2</c> 排除第 2 个。</item>
/// </list>
///
/// <para><b>注意</b>：切段必须走 <see cref="RuleAnalyzer.SplitTopLevel"/>——它已处理
/// 引号/括号/<c>&lt;js&gt;</c> 段与 <c>@@</c>（链式选择器前缀）的特判，
/// 朴素 <c>string.Split('@')</c> 会把 <c>textNodes</c> 之类的属性选择器切坏。</para>
/// </summary>
internal static class LegadoCss
{
    /// <summary>单段解析结果：CSS + 方括号索引 + 阅读原生单点索引（<c>.N</c>/<c>!N</c>）。</summary>
    /// <param name="Css">归一化后的 CSS 选择器。</param>
    /// <param name="Bracket">方括号索引规格（<c>[3,4]</c>/<c>[-1:0]</c>）；null = 无。</param>
    /// <param name="Index">阅读原生单点索引；-1 = 无。</param>
    /// <param name="Exclude">阅读原生 <c>!N</c>（排除该下标）。</param>
    internal readonly record struct CssPart(string Css, IndexSpec? Bracket, int Index, bool Exclude)
    {
        /// <summary>该段是否带索引（方括号或单点）。带索引时不能当作"裸属性名/取值词"。</summary>
        public bool HasIndex => Bracket is not null || Index >= 0;
    }

    /// <summary>
    /// 方括号索引规格（<c>[3,4]</c> / <c>[1:5]</c> / <c>[-1:0]</c> / <c>[!0,2]</c>）。
    /// <para>条目按书写顺序求值，输出顺序即条目顺序（与 legado 的"逆向扫描 + 逆向插入"等价）。</para>
    /// </summary>
    internal readonly record struct IndexSpec
    {
        /// <summary>条目（单点或区间）。</summary>
        public List<IndexEntry> Entries { get; init; }

        /// <summary>true = <c>[!…]</c>：剔除命中的下标，保留其余（原序）。</summary>
        public bool Exclude { get; init; }
    }

    /// <summary>索引条目：<paramref name="Point"/> 非空 = 单点；否则按 start:end:step 展开区间。</summary>
    internal readonly record struct IndexEntry(int? Point, int? Start, int? End, int Step);

    /// <summary>取值轴词表（大小写不敏感）。命中这些词时该段按"取值"解释而非选择器。</summary>
    private static readonly HashSet<string> AxisWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "textNodes", "ownText", "all", "html", "outerHtml", "innerText", "outerText", "node",
    };

    /// <summary>legado 缩写 → 标准 CSS。</summary>
    public static string ToCss(string selector)
    {
        var s = (selector ?? string.Empty).Trim();

        // 前导 '@'（@@class.tag 的"从片段根开始"标记）：本引擎本来就以片段根为起点，剥掉即可
        while (s.StartsWith("@", StringComparison.Ordinal))
        {
            s = s.Substring(1).TrimStart();
        }

        if (s.Length == 0)
        {
            return s;
        }

        if (s.StartsWith("class.", StringComparison.OrdinalIgnoreCase))
        {
            // class.a b c → .a.b.c（legado：空格分隔的多个类名作用于同一元素）
            var rest = s.Substring(6).Trim();
            if (rest.Length == 0)
            {
                return string.Empty;
            }

            var classes = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return "." + string.Join(".", classes);
        }

        if (s.StartsWith("id.", StringComparison.OrdinalIgnoreCase))
        {
            return "#" + s.Substring(3).Trim();
        }

        if (s.StartsWith("tag.", StringComparison.OrdinalIgnoreCase))
        {
            return s.Substring(4).Trim();
        }

        return s;
    }

    /// <summary>按顶层 '@' 切链（<c>@@</c> 不切；引号/括号/<c>&lt;js&gt;</c> 内不切）。</summary>
    public static string[] SplitChain(string rule)
    {
        var (parts, _) = RuleAnalyzer.SplitTopLevel(rule ?? string.Empty, new[] { "@" });
        // 裸 "@" 段必须过滤：@@class.x@text 的前导 @@ 会被切成 "@" + "class.x" + "text"
        // （FindNextDelimiter 只保证不在第一个 @ 上切）。若留着这段，ParsePart 剥完美得到
        // 空 CSS → SelectPart 恒空 → 整条规则静默返回空。legado RuleAnalyzer.trim() 的
        // 口径：前导 @ 是"从当前片段根开始"的标记，剥掉即可（样例里几十处
        // {{@@class.w-100 h-100.0@src}} 依赖这一点）。
        return parts
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim() != "@")
            .Select(p => p.Trim())
            .ToArray();
    }

    /// <summary>该段是否为取值轴词（text/textNodes/html/…）。</summary>
    public static bool IsAxisWord(string part) => AxisWords.Contains((part ?? string.Empty).Trim());

    /// <summary>
    /// 解析单段：缩写转 CSS + 拆末尾索引后缀（两种写法都支持）。
    /// <para><b>写法一（阅读原生）</b>：末尾 <c>.</c>/<c>!</c> + 纯数字 —— <c>class.pic.0</c>（取第 0 个）、
    /// <c>tag.li!1</c>（排除第 1 个）。<c>.content</c>、<c>.a.b</c> 的尾巴不是纯数字，不会被误拆。</para>
    /// <para><b>写法二（方括号，对齐 legado <c>ElementsSingle.findIndexSet</c>）</b>：
    /// <c>class.list[3,4]</c> / <c>class.list[1:5]</c> / <c>tag.div[-1:0]</c>（整列表反向）/
    /// <c>[!0,2]</c>（排除）/ <c>[0:9:2]</c>（步长）。索引支持负数与省略端点。</para>
    /// </summary>
    public static CssPart ParsePart(string part)
    {
        var raw = (part ?? string.Empty).Trim();
        var index = -1;
        var exclude = false;

        // ① 方括号索引：从末尾往前找到配对的 '['（选择器侧不会出现未闭合的 '['）
        IndexSpec? bracket = null;
        if (raw.EndsWith("]", StringComparison.Ordinal))
        {
            var open = raw.LastIndexOf('[');
            if (open >= 0)
            {
                var inner = raw[(open + 1)..^1].Trim();
                if (TryParseBracketIndex(inner, out var spec))
                {
                    bracket = spec;
                    raw = raw[..open].Trim();
                }
            }
        }

        // ② 阅读原生 .N / !N
        var cut = raw.LastIndexOfAny(new[] { '.', '!' });
        if (cut > 0 && cut < raw.Length - 1)
        {
            var tail = raw[(cut + 1)..].Trim();
            if (tail.Length > 0 && tail.All(char.IsAsciiDigit) && int.TryParse(tail, out var parsed))
            {
                index = parsed;
                exclude = raw[cut] == '!';
                raw = raw[..cut].Trim();
            }
        }

        return new CssPart(NormalizeCss(ToCss(raw)), bracket, index, exclude);
    }

    /// <summary>
    /// 解析方括号索引内容（legado <c>findIndexSet</c> 的正向等价实现）。
    /// <para>分隔符：<c>,</c> 分项、<c>:</c> 分端点与步长、前导 <c>!</c> 表示排除。
    /// 形如 <c>-1, 3:-2:-10, 2</c>。任何一项不合法 → false（整段按普通选择器处理，
    /// 比如 CSS 属性选择器 <c>class.x[data-id]</c> 不会被误当成索引）。</para>
    /// </summary>
    private static bool TryParseBracketIndex(string inner, out IndexSpec spec)
    {
        spec = new IndexSpec { Entries = new List<IndexEntry>() };
        var text = inner.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] == '!')
        {
            spec = spec with { Exclude = true };
            text = text[1..].Trim();
            if (text.Length == 0)
            {
                return false;
            }
        }

        foreach (var rawItem in text.Split(','))
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var colon = item.Split(':');
            if (colon.Length == 1)
            {
                if (!TryParseIndexNumber(colon[0], out var point))
                {
                    return false;
                }

                spec.Entries.Add(new IndexEntry(point, null, null, 1));
                continue;
            }

            if (colon.Length is > 3)
            {
                return false;
            }

            int? start = null;
            int? end = null;
            var step = 1;

            if (colon[0].Trim().Length > 0)
            {
                if (!TryParseIndexNumber(colon[0], out var s))
                {
                    return false;
                }

                start = s;
            }

            if (colon[1].Trim().Length > 0)
            {
                if (!TryParseIndexNumber(colon[1], out var e))
                {
                    return false;
                }

                end = e;
            }

            if (colon.Length == 3 && colon[2].Trim().Length > 0)
            {
                if (!TryParseIndexNumber(colon[2], out var st) || st == 0)
                {
                    return false;
                }

                step = st;
            }

            spec.Entries.Add(new IndexEntry(null, start, end, step));
        }

        return spec.Entries.Count > 0;
    }

    private static bool TryParseIndexNumber(string text, out int value)
    {
        value = 0;
        var t = text.Trim();
        if (t.Length == 0)
        {
            return false;
        }

        var start = t[0] == '-' ? 1 : 0;
        if (start == t.Length)
        {
            return false;
        }

        for (var i = start; i < t.Length; i++)
        {
            if (!char.IsAsciiDigit(t[i]))
            {
                return false;
            }
        }

        return int.TryParse(t, out value);
    }

    /// <summary>对命中的节点应用阅读原生单点索引（<paramref name="exclude"/> = 剔除该下标）。</summary>
    public static List<HtmlAgilityPack.HtmlNode> ApplyIndex(
        List<HtmlAgilityPack.HtmlNode> nodes, int index, bool exclude)
    {
        if (index < 0)
        {
            return nodes;
        }

        if (exclude)
        {
            if (index < nodes.Count)
            {
                var copy = new List<HtmlAgilityPack.HtmlNode>(nodes);
                copy.RemoveAt(index);
                return copy;
            }

            return nodes;
        }

        return index < nodes.Count
            ? new List<HtmlAgilityPack.HtmlNode> { nodes[index] }
            : new List<HtmlAgilityPack.HtmlNode>();
    }

    /// <summary>对命中的节点应用索引（方括号规格优先；否则用阅读原生单点索引）。</summary>
    public static List<HtmlAgilityPack.HtmlNode> ApplyIndex(List<HtmlAgilityPack.HtmlNode> nodes, CssPart part)
    {
        if (part.Bracket is { } bracket)
        {
            return ApplyBracketIndex(nodes, bracket);
        }

        return ApplyIndex(nodes, part.Index, part.Exclude);
    }

    /// <summary>
    /// 方括号索引求值（逐条展开成索引集合，按规格顺序输出；排除规格剔除后保留原序）。
    /// <para>端点语义同 legado：省略左端 = 0、省略右端 = len-1、负数加 len、越界夹取；
    /// <c>end &gt; start</c> 升序展开，否则降序（<c>[-1:0]</c> 用于整表反向）。</para>
    /// </summary>
    private static List<HtmlAgilityPack.HtmlNode> ApplyBracketIndex(
        List<HtmlAgilityPack.HtmlNode> nodes, IndexSpec spec)
    {
        var len = nodes.Count;
        if (len == 0)
        {
            return nodes;
        }

        var picked = new List<int>();
        var seen = new HashSet<int>();

        foreach (var entry in spec.Entries)
        {
            if (entry.Point is { } point)
            {
                var idx = point < 0 ? point + len : point;
                if (idx >= 0 && idx < len && seen.Add(idx))
                {
                    picked.Add(idx);
                }

                continue;
            }

            var start = entry.Start ?? 0;
            var end = entry.End ?? (len - 1);
            if (start < 0)
            {
                start += len;
            }

            if (end < 0)
            {
                end += len;
            }

            // 同侧越界 → 该区间无效（legado 原语义）
            if ((start < 0 && end < 0) || (start >= len && end >= len))
            {
                continue;
            }

            start = Math.Clamp(start, 0, len - 1);
            end = Math.Clamp(end, 0, len - 1);

            if (start == end)
            {
                if (seen.Add(start))
                {
                    picked.Add(start);
                }

                continue;
            }

            var step = entry.Step;
            if (step < 0)
            {
                step = -step;
            }

            if (end > start)
            {
                for (var i = start; i <= end; i += step)
                {
                    if (seen.Add(i))
                    {
                        picked.Add(i);
                    }
                }
            }
            else
            {
                for (var i = start; i >= end; i -= step)
                {
                    if (seen.Add(i))
                    {
                        picked.Add(i);
                    }
                }
            }
        }

        if (spec.Exclude)
        {
            var keep = new List<HtmlAgilityPack.HtmlNode>(len - picked.Count);
            for (var i = 0; i < len; i++)
            {
                if (!picked.Contains(i))
                {
                    keep.Add(nodes[i]);
                }
            }

            return keep;
        }

        var result = new List<HtmlAgilityPack.HtmlNode>(picked.Count);
        foreach (var idx in picked)
        {
            result.Add(nodes[idx]);
        }

        return result;
    }

    /// <summary>
    /// 在 <c>&gt;</c> 两侧补空格：绕开 HtmlAgilityPack.CssSelectors 对 <c>a&gt;b</c>（无空格）
    /// 的解析缺陷（会返回错误节点数），<c>a &gt; b</c> 则正常（项目踩坑点 5）。
    /// 不在 <c>[...]</c> 属性选择器或引号内部改写。
    /// </summary>
    public static string NormalizeCss(string css)
    {
        if (string.IsNullOrEmpty(css) || css.IndexOf('>') < 0)
        {
            return css;
        }

        var sb = new StringBuilder(css.Length + 4);
        var inBracket = false;
        var quote = '\0';
        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];

            // 引号内：属性值里可能出现 '>'（如 [title="a>b"]），原样拷贝
            if (quote != '\0')
            {
                if (c == quote && (i == 0 || css[i - 1] != '\\'))
                {
                    quote = '\0';
                }

                sb.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            // 属性选择器内：'[' 与 ']' 之间不当作组合符
            if (c == '[')
            {
                inBracket = true;
            }
            else if (c == ']')
            {
                inBracket = false;
            }

            if (c == '>' && !inBracket)
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }

                sb.Append('>');

                // 下一字符不是空格时才补（避免重复）
                if (i + 1 < css.Length && css[i + 1] != ' ')
                {
                    sb.Append(' ');
                }

                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

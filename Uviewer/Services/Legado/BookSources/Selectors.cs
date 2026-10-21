using System.Collections.Generic;
using System.Linq;
using System.Text;
using HtmlAgilityPack;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 裸属性名规则（作用于当前片段元素自身，对应 legado 的 href/src/text 直接取值）。
/// 这些词不作为 CSS 标签选择器，而是取当前元素的属性/文本——
/// 否则 QuerySelectorAll("href") 会按标签名匹配，恒为空（项目踩坑点 6）。
/// </summary>
internal static class CssAttrKeywords
{
    /// <summary>关键字表（大小写不敏感）：常见属性名 + text/html 系列取值轴。</summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "alt", "title", "text", "html", "ownText", "innerText", "outerHtml",
        "value", "name", "id", "class", "style", "rel", "content", "data-src", "data-url",
    };
}

/// <summary>
/// HTML 选择器求值（对标 reader html.rs / AnalyzeByJSoup）。
/// 基于 HtmlAgilityPack：CSS 用 QuerySelectorAll，XPath 用 SelectNodes。
/// 支持 legado 轴扩展：CSS <c>sel@text/@html/@ownText/@attr</c>，XPath <c>::text/::html/::attr(x)</c> 及原生 <c>/text()</c>、<c>/@attr</c>。
/// <para>入口分工：<see cref="ListFragments"/> 列表模式（文本形态，返回 OuterHtml 字符串）；
/// <see cref="ListFragmentContexts"/> 列表模式（节点形态，返回活节点上下文，热路径）；
/// <see cref="FieldValues(FragmentContext, string)"/> 字段模式（单片段上取字段值，热路径）。</para>
/// <para>所有入口都遵循"选择器异常不中断解析"原则（try/catch → 空列表）：
/// reader 的 select 返回 Result，异常只记日志（对标行为）。</para>
/// </summary>
internal static class Selectors
{
    /// <summary>
    /// 列表模式（文本形态）：返回每个匹配元素的 OuterHtml（作为字段提取的片段）。
    /// 选择器非法时返回空列表（reader 的 select 返回 Result，异常只记日志，不中断解析）。
    /// </summary>
    public static List<string> ListFragments(string rule, string content)
    {
        try
        {
            return ListFragmentsCore(rule, content);
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 列表模式的节点形态：直接把命中的活节点包成片段上下文（不序列化 OuterHtml）。
    /// 片段的 <see cref="FragmentContext.Html"/> 会惰性克隆进独立文档，
    /// 与"取 OuterHtml 再重新解析"的树形一致（根元素仍可被 CSS 匹配、绝对 XPath 仍限定在片段内），
    /// 但避免每个片段一次的字符串序列化 + 重新解析——大目录/搜索列表上这是最大的一笔开销。
    /// </summary>
    internal static List<FragmentContext> ListFragmentContexts(FragmentContext ctx, string rule)
    {
        try
        {
            return ListFragmentContextsCore(ctx, rule);
        }
        catch
        {
            return new List<FragmentContext>();
        }
    }

    /// <summary>
    /// 列表模式核心（文本形态）。步骤：
    /// ① 剥模式前缀；② 空规则或显式 body → 整段内容作为**单个**片段
    /// （legado body 取整体语义，项目踩坑点 8：不做"0 节点 = 空结果"处理）；
    /// ③ 按内容判定模式（XPath / CSS）；④ 执行选择；⑤ 收集各节点的 OuterHtml。
    /// </summary>
    private static List<string> ListFragmentsCore(string rule, string content)
    {
        var stripped = RuleAnalyzer.StripModePrefix(rule).Trim();
        // 列表规则为空或显式 body：把整段内容作为一个片段（对标 legado body 取整体）。
        if (stripped.Length == 0 || stripped == "body")
        {
            return new List<string> { content };
        }

        var mode = RuleAnalyzer.DetectMode(rule, content);
        var selector = stripped;
        var doc = Load(content);

        if (mode == RuleMode.XPath)
        {
            // XPath 模式：剥 ::text/::attr(x) 等轴后 SelectNodes（列表模式忽略轴，只取 OuterHtml）
            var (xpath, _) = StripXPathAxis(selector);
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes is null)
            {
                return new List<string>();
            }

            var list = new List<string>();
            foreach (var n in nodes)
            {
                list.Add(n.OuterHtml);
            }

            return list;
        }

        // 默认按 CSS 处理（包括标注 @css: 与无前缀默认）。
        // 走 legado 链式求值：class./id./tag. 缩写 + @ 链式下钻 + 索引后缀 + 尾部取值轴。
        // 盲用 QuerySelectorAll(整条) 会把 class.content@p@textNodes 当成非法选择器 → 恒空。
        var nodesCss = SelectElementChain(doc.DocumentNode, selector);

        var outList = new List<string>();
        foreach (var n in nodesCss)
        {
            outList.Add(n.OuterHtml);
        }

        return outList;
    }

    /// <summary>
    /// 列表模式核心（节点形态）。与 <see cref="ListFragmentsCore(string, string)"/> 步骤完全一致，
    /// 仅两处不同：① 文档复用调用方的 <see cref="FragmentContext.Html"/>（不重新 Load，
    /// 组合符的多个段落因此共享同一份解析）；② 产物是节点上下文而非 OuterHtml 字符串。
    /// 空/body 特例同样退化为文本形态片段。
    /// </summary>
    private static List<FragmentContext> ListFragmentContextsCore(FragmentContext ctx, string rule)
    {
        var stripped = RuleAnalyzer.StripModePrefix(rule).Trim();
        // 列表规则为空或显式 body：把整段内容作为一个片段（对标 legado body 取整体）。
        if (stripped.Length == 0 || stripped == "body")
        {
            return new List<FragmentContext> { new FragmentContext(ctx.Raw) };
        }

        var mode = RuleAnalyzer.DetectMode(rule, ctx);
        var selector = stripped;
        var doc = ctx.Html;

        if (mode == RuleMode.XPath)
        {
            var (xpath, _) = StripXPathAxis(selector);
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes is null)
            {
                return new List<FragmentContext>();
            }

            var list = new List<FragmentContext>(nodes.Count);
            foreach (var n in nodes)
            {
                list.Add(new FragmentContext(n));
            }

            return list;
        }

        // 默认按 CSS 处理（包括标注 @css: 与无前缀默认）：同样走 legado 链式求值
        var nodesCss = SelectElementChain(doc.DocumentNode, selector);

        var outList = new List<FragmentContext>();
        foreach (var n in nodesCss)
        {
            outList.Add(new FragmentContext(n));
        }

        return outList;
    }

    /// <summary>字段模式：返回每个匹配节点的提取值（文本/属性/HTML 等）。每次调用重新解析文档。</summary>
    public static List<string> FieldValues(string rule, string content) => FieldValues(Load(content), rule);

    /// <summary>
    /// 基于片段上下文求值（字段模式热路径）：复用片段已解析的 HTML 文档，
    /// 并用上下文缓存的 JSON 判定结果做模式识别（不再对每个字段重新序列化 OuterHtml / 重新嗅探 JSON）。
    /// </summary>
    internal static List<string> FieldValues(FragmentContext ctx, string rule)
    {
        try
        {
            return FieldValues(ctx.Html, rule, RuleAnalyzer.DetectMode(rule, ctx));
        }
        catch
        {
            // 选择器非法：返回空（reader 的 html::select 返回 Result，异常只记日志）
            return new List<string>();
        }
    }

    /// <summary>基于已解析文档求值（避免每条字段重新解析同一片段）。</summary>
    private static List<string> FieldValues(HtmlDocument doc, string rule) =>
        FieldValues(doc, rule, RuleAnalyzer.DetectModeCore(rule, false));

    private static List<string> FieldValues(HtmlDocument doc, string rule, RuleMode mode)
    {
        try
        {
            return FieldValuesCore(doc, rule, mode);
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 字段模式核心。步骤：
    /// ① 剥模式前缀；
    /// ② XPath 模式 → SelectNodes + 轴提取（::text/::html/::ownText/::attr(x) 或原生 /text() /@attr）；
    /// ③ CSS 模式 → legado 链式求值（见 <see cref="SelectChain"/>：class./id./tag. 缩写 +
    /// <c>@</c> 逐级下钻 + 索引后缀 + 尾部取值轴）；
    /// ④ 单段裸属性词（href/src/text…）→ 作用于"当前片段元素自身"；
    /// ⑤ 单段无轴 → textNodes（换行保留段落，与 reader select_all_text 一致）。
    /// </summary>
    private static List<string> FieldValuesCore(HtmlDocument doc, string rule, RuleMode mode)
    {
        var selector = RuleAnalyzer.StripModePrefix(rule);

        if (mode == RuleMode.XPath)
        {
            var (xpath, axis) = StripXPathAxis(selector);
            var xpathNodes = doc.DocumentNode.SelectNodes(xpath);
            if (xpathNodes is null)
            {
                return new List<string>();
            }

            var list = new List<string>();
            foreach (var n in xpathNodes)
            {
                list.Add(ApplyXPathAxis(n, axis));
            }

            return list;
        }

        // ① 按顶层 '@' 切链：["class.content", "p", "textNodes"] / ["href"] / [".a"] …
        //    切段必须用 SplitTopLevel（引号/括号/<js> 内不切、@@ 不切）
        var chain = LegadoCss.SplitChain(selector);
        if (chain.Length == 0)
        {
            return new List<string>();
        }

        // 单段裸属性/取值词（href / text / html…）：作用于当前片段元素自身，不按标签名查找。
        // 必须放在"选择节点"之前——否则 QuerySelectorAll("href") 恒无命中，
        // 早退成空列表（BookUrl = "href##$##…" 这类规则会整条失效）。
        if (chain.Length == 1)
        {
            var single = LegadoCss.ParsePart(chain[0]);
            if (single.Index < 0 && CssAttrKeywords.Names.Contains(single.Css))
            {
                return new List<string> { ApplyCssAxis(RootElementOf(doc.DocumentNode), single.Css) };
            }
        }

        // ② 逐级下钻：前 N-1 段是选择器；单段时整段都是选择器（无取值轴）
        var nodes = SelectChain(doc.DocumentNode, chain, out var lastPart);
        if (nodes.Count == 0)
        {
            return new List<string>();
        }

        var outList = new List<string>();

        // 单段选择器：与 reader select_all_text 一致 → textNodes（按换行保留段落）
        if (chain.Length == 1)
        {
            foreach (var n in nodes)
            {
                outList.Add(ApplyCssAxis(n, "textNodes"));
            }

            return outList;
        }

        // 末段是取值轴词：直接取值
        if (LegadoCss.IsAxisWord(lastPart))
        {
            foreach (var n in nodes)
            {
                outList.Add(ApplyCssAxis(n, lastPart));
            }

            return outList;
        }

        // ③ 末段既不是取值轴、也没有同名属性 → 当选择器，取其命中节点的文本
        //    （reader 的 find_map 语义：先按提取器解释，解释不通再按选择器走）
        var lastSelector = LegadoCss.ParsePart(lastPart);
        foreach (var n in nodes)
        {
            if (lastSelector.Index < 0 && IsSimpleName(lastSelector.Css))
            {
                var attr = n.GetAttributeValue(lastSelector.Css, null);
                if (attr is not null)
                {
                    outList.Add(attr);
                    continue;
                }
            }

            foreach (var m in SelectPart(n, lastSelector))
            {
                outList.Add(m.InnerText.Trim());
            }
        }

        return outList;
    }

    /// <summary>
    /// 链式求值（逐级下钻）：返回前 N-1 段命中的节点，末段交给调用方解释。
    /// <para>首段若是裸属性词（<c>href@text</c> 这类）→ 以当前片段元素自身为起点（保留既有语义）；
    /// 否则首段按选择器求值。</para>
    /// </summary>
    private static List<HtmlNode> SelectChain(HtmlNode scope, string[] chain, out string lastPart)
    {
        lastPart = chain[^1];

        // 单段：整段都是选择器
        var selectCount = chain.Length > 1 ? chain.Length - 1 : 1;
        var first = LegadoCss.ParsePart(chain[0]);

        List<HtmlNode> nodes;
        if (chain.Length > 1 && first.Index < 0 && CssAttrKeywords.Names.Contains(first.Css))
        {
            nodes = new List<HtmlNode> { RootElementOf(scope) };
        }
        else
        {
            nodes = SelectPart(scope, first);
        }

        for (var i = 1; i < selectCount && nodes.Count > 0; i++)
        {
            var part = LegadoCss.ParsePart(chain[i]);
            nodes = nodes.SelectMany(n => SelectPart(n, part)).ToList();
        }

        return nodes;
    }

    /// <summary>
    /// 列表模式选元素：链式下钻到"元素"为止。
    /// <para>末段若是取值轴（<c>text</c>/<c>html</c>…）则丢弃——列表项是元素，不是值；
    /// legado 的 chapterList 常写成 <c>class.list@tag.li@tag.a@text</c>，末段必须忽略。</para>
    /// </summary>
    private static List<HtmlNode> SelectElementChain(HtmlNode scope, string rule)
    {
        var chain = LegadoCss.SplitChain(rule);
        if (chain.Length == 0)
        {
            return new List<HtmlNode>();
        }

        var count = chain.Length;
        if (count > 1 && LegadoCss.IsAxisWord(chain[^1]))
        {
            count--;
        }

        var first = LegadoCss.ParsePart(chain[0]);
        var nodes = count > 1 && first.Index < 0 && CssAttrKeywords.Names.Contains(first.Css)
            ? new List<HtmlNode> { RootElementOf(scope) }
            : SelectPart(scope, first);

        for (var i = 1; i < count && nodes.Count > 0; i++)
        {
            var part = LegadoCss.ParsePart(chain[i]);
            nodes = nodes.SelectMany(n => SelectPart(n, part)).ToList();
        }

        return nodes;
    }

    /// <summary>按单段选择器取值（含索引后缀）。非法选择器 → 空列表（不中断解析）。</summary>
    private static List<HtmlNode> SelectPart(HtmlNode scope, (string Css, int Index, bool Exclude) part)
    {
        if (part.Css.Length == 0)
        {
            return new List<HtmlNode>();
        }

        var hits = scope.QuerySelectorAll(part.Css);
        var list = hits is null ? new List<HtmlNode>() : hits.ToList();
        return LegadoCss.ApplyIndex(list, part.Index, part.Exclude);
    }

    /// <summary>当前片段元素：文档节点取首个元素子节点，元素节点取自身。</summary>
    private static HtmlNode RootElementOf(HtmlNode scope) =>
        scope.NodeType == HtmlNodeType.Document
            ? scope.ChildNodes.FirstOrDefault(n => n.NodeType == HtmlNodeType.Element) ?? scope
            : scope;

    /// <summary>简单标识符（纯属性名形态，如 href/src/data-src）：不含选择器语法字符。</summary>
    private static bool IsSimpleName(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 64)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != ':')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>解析 HTML 片段（供 FragmentContext 惰性复用）。</summary>
    internal static HtmlDocument Load(string content)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        return doc;
    }

    // ---- CSS 轴 ----

    /// <summary>
    /// 应用 CSS 轴取值。轴语义（对齐 reader <c>extract_text</c>）：
    /// 无轴 → textNodes；text → 可见文本；textNodes → 全部文本节点按<b>换行</b>拼接
    /// （正文段落靠它保留分行，legado 的 @textNodes 同义）；html → InnerHtml；
    /// ownText → 仅直接文本子节点；outerHtml/node → OuterHtml；all → InnerHtml；
    /// 其余 → 按属性名取值。
    /// </summary>
    private static string ApplyCssAxis(HtmlNode node, string? axis)
    {
        if (string.IsNullOrEmpty(axis))
        {
            return TextNodes(node);
        }

        return axis switch
        {
            "text" => node.InnerText,
            "allText" => node.InnerText,
            "textNodes" => TextNodes(node),
            "ownText" => OwnText(node),
            "all" => node.InnerHtml,
            "html" => node.InnerHtml,
            "outerHtml" => node.OuterHtml,
            "outerText" => node.InnerText,
            "innerText" => node.InnerText,
            "node" => node.OuterHtml,
            _ => node.GetAttributeValue(axis, string.Empty),
        };
    }

    /// <summary>
    /// legado <c>textNodes</c>：递归收集全部文本节点，逐个去空白后按<b>换行</b>拼接。
    /// <para>与 <c>InnerText</c> 的差别：HAP 的 InnerText 把 `<p>a</p><p>b</p>` 拼成 "ab"，
    /// 正文会糊成一段；textNodes 保留段落边界（reader <c>get_text_nodes</c> 同语义）。</para>
    /// <para>性能：直接写 <see cref="StringBuilder"/>（旧实现先攒 <c>List&lt;string&gt;</c>
    /// 再 <c>Join</c>，多一次字符串数组 + 一次整串复制；正文提取是每章必走的热路径）。</para>
    /// </summary>
    private static string TextNodes(HtmlNode node)
    {
        var sb = new StringBuilder();
        AppendTextNodes(node, sb);
        return sb.ToString();
    }

    private static void AppendTextNodes(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                // span 版 Trim：不额外分配裁剪后的副本
                var text = child.InnerText.AsSpan().Trim();
                if (text.Length > 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(text);
                }

                continue;
            }

            if (child.NodeType == HtmlNodeType.Element)
            {
                AppendTextNodes(child, sb);
            }
        }
    }

    // ---- XPath 轴 ----

    /// <summary>
    /// 拆 XPath 轴（对标 reader split_xpath_axis）。支持两套写法：
    /// <list type="number">
    /// <item>legado 扩展轴：::text / ::html / ::ownText / ::allText / ::attr(name)；</item>
    /// <item>XPath 原生：/text() 与 /@attr。</item>
    /// </list>
    /// 返回 (纯 xpath, 轴)。无轴时轴为 null（调用方按 InnerText 处理）。
    /// </summary>
    private static (string xpath, string? axis) StripXPathAxis(string selector)
    {
        if (selector.EndsWith("::text", StringComparison.Ordinal))
        {
            return (selector.Substring(0, selector.Length - 6).Trim(), "text");
        }

        if (selector.EndsWith("::html", StringComparison.Ordinal))
        {
            return (selector.Substring(0, selector.Length - 6).Trim(), "html");
        }

        if (selector.EndsWith("::ownText", StringComparison.Ordinal))
        {
            return (selector.Substring(0, selector.Length - 9).Trim(), "ownText");
        }

        if (selector.EndsWith("::allText", StringComparison.Ordinal))
        {
            return (selector.Substring(0, selector.Length - 9).Trim(), "allText");
        }

        // ::attr(name)：取最后一个 ::attr( 到结尾 ) 之间的内容
        var attrIdx = selector.LastIndexOf("::attr(", StringComparison.Ordinal);
        if (attrIdx >= 0 && selector.EndsWith(")", StringComparison.Ordinal))
        {
            var attr = selector.Substring(attrIdx + 7, selector.Length - attrIdx - 8).Trim();
            return (selector.Substring(0, attrIdx).Trim(), "attr:" + attr);
        }

        // 原生 /text() 与 /@attr
        if (selector.EndsWith("/text()", StringComparison.Ordinal))
        {
            return (selector.Substring(0, selector.Length - 7).Trim(), "text");
        }

        var atIdx = selector.LastIndexOf("/@", StringComparison.Ordinal);
        if (atIdx >= 0)
        {
            var attr = selector.Substring(atIdx + 2).Trim();
            return (selector.Substring(0, atIdx).Trim(), "attr:" + attr);
        }

        return (selector, null);
    }

    /// <summary>
    /// 应用 XPath 轴取值。轴编码：attr:xxx 表示取属性；
    /// text/html/ownText/allText/outerHtml 同 CSS 轴；无轴 → InnerText（XPath 默认 string_value 语义）。
    /// </summary>
    private static string ApplyXPathAxis(HtmlNode node, string? axis)
    {
        if (string.IsNullOrEmpty(axis))
        {
            return node.InnerText;
        }

        if (axis.StartsWith("attr:", StringComparison.Ordinal))
        {
            var attr = axis.Substring(5);
            return node.GetAttributeValue(attr, string.Empty);
        }

        return axis switch
        {
            "text" => node.InnerText,
            "html" => node.InnerHtml,
            "ownText" => OwnText(node),
            "allText" => node.InnerText,
            "outerHtml" => node.OuterHtml,
            _ => node.InnerText,
        };
    }

    /// <summary>仅取元素自身的直接文本子节点（不含子元素文本，对标 legado ownText）。</summary>
    private static string OwnText(HtmlNode node)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                sb.Append(child.InnerText);
            }
        }

        return sb.ToString();
    }
}

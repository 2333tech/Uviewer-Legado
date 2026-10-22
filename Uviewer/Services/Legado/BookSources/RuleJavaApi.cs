using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using HtmlAgilityPack;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 4-5：T2 规则原语的 JS 侧包装（<c>java.getElements/getElement/getStringList/setContent</c>）。
///
/// <para><b>语义来源</b>：legado <c>AnalyzeRule.getElements/getElement/setContent</c>
/// （<c>legado-with-MD3/.../analyzeRule/AnalyzeRule.kt</c>）+ reader 的元素快照方案
/// （<c>reader/src/parser/js.rs</c> 的 <c>wrapElement/wrapElements</c>）。要点：</para>
/// <list type="number">
/// <item>HTML 命中 → <b>元素快照</b>对象：JSoup 风格方法 <c>text()/ownText()/html()/outerHtml()/
///   attr(name)/hasAttr(name)/tagName()/select(rule)</c>——与 legado 返回的 JSoup Element 对齐；</item>
/// <item>JSONPath 命中 → <b>原始 JSON 节点</b>（legado <c>Mode.Json</c> 走
///   <c>getAnalyzeByJSonPath(result).getList(rule)</c>）：脚本可直接 <c>item.title</c> 取值，
///   同时补退化版 <c>text/attr</c> 等方法（reader 同款兜底，JSON 自带同名字段时不覆盖）；</item>
/// <item>正则命中 → 文本快照（<c>text()</c> = 匹配串，对齐 legado <c>AnalyzeByRegex.getElements</c>）。</item>
/// </list>
///
/// <para><b>数组形态</b>：legado（Rhino）返回 <c>java.util.List</c>，样本里两种写法都有——
/// JS 风格（<c>.length/.map/[i]</c>）与 Java 风格（<c>.size()/.get(i)/.toArray()</c>）。
/// 这里返回 JS 数组（JS 风格天然支持），并在<b>数组实例</b>上补 <c>size()/get()/toArray()</c>
/// 与委托给首元素的 <c>text/attr/html/select</c>（样本里有 <c>getElements('...').attr('value')</c>
/// 这类对列表取属性的写法），两种作者习惯都能跑——这是相对 reader 的超集增强。</para>
/// </summary>
internal static class RuleJavaApi
{
    private static readonly Regex RxWhitespace = new(@"\s+", RegexOptions.Compiled);

    // ============================================================
    // getElements / getElement
    // ============================================================

    /// <summary>
    /// <c>java.getElements(rule)</c>：把规则命中项包成 JS 数组。
    /// <paramref name="items"/> 由 <see cref="RuleEngine"/> 的元素求值管线给出
    /// （HtmlNode / JsonNode / 匹配文本）。
    /// </summary>
    public static JsValue GetElements(Engine engine, JsBridge bridge, IReadOnlyList<object?> items)
    {
        var array = new JsArray(engine, Array.Empty<JsValue>());

        // 首元素只包一次：旧实现 AttachListExtras 里又 WrapItem(items[0])，
        // 同一个元素被包两个不同 JsObject（委托函数挂的是另一个实例）。
        JsValue? firstWrapped = null;
        foreach (var item in items)
        {
            var wrapped = WrapItem(engine, bridge, item);
            firstWrapped ??= wrapped;
            array.Push(wrapped);
        }

        AttachListExtras(engine, bridge, array, items, firstWrapped ?? JsValue.Null);
        return array;
    }

    /// <summary><c>java.getElement(rule)</c>：取首项；无命中返回 null（reader 语义）。</summary>
    public static JsValue GetElement(Engine engine, JsBridge bridge, IReadOnlyList<object?> items) =>
        items.Count > 0 ? WrapItem(engine, bridge, items[0]) : JsValue.Null;

    /// <summary>命中项 → JS 值：JSON 节点原样透传、HTML 元素节点包快照、文本/其余包文本快照。</summary>
    private static JsValue WrapItem(Engine engine, JsBridge bridge, object? item) => item switch
    {
        null => JsValue.Null,
        // 文本节点必须走文本快照：旧实现把所有 HtmlNode 都当元素包，
        // 文本节点的 text() 恒为空（ElementText 只递归子节点，文本节点没有子节点）。
        HtmlNode { NodeType: HtmlNodeType.Text } textNode =>
            WrapTextElement(engine, bridge, textNode.InnerText),
        HtmlNode node => WrapHtmlElement(engine, bridge, node),
        JsonNode json => FromJsonNode(engine, bridge, json),
        _ => WrapTextElement(engine, bridge, item.ToString() ?? string.Empty),
    };

    // ============================================================
    // getStringList
    // ============================================================

    /// <summary><c>java.getStringList(rule)</c>：字符串列表 → JS 数组（reader：解析失败退空数组；
    /// 这里对"输出非 JSON 数组"的宽容一层：按单值返回）。</summary>
    public static JsValue GetStringList(Engine engine, string? jsonList)
    {
        var array = new JsArray(engine, Array.Empty<JsValue>());
        if (string.IsNullOrWhiteSpace(jsonList))
        {
            return array;
        }

        try
        {
            var node = JsonNode.Parse(jsonList);
            switch (node)
            {
                case JsonArray list:
                    foreach (var entry in list)
                    {
                        array.Push(JsContextObjects.ToJs(engine, entry?.ToString() ?? string.Empty));
                    }

                    break;
                case null:
                    break;
                default:
                    array.Push(JsContextObjects.ToJs(engine, node.ToString()));
                    break;
            }
        }
        catch
        {
            // 输出不是合法 JSON：按"单个值"返回（比空数组更接近作者意图，脚本继续跑）
            array.Push(JsContextObjects.ToJs(engine, jsonList));
        }

        return array;
    }

    // ============================================================
    // JSON 节点 → JS 值（reader：原始节点透传 + 退化方法兜底）
    // ============================================================

    private static JsValue FromJsonNode(Engine engine, JsBridge bridge, JsonNode? node) => node switch
    {
        null => JsValue.Null,
        JsonValue value => FromJsonValue(engine, value),
        JsonObject obj => WrapJsonObject(engine, bridge, obj),
        JsonArray array => WrapJsonArray(engine, bridge, array),
        _ => JsValue.Null,
    };

    private static JsValue FromJsonValue(Engine engine, JsonValue value)
    {
        if (value.TryGetValue<string>(out var s))
        {
            return JsValue.FromObject(engine, s);
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return JsValue.FromObject(engine, b);
        }

        if (value.TryGetValue<double>(out var d))
        {
            return JsValue.FromObject(engine, d);
        }

        return JsValue.FromObject(engine, value.ToJsonString());
    }

    private static JsArray WrapJsonArray(Engine engine, JsBridge bridge, JsonArray array)
    {
        var result = new JsArray(engine, Array.Empty<JsValue>());
        foreach (var entry in array)
        {
            result.Push(FromJsonNode(engine, bridge, entry));
        }

        return result;
    }

    private static JsObject WrapJsonObject(Engine engine, JsBridge bridge, JsonObject obj)
    {
        var result = new JsObject(engine);
        foreach (var (key, value) in obj)
        {
            result.FastSetDataProperty(key, FromJsonNode(engine, bridge, value));
        }

        // 退化方法（reader 同款）：只补 JSON 对象没带的同名字段，原生字段优先。
        // sink 带真实诊断信息——旧实现传 (null, null, null)，这些方法里的异常
        // （attr 的类型错误等）会被静默丢弃，排障时完全看不到。
        var sink = new JsContextObjects.JsErrorSink(bridge.Diagnostics, bridge.SourceKey, bridge.Field);
        if (!result.HasOwnFunction("text"))
        {
            var title = obj.TryGetPropertyValue("title", out var t) ? t?.ToString() ?? string.Empty : string.Empty;
            result.FastSetDataProperty("text", JsContextObjects.CreateFunction(engine, "text", _ => title, sink));
        }

        if (!result.HasOwnFunction("attr"))
        {
            result.FastSetDataProperty("attr", JsContextObjects.CreateFunction(engine, "attr", args =>
            {
                var name = JsPureApi.AsString(args, 0);
                return obj.TryGetPropertyValue(name, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
            }, sink));
        }

        if (!result.HasOwnFunction("hasAttr"))
        {
            result.FastSetDataProperty("hasAttr", JsContextObjects.CreateFunction(engine, "hasAttr", args =>
                (object)obj.ContainsKey(JsPureApi.AsString(args, 0)), sink));
        }

        if (!result.HasOwnFunction("tagName"))
        {
            result.FastSetDataProperty("tagName", JsContextObjects.CreateFunction(engine, "tagName", _ => string.Empty, sink));
        }

        if (!result.HasOwnFunction("html"))
        {
            result.FastSetDataProperty("html", JsContextObjects.CreateFunction(engine, "html", _ => string.Empty, sink));
        }

        if (!result.HasOwnFunction("select"))
        {
            result.FastSetDataProperty("select", JsContextObjects.CreateFunction(engine, "select", _ => new JsArray(engine, Array.Empty<JsValue>()), sink));
        }

        if (!result.HasOwnFunction("toString"))
        {
            result.FastSetDataProperty("toString", JsContextObjects.CreateFunction(engine, "toString", _ => obj.ToJsonString(), sink));
        }

        return result;
    }

    /// <summary>
    /// 对象是否带**自有**同名函数属性。
    /// <para>只查自有属性：旧实现用 <c>Get(name)</c>（走原型链），
    /// <c>Object.prototype.toString/valueOf</c> 会命中 → JSON 元素的自定义
    /// <c>toString</c>（序列化 JSON）永远挂不上，脚本里拼对象拿到
    /// "[object Object]"。</para>
    /// </summary>
    private static bool HasOwnFunction(this JsObject obj, string name)
    {
        var descriptor = obj.GetOwnProperty(name);
        return descriptor is not null && descriptor.Value is Jint.Native.Function.Function;
    }

    // ============================================================
    // HTML 元素快照（对齐 JSoup Element / reader wrapElement）
    // ============================================================

    private static JsObject WrapHtmlElement(Engine engine, JsBridge bridge, HtmlNode node)
    {
        var sink = new JsContextObjects.JsErrorSink(bridge.Diagnostics, bridge.SourceKey, bridge.Field);
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("text", CreateText(engine, sink, () => ElementText(node)));
        obj.FastSetDataProperty("ownText", CreateText(engine, sink, () => OwnText(node)));
        obj.FastSetDataProperty("html", CreateText(engine, sink, () => node.InnerHtml));
        obj.FastSetDataProperty("outerHtml", CreateText(engine, sink, () => node.OuterHtml));
        obj.FastSetDataProperty("attr", JsContextObjects.CreateFunction(engine, "attr", args =>
        {
            var name = JsPureApi.AsString(args, 0);
            // 与 Selectors.AttrValue 同口径：decode 实体（脚本里 attr('data-src') 拿到的应是真 URL）
            return node.Attributes is null ? string.Empty : Selectors.AttrValue(node, name);
        }, sink));
        obj.FastSetDataProperty("hasAttr", JsContextObjects.CreateFunction(engine, "hasAttr", args =>
            (object)(node.Attributes is not null && node.Attributes.Contains(JsPureApi.AsString(args, 0))), sink));
        obj.FastSetDataProperty("tagName", CreateText(engine, sink, () => node.Name));

        // 元素内部继续查询：用 InnerHtml 作为内容，只匹配后代（JSoup select 不含元素自身）
        obj.FastSetDataProperty("select", JsContextObjects.CreateFunction(engine, "select", args =>
        {
            var inner = node.InnerHtml;
            if (string.IsNullOrEmpty(inner))
            {
                return new JsArray(engine, Array.Empty<JsValue>());
            }

            var items = bridge.EvaluateElements?.Invoke(JsPureApi.AsString(args, 0), inner);
            return items is null
                ? new JsArray(engine, Array.Empty<JsValue>())
                : GetElements(engine, bridge, items);
        }, sink));
        obj.FastSetDataProperty("toString", CreateText(engine, sink, () => node.OuterHtml));

        return obj;
    }

    /// <summary>文本快照（正则匹配 / 纯文本片段）：<c>text()</c> = 原串，其余退化为空。</summary>
    private static JsObject WrapTextElement(Engine engine, JsBridge bridge, string text)
    {
        var sink = new JsContextObjects.JsErrorSink(bridge.Diagnostics, bridge.SourceKey, bridge.Field);
        var obj = new JsObject(engine);
        obj.FastSetDataProperty("text", CreateText(engine, sink, () => text));
        obj.FastSetDataProperty("ownText", CreateText(engine, sink, () => text));
        obj.FastSetDataProperty("html", CreateText(engine, sink, () => string.Empty));
        obj.FastSetDataProperty("outerHtml", CreateText(engine, sink, () => string.Empty));
        obj.FastSetDataProperty("attr", CreateText(engine, sink, () => string.Empty));
        obj.FastSetDataProperty("hasAttr", JsContextObjects.CreateFunction(engine, "hasAttr", _ => false, sink));
        obj.FastSetDataProperty("tagName", CreateText(engine, sink, () => "#text"));
        obj.FastSetDataProperty("select", CreateText(engine, sink, () => string.Empty));
        obj.FastSetDataProperty("toString", CreateText(engine, sink, () => text));
        return obj;
    }

    private static JsValue CreateText(Engine engine, JsContextObjects.JsErrorSink sink, Func<string> body) =>
        JsContextObjects.CreateFunction(engine, "text", _ => body(), sink);

    /// <summary>
    /// JSoup <c>Element.text()</c> 语义：后代文本节点合并、压缩空白成单行；
    /// <b>排除 script/style</b>（JSoup 同款：脚本文本不是页面文本）。
    /// </summary>
    private static string ElementText(HtmlNode node)
    {
        var sb = new StringBuilder(64);
        AppendElementText(node, sb);
        return sb.Length == 0 ? string.Empty : RxWhitespace.Replace(sb.ToString(), " ").Trim();
    }

    private static void AppendElementText(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                AppendSpan(sb, child.InnerText);
            }
            else if (child.NodeType == HtmlNodeType.Element
                     && !string.Equals(child.Name, "script", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(child.Name, "style", StringComparison.OrdinalIgnoreCase))
            {
                AppendElementText(child, sb);
            }
        }
    }

    /// <summary>JSoup <c>Element.ownText()</c> 语义：只取<b>直接</b>子文本节点（不含后代）。</summary>
    private static string OwnText(HtmlNode node)
    {
        var sb = new StringBuilder(32);
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                AppendSpan(sb, child.InnerText);
            }
        }

        return sb.Length == 0 ? string.Empty : RxWhitespace.Replace(sb.ToString(), " ").Trim();
    }

    private static void AppendSpan(StringBuilder sb, string text)
    {
        var span = text.AsSpan().Trim();
        if (span.Length == 0)
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(span);
    }

    // ============================================================
    // 数组实例增强（JS 风格 + Java 风格两种作者习惯，见类注释）
    // ============================================================

    /// <summary>
    /// 在数组实例上补 <c>size()/get(i)/toArray()</c> 与委托首元素的
    /// <c>text/attr/html/select</c>（样本里的真实调用形态）。
    /// <para>只挂<b>数组实例</b>，不动 Array.prototype：不污染同引擎里的其它数组。</para>
    /// </summary>
    private static void AttachListExtras(Engine engine, JsBridge bridge, JsArray array, IReadOnlyList<object?> items, JsValue first)
    {
        var sink = new JsContextObjects.JsErrorSink(bridge.Diagnostics, bridge.SourceKey, bridge.Field);

        // 委托函数：首元素有该函数就用它的（list.text() == first.text()）；没有退化为空串
        JsValue Delegate(string name, string fallbackText)
        {
            if (first.IsObject())
            {
                var fn = first.AsObject().Get(name);
                if (fn is Jint.Native.Function.Function)
                {
                    return fn;
                }
            }

            return JsContextObjects.CreateFunction(engine, name, _ => fallbackText, sink);
        }

        array.FastSetDataProperty("size", JsContextObjects.CreateFunction(engine, "size", _ => (double)items.Count, sink));
        array.FastSetDataProperty("get", JsContextObjects.CreateFunction(engine, "get", args =>
        {
            var index = (int)JsPureApi.AsLong(args, 0);
            return index >= 0 && index < items.Count ? WrapItem(engine, bridge, items[index]) : JsValue.Null;
        }, sink));
        array.FastSetDataProperty("toArray", JsContextObjects.CreateFunction(engine, "toArray", _ => array, sink));

        array.FastSetDataProperty("text", Delegate("text", string.Empty));
        array.FastSetDataProperty("attr", Delegate("attr", string.Empty));
        array.FastSetDataProperty("html", Delegate("html", string.Empty));
        array.FastSetDataProperty("select", Delegate("select", string.Empty));
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// JSONPath 求值（自包含实现，覆盖 legado 书源用到的子集）。
/// 支持：$ 根、.name / ['name'] 属性、[*] 通配、[n] 索引、[a:b] 切片、.. 递归下降。
/// 对标 reader jsonpath_query。去掉外部 JsonPath.Net 依赖（net10.0 下引用解析异常，项目踩坑点 9）。
/// <para>性能设计（热路径：每个 JSON 书源的每个字段都走本类）：</para>
/// <list type="number">
/// <item>路径分段缓存：同一书源的规则路径不在每个片段上重新解析（<see cref="ParseCached"/>，有界 1024）；</item>
/// <item>节点重载：调用方已解析的根节点直接复用，不重复全量解析
/// （<see cref="QueryNodes(JsonNode, string)"/> / <see cref="Query(JsonNode, string)"/>）。</item>
/// </list>
/// </summary>
internal static class JsonPathEval
{
    /// <summary>
    /// 路径分段缓存：同一书源的 chapterList / 字段规则会在 N 个片段（章/书）上反复求值，
    /// 逐次重新解析路径文本的开销随片段数线性放大。Seg 解析后不再改动，可跨线程共享。
    /// 有界（上限 PathCacheLimit）防止海量书源把缓存撑爆。
    /// </summary>
    private const int PathCacheLimit = 1024;

    private static readonly ConcurrentDictionary<string, List<Seg>?> PathCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 取（并缓存）路径分段。缓存 key 是**归一化后**的路径（$ 前缀有无不影响语义）。
    /// 并发下重复解析无害（结果等价，后者覆盖前者）。
    /// </summary>
    private static List<Seg>? ParseCached(string normalizedPath)
    {
        if (PathCache.TryGetValue(normalizedPath, out var cached))
        {
            return cached;
        }

        var segs = Parse(normalizedPath);
        if (PathCache.Count < PathCacheLimit)
        {
            PathCache[normalizedPath] = segs;
        }

        return segs;
    }

    /// <summary>对 JSON 文本求值，返回各匹配节点的字符串值（多值按 \n 拼接由调用方决定）。</summary>
    public static List<string> Query(string jsonContent, string path)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return result;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonContent);
        }
        catch
        {
            // 非 JSON 文本：空结果（调用方按无匹配处理）
            return result;
        }

        if (root is null)
        {
            return result;
        }

        return Query(root, path);
    }

    /// <summary>
    /// 对已解析的 JSON 节点求值（不重复解析文本）。用于单本书片段只解析一次后提取其多个字段。
    /// </summary>
    public static List<string> Query(JsonNode? root, string path)
    {
        var result = new List<string>();
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return result;
        }

        var segs = ParseCached(NormalizePath(path));
        if (segs is null)
        {
            return result;
        }

        var nodes = Eval(root, segs);
        foreach (var n in nodes)
        {
            var s = NodeToString(n);
            if (s is not null)
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>
    /// 返回匹配到的 JSON 节点列表（不转字符串），供调用方逐节点作为片段只解析一次。
    /// </summary>
    public static List<JsonNode?> QueryNodes(string jsonContent, string path)
    {
        var result = new List<JsonNode?>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return result;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonContent);
        }
        catch
        {
            return result;
        }

        if (root is null)
        {
            return result;
        }

        return QueryNodes(root, path);
    }

    /// <summary>
    /// 对已解析的 JSON 节点求值并返回节点列表（不重复解析文本）。
    /// 目录/列表模式先解析整份 body 一次，这里直接复用，避免二次全量解析
    /// （优化前大目录页的 JSON 要被完整解析 3~4 次）。
    /// </summary>
    public static List<JsonNode?> QueryNodes(JsonNode? root, string path)
    {
        var result = new List<JsonNode?>();
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return result;
        }

        var segs = ParseCached(NormalizePath(path));
        if (segs is null)
        {
            return result;
        }

        return Eval(root, segs);
    }

    /// <summary>
    /// 路径归一化（对标 reader jsonpath_query 的开头处理）：
    /// legado 允许省略 <c>$</c> 前缀（如 <c>data.books</c>），且前导点要剥掉，
    /// 否则会拼成 <c>$..</c>（递归下降）导致语义反转（项目铁律 1）。
    /// </summary>
    private static string NormalizePath(string path)
    {
        var p = path.Trim();
        if (p.Length == 0 || p[0] == '$')
        {
            return p;
        }

        return p.TrimStart('.');
    }

    /// <summary>分段类型：Root（$）/ Recursive（..）/ Name（.a 或 ['a']）/ Wildcard（*）/ Index（[n]）/ Slice（[a:b]）。</summary>
    private enum SegType
    {
        Root,
        Recursive,
        Name,
        Wildcard,
        Index,
        Slice,
    }

    /// <summary>
    /// 路径分段（解析后不可变，可跨线程共享——PathCache 的前提）。
    /// I1/I2/I3 复用为 Index 的下标、Slice 的 start/end/step。
    /// </summary>
    private sealed class Seg
    {
        public SegType Type;
        public string? Name;
        public int I1, I2, I3;
    }

    /// <summary>
    /// 路径解析（手写递归下降，对标 reader jsonpath 的解析子集）。
    /// <para>支持的全部构造：</para>
    /// <list type="bullet">
    /// <item>$ 根、.name / ['name'] / ["name"] 属性；</item>
    /// <item>.* 与 [*] 通配（对象取值集合 / 数组取元素）；</item>
    /// <item>[n] 索引（支持负数，-1 = 末元素）；</item>
    /// <item>[a:b] 与 [a:b:c] 切片（负数与越界钳制）；</item>
    /// <item>..name / ..* / ..[n] 递归下降；</item>
    /// <item>省略 $ 的相对路径（裸字段名开头）。</item>
    /// </list>
    /// </summary>
    private static List<Seg>? Parse(string path)
    {
        var segs = new List<Seg>();
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            // 空白跳过
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                i++;
                continue;
            }

            // $ 根
            if (c == '$')
            {
                segs.Add(new Seg { Type = SegType.Root });
                i++;
                continue;
            }

            // .. 递归下降：..name / ..* / ..[n] / ..['name']
            if (c == '.' && i + 1 < path.Length && path[i + 1] == '.')
            {
                i += 2;
                if (i < path.Length && path[i] == '.')
                {
                    i++; // 吞掉多余的点
                }

                if (i < path.Length && path[i] == '*')
                {
                    segs.Add(new Seg { Type = SegType.Recursive, Name = "*" });
                    i++;
                    continue;
                }

                if (i < path.Length && path[i] == '[')
                {
                    var (inner, next) = ReadBracket(path, i);
                    i = next;
                    if (inner == "*")
                    {
                        segs.Add(new Seg { Type = SegType.Recursive, Name = "*" });
                    }
                    else if (int.TryParse(inner, out var idx))
                    {
                        segs.Add(new Seg { Type = SegType.Recursive, Name = idx.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    }
                    else
                    {
                        segs.Add(new Seg { Type = SegType.Recursive, Name = inner.Trim('\'', '"') });
                    }

                    continue;
                }

                var start = i;
                while (i < path.Length && path[i] != '[' && path[i] != '.')
                {
                    i++;
                }

                segs.Add(new Seg { Type = SegType.Recursive, Name = path.Substring(start, i - start) });
                continue;
            }

            // . 单层：.* 通配 或 .name 属性
            if (c == '.')
            {
                i++;
                if (i < path.Length && path[i] == '*')
                {
                    segs.Add(new Seg { Type = SegType.Wildcard });
                    i++;
                    continue;
                }

                var s = i;
                while (i < path.Length && path[i] != '[' && path[i] != '.')
                {
                    i++;
                }

                var name = path.Substring(s, i - s);
                if (name.Length > 0)
                {
                    segs.Add(new Seg { Type = SegType.Name, Name = name });
                }

                continue;
            }

            // [ 括号段：* / ['name'] / [a:b] / [n]
            if (c == '[')
            {
                var (inner, next) = ReadBracket(path, i);
                i = next;
                if (inner == "*")
                {
                    segs.Add(new Seg { Type = SegType.Wildcard });
                }
                else if (inner.Length > 1 && (inner[0] == '\'' || inner[0] == '"'))
                {
                    segs.Add(new Seg { Type = SegType.Name, Name = inner.Trim('\'', '"') });
                }
                else if (inner.Contains(":", StringComparison.Ordinal) || inner.Contains(",", StringComparison.Ordinal))
                {
                    var parts = inner.Split(':');
                    var a = parts.Length > 0 && int.TryParse(parts[0], out var aa) ? aa : 0;
                    var b = parts.Length > 1 && int.TryParse(parts[1], out var bb) ? bb : int.MaxValue;
                    var step = parts.Length > 2 && int.TryParse(parts[2], out var ss) ? ss : 1;
                    segs.Add(new Seg { Type = SegType.Slice, I1 = a, I2 = b, I3 = step });
                }
                else if (int.TryParse(inner, out var idx))
                {
                    segs.Add(new Seg { Type = SegType.Index, I1 = idx });
                }
                else
                {
                    segs.Add(new Seg { Type = SegType.Name, Name = inner });
                }

                continue;
            }

            // 裸字段名（相对路径，无 $ 前缀）
            var bs = i;
            while (i < path.Length && path[i] != '[' && path[i] != '.')
            {
                i++;
            }

            var bname = path.Substring(bs, i - bs);
            if (bname.Length > 0)
            {
                segs.Add(new Seg { Type = SegType.Name, Name = bname });
            }
        }

        return segs;
    }

    /// <summary>读取 [openIdx 起的配对 ] 内容，返回 (内部文本 trim, 下一个下标)。无闭合 ] 时吃到路径末尾。</summary>
    private static (string Inner, int Next) ReadBracket(string path, int openIdx)
    {
        var close = path.IndexOf(']', openIdx);
        if (close < 0)
        {
            return (string.Empty, path.Length);
        }

        return (path.Substring(openIdx + 1, close - openIdx - 1).Trim(), close + 1);
    }

    /// <summary>
    /// 按分段逐级求值。每级把"当前节点集合"映射为"下一级节点集合"（集合语义，非单路径），
    /// 因此 [*] 之后可继续接 .name 等多级。最后统一展平数组
    /// （reader jsonpath_query：结果里若含数组，直接展平为其元素——
    /// legado 的 data.chapter_lists 不带 [*] 即指向数组本身，期望取到它的元素，项目铁律 1）。
    /// </summary>
    private static List<JsonNode?> Eval(JsonNode? node, List<Seg> segs)
    {
        var current = new List<JsonNode?> { node };
        foreach (var seg in segs)
        {
            var next = new List<JsonNode?>();
            foreach (var n in current)
            {
                if (n is null)
                {
                    continue;
                }

                switch (seg.Type)
                {
                    case SegType.Root:
                    {
                        // $ 根：集合不变（路径起点）
                        next.Add(n);
                        break;
                    }

                    case SegType.Name:
                    {
                        // .name：对象取属性；数组/标点上无此属性 → 无输出（不报错）
                        if (n is JsonObject obj && seg.Name is not null
                            && obj.TryGetPropertyValue(seg.Name, out var v))
                        {
                            next.Add(v);
                        }

                        break;
                    }

                    case SegType.Wildcard:
                    {
                        // *：对象取全部属性值，数组取全部元素
                        if (n is JsonObject o)
                        {
                            foreach (var kv in o)
                            {
                                next.Add(kv.Value);
                            }
                        }
                        else if (n is JsonArray a)
                        {
                            foreach (var e in a)
                            {
                                next.Add(e);
                            }
                        }

                        break;
                    }

                    case SegType.Index:
                    {
                        // [n]：数组下标（支持负数，-1 = 末元素）；越界无输出
                        if (n is JsonArray arr && seg.I1 >= -arr.Count && seg.I1 < arr.Count)
                        {
                            var idx = seg.I1 < 0 ? arr.Count + seg.I1 : seg.I1;
                            if (idx >= 0 && idx < arr.Count)
                            {
                                next.Add(arr[idx]);
                            }
                        }

                        break;
                    }

                    case SegType.Slice:
                    {
                        // [a:b:c]：切片（负数相对末尾；start<0 钳到 0；end>count 钳到 count；step<=0 视 1）
                        if (n is JsonArray arr)
                        {
                            var count = arr.Count;
                            var start = seg.I1;
                            if (start < 0)
                            {
                                start = count + start;
                            }

                            if (start < 0)
                            {
                                start = 0;
                            }

                            var end = seg.I2 == int.MaxValue ? count : seg.I2;
                            if (end < 0)
                            {
                                end = count + end;
                            }

                            if (end > count)
                            {
                                end = count;
                            }

                            var step = seg.I3 == 0 ? 1 : seg.I3;
                            for (var k = start; k < end; k += step)
                            {
                                if (k >= 0 && k < count)
                                {
                                    next.Add(arr[k]);
                                }
                            }
                        }

                        break;
                    }

                    case SegType.Recursive:
                    {
                        // ..name / ..* / ..[n]：先收集当前节点整棵子树（含自身），再按 name/索引过滤
                        var collected = new List<JsonNode?>();
                        foreach (var d in AllNodes(n))
                        {
                            collected.Add(d);
                        }

                        var outNodes = new List<JsonNode?>();
                        if (seg.Name == "*" || string.IsNullOrEmpty(seg.Name))
                        {
                            outNodes = collected;
                        }
                        else if (int.TryParse(seg.Name, out var ri))
                        {
                            foreach (var d in collected)
                            {
                                if (d is JsonArray arr && ri >= -arr.Count && ri < arr.Count)
                                {
                                    var k = ri < 0 ? arr.Count + ri : ri;
                                    if (k >= 0 && k < arr.Count)
                                    {
                                        outNodes.Add(arr[k]);
                                    }
                                }
                            }
                        }
                        else
                        {
                            foreach (var d in collected)
                            {
                                if (d is JsonObject o && o.TryGetPropertyValue(seg.Name, out var v))
                                {
                                    outNodes.Add(v);
                                }
                            }
                        }

                        next = outNodes;
                        break;
                    }
                }
            }

            current = next;
        }

        // reader jsonpath_query：结果里若含数组，直接展平为其元素
        return FlattenArrays(current);
    }

    /// <summary>
    /// 结果展平：列表里若含数组节点，替换为其元素（legado chapter_lists 语义，见 Eval 注释）。
    /// 快路径：无数组时零分配返回原列表。
    /// </summary>
    private static List<JsonNode?> FlattenArrays(List<JsonNode?> nodes)
    {
        var hasArray = false;
        foreach (var n in nodes)
        {
            if (n is JsonArray)
            {
                hasArray = true;
                break;
            }
        }

        if (!hasArray)
        {
            // 快路径：没有数组时零分配直接返回
            return nodes;
        }

        var result = new List<JsonNode?>(nodes.Count + 8);
        foreach (var n in nodes)
        {
            if (n is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        result.Add(item);
                    }
                }
            }
            else
            {
                result.Add(n);
            }
        }

        return result;
    }

    /// <summary>深度优先收集整棵子树（含自身），供递归下降用。迭代实现（栈），无递归深度限制。</summary>
    private static IEnumerable<JsonNode?> AllNodes(JsonNode? node)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is null)
            {
                continue;
            }

            yield return n;

            if (n is JsonObject o)
            {
                foreach (var kv in o)
                {
                    stack.Push(kv.Value);
                }
            }
            else if (n is JsonArray a)
            {
                foreach (var e in a)
                {
                    stack.Push(e);
                }
            }
        }
    }

    /// <summary>
    /// 节点转字符串（对标 reader value_to_string）：
    /// 字符串标点去引号（JSON 源码形态）；其他标点取原文；对象/数组取 JSON 序列化。
    /// null 节点返回 null（调用方过滤，不入结果）。
    /// </summary>
    private static string? NodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            var raw = node.ToString();
            // 字符串标点：ToString 带引号，去掉才是字段值
            if (raw.Length >= 2 && raw[0] == '"')
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(raw);
                }
                catch
                {
                    return raw;
                }
            }

            return raw;
        }

        // 对象/数组：序列化为 JSON 文本（作为整体字段值）
        return node.ToJsonString();
    }
}

using System.Text.Json.Nodes;
using HtmlAgilityPack;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 单条片段（一本书 / 一章）的解析上下文。
/// 片段的 HTML 文档 / JSON 节点只解析一次并缓存在本实例，供该片段的 M 个字段规则复用，
/// 消除「N 个片段 × M 个字段」的重复解析（阶段 1 最大的一处开销）。
/// <para>三种构造形态（按来源）：</para>
/// <list type="number">
/// <item><see cref="FragmentContext(string)"/>： OuterHtml 文本（正则匹配产物、JS 输出、
/// body/空列表规则的整体内容）；</item>
/// <item><see cref="FragmentContext(JsonNode)"/>：JSONPath 列表模式命中的节点
/// （Raw 惰性序列化，纯 JSONPath 字段不付 ToJsonString 开销）；</item>
/// <item><see cref="FragmentContext(HtmlNode)"/>：列表选择器命中的活节点
/// （Raw 惰性 OuterHtml，Html 惰性克隆进独立文档）。</item>
/// </list>
/// <para>活节点克隆的必要性：直接查询活节点会让"选择器命中片段根元素自身"的语义丢失
/// （如 @css:li@class 作用于 li 片段），且绝对 XPath 会逃逸到整份文档；
/// 克隆进以本节点为唯一子节点的新文档后，两种语义都与 OuterHtml 重解析完全一致，
/// 但省掉每个片段一次的字符串序列化 + 重新解析。</para>
/// <para>线程约定：一个实例同时只被一个线程使用（片段处理串行），故惰性解析用无锁 ??=；
/// 并行搜索时每个片段各自持有独立实例，互不共享。</para>
/// </summary>
internal sealed class FragmentContext
{
    // JSON 判定三态缓存：Unknown（未探测）/ Yes（是 JSON，_json 有效）/ No（不是）。
    private const int JsonUnknown = 0;
    private const int JsonYes = 1;
    private const int JsonNo = 2;

    // 三种来源各自最多有一个非 null：_raw（文本）/ _jsonNode（JSON 节点）/ _htmlNode（HTML 活节点）。
    private readonly string? _raw;
    private readonly JsonNode? _jsonNode;
    private readonly HtmlNode? _htmlNode;

    // 惰性计算结果缓存。
    private string? _lazyRaw;
    private HtmlDocument? _html;
    private JsonNode? _json;
    private int _jsonState;

    /// <summary>文本形态构造（片段 = 一段 HTML/JSON/纯文本）。</summary>
    public FragmentContext(string raw)
    {
        _raw = raw;
    }

    /// <summary>
    /// 已持有 JSON 节点时直接构造；Raw 推迟到真正需要文本时再序列化。
    /// JSON 判定直接置位（节点必然是 JSON），免去首字符嗅探。
    /// </summary>
    public FragmentContext(JsonNode? json)
    {
        _jsonNode = json;
        _json = json;
        _jsonState = json is null ? JsonNo : JsonYes;
    }

    /// <summary>列表选择器命中的活节点：Html/Raw 均惰性生成（见类型注释）。</summary>
    public FragmentContext(HtmlNode node)
    {
        _htmlNode = node;
    }

    /// <summary>已持有 JSON 节点且已知其文本形态时使用（避免惰性序列化的例外路径）。</summary>
    public FragmentContext(JsonNode? json, string raw)
    {
        _raw = raw;
        _jsonNode = json;
        _json = json;
        _jsonState = json is null ? JsonNo : JsonYes;
    }

    /// <summary>
    /// 片段的文本形态（按来源惰性计算，结果缓存）：
    /// 文本构造 → 原串；活节点 → node.OuterHtml；JSON 节点 → node.ToJsonString()。
    /// <para>惰性意义：JSON 书源的字段规则几乎都是 JSONPath，永远不碰 Raw；
    /// HTML 片段的正则/JS 规则才需要 OuterHtml。只有这些场景才付序列化开销。</para>
    /// </summary>
    public string Raw
    {
        get
        {
            if (_raw is not null)
            {
                return _raw;
            }

            if (_htmlNode is not null)
            {
                return _lazyRaw ??= _htmlNode.OuterHtml;
            }

            return _lazyRaw ??= _jsonNode?.ToJsonString() ?? string.Empty;
        }
    }

    /// <summary>
    /// 惰性解析并缓存的 HTML 文档。
    /// <para>活节点形态：克隆进独立文档（CloneNode 深拷贝子树 + AppendChild 挂到新 DocumentNode）。
    /// 已验证语义：从新 DocumentNode 出发的 CSS/XPath 查询都限定在本片段内
    /// （绝对 XPath 不会逃逸到原文档——HAP 的 navigator 以 DocumentNode 的所有者文档为根），
    /// 且克隆根作为唯一子节点可被 CSS 匹配（保持 OuterHtml 重解析的根匹配语义）。</para>
    /// </summary>
    public HtmlDocument Html
    {
        get
        {
            if (_html is not null)
            {
                return _html;
            }

            if (_htmlNode is not null)
            {
                var wrapped = new HtmlDocument();
                wrapped.DocumentNode.AppendChild(_htmlNode.CloneNode(true));
                return _html = wrapped;
            }

            // 文本形态：按需解析（含 JSON 文本被当 HTML 用的边缘情况——选择器匹配不到即空）
            return _html ??= Selectors.Load(Raw);
        }
    }

    /// <summary>
    /// 底层 HTML 活节点（阶段 4-5，元素快照用）；非"活节点"形态为 null
    /// （文本/JSON 形态没有节点，调用方走文本快照兜底）。
    /// </summary>
    public HtmlNode? Node => _htmlNode;

    /// <summary>惰性解析并缓存的 JSON 节点（非 JSON 片段为 null）。</summary>
    public JsonNode? Json
    {
        get
        {
            ProbeJson();
            return _json;
        }
    }

    /// <summary>
    /// 片段是否为 JSON。结果在本实例内缓存：整个片段生命周期最多解析一次。
    /// 先做首字符嗅探（O(1)），HTML 片段完全不触发 JSON 解析。
    /// </summary>
    public bool IsJson
    {
        get
        {
            ProbeJson();
            return _jsonState == JsonYes;
        }
    }

    /// <summary>
    /// JSON 探测（惰性，三态缓存）：
    /// ① 已判定过 → 直接返回；
    /// ② 无文本来源（活节点/JSON 节点构造路径）→ 置 No（元素片段不是 JSON 文本）；
    /// ③ 首字符不是 { 或 [ → 置 No（O(1) 拒绝 HTML）；
    /// ④ 尝试 JsonNode.Parse：成功置 Yes 并缓存节点，失败置 No（形似 JSON 的截断响应体按文本处理）。
    /// </summary>
    private void ProbeJson()
    {
        if (_jsonState != JsonUnknown)
        {
            return;
        }

        if (_raw is null)
        {
            _jsonState = JsonNo;
            return;
        }

        var t = _raw.AsSpan().Trim();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '['))
        {
            _jsonState = JsonNo;
            return;
        }

        try
        {
            _json = JsonNode.Parse(_raw);
            _jsonState = JsonYes;
        }
        catch
        {
            // 形似 JSON 但解析失败：按非 JSON 处理，后续按 HTML/文本规则求值
            _jsonState = JsonNo;
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源 URL 编译（逐个函数对标 reader <c>src/crawler/url_analyzer.rs</c>）。
/// 把书源的 searchUrl / 章节 URL 模板编译成可直接执行的 <see cref="RequestSpec"/>。
/// <para>处理顺序与源项目严格一致（顺序不同会导致 <c>{{}}</c> 与 <c>,{json}</c> 的相对位置变化，
/// 这是项目铁律，不可调换）：</para>
/// <list type="number">
/// <item>归一化（Normalize）；</item>
/// <item>&lt;js&gt;…&lt;/js&gt; / @js: 段（eval_url_js_segments）；</item>
/// <item>{{表达式}}（replace_inline_js：表达式交给 JS 求值，不是字符串替换）；</item>
/// <item>{key} / {page} / searchKey / searchPage（replace_legacy_placeholders）；</item>
/// <item>&lt;a,b,c&gt; 分页选项（replace_page_choices）；</item>
/// <item>切出 ,{json} 选项块（split_url_options，括号/引号深度扫描）；</item>
/// <item>解析选项块 + 请求编码（encode_get_query）。</item>
/// </list>
/// <para>线程约定：实例持有惰性创建的 Jint 引擎，<b>不跨线程共享</b>——
/// 并行路径（批量正文/多页目录）每个任务各自 new UrlAnalyzer。</para>
/// </summary>
public sealed class UrlAnalyzer
{
    // 常量正则：编译一次长期使用（RegexOptions.Compiled 让重复求值走 JIT 后的匹配器）。

    /// <summary>{{表达式}} 匹配（非贪婪，[\w\W] 兼容换行的脚本）。</summary>
    private static readonly Regex TemplateRegex = new(@"\{\{([\w\W]*?)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// JS 段匹配：二选一形态 —— &lt;js&gt;…&lt;/js&gt;（组1）或 @js: 开头吞掉后续全部（组2）。
    /// (?is) = 忽略大小写 + 单行（. 匹配换行）。
    /// </summary>
    private static readonly Regex JsSegmentRegex =
        new(@"(?is)<js>(.*?)</js>|@js:([\w\W]*)", RegexOptions.Compiled);

    /// <summary>&lt;a,b,c&gt; 分页选项匹配。</summary>
    private static readonly Regex PageChoiceRegex = new(@"<(.*?)>", RegexOptions.Compiled);

    /// <summary>复用单个 Jint 引擎（构造昂贵）；UrlAnalyzer 实例不跨线程共享。</summary>
    private Engine? _engine;

    /// <summary>
    /// 编译书源 URL 模板为请求描述（七步，顺序即项目铁律，见类型注释）。
    /// </summary>
    public RequestSpec BuildSpec(string ruleUrl, string key, int page, string? baseUrl = null)
    {
        // ① 归一化：去控制字符 + trim。空 URL → 空 spec（调用方按"无可请求"处理）
        var raw = UrlUtil.Normalize(ruleUrl);
        if (raw.Length == 0)
        {
            return new RequestSpec();
        }

        // ② <js>…</js> / @js: 段（可能改写整个 URL，如签名参数）
        raw = EvalUrlJsSegments(raw, key, page);
        // ③ {{表达式}}（{{key}}/{{page}} 走快捷替换，其余交 JS）
        raw = ReplaceInlineJs(raw, key, page);
        // ④ {key} / {page} / searchKey / searchPage 旧版占位符（纯字符串替换）
        raw = ReplaceLegacyPlaceholders(raw, key, page);
        // ⑤ <a,b,c> 分页选项：按 page 取下标（越界取最后一项）
        raw = ReplacePageChoices(raw, page);
        // ⑥ 切出 ,{json} 选项块（括号/引号深度扫描，含 data: 特判）
        var (urlPart, optionsJson) = SplitUrlOptions(raw);

        // ⑦ 先按"严格 JSON"解析选项块，失败再交 JS 宽松解析（GSON 风格单引号/无引号键名）
        var spec = new RequestSpec { Url = urlPart };
        if (optionsJson is not null && TryParseOptions(optionsJson, out var parsed))
        {
            spec = parsed with { Url = urlPart };
        }

        // ⑧ 请求编码：GET 查询串百分号编码（已含 % 视为源已编码，避免二次编码）
        return spec with { Url = EncodeUrl(spec.Url, spec.Charset) };
    }

    /// <summary>旧版元组入口（保持对外兼容；内部实现同 BuildSpec）。</summary>
    public (string Url, string? Method, string? Body, Dictionary<string, string>? Headers, string? Charset)
        Build(string searchUrl, string key, int page)
    {
        var spec = BuildSpec(searchUrl, key, page);
        return (spec.Url, spec.Method, spec.Body, spec.Headers?.ToDictionary(k => k.Key, v => v.Value), spec.Charset);
    }

    // ---- ②：JS 段求值 ----

    /// <summary>
    /// 步骤②：求值 URL 里的 &lt;js&gt;…&lt;/js&gt; / @js: 段（reader eval_url_js_segments）。
    /// <para>两种形态行为不同：</para>
    /// <list type="bullet">
    /// <item>&lt;js&gt;…&lt;/js&gt;：脚本输出**替换该段**，后续内容保留（一段 URL 里可有多段）；</item>
    /// <item>@js:…：脚本输出**吞掉后续全部内容**（reader 命中后立即 return）——
    /// 即 @js: 必须是最后一段，其后写什么都不生效。</item>
    /// </list>
    /// <para>@result 占位符：脚本输出里可含 "@result" 指代自身（递归拼接场景），
    /// 这里在替换段与吞尾分支各做一次 sb.Replace("@result", result) 兑现。</para>
    /// </summary>
    private string EvalUrlJsSegments(string rule, string key, int page)
    {
        var matches = JsSegmentRegex.Matches(rule);
        if (matches.Count == 0)
        {
            // 无 JS 段：原样返回（绝大多数 URL 走此快路径，零分配）
            return rule;
        }

        var sb = new StringBuilder(rule.Length + 32);
        var pos = 0;
        foreach (Match m in matches)
        {
            // 拷贝匹配段之前的原文
            sb.Append(rule, pos, m.Index - pos);
            pos = m.Index + m.Length;

            // 组1 = <js>…</js> 内脚本；组2 = @js: 后脚本（二者必有其一命中）
            var script = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var result = RunJs(script, key, page);
            ReplacePlaceholder(sb, result);
            sb.Append(result);

            if (m.Groups[2].Success)
            {
                // @js: 形态：兑现 @result 后立即结束（后续内容被吞）
                ReplacePlaceholder(sb, result);
                return sb.ToString().Replace("@result", result);
            }
        }

        // 拷贝最后一个匹配段之后的原文
        sb.Append(rule, pos, rule.Length - pos);
        return sb.ToString();
    }

    // ---- ③：{{表达式}} ----

    /// <summary>
    /// 步骤③：替换 {{表达式}}（reader replace_inline_js）。
    /// <para>{{key}} / {{page}} 是快捷项（不走 JS，直接插值）；其余表达式交 Jint 求值
    /// （如 {{new Date().getTime()}}、{{java.md5(...)}}）。</para>
    /// </summary>
    private string ReplaceInlineJs(string rule, string key, int page) =>
        TemplateRegex.Replace(rule, m =>
        {
            var expr = m.Groups[1].Value.Trim();
            return expr switch
            {
                "key" => key,
                "page" => page.ToString(CultureInfo.InvariantCulture),
                _ => RunJs(expr, key, page),
            };
        });

    /// <summary>
    /// 步骤④：旧版占位符纯字符串替换（reader replace_legacy_placeholders）。
    /// {key}/{page} 是新版写法，searchKey/searchPage 是 legado 早期写法，四种都认。
    /// </summary>
    private static string ReplaceLegacyPlaceholders(string rule, string key, int page)
    {
        var p = page.ToString(CultureInfo.InvariantCulture);
        return rule
            .Replace("{key}", key)
            .Replace("{page}", p)
            .Replace("searchKey", key)
            .Replace("searchPage", p);
    }

    /// <summary>
    /// 步骤⑤：&lt;a,b,c&gt; 分页选项（reader replace_page_choices）。
    /// 按 page-1 取下标；越界（page 超出选项数）取**最后一项**（reader 行为，不是报错）。
    /// </summary>
    private string ReplacePageChoices(string rule, int page) =>
        PageChoiceRegex.Replace(rule, m =>
        {
            var parts = m.Groups[1].Value.Split(',');
            var idx = page - 1;
            // 越界取最后一项（reader 行为）
            return parts.Length == 0
                ? string.Empty
                : parts[(idx >= 0 && idx < parts.Length) ? idx : parts.Length - 1].Trim();
        });

    // ---- ⑥：选项块切分与解析 ----

    /// <summary>
    /// 步骤⑥：按括号/引号深度扫描切出最外层 <c>,{...}</c>（reader split_url_options）。
    /// <para>规则：</para>
    /// <list type="number">
    /// <item>只在深度 0（不在任何括号/引号内）的逗号处尝试切分；</item>
    /// <item>逗号右侧 trim 后必须以 <c>{</c> 开头才算选项块（否则是普通 URL 参数分隔符）；</item>
    /// <item>选项块取到**最后一个 <c>}</c>**（块内部可能还有 }）；</item>
    /// <item><c>data:</c> URL 特判：跳过内容里的第一个逗号（那是数据自身的分隔符）。</item>
    /// </list>
    /// </summary>
    private static (string Url, string? Options) SplitUrlOptions(string rule)
    {
        var depth = 0;
        var inQuote = false;
        var skipFirstDataComma = rule.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        var seenDataComma = false;

        for (var i = 0; i < rule.Length; i++)
        {
            var c = rule[i];
            if (inQuote)
            {
                // 引号内：只跟踪闭合引号，其余一切字符（含逗号括号）都是数据
                if (c == '"')
                {
                    inQuote = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuote = true;
                    continue;
                case '{':
                case '[':
                case '(':
                    depth++;
                    continue;
                case '}':
                case ']':
                case ')':
                    depth = Math.Max(0, depth - 1);
                    continue;
            }

            if (c == ',' && depth == 0 && i + 1 < rule.Length)
            {
                if (skipFirstDataComma && !seenDataComma)
                {
                    // data: 内容的第一个逗号是数据本身的分隔符，不是选项起点
                    seenDataComma = true;
                    continue;
                }

                var rest = rule.Substring(i + 1).TrimStart();
                if (rest.StartsWith("{", StringComparison.Ordinal))
                {
                    // 取到最后一个 '}'：选项块本身内部可能有 '}'
                    var end = rule.LastIndexOf('}');
                    if (end > i)
                    {
                        // 含右括号：取 [i+1, end]（闭区间），别把收尾的 '}' 截掉
                        return (rule.Substring(0, i).Trim(), rule.Substring(i + 1, end - i).Trim());
                    }
                }
            }
        }

        return (rule, null);
    }

    /// <summary>
    /// 解析选项块 JSON → <see cref="RequestSpec"/>（reader parse_url_options）。
    /// <para>三级策略：① 严格 JSON 解析；② 失败则交 JS（JSON.stringify 宽松对象字面量），
    /// 支持 legado GSON 宽松模式的常见写法（单引号键名/无引号键名/尾逗号）；
    /// ③ 都失败返回 false，调用方按"无选项"处理。</para>
    /// <para>字段语义（均与 reader 对齐）：method 先转大写再匹配（故 "post" 也能识别），
    /// 非标准方法回落 GET；headers 支持对象与"JSON 字符串"两种形态；retry/timeout 取整数。</para>
    /// </summary>
    private bool TryParseOptions(string json, out RequestSpec spec)
    {
        spec = new RequestSpec();

        // ① 严格 JSON；② 失败 → JS 宽松解析（第三级回退）
        using var doc = TryParseStrict(json) ?? TryParseViaJs(json);
        if (doc is null)
        {
            return false;
        }

        var root = doc.RootElement;
        string? method = null;
        string? body = null;
        string? charset = null;
        Dictionary<string, string>? headers = null;
        var retry = RequestSpec.DefaultRetry;
        int? timeout = null;

        // method：先转大写再匹配（reader 行为，书源里常见的 "post" 因此能正确识别）
        if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
        {
            var upper = (m.GetString() ?? string.Empty).Trim().ToUpperInvariant();
            method = upper is "POST" or "HEAD" or "PUT" or "PATCH" or "DELETE" ? upper : "GET";
        }

        // body：字符串取原值，非字符串（对象/数组）取 JSON 原文
        if (root.TryGetProperty("body", out var b))
        {
            body = b.ValueKind == JsonValueKind.String ? b.GetString() : b.GetRawText();
        }

        if (root.TryGetProperty("charset", out var c) && c.ValueKind == JsonValueKind.String)
        {
            charset = c.GetString();
        }

        // headers：两种形态 —— JSON 对象，或一个"JSON 字符串"（reader headers_from_value 再解析一次）
        if (root.TryGetProperty("headers", out var h))
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (h.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in h.EnumerateObject())
                {
                    headers[NormalizeHeaderName(prop.Name)] =
                        prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : prop.Value.GetRawText();
                }
            }
            else if (h.ValueKind == JsonValueKind.String)
            {
                var nested = h.GetString();
                if (!string.IsNullOrWhiteSpace(nested) && TryParseHeaderText(nested, headers))
                {
                    // 已填充
                }
            }
        }

        if (root.TryGetProperty("retry", out var r) && r.TryGetInt32(out var rv))
        {
            retry = Math.Max(0, rv);
        }

        if (root.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var tv))
        {
            timeout = tv;
        }

        // 阶段 4 预留字段：webView / webJs / serverID(serverId)；4-7 起消费 webViewDelayTime
        var webView = root.TryGetProperty("webView", out var wv) && IsTruthy(wv);
        var webJs = root.TryGetProperty("webJs", out var wj) && wj.ValueKind == JsonValueKind.String ? wj.GetString() : null;
        var serverId = root.TryGetProperty("serverID", out var sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()
            : root.TryGetProperty("serverId", out var sid2) && sid2.ValueKind == JsonValueKind.String
                ? sid2.GetString()
                : null;

        // webViewDelayTime：页面加载完成后的额外等待（毫秒；legado BackstageWebView 同名选项）
        var delayMs = 0;
        if (root.TryGetProperty("webViewDelayTime", out var dt) && dt.TryGetInt32(out var dtv))
        {
            delayMs = Math.Clamp(dtv, 0, 30_000);
        }

        spec = new RequestSpec
        {
            Method = method ?? "GET",
            Body = body,
            Charset = charset,
            Headers = headers,
            Retry = retry,
            TimeoutSeconds = timeout,
            WebView = webView,
            WebViewDelayMs = delayMs,
            WebJs = webJs,
            ServerId = serverId,
        };
        return true;
    }

    /// <summary>严格 JSON 解析；失败返回 null（由调用方走宽松回退）。</summary>
    private static JsonDocument? TryParseStrict(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// JS 宽松对象字面量 → JSON 文本 → 严格解析（reader 的第三级回退）。
    /// 用 Jint 的 JSON.stringify 把 {a:1,'b':2,} 这类写法规范化。
    /// </summary>
    private JsonDocument? TryParseViaJs(string json)
    {
        try
        {
            _engine ??= new Engine();
            var text = _engine.Evaluate($"JSON.stringify({json})").AsString();
            return string.IsNullOrEmpty(text) ? null : JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>headers 的"JSON 字符串"形态再解析一次（reader headers_from_value）。</summary>
    private static bool TryParseHeaderText(string text, Dictionary<string, string> headers)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                headers[NormalizeHeaderName(prop.Name)] =
                    prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.GetRawText();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>请求头名归一化：Unicode 连字符（U+2010~2015/2212）→ ASCII '-'（reader:309-319）。</summary>
    private static string NormalizeHeaderName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212'
                ? '-'
                : c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 选项块真值判定（webView: true/"true"/1/0 均为真，与 reader <c>is_truthy_webview</c> 口径一致）。
    /// <para><b>0 判真的理由</b>：选项块里写 <c>webView: 0</c> 的源是"显式要用 WebView"（数值是
    /// 历史上手误/参数位遗留），出现该键本身就是意图；legado 对 0 会关掉 WebView 导致这些源
    /// 拿不到渲染内容——reader 修正过这一点（2026-10-21 调研记录）。</para>
    /// </summary>
    private static bool IsTruthy(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Number => true,
            JsonValueKind.String => RuleAnalyzer.IsTruthy(e.GetString() ?? string.Empty),
            _ => false,
        };

    // ---- ⑧：编码 ----

    /// <summary>
    /// 对 GET 查询串做百分号编码（reader encode_get_query）。
    /// 已含 <c>%</c> 的字段视为源已编码，原样保留，避免二次编码（如 %E4%B8%AD 被编成 %25E4...）。
    /// <para>逐 &amp; 分段、逐 = 切名值后分别 EscapeDataString；无 = 的段整体编码。</para>
    /// </summary>
    private static string EncodeUrl(string url, string? charset)
    {
        var q = url.IndexOf('?');
        if (q < 0 || q == url.Length - 1)
        {
            // 无查询串（或 ? 在末尾）：无需编码
            return url;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data: URL 的"查询串"是数据本体，不能编码
            return url;
        }

        var query = url.Substring(q + 1);
        if (query.Length == 0 || (query.Contains('%') && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            // ① 空查询；② 已含 % 且书源未显式声明 utf-8 → 视为源已编码，原样保留
            return url;
        }

        var sb = new StringBuilder(url.Length + 32);
        sb.Append(url, 0, q + 1);
        var first = true;
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            if (!first)
            {
                sb.Append('&');
            }

            first = false;

            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                // 无值参数：整体编码
                sb.Append(Uri.EscapeDataString(pair));
                continue;
            }

            // 名、值分别编码（= 两侧）
            sb.Append(Uri.EscapeDataString(pair.Substring(0, eq)));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(pair.Substring(eq + 1)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// POST 体编码时机判定（reader fetcher:183-190）：已是 JSON/XML 或自带 Content-Type 时不改。
    /// 返回 true 表示需要按 application/x-www-form-urlencoded 发送（由 fetcher 加头）。
    /// </summary>
    public static bool NeedsFormEncode(string? body, IReadOnlyDictionary<string, string>? headers)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        // 自带 Content-Type：尊重书源声明，不插手
        if (headers is not null)
        {
            foreach (var k in headers.Keys)
            {
                if (string.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        // 体已像 JSON/XML（{ [ &lt; 开头）或已含 % → 不按表单编码
        var t = body.TrimStart();
        return t.Length > 0 && t[0] is not ('{' or '[' or '<') && !body.Contains('%');
    }

    /// <summary>
    /// URL 编译期 JS 求值（步骤②③ 共用）。
    /// 绑定 key/page 两个全局变量（书源脚本常用）；异常时返回空串（legado：脚本失败不中断编译）。
    /// </summary>
    private string RunJs(string script, string key, int page)
    {
        try
        {
            _engine ??= new Engine();
            _engine.SetValue("key", key);
            _engine.SetValue("page", page);
            return _engine.Evaluate(script)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>兑现 @result 占位符（脚本输出里 "@result" 指代自身，支持递归拼接）。</summary>
    private static void ReplacePlaceholder(StringBuilder sb, string result) =>
        sb.Replace("@result", result);
}

using System.Text.RegularExpressions;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// URL 处理工具（逐个函数对标 reader）：
/// <c>resolve_url</c>（<c>src/parser/rule_engine.rs:1806</c>，**解析结果**用）
/// 与 <c>absolute_url</c>（<c>src/crawler/url_analyzer.rs:251</c>，**发起请求**用）。
/// <para>两个函数分工不可混用（项目铁律）：resolve_url 保留 query（书源规则拼出来的地址常带参数），
/// absolute_url 去 query + fragment 且 javascript: 返回空串（表示"不发起请求"）。</para>
/// </summary>
internal static class UrlUtil
{
    /// <summary>
    /// 去掉 URL 尾部的取图选项 / WebView 配置（对标 strip_url_config）。
    /// 书源常在图片地址后跟 ,{'webView':...} 或 ##$## 配置段，取数前必须剥掉。
    /// </summary>
    private static readonly string[] ConfigMarkers = { "##$##", ",{'webView'", ",{\"webView\"" };

    /// <summary>HTML meta charset 探测（仅扫前 4KB，对标 charset_from_html_meta）。</summary>
    private static readonly Regex MetaCharsetRegex =
        new("charset\\s*=\\s*[\"']?([a-zA-Z0-9\\-_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解析结果绝对化（reader rule_engine::resolve_url）。
    /// 保留 query、去 fragment；<c>//host/x</c> 补 https；失败时手工拼接 base/url。
    /// <para>步骤：</para>
    /// ① 双方 Normalize（去控制字符 + trim）+ url 剥取图配置；
    /// ② url 为空 → 返回 base（无路可走时退化为当前页）；
    /// ③ 已是绝对地址（http/https/data:）→ 原样返回；
    /// ④ 协议相对 //host → 补 https:；
    /// ⑤ base 为空 → 返回 url 本身；
    /// ⑥ 优先 Uri 组合（base 保留 query），失败再手工字符串拼接兜底。
    /// </summary>
    public static string ResolveUrl(string? baseUrl, string? url)
    {
        // ① 归一化双方；url 额外剥掉尾部取图/WebView 配置段
        var baseText = Normalize(baseUrl);
        var text = Normalize(StripUrlConfig(url));
        if (text.Length == 0)
        {
            // ② 无相对地址：退化为 base（reader：空 url 返回 base）
            return baseText;
        }

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // ③ 绝对地址（含 data: 内联图）原样返回
            return text;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            // ④ 协议相对地址：一律补 https:（项目铁律）
            return "https:" + text;
        }

        if (baseText.Length == 0)
        {
            // ⑤ 没有 base 可拼：返回相对地址本身
            return text;
        }

        // ⑥ Uri 组合：StripFragment 后再 join，保证结果无 fragment
        if (Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, StripFragment(text), out var joined))
        {
            return StripFragment(joined.ToString());
        }

        // ⑥ 兜底：手工拼接（Uri.TryCreate 拒绝极端形态时，如 base 含非法字符）
        return baseText.TrimEnd('/') + "/" + text.TrimStart('/');
    }

    /// <summary>
    /// 请求 URL 绝对化（reader url_analyzer::absolute_url）。
    /// query 与 fragment 都去掉；<c>javascript:</c> 返回空串（表示"不发起请求"）。
    /// <para>与 <see cref="ResolveUrl"/> 的差异：本函数用于"真正要发出的请求"，
    /// 因此更激进——剥 query（书源 URL 里的 query 多是跟踪参数）、剥 fragment。</para>
    /// </summary>
    public static string AbsoluteUrl(string? baseUrl, string? url)
    {
        var baseText = Normalize(baseUrl);
        var text = Normalize(url);
        if (text.Length == 0)
        {
            // 空 url：退化为 base
            return baseText;
        }

        if (text.StartsWith("javascript", StringComparison.OrdinalIgnoreCase))
        {
            // javascript: 伪协议 → 空串 = 不发起请求（reader 语义）
            return string.Empty;
        }

        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 绝对地址原样返回（注意：此处不去 query，与 resolve_url 的"已是绝对则原样"一致）
            return text;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + text;
        }

        if (Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri))
        {
            // base 先去 query + fragment 再 join（请求地址不带尾巴）
            var clean = new UriBuilder(baseUri) { Query = string.Empty, Fragment = string.Empty }.Uri;
            if (Uri.TryCreate(clean, text, out var joined))
            {
                return joined.ToString();
            }
        }

        return text;
    }

    /// <summary>取 URL 的 <c>scheme://host</c>（用于图片防盗链 Referer，对标 get_cover 里的 Referer 推导）。</summary>
    public static string OriginOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return string.Empty;
    }

    /// <summary>
    /// 只对 path 相同、可解析的 URL 做比较用：剔除 fragment 后的规范化形式。
    /// 用于目录/正文分页的"是否已访问"判定（对标 strip_fragment）。
    /// </summary>
    public static string StripFragment(string url)
    {
        var hash = url.IndexOf('#');
        return hash >= 0 ? url.Substring(0, hash) : url;
    }

    /// <summary>
    /// 滤除控制字符（保留 \n \r \t）后 trim，对标 normalize_source_url。
    /// <para>书源 JSON 里的 URL 字段常混入不可见控制字符（从网页复制而来），
    /// 直接进 Uri.TryCreate 会静默失败，故一切 URL 处理前先过本函数。</para>
    /// </summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        // ① 先 trim；② 逐字符过滤控制字符（白名单 \n \r \t 保留）；③ 结果再 trim 一次。
        //    先扫一遍判"脏不脏"：绝大多数 URL 没有控制字符，直接返回可省掉缓冲数组 + 二次 Trim。
        var span = url.AsSpan().Trim();
        var dirty = false;
        foreach (var c in span)
        {
            if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t')
            {
                dirty = true;
                break;
            }
        }

        if (!dirty)
        {
            return span.ToString();
        }

        var buffer = new char[span.Length];
        var len = 0;
        foreach (var c in span)
        {
            if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t')
            {
                continue;
            }

            buffer[len++] = c;
        }

        return new string(buffer, 0, len).Trim();
    }

    /// <summary>
    /// 剥掉 URL 尾部的取图选项 / WebView 配置段（对标 strip_url_config）。
    /// 取三个标记中**最早**出现的位置截断；都没有则原样返回。
    /// </summary>
    private static string StripUrlConfig(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var cut = url.Length;
        foreach (var marker in ConfigMarkers)
        {
            var idx = url.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && idx < cut)
            {
                cut = idx;
            }
        }

        return cut == url.Length ? url : url.Substring(0, cut);
    }

    /// <summary>
    /// 从 HTTP Content-Type 或 HTML meta 探测字符集名字（对标 charset_from_content_type / charset_from_html_meta）。
    /// <para>探测顺序（reader 四级链的前两级）：① Content-Type 的 charset 参数；② HTML 前 4KB 的 meta 声明。
    /// 只看前 4KB：meta 一定在 head 里，避免为探测把整个响应体扫一遍。
    /// 传入 null 的入参会被跳过，调用方（HttpBookSourceFetcher）据此实现惰性嗅探。</para>
    /// </summary>
    public static string? DetectCharset(string? contentType, string? htmlHead)
    {
        // ① Content-Type: text/html; charset=gb18030
        if (!string.IsNullOrEmpty(contentType))
        {
            var m = MetaCharsetRegex.Match(contentType);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        // ② <meta charset="..."> / <meta http-equiv="Content-Type" content="...charset=...">
        if (!string.IsNullOrEmpty(htmlHead))
        {
            var m = MetaCharsetRegex.Match(htmlHead);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }
}

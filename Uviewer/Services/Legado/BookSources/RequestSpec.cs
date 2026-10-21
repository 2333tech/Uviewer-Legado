namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 一次书源请求的完整描述（对标 reader <c>crawler::RequestSpec</c>）。
/// 由 <see cref="UrlAnalyzer"/> 从书源 URL 模板编译而来，交给 <see cref="IBookSourceFetcher"/> 执行。
/// <para>不可变 record（init 属性）：编译结果可安全跨线程共享（并行批量抓取时同一 spec 被多个任务读取）。</para>
/// </summary>
public sealed record RequestSpec
{
    /// <summary>reader 默认重试 2 次（即最多 3 次尝试）。</summary>
    public const int DefaultRetry = 2;

    /// <summary>最终请求地址（已插值、已切选项块，但尚未绝对化——由调用方决定绝对化时机）。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>HTTP 方法，默认 GET（选项块里 "post" 会先转大写再匹配，故小写也能识别）。</summary>
    public string Method { get; init; } = "GET";

    /// <summary>POST 体（选项块 body；字符串原样或 JSON 原文）。</summary>
    public string? Body { get; init; }

    /// <summary>请求头（名已归一化：Unicode 连字符 → ASCII '-'）。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>显式字符集（参与请求编码与响应解码；缺省时按三级探测）。</summary>
    public string? Charset { get; init; }

    /// <summary>重试次数（reader 默认 2 → 最多 3 次尝试）。</summary>
    public int Retry { get; init; } = DefaultRetry;

    /// <summary>单请求超时（秒）。null 表示沿用客户端默认（reader 默认 15s）。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// 书源限速配置（原样携带 <see cref="Models.BookSource.ConcurrentRate"/>：
    /// <c>"N"</c> = 串行 + 最小间隔 N 毫秒；<c>"N/M"</c> = 每 M 毫秒窗口最多 N 次）。
    /// <para>由调用方（引擎/搜索/JS 同步桥）在编译 spec 时填入；<see cref="RateLimitedFetcher"/>
    /// 消费。放在 spec 而不是 fetcher 注册表里：限速是<b>每请求</b>的属性，
    /// 这样并行链路不会把 A 源的限速错配到 B 源。</para>
    /// </summary>
    public string? ConcurrentRate { get; init; }

    // 以下为阶段 4（WebView / 服务端代理）预留：先随 spec 解析出来，暂不消费。

    /// <summary>是否需要 WebView 渲染（选项块 webView: true）。</summary>
    public bool WebView { get; init; }

    /// <summary>WebView 注入 JS（选项块 webJs）。</summary>
    public string? WebJs { get; init; }

    /// <summary>服务端代理 ID（选项块 serverID / serverId 两种拼写都认）。</summary>
    public string? ServerId { get; init; }
}

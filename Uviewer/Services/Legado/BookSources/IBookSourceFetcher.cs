namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源取数契约（对齐 reader crawler::fetcher + source_session）。
/// 取文本按 <see cref="RequestSpec"/> 执行；取图片额外带防盗链 Referer。
/// <para>抽象目的：① 测试用静态内容实现（不经网络）；② 阶段 4 可插入
/// WebView 升级装饰器（重 JS 书源）与限速装饰器（<see cref="RateLimitedFetcher"/>）而不改编排层。</para>
/// <para>阶段 4-3 起实现方通常注入 <see cref="ISourceCookieStore"/>：同一书源的所有请求
/// （搜索/目录/正文/<c>java.ajax</c>）共用一份 Cookie，登录态才能贯通（修 D3）。</para>
/// </summary>
public interface IBookSourceFetcher
{
    /// <summary>
    /// 抓取 URL，返回 (解码后的响应体, 最终 URL)。最终 URL 用于相对地址解析（可能被重定向）。
    /// </summary>
    /// <param name="url">请求地址（调用方已绝对化）。</param>
    /// <param name="spec">请求描述（方法/体/头/字符集/重试）。</param>
    /// <param name="sourceKey">书源标识：按源隔离 Cookie（reader 每源一个 CookieJar，避免跨源串味）。</param>
    Task<(string Content, string BaseUrl)> FetchAsync(
        string url,
        RequestSpec? spec = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>取图片字节（自动补 Referer 防盗链，对标 reader get_cover 的 Referer 推导）。</summary>
    Task<byte[]?> FetchBytesAsync(
        string url,
        string? sourceKey = null,
        string? referer = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 一次请求的<b>富响应</b>（阶段 4-3）：状态码 + 响应头 + 最终 URL + 文本。
/// <para>只给 <c>java.getStrResponse</c>/<c>connect</c>/<c>post</c>/<c>ajaxAll</c> 的
/// StrResponse 语义用；普通取数仍走 <see cref="IBookSourceFetcher.FetchAsync"/>（零额外开销）。</para>
/// </summary>
/// <param name="Content">解码后的响应体（已归一为 LF）。</param>
/// <param name="BaseUrl">重定向后的最终 URL。</param>
/// <param name="StatusCode">HTTP 状态码；0 表示"不可知"（仅测试替身会走到）。</param>
/// <param name="Headers">响应头（名小写，多值用 ", " 合并）。</param>
public sealed record FetchResponse(
    string Content,
    string BaseUrl,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// 可选的"富响应"能力（阶段 4-3）：由 <c>HttpBookSourceFetcher</c> 与
/// <see cref="RateLimitedFetcher"/> 实现。<b>不放进 <see cref="IBookSourceFetcher"/></b>，
/// 因为测试替身只需要文本；用能力接口而不是加必选成员，避免为 10 个 fake 写空实现。
/// </summary>
public interface IRichResponseFetcher
{
    /// <summary>抓取并返回状态码/响应头（语义与 <see cref="IBookSourceFetcher.FetchAsync"/> 一致）。</summary>
    Task<FetchResponse> FetchDetailedAsync(
        string url,
        RequestSpec? spec = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default);
}

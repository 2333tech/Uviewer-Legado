namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源取数契约（对齐 reader crawler::fetcher + source_session）。
/// 取文本按 <see cref="RequestSpec"/> 执行；取图片额外带防盗链 Referer。
/// <para>抽象目的：① 测试用静态内容实现（不经网络）；② 阶段 4 可插入
/// WebView 升级装饰器（重 JS 书源）而不改编排层。</para>
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

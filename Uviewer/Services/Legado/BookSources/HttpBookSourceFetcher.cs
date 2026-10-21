using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 默认 HTTP 抓取实现（对标 reader crawler::http_client + fetcher + source_session）。
/// <para>设计要点：</para>
/// <list type="bullet">
/// <item>共享静态 <see cref="HttpClient"/>（连接池复用；进程级单例，勿 dispose）；</item>
/// <item>Cookie 由每源容器手动管理（不放进 Handler，避免跨源串味，reader source_session 语义）；</item>
/// <item>字符集三级探测：Content-Type → HTML meta（仅扫前 4KB）→ 书源 charset → UTF-8，
/// 因此 GB18030/GBK 中文站点不会乱码（reader 用 encoding_rs，行为一致）；</item>
/// <item>重试：最多 3 次尝试、仅 5xx 重试、200ms×(n+1) 线性退避（reader fetcher.rs:237/256）；</item>
/// <item>逐字节读取响应体：编码未知时不能先按 UTF-8 解码（否则 GBK 会变成替换字符后再也无法还原）。</item>
/// </list>
/// <para>线程安全：<see cref="HttpClient"/> 本身线程安全；Cookie 容器按源加锁；
/// <see cref="MethodCache"/> 为并发字典。</para>
/// </summary>
public sealed class HttpBookSourceFetcher : IBookSourceFetcher, IDisposable
{
    /// <summary>reader app/config.rs:31 默认 15s。</summary>
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>reader url_analyzer.rs:11 的 Chrome UA。</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>图片请求 Accept 头（reader get_cover）。</summary>
    private const string ImageAccept = "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8";

    /// <summary>charset 嗅探只扫响应体前 4KB（meta 一定在 head 里，避免全量扫描）。</summary>
    private const int MaxHeadForCharset = 4096;

    /// <summary>
    /// 共享 HttpClient：静态单例，连接池跨所有书源请求复用。
    /// UseCookies=false：Cookie 由每源容器手动管理（见 ApplyCookies/SaveCookies）。
    /// </summary>
    private static readonly HttpClient Shared = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false, // Cookie 由每源容器手动管理，避免跨源串味
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
    };

    /// <summary>
    /// GB18030/GBK 等代码页编码是否已注册（进程级一次性）。
    /// <para>.NET Core 默认只带 UTF-8/UTF-16/ASCII，中文站点常见的 <c>gbk</c>/<c>gb2312</c>
    /// 必须先注册 <c>CodePagesEncodingProvider</c> 才能取到 Encoding 实例，
    /// 否则 <c>Encoding.GetEncoding("gbk")</c> 抛异常 → 抓回来的正文是乱码或空。
    /// 多线程首次并发调用注册也无害（重复注册幂等）。</para>
    /// </summary>
    private static volatile bool _codePagesRegistered;

    /// <summary>常见 HTTP 方法静态复用（避免每次请求 new HttpMethod + ToUpperInvariant）。</summary>
    private static readonly ConcurrentDictionary<string, HttpMethod> MethodCache =
        new(StringComparer.Ordinal);

    /// <summary>每源 Cookie 容器（reader source_session：每源一个 jar）。</summary>
    private readonly ConcurrentDictionary<string, CookieContainer> _cookies = new(StringComparer.Ordinal);

    /// <summary>默认超时（可被 spec.TimeoutSeconds 覆盖）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>
    /// 抓取文本（对标 reader fetcher::fetch）。步骤：
    /// ① 计算重试上限与超时（spec 优先，否则实例默认）；
    /// ② 每次尝试：关联取消源 + CancelAfter 超时；
    /// ③ 发请求；④ 5xx 且还有重试额度 → 线性退避后重试（4xx 直接返回，不重试）；
    /// ⑤ 解码响应体 + 保存 Cookie；⑥ 取消/网络异常且还有额度 → 退避重试。
    /// </summary>
    public async Task<(string Content, string BaseUrl)> FetchAsync(
        string url,
        RequestSpec? spec = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default)
    {
        var maxRetries = spec?.Retry ?? RequestSpec.DefaultRetry;
        var timeout = spec?.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : Timeout;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ② 单次尝试的超时控制（关联外部取消，超时只取消本次尝试）
            using var cts = timeout <= TimeSpan.Zero
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (cts is not null)
            {
                cts.CancelAfter(timeout);
            }

            try
            {
                using var request = BuildRequest(url, spec, sourceKey, isImage: false);
                using var response = await Shared
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts?.Token ?? cancellationToken).ConfigureAwait(false);

                // reader：仅 5xx 重试，4xx 直接返回
                if ((int)response.StatusCode >= 500 && attempt < maxRetries)
                {
                    await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var (text, finalUrl) = await DecodeAsync(response, spec?.Charset, request.RequestUri).ConfigureAwait(false);
                SaveCookies(response, sourceKey, request.RequestUri);
                return (text, finalUrl ?? url);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
            {
                // ⑥ 单次超时（非外部取消）：退避重试
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                // ⑥ 网络层异常：退避重试
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 抓取图片字节（对标 reader get_cover）：带图片 Accept + UA + Referer 防盗链 + 每源 Cookie。
    /// 失败（非 2xx / 异常）返回 null——图片失败不影响正文阅读。
    /// </summary>
    public async Task<byte[]?> FetchBytesAsync(
        string url,
        string? sourceKey = null,
        string? referer = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", ImageAccept);
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }

            ApplyCookies(request, sourceKey);
            using var response = await Shared
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 图片失败不影响正文阅读
            return null;
        }
    }

    /// <summary>
    /// 方法解析：常见方法走静态单例（GET/POST/HEAD/PUT/PATCH/DELETE），
    /// 其余进并发缓存（书源自定义方法极少，缓存天然有界）。
    /// <para>常见方法用 <c>Equals(OrdinalIgnoreCase)</c> 直接比：旧实现先
    /// <c>ToUpperInvariant()</c> 再 switch，<b>每个请求都会多分配一个字符串</b>
    /// （搜索一次并发几十个请求时纯属白给）。</para>
    /// </summary>
    private static HttpMethod ResolveMethod(string? method)
    {
        if (string.IsNullOrEmpty(method) || method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethod.Get;
        }

        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return HttpMethod.Post;
        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return HttpMethod.Head;
        if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase)) return HttpMethod.Put;
        if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)) return HttpMethod.Patch;
        if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)) return HttpMethod.Delete;

        var normalized = method.ToUpperInvariant();
        return MethodCache.GetOrAdd(normalized, static v => new HttpMethod(v));
    }

    /// <summary>
    /// 构造请求（对标 reader build_request）。步骤：
    /// ① 方法（默认 GET）；② 书源请求头（名为 proxy 的头被抽出当代理，不作为请求头发送——
    ///    阶段 4 代理管道消费，这里先跳过）；③ 缺省补 UA（书源自定义 UA 优先）；
    /// ④ 图片请求缺省补 Accept；⑤ 附加每源 Cookie；
    /// ⑥ 有 body 时按 NeedsFormEncode 判定 Content-Type（表单 / JSON，书源自带头优先）。
    /// </summary>
    private HttpRequestMessage BuildRequest(string url, RequestSpec? spec, string? sourceKey, bool isImage)
    {
        var request = new HttpRequestMessage(ResolveMethod(spec?.Method), url);

        var hasUserAgent = false;
        var hasAccept = false;
        if (spec?.Headers is { } headers)
        {
            foreach (var (k, v) in headers)
            {
                // reader：名为 proxy 的头被抽出当代理，不作为请求头发送
                if (string.Equals(k, "proxy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hasUserAgent |= string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase);
                hasAccept |= string.Equals(k, "Accept", StringComparison.OrdinalIgnoreCase);
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        if (!hasUserAgent)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }

        if (isImage && !hasAccept)
        {
            request.Headers.TryAddWithoutValidation("Accept", ImageAccept);
        }

        ApplyCookies(request, sourceKey);

        if (!string.IsNullOrEmpty(spec?.Body))
        {
            var body = spec!.Body!;
            if (UrlAnalyzer.NeedsFormEncode(body, spec.Headers))
            {
                // 表单体：显式 application/x-www-form-urlencoded
                request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            else
            {
                // 非表单体：默认按 JSON 发（书源自带 Content-Type 优先）
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.Remove("Content-Type");
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Type", spec.Headers is { } h && h.ContainsKey("Content-Type")
                        ? h["Content-Type"]
                        : "application/json;charset=UTF-8");
            }
        }

        return request;
    }

    /// <summary>
    /// 响应体解码（对标 reader decode_body）。步骤：
    /// ① 逐字节读取（不能先按 UTF-8 解码，否则 GBK 响应体会被替换字符污染）；
    /// ② Content-Type 带 charset → 直接用（此时完全不碰响应体，零额外分配）；
    /// ③ 否则才扫前 4KB 的 meta 声明（Latin1 逐字节解码保真）；
    /// ④ 编码解析链：探测值 → 书源 charset → UTF-8（reader 四级链，探测值非法时继续下沉）；
    /// ⑤ 按编码解码全量字节；⑥ 取最终 URL（重定向后）。
    /// </summary>
    private async Task<(string Text, string? FinalUrl)> DecodeAsync(
        HttpResponseMessage response, string? sourceCharset, Uri? requestUri)
    {
        // ① 逐字节读取：编码未知时不能先按 UTF-8 解码（否则 GBK 会变成替换字符后再也无法还原）
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.CharSet;

        // ②③ Content-Type 带 charset 时完全不碰响应体；只有它缺失时才扫前 4KB 的 meta
        var label = UrlUtil.DetectCharset(contentType, null);
        if (label is null)
        {
            var head = bytes.Length > MaxHeadForCharset
                ? Encoding.Latin1.GetString(bytes, 0, MaxHeadForCharset)
                : Encoding.Latin1.GetString(bytes);
            label = UrlUtil.DetectCharset(null, head);
        }

        // ④ 探测值 → 书源 charset → UTF-8
        var encoding = GetEncoding(label) ?? GetEncoding(sourceCharset) ?? Encoding.UTF8;

        // ⑤ 按编码解码全量字节
        var text = encoding.GetString(bytes);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        // ⑥ 换行归一（CRLF/CR → LF）：书源规则（选择器/@js:/标题正则/替换规则）全部按 LF 语义编写，
        // 站点 HTML 的 CRLF 会让 `$` 收尾的规则静默失效（.NET 的 `$` 只认 `\n` 前的边界）。
        // 归一放在解码处 = 抓取出的"原文"本身就是 LF，后续任何环节都不必再兼容两种换行。
        return (TextNormalization.ToLf(text), finalUrl);
    }

    /// <summary>
    /// 编码解析（含 GB18030 / GBK / Big5，依赖 System.Text.Encoding.CodePages 提供器）。
    /// net10 默认只内置 UTF-8/16/32、ASCII、Latin1，缺该提供器时
    /// Encoding.GetEncoding("GB18030") 直接抛 ArgumentException（项目踩坑点 16）。
    /// </summary>
    private static Encoding? GetEncoding(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        EnsureCodePages();
        try
        {
            return Encoding.GetEncoding(label.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CodePages 提供器注册（幂等，并发下重复注册无害）。</summary>
    private static void EnsureCodePages()
    {
        if (_codePagesRegistered)
        {
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _codePagesRegistered = true;
    }

    /// <summary>
    /// 附加每源 Cookie：从该源的容器取当前请求 URL 的 Cookie 头（容器按源加锁）。
    /// 无 sourceKey 或无容器 → 不加（首次请求尚无 Cookie）。
    /// </summary>
    private void ApplyCookies(HttpRequestMessage request, string? sourceKey)
    {
        if (string.IsNullOrEmpty(sourceKey) || request.RequestUri is null)
        {
            return;
        }

        if (!_cookies.TryGetValue(sourceKey, out var jar))
        {
            return;
        }

        string header;
        lock (jar)
        {
            header = jar.GetCookieHeader(request.RequestUri);
        }

        if (header.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    /// <summary>
    /// 保存响应 Set-Cookie 到每源容器（按源加锁；单个非法 Cookie 不影响其它）。
    /// </summary>
    private void SaveCookies(HttpResponseMessage response, string? sourceKey, Uri? requestUri)
    {
        if (string.IsNullOrEmpty(sourceKey) || requestUri is null
            || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        var jar = _cookies.GetOrAdd(sourceKey, _ => new CookieContainer());
        lock (jar)
        {
            foreach (var value in values)
            {
                try
                {
                    jar.SetCookies(requestUri, value);
                }
                catch (CookieException)
                {
                    // 单个非法 Set-Cookie 不影响其它
                }
            }
        }
    }

    /// <summary>线性退避：200ms×(attempt+1)（reader fetcher.rs:237/256）。取消时静默结束。</summary>
    private static async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200 * (attempt + 1), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退避期间被取消：直接结束
        }
    }

    /// <summary>
    /// 释放。静态 HttpClient 由进程持有，不在此释放（多实例共享，勿 dispose）。
    /// </summary>
    public void Dispose()
    {
        // 静态 HttpClient 由进程持有，不在此释放
    }
}

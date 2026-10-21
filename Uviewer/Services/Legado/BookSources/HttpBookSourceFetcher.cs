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
/// <item><b>Cookie 由注入的 <see cref="ISourceCookieStore"/> 统一管理</b>（阶段 4-3 修 D3）：
/// 不再是本实例的私有字典——同一书源的搜索/目录/正文/<c>java.ajax</c> 共用同一份 Cookie；</item>
/// <item>重定向<b>手动跟随</b>（AllowAutoRedirect=false）：每一跳重新附加 Cookie 并捕获
/// Set-Cookie——302 上设置会话 Cookie 是常见登录姿势，自动跟随时中间响应不可见会整段丢失；</item>
/// <item>字符集三级探测：Content-Type → HTML meta（仅扫前 4KB）→ 书源 charset → UTF-8，
/// 因此 GB18030/GBK 中文站点不会乱码（reader 用 encoding_rs，行为一致）；
/// 书源 charset 同时参与<b>请求体</b>编码；</item>
/// <item>重试：最多 3 次尝试、仅 5xx 重试、200ms×(n+1) 线性退避（reader fetcher.rs:237/256）；
/// 超时由每次尝试的 linked CTS 控制（spec.TimeoutSeconds 优先，否则实例默认 15s），
/// 共享客户端自身不设超时（避免先于 CTS 触发而截断更长的书源配置）；</item>
/// <item>逐字节读取响应体：编码未知时不能先按 UTF-8 解码（否则 GBK 会变成替换字符后再也无法还原）。</item>
/// </list>
/// <para>线程安全：<see cref="HttpClient"/> 本身线程安全；Cookie 容器由仓库按源串行化；
/// <see cref="MethodCache"/> 为并发字典。</para>
/// </summary>
public sealed class HttpBookSourceFetcher : IBookSourceFetcher, IRichResponseFetcher, IDisposable
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
    /// <para>AllowAutoRedirect=false：重定向由 <see cref="SendFollowRedirectsAsync"/>
    /// 手动跟随——自动跟随时中间响应对调用方不可见，而 Set-Cookie 常常就设在
    /// 302 上（登录/会话建立），整段丢失会让后续请求裸奔（reader 的
    /// cookie_provider 同样跨跳共享）。</para>
    /// <para>Timeout=Infinite：超时统一由每次尝试的 linked CTS 控制
    /// （spec.TimeoutSeconds 优先，否则实例默认）。旧实现 Shared.Timeout=15s
    /// 会**先于** CTS 触发，书源显式配置的更长超时（30s/60s）被静默截断，
    /// 且超时后还会按重试逻辑再跑 2 遍。</para>
    /// </summary>
    private static readonly HttpClient Shared = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false, // Cookie 由每源容器手动管理，避免跨源串味
        AllowAutoRedirect = false,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    /// <summary>手动重定向的最大跳数（reqwest Policy::limited(10) / HttpClient 默认同为 10）。</summary>
    private const int MaxRedirectHops = 10;

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

    /// <summary>
    /// Cookie 仓库（阶段 4-3）：同一书源的所有取数实现/脚本共用同一份。
    /// <para>默认 <see cref="SourceCookieStore.Memory"/>（纯内存）：单测不写用户目录；
    /// 生产由 <c>LegadoRuntime</c> 注入带 <see cref="SourceStateStore"/> 的实例，Cookie 跨重启保留。</para>
    /// </summary>
    private readonly ISourceCookieStore _cookies;

    /// <summary>默认超时（可被 spec.TimeoutSeconds 覆盖）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <param name="cookies">Cookie 仓库（可选；默认纯内存实例）。</param>
    public HttpBookSourceFetcher(ISourceCookieStore? cookies = null) =>
        _cookies = cookies ?? SourceCookieStore.Memory;

    /// <summary>当前使用的 Cookie 仓库（诊断/接线校验用）。</summary>
    public ISourceCookieStore Cookies => _cookies;

    /// <summary>
    /// 抓取文本（对标 reader fetcher::fetch）。步骤见 <see cref="FetchDetailedAsync"/>；
    /// 本重载只丢弃状态码/响应头（普通取数不需要富响应，零额外开销）。
    /// </summary>
    public async Task<(string Content, string BaseUrl)> FetchAsync(
        string url,
        RequestSpec? spec = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default)
    {
        var response = await FetchDetailedAsync(url, spec, sourceKey, cancellationToken).ConfigureAwait(false);
        return (response.Content, response.BaseUrl);
    }

    /// <summary>
    /// 抓取并返回富响应（阶段 4-3，供 <c>java.getStrResponse/connect/post/ajaxAll</c> 用）。
    /// <para>步骤：</para>
    /// <list type="number">
    /// <item>计算重试上限与超时（spec 优先，否则实例默认）；</item>
    /// <item>每次尝试：关联取消源 + CancelAfter 超时；</item>
    /// <item>发请求；5xx 且还有重试额度 → 线性退避后重试（4xx 直接返回，不重试）；</item>
    /// <item>解码响应体 + 把 <c>Set-Cookie</c> 交给 Cookie 仓库；</item>
    /// <item>取消/网络异常且还有额度 → 退避重试；耗尽 → 异常上抛（由调用方决定回退策略）。</item>
    /// </list>
    /// </summary>
    public async Task<FetchResponse> FetchDetailedAsync(
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
                var (response, request, requestUri) = await SendFollowRedirectsAsync(
                    url, spec, sourceKey, cts?.Token ?? cancellationToken).ConfigureAwait(false);

                using (response)
                using (request)
                {
                    // reader：仅 5xx 重试，4xx 直接返回
                    if ((int)response.StatusCode >= 500 && attempt < maxRetries)
                    {
                        await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var (text, finalUrl) = await DecodeAsync(response, spec?.Charset, requestUri).ConfigureAwait(false);
                    CaptureCookies(response, sourceKey, requestUri);
                    return new FetchResponse(
                        text,
                        finalUrl ?? url,
                        (int)response.StatusCode,
                        CollectResponseHeaders(response));
                }
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
    /// 发送请求并<b>手动跟随重定向链</b>（对标 reader cookie_provider 的跨跳共享语义）。
    /// <para>共享客户端的 <c>AllowAutoRedirect=false</c>，由本方法逐跳跟：每一跳都重新
    /// <see cref="BuildRequest"/>（带上该源最新 Cookie）并把本跳 <c>Set-Cookie</c>
    /// 交给仓库后再跳下一跳。自动跟随时这些中间响应对调用方不可见，会话 Cookie 会丢。</para>
    /// <para>301/302/303 且原方法为 POST 时按 RFC 7231 改写为 GET 并丢弃 body
    /// （307/308 保持原方法与 body）；相对 Location 基于当前跳绝对化。</para>
    /// <para>返回终响应与其请求（由调用方 Dispose；中间跳的请求/响应已在此处置）。</para>
    /// </summary>
    private async Task<(HttpResponseMessage Response, HttpRequestMessage Request, Uri RequestUri)>
        SendFollowRedirectsAsync(
        string url,
        RequestSpec? spec,
        string? sourceKey,
        CancellationToken cancellationToken)
    {
        var currentUrl = url;
        var currentSpec = spec;

        for (var hop = 0; ; hop++)
        {
            var request = BuildRequest(currentUrl, currentSpec, sourceKey, isImage: false);

            HttpResponseMessage response;
            try
            {
                response = await Shared
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                request.Dispose();
                throw;
            }

            var location = response.Headers.Location;
            var isRedirect = hop < MaxRedirectHops
                && location is not null
                && response.StatusCode is HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect;

            if (!isRedirect)
            {
                return (response, request, request.RequestUri!);
            }

            // 本跳 Set-Cookie 先落库，下一跳的 BuildRequest 才会带上
            CaptureCookies(response, sourceKey, request.RequestUri);
            currentUrl = new Uri(request.RequestUri!, location!).ToString();

            if (currentSpec is not null
                && response.StatusCode is HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther
                && !string.Equals(currentSpec.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentSpec.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                currentSpec = currentSpec with { Method = "GET", Body = null };
            }

            request.Dispose();
            response.Dispose();
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
            // 图片头（Accept / Referer 防盗链）走 spec.Headers，与文本路径共用
            // 同一条重定向跟随 + Cookie 管线（CDN 302 是图片失败的常见原因）
            var imageHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = ImageAccept,
            };
            if (!string.IsNullOrEmpty(referer))
            {
                imageHeaders["Referer"] = referer!;
            }

            var (response, request, _) = await SendFollowRedirectsAsync(
                url, new RequestSpec { Headers = imageHeaders }, sourceKey, cancellationToken).ConfigureAwait(false);

            using (response)
            using (request)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
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
    /// ④ 图片请求缺省补 Accept；⑤ 附加该源 Cookie（书源自带 Cookie 头优先）；
    /// ⑥ 有 body 时按 NeedsFormEncode 判定 Content-Type（表单 / JSON，书源自带头优先）。
    /// <para>internal 仅为了让测试能断言"Cookie 确实来自共享仓库"（测试工程链接源码编译）。</para>
    /// </summary>
    internal HttpRequestMessage BuildRequest(string url, RequestSpec? spec, string? sourceKey, bool isImage)
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
            // 请求体字符集：书源显式声明的 charset 参与请求编码（GBK 站点 POST 表单），
            // 解析失败/未声明回落 UTF-8（RequestSpec.Charset 文档口径）
            var body = spec.Body;
            var encoding = GetEncoding(spec.Charset) ?? Encoding.UTF8;
            if (UrlAnalyzer.NeedsFormEncode(body, spec.Headers))
            {
                // 表单体：显式 application/x-www-form-urlencoded
                request.Content = new StringContent(body, encoding, "application/x-www-form-urlencoded");
            }
            else
            {
                // 非表单体：默认按 JSON 发（书源自带 Content-Type 优先）
                request.Content = new StringContent(body, encoding);
                request.Content.Headers.Remove("Content-Type");
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Type", spec.Headers is { } h && h.ContainsKey("Content-Type")
                        ? h["Content-Type"]
                        : $"application/json;charset={encoding.WebName}");
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
    /// 附加该源 Cookie：从共享仓库取当前请求 URL 的 Cookie 头。
    /// <para><b>书源自带 Cookie 头时以书源为准</b>（不追加第二个 Cookie 头——HTTP 会把它当两行，
    /// 多数服务器只认第一行；书源显式写 Cookie 往往是"绕过登录"的确定性做法）。</para>
    /// </summary>
    private void ApplyCookies(HttpRequestMessage request, string? sourceKey)
    {
        if (string.IsNullOrEmpty(sourceKey) || request.RequestUri is null)
        {
            return;
        }

        if (request.Headers.Contains("Cookie"))
        {
            return;
        }

        var header = _cookies.GetCookieHeader(sourceKey, request.RequestUri.ToString());
        if (header.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    /// <summary>
    /// 把响应 <c>Set-Cookie</c> 交给共享仓库（按真实 HTTP 语义：Domain/Path/过期/Max-Age=0 删除）。
    /// <para>阶段 4-3 起不再有"本实例私有容器"：写进仓库后，搜索链/阅读链/<c>java.ajax</c>
    /// 的下一跳都能立即读到（这正是 D3 会话割裂的修复点）。</para>
    /// </summary>
    private void CaptureCookies(HttpResponseMessage response, string? sourceKey, Uri? requestUri)
    {
        if (string.IsNullOrEmpty(sourceKey) || requestUri is null
            || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        _cookies.CaptureSetCookie(sourceKey, requestUri.ToString(), values);
    }

    /// <summary>收集响应头（名小写，多值用 ", " 合并）——给 <c>java.getStrResponse().headers()</c> 用。</summary>
    private static IReadOnlyDictionary<string, string> CollectResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            headers[name.ToLowerInvariant()] = string.Join(", ", values);
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            headers[name.ToLowerInvariant()] = string.Join(", ", values);
        }

        return headers;
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

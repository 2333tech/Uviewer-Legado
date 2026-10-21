using System;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 4-7：WebView 取数装饰器——选项块带 <c>webView: true</c> 的请求改走宿主 WebView2 离屏渲染
/// （JS/Cloudflare 挑战页面，静态请求拿不到内容），其余原样透传静态管线。
///
/// <para><b>降级链</b>（方案 §4-7 验收口径）：宿主未接线 / WebView2 不可用 / WebView 取数失败
/// → 回退静态请求并记诊断——<b>永不因 WebView 缺失让请求直接失败</b>。</para>
///
/// <para><b>图片不过 WebView</b>：<see cref="FetchBytesAsync"/> 直接透传（与限速闸门同口径：
/// 封面/插图按页面渲染串行会让阅读器长时间白屏）。</para>
///
/// <para>不做"按源记忆决策"（方案里的 Static/WebView/Broken 三态缓存）：原始样本里显式
/// <c>webView</c> 选项仅 3 处，每请求判一次 <c>spec.WebView</c> 是零成本的，缓存属于过早优化。</para>
/// </summary>
internal sealed class WebViewFetchDecorator : IBookSourceFetcher
{
    private readonly IBookSourceFetcher _inner;
    private readonly Func<IWebViewHost?> _host;

    public WebViewFetchDecorator(IBookSourceFetcher inner, Func<IWebViewHost?> host)
    {
        _inner = inner;
        _host = host;
    }

    public async Task<(string Content, string BaseUrl)> FetchAsync(
        string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
    {
        var host = _host();
        if (spec?.WebView == true && host is { IsAvailable: true })
        {
            try
            {
                var result = await host.FetchAsync(
                    new WebViewRequest(url, Headers: spec.Headers, DelayMs: spec.WebViewDelayMs),
                    sourceKey, cancellationToken).ConfigureAwait(false);
                return (result.Html, result.FinalUrl);
            }
            catch (Exception ex)
            {
                // WebView 取数失败：回退静态（Cloudflare 挑战页会拿到挑战 HTML，但诊断里可见原因）
                JsDiagnostics.Shared.RecordStateError(sourceKey, null, "webview 取数失败，回退静态（" + ex.Message + "）");
            }
        }
        else if (spec?.WebView == true)
        {
            JsDiagnostics.Shared.RecordStateError(sourceKey, null, "webview 不可用，回退静态取数");
        }

        return await _inner.FetchAsync(url, spec, sourceKey, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]?> FetchBytesAsync(
        string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default) =>
        _inner.FetchBytesAsync(url, sourceKey, referer, cancellationToken);
}

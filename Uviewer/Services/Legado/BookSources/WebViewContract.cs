using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 4-7：WebView 取数契约（引擎侧纯数据，宿主实现 <c>WebView2Host</c>；Fake 可单测）。
///
/// <para><b>为什么契约会穿到引擎侧</b>：<c>java.startBrowserAwait</c> 是 JS 同步桥——脚本
/// 要拿到"用户完成图形验证码之后的页面"，宿主必须在脚本线程上给出结果。契约里的
/// <see cref="ShowInteractiveAsync"/> 即"弹出用户可见的 WebView2 窗口，用户填完验证码点完成，
/// 返回最终页面 HTML + Cookie"；<see cref="FetchAsync"/> 是离屏形态（<c>java.webView</c> 的
/// 静默取数，不带 UI）。</para>
///
/// <para><b>线程约定</b>：实现方负责把调用 marshal 到 UI 线程（引擎侧永远在线程池上同步等待，
/// 与 <c>java.ajax</c> 同一套同步桥约定——Debug 断言非 UI 线程）。</para>
///
/// <para><b>音视频边界（长期约定）</b>：音频/视频书源在搜索层已被拦截，不会走到 WebView 链路；
/// 本契约只服务文本源的验证码/Cloudflare 校验场景。</para>
/// </summary>
public interface IWebViewHost
{
    /// <summary>宿主是否具备 WebView2 运行时（不可用时引擎降级为静态取数 + 诊断，不假装成功）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 离屏取数：导航到 <paramref name="request"/>.Url，可选执行其 Script（<c>java.webView</c>），
    /// 返回最终 HTML / 脚本结果 / 导出的 Cookie。失败抛异常（调用方记诊断）。
    /// </summary>
    Task<WebViewFetchResult> FetchAsync(WebViewRequest request, string? sourceKey, CancellationToken cancellationToken);

    /// <summary>
    /// <b>用户可见窗口</b>（图形验证码/Cloudflare 校验场景）：展示页面让用户手动操作，
    /// 用户点「完成」后返回最终页面；点「取消」/关窗返回 null（调用方按失败记诊断）。
    /// </summary>
    Task<WebViewFetchResult?> ShowInteractiveAsync(string url, string title, string? sourceKey, CancellationToken cancellationToken);
}

/// <summary>WebView 取数请求。</summary>
/// <param name="Url">目标地址（脚本原样给出）。</param>
/// <param name="Script">导航完成后要执行的 JS（<c>java.webView</c> 第三参数；null = 只取页面）。</param>
/// <param name="Headers">请求头（书源 header + 脚本给的 UA 等；WebView 侧尽力设置）。</param>
/// <param name="DelayMs">页面加载完成后的额外等待（书源 webViewDelayTime，0 = 默认 1s）。</param>
public sealed record WebViewRequest(
    string Url,
    string? Script = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    int DelayMs = 0);

/// <summary>WebView 取数结果。</summary>
/// <param name="Html">最终页面 HTML（<c>document.documentElement.outerHTML</c>）。</param>
/// <param name="FinalUrl">最终 URL（可能经过重定向）。</param>
/// <param name="ScriptResult">可选脚本结果（<c>java.webView</c> 用；未执行脚本为 null）。</param>
/// <param name="Cookies">导出的 Cookie（写入 <see cref="ISourceCookieStore"/>，后续静态请求立即可用）。</param>
public sealed record WebViewFetchResult(
    string Html,
    string FinalUrl,
    string? ScriptResult,
    IReadOnlyList<WebViewCookie> Cookies);

/// <summary>WebView 导出的 Cookie（写入 <see cref="ISourceCookieStore"/> 的最小字段集）。</summary>
public sealed record WebViewCookie(string Name, string Value, string Domain, string Path, DateTimeOffset? Expires);

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Jint;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 4-7：T4 浏览器原语（<c>java.startBrowser / startBrowserAwait / webView</c>）。
///
/// <para><b>语义来源</b>：legado <c>JsExtensions.startBrowser/startBrowserAwait/webView</c> +
/// reader <c>js.rs</c>（<c>J.startBrowser/J.startBrowserAwait/J.webView</c>）+ 样本 408 条的
/// 真实调用形态。核心场景（用户要求）：<b>图形验证码 / Cloudflare 校验</b>——
/// 弹出用户可见的 WebView2 窗口，用户手动完成验证后点「完成」，页面 HTML 与 Cookie
/// 回灌脚本（Cookie 同时进 <see cref="ISourceCookieStore"/>，后续 <c>java.ajax</c>/静态请求立即可用）。</para>
///
/// <list type="bullet">
/// <item><c>startBrowser(url, title)</c>：打开窗口等用户完成，返回 true（legado 异步打开、不阻塞；
///   这里同步等待——同步桥语义，样本里该返回值无人消费）。</item>
/// <item><c>startBrowserAwait(url[, 选项块], title[, refetch])</c> → <b>StrResponse</b>（样本全部用
///   <c>.body()</c> 消费）：用户完成后返回页面；refetch 缺省 true 时按 reader 语义用新 Cookie 重取
///   url（Cloudflare 过验证后的正文页内容以静态请求为准），false 直接用页面 HTML（样本 7 处显式传 false）。</item>
/// <item><c>webView(html, url, js)</c> → 脚本结果（如 <c>document.cookie</c>）：离屏取数，不打扰用户；
///   样本里 html 恒为 null（传了也只是展示内容，无法等效 loadDataWithBaseURL，记诊断忽略）。</item>
/// </list>
///
/// <para><b>宿主未接入/WebView2 不可用</b>：记诊断降级——startBrowser 返回 true（不炸脚本）、
/// startBrowserAwait 返回 code=0 空响应、webView 返回空串；不假装拿到了页面。</para>
/// </summary>
internal static class BrowserJavaApi
{
    public static object? Invoke(
        Engine engine, JsScope scope, JsDiagnostics diagnostics, string name, object?[] args)
    {
        // 诊断显式传参（不用 scope.Diagnostics）：生产路径两者是同一对象，但测试里 bridge 可能
        // 挂独立诊断实例——4-1 的"诊断张冠李戴"教训，见 JsContextObjects.Bind 同款注释。
        var host = scope.WebView;
        return name switch
        {
            "startBrowser" => StartBrowser(scope, diagnostics, host, args),
            "startBrowserAwait" => StartBrowserAwait(engine, scope, diagnostics, host, args),
            "webView" => WebView(scope, diagnostics, host, args),
            _ => null,
        };
    }

    private static object? StartBrowser(JsScope scope, JsDiagnostics diagnostics, IWebViewHost? host, object?[] args)
    {
        if (host is null || !host.IsAvailable)
        {
            diagnostics.RecordMissingApi(scope.SourceKey, null, "startBrowser(WebView2 不可用，已跳过窗口)");
            return true;
        }

        var url = JsPureApi.AsString(args, 0);
        var title = args.Length > 1 ? JsPureApi.AsString(args, 1) : "浏览";
        try
        {
            var result = host.ShowInteractiveAsync(url, title, scope.SourceKey, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (result is not null)
            {
                MergeCookies(scope, diagnostics, result);
            }
            else
            {
                diagnostics.RecordStateError(scope.SourceKey, null, "startBrowser: 用户取消了窗口");
            }
        }
        catch (Exception ex)
        {
            diagnostics.RecordScriptError(scope.SourceKey, null, ex);
        }

        return true;
    }

    private static object? StartBrowserAwait(Engine engine, JsScope scope, JsDiagnostics diagnostics, IWebViewHost? host, object?[] args)
    {
        if (host is null || !host.IsAvailable)
        {
            diagnostics.RecordMissingApi(scope.SourceKey, null, "startBrowserAwait(WebView2 不可用)");
            return JsNetworkSession.BuildResponse(
                engine, scope, "startBrowserAwait",
                new FetchResponse(string.Empty, JsPureApi.AsString(args, 0), 0, EmptyHeaders));
        }

        // 参数形态（样本实拍）：(url) / (url, title) / (url, title, refetch) /
        // (urlWithOptionBlock, title, refetch) —— url 参数直接复用 ajax 的选项块编译链。
        var rawUrl = JsPureApi.AsString(args, 0);
        string title;
        var refetch = true;
        string? secondArg = null;
        if (args.Length > 1 && args[1] is not null)
        {
            secondArg = JsPureApi.AsString(args, 1);
        }

        if (args.Length > 2 && args[2] is not null)
        {
            title = secondArg ?? "浏览";
            refetch = !string.Equals(JsPureApi.AsString(args, 2), "false", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // 两参形态：第二参数是标题（不可能是选项块——样本里选项块内联在 url 串里）
            title = secondArg ?? "浏览";
        }

        WebViewFetchResult? result;
        try
        {
            // 选项块里的 headers（如 UA）交给 WebView 宿主
            var (url, headers) = CompileUrl(scope, diagnostics, rawUrl);
            result = host.ShowInteractiveAsync(url, title, scope.SourceKey, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            diagnostics.RecordScriptError(scope.SourceKey, null, ex);
            result = null;
        }

        if (result is null)
        {
            // 用户取消 / 窗口失败：code=0 空响应（与 java.ajax 失败语义一致，脚本可判断 body 为空）
            diagnostics.RecordStateError(scope.SourceKey, null, "startBrowserAwait: 用户取消或窗口失败");
            return JsNetworkSession.BuildResponse(
                engine, scope, "startBrowserAwait",
                new FetchResponse(string.Empty, rawUrl, 0, EmptyHeaders));
        }

        MergeCookies(scope, diagnostics, result);

        var body = result.Html;
        var finalUrl = result.FinalUrl;
        if (refetch && finalUrl.Length > 0)
        {
            // reader 语义：过验证后用新 Cookie 重取原 url（页面内容以静态请求为准）
            var refetched = TryRefetch(scope, diagnostics, rawUrl);
            if (refetched is { } response)
            {
                body = response.Content;
                finalUrl = response.BaseUrl;
            }
        }

        return JsNetworkSession.BuildResponse(
            engine, scope, "startBrowserAwait", new FetchResponse(body, finalUrl, 200, EmptyHeaders));
    }

    private static object? WebView(JsScope scope, JsDiagnostics diagnostics, IWebViewHost? host, object?[] args)
    {
        if (host is null || !host.IsAvailable)
        {
            diagnostics.RecordMissingApi(scope.SourceKey, null, "webView(WebView2 不可用)");
            return string.Empty;
        }

        var html = args.Length > 0 && args[0] is not null ? JsPureApi.AsString(args, 0) : null;
        var url = JsPureApi.AsString(args, 1);
        var script = args.Length > 2 && args[2] is not null ? JsPureApi.AsString(args, 2) : null;

        if (!string.IsNullOrWhiteSpace(html))
        {
            // WebView2 无 loadDataWithBaseURL 等效能力；样本里该参数恒为 null，如实记诊断后按 url 导航
            diagnostics.RecordStateError(scope.SourceKey, null, "webView: html 参数不支持注入，忽略并按 url 导航");
        }

        var (compiledUrl, headers) = CompileUrl(scope, diagnostics, url);
        try
        {
            var result = host.FetchAsync(
                new WebViewRequest(compiledUrl, script, headers, DelayMs: 0),
                scope.SourceKey, CancellationToken.None).GetAwaiter().GetResult();
            MergeCookies(scope, diagnostics, result);
            return result.ScriptResult ?? result.Html;
        }
        catch (Exception ex)
        {
            diagnostics.RecordScriptError(scope.SourceKey, null, ex);
            return string.Empty;
        }
    }

    // ============================================================
    // 辅助
    // ============================================================

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>url 参数复用 ajax 的选项块编译链（<c>"https://x,{"headers":…}"</c> 形态，样本真实写法）。</summary>
    private static (string Url, IReadOnlyDictionary<string, string>? Headers) CompileUrl(JsScope scope, JsDiagnostics diagnostics, string rawUrl)
    {
        try
        {
            var spec = new UrlAnalyzer().BuildSpec(rawUrl, string.Empty, 1, scope.SourceKey);
            var url = UrlUtil.AbsoluteUrl(scope.SourceKey, spec.Url);
            var headers = spec.Headers is { Count: > 0 } ? spec.Headers : null;
            return (url.Length > 0 ? url : rawUrl, headers);
        }
        catch (Exception ex)
        {
            diagnostics.RecordStateError(scope.SourceKey, null, "startBrowser: URL 编译失败（" + ex.Message + "）");
            return (rawUrl, null);
        }
    }

    /// <summary>
    /// WebView 导出的 Cookie 写入共享仓库（后续静态请求 / java.ajax 立即可用——D3 管线）。
    /// <para>转成 Set-Cookie 行交给 <see cref="ISourceCookieStore.CaptureSetCookie"/>——它已实现
    /// 真实 HTTP 语义（Domain/Path/Expires/Max-Age），不再另开一条写入口。</para>
    /// </summary>
    private static void MergeCookies(JsScope scope, JsDiagnostics diagnostics, WebViewFetchResult result)
    {
        foreach (var cookie in result.Cookies)
        {
            if (string.IsNullOrEmpty(cookie.Name))
            {
                continue;
            }

            var header = new StringBuilder(cookie.Name).Append('=').Append(cookie.Value);
            if (!string.IsNullOrEmpty(cookie.Domain))
            {
                header.Append("; Domain=").Append(cookie.Domain);
            }

            header.Append("; Path=").Append(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path);
            if (cookie.Expires is { } expires)
            {
                header.Append("; Expires=").Append(expires.ToString("R", CultureInfo.InvariantCulture));
            }

            scope.Cookies.CaptureSetCookie(scope.SourceKey, result.FinalUrl, new[] { header.ToString() });
        }
    }

    /// <summary>过验证后的静态重取（reader：refetchAfterSuccess !== false 时 connect(url)）。</summary>
    private static FetchResponse? TryRefetch(JsScope scope, JsDiagnostics diagnostics, string rawUrl)
    {
        var fetcher = scope.Fetcher;
        if (fetcher is null)
        {
            return null;
        }

        try
        {
            var spec = new UrlAnalyzer().BuildSpec(rawUrl, string.Empty, 1, scope.SourceKey);
            var url = UrlUtil.AbsoluteUrl(scope.SourceKey, spec.Url);
            if (url.Length == 0)
            {
                return null;
            }

            return fetcher.FetchAsync(url, spec, scope.SourceKey, CancellationToken.None)
                .GetAwaiter().GetResult() is { } pair
                    ? new FetchResponse(pair.Content, pair.BaseUrl, 0, EmptyHeaders)
                    : null;
        }
        catch (Exception ex)
        {
            diagnostics.RecordStateError(scope.SourceKey, null, "startBrowserAwait: 重取失败（" + ex.Message + "）");
            return null;
        }
    }
}

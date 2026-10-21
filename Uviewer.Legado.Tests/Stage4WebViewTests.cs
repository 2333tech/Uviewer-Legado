using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-7：WebView 取数链（引擎侧契约 + T4 浏览器原语 + 装饰器降级）。
/// UI（WebView2 窗口本体）无法无头断言——本文件锁<b>数据与管线行为</b>：
/// Fake <see cref="IWebViewHost"/> 回放"用户完成验证码"的结果，断言 Cookie 回灌 / StrResponse
/// 形态 / refetch 语义 / 宿主缺失降级 / <c>webView:true</c> 请求路由。
/// </summary>
public class Stage4WebViewTests
{
    // ============================================================
    // Fake 宿主：可编程回放
    // ============================================================

    private sealed class FakeWebViewHost : IWebViewHost
    {
        public bool Available { get; set; } = true;
        public WebViewFetchResult? InteractiveResult { get; set; } =
            new("<html>验证后页面</html>", "https://cf.example.com/ok", null,
                new[] { new WebViewCookie("cf_clearance", "abc123", ".example.com", "/", null) });
        public int InteractiveCalls;
        public WebViewRequest? LastFetch;

        public bool IsAvailable => Available;

        public Task<WebViewFetchResult> FetchAsync(WebViewRequest request, string? sourceKey, CancellationToken cancellationToken)
        {
            LastFetch = request;
            return Task.FromResult(new WebViewFetchResult(
                "<html>离屏页面</html>", request.Url, request.Script is null ? null : "document.cookie=TEST",
                Array.Empty<WebViewCookie>()));
        }

        public Task<WebViewFetchResult?> ShowInteractiveAsync(string url, string title, string? sourceKey, CancellationToken cancellationToken)
        {
            InteractiveCalls++;
            return Task.FromResult(InteractiveResult);
        }
    }

    private static JsBridge BridgeWith(IWebViewHost? host, ISourceCookieStore? cookies = null, IBookSourceFetcher? fetcher = null)
    {
        var diagnostics = new JsDiagnostics();
        return new JsBridge
        {
            Diagnostics = diagnostics,
            SourceKey = "https://webview.example.com",
            Field = "content",
            Scope = new JsScope(
                "https://webview.example.com",
                fetcher: fetcher,
                cookies: cookies,
                webView: host),
        };
    }

    // ============================================================
    // startBrowserAwait：图形验证码核心链路
    // ============================================================

    [Fact]
    public async Task StartBrowserAwait_Returns_StrResponse_With_Page_Body_And_Merges_Cookies()
    {
        var store = new SourceCookieStore();
        var host = new FakeWebViewHost();
        var bridge = BridgeWith(host, store);

        var script = "var r = java.startBrowserAwait('https://cf.example.com/page', '验证'); r.body() + '|' + r.code()";
        Assert.Equal("<html>验证后页面</html>|200", bridge.Run(script, "body", "https://cf.example.com/page"));

        Assert.Equal(1, host.InteractiveCalls);

        // Cookie 回灌共享仓库：后续 java.getCookie 立即可读（D3 管线——验证码登录态贯通）
        Assert.Contains("abc123", store.GetCookieHeader("https://webview.example.com", "https://cf.example.com/other"));
    }

    [Fact]
    public async Task StartBrowserAwait_Refetch_False_Uses_Page_Html()
    {
        var store = new SourceCookieStore();
        var host = new FakeWebViewHost();
        var fetcher = new StaticFetcher("静态重取内容");
        var bridge = BridgeWith(host, store, fetcher);

        // refetch=false（样本 7 处显式形态）：直接用页面 HTML，不发起静态重取
        Assert.Equal(
            "<html>验证后页面</html>",
            bridge.Run("java.startBrowserAwait('https://cf.example.com/page', '验证', false).body()", "body", ""));
        Assert.Equal(0, fetcher.CallCount);
    }

    [Fact]
    public async Task StartBrowserAwait_Default_Refetch_Goes_Through_Static_Pipeline()
    {
        var store = new SourceCookieStore();
        var host = new FakeWebViewHost();
        var fetcher = new StaticFetcher("静态重取内容");
        var bridge = BridgeWith(host, store, fetcher);

        // refetch 缺省（reader 语义）：过验证后用新 Cookie 重取 url，正文以静态请求为准
        Assert.Equal(
            "静态重取内容",
            bridge.Run("java.startBrowserAwait('https://cf.example.com/page', '验证').body()", "body", ""));
        Assert.Equal(1, fetcher.CallCount);
    }

    [Fact]
    public void StartBrowserAwait_User_Cancelled_Yields_Code_Zero()
    {
        var host = new FakeWebViewHost { InteractiveResult = null };
        var bridge = BridgeWith(host, new SourceCookieStore());

        // 用户点「取消」：code=0 空响应（不假装 200），脚本可判 body 为空
        Assert.Equal("|0", bridge.Run("var r = java.startBrowserAwait('https://x.example.com', '验证'); r.body() + '|' + r.code()", "body", ""));
    }

    [Fact]
    public void StartBrowser_Without_Host_Degrades_With_Diagnostic()
    {
        var bridge = BridgeWith(host: null, new SourceCookieStore());

        Assert.Equal("true", bridge.Run("String(java.startBrowser('https://x.example.com', '验证'))", "body", ""));
        Assert.Contains(
            bridge.Diagnostics.Snapshot(),
            e => e.Kind == JsDiagnosticKind.MissingApi && e.Detail.Contains("startBrowser"));
    }

    // ============================================================
    // java.webView：离屏取数
    // ============================================================

    [Fact]
    public void WebView_Returns_Script_Result_And_Routes_Request()
    {
        var host = new FakeWebViewHost();
        var bridge = BridgeWith(host, new SourceCookieStore());

        // 样本形态：java.webView(null, u, "document.cookie") → 脚本结果
        Assert.Equal("document.cookie=TEST", bridge.Run("java.webView(null, 'https://x.example.com/', 'document.cookie')", "body", ""));

        Assert.NotNull(host.LastFetch);
        Assert.Equal("https://x.example.com/", host.LastFetch!.Url);
        Assert.Equal("document.cookie", host.LastFetch.Script);
    }

    [Fact]
    public void WebView_Without_Host_Returns_Empty_String_With_Diagnostic()
    {
        var bridge = BridgeWith(null, new SourceCookieStore());
        Assert.Equal("", bridge.Run("java.webView(null, 'https://x.example.com/', 'document.cookie')", "body", ""));
        Assert.Contains(bridge.Diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.MissingApi);
    }

    // ============================================================
    // WebViewFetchDecorator：选项块 webView:true 的请求路由 + 降级
    // ============================================================

    [Fact]
    public async Task Decorator_Routes_WebView_Flagged_Requests_To_Host()
    {
        var host = new FakeWebViewHost();
        var inner = new StaticFetcher("静态内容");
        var decorator = new WebViewFetchDecorator(inner, () => host);

        var spec = new RequestSpec { WebView = true, WebViewDelayMs = 500 };
        var (content, baseUrl) = await decorator.FetchAsync("https://spa.example.com/", spec, "src");

        // 命中宿主：内容来自 WebView（脚本/Cloudflare 页面），且 delay 透传
        Assert.Equal("<html>离屏页面</html>", content);
        Assert.Equal("https://spa.example.com/", baseUrl);
        Assert.NotNull(host.LastFetch);
        Assert.Equal(500, host.LastFetch!.DelayMs);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task Decorator_Falls_Back_To_Static_When_Host_Unavailable()
    {
        var host = new FakeWebViewHost { Available = false };
        var inner = new StaticFetcher("静态内容");
        var decorator = new WebViewFetchDecorator(inner, () => host);

        var spec = new RequestSpec { WebView = true };
        var (content, _) = await decorator.FetchAsync("https://spa.example.com/", spec, "src");

        // WebView2 不可用：回退静态 + 诊断（永不因 WebView 缺失让请求失败）
        Assert.Equal("静态内容", content);
        Assert.Equal(1, inner.CallCount);
        Assert.Contains(
            JsDiagnostics.Shared.Snapshot(),
            e => e.Kind == JsDiagnosticKind.StateError && e.Detail.Contains("webview"));
        JsDiagnostics.Shared.Clear();
    }

    [Fact]
    public async Task Decorator_Passes_Non_Webview_Requests_Straight_Through()
    {
        var host = new FakeWebViewHost();
        var inner = new StaticFetcher("静态内容");
        var decorator = new WebViewFetchDecorator(inner, () => host);

        var (content, _) = await decorator.FetchAsync("https://plain.example.com/", null, "src");

        Assert.Equal("静态内容", content);
        Assert.Null(host.LastFetch);
        Assert.Equal(1, inner.CallCount);
    }

    // ============================================================
    // catalog / 真值口径
    // ============================================================

    [Fact]
    public void Catalog_Browser_Family_Is_Implemented()
    {
        Assert.True(JsApiCatalog.IsImplemented("startBrowser"));
        Assert.True(JsApiCatalog.IsImplemented("startBrowserAwait"));
        Assert.True(JsApiCatalog.IsImplemented("webView"));
    }

    [Fact]
    public void UrlAnalyzer_Webview_Zero_Is_Truthy_Reader_Style()
    {
        var spec = new UrlAnalyzer().BuildSpec(
            "https://spa.example.com/list,{\"method\":\"POST\",\"webView\":0,\"webViewDelayTime\":1200}", "k", 1, "https://spa.example.com");
        Assert.True(spec.WebView);
        Assert.Equal(1200, spec.WebViewDelayMs);
    }

    /// <summary>静态取数替身（记录调用次数；重取/refetch 断言用）。</summary>
    private sealed class StaticFetcher : IBookSourceFetcher
    {
        private readonly string _body;

        public StaticFetcher(string body) => _body = body;

        public int CallCount;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult((_body, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }
}

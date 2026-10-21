using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Uviewer.Services.Legado.BookSources;

namespace Uviewer.Services.Legado.WebView;

/// <summary>
/// 阶段 4-7：<see cref="IWebViewHost"/> 的宿主实现（WebView2，WindowsAppSDK 传递引用，无新增 NuGet）。
///
/// <list type="bullet">
/// <item><b>交互形态</b>（<see cref="ShowInteractiveAsync"/>）：<see cref="LegadoWebViewDialog"/>
///   弹窗加载页面，用户填图形验证码/过 Cloudflare 后点「完成」，导出页面 HTML + Cookie——
///   Cookie 经 <see cref="BrowserJavaApi"/> 进共享仓库，后续静态请求/<c>java.ajax</c> 立即可用。</item>
/// <item><b>离屏形态</b>（<see cref="FetchAsync"/>，<c>java.webView</c>）：
///   复用一个 1×1 隐藏 WebView2（挂在主窗口树内但不可见），导航 → 等待 → 延迟 → 执行脚本 →
///   空结果每秒重试（≤30 次，方案 §3.5 的空结果语义）、整体 60s 超时。</item>
/// </list>
///
/// <para><b>线程</b>：引擎侧在线程池上同步等待；本类所有 UI 操作经 <see cref="OnUiAsync"/>
/// marshal 到 <see cref="DispatcherQueue"/>（引擎线程阻塞等待结果，与 <c>java.ajax</c> 同一套同步桥约定）。</para>
///
/// <para><b>可用性</b>：WebView2 运行时缺失时 <see cref="IsAvailable"/>=false，引擎自动回退静态取数 + 诊断
/// （<see cref="WebViewFetchDecorator"/> / <see cref="BrowserJavaApi"/>），不假装成功。</para>
/// </summary>
internal sealed class WebView2Host : IWebViewHost
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<FrameworkElement?> _rootProvider;

    // 离屏 WebView2（懒建、复用）：1×1 透明，挂在主窗口根容器里但不参与布局视觉
    private Microsoft.UI.Xaml.Controls.WebView2? _offscreen;
    private readonly SemaphoreSlim _offscreenGate = new(1, 1);

    /// <param name="dispatcherQueue">UI 线程队列（宿主窗口的）。</param>
    /// <param name="rootProvider">取主窗口任一可见元素（弹窗 XamlRoot / 离屏控件的挂载点）。</param>
    public WebView2Host(DispatcherQueue dispatcherQueue, Func<FrameworkElement?> rootProvider)
    {
        _dispatcherQueue = dispatcherQueue;
        _rootProvider = rootProvider;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                // WebView2 运行时是否已安装（WinAppSDK 传递引用了 Loader 程序集，探测零成本）
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch
            {
                return false;
            }
        }
    }

    // ============================================================
    // 交互窗口（图形验证码核心场景）
    // ============================================================

    public Task<WebViewFetchResult?> ShowInteractiveAsync(string url, string title, string? sourceKey, CancellationToken cancellationToken) =>
        OnUiAsync(async () =>
        {
            var root = _rootProvider()
                ?? throw new InvalidOperationException("WebView 窗口无宿主元素（主窗口尚未就绪）");

            var dialog = new LegadoWebViewDialog();
            var choice = await dialog.ShowAsync(root, url, title, userAgent: null).ConfigureAwait(true);
            if (choice != ContentDialogResult.Primary)
            {
                return null; // 用户取消：脚本侧记诊断，不假装拿到页面
            }

            return await dialog.ExportAsync().ConfigureAwait(true);
        });

    // ============================================================
    // 离屏取数（java.webView）
    // ============================================================

    public Task<WebViewFetchResult> FetchAsync(WebViewRequest request, string? sourceKey, CancellationToken cancellationToken) =>
        OnUiAsync(async () =>
        {
            var webview = await EnsureOffscreenAsync().ConfigureAwait(true);
            var core = webview.CoreWebView2!;

            ApplyRequestContext(core, request);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
            core.NavigationCompleted += OnCompleted;
            try
            {
                webview.Source = new Uri(request.Url);
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                core.NavigationCompleted -= OnCompleted;
            }

            // 页面渲染延迟：webViewDelayTime 或默认 1s（方案 §3.5 的 1000ms 基线）
            await Task.Delay(Math.Max(request.DelayMs, 1000), cancellationToken).ConfigureAwait(true);

            string? scriptResult = null;
            if (!string.IsNullOrEmpty(request.Script))
            {
                var json = await core.ExecuteScriptAsync(request.Script).AsTask().ConfigureAwait(true);
                scriptResult = JsonValueToString(json);
            }

            var htmlJson = await core.ExecuteScriptAsync("document.documentElement.outerHTML").AsTask().ConfigureAwait(true);
            var html = JsonSerializer.Deserialize<string>(htmlJson) ?? string.Empty;

            var finalUrl = webview.Source?.ToString() ?? request.Url;
            var cookies = await ExportCookiesAsync(core, finalUrl).ConfigureAwait(true);

            // 空结果重试（方案 §3.5）：某些 SPA 首屏慢，每 1s 重试 ≤30 次
            for (var attempt = 0; string.IsNullOrWhiteSpace(scriptResult) && html.Length < 64 && attempt < 30; attempt++)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
                htmlJson = await core.ExecuteScriptAsync("document.documentElement.outerHTML").AsTask().ConfigureAwait(true);
                html = JsonSerializer.Deserialize<string>(htmlJson) ?? string.Empty;
                finalUrl = webview.Source?.ToString() ?? finalUrl;
            }

            return new WebViewFetchResult(html, finalUrl, scriptResult, cookies);
        });

    // ============================================================
    // 离屏控件管理
    // ============================================================

    /// <summary>懒建并复用一个 1×1 透明 WebView2（必须挂在视觉树内，否则 WebView2 不渲染）。</summary>
    private async Task<Microsoft.UI.Xaml.Controls.WebView2> EnsureOffscreenAsync()
    {
        await _offscreenGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_offscreen is not null)
            {
                return _offscreen;
            }

            var root = _rootProvider()
                ?? throw new InvalidOperationException("WebView 离屏控件无挂载点（主窗口尚未就绪）");
            if (root is not Panel panel)
            {
                throw new InvalidOperationException("WebView 离屏控件挂载点不是容器（RootGrid 预期）");
            }

            var webview = new Microsoft.UI.Xaml.Controls.WebView2
            {
                Width = 1,
                Height = 1,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            panel.Children.Add(webview);
            await webview.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
            _offscreen = webview;
            return webview;
        }
        finally
        {
            _offscreenGate.Release();
        }
    }

    /// <summary>把请求上下文应用到 WebView2：UA / Cookie（headers 里能等效迁移的就这两样）。</summary>
    private static void ApplyRequestContext(CoreWebView2 core, WebViewRequest request)
    {
        if (request.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    core.Settings.UserAgent = value;
                }
                else if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    // Cookie 头 → 逐个 CreateCookie/AddOrUpdateCookie（该版本 CookieManager 无 FromHeader API；
                    // 域取目标 URL 的 host，路径 /——够覆盖书源"带 Cookie 打开页面"的场景）
                    try
                    {
                        var host = new Uri(request.Url).Host;
                        foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var eq = pair.IndexOf('=');
                            if (eq <= 0)
                            {
                                continue;
                            }

                            var cookie = core.CookieManager.CreateCookie(
                                pair[..eq].Trim(), pair[(eq + 1)..].Trim(), host, "/");
                            core.CookieManager.AddOrUpdateCookie(cookie);
                        }
                    }
                    catch
                    {
                        // 非法 Cookie 头：忽略（WebView2 自身的 Cookie 由上一次交互带回）
                    }
                }
            }
        }
    }

    private static async Task<List<WebViewCookie>> ExportCookiesAsync(CoreWebView2 core, string url)
    {
        var cookies = new List<WebViewCookie>();
        var coreCookies = await core.CookieManager.GetCookiesAsync(url).AsTask().ConfigureAwait(true);
        foreach (var cookie in coreCookies)
        {
            cookies.Add(new WebViewCookie(
                cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                cookie.Expires >= 0 ? DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires) : null));
        }

        return cookies;
    }

    /// <summary>ExecuteScriptAsync 返回 JSON 文本；脚本结果反序列化为 CLR 值再转字符串（对象 → JSON 串）。</summary>
    private static string? JsonValueToString(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => doc.RootElement.ToString(),
            };
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // ============================================================
    // UI 线程 marshal
    // ============================================================

    /// <summary>
    /// 把异步操作 marshal 到 UI 线程并等它完成。已在 UI 线程（对话框路径）就直接执行；
    /// 否则经 <see cref="DispatcherQueue.TryEnqueue"/>（引擎侧在线程池上阻塞等待结果——同步桥约定）。
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _ = action().ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                tcs.TrySetResult(t.Result);
                            }
                            else if (t.IsFaulted)
                            {
                                tcs.TrySetException(t.Exception!.GetBaseException());
                            }
                            else
                            {
                                tcs.TrySetCanceled();
                            }
                        }, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("WebView 宿主：UI 队列不可用"));
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        _offscreenGate.Dispose();
        _offscreen = null;
    }
}

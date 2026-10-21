using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Uviewer.Services.Legado.BookSources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uviewer.Services.Legado.WebView;

/// <summary>
/// 阶段 4-7：WebView2 <b>用户可见窗口</b>——图形验证码 / Cloudflare 校验场景（用户要求的核心交付）。
///
/// <para><b>交互流程</b>：脚本调 <c>java.startBrowserAwait(url, "验证")</c> → 宿主弹出本窗口加载页面 →
/// 用户在页面里手动完成验证码/人机校验（页面所见即所填）→ 点「完成」→ 窗口关闭，
/// 宿主随即调 <see cref="ExportAsync"/> 从仍存活的 <c>CoreWebView2</c> 导出
/// <c>document.documentElement.outerHTML</c> 与全部 Cookie 回灌脚本；「取消」/关窗 → 宿主返回 null。</para>
///
/// <para><b>弹窗铁律（红线 4）</b>：尺寸只用 <see cref="LegadoDialogLayout.Fit"/> 资源键覆盖；
/// 禁设 dialog 的 MaxWidth/对齐属性；文字一律灰度。</para>
///
/// <para><b>线程</b>：所有 UI 操作由 <see cref="WebView2Host"/> marshal 到 UI 线程后调用。</para>
/// </summary>
internal sealed class LegadoWebViewDialog
{
    private readonly ContentDialog _dialog = new()
    {
        Title = "页面验证",
        PrimaryButtonText = "完成",
        CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Primary,
    };

    private readonly TextBlock _status = new()
    {
        Text = "正在加载页面…",
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8,
        FontSize = 12,
    };

    private readonly Microsoft.UI.Xaml.Controls.WebView2 _webView = new();

    /// <summary>展示窗口；返回 <c>ContentDialogResult.Primary</c> = 用户点「完成」（导出结果），
    /// 其余 = 取消。窗口关闭后 <see cref="ExportAsync"/> 仍可调用（CoreWebView2 未随对话框销毁）。</summary>
    public async Task<ContentDialogResult> ShowAsync(
        FrameworkElement root, string url, string title, string? userAgent)
    {
        _dialog.Title = string.IsNullOrWhiteSpace(title) ? "页面验证" : title;

        // 布局：说明行 + WebView（占满）+ 状态行
        var content = new Grid { RowSpacing = 8 };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        var hint = new TextBlock
        {
            Text = "请在页面中完成图形验证码 / 人机校验，看到目标内容后点「完成」。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        Grid.SetRow(hint, 0);
        content.Children.Add(hint);

        _webView.MinHeight = 360;
        Grid.SetRow(_webView, 1);
        content.Children.Add(_webView);

        Grid.SetRow(_status, 2);
        content.Children.Add(_status);

        _dialog.Content = content;
        _dialog.Resources["ContentDialogMaxHeight"] = 900d;
        LegadoDialogLayout.Fit(_dialog, root, 720, preferredContentHeight: 700);
        _dialog.XamlRoot = root.XamlRoot;

        _webView.NavigationStarting += OnNavigationStarting;
        _webView.NavigationCompleted += OnNavigationCompleted;

        // EnsureCore 与 Show 并行：环境初始化（首次 ~几百 ms）不阻塞窗口出现，导航在就绪后开始
        var showTask = _dialog.ShowAsync().AsTask();
        await EnsureCoreAsync(userAgent).ConfigureAwait(true);
        _webView.Source = new Uri(url);

        return await showTask.ConfigureAwait(true);
    }

    /// <summary>导出最终页面（窗口已关仍可用；CoreWebView2 活着）。</summary>
    public async Task<WebViewFetchResult?> ExportAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var finalUrl = _webView.Source?.ToString() ?? string.Empty;

            // outerHTML 经 ExecuteScriptAsync 返回 JSON 字符串（带引号转义），反序列化还原
            var htmlJson = await _webView.CoreWebView2
                .ExecuteScriptAsync("document.documentElement.outerHTML").AsTask().ConfigureAwait(true);
            var html = JsonSerializer.Deserialize<string>(htmlJson) ?? string.Empty;

            var cookies = new List<WebViewCookie>();
            var coreCookies = await _webView.CoreWebView2.CookieManager
                .GetCookiesAsync(finalUrl).AsTask().ConfigureAwait(true);
            foreach (var cookie in coreCookies)
            {
                cookies.Add(new WebViewCookie(
                    cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
                    cookie.Expires >= 0 ? DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires) : null));
            }

            return new WebViewFetchResult(html, finalUrl, null, cookies);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[LegadoWebViewDialog] 导出失败: " + ex.Message);
            return null;
        }
    }

    private async Task EnsureCoreAsync(string? userAgent)
    {
        await _webView.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
        var core = _webView.CoreWebView2;
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            core.Settings.UserAgent = userAgent;
        }

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _status.Text = "正在加载：" + e.Uri;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _status.Text = e.IsSuccess
            ? "页面加载完成——请在页面中完成验证，然后点「完成」。"
            : "页面加载失败——可点「取消」放弃。";
    }
}

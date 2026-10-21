using System;
using System.IO;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源运行时的<b>组合根</b>（阶段 4-3 新增）：把"取数管线 / JS 状态 / Cookie / 限速 / 诊断"
/// 收敛成一个对象，保证全应用只有<b>一条</b>取数链。
///
/// <para><b>为什么必须只有一个组合根（修 D3）</b>：阶段 4-3 之前
/// <c>BookSourceEngine</c>、<c>SearchService</c>、<c>BookSourceSearchService</c> 各自
/// <c>new HttpBookSourceFetcher()</c>，于是：</para>
/// <list type="bullet">
/// <item>搜索用的 Cookie 与阅读用的 Cookie 是两套容器 —— 登录态永远不贯通；</item>
/// <item><c>java.ajax</c> 又会再开一套（即使补上实现，也读不到静态请求的会话）；</item>
/// <item>限速器/诊断/引擎池也各有一份，无法统一观测。</item>
/// </list>
/// <para>本类把这些实例按"进程/服务一份"创建，并通过 <see cref="CreateEngine"/> /
/// <see cref="CreateSearchService"/> 注入到每个解析引擎；<c>java.ajax</c> 也从
/// <see cref="JsScope.Fetcher"/> 拿到同一条管线。</para>
///
/// <para><b>生命周期</b>：由宿主 <c>BookSourceService</c> 持有（与书源服务同寿命）；
/// 测试可以直接 <c>new LegadoRuntime(tempDir, fakeFetcher)</c> 注入替身，完全离线。</para>
/// </summary>
public sealed class LegadoRuntime : IDisposable
{
    private readonly bool _ownsHttpFetcher;

    /// <summary>建一个运行时。</summary>
    /// <param name="stateDirectory">JS 状态落盘目录（<c>…/legado/sources</c>）；null = 纯内存。</param>
    /// <param name="httpFetcher">底层取数实现；null = 默认 <see cref="HttpBookSourceFetcher"/>
    /// （用本运行时的 Cookie 仓库）。传入替身时它仍会被套上限速装饰器。</param>
    public LegadoRuntime(string? stateDirectory, IBookSourceFetcher? httpFetcher = null)
    {
        State = new SourceStateStore(stateDirectory);
        Cookies = new SourceCookieStore(State);
        _ownsHttpFetcher = httpFetcher is null;
        HttpFetcher = httpFetcher ?? new HttpBookSourceFetcher(Cookies);
        // 4-7：选项块带 webView:true 的请求改走宿主 WebView2（未接线时装饰器自动回退静态 + 诊断）
        Fetcher = new RateLimitedFetcher(new WebViewFetchDecorator(HttpFetcher, () => WebViewHost));
    }

    /// <summary>
    /// WebView2 宿主（阶段 4-7；宿主 UI 在窗口就绪后注入）。
    /// <para>null = 未接线：WebView 类请求自动回退静态取数 + 诊断（<see cref="WebViewFetchDecorator"/>），
    /// <c>java.startBrowser/startBrowserAwait/webView</c> 记诊断降级——测试/无 WebView2 运行时均安全。</para>
    /// </summary>
    public IWebViewHost? WebViewHost { get; set; }

    /// <summary>默认构造：状态落在 <c>&lt;dataDirectory&gt;/legado/sources</c>。</summary>
    public static LegadoRuntime CreateDefault(string dataDirectory, IBookSourceFetcher? httpFetcher = null) =>
        new(Path.Combine(dataDirectory, "legado", "sources"), httpFetcher);

    /// <summary>JS 状态仓库（变量/cache/书籍变量）。</summary>
    public SourceStateStore State { get; }

    /// <summary>Cookie 仓库（脚本 / 静态请求 / <c>java.ajax</c> 共用，落盘到 <see cref="State"/>）。</summary>
    public ISourceCookieStore Cookies { get; }

    /// <summary>底层取数实现（HTTP 或注入的替身）。</summary>
    public IBookSourceFetcher HttpFetcher { get; }

    /// <summary>全应用统一的取数管线（限速装饰器包住 WebView 装饰器与 <see cref="HttpFetcher"/>）。</summary>
    public RateLimitedFetcher Fetcher { get; }

    /// <summary>建一个解析引擎（与运行时共享取数管线 / 状态 / Cookie / WebView 宿主）。</summary>
    public BookSourceEngine CreateEngine(ContentPurifier? purifier = null) =>
        new(Fetcher, purifier, State, Cookies) { WebViewHost = WebViewHost };

    /// <summary>建一个搜索服务（同上；每次搜索内部仍各自新建 RuleEngine，Jint 非线程安全）。</summary>
    public SearchService CreateSearchService() =>
        new(Fetcher, State, Cookies) { WebViewHost = WebViewHost };

    /// <summary>只释放本类创建的 HTTP 实现（注入的替身由调用方负责）。</summary>
    public void Dispose()
    {
        if (_ownsHttpFetcher && HttpFetcher is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

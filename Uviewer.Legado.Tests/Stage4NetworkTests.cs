using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Models = Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-3：组合根（<see cref="LegadoRuntime"/>）、限速（<see cref="RateLimitedFetcher"/> /
/// <see cref="SourceRateGate"/>）、共享 Cookie（<see cref="SourceCookieStore"/>）、
/// JS 同步网络桥（<c>java.ajax/post/connect/head/ajaxAll/getStrResponse</c>）。
///
/// <para><b>验收口径（方案 §4 的 4-3）</b>：</para>
/// <list type="number">
/// <item><c>RateLimitedFetcherTests</c>：<c>N</c> / <c>N/M</c> / 非法 / <c>data:</c> / 取消；</item>
/// <item><c>SyncBridge</c> 不在 UI 线程阻塞（解析链路在线程池跑，异步 fetcher 被同步消费）；</item>
/// <item><b>同一 <c>BookSourceService</c> 下搜索与阅读共享 Cookie</b>（先写再读，两链都带）；</item>
/// <item><c>java.ajax</c> 走 <c>UrlAnalyzer</c> 选项块（<c>"url,{method:'POST'}"</c> 案例）。</item>
/// </list>
/// </summary>
public class Stage4NetworkTests : IDisposable
{
    private readonly string _dir;

    public Stage4NetworkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uviewer-network-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    // ============================================================
    // ① 限速：解析口径
    // ============================================================

    [Fact]
    public void Rate_Parsing_Matches_Reader_Semantics()
    {
        Assert.Null(SourceRateGate.Create(null));
        Assert.Null(SourceRateGate.Create("  "));
        Assert.Null(SourceRateGate.Create("0"));
        Assert.Null(SourceRateGate.Create("abc"));
        Assert.Null(SourceRateGate.Create("1/0"));   // 窗口为 0 → 视为非法
        Assert.Null(SourceRateGate.Create("0/100")); // 次数为 0 → 非法

        var serial = SourceRateGate.Create(" 800 ");
        Assert.NotNull(serial);
        Assert.False(serial!.IsWindow);
        Assert.Equal(800, serial.WindowMs);

        var window = SourceRateGate.Create("3 / 1000");
        Assert.NotNull(window);
        Assert.True(window!.IsWindow);
        Assert.Equal(3, window.Limit);
        Assert.Equal(1000, window.WindowMs);
    }

    // ============================================================
    // ② 限速：串行（同源同时只 1 个 + 最小间隔）
    // ============================================================

    [Fact]
    public async Task Serial_Rate_Enforces_Interval_And_Exclusivity()
    {
        var gate = SourceRateGate.Create("80")!;
        var running = 0;
        var maxRunning = 0;
        var starts = new List<long>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, 3).Select(_ => gate.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref running);
            lock (starts)
            {
                starts.Add(clock.ElapsedMilliseconds);
            }

            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref running);
            return true;
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxRunning); // 串行：同时最多 1 个
        Assert.Equal(3, starts.Count);
        for (var i = 1; i < starts.Count; i++)
        {
            Assert.True(starts[i] - starts[i - 1] >= 70,
                $"第 {i} 次发起间隔不足：{starts[i] - starts[i - 1]}ms");
        }
    }

    // ============================================================
    // ③ 限速：窗口（N/M）
    // ============================================================

    [Fact]
    public async Task Window_Rate_Limits_Per_Window()
    {
        var gate = SourceRateGate.Create("2/100")!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var starts = new List<long>();

        var tasks = Enumerable.Range(0, 3).Select(_ => gate.RunAsync(() =>
        {
            lock (starts)
            {
                starts.Add(clock.ElapsedMilliseconds);
            }

            return Task.FromResult(true);
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(3, starts.Count);
        // 第 3 个必须等窗口滑出（≥ ~100ms）
        Assert.True(starts[2] >= 90, $"第 3 次发起太早：{starts[2]}ms");
    }

    // ============================================================
    // ④ 限速：取消（等待可中断，不傻等）
    // ============================================================

    [Fact]
    public async Task Rate_Wait_Is_Cancellable()
    {
        var gate = SourceRateGate.Create("10000")!; // 首次立即通过，第二次要等 10s

        await gate.RunAsync(() => Task.FromResult(true), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunAsync(() => Task.FromResult(true), cts.Token));

        // 取消在 ~80ms 生效，而不是等满 10s
        Assert.True(clock.ElapsedMilliseconds < 2000, $"取消太慢：{clock.ElapsedMilliseconds}ms");
    }

    // ============================================================
    // ⑤ RateLimitedFetcher：装饰器行为
    // ============================================================

    [Fact]
    public async Task RateLimitedFetcher_Passes_Spec_And_Tracks_Calls()
    {
        var inner = new RecordingFetcher();
        var fetcher = new RateLimitedFetcher(inner);

        var (content, url) = await fetcher.FetchAsync(
            "https://a.com/1",
            new RequestSpec { ConcurrentRate = "0" },
            "src");

        Assert.Equal("body", content); // RecordingFetcher 默认内容
        Assert.Equal("https://a.com/1", url);
        Assert.Single(inner.Calls);
        Assert.Equal("src", inner.Calls[0]);
    }

    [Fact]
    public async Task RateLimitedFetcher_Rejects_Nothing_On_Invalid_Rate()
    {
        var inner = new RecordingFetcher();
        var fetcher = new RateLimitedFetcher(inner);

        // 非法/空限速 = 不过闸（reader 同）：连续 3 次不应有额外等待
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            await fetcher.FetchAsync("https://a.com/x", new RequestSpec { ConcurrentRate = "bad" }, "src");
        }

        Assert.Equal(3, inner.Calls.Count);
        Assert.True(clock.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task Raw_Source_ConcurrentRate_Is_Carried_Into_Every_Request()
    {
        // 原始条目：速读谷吧（concurrentRate = "3"）—— 引擎取数时必须把它带到 spec 上，
        // 否则 RateLimitedFetcher 无从限速（阶段 4-3 之前该字段完全没人消费）。
        var source = await SampleData.RequireAsync("https://www.sudugu.org");
        Assert.Equal("3", source.ConcurrentRate);

        var inner = new RecordingFetcher();
        var engine = new BookSourceEngine(inner, store: new SourceStateStore(), cookies: new SourceCookieStore());

        await engine.GetContentAsync(source, "https://www.sudugu.org/book/1/1", source.BookSourceUrl, CancellationToken.None);

        var spec = Assert.Single(inner.Specs);
        Assert.Equal("3", spec.ConcurrentRate);
    }

    // ============================================================
    // ⑥ 共享 Cookie（D3）：同一仓库被 HTTP 与脚本共同使用
    // ============================================================

    [Fact]
    public void Shared_Cookie_Store_Is_Used_By_Http_Fetcher()
    {
        var store = new SourceCookieStore();
        store.SetCookieHeader("https://a.com/src", "https://www.a.com/login", "sid=abc; u=1");

        var fetcher = new HttpBookSourceFetcher(store);
        using var request = fetcher.BuildRequest("https://www.a.com/api", spec: null, "https://a.com/src", isImage: false);

        Assert.Equal("sid=abc; u=1", request.Headers.GetValues("Cookie").Single());
        // 跨子域可读（legado getSubDomain 语义）
        Assert.Equal("abc", store.GetCookieValue("https://a.com/src", "https://m.a.com/x", "sid"));
    }

    [Fact]
    public void Explicit_Cookie_Header_Wins_Over_Jar()
    {
        var store = new SourceCookieStore();
        store.SetCookieHeader("src", "https://a.com/x", "fromjar=1");

        var fetcher = new HttpBookSourceFetcher(store);
        using var request = fetcher.BuildRequest("https://a.com/x",
            new RequestSpec { Headers = new Dictionary<string, string> { ["Cookie"] = "fromspec=2" } },
            "src", isImage: false);

        // 书源显式写的 Cookie 优先，且不追加第二个 Cookie 头
        Assert.Equal(new[] { "fromspec=2" }, request.Headers.GetValues("Cookie").ToArray());
    }

    [Fact]
    public void Cookie_Store_Round_Trips_Through_State_Store()
    {
        var state = new SourceStateStore(_dir);
        var store = new SourceCookieStore(state);
        store.SetCookieHeader("src", "https://a.com/x", "sid=1; u=2");

        // 新实例（等价重启）从快照恢复
        var reopened = new SourceCookieStore(new SourceStateStore(_dir));
        Assert.Equal("sid=1; u=2", reopened.GetCookieHeader("src", "https://a.com/x"));

        // 删除后不再恢复
        reopened.RemoveCookie("src", "https://a.com/x");
        Assert.Equal(string.Empty, new SourceCookieStore(new SourceStateStore(_dir)).GetCookieHeader("src", "https://a.com/x"));
    }

    [Fact]
    public void Cookie_Store_Captures_SetCookie_With_Http_Semantics()
    {
        var store = new SourceCookieStore();
        var applied = store.CaptureSetCookie("src", "https://a.com/x",
            new[] { "sid=1; Path=/; Domain=.a.com", "tmp=2" });

        Assert.Equal(2, applied);
        Assert.Equal("1", store.GetCookieValue("src", "https://m.a.com/y", "sid"));
        Assert.Equal("2", store.GetCookieValue("src", "https://a.com/x", "tmp"));

        // Max-Age=0 → 删除（真实 HTTP 语义，不是"写入空值"）
        store.CaptureSetCookie("src", "https://a.com/x", new[] { "tmp=; Max-Age=0; Path=/" });
        Assert.Equal(string.Empty, store.GetCookieValue("src", "https://a.com/x", "tmp"));
    }

    // ============================================================
    // ⑦ 组合根：引擎 / 搜索 / JS 桥共用一条管线
    // ============================================================

    [Fact]
    public async Task Runtime_Engine_And_Search_Share_Pipeline_And_Cookies()
    {
        var inner = new RecordingFetcher
        {
            Handler = (url, spec) => (url.Contains("search") ? "{\"books\":[]}" : "page", url),
        };
        var runtime = new LegadoRuntime(_dir, inner);
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://a.com/src",
            SearchUrl = "https://a.com/search?q={{key}}",
            RuleSearch = new Models.SearchRule { BookList = "$.books[*]", Name = "$.name" },
            // 正文链里读 Cookie：证明引擎的 JS 作用域用的是运行时那份 Cookie 仓库
            RuleContent = new Models.ContentRule { Content = "@js:cookie.getCookie('https://a.com/')" },
        };

        // 写 Cookie（模拟登录/脚本写入同一个仓库）
        runtime.Cookies.SetCookieHeader(source.BookSourceUrl, "https://a.com/", "sid=42");

        // 搜索链：取数走同一管线（RecordingFetcher 记录 sourceKey）
        var search = runtime.CreateSearchService();
        await search.SearchAsync(source, "k");
        Assert.Contains(inner.Calls, c => c == source.BookSourceUrl);

        // 阅读链：引擎取数 + 脚本读到同一份 Cookie
        var engine = runtime.CreateEngine();
        var content = await engine.GetContentAsync(
            source, "https://a.com/c/1", source.BookSourceUrl, CancellationToken.None);

        Assert.NotNull(content);
        Assert.Equal("sid=42", content!.Content);
        Assert.Equal("42", runtime.Cookies.GetCookieValue(source.BookSourceUrl, "https://a.com/", "sid"));
    }

    // ============================================================
    // ⑧ java.ajax / post / ajaxAll / getStrResponse（同步桥）
    // ============================================================

    [Fact]
    public async Task Java_Ajax_Uses_Option_Block()
    {
        var inner = new RecordingFetcher
        {
            // 断言"选项块被 UrlAnalyzer 正确解析"：方法/体/限速都在 spec 上
            Handler = (url, spec) => ($"{spec?.Method}|{spec?.Body}|{spec?.ConcurrentRate}", url),
        };
        var runtime = new LegadoRuntime(_dir, inner);

        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "https://a.com/src";
        source.ConcurrentRate = "5";
        source.RuleContent = new Models.ContentRule
        {
            Content = """
                @js:
                var body = java.ajax('https://a.com/api,{"method":"POST","body":"k=v","headers":{"X-T":"1"}}');
                body
                """,
        };

        var engine = JsEngine(runtime);
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://a.com/c/1", null, "https://a.com/c/1");

        // POST（选项块 method）+ body（选项块 body）+ 限速随请求走
        Assert.Equal("POST|k=v|5", text);
        Assert.Contains("https://a.com/api", inner.Urls);
    }

    [Fact]
    public async Task Java_Post_Connect_Return_StrResponse()
    {
        var inner = new RecordingFetcher
        {
            Handler = (url, spec) => ("{\"ok\":1}", url),
        };
        var runtime = new LegadoRuntime(_dir, inner);
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "https://a.com/src2";
        source.RuleContent = new Models.ContentRule
        {
            Content = """
                @js:
                var r = java.post('https://a.com/p', 'a=1', {'X-A':'2'});
                var c = java.connect('https://a.com/q', {'X-B':'3'});
                [r.body(), r.code(), c.body(), String(r).slice(0,7)].join('|')
                """,
        };

        var engine = JsEngine(runtime);
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://a.com/c/1", null, "https://a.com/c/1");

        // RecordingFetcher 不是富抓取器 → code 退化为 200；body/toString 正常
        Assert.StartsWith("{\"ok\":1}|", text);
        Assert.Contains("|{\"ok\":1}|", text);
    }

    [Fact]
    public async Task Java_AjaxAll_Returns_Array_Of_Responses()
    {
        var inner = new RecordingFetcher
        {
            Handler = (url, spec) => ("body:" + url, url),
        };
        var runtime = new LegadoRuntime(_dir, inner);
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "https://a.com/src3";
        source.RuleContent = new Models.ContentRule
        {
            Content = """
                @js:
                var list = java.ajaxAll(['https://a.com/1','https://a.com/2']);
                [list.length, list[0].body(), list[1].body()].join('|')
                """,
        };

        var engine = JsEngine(runtime);
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://a.com/c/1", null, "https://a.com/c/1");

        Assert.Equal("2|body:https://a.com/1|body:https://a.com/2", text);
    }

    [Fact]
    public async Task Java_Ajax_Failure_Records_Diagnostic_And_Returns_Empty()
    {
        var inner = new RecordingFetcher { Throw = true };
        var runtime = new LegadoRuntime(_dir, inner);
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "https://a.com/src4";
        source.RuleContent = new Models.ContentRule
        {
            Content = "@js:'[' + java.ajax('https://a.com/boom') + ']'",
        };

        var diagnostics = new JsDiagnostics();
        var engine = JsEngine(runtime, diagnostics);
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://a.com/c/1", null, "https://a.com/c/1");

        Assert.Equal("[]", text);
        Assert.Contains(diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.ScriptError);
    }

    [Fact]
    public async Task Java_Ajax_Without_Pipeline_Is_Honest()
    {
        // 未接线（纯 RuleEngine）：不假装成功，记 MissingApi 并返回空
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.RuleContent = new Models.ContentRule
        {
            Content = "@js:'[' + java.ajax('https://a.com/x') + ']'",
        };

        var diagnostics = new JsDiagnostics();
        var engine = new RuleEngine { Diagnostics = diagnostics };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://a.com/c/1", null, "https://a.com/c/1");

        Assert.Equal("[]", text);
        Assert.Contains(diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.MissingApi);
    }

    [Fact]
    public async Task Java_GetStrResponse_Returns_Last_Response()
    {
        var inner = new RecordingFetcher
        {
            Handler = (url, spec) => ("payload", url),
        };
        var runtime = new LegadoRuntime(_dir, inner);
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "https://a.com/src5";
        source.RuleContent = new Models.ContentRule
        {
            Content = """
                @js:
                java.ajax('https://a.com/x');
                var r = java.getStrResponse(null, null);
                [r.body(), r.code(), r.url(), r.isSuccessful].join('|')
                """,
        };

        var engine = JsEngine(runtime);
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://a.com/c/1", null, "https://a.com/c/1");

        Assert.Equal("payload|200|https://a.com/x|true", text);
    }

    // ============================================================
    // ⑨ 宿主接线（真实 HTTP 回环）：两条链共享 Cookie + java.ajax 带上 Cookie
    // ============================================================

    [Fact]
    public async Task Sync_Bridge_Runs_Off_The_Caller_SynchronizationContext()
    {
        // 方案 §3.2.2 的线程约定：JS 在非 UI 线程执行，同步桥才允许阻塞。
        // BookSourceEngine.ParseAsync 用 Task.Run 跑解析 → java.ajax 内的阻塞发生在池线程。
        SynchronizationContext? probed = new SynchronizationContext(); // 非 null 哨兵
        var inner = new RecordingFetcher
        {
            Handler = (url, spec) =>
            {
                probed = SynchronizationContext.Current;
                return ("ok", url);
            },
        };

        var engine = new BookSourceEngine(inner, store: new SourceStateStore(), cookies: new SourceCookieStore());
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "https://a.com/ctx";
        source.RuleContent = new Models.ContentRule { Content = "@js:java.ajax('https://a.com/x')" };

        var content = await engine.GetContentAsync(
            source, "https://a.com/c/1", source.BookSourceUrl, CancellationToken.None);

        Assert.NotNull(content);
        Assert.Equal("ok", content!.Content);
        Assert.Null(probed);
    }

    [Fact]
    public async Task BookSourceService_Shares_Cookies_Between_Search_And_Reading()
    {
        // 真实 HttpBookSourceFetcher + 回环 HTTP 服务：只有这样才能证明
        // "搜索链收到的 Set-Cookie → 阅读链请求带上"（D3 的端到端验收）。
        using var server = LoopbackServer.Start(request =>
        {
            if (request.Path.StartsWith("/search", StringComparison.Ordinal))
            {
                // 搜索响应下发 Cookie（模拟登录态/会话）
                return new LoopbackResponse(200, "{\"books\":[]}", new[] { "sid=xyz; Path=/" });
            }

            return new LoopbackResponse(200, "正文|" + request.Header("Cookie"), null);
        });

        var runtime = new LegadoRuntime(_dir);
        await using var service = new BookSourceService(_dir, runtime);

        var source = new BookSourceModel
        {
            BookSourceUrl = server.BaseUrl,
            SearchUrl = server.BaseUrl + "/search?q={{key}}",
            RuleSearch = new Models.SearchRule { BookList = "$.books[*]", Name = "$.name" },
            // 空串 = 整份响应体就是正文（reader 语义）；"all" 会被当标签选择器而取到空
            RuleContent = new Models.ContentRule { Content = string.Empty },
        };

        await service.Search.SearchAsync(source, "k");

        // 搜索链的 Set-Cookie 已进入共享仓库
        Assert.Equal("xyz", service.Cookies.GetCookieValue(source.BookSourceUrl, server.BaseUrl, "sid"));

        var content = await service.Engine.GetContentAsync(
            source, server.BaseUrl + "/c/1", source.BookSourceUrl, CancellationToken.None);

        Assert.NotNull(content);
        Assert.Equal("正文|sid=xyz", content!.Content);
    }

    [Fact]
    public async Task Java_Ajax_Uses_Real_Http_And_Propagates_Cookies()
    {
        using var server = LoopbackServer.Start(request => request.Path.StartsWith("/api", StringComparison.Ordinal)
            ? new LoopbackResponse(200, "resp|" + request.Method + "|" + request.Body + "|" + request.Header("Cookie"),
                new[] { "ajax=1; Path=/" })
            : new LoopbackResponse(200, "page", null));

        var runtime = new LegadoRuntime(_dir);
        runtime.Cookies.SetCookieHeader("src", server.BaseUrl, "sid=7");

        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.BookSourceUrl = "src";
        source.RuleContent = new Models.ContentRule
        {
            // 选项块：POST + body（java.ajax 的 "url,{json}" 形态）
            Content = "@js:java.ajax(baseUrl.replace(/\\/page.*/, '/api') + ',{\"method\":\"POST\",\"body\":\"k=v\"}')",
        };

        var engine = JsEngine(runtime);
        var (text, _, _) = engine.ParseContentPage(
            source,
            "<html></html>",
            server.BaseUrl + "/api",
            null,
            server.BaseUrl + "/api");

        // 静态请求与 java.ajax 共用 Cookie；选项块被正确解析成 POST + body
        Assert.Equal("resp|POST|k=v|sid=7", text);
        // 响应 Set-Cookie 同样进入仓库（下一步请求即可用）
        Assert.Equal("1", runtime.Cookies.GetCookieValue("src", server.BaseUrl, "ajax"));
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>
    /// 建一个"已接运行时节线"的 <see cref="RuleEngine"/>：测试直接用 <c>ParseContentPage</c>
    /// 驱动脚本时，取数管线/状态/Cookie 必须与运行时是同一份，否则测不到共享语义。
    /// </summary>
    private static RuleEngine JsEngine(LegadoRuntime runtime, JsDiagnostics? diagnostics = null) =>
        new()
        {
            Store = runtime.State,
            Fetcher = runtime.Fetcher,
            Cookies = runtime.Cookies,
            Diagnostics = diagnostics ?? new JsDiagnostics(),
        };

    /// <summary>记录调用并返回可控内容的取数替身（不联网）。</summary>
    private sealed class RecordingFetcher : IBookSourceFetcher
    {
        public bool Throw { get; init; }

        public Func<string, RequestSpec?, (string Content, string Url)>? Handler { get; init; }

        public List<string> Calls { get; } = new();

        public List<string> Urls { get; } = new();

        /// <summary>每次请求的 spec 快照（断言选项块/限速/方法用）。</summary>
        public List<RequestSpec?> Specs { get; } = new();

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(sourceKey ?? string.Empty);
            Urls.Add(url);
            Specs.Add(spec);
            if (Throw)
            {
                return Task.FromException<(string, string)>(new HttpRequestException("boom"));
            }

            var (content, finalUrl) = Handler?.Invoke(url, spec) ?? ("body", url);
            return Task.FromResult((content, finalUrl));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }

    /// <summary>回环 HTTP 请求的解析结果（够用即可：方法/路径/体/头）。</summary>
    private sealed record LoopbackRequest(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private sealed record LoopbackResponse(int Status, string Body, string[]? SetCookies);

    /// <summary>
    /// 极简回环 HTTP 服务（<see cref="System.Net.Sockets.TcpListener"/>，无需管理员 URL ACL）。
    /// <para>只支持 "一行请求行 + 头 + 可选 Content-Length 体"，响应后立即关闭连接
    /// （<c>Connection: close</c>）——足够测试 Cookie/方法/体/状态码，且不引入测试用 Web 服务器依赖。</para>
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private LoopbackServer(System.Net.Sockets.TcpListener listener, Func<LoopbackRequest, LoopbackResponse> handler)
        {
            _listener = listener;
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = Task.Run(() => AcceptLoopAsync(handler, _cts.Token));
        }

        public string BaseUrl { get; }

        public static LoopbackServer Start(Func<LoopbackRequest, LoopbackResponse> handler)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackServer(listener, handler);
        }

        private async Task AcceptLoopAsync(Func<LoopbackRequest, LoopbackResponse> handler, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client, handler), CancellationToken.None);
            }
        }

        private static async Task HandleAsync(System.Net.Sockets.TcpClient client, Func<LoopbackRequest, LoopbackResponse> handler)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "GET";
                var path = parts.Length > 1 ? parts[1] : "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                var body = string.Empty;
                if (headers.TryGetValue("Content-Length", out var lengthText)
                    && int.TryParse(lengthText, out var length) && length > 0)
                {
                    var buffer = new char[length];
                    var read = 0;
                    while (read < length)
                    {
                        var n = await reader.ReadAsync(buffer.AsMemory(read, length - read)).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    body = new string(buffer, 0, read);
                }

                var response = handler(new LoopbackRequest(method, path, body, headers));
                var payload = System.Text.Encoding.UTF8.GetBytes(response.Body);
                var sb = new System.Text.StringBuilder();
                sb.Append("HTTP/1.1 ").Append(response.Status).Append(" OK\r\n");
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
                sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                sb.Append("Connection: close\r\n");
                foreach (var cookie in response.SetCookies ?? Array.Empty<string>())
                {
                    sb.Append("Set-Cookie: ").Append(cookie).Append("\r\n");
                }

                sb.Append("\r\n");
                var head = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
                await stream.WriteAsync(head).ConfigureAwait(false);
                await stream.WriteAsync(payload).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

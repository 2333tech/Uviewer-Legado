using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源 JS 的<b>同步网络原语</b>（阶段 4-3，T1）：<c>java.ajax</c> / <c>post</c> / <c>connect</c> /
/// <c>head</c> / <c>ajaxAll</c> / <c>ajaxTestAll</c> / <c>getStrResponse</c>。
///
/// <para><b>语义来源</b>：legado <c>JsExtensions.kt</c>（ajax 失败返回错误文本、ajaxAll 返回
/// <c>Array&lt;StrResponse&gt;</c>、post/connect/head 返回 Jsoup <c>Connection.Response</c>）；
/// reader <c>js.rs</c> 的 <c>J.ajax/J.post/J.connect/J.head/J.ajaxAll/J.getStrResponse</c>
/// （StrResponse 用 <c>body()/code()/url()/headers()</c> 访问）。</para>
///
/// <para><b>同步桥</b>：Jint 里脚本是同步执行的，这里对异步 fetcher 做
/// <c>GetAwaiter().GetResult()</c> 阻塞（与 legado <c>runBlocking</c> 同性质）。因此
/// <b>调用方必须在非 UI 线程</b>：<see cref="RuleEngine"/> 的解析入口已由
/// <c>BookSourceEngine</c>/<c>SearchService</c> 放进线程池（<c>Task.Run</c>）。
/// 单个原语阻塞超过 <see cref="SlowCallThresholdMs"/> 会记一条
/// <see cref="JsDiagnosticKind.SlowNetwork"/>，供界面提示"慢源"。</para>
///
/// <para><b>Cookie / 限速 / 重试</b>：全部复用注入的 <see cref="IBookSourceFetcher"/> 管线
/// （<see cref="RateLimitedFetcher"/> 套 <c>HttpBookSourceFetcher</c>）——
/// 故 <c>java.ajax</c> 与静态请求共享 Cookie（D3）与限速，不另开一套网络栈。</para>
/// </summary>
internal sealed class JsNetworkSession
{
    /// <summary>单次同步网络原语超过该毫秒数记"慢调用"诊断（方案 §3.2.2 的 2s 阈值）。</summary>
    public const int SlowCallThresholdMs = 2000;

    /// <summary>最近一次网络响应（<c>java.getStrResponse()</c> 无参时的语义，legado 同）。</summary>
    public FetchResponse? LastResponse { get; private set; }

    private JsScope? _scope;

    /// <summary>
    /// 派发一个网络原语。<paramref name="currentBody"/>/<paramref name="baseUrl"/> 是本次脚本的
    /// <c>result</c>/<c>baseUrl</c>（<c>getStrResponse</c> 在没有网络响应时用它们兜底）。
    /// </summary>
    public object? Invoke(
        Engine engine,
        JsScope scope,
        string name,
        object?[] args,
        string currentBody,
        string baseUrl)
    {
        // 换链（新 scope）时清空"上一次响应"：legado 的 getStrResponse() 只应看到本链的响应
        if (!ReferenceEquals(_scope, scope))
        {
            _scope = scope;
            LastResponse = null;
        }

        return name switch
        {
            "ajax" => Ajax(scope, args),
            "post" => Post(engine, scope, args),
            "connect" => Connect(engine, scope, args),
            "head" => Head(engine, scope, args),
            "ajaxAll" => AjaxAll(engine, scope, args, testMode: false),
            "ajaxTestAll" => AjaxAll(engine, scope, args, testMode: true),
            "getStrResponse" => GetStrResponse(engine, scope, currentBody, baseUrl),
            _ => null,
        };
    }

    // ============================================================
    // 各原语
    // ============================================================

    /// <summary><c>java.ajax(url[, timeout])</c> → 响应体字符串（失败返回空串并记诊断）。</summary>
    private string Ajax(JsScope scope, object?[] args)
    {
        var raw = JsPureApi.AsString(args, 0);
        var timeout = args.Length > 1 ? (int?)JsPureApi.AsLong(args, 1) : null;
        var response = TryFetch(scope, "ajax", raw, method: null, body: null, headers: null, timeoutMs: timeout);
        return response?.Content ?? string.Empty;
    }

    /// <summary>
    /// <c>java.post(url, body, headers)</c> → StrResponse。
    /// <para>headers 参数兼容 JS 对象 / JSON 字符串（legado 传 Map，reader 传对象或 JSON）。</para>
    /// <para>失败返回<b>空响应对象</b>而不是 null：旧实现返回 null，脚本 <c>resp.body()</c>
    /// 遇 null 抛 TypeError 整段回退——而 ajaxAll 的失败项早就是空响应对象，口径统一。</para>
    /// </summary>
    private object? Post(Engine engine, JsScope scope, object?[] args)
    {
        var raw = JsPureApi.AsString(args, 0);
        var body = JsPureApi.AsString(args, 1);
        var headers = ParseHeaders(args.Length > 2 ? args[2] : null);
        var response = TryFetch(scope, "post", raw, "POST", body, headers, timeoutMs: null);
        return response is null
            ? BuildResponse(engine, scope, "post", new FetchResponse(string.Empty, raw, 0, EmptyHeaders))
            : BuildResponse(engine, scope, "post", response);
    }

    /// <summary><c>java.connect(url[, headers])</c> → StrResponse（可带选项块 <c>,{...}</c>）。失败同 post 返空响应。</summary>
    private object? Connect(Engine engine, JsScope scope, object?[] args)
    {
        var raw = JsPureApi.AsString(args, 0);
        var headers = ParseHeaders(args.Length > 1 ? args[1] : null);
        var response = TryFetch(scope, "connect", raw, method: null, body: null, headers, timeoutMs: null);
        return response is null
            ? BuildResponse(engine, scope, "connect", new FetchResponse(string.Empty, raw, 0, EmptyHeaders))
            : BuildResponse(engine, scope, "connect", response);
    }

    /// <summary><c>java.head(url[, headers])</c> → StrResponse。失败同 post 返空响应。</summary>
    private object? Head(Engine engine, JsScope scope, object?[] args)
    {
        var raw = JsPureApi.AsString(args, 0);
        var headers = ParseHeaders(args.Length > 1 ? args[1] : null);
        var response = TryFetch(scope, "head", raw, "HEAD", body: null, headers, timeoutMs: null);
        return response is null
            ? BuildResponse(engine, scope, "head", new FetchResponse(string.Empty, raw, 0, EmptyHeaders))
            : BuildResponse(engine, scope, "head", response);
    }

    /// <summary>
    /// <c>java.ajaxAll(urls[, skipRateLimit])</c> / <c>java.ajaxTestAll(urls, timeoutMs)</c>
    /// → <c>Array&lt;StrResponse&gt;</c>。
    /// <para>legado 用并发（threadCount）执行；这里**串行**（与 reader 一致）：Jint 里并发拿
    /// 同一书源的 Cookie/限速会互相阻塞，且脚本拿到的顺序必须与输入一致。
    /// 失败项也返回一个空响应对象（脚本用 <c>responses[i].body()</c> 时不会因 null 崩）。</para>
    /// </summary>
    private object? AjaxAll(Engine engine, JsScope scope, object?[] args, bool testMode)
    {
        var urls = ParseUrlList(args.Length > 0 ? args[0] : null);
        if (urls.Count == 0)
        {
            return new JsArray(engine, Array.Empty<JsValue>());
        }

        int? timeout = null;
        if (testMode && args.Length > 1)
        {
            // legado ajaxTestAll 的 timeout 是毫秒；请求 spec 用秒（至少 1 秒）
            var ms = JsPureApi.AsLong(args, 1);
            if (ms > 0)
            {
                timeout = (int)Math.Max(1, Math.Ceiling(ms / 1000d));
            }
        }

        var values = new JsValue[urls.Count];
        for (var i = 0; i < urls.Count; i++)
        {
            var response = TryFetch(scope, testMode ? "ajaxTestAll" : "ajaxAll", urls[i],
                method: null, body: null, headers: null, timeoutMs: timeout);
            values[i] = response is null
                ? BuildResponse(engine, scope, "ajaxAll", new FetchResponse(string.Empty, urls[i], 0, EmptyHeaders))
                : BuildResponse(engine, scope, "ajaxAll", response);
        }

        return new JsArray(engine, values);
    }

    /// <summary>
    /// <c>java.getStrResponse([jsStr, sourceRegex])</c> → StrResponse。
    /// <para>legado 里参数是"页面内执行 JS / 响应裁剪正则"，不是 URL；无参数时返回当前页响应。
    /// 这里：有网络响应 → 返回最近一次；否则用本次脚本的 <c>result</c>/<c>baseUrl</c> 兜底构造
    /// （<c>code</c> 不可知 → 0，并记一条诊断，避免脚本误以为 200）。</para>
    /// </summary>
    private object? GetStrResponse(Engine engine, JsScope scope, string currentBody, string baseUrl)
    {
        if (LastResponse is not null)
        {
            return BuildResponse(engine, scope, "getStrResponse", LastResponse);
        }

        scope.Diagnostics.RecordStateError(
            scope.SourceKey, null, "java.getStrResponse 无已记录响应（用当前 result 兜底，code 不可知）");
        return BuildResponse(engine, scope, "getStrResponse",
            new FetchResponse(currentBody, baseUrl, 0, EmptyHeaders));
    }

    // ============================================================
    // 取数（同步桥）
    // ============================================================

    /// <summary>
    /// 编译 URL（含 <c>,{json}</c> 选项块）→ 过限速/重试管线 → 富响应。任何失败返回 null 并记诊断。
    /// </summary>
    private FetchResponse? TryFetch(
        JsScope scope,
        string api,
        string rawUrl,
        string? method,
        string? body,
        IReadOnlyDictionary<string, string>? headers,
        int? timeoutMs)
    {
        var fetcher = scope.Fetcher;
        if (fetcher is null)
        {
            scope.Diagnostics.RecordMissingApi(scope.SourceKey, null, api + "(无取数管线)");
            return null;
        }

        // 与引擎同一条编译链：选项块、模板、charset 等全部复用 UrlAnalyzer。
        // 编译本身也可能抛（畸形选项块/非法模板）：这里兜住并记诊断，不让异常冒到脚本外层。
        RequestSpec spec;
        string url;
        try
        {
            spec = new UrlAnalyzer().BuildSpec(rawUrl, string.Empty, 1, scope.SourceKey);
            url = UrlUtil.AbsoluteUrl(scope.SourceKey, spec.Url);
        }
        catch (Exception ex)
        {
            scope.Diagnostics.RecordStateError(scope.SourceKey, null, api + ": URL 编译失败（" + ex.Message + "）");
            return null;
        }

        if (url.Length == 0)
        {
            scope.Diagnostics.RecordStateError(scope.SourceKey, null, api + ": URL 为空（" + rawUrl + "）");
            return null;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // data: / 相对协议以外的东西不在静态管线里；如实记诊断，不假装拿到内容
            scope.Diagnostics.RecordStateError(scope.SourceKey, null, api + ": 非 HTTP(S) URL（" + url + "）");
            return null;
        }

        // 书源限速随请求走（RateLimitedFetcher 消费；未套该装饰器时是无效字段）
        spec = spec with { ConcurrentRate = scope.Source?.ConcurrentRate };

        if (!string.IsNullOrEmpty(method))
        {
            spec = spec with { Method = method };
        }

        if (body is not null)
        {
            spec = spec with { Body = body };
        }

        if (headers is { Count: > 0 })
        {
            spec = spec with { Headers = MergeHeaders(spec.Headers, headers) };
        }

        if (timeoutMs is { } ms)
        {
            // 原语的 timeout 参数是毫秒（legado ajaxTestAll 同），spec 用秒（至少 1 秒）
            spec = spec with { TimeoutSeconds = (int)Math.Max(1, Math.Ceiling(ms / 1000d)) };
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = fetcher is IRichResponseFetcher rich
                ? rich.FetchDetailedAsync(url, spec, scope.SourceKey, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult()
                : BuildFromPlain(fetcher, url, spec, scope);

            LastResponse = response;
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= SlowCallThresholdMs)
            {
                scope.Diagnostics.RecordSlowNetwork(scope.SourceKey, null, api, stopwatch.ElapsedMilliseconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            // legado 的 ajax 失败会返回异常文本；这里返回 null → 上层给空串/空响应，
            // 但**必须留下诊断**（否则又变成"脚本默默拿不到内容"）。
            scope.Diagnostics.RecordScriptError(scope.SourceKey, api, ex);
            return null;
        }
    }

    /// <summary>测试替身（非富抓取器）路径：没有状态码，按"有内容 200 / 空 0"退化。</summary>
    private static FetchResponse BuildFromPlain(
        IBookSourceFetcher fetcher, string url, RequestSpec spec, JsScope scope)
    {
        var (content, baseUrl) = fetcher
            .FetchAsync(url, spec, scope.SourceKey, CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        return new FetchResponse(content, baseUrl, content.Length > 0 ? 200 : 0, EmptyHeaders);
    }

    /// <summary>空响应头（失败/替身路径复用）。</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(0);

    // ============================================================
    // StrResponse（JS 对象）
    // ============================================================

    /// <summary>
    /// 构造 legado/reader 兼容的 StrResponse：
    /// <c>body()</c> / <c>code()</c> / <c>url()</c> / <c>header(name)</c> / <c>headers</c> /
    /// <c>isSuccessful</c> / <c>toString()</c>/<c>valueOf()</c>。
    /// <para><c>headers</c> 既是函数（<c>resp.headers()</c> 返回映射）又挂同名属性
    /// （<c>resp.headers.Location</c>）——两种历史写法都有人用（样本里两种都出现过）。</para>
    /// </summary>
    internal static JsValue BuildResponse(Engine engine, JsScope scope, string api, FetchResponse response) =>
        BuildResponseCore(engine, scope.Diagnostics, scope.SourceKey, api, response);

    /// <summary>
    /// 无作用域时的空 StrResponse（body 空串 / code 0）——失败口径统一：
    /// 脚本 <c>resp.body()</c> 拿到空串而不是对 null 抛 TypeError。
    /// </summary>
    internal static JsValue EmptyResponse(Engine engine, string api) =>
        BuildResponseCore(engine, JsDiagnostics.Shared, null, api,
            new FetchResponse(string.Empty, string.Empty, 0, EmptyHeaders));

    private static JsValue BuildResponseCore(
        Engine engine, JsDiagnostics diagnostics, string? sourceKey, string api, FetchResponse response)
    {
        var sink = new JsContextObjects.JsErrorSink(diagnostics, sourceKey, null);
        var obj = new JsObject(engine);

        obj.FastSetDataProperty("body", JsContextObjects.CreateFunction(engine, "body", _ => response.Content, sink));
        obj.FastSetDataProperty("code", JsContextObjects.CreateFunction(engine, "code", _ => response.StatusCode, sink));
        obj.FastSetDataProperty("url", JsContextObjects.CreateFunction(engine, "url", _ => response.BaseUrl, sink));
        obj.FastSetDataProperty("isSuccessful",
            JsContextObjects.ToJs(engine, response.StatusCode is >= 200 and < 300));
        obj.FastSetDataProperty("toString",
            JsContextObjects.CreateFunction(engine, "toString", _ => response.Content, sink));
        obj.FastSetDataProperty("valueOf",
            JsContextObjects.CreateFunction(engine, "valueOf", _ => response.Content, sink));

        // headers：映射 + 可调用（见方法注释）
        var map = new JsObject(engine);
        foreach (var (name, value) in response.Headers)
        {
            map.FastSetDataProperty(name, JsContextObjects.ToJs(engine, value));
        }

        var headersFn = JsContextObjects.CreateFunction(engine, "headers", _ => map, sink);
        if (headersFn is ObjectInstance headersObject)
        {
            foreach (var (name, value) in response.Headers)
            {
                headersObject.FastSetDataProperty(name, JsContextObjects.ToJs(engine, value));
            }
        }

        obj.FastSetDataProperty("headers", headersFn);
        obj.FastSetDataProperty("header", JsContextObjects.CreateFunction(engine, "header", args =>
        {
            var name = JsPureApi.AsString(args, 0);
            // 头名大小写不敏感（HTTP 语义）：书源里 resp.header("content-type")
            // 查 Content-Type 是小写查询的常见写法，Ordinal 原样查会查不到
            foreach (var (k, v) in response.Headers)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                {
                    return v;
                }
            }

            return null;
        }, sink));

        return obj;
    }

    // ============================================================
    // 参数解析
    // ============================================================

    /// <summary>把 JS 数组/JSON 数组字符串/逗号分隔串解析成 URL 列表。</summary>
    private static List<string> ParseUrlList(object? value)
    {
        var list = new List<string>();
        switch (value)
        {
            case null:
                break;
            case object?[] array:
                foreach (var item in array)
                {
                    var text = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }

                break;
            case string text:
                var trimmed = text.Trim();
                if (trimmed.StartsWith('['))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var element in doc.RootElement.EnumerateArray())
                            {
                                if (element.ValueKind == JsonValueKind.String
                                    && element.GetString() is { Length: > 0 } url)
                                {
                                    list.Add(url);
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // 非法 JSON：退化为逗号分隔
                    }
                }

                if (list.Count == 0)
                {
                    if (trimmed.Contains('{'))
                    {
                        // 含选项块（",{...}" 形态）：逗号切分会把选项块切碎，
                        // 整体作为一条交给 TryFetch 的选项块解析
                        list.Add(trimmed);
                    }
                    else
                    {
                        foreach (var part in trimmed.Split(','))
                        {
                            if (!string.IsNullOrWhiteSpace(part))
                            {
                                list.Add(part.Trim());
                            }
                        }
                    }
                }

                break;
            case IDictionary<string, object?> dict:
                foreach (var pair in dict)
                {
                    if (pair.Value is { } item)
                    {
                        list.Add(Convert.ToString(item) ?? string.Empty);
                    }
                }

                break;
        }

        return list;
    }

    /// <summary>解析 headers 参数：JS 对象（字典）/ JSON 字符串 / null。</summary>
    private static IReadOnlyDictionary<string, string>? ParseHeaders(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case IDictionary<string, object?> dict:
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, item) in dict)
                {
                    var text = item switch
                    {
                        null => string.Empty,
                        string s => s,
                        _ => JsonSerializer.Serialize(item),
                    };
                    headers[name] = text;
                }

                return headers.Count > 0 ? headers : null;
            }

            case string text when text.TrimStart().StartsWith('{'):
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        headers[property.Name] = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? string.Empty
                            : property.Value.GetRawText();
                    }

                    return headers.Count > 0 ? headers : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            default:
                return null;
        }
    }

    /// <summary>合并请求头：URL 选项块里的头为基，显式参数覆盖同名项。</summary>
    private static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string>? baseHeaders, IReadOnlyDictionary<string, string> extra)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseHeaders is not null)
        {
            foreach (var (name, value) in baseHeaders)
            {
                merged[name] = value;
            }
        }

        foreach (var (name, value) in extra)
        {
            merged[name] = value;
        }

        return merged;
    }
}

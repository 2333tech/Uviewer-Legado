using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-8 收尾测试：远程 jsLib 下载链路。
/// <para>语义锚点（legado AnalyzeRule + reader resolve_js_lib_entry）：
/// jsLib 支持裸 URL 与 JSON 映射两种远程形态；下载失败只记诊断并跳过该条目，
/// 其余条目照常执行；进程级按 URL 缓存成功结果。</para>
/// </summary>
public class Stage4WrapupTests
{
    /// <summary>构造带取数管线的 JsScope（远程 jsLib 下载的落点）。</summary>
    private static JsScope ScopeWith(IBookSourceFetcher fetcher) => new(
        sourceKey: "wrapup-src",
        fetcher: fetcher);

    private static JsBridge Bridge(IBookSourceFetcher fetcher, string jsLib, JsDiagnostics diagnostics)
    {
        var bridge = new JsBridge
        {
            Diagnostics = diagnostics,
            SourceKey = "wrapup-src",
            Field = "content",
            JsLib = jsLib,
        };
        bridge.Scope = ScopeWith(fetcher);
        return bridge;
    }

    [Fact]
    public void RemoteJsLib_JsonMap_Downloads_And_Injects()
    {
        // JSON 映射形态：值为 URL → 下载后拼进库；进程级缓存使第二次 Run 不再请求
        var fetcher = new CountingFetcher(
            new Dictionary<string, string> { ["/lib.js"] = "function hi(){ return 'hi-remote'; }" });
        var diagnostics = new JsDiagnostics();
        var bridge = Bridge(
            fetcher,
            """{"lib":"http://cdn.test/lib.js","inline":"function lo(){ return 'lo-inline'; }"}""",
            diagnostics);

        Assert.Equal("hi-remote|lo-inline", bridge.Run("hi() + '|' + lo()", "body", "https://x.test/"));
        Assert.Equal("hi-remote|lo-inline", bridge.Run("hi() + '|' + lo()", "body", "https://x.test/"));
        Assert.Equal(1, fetcher.RequestCount); // 缓存命中，只下载一次
        Assert.DoesNotContain(diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.ScriptError);
    }

    [Fact]
    public void RemoteJsLib_BareUrl_Form_Works()
    {
        // 裸 URL 形态（legado 允许 jsLib 直接填库地址）：整个 jsLib 就是一个 URL
        var fetcher = new CountingFetcher(
            new Dictionary<string, string> { ["/bare.js"] = "function bare(){ return 'bare-ok'; }" });
        var bridge = Bridge(fetcher, "http://cdn.test/bare.js", new JsDiagnostics());

        Assert.Equal("bare-ok", bridge.Run("bare()", "body", "https://x.test/"));
        // Compile 侧：裸 URL 的本地部分必须是空串（不能把 URL 文本当脚本执行）
        Assert.Equal(string.Empty, JsLibCompiler.Compile("http://cdn.test/bare.js"));
    }

    [Fact]
    public void RemoteJsLib_Failure_Skips_Entry_Records_Diagnostic()
    {
        // 下载失败（空响应）：记 ScriptError，其余条目照常（reader 语义）
        var diagnostics = new JsDiagnostics();
        var bridge = Bridge(
            new CountingFetcher(new Dictionary<string, string>()), // 无匹配 → 空响应
            """{"dead":"http://cdn.test/dead.js","inline":"function ok(){ return 'still-ok'; }"}""",
            diagnostics);

        Assert.Equal("still-ok", bridge.Run("ok()", "body", "https://x.test/"));
        // 诊断口径：Detail = 异常类型名，Sample = 消息（含 URL）
        Assert.Contains(diagnostics.Snapshot(), e =>
            e.Kind == JsDiagnosticKind.ScriptError && e.Sample.Contains("dead.js"));
    }

    [Fact]
    public void RemoteJsLib_NoFetcher_Records_Diagnostic_Instead_Of_Throwing()
    {
        // Scope 为 null（离线/旧调用路径）：不炸，记诊断并跳过（维持旧行为但显式可观测）
        var diagnostics = new JsDiagnostics();
        var bridge = new JsBridge
        {
            Diagnostics = diagnostics,
            SourceKey = "wrapup-src",
            Field = "content",
            JsLib = "http://cdn.test/nowhere.js",
        };

        Assert.Equal("body", bridge.Run("result", "body", "https://x.test/"));
        Assert.Contains(diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.ScriptError);
    }

    [Fact]
    public void ExtractRemoteUrls_All_Forms()
    {
        // ① 裸 URL
        Assert.Equal(new[] { "https://a.test/x.js" }, JsLibCompiler.ExtractRemoteUrls("https://a.test/x.js"));
        // ② JSON 映射（保序 + 去重）
        Assert.Equal(
            new[] { "https://a.test/x.js", "https://b.test/y.js" },
            JsLibCompiler.ExtractRemoteUrls(
                """{"k1":"https://a.test/x.js","inline":"var i=1;","k2":"https://b.test/y.js","k3":"https://a.test/x.js"}"""));
        // ③ 纯脚本 / 空 / 非法 JSON：无远程条目
        Assert.Empty(JsLibCompiler.ExtractRemoteUrls("function f(){}"));
        Assert.Empty(JsLibCompiler.ExtractRemoteUrls(null));
        Assert.Empty(JsLibCompiler.ExtractRemoteUrls("{not json"));
        // ④ HasRemoteEntry 覆盖裸 URL 形态
        Assert.True(JsLibCompiler.HasRemoteEntry("https://a.test/x.js"));
        Assert.False(JsLibCompiler.HasRemoteEntry("function f(){}"));
    }

    /// <summary>带请求计数的假取数器：URL 子串匹配返回映射内容，否则空串。</summary>
    private sealed class CountingFetcher(Dictionary<string, string> map) : IBookSourceFetcher
    {
        public int RequestCount;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url,
            RequestSpec? spec = null,
            string? sourceKey = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RequestCount);
            foreach (var kv in map)
            {
                if (url.Contains(kv.Key, StringComparison.Ordinal))
                {
                    return Task.FromResult((kv.Value, url));
                }
            }

            return Task.FromResult((string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url,
            string? referer = null,
            string? sourceKey = null,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    }
}

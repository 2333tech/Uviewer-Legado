using System.Reflection;
using System.Text.RegularExpressions;
using Uviewer.Services.Legado.BookSources;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-1：原语目录 / 调用面扫描 / 诊断 / T0 纯函数原语 / 能力口径修正。
/// <para>全部用例只读 <c>Sample/bookSource_202610192010.json</c> 的**原始条目**
/// （见 <see cref="SampleData.RequireAsync"/>），不触碰导入后的 <c>booksources.json</c>。</para>
/// </summary>
public class Stage4PrimitivesTests
{
    // ============================================================
    // ① 原语目录
    // ============================================================

    [Fact]
    public async Task Catalog_Covers_Every_Java_Api_Used_In_Raw_Sample()
    {
        // 原始样本里出现过的 java.X 必须全部登记（未登记会按 Missing 处理，虽然不会崩，
        // 但能力评估与诊断就没法给出可读的名字）。新增原语时这条会先红，逼你补 catalog。
        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        await foreach (var source in SampleData.EnumerateAsync())
        {
            foreach (var name in CollectJavaApis(source))
            {
                if (!JsApiCatalog.TryGet(name, out _))
                {
                    unknown.Add(name);
                }
            }
        }

        Assert.True(unknown.Count == 0, "未登记的原语：" + string.Join(",", unknown));
    }

    [Fact]
    public void Catalog_Tiers_And_Status_Are_Consistent()
    {
        // 已实现的原语不允许再出现在 MissingNames 里（4-3 起含网络族，4-5 起含 T2 规则族）
        Assert.Empty(JsApiCatalog.MissingNames(
            new[] { "getString", "put", "get", "md5Encode", "timeFormat", "ajax", "post", "connect", "head", "ajaxAll",
                    "getStringList", "getElements", "getElement", "setContent" }));

        // 未实现的必须出现在 MissingNames（refreshTocUrl 待 4-6 登录链路；
        // ruleUrl 是"属性读法"的空串桩（Stub 同样计入缺口，由 RuleUrl 行为测试单独覆盖）
        Assert.Equal(new[] { "refreshTocUrl", "ruleUrl" }, JsApiCatalog.MissingNames(new[] { "refreshTocUrl", "ruleUrl", "getString" }));

        // RequiredTier = 未实现原语里的最高层
        Assert.Equal(JsApiTier.Pure, JsApiCatalog.RequiredTier(new[] { "getString", "md5Encode" }));
        Assert.Equal(JsApiTier.Rule, JsApiCatalog.RequiredTier(new[] { "getString", "refreshTocUrl" }));
        Assert.Equal(JsApiTier.Rule, JsApiCatalog.RequiredTier(new[] { "refreshTocUrl", "getStringList" }));

        // 未登记名字按 Browser（最保守）处理
        Assert.Equal(JsApiTier.Browser, JsApiCatalog.TierOf("neverHeardOfIt"));
    }

    // ============================================================
    // ② 调用面扫描
    // ============================================================

    [Fact]
    public void Surface_Extracts_Java_And_Globals()
    {
        var surface = JsApiSurfaceScanner.Scan(
            "var x = java . md5Encode(a); var y = java.ajax('u,' + JSON.stringify({}));" +
            " var n = book.name; var c = cache.get('k');");

        Assert.Contains("md5Encode", surface.JavaCalls);
        Assert.Contains("ajax", surface.JavaCalls);
        Assert.Contains("book", surface.Globals);
        Assert.Contains("cache", surface.Globals);
    }

    [Fact]
    public void Surface_Ignores_Local_Variables_And_Unknown_Members()
    {
        // book/cache 作为局部变量（成员名不在白名单）不参与判定——否则会大面积误判"缺全局对象"
        var surface = JsApiSurfaceScanner.Scan("var book = {}; book.foo; var cache = {}; cache.length;");

        Assert.Empty(surface.Globals);
        Assert.True(surface.IsEmpty);
    }

    [Fact]
    public void Surface_Ignores_Prefixed_Access()
    {
        // $.book / a.book 不是全局对象；前置字符排除
        var surface = JsApiSurfaceScanner.Scan("var x = $.book.name; var y = a.java.ajax();");

        Assert.Empty(surface.Globals);
        Assert.Empty(surface.JavaCalls);
    }

    // ============================================================
    // ③ T0 纯函数原语（经 JsBridge 走完整 JS 通道）
    // ============================================================

    [Fact]
    public void Pure_Md5_And_Md5_16()
    {
        using var scope = new BridgeScope();
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", scope.Run("java.md5Encode('abc')"));
        Assert.Equal("3cd24fb0d6963f7d", scope.Run("java.md5Encode16('abc')"));
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", scope.Run("java.md5('abc')"));
    }

    [Fact]
    public void Pure_Time_Format()
    {
        using var scope = new BridgeScope();

        // timeFormatUTC：偏移 0 时是确定值（本地时区的 timeFormat 只断言格式）
        Assert.Equal("1970-01-01 00:00", scope.Run("java.timeFormatUTC(0,'yyyy-MM-dd HH:mm',0)"));
        Assert.Equal("1970-01-01 08:00", scope.Run("java.timeFormatUTC(0,'yyyy-MM-dd HH:mm',8)"));
        Assert.Matches(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}$", scope.Run("java.timeFormat(0)"));
    }

    [Fact]
    public void Pure_Encode_And_Hex()
    {
        using var scope = new BridgeScope();
        Assert.Equal("a+b", scope.Run("java.encodeURI('a b')"));
        Assert.Equal("e4b8ad", scope.Run("java.hexEncodeToString('中')"));
        Assert.Equal("中", scope.Run("java.hexDecodeToString('e4b8ad')"));
        Assert.Equal("5Lit", scope.Run("java.base64Encode('中')"));
        Assert.Equal("中", scope.Run("java.base64Decode('5Lit')"));
    }

    [Theory]
    [InlineData("第一百二十三章", "第123章")]
    [InlineData("第一章", "第1章")]
    [InlineData("第1025章", "第1025章")]
    [InlineData("第一千零二章", "第1002章")]
    [InlineData("第两百章", "第200章")]
    [InlineData("正文", "正文")]
    public void Pure_ToNumChapter(string input, string expected)
    {
        using var scope = new BridgeScope();
        Assert.Equal(expected, scope.Run($"java.toNumChapter({JsString(input)})"));
    }

    [Fact]
    public void Pure_Lang_Namespace()
    {
        using var scope = new BridgeScope();

        Assert.Equal("123", scope.Run("String(java.lang.Integer.parseInt('123'))"));
        Assert.Equal("0", scope.Run("String(java.lang.Math.abs(0))"));
        Assert.Equal("true", scope.Run("String(String(java.lang.System.currentTimeMillis()).length >= 13)"));
    }

    // ============================================================
    // ④ 诊断：缺 API 不再让整段脚本作废
    // ============================================================

    [Fact]
    public void Missing_Api_Is_Recorded_But_Script_Keeps_Running()
    {
        using var scope = new BridgeScope();

        var output = scope.Run("var a = java.neverImplementedApi('x'); result + '|ok'");

        Assert.Equal("body|ok", output);
        var entry = Assert.Single(scope.Diagnostics.Snapshot(),
            e => e.Kind == JsDiagnosticKind.MissingApi);
        Assert.Equal("neverImplementedApi", entry.Detail);
        Assert.Equal("src-1", entry.SourceKey);
        Assert.Equal("content", entry.Field);
    }

    [Fact]
    public void Stub_Call_Is_Recorded()
    {
        using var scope = new BridgeScope();

        // getStringList/getElements/setContent 在 4-5 已成真实实现；ajaxJson 仍是桩（legado 无此正式 API）。
        scope.Run("java.ajaxJson('x')");

        var entry = Assert.Single(scope.Diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.StubCalled);
        Assert.Equal("ajaxJson", entry.Detail);
    }

    [Fact]
    public void Script_Error_Falls_Back_To_Raw_And_Is_Recorded()
    {
        using var scope = new BridgeScope();

        var output = scope.Run("throw new Error('boom')");

        Assert.Equal("body", output);
        var entry = Assert.Single(scope.Diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.ScriptError);
        Assert.Contains("boom", entry.Sample);
    }

    [Fact]
    public void Log_And_Toast_Are_Recorded_Without_Changing_Result()
    {
        using var scope = new BridgeScope();

        var output = scope.Run("java.log('hello', 42); java.toast('t'); result");

        Assert.Equal("body", output);
        var logs = scope.Diagnostics.Snapshot().Where(e => e.Kind == JsDiagnosticKind.Log).ToList();
        Assert.Contains(logs, e => e.Sample.Contains("hello"));
        Assert.Single(scope.Diagnostics.Snapshot(), e => e.Kind == JsDiagnosticKind.Toast);
    }

    [Fact]
    public void Put_Get_Shares_State_Across_Runs()
    {
        using var scope = new BridgeScope();

        scope.Run("java.put('k','v')");
        Assert.Equal("v", scope.Run("java.get('k')"));
    }

    [Fact]
    public void Diagnostics_Aggregates_Counts()
    {
        var diagnostics = new JsDiagnostics();
        for (var i = 0; i < 3; i++)
        {
            diagnostics.RecordMissingApi("s", "content", "ajax");
        }

        var entry = Assert.Single(diagnostics.Snapshot());
        Assert.Equal(3, entry.Count);
        Assert.Equal(new[] { "ajax" }, diagnostics.MissingApisOf("s"));
    }

    // ============================================================
    // ⑤ 能力口径修正（原始条目全量基线）
    // ============================================================

    [Fact]
    public async Task Capabilities_Raw_Sample_Baseline()
    {
        // 这条是全量基线：口径再变时会立刻红，逼你同步文档与 ROI 数字。
        // 2026-10-21（4-7 WebView 落地后）实测值：startBrowser/startBrowserAwait/webView 实现后，
        // 阅读链路可跑 377 → 397（+20）、整源 365 → 389；
        // 阅读链缺口只剩登录依赖（refreshTocUrl 6，待 4-6）、词典类（t2s 5）、openUrl（1，暂缓）。
        var all = await SampleData.AllAsync();
        Assert.Equal(408, all.Count);

        var readingOk = 0;
        var allOk = 0;
        var needsStage4 = 0;
        var missingApiHistogram = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var source in all)
        {
            var report = BookSourceCapabilities.AssessDetailed(source);
            if (report.ReadingSupported)
            {
                readingOk++;
            }

            if (report.AllSupported)
            {
                allOk++;
            }

            if (!report.ReadingSupported)
            {
                needsStage4++;
            }

            foreach (var api in report.ReadingMissingApis)
            {
                missingApiHistogram[api] = missingApiHistogram.GetValueOrDefault(api) + 1;
            }
        }

        Assert.Equal(397, readingOk);
        Assert.Equal(389, allOk);
        Assert.Equal(11, needsStage4);

        // 阅读链路 Top 缺口（后续步骤的 ROI 依据；4-7 后浏览器族已清零，只剩 3 项）
        var top = missingApiHistogram.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(5).Select(p => p.Key).ToArray();
        Assert.Equal(new[] { "refreshTocUrl", "t2s", "openUrl" }, top);
    }

    /// <summary>阅读链路当前可跑的原始条目数（2026-10-21，4-7 落地后；4-5b 为 377、4-5 为 363）。</summary>
    private const int ExpectedReadingOk = 397;

    /// <summary>整源（含搜索/详情链）当前可跑的原始条目数（4-7 落地后；4-5b 为 365、4-5 为 348）。</summary>
    private const int ExpectedAllOk = 389;

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>收集一个书源里所有脚本上下文原语名（与能力评估同一口径）。</summary>
    private static IEnumerable<string> CollectJavaApis(BookSourceModel source)
    {
        var builder = new JsApiSurfaceScanner.Builder();
        foreach (var rule in new object?[]
                 {
                     source.RuleSearch, source.RuleExplore, source.RuleBookInfo,
                     source.RuleToc, source.RuleContent,
                 })
        {
            if (rule is null)
            {
                continue;
            }

            foreach (var property in rule.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || !property.CanRead)
                {
                    continue;
                }

                var value = (string?)property.GetValue(rule);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (property.Name.EndsWith("Js", StringComparison.Ordinal)
                    || value.Contains("<js>", StringComparison.Ordinal)
                    || value.Contains("@js:", StringComparison.Ordinal)
                    || value.TrimStart().StartsWith("js:", StringComparison.Ordinal))
                {
                    builder.Add(value);
                }
            }
        }

        builder.Add(source.LoginCheckJs);
        builder.Add(source.JsLib);
        foreach (var url in new[] { source.SearchUrl, source.ExploreUrl, source.LoginUrl })
        {
            builder.Add(url);
        }

        return builder.ToSurface().JavaCalls;
    }

    private static string JsString(string text) => "'" + text.Replace("'", "\\'") + "'";

    /// <summary>JsBridge 测试壳：独立诊断实例 + 固定 sourceKey/field。</summary>
    private sealed class BridgeScope : IDisposable
    {
        public BridgeScope()
        {
            Diagnostics = new JsDiagnostics();
            _bridge = new JsBridge
            {
                Diagnostics = Diagnostics,
                SourceKey = "src-1",
                Field = "content",
            };
        }

        private readonly JsBridge _bridge;

        public JsDiagnostics Diagnostics { get; }

        public string Run(string script) => _bridge.Run(script, "body", "https://example.com/book/1.html");

        public void Dispose() => Diagnostics.Clear();
    }
}

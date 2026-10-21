using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 5（阅读整合）库侧支撑测试：
/// ① jsLib 注入（对标 reader <c>parser/js.rs::compile_js_lib</c> + 上下文缓存语义）；
/// ② 书源能力静态评估（防"打开才静默失败"）；
/// ③ 阅读进度存储（断点续读数据层，范式与阶段 0 的 BookSourceManager 一致）。
/// </summary>
public class Phase5LibraryTests
{
    // ============================================================
    // ① jsLib 注入
    //   注：字段规则的正确形态是「选择器 + \n@js:脚本」（脚本里 result = 字段值），
    //       直接写 "@js:fn(x)" 不是字段规则形态（那是列表/整段脚本的形态）。
    // ============================================================

    [Fact]
    public void JsLib_Function_Is_Visible_To_Rule_Script()
    {
        var source = Source(
            jsLib: "function pad(s){ return '[' + s + ']'; }",
            tocRule: new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title\n@js:pad(result)",
                ChapterUrl = "id\n@js:pad(result)",
            });

        var (chapters, _) = new RuleEngine().ParseTocPage(
            source,
            """{"data":[{"id":"/c/1","title":"第一章"}]}""",
            "https://api.example.com");

        Assert.Single(chapters);
        Assert.Equal("[第一章]", chapters[0].ChapterName);
        Assert.Equal("[第一章]", chapters[0].ChapterName);
    }

    [Fact]
    public void JsLib_Json_Form_Multiple_Entries_Are_Concatenated()
    {
        // legado 的 {"name":"脚本或URL"} 形式：两个内联条目都要生效（后者可调用前者）
        var source = Source(
            jsLib: """{"libA":"function tagA(s){ return 'A-' + s; }","libB":"function tagB(s){ return 'B-' + s; }"}""",
            tocRule: new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title\n@js:tagA(tagB(result))",
                ChapterUrl = "id\n@js:tagB(result)",
            });

        var (chapters, _) = new RuleEngine().ParseTocPage(
            source,
            """{"data":[{"id":"/c/1","title":"第一章"}]}""",
            "https://api.example.com");

        Assert.Single(chapters);
        Assert.Equal("A-B-第一章", chapters[0].ChapterName);
        // ChapterUrl 会走 URL 绝对化（相对路径拼 baseUrl，reader 语义）
        Assert.Equal("https://api.example.com/B-/c/1", chapters[0].ChapterUrl);
    }

    [Fact]
    public void JsLib_Remote_Entry_Is_Skipped_But_Others_Still_Work()
    {
        // reader：远程条目离线下载失败只记日志，其余条目照常执行
        var source = Source(
            jsLib: """{"remote":"https://cdn.example.com/lib.js","inline":"function tagX(s){ return 'X:' + s; }"}""",
            tocRule: new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title\n@js:tagX(result)",
            });

        var (chapters, _) = new RuleEngine().ParseTocPage(
            source,
            """{"data":[{"id":"/c/1","title":"第一章"}]}""",
            "https://api.example.com");

        Assert.Single(chapters);
        Assert.Equal("X:第一章", chapters[0].ChapterName);
    }

    [Fact]
    public void JsLibCompiler_Bare_Script_Passes_Through_Trimmed()
    {
        // ① 裸脚本形态（最常见）：原样（去空白后）作为库代码
        Assert.Equal("function f(){ return 1; }", JsLibCompiler.Compile("  function f(){ return 1; }  "));
        Assert.Equal(string.Empty, JsLibCompiler.Compile(null));
        Assert.Equal(string.Empty, JsLibCompiler.Compile("   "));
    }

    [Fact]
    public void JsLibCompiler_Json_Object_Concatenates_Inline_Entries_And_Skips_Remote()
    {
        // ② JSON 对象形态：内联条目按顺序拼接，远程 URL 条目跳过（reader：离线只记日志）
        var compiled = JsLibCompiler.Compile(
            """{"a":"var A=1;","remote":"https://cdn.example.com/x.js","b":"var B=2;"}""");

        Assert.Contains("var A=1;", compiled, StringComparison.Ordinal);
        Assert.Contains("var B=2;", compiled, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example.com", compiled, StringComparison.Ordinal);
        // A 在 B 之前（JSON 对象保序）
        Assert.True(compiled.IndexOf("var A=1;", StringComparison.Ordinal)
            < compiled.IndexOf("var B=2;", StringComparison.Ordinal));
        Assert.True(JsLibCompiler.HasRemoteEntry("""{"a":"https://cdn.example.com/x.js"}"""));
        Assert.False(JsLibCompiler.HasRemoteEntry("""{"a":"var A=1;"}"""));
    }

    [Fact]
    public void JsLibCompiler_Invalid_Json_Falls_Back_To_Whole_As_Inline_Script()
    {
        // ③ 以 { 开头但非法 JSON → 整体按裸脚本处理（reader 同）；不是 JSON 对象也不是数组时同样退化
        const string broken = "{ 这不是 JSON, 只是裸脚本 }";
        Assert.Equal(broken, JsLibCompiler.Compile(broken));
        Assert.False(JsLibCompiler.HasRemoteEntry(broken));

        // 非对象 JSON（数组/字面量）也按裸脚本退化
        Assert.Equal("[1,2,3]", JsLibCompiler.Compile("[1,2,3]"));
    }

    [Fact]
    public void JsLib_Broken_Code_Falls_Back_And_Rules_Keep_Working()
    {
        // 库代码语法错误：legado 语义 = 脚本异常回退原文，不中断整条解析链。
        // 目录列表规则仍应按可用路径（本例为 JSONPath）继续解析出章节。
        var source = Source(
            jsLib: "function {{{ 语法错误",
            tocRule: new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title",
                ChapterUrl = "id",
            });

        var (chapters, _) = new RuleEngine().ParseTocPage(
            source,
            """{"data":[{"id":"/c/1","title":"第一章"},{"id":"/c/2","title":"第二章"}]}""",
            "https://api.example.com");

        Assert.Equal(2, chapters.Count);
        Assert.Equal("第一章", chapters[0].ChapterName);
    }

    [Fact]
    public void JsLib_Loaded_Once_Per_Engine_And_Survives_Multiple_Pages()
    {
        // 库在上下文初始化时执行一次，后续多条规则/多次解析共用同一份已定义的函数。
        // 这里连跑三页目录（同一 RuleEngine → 同一 JsBridge → 同一 Jint 引擎）。
        var source = Source(
            jsLib: "function tagZ(s){ return 'Z' + s; }",
            tocRule: new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title\n@js:tagZ(result)",
            });

        var engine = new RuleEngine();
        for (var page = 1; page <= 3; page++)
        {
            var (chapters, _) = engine.ParseTocPage(
                source,
                $$"""{"data":[{"id":"/c/{{page}}","title":"第{{page}}章"}]}""",
                "https://api.example.com");

            Assert.Single(chapters);
            Assert.Equal($"Z第{page}章", chapters[0].ChapterName);
        }
    }

    [Fact]
    public void JsLib_Not_Leaked_Between_Sources_On_Same_Engine()
    {
        // 同一 RuleEngine 先解析带 jsLib 的源 A，再解析无 jsLib 的源 B：
        // B 不能"白拿"A 定义的函数（否则书源之间会串味）。
        var engine = new RuleEngine();

        var withLib = Source(
            jsLib: "function tagW(s){ return 'W' + s; }",
            tocRule: new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title\n@js:tagW(result)",
            });

        var (a, _) = engine.ParseTocPage(
            withLib, """{"data":[{"id":"/c/1","title":"甲"}]}""", "https://api.example.com");
        Assert.Equal("W甲", a[0].ChapterName);

        // 源 B：字段规则引用 A 的函数 → A 的库文本变了，B 应重新加载（此处为空库）→ 脚本报错回退原文
        var other = Source(tocRule: new TocRule
        {
            ChapterList = "data[*]",
            ChapterName = "title\n@js:tagW(result)",
        });

        var (b, _) = engine.ParseTocPage(
            other, """{"data":[{"id":"/c/1","title":"乙"}]}""", "https://api.example.com");
        Assert.Single(b);
        // tagW 不存在 → 脚本异常 → Run 回退原始字段值（即未经加工的 "乙"）
        Assert.Equal("乙", b[0].ChapterName);
    }

    [Fact]
    public void JsLib_Works_In_Search_Chain_Too()
    {
        var source = Source(jsLib: "function up(s){ return s.toUpperCase(); }");
        source.RuleSearch = new SearchRule
        {
            BookList = "data[*]",
            Name = "title\n@js:up(result)",
            BookUrl = "id",
        };

        var books = new RuleEngine().SearchBooks(
            source, """{"data":[{"id":"/b/1","title":"book-a"}]}""", "https://api.example.com");

        Assert.Single(books);
        Assert.Equal("BOOK-A", books[0].Name);
    }

    [Fact]
    public void JsLib_Works_In_BookInfo_Chain()
    {
        var source = Source(jsLib: "function wrap(s){ return '《' + s + '》'; }");
        source.RuleBookInfo = new BookInfoRule
        {
            Name = "name\n@js:wrap(result)",
            Author = "author",
        };

        var info = new RuleEngine().ParseBookInfo(
            source, """{"name":"平凡的世界","author":"路遥"}""", "https://api.example.com");

        Assert.NotNull(info);
        Assert.Equal("《平凡的世界》", info!.Name);
        Assert.Equal("路遥", info.Author);
    }

    // ============================================================
    // ② 能力静态评估
    // ============================================================

    [Fact]
    public void Capabilities_Clean_Source_Is_None()
    {
        var source = Source(tocRule: new TocRule { ChapterList = "data[*]", ChapterName = "title" });

        Assert.Equal(BookSourceCapability.None, BookSourceCapabilities.Assess(source));
        Assert.True(BookSourceCapabilities.IsFullySupported(source));
        Assert.False(BookSourceCapabilities.ReadingNeedsStage4(source));
    }

    [Fact]
    public void Capabilities_Detects_Java_Call_Only_In_Script_Context()
    {
        // 规则值里的 java.* 且带 JS 标记，且该原语**尚未实现** → 需要阶段 4
        // （ajax 在 4-3、加解密族在 4-5b 已成真实实现，这里改用仍是缺口的 refreshTocUrl）
        var marked = Source(contentRule: new ContentRule { Content = "<js>java.refreshTocUrl()</js>" });
        Assert.True(BookSourceCapabilities.Assess(marked).HasFlag(BookSourceCapability.RequiresJsEngine));

        // 阶段 4-1 口径修正：已实现的原语（getString/get/put/md5/base64/timeFormat…）不再算缺口
        var implemented = Source(contentRule: new ContentRule { Content = "<js>java.getString('x')</js>" });
        Assert.False(BookSourceCapabilities.Assess(implemented).HasFlag(BookSourceCapability.RequiresJsEngine));

        // 阶段 4-3：网络原语已实现 → 不再算缺口
        var network = Source(contentRule: new ContentRule { Content = "<js>java.ajax('x')</js>" });
        Assert.False(BookSourceCapabilities.Assess(network).HasFlag(BookSourceCapability.RequiresJsEngine));

        // 纯 URL 模板里出现 "java." 字样（真实样例常见）→ 不误报
        var urlish = Source(tocRule: new TocRule { ChapterUrl = "https://host/java.lang/book/{{id}}" });
        Assert.False(BookSourceCapabilities.Assess(urlish).HasFlag(BookSourceCapability.RequiresJsEngine));
    }

    [Fact]
    public void Capabilities_Detects_WebView_And_Login_And_RemoteJsLib()
    {
        var web = Source(tocRule: new TocRule { ChapterList = "@webjs:window.eval('x')" });
        Assert.True(BookSourceCapabilities.Assess(web).HasFlag(BookSourceCapability.RequiresWebView));

        var login = Source();
        login.LoginUrl = "https://api.example.com/login";
        var caps = BookSourceCapabilities.Assess(login);
        Assert.True(caps.HasFlag(BookSourceCapability.RequiresLogin));
        Assert.False(BookSourceCapabilities.IsFullySupported(login));

        var remote = Source(jsLib: """{"a":"https://cdn.example.com/x.js"}""");
        Assert.True(BookSourceCapabilities.Assess(remote).HasFlag(BookSourceCapability.RequiresRemoteJsLib));
        Assert.True(BookSourceCapabilities.Assess(remote).HasFlag(BookSourceCapability.HasJsLib));
    }

    [Fact]
    public void Capabilities_Inline_JsLib_Stays_Fully_Supported()
    {
        // 内联 jsLib 已由阶段 5 注入 → 只有信息标记，仍算完整支持
        var source = Source(jsLib: "function f(s){ return s; }");
        var caps = BookSourceCapabilities.Assess(source);

        Assert.Equal(BookSourceCapability.HasJsLib, caps);
        Assert.True(BookSourceCapabilities.IsFullySupported(source));
    }

    [Fact]
    public void Capabilities_Assessment_Is_Consistent_For_Same_Source()
    {
        var source = Source(tocRule: new TocRule { ChapterList = "<js>java.log('x')</js>" });
        var first = BookSourceCapabilities.Assess(source);

        // 并发重复评估：反射属性缓存是并发字典，结果必须完全一致
        var results = new System.Collections.Concurrent.ConcurrentBag<BookSourceCapability>();
        Parallel.For(0, 64, _ => results.Add(BookSourceCapabilities.Assess(source)));

        Assert.Equal(64, results.Count);
        Assert.All(results, r => Assert.Equal(first, r));
    }

    [Fact]
    public async Task Capabilities_Real_Sample_Slice_Matches_Static_Analysis()
    {
        // 对 Sample 真实样例做一次抽样评估，钉住"评估器不抛异常 + 分类可复现"
        Assert.True(File.Exists(SampleData.FilePath), "Sample 文件缺失");

        var firstPass = new Dictionary<string, BookSourceCapability>(StringComparer.Ordinal);
        var sources = new List<BookSourceModel>();
        await foreach (var s in SampleData.EnumerateAsync(60))
        {
            if (!string.IsNullOrEmpty(s.BookSourceUrl))
            {
                sources.Add(s);
                firstPass[s.BookSourceUrl] = BookSourceCapabilities.Assess(s);
            }
        }

        Assert.NotEmpty(sources);
        foreach (var s in sources)
        {
            Assert.Equal(firstPass[s.BookSourceUrl], BookSourceCapabilities.Assess(s));
        }

        // 至少有一些源需要阶段 4（样例中约三成）——若为 0 说明评估器失灵
        Assert.Contains(sources, s =>
            BookSourceCapabilities.Assess(s).HasFlag(BookSourceCapability.RequiresJsEngine));
    }

    // ============================================================
    // ③ 阅读进度存储
    // ============================================================

    [Fact]
    public void ReadingStore_Upsert_Get_Remove_Roundtrip()
    {
        var path = TempPath();
        try
        {
            var store = new BookSourceReadingStore(path);
            store.Upsert(Pos("https://s.example", "https://s.example/b/1", "书名甲", 7, "第七章"));
            store.Upsert(Pos("https://s.example", "https://s.example/b/2", "书名乙", 0, "第一章"));

            Assert.Equal(2, store.Count);
            Assert.Equal(7, store.Get("https://s.example", "https://s.example/b/1")!.ChapterIndex);
            Assert.Equal("第七章", store.Get("https://s.example", "https://s.example/b/1")!.ChapterTitle);
            Assert.Null(store.Get("https://s.example", "https://s.example/nope"));

            // 同键覆盖 = 最近一次进度
            store.Upsert(Pos("https://s.example", "https://s.example/b/1", "书名甲", 8, "第八章"));
            Assert.Equal(8, store.Get("https://s.example", "https://s.example/b/1")!.ChapterIndex);
            Assert.Equal(2, store.Count);

            Assert.True(store.Remove("https://s.example", "https://s.example/b/2"));
            Assert.False(store.Remove("https://s.example", "https://s.example/b/2"));
            Assert.Equal(1, store.Count);
        }
        finally
        {
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task ReadingStore_Save_Load_Roundtrip_Preserves_Fields()
    {
        var path = TempPath();
        try
        {
            var store = new BookSourceReadingStore(path);
            store.Upsert(Pos("https://s.example", "https://s.example/b/1", "书名甲", 12, "第十三章", author: "作者甲"));
            store.Upsert(Pos("https://s2.example", "https://s2.example/b/9", "书名乙", 3, "第四章", author: "作者乙"));
            await store.SaveAsync();

            var reloaded = new BookSourceReadingStore(path);
            await reloaded.LoadAsync();

            Assert.Equal(2, reloaded.Count);
            var p = reloaded.Get("https://s.example", "https://s.example/b/1");
            Assert.NotNull(p);
            Assert.Equal("书名甲", p!.BookName);
            Assert.Equal("作者甲", p.Author);
            Assert.Equal(12, p.ChapterIndex);
            Assert.Equal("第十三章", p.ChapterTitle);
            Assert.True(p.LastReadTicks > 0);
            Assert.Equal(DateTimeKind.Utc, p.LastReadUtc.Kind);

            // 同一本书在不同书源下互不干扰
            Assert.NotNull(reloaded.Get("https://s2.example", "https://s2.example/b/9"));
        }
        finally
        {
            SafeDelete(path);
            SafeDelete(path + ".tmp");
        }
    }

    [Fact]
    public async Task ReadingStore_Missing_Or_Corrupt_File_Loads_Empty()
    {
        var missing = new BookSourceReadingStore(TempPath());
        await missing.LoadAsync();
        Assert.Equal(0, missing.Count);

        var corruptPath = TempPath();
        try
        {
            await File.WriteAllTextAsync(corruptPath, "{ 这不是合法 JSON 数组 ");
            var store = new BookSourceReadingStore(corruptPath);
            await store.LoadAsync();
            Assert.Equal(0, store.Count);

            // 损坏后仍可正常写入（覆盖为合法文件）
            store.Upsert(Pos("https://s.example", "https://s.example/b/1", "甲", 1, "第一章"));
            await store.SaveAsync();
            Assert.True(File.Exists(corruptPath));
        }
        finally
        {
            SafeDelete(corruptPath);
        }
    }

    [Fact]
    public async Task ReadingStore_Concurrent_Reads_Writes_Are_Safe()
    {
        var path = TempPath();
        try
        {
            var store = new BookSourceReadingStore(path);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, 200),
                async (i, ct) =>
                {
                    store.Upsert(Pos("https://s.example", $"https://s.example/b/{i}", $"书{i}", i, $"第{i}章"));
                    // 读路径同时走：快照 + O(1) 取值
                    _ = store.Positions;
                    _ = store.Get("https://s.example", $"https://s.example/b/{i}");
                    await Task.Yield();
                });

            // 写者还在跑时用下界断言（教训 13）
            Assert.True(store.Count >= 150);
            Assert.True(store.Positions.Count >= 150);

            await store.SaveAsync();
            var reloaded = new BookSourceReadingStore(path);
            await reloaded.LoadAsync();
            Assert.True(reloaded.Count >= 150);
        }
        finally
        {
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task ReadingStore_Touch_Refreshes_Timestamp()
    {
        var position = Pos("https://s.example", "https://s.example/b/1", "甲", 1, "第一章") with { LastReadTicks = 1 };
        var touched = position.Touch();

        Assert.Equal(1, position.LastReadTicks);   // 原记录不可变
        Assert.True(touched.LastReadTicks > 1);
        Assert.Equal(1, touched.ChapterIndex);    // 其余字段不变
        Assert.Equal("甲", touched.BookName);
    }

    // ============================================================
    // 辅助
    // ============================================================

    private static BookSourceModel Source(
        string? jsLib = null,
        TocRule? tocRule = null,
        ContentRule? contentRule = null)
    {
        return new BookSourceModel
        {
            BookSourceUrl = "https://api.example.com",
            BookSourceName = "测试源",
            JsLib = jsLib,
            RuleToc = tocRule,
            RuleContent = contentRule,
        };
    }

    private static ReadingPosition Pos(
        string sourceUrl, string bookUrl, string name, int index, string chapter, string? author = null)
    {
        return new ReadingPosition
        {
            BookSourceUrl = sourceUrl,
            BookUrl = bookUrl,
            BookName = name,
            Author = author ?? string.Empty,
            ChapterIndex = index,
            ChapterTitle = chapter,
            LastReadTicks = DateTime.UtcNow.Ticks,
        };
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"uv_reading_{Guid.NewGuid():N}.json");

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
}
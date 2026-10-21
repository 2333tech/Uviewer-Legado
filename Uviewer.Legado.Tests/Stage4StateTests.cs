using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Models = Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-2：全局对象（<c>source</c>/<c>book</c>/<c>chapter</c>/<c>cache</c>/<c>cookie</c>/<c>html</c>）
/// + <see cref="SourceStateStore"/> 状态持久化。
///
/// <para><b>验收口径（方案 §4 的 4-2）</b>：</para>
/// <list type="number">
/// <item><c>SourceStateStoreTests</c> 语义：原子写、并发、损坏文件容错；</item>
/// <item><c>java.put</c> 跨链可见（目录链写、正文链读）；</item>
/// <item><c>book.name</c> 在正文链可用（用原始条目里的 book.* 源做离线 fixture）。</item>
/// </list>
///
/// <para>原始条目一律通过 <see cref="SampleData.RequireAsync"/> 从
/// <c>Sample/bookSource_202610192010.json</c> 直读，不碰导入后的 <c>booksources.json</c>。</para>
/// </summary>
public class Stage4StateTests : IDisposable
{
    private readonly string _dir;

    public Stage4StateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uviewer-state-tests", Guid.NewGuid().ToString("N"));
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
            // 临时目录清理失败不影响断言（Windows 上偶发占用）
        }
    }

    // ============================================================
    // ① SourceStateStore：落盘 / 原子写 / 并发 / 损坏容错
    // ============================================================

    [Fact]
    public void Store_Persists_And_Reloads_From_Disk()
    {
        var store = new SourceStateStore(_dir);
        Assert.True(store.IsPersistent);

        Assert.True(store.Set("https://a.com/", "k1", "v1"));
        Assert.Equal("v1", store.Get("https://a.com", "k1")); // 归一化：尾斜杠不影响

        var path = store.FilePathOf("https://a.com");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp")); // 原子写不留临时文件

        // 新实例（等价"重启进程"）能读回同一份状态
        var reloaded = new SourceStateStore(_dir);
        Assert.Equal("v1", reloaded.Get("https://a.com", "k1"));
        Assert.Equal(1, reloaded.Count("https://a.com"));
    }

    [Fact]
    public void Store_Writes_Valid_Sorted_Json_Without_Bom()
    {
        var store = new SourceStateStore(_dir);
        store.Set("src", "b", "2");
        store.Set("src", "a", "1");

        var bytes = File.ReadAllBytes(store.FilePathOf("src"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("src", doc.RootElement.GetProperty("source").GetString());
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal("1", values.GetProperty("a").GetString());
        // 排序输出：a 在 b 之前（同一份状态两次写入字节一致，便于 diff）
        var names = values.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "a", "b" }, names);
    }

    [Fact]
    public void Store_Concurrent_Writes_Are_All_Kept()
    {
        var store = new SourceStateStore(_dir);
        var source = "https://concurrent.example.com";

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 20; i++)
            {
                store.Set(source, $"w{worker}-{i}", $"v{i}");
            }
        });

        Assert.Equal(160, store.Count(source));
        Assert.Equal("v0", store.Get(source, "w7-0"));

        // 并发写完后文件仍是合法 JSON（原子替换 + 同源串行化）
        var text = File.ReadAllText(store.FilePathOf(source));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(160, doc.RootElement.GetProperty("values").EnumerateObject().Count());
    }

    [Fact]
    public void Store_Tolerates_Corrupt_File()
    {
        var store = new SourceStateStore(_dir);
        var path = store.FilePathOf("https://broken.example.com");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json ");

        var reopened = new SourceStateStore(_dir);
        Assert.Null(reopened.Get("https://broken.example.com", "k"));
        Assert.False(string.IsNullOrEmpty(reopened.LastLoadError("https://broken.example.com")));

        // 损坏文件被隔离为 .corrupt（保留现场），写入立刻重建可用文件
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.True(reopened.Set("https://broken.example.com", "k", "v"));
        Assert.Equal("v", new SourceStateStore(_dir).Get("https://broken.example.com", "k"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("v", doc.RootElement.GetProperty("values").GetProperty("k").GetString());
    }

    [Fact]
    public void Store_Memory_Only_Does_Not_Touch_Disk()
    {
        var store = new SourceStateStore();
        Assert.False(store.IsPersistent);
        Assert.Equal(string.Empty, store.FilePathOf("anything"));

        Assert.True(store.Set("src", "k", "v"));
        Assert.Equal("v", store.Get("src", "k"));

        // 两个内存实例互相隔离（不共享全局状态）
        Assert.Null(new SourceStateStore().Get("src", "k"));
    }

    [Fact]
    public void Store_Enforces_Size_Limits()
    {
        var store = new SourceStateStore(_dir) { MaxKeysPerSource = 2, MaxValueLength = 4 };
        Assert.True(store.Set("s", "a", "1234"));
        Assert.False(store.Set("s", "b", "12345")); // 超长值被拒
        Assert.Equal(1, store.Count("s"));

        Assert.True(store.Set("s", "b", "1"));
        Assert.True(store.Set("s", "c", "1") is false); // 第 3 个键超上限被拒
        Assert.Equal(2, store.Count("s"));

        // 覆盖已有键不受键数上限影响
        Assert.True(store.Set("s", "a", "9"));
        Assert.Equal("9", store.Get("s", "a"));
    }

    [Fact]
    public void Store_Remove_Clear_And_Memory_Cache()
    {
        var store = new SourceStateStore(_dir);
        store.Set("src", "k", "v");
        Assert.True(store.Remove("src", "k"));
        Assert.Null(store.Get("src", "k"));

        store.Set("src", "k2", "v2");
        store.Clear("src");
        Assert.Equal(0, store.Count("src"));
        Assert.False(File.Exists(store.FilePathOf("src")));

        // 内存缓存（cache.*Memory）：不落盘
        store.SetMemory("src", "m", "{\"a\":1}");
        Assert.Equal("{\"a\":1}", store.GetMemory("src", "m"));
        store.RemoveMemory("src", "m");
        Assert.Null(store.GetMemory("src", "m"));
        store.SetMemory("src", "m2", "1");
        store.ClearMemory();
        Assert.Null(store.GetMemory("src", "m2"));
    }

    // ============================================================
    // ② JsScope：键命名空间
    // ============================================================

    [Fact]
    public void Scope_Key_Namespaces_Are_Stable()
    {
        var scope = new JsScope(
            "https://a.com/",
            book: new JsBookState { BookUrl = "https://a.com/book/1" },
            chapterUrl: "https://a.com/ch/9",
            store: new SourceStateStore());

        Assert.Equal("https://a.com/", scope.SourceKey);          // 原样（source.key 语义）
        Assert.Equal("https://a.com", scope.StorageKey);          // 归一化（存储键）
        Assert.Equal("sv:variable", scope.SourceMetaKey("variable"));
        Assert.Equal("sp:x", scope.SourcePutKey("x"));
        Assert.Equal("bv:https://a.com/book/1:x", scope.BookKey("x"));
        Assert.Equal("cv:https://a.com/ch/9:x", scope.ChapterKey("x"));
        Assert.Equal("c:x", scope.CacheKey("x"));
        Assert.Equal("file:x", scope.FileCacheKey("x"));
        Assert.Equal("ck:a.com", scope.CookieKey("a.com"));
        Assert.Equal("brc:https://a.com/book/1", scope.ReadConfigKey());
    }

    [Fact]
    public void Scope_State_Is_Shared_By_Normalized_Source_Key()
    {
        var store = new SourceStateStore();
        var a = new JsScope("https://a.com/", store: store);
        var b = new JsScope("https://a.com", store: store);

        Assert.True(a.Write(a.SourceMetaKey("variable"), "token"));
        Assert.Equal("token", b.Read(b.SourceMetaKey("variable")));
    }

    [Fact]
    public void BookState_FromBookInfo_And_WithChapter()
    {
        var info = new Models.BookInfo
        {
            Name = "书名",
            Author = "作者",
            Intro = "简介",
            Kind = "玄幻",
            CoverUrl = "https://a.com/c.jpg",
            TocUrl = "https://a.com/toc",
        };

        var book = JsBookState.FromBookInfo(info, "https://a.com/book/1", "兜底名", chapterCount: 12);
        Assert.Equal("书名", book.Name);
        Assert.Equal("作者", book.Author);
        Assert.Equal("https://a.com/book/1", book.BookUrl);
        Assert.Equal("https://a.com/toc", book.TocUrl);
        Assert.Equal(12, book.TotalChapterNum);

        var chained = book.WithChapter(3, "第三章");
        Assert.Equal(3, chained.DurChapterIndex);
        Assert.Equal("第三章", chained.DurChapterTitle);
        Assert.Equal(12, chained.TotalChapterNum); // 未传总章数时保留原值
        Assert.Equal(-1, JsBookState.FromBookUrl("https://a.com/b").TotalChapterNum);
    }

    // ============================================================
    // ③ java.put 跨链可见
    // ============================================================

    [Fact]
    public void Java_Put_In_Toc_Chain_Is_Visible_In_Content_Chain()
    {
        var store = new SourceStateStore();
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com/src",
            RuleToc = new Models.TocRule
            {
                // 目录链：把 bid 写进变量（legado 里落到书籍变量）
                ChapterList = "@js:java.put('bid','B7'); result=''",
            },
            RuleContent = new Models.ContentRule
            {
                // 正文链：读回来（章节变量未命中 → 书籍变量命中）
                Content = "@js:java.get('bid')",
            },
        };

        var book = new JsBookState { BookUrl = "https://example.com/book/1", Name = "书" };
        var engine = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };

        var (chapters, _) = engine.ParseTocPage(source, "<html></html>", "https://example.com/toc", book);
        Assert.Empty(chapters);

        var (text, _, _) = engine.ParseContentPage(
            source, "<p>x</p>", "https://example.com/ch/1", book, "https://example.com/ch/1");
        Assert.Equal("B7", text);

        // 落点口径：目录链没有章节 → 书籍变量（bv:）
        Assert.Equal("B7", store.Get("https://example.com/src", "bv:https://example.com/book/1:bid"));
    }

    [Fact]
    public void Java_Put_Cross_Chain_Works_Across_Engine_Instances()
    {
        // 两个引擎实例（模拟"这次解析用池里的 A，下次用池里的 B"）+ 同一个仓库：
        // 章节变量（cv:）在实例之间必须可见。
        var store = new SourceStateStore();
        var putSource = new BookSourceModel
        {
            BookSourceUrl = "https://example.com/src2",
            RuleContent = new Models.ContentRule { Content = "@js:java.put('token','T1'); result=''" },
        };
        var getSource = new BookSourceModel
        {
            BookSourceUrl = "https://example.com/src2",
            RuleContent = new Models.ContentRule { Content = "@js:java.get('token')" },
        };

        var writer = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };
        writer.ParseContentPage(putSource, "<p>x</p>", "https://example.com/c/1", null, "https://example.com/c/1");
        Assert.Equal("T1", store.Get("https://example.com/src2", "cv:https://example.com/c/1:token"));

        var reader = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };
        Assert.Equal("T1", reader.ParseContentPage(
            getSource, "<p>x</p>", "https://example.com/c/1", null, "https://example.com/c/1").Text);

        // 没有章节/书籍上下文时（搜索链）落书源 sp: 键
        Assert.True(store.Set("https://example.com/src2", "sp:only", "S"));
        Assert.Equal("S", store.Get("https://example.com/src2", "sp:only"));
    }

    [Fact]
    public void Java_Get_Falls_Back_Chapter_Then_Book_Then_Source()
    {
        var store = new SourceStateStore();
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com/src3",
            RuleContent = new Models.ContentRule
            {
                Content = "@js:[java.get('only-source'),java.get('only-book'),java.get('only-chapter'),java.get('bookName')].join('|')",
            },
        };

        var book = new JsBookState { BookUrl = "https://example.com/book/3", Name = "三号书" };
        var scopeKeys = new JsScope("https://example.com/src3", book: book, chapterUrl: "https://example.com/c/3");
        store.Set("https://example.com/src3", scopeKeys.SourcePutKey("only-source"), "S");
        store.Set("https://example.com/src3", scopeKeys.BookKey("only-book"), "B");
        store.Set("https://example.com/src3", scopeKeys.ChapterKey("only-chapter"), "C");

        var engine = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };
        var (text, _, _) = engine.ParseContentPage(
            source, "<p>x</p>", "https://example.com/c/3", book, "https://example.com/c/3");

        Assert.Equal("S|B|C|三号书", text);
    }

    // ============================================================
    // ④ book.name 在正文链可用（原始条目离线 fixture）
    // ============================================================

    [Fact]
    public async Task Book_Name_Is_Used_By_Raw_Source_Toc_Rule()
    {
        // 原始条目：百度网盘（优++）—— chapterList 脚本把 book.name 拼成章节标题：
        //   <js>'<a href="'+baseUrl+'">'+book.name+'</a>'</js>  + tag.a
        // 4-2 之前 book 未定义 → 脚本抛错 → JsBridge 回退原文 → 目录为空/错。
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        var book = new JsBookState { BookUrl = "https://pan.baidu.com/disk/home", Name = "测试书名" };

        var engine = new RuleEngine { Store = new SourceStateStore(), Diagnostics = new JsDiagnostics() };
        var (chapters, next) = engine.ParseTocPage(source, "<html></html>", "https://pan.baidu.com", book);

        Assert.Empty(next);
        var chapter = Assert.Single(chapters);
        Assert.Equal("测试书名", chapter.ChapterName);
        Assert.Equal("https://pan.baidu.com", chapter.ChapterUrl);
    }

    [Fact]
    public async Task Book_Context_Is_Used_By_Raw_Source_Content_Rule()
    {
        // 原始条目：🎨 爱优漫 —— 正文脚本读 book.totalChapterNum 与 chapter.index 计算图片序号。
        // 用一份最小 JSON 响应离线复现（漫画章节表 3 条），断言"书名上下文 + 章节上下文"都生效。
        var source = await SampleData.RequireAsync("https://m.iyouman.com#Haxc1107");
        var body = """
            {"data":{"comic_chapter":[
              {"end_num":2,"rule":"/$$.jpg","chapter_domain":"a.example"},
              {"end_num":1,"rule":"/$$.jpg","chapter_domain":"b.example"},
              {"end_num":3,"rule":"/$$.jpg","chapter_domain":"c.example"}]}}
            """;

        var engine = new RuleEngine { Store = new SourceStateStore(), Diagnostics = new JsDiagnostics() };
        var book = new JsBookState
        {
            BookUrl = "https://m.iyouman.com/book/1",
            Name = "漫画书",
            TotalChapterNum = 3,
        }.WithChapter(2, "第3话");

        var (text, _, _) = engine.ParseContentPage(
            source, body, "https://m.iyouman.com/ch/3", book, "https://m.iyouman.com/ch/3");

        // index = totalChapterNum(3) - (chapter.index(2) + 1) = 0 → 第一条（c.example 才是第 1 条？）
        // 说明：脚本 index 从 1 起算再取反，故 book.totalChapterNum=3 / chapter.index=2 → 取 comic_chapter[0]。
        Assert.Contains("a.example", text);
        Assert.Contains("aym.middle.webp", text);
        Assert.DoesNotContain("c.example", text);
    }

    [Fact]
    public async Task Content_Chain_Exposes_Book_Name_And_Author()
    {
        // 原始样本里"正文规则直接读 book.name"的只有 2 条（一条依赖 WebView 刷新、一条用 {{}} 模板），
        // 都无法离线复现。这里用原始条目的书籍上下文 + 等价脚本文本验证同一契约：
        // 正文链的脚本能读到 book.name/book.author（此前是 ReferenceError → 整段回退原文）。
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.RuleContent = new Models.ContentRule { Content = "@js:book.name + '/' + book.author + '/' + book.bookUrl" };

        var book = new JsBookState
        {
            BookUrl = "https://pan.baidu.com/book/9",
            Name = "书名甲",
            Author = "作者乙",
        };

        var engine = new RuleEngine { Store = new SourceStateStore(), Diagnostics = new JsDiagnostics() };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/1", book, "https://pan.baidu.com/ch/1");

        Assert.Equal("书名甲/作者乙/https://pan.baidu.com/book/9", text);
    }

    // ============================================================
    // ⑤ source / chapter / cache / cookie / html 对象
    // ============================================================

    [Fact]
    public async Task Source_Object_Exposes_Identity_Variables_And_LoginInfo()
    {
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        var script = """
            @js:
            source.putVariable('token-1');
            var v1 = source.getVariable();
            source.put('spk','spv');
            source.putLoginInfo(JSON.stringify({'源作者':'甲','设备':'乙'}));
            var info = source.getLoginInfoMap();
            source.putLoginHeader(JSON.stringify({'Cookie':'sid=9; u=8'}));
            [source.getKey(), source.key, source.bookSourceName, source.bookSourceUrl,
             v1, source.get('spk'), info.get('源作者'), info['设备'],
             cookie.getKey(source.getKey(),'sid'), cookie.getCookie(source.getKey())].join('|')
            """;
        source.RuleContent = new Models.ContentRule { Content = script };

        var engine = new RuleEngine { Store = new SourceStateStore(), Diagnostics = new JsDiagnostics() };
        var (text, _, _) = engine.ParseContentPage(
            source,
            "<html></html>",
            "https://pan.baidu.com/ch/1",
            new JsBookState { BookUrl = "https://pan.baidu.com/book/1" },
            "https://pan.baidu.com/ch/1");
        Assert.Equal("https://pan.baidu.com|https://pan.baidu.com|百度网盘（优++）|https://pan.baidu.com|token-1|spv|甲|乙|9|sid=9; u=8", text);

        // 变量确实落到书源级键（sv:variable）
        Assert.Equal("token-1", engine.Store.Get("https://pan.baidu.com", "sv:variable"));
        // putLoginHeader 里的 Cookie 同步进 Cookie 仓库（显式设置，不依赖脚本内部状态）
        var store = new SourceStateStore();
        var scope = new JsScope("https://pan.baidu.com", store: store);
        Assert.Equal(string.Empty, JsCookieJar.GetCookie(scope, "https://pan.baidu.com"));
        store.Set("https://pan.baidu.com", scope.SourceMetaKey("variable"), "token-1");
        Assert.Equal("token-1", store.Get("https://pan.baidu.com", "sv:variable"));
    }

    [Fact]
    public async Task Chapter_Object_Exposes_Current_Chapter()
    {
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.RuleContent = new Models.ContentRule
        {
            Content = "@js:chapter.title + '|' + chapter.index + '|' + chapter.url + '|' + chapter.getKey()",
        };

        var book = new JsBookState { BookUrl = "https://pan.baidu.com/book/1" }.WithChapter(4, "第四章");
        var engine = new RuleEngine { Store = new SourceStateStore(), Diagnostics = new JsDiagnostics() };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/4", book, "https://pan.baidu.com/ch/4");

        Assert.Equal("第四章|4|https://pan.baidu.com/ch/4|https://pan.baidu.com/ch/4", text);
    }

    [Fact]
    public async Task Cache_Object_Persists_And_Memory_Variant_Parses_Json()
    {
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.RuleContent = new Models.ContentRule
        {
            Content = """
                @js:
                cache.put('k1','v1');
                cache.putFile('f1','fv');
                cache.putMemory('m1',{'a':1,'b':'x'});
                var mem = cache.getFromMemory('m1');
                [cache.get('k1'), cache.getFile('f1'), mem.a, mem.b, (cache.get('missing') === null)].join('|')
                """,
        };

        var store = new SourceStateStore();
        var engine = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/1", null, "https://pan.baidu.com/ch/1");

        Assert.Equal("v1|fv|1|x|true", text);
        Assert.Equal("v1", store.Get("https://pan.baidu.com", "c:k1"));
        Assert.Equal("fv", store.Get("https://pan.baidu.com", "file:f1"));

        // delete / deleteMemory / deleteFile
        source.RuleContent = new Models.ContentRule
        {
            Content = "@js:cache.delete('k1'); cache.deleteFile('f1'); cache.deleteMemory('m1');" +
                      "[cache.get('k1'),cache.getFile('f1'),cache.getFromMemory('m1')].join('|')",
        };
        var (afterDelete, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/1", null, "https://pan.baidu.com/ch/1");
        Assert.Equal("||", afterDelete);
    }

    [Fact]
    public async Task Cookie_Object_Handles_Subdomains_Replace_And_Remove()
    {
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        var script = """
            @js:
            cookie.setCookie('https://www.example.com/a','sid=1; u=2');
            var v1 = cookie.getKey('https://m.example.com/b','sid');
            cookie.replaceCookie('https://www.example.com/a','u=9; x=3');
            var v2 = cookie.getCookie('https://example.com');
            cookie.removeCookie('https://example.com');
            [v1, v2, cookie.getCookie('https://example.com')].join('|')
            """;
        source.RuleContent = new Models.ContentRule { Content = script };

        var store = new SourceStateStore();
        var engine = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/1", null, "https://pan.baidu.com/ch/1");

        Assert.Equal("1|sid=1; u=9; x=3|", text);

        // 域归并：近似二级域名（跨子域可读）+ 常见二级后缀
        Assert.Equal("example.com", JsCookieJar.DomainOf("https://www.example.com/a"));
        Assert.Equal("example.com", JsCookieJar.DomainOf("https://m.example.com"));
        Assert.Equal("foo.com.cn", JsCookieJar.DomainOf("https://a.foo.com.cn/x"));
        Assert.Equal("localhost", JsCookieJar.DomainOf("http://localhost:8080/x"));
        Assert.Equal("not a url", JsCookieJar.DomainOf("  not a url  "));
    }

    [Fact]
    public async Task Unknown_Members_Record_Diagnostics_Instead_Of_Throwing()
    {
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.RuleContent = new Models.ContentRule
        {
            // 注意：Proxy 兜底是"访问到的未知成员返回一个函数，调用它才记诊断"，
            // 故这里必须真的调用（typeof book.nopeFn 只访问不调用，不会记录）。
            Content = "@js:[book.nopeFn(), source.whatever(), html.match('x'), chapter.nothing()].join('|')",
        };

        var diagnostics = new JsDiagnostics();
        var engine = new RuleEngine { Store = new SourceStateStore(), Diagnostics = diagnostics };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/1",
            new JsBookState { BookUrl = "https://pan.baidu.com/book/1" }, "https://pan.baidu.com/ch/1");

        // Proxy 兜底：脚本跑完（不因 TypeError 整段回退原文）
        Assert.Equal("|||", text);

        var missing = diagnostics.MissingApisOf("https://pan.baidu.com");
        Assert.Contains("book.nopeFn", missing);
        Assert.Contains("source.whatever", missing);
        Assert.Contains("html.match", missing);
        Assert.Contains("chapter.nothing", missing);
    }

    [Fact]
    public async Task Book_ReadConfig_And_ReplaceSwitch_Round_Trip()
    {
        var source = await SampleData.RequireAsync("https://pan.baidu.com");
        source.RuleContent = new Models.ContentRule
        {
            Content = "@js:book.setUseReplaceRule(false); [book.getUseReplaceRule(), book.readConfig.useReplaceRule].join('|')",
        };

        var store = new SourceStateStore();
        var engine = new RuleEngine { Store = store, Diagnostics = new JsDiagnostics() };
        var book = new JsBookState { BookUrl = "https://pan.baidu.com/book/7" };
        var (text, _, _) = engine.ParseContentPage(
            source, "<html></html>", "https://pan.baidu.com/ch/1", book, "https://pan.baidu.com/ch/1");

        Assert.Equal("false|false", text);
        Assert.Equal("false", store.Get("https://pan.baidu.com", "brc:https://pan.baidu.com/book/7"));
    }

    [Fact]
    public void Stub_Members_Are_Recorded_Not_Silently_Successful()
    {
        var scope = new JsScope("https://example.com", book: new JsBookState { BookUrl = "https://example.com/b" });
        var diagnostics = new JsDiagnostics();
        var bridge = new JsBridge
        {
            Scope = scope,
            Diagnostics = diagnostics,
            SourceKey = scope.SourceKey,
            Field = "toc",
        };

        Assert.Equal("true|true", bridge.Run("source.refreshExplore() + '|' + book.setReverseToc('yes')", "x", "https://example.com"));
        var missing = diagnostics.MissingApisOf("https://example.com");
        Assert.Contains("source.refreshExplore", missing);
        Assert.Contains("book.setReverseToc", missing);

        // 解绑后旧作用域的对象不能残留（否则下一次无作用域调用会读到上一本书）
        bridge.Scope = null;
        Assert.Equal("undefined", bridge.Run("typeof source", "x", "https://example.com"));
    }

    // ============================================================
    // ⑥ 能力口径：全局对象不再算缺口
    // ============================================================

    [Fact]
    public void Globals_Are_Not_Capability_Gaps_After_Stage42()
    {
        Assert.Empty(BookSourceCapabilities.AssessDetailed(new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            RuleContent = new Models.ContentRule { Content = "@js:book.name + source.key + cookie.getKey('u','k') + cache.get('x')" },
        }).MissingGlobals);

        // html 不参与判定（样本里的 html.* 全是局部变量）；它也不是"已实现"
        Assert.False(JsGlobalCatalog.IsImplemented("html"));
        Assert.False(JsGlobalCatalog.IsGated("html"));
        Assert.True(JsGlobalCatalog.IsImplemented("book"));
        Assert.True(JsGlobalCatalog.IsGated("book"));
        Assert.Empty(BookSourceCapabilities.AssessDetailed(new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            RuleContent = new Models.ContentRule { Content = "@js:html.match('x')" },
        }).MissingGlobals);
    }
}

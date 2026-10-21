using System.Collections.Concurrent;
using System.Linq;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 2（详情 / 目录 / 正文 / 图片）验收。
/// 规则结构取材于 Sample/ 真实书源（如 api-bc.wtzw.com「阅读助手」的 data.book / data.chapter_lists 形态），
/// 内容用轻量 fixture，不依赖网络与 java.ajax（真实源多为加密 JS，离线无法执行）。
/// </summary>
public class Phase2ContentTests
{
    // ---------- 测试用静态取数 ----------

    /// <summary>同步回调的 IProgress（测试用，避免 Progress&lt;T&gt; 的异步编组）。</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _on;

        public SyncProgress(Action<T> on) => _on = on;

        public void Report(T value) => _on(value);
    }

    private sealed class FixtureFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map;
        public ConcurrentBag<string> Requests { get; } = new();

        public FixtureFetcher(Dictionary<string, string> map) => _map = map;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(url);
            var hit = _map.FirstOrDefault(kv => url.Contains(kv.Key, StringComparison.Ordinal));
            return Task.FromResult((hit.Value ?? string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(url);
            return Task.FromResult<byte[]?>(null);
        }
    }

    // ---------- 真实书源形态的 JSON 型书源 ----------

    private static BookSourceModel MakeJsonSource() => new()
    {
        BookSourceUrl = "https://api-bc.wtzw.com",
        BookSourceName = "阅读助手（测试）",
        RuleSearch = new SearchRule { BookList = "data.books[*]", Name = "title", BookUrl = "id" },
        RuleBookInfo = new BookInfoRule
        {
            Init = "data.book",
            Name = "title",
            Author = "author",
            CoverUrl = "image_link",
            Intro = "intro",
            LastChapter = "latest_chapter_title",
            TocUrl = "toc_url",
        },
        RuleToc = new TocRule
        {
            ChapterList = "data.chapter_lists",
            ChapterName = "title",
            ChapterUrl = "id",
            UpdateTime = "{{$.words}} 字",
        },
        RuleContent = new ContentRule
        {
            Content = "data.content",
            Title = "data.title",
            NextContentUrl = "data.next",
        },
    };

    private const string BookInfoJson = """
    {"data":{"book":{"id":"1001","title":"测试书名","author":"作者甲","image_link":"/img/1001.jpg",
      "intro":"这是一段简介。","latest_chapter_title":"最后一章","toc_url":"/toc/1001.html"}}}
    """;

    private const string TocJson = """
    {"data":{"chapter_lists":[
      {"id":"/ch/1","title":"第一章","words":2100},
      {"id":"/ch/2","title":"第二章","words":1800},
      {"id":"/ch/1","title":"第一章（重复应被去重）","words":2100}
    ]}}
    """;

    private const string ContentJson = """
    {"data":{"title":"第一章","content":"<p>正文第一段</p><p>正文第二段</p>","next":null}}
    """;

    // ---- 1. 详情（JSON 源）----

    [Fact]
    public async Task BookInfo_Json_Source_Extracts_All_Fields()
    {
        var source = MakeJsonSource();
        var engine = new RuleEngine();
        var info = engine.ParseBookInfo(source, BookInfoJson, "https://api-bc.wtzw.com/book/1001.html", "https://api-bc.wtzw.com/book/1001.html")!;

        Assert.NotNull(info);
        // init = data.book → 作用域下移到 data.book
        Assert.Equal("测试书名", info.Name);
        Assert.Equal("作者甲", info.Author);
        Assert.Equal("这是一段简介。", info.Intro);
        Assert.Equal("最后一章", info.LastChapter);
        // 相对地址被绝对化（reader resolve_url）
        Assert.Equal("https://api-bc.wtzw.com/img/1001.jpg", info.CoverUrl);
        Assert.Equal("https://api-bc.wtzw.com/toc/1001.html", info.TocUrl);
    }

    [Fact]
    public void BookInfo_TocUrl_Empty_Falls_Back_To_BookUrl()
    {
        var source = MakeJsonSource();
        source.RuleBookInfo = new BookInfoRule { Init = "data.book", Name = "title" };
        var info = new RuleEngine().ParseBookInfo(
            source, BookInfoJson, "https://api-bc.wtzw.com", "https://api-bc.wtzw.com/book/1001.html")!;

        // reader：tocUrl 为空 → 用详情页 URL 当目录页
        Assert.Equal("https://api-bc.wtzw.com/book/1001.html", info.TocUrl);
        Assert.Equal("https://api-bc.wtzw.com/book/1001.html", info.BookUrl);
    }

    [Fact]
    public async Task BookInfo_Name_Falls_Back_To_ShelfName_Then_Url()
    {
        var source = MakeJsonSource();
        source.RuleBookInfo = new BookInfoRule { Init = "data.book" }; // 无 name 规则
        var fetcher = new FixtureFetcher(new Dictionary<string, string> { ["wtzw.com"] = BookInfoJson });
        var engine = new BookSourceEngine(fetcher);

        var withShelf = await engine.GetBookInfoAsync(
            source, "https://api-bc.wtzw.com/book/1001.html", "书架里的名字");
        Assert.Equal("书架里的名字", withShelf!.Name);

        var noShelf = await engine.GetBookInfoAsync(source, "https://api-bc.wtzw.com/book/1001.html");
        // URL 末段兜底（reader book_service:772）
        Assert.Equal("1001.html", noShelf!.Name);
    }

    // ---- 2. 详情（HTML 源 + AllInOne init）----

    [Fact]
    public void BookInfo_Html_With_Init_AllInOne()
    {
        const string html = """
        <html><body>
          <div class="wrap">
            <h1 class="name">HTML 书名</h1>
            <span class="author">作者乙</span>
            <div class="intro">HTML 简介</div>
            <img class="cover" src="/c/1.jpg"/>
            <a class="toc" href="/toc/1">目录</a>
          </div>
        </body></html>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleBookInfo = new BookInfoRule
            {
                Init = "@css:.wrap", // AllInOne：先取容器，后续字段在该容器内求值
                Name = "@css:.name@text",
                Author = "@css:.author@text",
                Intro = "@css:.intro@text",
                CoverUrl = "@css:.cover@src",
                TocUrl = "@css:.toc@href",
            },
        };

        var info = new RuleEngine().ParseBookInfo(source, html, "https://h.example.com/book/1.html", "https://h.example.com/book/1.html")!;

        Assert.Equal("HTML 书名", info.Name);
        Assert.Equal("作者乙", info.Author);
        Assert.Equal("HTML 简介", info.Intro);
        Assert.Equal("https://h.example.com/c/1.jpg", info.CoverUrl);
        Assert.Equal("https://h.example.com/toc/1", info.TocUrl);
    }

    // ---- 3. 目录 ----

    [Fact]
    public void Toc_Json_Parses_And_Dedupes_By_Absolute_Url()
    {
        var source = MakeJsonSource();
        var (chapters, _) = new RuleEngine().ParseTocPage(source, TocJson, "https://api-bc.wtzw.com");

        // 第三条与第一条例 URL 相同 → 被去重（reader 按绝对 URL 去重）
        Assert.Equal(2, chapters.Count);
        Assert.Equal("第一章", chapters[0].ChapterName);
        Assert.Equal("https://api-bc.wtzw.com/ch/1", chapters[0].ChapterUrl);
        Assert.Equal("2100 字", chapters[0].UpdateTime);
        Assert.Equal("第二章", chapters[1].ChapterName);
    }

    [Fact]
    public void Toc_Html_Parses_Volumes_And_Pseudo_Url()
    {
        const string html = """
        <div class="list">
          <li><a href="/c/1.html">第一章</a><span class="vip">1</span></li>
          <li class="vol"><span>第一卷 起始</span></li>
          <li><a href="/c/2.html">第二章</a></li>
        </div>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleToc = new TocRule
            {
                ChapterList = "@css:.list>li",
                ChapterName = "@css:a@text||@css:span@text",
                ChapterUrl = "a@href",
                IsVolume = "@css:li@class",
                IsVip = "@css:.vip@text",
            },
        };

        var (chapters, _) = new RuleEngine().ParseTocPage(source, html, "https://h.example.com/toc/1.html");

        Assert.Equal(3, chapters.Count);
        Assert.Equal("第一章", chapters[0].ChapterName);
        Assert.Equal("https://h.example.com/c/1.html", chapters[0].ChapterUrl);
        Assert.True(chapters[0].IsVip);
        // 卷无链接 → title+index 伪 URL（reader finalize_chapter_url）
        Assert.True(chapters[1].IsVolume);
        Assert.Equal("第一卷 起始1", chapters[1].ChapterUrl);
    }

    [Fact]
    public void Toc_NextUrls_Are_Absolutized()
    {
        const string html = """
        <div class="pager"><a href="/toc/1_2.html">2</a><a href="/toc/1_3.html">3</a></div>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleToc = new TocRule
            {
                ChapterList = "@css:.list>li",
                ChapterName = "@css:a@text",
                NextTocUrl = "@css:.pager>a@href",
            },
        };

        var (_, next) = new RuleEngine().ParseTocPage(source, html, "https://h.example.com/toc/1.html");

        Assert.Equal(2, next.Count);
        Assert.Equal("https://h.example.com/toc/1_2.html", next[0]);
        Assert.Equal("https://h.example.com/toc/1_3.html", next[1]);
    }

    // ---- 4. 目录分页 ----

    [Fact]
    public async Task Toc_Pagination_Follows_Single_Chain_And_Dedupes()
    {
        var page1 = TocJson.Replace("\"next\":null", "") // 占位
            .Replace("]}", "]}");
        var source = MakeJsonSource();
        source.RuleToc = new TocRule
        {
            ChapterList = "data.chapter_lists",
            ChapterName = "title",
            ChapterUrl = "id",
            NextTocUrl = "data.next",
        };

        var fetcher = new FixtureFetcher(new Dictionary<string, string>
        {
            ["/toc/1"] = "{\"data\":{\"chapter_lists\":[{\"id\":\"/ch/1\",\"title\":\"第一章\"},{\"id\":\"/ch/2\",\"title\":\"第二章\"}],\"next\":\"/toc/2\"}}",
            ["/toc/2"] = "{\"data\":{\"chapter_lists\":[{\"id\":\"/ch/3\",\"title\":\"第三章\"},{\"id\":\"/ch/1\",\"title\":\"重复的第一章\"}]}}",
        });

        var engine = new BookSourceEngine(fetcher);
        var chapters = await engine.GetTocAsync(source, "https://api-bc.wtzw.com/toc/1");

        Assert.Equal(3, chapters.Count);
        Assert.Equal("第一章", chapters[0].ChapterName);
        Assert.Equal("第三章", chapters[2].ChapterName);
        // 全局序号连续
        Assert.Equal(0, chapters[0].Index);
        Assert.Equal(2, chapters[2].Index);
        Assert.Equal(2, fetcher.Requests.Count);
    }

    [Fact]
    public async Task Toc_Pagination_Fetches_Multiple_Urls_And_Keeps_Order()
    {
        var source = MakeJsonSource();
        source.RuleToc = new TocRule
        {
            ChapterList = "data.chapter_lists",
            ChapterName = "title",
            ChapterUrl = "id",
            NextTocUrl = "@json:$.data.nexts[*]",
        };

        var fetcher = new FixtureFetcher(new Dictionary<string, string>
        {
            ["/toc/1"] = "{\"data\":{\"chapter_lists\":[{\"id\":\"/ch/1\",\"title\":\"第一章\"}],\"nexts\":[\"/toc/2\",\"/toc/3\"]}}",
            ["/toc/2"] = "{\"data\":{\"chapter_lists\":[{\"id\":\"/ch/2\",\"title\":\"第二章\"}]}}",
            ["/toc/3"] = "{\"data\":{\"chapter_lists\":[{\"id\":\"/ch/3\",\"title\":\"第三章\"}]}}",
        });

        var engine = new BookSourceEngine(fetcher);
        var chapters = await engine.GetTocAsync(source, "https://api-bc.wtzw.com/toc/1");

        // 多 URL 并行抓取后按 URL 顺序合并（结果与 reader 的串行 for 一致）
        Assert.Equal(3, chapters.Count);
        Assert.Equal(new[] { "第一章", "第二章", "第三章" }, chapters.Select(c => c.ChapterName));
        Assert.Equal(3, fetcher.Requests.Count);
    }

    // ---- 5. 正文 ----

    [Fact]
    public void Content_Parses_Text_Title_And_Null_Next()
    {
        var source = MakeJsonSource();
        var (text, title, next) = new RuleEngine().ParseContentPage(source, ContentJson, "https://api-bc.wtzw.com/ch/1");

        Assert.Equal("<p>正文第一段</p><p>正文第二段</p>", text);
        Assert.Equal("第一章", title);
        Assert.Null(next);
    }

    [Fact]
    public void Content_Empty_String_Rule_Returns_Whole_Body_While_Missing_Rule_Returns_Empty()
    {
        var source = MakeJsonSource();

        // 显式空串（JSON 里 "content": ""）：reader 走 Some("") 分支 → 整个原文作为正文
        source.RuleContent = new ContentRule { Content = string.Empty, Title = "data.title" };
        var (whole, _, _) = new RuleEngine().ParseContentPage(source, ContentJson, "https://api-bc.wtzw.com/ch/1");
        Assert.Equal(ContentJson.Replace("\r\n", "\n"), whole.Replace("\r\n", "\n"));

        // 完全缺失（null）：reader 走 None 分支 → 返回空串
        source.RuleContent = new ContentRule { Title = "data.title" };
        var (empty, _, _) = new RuleEngine().ParseContentPage(source, ContentJson, "https://api-bc.wtzw.com/ch/1");
        Assert.Equal(string.Empty, empty);
    }

    [Fact]
    public void Content_SourceRegex_And_ReplaceRegex_Are_Applied()
    {
        const string html = "<html><body><div id=\"content\"><p>第一段</p><script>bad()</script></div><div id=\"ad\">广告</div></body></html>";
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleContent = new ContentRule
            {
                SourceRegex = "##<div id=\"content\">([\\s\\S]*?)</div>##$1", // 先裁剪出正文块
                Content = "@css:p@text",
                ReplaceRegex = "##广告##",
            },
        };

        var (text, _, _) = new RuleEngine().ParseContentPage(source, html, "https://h.example.com/ch/1");
        Assert.Equal("第一段", text);
    }

    [Fact]
    public async Task Content_Pagination_Follows_Next_Page_And_Stops_At_Next_Chapter()
    {
        var source = MakeJsonSource();
        var fetcher = new FixtureFetcher(new Dictionary<string, string>
        {
            ["/ch/1.html"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>A</p>\",\"next\":\"/ch/1_2.html\"}}",
            ["/ch/1_2.html"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>B</p>\",\"next\":\"/ch/1_3.html\"}}",
            ["/ch/1_3.html"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>C</p>\",\"next\":\"/ch/2.html\"}}",
            ["/ch/2.html"] = "{\"data\":{\"title\":\"第二章\",\"content\":\"<p>下一章不该被拉进来</p>\",\"next\":null}}",
        });

        var engine = new BookSourceEngine(fetcher);
        var content = await engine.GetContentAsync(source, "https://api-bc.wtzw.com/ch/1.html");

        // 1_2 / 1_3 是同章续页（跟随）；2.html 是下一章（闸门拦住）
        Assert.NotNull(content);
        Assert.Equal("<p>A</p>\n<p>B</p>\n<p>C</p>", content!.Content);
        Assert.Equal("第一章", content.Title);
        Assert.Equal(3, fetcher.Requests.Count);
    }

    [Fact]
    public async Task Content_Pagination_Stops_On_Repeated_Url()
    {
        var source = MakeJsonSource();
        var fetcher = new FixtureFetcher(new Dictionary<string, string>
        {
            ["/ch/1.html"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>A</p>\",\"next\":\"/ch/1_2.html\"}}",
            ["/ch/1_2.html"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>B</p>\",\"next\":\"/ch/1.html\"}}",
        });

        var engine = new BookSourceEngine(fetcher);
        var content = await engine.GetContentAsync(source, "https://api-bc.wtzw.com/ch/1.html");

        // 自环终止（reader：visited_urls 命中即停）
        Assert.Equal("<p>A</p>\n<p>B</p>", content!.Content);
        Assert.Equal(2, fetcher.Requests.Count);
    }

    // ---- 6. 图片 ----

    [Fact]
    public void Images_Are_Extracted_From_DataSrc_And_Absolutized()
    {
        const string html = "<p><img data-src=\"/img/a.jpg\"/><img src=\"/img/b.jpg\"/><img src=\"/img/b.jpg\"/></p>";
        var urls = RuleEngine.ExtractImageUrls(html, "https://h.example.com/ch/1.html");

        Assert.Equal(2, urls.Count); // 第三张重复
        Assert.Equal("https://h.example.com/img/a.jpg", urls[0]);
        Assert.Equal("https://h.example.com/img/b.jpg", urls[1]);
    }

    [Fact]
    public async Task Content_Image_Urls_Are_Attached_To_Chapter()
    {
        var source = MakeJsonSource();
        source.RuleContent = new ContentRule
        {
            Content = "data.content",
            Title = "data.title",
        };
        var fetcher = new FixtureFetcher(new Dictionary<string, string>
        {
            ["/ch/1"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<img src=\\\"//cdn.com/a.jpg\\\"/>\"}}",
        });

        var engine = new BookSourceEngine(fetcher);
        var content = await engine.GetContentAsync(source, "https://api-bc.wtzw.com/ch/1");

        Assert.Single(content!.ImageUrls);
        Assert.Equal("https://cdn.com/a.jpg", content.ImageUrls[0]);
    }

    // ---- 7. 批量并行 ----

    [Fact]
    public async Task Contents_Batch_Keeps_Input_Order_And_Reports_Progress()
    {
        var source = MakeJsonSource();
        var map = new Dictionary<string, string>();
        for (var i = 1; i <= 8; i++)
        {
            map[$"/ch/{i}"] = $"{{\"data\":{{\"title\":\"第{i}章\",\"content\":\"<p>内容{i}</p>\",\"next\":null}}}}";
        }

        var fetcher = new FixtureFetcher(map);
        var engine = new BookSourceEngine(fetcher);
        var chapters = Enumerable.Range(1, 8)
            .Select(i => new TocItem { Index = i - 1, ChapterName = $"第{i}章", ChapterUrl = $"https://api-bc.wtzw.com/ch/{i}" })
            .ToList();

        var progress = new ConcurrentBag<int>();
        // 用同步 IProgress：Progress<T> 会把回调抛到线程池，测试里断言时可能还没跑
        var results = await engine.GetContentsAsync(
            source, chapters, null, 4, new SyncProgress<int>(p => progress.Add(p)));

        Assert.Equal(8, results.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal($"<p>内容{i + 1}</p>", results[i].Content);
            Assert.Equal($"第{i + 1}章", results[i].Title);
        }

        Assert.Equal(8, progress.Count);
    }

    // ---- 8. 真实 Sample：规则健壮性（只取部分）----

    [Fact]
    public async Task Sample_Sources_Rules_Do_Not_Throw_On_Dummy_Bodies()
    {
        var slice = await SampleData.SliceAsync(60);
        var html = "<html><body><div class='a'><a href='/x'>t</a></div></body></html>";
        var json = "{\"data\":{\"list\":[{\"id\":1,\"title\":\"t\"}]},\"list\":[{\"id\":1}]}";

        var withInfo = 0;
        var withToc = 0;
        var withContent = 0;

        foreach (var src in slice)
        {
            if (src.RuleBookInfo is not null)
            {
                withInfo++;
                foreach (var body in new[] { html, json })
                {
                    new RuleEngine().ParseBookInfo(src, body, src.BookSourceUrl, src.BookSourceUrl);
                }
            }

            if (src.RuleToc is not null)
            {
                withToc++;
                foreach (var body in new[] { html, json })
                {
                    new RuleEngine().ParseTocPage(src, body, src.BookSourceUrl);
                }
            }

            if (src.RuleContent is not null)
            {
                withContent++;
                foreach (var body in new[] { html, json })
                {
                    new RuleEngine().ParseContentPage(src, body, src.BookSourceUrl);
                }
            }
        }

        // 样例里绝大多数书源都配齐了三类规则
        Assert.True(withInfo > 50, $"应有 >50 个书源配了详情规则，实际 {withInfo}");
        Assert.True(withToc > 50, $"应有 >50 个书源配了目录规则，实际 {withToc}");
        Assert.True(withContent > 50, $"应有 >50 个书源配了正文规则，实际 {withContent}");
    }

    [Fact]
    public async Task Sample_EndToEnd_Through_Engine_With_Static_Fixtures()
    {
        // 用 Sample 里的真实书源对象跑完整链路：详情 → 目录 → 正文。
        // 真实源的三类规则依赖 java.ajax / 加密 JS（离线不可执行），
        // 故只保留其非 JS 的规则部分（init/路径式 JSONPath 与真实源写法一致），其余字段（URL/请求头等）保持原样。
        var slice = await SampleData.SliceAsync(60);
        var source = slice.First(s => s.BookSourceUrl == "https://api-bc.wtzw.com");
        source.RuleBookInfo = new BookInfoRule
        {
            Init = "data.book",
            Name = "title",
            Author = "author",
            CoverUrl = "image_link",
            Intro = "intro",
            LastChapter = "latest_chapter_title",
            TocUrl = "toc_url",
        };
        source.RuleToc = new TocRule
        {
            ChapterList = "data.chapter_lists",
            ChapterName = "title",
            ChapterUrl = "id",
        };
        source.RuleContent = new ContentRule { Content = "data.content", Title = "data.title" };

        var fetcher = new FixtureFetcher(new Dictionary<string, string>
        {
            ["book/1001"] = BookInfoJson,
            ["toc/1001"] = TocJson,
            ["ch/1"] = ContentJson,
            ["ch/2"] = "{\"data\":{\"title\":\"第二章\",\"content\":\"<p>第二段</p>\",\"next\":null}}",
        });

        var engine = new BookSourceEngine(fetcher);
        var info = await engine.GetBookInfoAsync(source, "https://api-bc.wtzw.com/book/1001");
        Assert.NotNull(info);
        Assert.Equal("测试书名", info!.Name);

        var toc = await engine.GetTocAsync(source, info.TocUrl!);
        Assert.Equal(2, toc.Count);

        var content = await engine.GetContentAsync(source, toc[0].ChapterUrl!);
        Assert.Equal("<p>正文第一段</p><p>正文第二段</p>", content!.Content);
    }
}

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 步骤 0/1/2 性能优化的回归测试：锁定"优化不改变结果"的等价性。
/// 覆盖：节点化片段（活节点 → 独立文档）与旧 OuterHtml 路径的结果一致、
/// JSONPath 路径分段缓存的并发安全、惰性 Raw、剥标题+抽图片合并、引擎池化后的批量正文。
/// </summary>
public class Phase012OptimizationTests
{
    // ---------- 1. 节点化 HTML 片段：与 OuterHtml 路径结果一致 ----------

    [Fact]
    public void Toc_Html_Node_Fragments_Keep_All_Field_Semantics()
    {
        const string html = """
        <html><body><div id="list">
          <li class="item"><a href="/c/1.html">第一章</a><span class="vip">1</span></li>
          <li class="vol"><span>第一卷 起始</span></li>
          <li><a href="/c/2.html">第二章</a></li>
        </div></body></html>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleToc = new TocRule
            {
                ChapterList = "@css:#list>li",
                ChapterName = "@css:a@text||@css:span@text",
                ChapterUrl = "a@href",
                IsVolume = "@css:li@class",   // 根匹配：选择器命中片段根元素自身的 class
                IsVip = "@css:.vip@text",
            },
        };

        var (chapters, _) = new RuleEngine().ParseTocPage(source, html, "https://h.example.com/toc/1.html");

        Assert.Equal(3, chapters.Count);
        Assert.Equal("第一章", chapters[0].ChapterName);
        Assert.Equal("https://h.example.com/c/1.html", chapters[0].ChapterUrl);
        Assert.True(chapters[0].IsVip);
        // 根匹配语义：li@class 取到片段根元素自身的 class 值（"item" 非空 → 也判为卷）
        Assert.True(chapters[0].IsVolume);
        Assert.True(chapters[1].IsVolume);   // class="vol" → 卷 + 伪 URL
        Assert.Equal("第一卷 起始1", chapters[1].ChapterUrl);
        Assert.False(chapters[2].IsVolume);  // 无 class → 空串 → 非卷
    }

    [Fact]
    public void Toc_Html_XPath_Node_Fragments_Stay_Scoped()
    {
        const string html = """
        <html><body><div id="list">
          <li><a href="/c/1.html">第一章</a></li>
          <li><a href="/c/2.html">第二章</a></li>
        </div><p id="outside"><a href="/outside.html">外部链接</a></p></body></html>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleToc = new TocRule
            {
                // XPath 列表模式走节点形态：片段文档内的绝对 XPath 必须仍限定在片段内
                ChapterList = "//li",
                ChapterName = "a@text",
                ChapterUrl = "a@href",
                NextTocUrl = "//p[@id='outside']/a/@href",
            },
        };

        var (chapters, next) = new RuleEngine().ParseTocPage(source, html, "https://h.example.com/toc/1.html");

        Assert.Equal(2, chapters.Count);
        Assert.Equal("第一章", chapters[0].ChapterName);
        Assert.Equal("第二章", chapters[1].ChapterName);
        Assert.Equal(new[] { "https://h.example.com/outside.html" }, next);
    }

    [Fact]
    public void Search_Html_Node_Fragments_Extract_Fields_And_Absolute_Urls()
    {
        var sb = new System.Text.StringBuilder("<html><body><ul class=\"book-list\">");
        for (var i = 0; i < 50; i++)
        {
            sb.Append($"<li class=\"book-item\"><a href=\"/novel/{i}\"><img src=\"/cover/{i}.jpg\"/><p class=\"name\">书名{i}</p><p class=\"author\">作者{i}</p></a></li>");
        }
        sb.Append("</ul></body></html>");

        var source = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleSearch = new SearchRule
            {
                BookList = "@css:.book-list>li",
                Name = "@css:.name@text",
                Author = "@css:.author@text",
                BookUrl = "a@href",
                CoverUrl = "img@src",   // 裸属性名作用于当前片段元素
            },
        };

        var books = new RuleEngine().SearchBooks(source, sb.ToString(), "https://h.example.com/s?k=x");

        Assert.Equal(50, books.Count);
        Assert.Equal("书名0", books[0].Name);
        Assert.Equal("作者49", books[49].Author);
        Assert.Equal("https://h.example.com/novel/7", books[7].BookUrl);
        Assert.Equal("https://h.example.com/cover/7.jpg", books[7].CoverUrl);
    }

    // ---------- 2. JSONPath 路径分段缓存：并发安全 + 结果不变 ----------

    [Fact]
    public void JsonPathEval_PathCache_Concurrent_Queries_Match_Serial()
    {
        var json = """
        {"data":{"books":[{"id":"1","title":"甲"},{"id":"2","title":"乙"},{"id":"3","title":"丙"}]}}
        """;
        var node = JsonNode.Parse(json)!;

        var serial = JsonPathEval.Query(node, "$.data.books[*].title");
        Assert.Equal(new[] { "甲", "乙", "丙" }, serial);

        // 并发跑同一批路径（分段缓存被多线程读写）
        var bag = new ConcurrentBag<List<string>>();
        Parallel.For(0, 64, _ =>
        {
            bag.Add(JsonPathEval.Query(node, "$.data.books[*].title"));
            bag.Add(JsonPathEval.Query(node, "data.books[0].id"));
            bag.Add(JsonPathEval.Query(node, "$.data.books[-1].title"));
        });

        Assert.Equal(64 * 3, bag.Count);
        Assert.All(bag, r => Assert.NotEmpty(r));
        Assert.All(bag.Where(r => r.Count == 1), r => Assert.True(r[0] is "甲" or "乙" or "丙" or "1" or "2" or "3"));
    }

    [Fact]
    public void JsonPathEval_QueryNodes_Reuses_Parsed_Root()
    {
        var json = """{"data":{"chapter_lists":[{"id":"/ch/1","title":"c1"},{"id":"/ch/2","title":"c2"}]}}""";
        var root = JsonNode.Parse(json)!;

        var nodes = JsonPathEval.QueryNodes(root, "data.chapter_lists");
        Assert.Equal(2, nodes.Count);
        Assert.Equal("c1", nodes[0]!["title"]!.GetValue<string>());

        // 省略 $ 前缀与 $[*] 展平语义保持（ASCII 避免默认编码器转义干扰断言）
        var flat = JsonPathEval.Query(root, "data.chapter_lists");
        Assert.Equal(new[] { "{\"id\":\"/ch/1\",\"title\":\"c1\"}", "{\"id\":\"/ch/2\",\"title\":\"c2\"}" }, flat);
    }

    // ---------- 3. 惰性 Raw：JSON 节点片段 + 正则字段规则 ----------

    [Fact]
    public void Json_Node_Fragment_LazyRaw_Regex_Rule()
    {
        var json = """
        {"data":{"books":[{"id":"/novel/1","title":"书名甲","code":"ABC123"}]}}
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://api.example.com",
            RuleSearch = new SearchRule
            {
                BookList = "data.books[*]",
                Name = "title",
                // 正则字段规则作用于片段的 Raw（惰性序列化出的 JSON 文本）。
                // 纯 ASCII 模式，避免 ToJsonString 的 \uXXXX 转义干扰断言。
                Intro = "@regex:ABC(\\d+)",
                BookUrl = "id",
            },
        };

        var books = new RuleEngine().SearchBooks(source, json, "https://api.example.com");

        Assert.Single(books);
        Assert.Equal("书名甲", books[0].Name);
        // legado 语义：正则提取返回整个匹配（不是捕获组）
        Assert.Equal("ABC123", books[0].Intro);
        Assert.Equal("https://api.example.com/novel/1", books[0].BookUrl);
    }

    // ---------- 4. 剥标题 + 抽图片：合并路径与分开做结果一致 ----------

    [Fact]
    public void StripTitleAndExtractImages_Matches_Split_Path()
    {
        var cases = new[]
        {
            "<h2>第一章</h2><p>正文</p><img src=\"/a.jpg\"/><img data-src=\"/b.jpg\"/><img src=\"/a.jpg\"/>",
            "<div><h3>标题</h3><p>内容</p><img src=\"//cdn.com/x.png\"/></div>",
            "<p>没有标题直接正文</p><img src=\"/c.jpg\"/>",
            "<article><h1>文章标题</h1><img src=\"/d.jpg\"/></article>",
            "",
        };

        foreach (var html in cases)
        {
            var expectedHtml = BookSourceEngine.StripLeadingContentTitle(html);
            var expectedImages = RuleEngine.ExtractImageUrls(expectedHtml, "https://h.example.com/ch/1.html");

            var (actualHtml, actualImages) = BookSourceEngine.StripTitleAndExtractImages(html, "https://h.example.com/ch/1.html");

            Assert.Equal(expectedHtml, actualHtml);
            Assert.Equal(expectedImages, actualImages);
        }
    }

    [Fact]
    public void StripTitleAndExtractImages_Dedupes_And_Keeps_Order()
    {
        const string html = "<h2>标题</h2><p><img data-src=\"/img/a.jpg\"/><img src=\"/img/b.jpg\"/><img src=\"/img/a.jpg\"/></p>";

        var (stripped, images) = BookSourceEngine.StripTitleAndExtractImages(html, "https://h.example.com/ch/1.html");

        Assert.DoesNotContain("<h2>", stripped);
        Assert.Equal(new[] { "https://h.example.com/img/a.jpg", "https://h.example.com/img/b.jpg" }, images);
    }

    // ---------- 5. 引擎池化：批量正文结果与串行一致（含 JS 书源） ----------

    private sealed class MapFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map;
        public MapFetcher(Dictionary<string, string> map) => _map = map;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            foreach (var kv in _map)
            {
                if (url.Contains(kv.Key, StringComparison.Ordinal))
                {
                    return Task.FromResult((kv.Value, url));
                }
            }
            return Task.FromResult((string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    [Fact]
    public async Task GetContentsAsync_PooledEngines_Match_Serial_Results()
    {
        // JS 型正文（前导 <js> + replaceRegex）：每章都会构造/复用 Jint 引擎
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://js.example.com",
            RuleContent = new ContentRule
            {
                Content = "<js>var t = result.trim(); t</js>@css:#content@html",
                Title = "@css:h2@text",
                ReplaceRegex = "##<script[\\s\\S]*?</script>##",
            },
        };

        var map = new Dictionary<string, string>();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 24; i++)
        {
            var url = $"https://js.example.com/ch/{i}";
            map[$"/ch/{i}"] = $"<html><body><div id=\"content\"><h2>第{i}章</h2><p>内容{i}</p><script>bad()</script><img src=\"/img/{i}.jpg\"/></div></body></html>";
            chapters.Add(new TocItem { Index = i, ChapterName = $"第{i}章", ChapterUrl = url });
        }

        var fetcher = new MapFetcher(map);
        var engine = new BookSourceEngine(fetcher);

        // 串行基准（逐章）
        var serial = new List<ChapterContent>();
        foreach (var c in chapters)
        {
            serial.Add((await engine.GetContentAsync(source, c.ChapterUrl!))!);
        }

        // 并行批量（池化引擎）
        var batch = await engine.GetContentsAsync(source, chapters, null, 8, null);

        Assert.Equal(serial.Count, batch.Count);
        for (var i = 0; i < serial.Count; i++)
        {
            Assert.Equal(serial[i].Title, batch[i].Title);
            Assert.Equal(serial[i].Content, batch[i].Content);
            Assert.Equal(serial[i].ImageUrls, batch[i].ImageUrls);
        }

        // 标题剥离 + 图片绝对化在批量路径同样生效
        Assert.All(batch, c =>
        {
            Assert.DoesNotContain("<h2>", c.Content);
            Assert.DoesNotContain("<script>", c.Content);
            Assert.Contains("https://js.example.com/img/", c.ImageUrls[0]);
        });
    }

    [Fact]
    public async Task GetContentsAsync_JsonSource_Matches_Serial()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://api-bc.wtzw.com",
            RuleContent = new ContentRule { Content = "data.content", Title = "data.title", NextContentUrl = "data.next" },
        };

        var map = new Dictionary<string, string>();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 20; i++)
        {
            var url = $"https://api-bc.wtzw.com/ch/{i}";
            map[$"/ch/{i}"] = $"{{\"data\":{{\"title\":\"第{i}章\",\"content\":\"<p>内容{i}</p><img src=\\\"/img/{i}.jpg\\\"/>\",\"next\":null}}}}";
            chapters.Add(new TocItem { Index = i, ChapterName = $"第{i}章", ChapterUrl = url });
        }

        var engine = new BookSourceEngine(new MapFetcher(map));
        var serial = new List<ChapterContent>();
        foreach (var c in chapters)
        {
            serial.Add((await engine.GetContentAsync(source, c.ChapterUrl!))!);
        }

        var batch = await engine.GetContentsAsync(source, chapters, null, 4, null);

        Assert.Equal(serial.Count, batch.Count);
        for (var i = 0; i < serial.Count; i++)
        {
            Assert.Equal(serial[i].Content, batch[i].Content);
            Assert.Equal(serial[i].Title, batch[i].Title);
            Assert.Equal(serial[i].ImageUrls, batch[i].ImageUrls);
        }
    }

    // ---------- 6. 模式识别：上下文判定与字符串判定等价 ----------

    [Fact]
    public void DetectMode_Context_Matches_String_Overload()
    {
        var bodies = new[]
        {
            "{\"a\":1}",
            "  [1,2,3] ",
            "<html><body><p>x</p></body></html>",
            "{invalid json",
            "plain text",
            "",
        };
        var rules = new[]
        {
            "@css:.a", "@xpath://a", "@json:$.a", "@regex:\\d+", "<js>1</js>", "@js:1", "js:1",
            "/div/a", "./a", "$.a", "$[0]", ":pattern", ".a", "a.b", "body", "",
        };

        foreach (var body in bodies)
        {
            var ctx = new FragmentContext(body);
            foreach (var rule in rules)
            {
                var viaCtx = RuleAnalyzer.DetectMode(rule, ctx);
                var viaString = RuleAnalyzer.DetectMode(rule, body);
                Assert.Equal(viaString, viaCtx);
            }
        }
    }

    // ---------- 7. 组合符列表规则：多段共享同一份 body 解析 ----------

    [Fact]
    public void Search_Combo_ListRules_Share_Body_And_Concat_In_Order()
    {
        var json = """
        {"data":{"books":[{"id":"/1","title":"甲"}],"more":[{"id":"/2","title":"乙"}]}}
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://api.example.com",
            RuleSearch = new SearchRule
            {
                BookList = "data.books[*]&&data.more[*]",
                Name = "title",
                BookUrl = "id",
            },
        };

        var books = new RuleEngine().SearchBooks(source, json, "https://api.example.com");

        Assert.Equal(2, books.Count);
        Assert.Equal("甲", books[0].Name);
        Assert.Equal("乙", books[1].Name);
        Assert.Equal("https://api.example.com/2", books[1].BookUrl);
    }

    [Fact]
    public void Search_Combo_ListRules_Or_Takes_First_NonEmpty()
    {
        var json = """
        {"data":{"books":[{"id":"/1","title":"甲"}],"more":[{"id":"/2","title":"乙"}]}}
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://api.example.com",
            RuleSearch = new SearchRule
            {
                BookList = "data.nothing[*]||data.more[*]",
                Name = "title",
                BookUrl = "id",
            },
        };

        var books = new RuleEngine().SearchBooks(source, json, "https://api.example.com");

        Assert.Single(books);
        Assert.Equal("乙", books[0].Name);
    }
}

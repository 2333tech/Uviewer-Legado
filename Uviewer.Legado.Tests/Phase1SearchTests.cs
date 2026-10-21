using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 1 验收：基于 Sample/ 真实书源定义 + 各模式 fixture 的同步测试。
/// 覆盖 RuleAnalyzer / Selectors / JsonPathEval / JsBridge / RuleEngine / UrlAnalyzer / SearchService。
/// </summary>
public class Phase1SearchTests
{
    // 取样例中前 N 条（部分内容）；全量时直接用 SampleData.EnumerateAsync()
    private static List<BookSourceModel> LoadSample() =>
        BookSourceSerializer.DeserializeBookSourceList(File.ReadAllText(SampleData.FilePath));

    // ---- A. 全量真实书源规则可被引擎解析（按模式计数）----

    [Fact]
    public void Sample_All_Searchable_Rules_Compile_Without_Exception()
    {
        var list = LoadSample();
        var jsonDummy = "{\"data\":[]}";
        var htmlDummy = "<html></html>";
        var tally = new Dictionary<string, int>();
        var searchable = 0;

        foreach (var src in list)
        {
            if (string.IsNullOrWhiteSpace(src.SearchUrl) || src.RuleSearch is null)
            {
                continue;
            }

            searchable++;
            var rule = src.RuleSearch!;
            // 对 bookList 与每个字段规则执行模式识别（确保不抛异常）
            var listMode = RuleAnalyzer.DetectMode(rule.BookList ?? string.Empty, jsonDummy);
            Tally(tally, listMode);
            foreach (var f in new[] { rule.Name, rule.Author, rule.BookUrl, rule.CoverUrl, rule.Intro, rule.Kind, rule.LastChapter, rule.UpdateTime, rule.WordCount })
            {
                if (!string.IsNullOrWhiteSpace(f))
                {
                    Tally(tally, RuleAnalyzer.DetectMode(f, jsonDummy));
                    Tally(tally, RuleAnalyzer.DetectMode(f, htmlDummy));
                }
            }
        }

        Assert.True(searchable > 100, $"可搜索书源应 >100，实际 {searchable}");
        // Sample 中 JSON 模式占多数（起点/番茄/猫眼 等），HTML/CSS/XPath 也有相当数量
        Assert.True(tally.GetValueOrDefault("JsonPath", 0) > 50, $"JsonPath 规则应 >50，实际 {tally.GetValueOrDefault("JsonPath", 0)}");
    }

    private static void Tally(Dictionary<string, int> tally, RuleMode mode)
    {
        var key = mode == RuleMode.Default ? "Css" : mode.ToString();
        tally.TryGetValue(key, out var v);
        tally[key] = v + 1;
    }

    // ---- B. JSONPath 模式（对标 猫眼看书）----

    [Fact]
    public void JsonPath_Search_Extracts_Books()
    {
        const string json = """
        {
          "data": [
            {"novelId":"123","novelName":"测试小说","authorName":"作者A","cover":"//img.x.com/a.jpg","summary":"简介","categoryNames":{"className":"玄幻"},"wordNum":123456},
            {"novelId":"9","novelName":"第二本","authorName":"作者B","cover":"//img.x.com/b.jpg","summary":"S2","categoryNames":{"className":"都市"},"wordNum":99}
          ]
        }
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://api.x.com/search?keyword={{key}}&page={{page}}",
            RuleSearch = new SearchRule
            {
                BookList = "$.data[*]",
                Name = "$.novelName",
                Author = "$.authorName",
                BookUrl = "/novel/{{$.novelId}}?isSearch=1",
                CoverUrl = "$.cover",
                Intro = "$.summary",
                Kind = "$.categoryNames.className",
                WordCount = "$.wordNum",
            },
        };

        var books = new RuleEngine().SearchBooks(source, json, "http://api.x.com/search?keyword=k&page=1");

        Assert.Equal(2, books.Count);
        Assert.Equal("测试小说", books[0].Name);
        Assert.Equal("作者A", books[0].Author);
        Assert.Equal("玄幻", books[0].Kind);
        Assert.Contains("novel/123", books[0].BookUrl);
        Assert.Contains("http://api.x.com/novel/123", books[0].BookUrl);
        // reader resolve_url：协议相对地址 //host/x 一律补 https:
        Assert.Equal("https://img.x.com/a.jpg", books[0].CoverUrl);
        Assert.Equal("第二本", books[1].Name);
    }

    // ---- C. CSS 模式（对标 星际漫画）----

    [Fact]
    public void Css_Search_Extracts_Books_With_Axis_And_RegexReplace()
    {
        const string html = """
        <div class="manga-list">
          <a href="/manga/aaa">
            <img src="//cdn.com/aaa.jpg"/>
            <div class="manga-item-title">漫画A</div>
            <div class="manga-item-subtitle"><span>热血</span></div>
          </a>
          <a href="/manga/bbb">
            <img src="//cdn.com/bbb.jpg"/>
            <div class="manga-item-title">漫画B</div>
          </a>
        </div>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/search?title={{key}}&page={{page}}",
            RuleSearch = new SearchRule
            {
                BookList = "@css:.manga-list>a",
                Name = "@css:.manga-item-title@text",
                CoverUrl = "img@src",
                Kind = "@css:.manga-item-subtitle>span@text",
                BookUrl = "href##$##,{Cookie:\"xmanhua_lang=2\"}",
            },
        };

        var books = new RuleEngine().SearchBooks(source, html, "http://x.com/search?title=k&page=1");

        Assert.Equal(2, books.Count);
        Assert.Equal("漫画A", books[0].Name);
        Assert.Equal("https://cdn.com/aaa.jpg", books[0].CoverUrl);
        Assert.Equal("热血", books[0].Kind);
        // `##$##` 把 `$`（正则：行尾）替换为 `,{Cookie...}`（legado 追加取图选项）
        Assert.Contains("/manga/aaa", books[0].BookUrl);
        Assert.EndsWith(",{Cookie:\"xmanhua_lang=2\"}", books[0].BookUrl);
    }

    // ---- D. XPath 模式（对标 灵感小说）----

    [Fact]
    public void XPath_Search_Extracts_Books_With_Text_And_Attr()
    {
        const string html = """
        <ul class='list'>
          <li>
            <a href="/book/1.html"><p class='bookname'><a>书名一</a></p></a>
            <p class='data'><a>作者甲</a></p>
            <p class='intro'>简介一</p>
            <a href="/img/1.jpg"><img src="/img/1.jpg"/></a>
          </li>
          <li>
            <a href="/book/2.html"><p class='bookname'><a>书名二</a></p></a>
            <p class='data'><a>作者乙</a></p>
            <p class='intro'>简介二</p>
          </li>
        </ul>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/search.html",
            RuleSearch = new SearchRule
            {
                BookList = "//ul[@class='list']/li",
                Name = "//p[@class='bookname']/a/text()",
                Author = "//p[@class='data']/a/text()",
                BookUrl = "//li/a[1]/@href",
                CoverUrl = "//li//img/@src",
                Intro = "//p[@class='intro']/text()",
                LastChapter = "//p[@class='data'][2]/a/text()",
            },
        };

        var books = new RuleEngine().SearchBooks(source, html, "http://x.com/search.html");

        Assert.Equal(2, books.Count);
        Assert.Equal("书名一", books[0].Name);
        Assert.Equal("作者甲", books[0].Author);
        Assert.Equal("http://x.com/book/1.html", books[0].BookUrl);
        Assert.Equal("http://x.com/img/1.jpg", books[0].CoverUrl);
        Assert.Equal("简介一", books[0].Intro);
        Assert.Equal("书名二", books[1].Name);
    }

    // ---- E. 行内 ## 替换（去标签/去前缀）----

    [Fact]
    public void Inline_Regex_Replacement_Strips_Label()
    {
        const string html = """
        <div class='data'>作者：张三</div>
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/s",
            RuleSearch = new SearchRule
            {
                BookList = "//div",
                Author = "//div/text()##作者：",
                Name = "//div/text()##作者：##新笔名",
            },
        };

        var books = new RuleEngine().SearchBooks(source, html, "http://x.com/s");
        Assert.Single(books);
        Assert.Equal("张三", books[0].Author);
        // ##作者：##新笔名 => 把"作者："替换为"新笔名"
        Assert.Equal("新笔名张三", books[0].Name);
    }

    // ---- F. 组合符 || 取首个非空 ----

    [Fact]
    public void Combinator_Or_Returns_First_NonEmpty()
    {
        const string html = "<h3><a>标题B</a></h3>";
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/s",
            RuleSearch = new SearchRule
            {
                BookList = "body",
                Name = "//h2/a/text()||//h3/a/text()",
            },
        };

        var books = new RuleEngine().SearchBooks(source, html, "http://x.com/s");
        Assert.Single(books);
        Assert.Equal("标题B", books[0].Name);
    }

    // ---- G. 内联 JS 字段转换 ----

    [Fact]
    public void Js_Field_Transforms_Value()
    {
        const string json = "[{\"name\":\"alice\"}]";
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/s",
            RuleSearch = new SearchRule
            {
                BookList = "$[*]",
                Name = "$.name\n@js:\nresult.toUpperCase()",
            },
        };

        var books = new RuleEngine().SearchBooks(source, json, "http://x.com/s");
        Assert.Single(books);
        Assert.Equal("ALICE", books[0].Name);
    }

    // ---- H. JS 列表段 → 产出 JSON → 再解析 ----

    [Fact]
    public void Js_List_Produces_Json_Then_Parsed()
    {
        const string html = "<body>any</body>";
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/s",
            RuleSearch = new SearchRule
            {
                BookList = "<js>JSON.stringify([{name:\"a\"},{name:\"b\"}])</js>$[*]",
                Name = "$.name",
            },
        };

        var books = new RuleEngine().SearchBooks(source, html, "http://x.com/s");
        Assert.Equal(2, books.Count);
        Assert.Equal("a", books[0].Name);
        Assert.Equal("b", books[1].Name);
    }

    // ---- I. 端到端：真实 Sample 书源 + 静态抓取（猫眼看书）----

    [Fact]
    public async Task EndToEnd_Real_Sample_Source_Through_SearchService()
    {
        var list = LoadSample();
        var source = list.FirstOrDefault(s => s.BookSourceUrl.StartsWith("http://api.jmlldsc.com"));
        Assert.NotNull(source);
        Assert.NotNull(source!.RuleSearch);

        const string response = """
        {"data":[{"novelId":"777","novelName":"端到端小说","authorName":"_writer","cover":"//c.im/g.jpg","summary":"端到端简介","categoryNames":{"className":"科幻"},"wordNum":555}]}
        """;
        var fetcher = new StaticFetcher(new Dictionary<string, string>
        {
            ["api.jmlldsc.com"] = response,
        });

        var service = new SearchService(fetcher);
        var books = await service.SearchAsync(source, "任意词", 1);

        Assert.Single(books);
        Assert.Equal("端到端小说", books[0].Name);
        Assert.Equal("555", books[0].WordCount);
        Assert.Contains("novel/777", books[0].BookUrl);
    }

    // ---- J. UrlAnalyzer：{{key}}/{{page}} 与 ,{json} 选项 ----

    [Fact]
    public void UrlAnalyzer_Interpolates_And_Parses_Options()
    {
        var ua = new UrlAnalyzer();
        var (url, method, body, headers, charset) = ua.Build(
            "https://x.com/s?wd={{key}}&pn={{(page - 1) * 10}},{\"method\":\"post\",\"body\":\"q={{key}}\",\"headers\":{\"User-Agent\":\"T\"}}",
            "搜索词",
            2);

        // reader encode_get_query：中文查询串按 UTF-8 百分号编码
        Assert.Equal("https://x.com/s?wd=%E6%90%9C%E7%B4%A2%E8%AF%8D&pn=10", url);
        Assert.Equal("https://x.com/s?wd=搜索词&pn=10", Uri.UnescapeDataString(url));
        // reader：method 先转大写再匹配，最终规范化为大写
        Assert.Equal("POST", method);
        Assert.Equal("q=搜索词", body);
        Assert.NotNull(headers);
        Assert.True(headers!.ContainsKey("User-Agent"));
    }

    // ---- K. 并行搜索：每个线程独立引擎，结果一致 ----

    [Fact]
    public void Parallel_Searches_Use_Independent_Engines()
    {
        const string json = """
        {"data":[
          {"novelId":"1","novelName":"A","authorName":"甲"},
          {"novelId":"2","novelName":"B","authorName":"乙"}
        ]}
        """;
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/s",
            RuleSearch = new SearchRule { BookList = "$.data[*]", Name = "$.novelName", Author = "$.authorName" },
        };

        // RuleEngine 持有 Jint 引擎，不保证线程安全 => 并发场景下每线程一个实例
        var results = new System.Collections.Concurrent.ConcurrentBag<List<SearchResult>>();
        Parallel.For(0, 16, _ =>
        {
            var engine = new RuleEngine();
            results.Add(engine.SearchBooks(source, json, "http://x.com/s"));
        });

        Assert.Equal(16, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(2, r.Count);
            Assert.Equal("A", r[0].Name);
            Assert.Equal("乙", r[1].Author);
        });
    }

    // ---- L. 正则缓存：同一规则反复求值结果一致（缓存命中路径）----

    [Fact]
    public void Regex_Cache_Returns_Stable_Results_Under_Repetition()
    {
        var html = string.Concat(Enumerable.Range(0, 200)
            .Select(i => $"<div class='item'>名称：第{i}本</div>"));
        var source = new BookSourceModel
        {
            BookSourceUrl = "x",
            SearchUrl = "http://x.com/s",
            RuleSearch = new SearchRule
            {
                BookList = "//div[@class='item']",
                Name = "//div/text()##名称：",
            },
        };

        var books = new RuleEngine().SearchBooks(source, html, "http://x.com/s");

        Assert.Equal(200, books.Count);
        Assert.Equal("第0本", books[0].Name);
        Assert.Equal("第199本", books[199].Name);
    }

    private sealed class StaticFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map;

        public StaticFetcher(Dictionary<string, string> map) => _map = map;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url,
            RequestSpec? spec = null,
            string? sourceKey = null,
            CancellationToken cancellationToken = default)
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
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }
}

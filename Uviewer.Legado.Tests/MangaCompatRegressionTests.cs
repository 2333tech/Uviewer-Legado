using System.Collections.Generic;
using System.Linq;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 漫画（图片书源）兼容性回归：包子漫画一族在 legado MD3 上能读、在本项目"无法获取目录和第一页正文"，
/// 逐条定位到引擎里六处与 legado 语义不一致的地方。每个用例对应一处，fixture 取自这些站点的真实结构。
/// <list type="number">
/// <item>URL 字段（bookUrl/coverUrl/tocUrl/chapterUrl/nextContentUrl）多命中必须取首个；
///   legado 走 <c>getString(isUrl = true)</c> → jsoup <c>getString0</c>，文本字段才 <c>\n</c> 拼接。</item>
/// <item>脚本全局 <c>src</c>（= 当前内容）必须绑定；包子漫画的 tocUrl 用它正则抽 <c>data-mid</c>。</item>
/// <item><c>java.getElements(...)</c> 返回的元素数组可以<b>直接当</b> chapterList / bookList —— 
///   走 ToString() 只会得到 "[object Object],…"。</item>
/// <item><c>java.getElements</c> 的规则要支持 <c>&amp;&amp;</c>/<c>||</c>/<c>%%</c> 合并。</item>
/// <item>方括号索引 <c>[3,4]</c>/<c>[-1:0]</c>/<c>[!0,1]</c>（legado <c>ElementsSingle</c>）。</item>
/// <item>属性值要解码 HTML 实体：<c>&amp;amp;</c> 不解码，章节链接的第二个参数名会变成 <c>amp;slot</c>。</item>
/// <item>正文图片地址可带行内选项块 <c>地址,{"headers":…}</c>，取图片地址时必须剥掉。</item>
/// <item><c>tocRule.preUpdateJs</c> 只取副作用（legado 丢弃返回值），不能把 <c>book.canUpdate=false</c>
///   的 "false" 当新原文。</item>
/// </list>
/// </summary>
public class MangaCompatRegressionTests
{
    private static RuleEngine NewEngine() => new()
    {
        Store = SourceStateStore.InMemory,
        Cookies = SourceCookieStore.Memory,
    };

    // ---------- ① URL 字段多命中取首个 ----------

    [Fact]
    public void Search_BookUrl_Takes_First_Match_When_Selector_Hits_Two_Anchors()
    {
        // 包子漫画（优+）的搜索规则就是 tag.a@href：卡片外层与标题各有一个 a
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://cn.bzmanga.com",
            BookSourceName = "包子（测试）",
            RuleSearch = new SearchRule
            {
                BookList = "class.comics-card",
                Name = "class.comics-card__title@text",
                BookUrl = "tag.a@href",
            },
        };

        const string html = """
            <div class="comics-card">
              <a href="/comic/aaa"><amp-img src="/c.jpg"></amp-img></a>
              <a href="/comic/aaa/comments"><h3 class="comics-card__title">斗罗大陆4终极斗罗</h3></a>
            </div>
            """;

        var books = NewEngine().SearchBooks(source, html, source.BookSourceUrl);

        var book = Assert.Single(books);
        Assert.Equal("斗罗大陆4终极斗罗", book.Name);
        Assert.Equal("https://cn.bzmanga.com/comic/aaa", book.BookUrl);
        Assert.DoesNotContain("\n", book.BookUrl!);
    }

    [Fact]
    public void Toc_ChapterUrl_Takes_First_Match_When_Selector_Hits_Two_Anchors()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://cn.bzmanga.com",
            BookSourceName = "包子（测试）",
            RuleToc = new TocRule
            {
                ChapterList = "class.item",
                ChapterName = "tag.span@text",
                ChapterUrl = "tag.a@href",
            },
        };

        const string html = """
            <div class="item"><a href="/ch/1"><span>第1话</span></a><a href="/ch/1?from=list"></a></div>
            """;

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://cn.bzmanga.com/comic/aaa");

        var chapter = Assert.Single(chapters);
        Assert.Equal("第1话", chapter.ChapterName);
        Assert.Equal("https://cn.bzmanga.com/ch/1", chapter.ChapterUrl);
    }

    // ---------- ② src 全局绑定 ----------

    [Fact]
    public void BookInfo_TocUrl_Js_Can_Read_Src_Binding()
    {
        // 包子漫画（优++）真实写法：从详情页 HTML 里正则抽 data-mid，拼出目录 API 地址
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://manhuafree.com",
            BookSourceName = "包子（测试）",
            RuleBookInfo = new BookInfoRule
            {
                Name = "h1@text",
                TocUrl = """
                    <js>
                    var mid = src.match(/data-mid="(\d+)"/)[1];
                    java.put("mid", mid);
                    "https://api.example.com/api/manga/get?mid=" + mid;
                    </js>
                    """,
            },
        };

        const string html = """<html><body><div data-mid="335"><h1>斗罗大陆4终极斗罗</h1></div></body></html>""";

        var info = NewEngine().ParseBookInfo(source, html, source.BookSourceUrl, "https://manhuafree.com/manga/x");

        Assert.NotNull(info);
        Assert.Equal("https://api.example.com/api/manga/get?mid=335", info!.TocUrl);
    }

    // ---------- ③ 纯 JS chapterList 返回元素数组 ----------

    [Fact]
    public void Toc_ChapterList_Js_May_Return_Element_Array()
    {
        // 包子漫画5 真实写法：脚本直接返回 java.getElements(...)
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://cn.webmota.com",
            BookSourceName = "包子（测试）",
            RuleToc = new TocRule
            {
                ChapterList = """
                    <js>
                    java.getElements("id.chapter-items@class.comics-chapters@a");
                    </js>
                    """,
                ChapterName = "a@text||span@text||text",
                ChapterUrl = "tag.a@href||href",
            },
        };

        const string html = """
            <div id="chapter-items">
              <div class="pure-u-1 comics-chapters"><a class="comics-chapters__item" href="/c/1"><div><span>序章</span></div></a></div>
              <div class="pure-u-1 comics-chapters"><a class="comics-chapters__item" href="/c/2"><div><span>神秘之人</span></div></a></div>
            </div>
            """;

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://cn.webmota.com/comic/x");

        Assert.Equal(2, chapters.Count);
        Assert.Equal("序章", chapters[0].ChapterName);
        Assert.Equal("https://cn.webmota.com/c/1", chapters[0].ChapterUrl);
    }

    [Fact]
    public void Toc_ChapterList_Js_May_Return_Object_Array()
    {
        // 另一种真实写法：脚本自己 push {text, href}
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "对象数组（测试）",
            RuleToc = new TocRule
            {
                ChapterList = """
                    <js>
                    var list = [];
                    list.push({text: "第一章", href: "/c/1"});
                    list.push({text: "第二章", href: "/c/2"});
                    list;
                    </js>
                    """,
                ChapterName = "$.text",
                ChapterUrl = "$.href",
            },
        };

        var (chapters, _) = NewEngine().ParseTocPage(source, "<html></html>", "https://example.com/book/1");

        Assert.Equal(2, chapters.Count);
        Assert.Equal("第二章", chapters[1].ChapterName);
        Assert.Equal("https://example.com/c/2", chapters[1].ChapterUrl);
    }

    [Fact]
    public void Search_BookList_Js_May_Return_Element_Array()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "搜索数组（测试）",
            RuleSearch = new SearchRule
            {
                BookList = """<js>java.getElements("class.card");</js>""",
                Name = "h3@text",
                BookUrl = "a@href",
            },
        };

        const string html = """
            <div class="card"><a href="/book/1"><h3>书一</h3></a></div>
            <div class="card"><a href="/book/2"><h3>书二</h3></a></div>
            """;

        var books = NewEngine().SearchBooks(source, html, source.BookSourceUrl);

        Assert.Equal(2, books.Count);
        Assert.Equal("书二", books[1].Name);
        Assert.Equal("https://example.com/book/2", books[1].BookUrl);
    }

    // ---------- ④ java.getElements 的组合符 ----------

    [Fact]
    public void JavaGetElements_Supports_Union_Combinator()
    {
        // 包子漫画5：id.chapter-items@…@a && id.chapters_other_list@…@a
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "合并（测试）",
            RuleToc = new TocRule
            {
                ChapterList = """
                    <js>
                    java.getElements("id.a@a&&id.b@a");
                    </js>
                    """,
                ChapterName = "text",
                ChapterUrl = "href",
            },
        };

        const string html = """
            <div id="a"><a href="/x/1">A1</a></div>
            <div id="b"><a href="/x/2">B1</a></div>
            """;

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://example.com/list");

        Assert.Equal(2, chapters.Count);
        Assert.Equal(new[] { "A1", "B1" }, chapters.Select(c => c.ChapterName));
    }

    // ---------- ⑤ 方括号索引 ----------

    [Theory]
    [InlineData("class.item[0,2]", new[] { "一", "三" })]
    [InlineData("class.item[1:2]", new[] { "二", "三" })]
    [InlineData("class.item[-1:0]", new[] { "三", "二", "一" })]
    [InlineData("class.item[!0,1]", new[] { "三" })]
    [InlineData("class.item[0:-1:2]", new[] { "一", "三" })]
    public void Bracket_Index_Spec_Matches_Legado(string listRule, string[] expected)
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "索引（测试）",
            RuleToc = new TocRule
            {
                ChapterList = listRule,
                ChapterName = "text",
                ChapterUrl = "href",
            },
        };

        const string html = """
            <div class="item" href="/c/1">一</div>
            <div class="item" href="/c/2">二</div>
            <div class="item" href="/c/3">三</div>
            """;

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://example.com/list");

        Assert.Equal(expected, chapters.Select(c => c.ChapterName));
    }

    // ---------- ⑥ 属性 HTML 实体解码 ----------

    [Fact]
    public void Chapter_Url_Attribute_Html_Entity_Is_Decoded()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://cn.webmota.com",
            BookSourceName = "实体（测试）",
            RuleToc = new TocRule
            {
                ChapterList = "class.item",
                ChapterName = "span@text",
                ChapterUrl = "a@href",
            },
        };

        const string html = """
            <div class="item"><a href="/p?comic_id=dl4&amp;section_slot=0&amp;chapter_slot=1"><span>序章</span></a></div>
            """;

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://cn.webmota.com/comic/dl4");

        var url = Assert.Single(chapters).ChapterUrl!;
        Assert.Contains("comic_id=dl4&section_slot=0&chapter_slot=1", url);
        Assert.DoesNotContain("&amp;", url);
    }

    // ---------- ⑦ 图片地址的行内选项块 ----------

    [Fact]
    public void Image_Url_Strips_Inline_Options_Block()
    {
        const string html = """
            <div class="comic-contain">
              <img src="https://img.example.com/1.jpg,{&quot;headers&quot;:{&quot;Referer&quot;:&quot;https://cn.webmota.com/&quot;}}">
              <img src="https://img.example.com/2.jpg">
            </div>
            """;

        var urls = RuleEngine.ExtractImageUrls(html, "https://cn.webmota.com/comic/x");

        Assert.Equal(new[] { "https://img.example.com/1.jpg", "https://img.example.com/2.jpg" }, urls);
    }

    // ---------- ⑧ preUpdateJs 只取副作用 ----------

    [Fact]
    public void Toc_PreUpdateJs_Does_Not_Replace_Body()
    {
        // 包子漫画（优+）真实写法：preUpdateJs = "book.canUpdate=false"
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://cn.bzmanga.com",
            BookSourceName = "preUpdateJs（测试）",
            RuleToc = new TocRule
            {
                PreUpdateJs = "book.canUpdate=false",
                ChapterList = "class.item",
                ChapterName = "text",
                ChapterUrl = "href",
            },
        };

        const string html = """<div class="item" href="/c/1">一</div><div class="item" href="/c/2">二</div>""";

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://cn.bzmanga.com/comic/x");

        Assert.Equal(2, chapters.Count);
    }

    // ---------- ⑨ chapterList 的 || 备用选择器 ----------

    [Fact]
    public void Toc_ChapterList_Supports_Or_Fallback_Selector()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "备用选择器（测试）",
            RuleToc = new TocRule
            {
                ChapterList = "class.not-exist@class.item||class.item",
                ChapterName = "text",
                ChapterUrl = "href",
            },
        };

        const string html = """<div class="item" href="/c/1">一</div><div class="item" href="/c/2">二</div>""";

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://example.com/list");

        Assert.Equal(2, chapters.Count);
    }

    [Fact]
    public void Toc_ChapterList_Supports_And_Union_Of_Containers()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "并集（测试）",
            RuleToc = new TocRule
            {
                ChapterList = "class.list-a@class.item&&class.list-b@class.item",
                ChapterName = "text",
                ChapterUrl = "href",
            },
        };

        const string html = """
            <div class="list-a"><div class="item" href="/c/1">一</div></div>
            <div class="list-b"><div class="item" href="/c/2">二</div></div>
            """;

        var (chapters, _) = NewEngine().ParseTocPage(source, html, "https://example.com/list");

        Assert.Equal(new[] { "一", "二" }, chapters.Select(c => c.ChapterName));
    }
}

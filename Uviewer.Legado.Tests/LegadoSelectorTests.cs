using System.Collections.Generic;
using System.Linq;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// legado 选择器语法（对标 reader <c>parser/html.rs</c>）：<c>class./id./tag.</c> 缩写、
/// <c>@</c> 链式下钻、索引后缀、<c>textNodes</c> 轴。
///
/// <para><b>为什么必须锁死</b>：书源正文规则的绝大多数写法都是这套 legado 专有语法，
/// 盲用标准 CSS 会得到"请求成功但正文恒空"——目录规则往往只用单段选择器，
/// 于是表现为"目录正常、正文空"，用户会以为书源坏了。</para>
/// </summary>
public class LegadoSelectorTests
{
    // 一条真实章节页的骨架（含正文/标题/下一页，等价于书源抓到的 body）
    private const string ContentPage = """
<html><body>
<div class="bookname"><h1>第一章 开端</h1></div>
<div class="content" id="content">
<p>　　第一段正文。</p>
<p>　　第二段正文。</p>
<p>　　第三段正文。</p>
</div>
<div class="bottem"><a href="/book/2.html">下一章</a></div>
</body></html>
""";

    // ---------- 1. 缩写 ----------

    [Theory]
    [InlineData("class.content", ".content")]
    [InlineData("class.mod block", ".mod.block")]
    [InlineData("id.content", "#content")]
    [InlineData("tag.div", "div")]
    [InlineData(".content", ".content")]
    [InlineData("#nr1", "#nr1")]
    public void ToCss_Translates_Legado_Shorthand(string legado, string expected)
    {
        Assert.Equal(expected, LegadoCss.ToCss(legado));
    }

    [Fact]
    public void Shorthand_Class_Selector_Finds_Node()
    {
        // 旧实现把 class.content 当标签名 → 恒空
        var values = Selectors.FieldValues("class.content@html", ContentPage);
        Assert.Single(values);
        Assert.Contains("第一段正文", values[0]);
    }

    [Fact]
    public void Shorthand_Multiple_Classes_Require_All()
    {
        const string html = """<div class="a b">命中</div><div class="a">不命中</div>""";
        var values = Selectors.FieldValues("class.a b", html);
        Assert.Single(values);
        Assert.Equal("命中", values[0]);
    }

    // ---------- 2. 链式 @ 下钻（正文为空的元凶） ----------

    [Fact]
    public void Chain_P_Tag_Then_Html_Takes_Paragraph_InnerHtml()
    {
        // @html = 元素的 innerHtml（Jsoup element.html() 语义）
        var values = Selectors.FieldValues(".content@p@html", ContentPage);

        Assert.Equal(3, values.Count);
        Assert.Equal("　　第一段正文。", values[0]);
        Assert.Contains("第三段正文", values[2]);
    }

    [Fact]
    public void Chain_Shorthand_With_TextNodes_Joins_By_Newline()
    {
        // 用户书源原样规则：class.content@p@textNodes
        var values = Selectors.FieldValues("class.content@p@textNodes", ContentPage);

        Assert.Equal(3, values.Count);
        // textNodes 逐个去空白（reader get_text_nodes 同语义），首尾缩进被裁掉
        Assert.Equal("第一段正文。", values[0]);
        Assert.Equal("第三段正文。", values[2]);
    }

    [Fact]
    public void Chain_Index_Picks_Nth_Node()
    {
        const string html = """
<div class="pic"><img src="a.jpg"/></div>
<div class="pic"><img src="b.jpg"/></div>
""";
        // 用户书源原样规则：class.pic.0@tag.img@src
        var values = Selectors.FieldValues("class.pic.0@tag.img@src", html);
        Assert.Single(values);
        Assert.Equal("a.jpg", values[0]);
    }

    [Fact]
    public void Chain_Exclude_Index_Drops_That_Node()
    {
        const string html = """<ul class="l"><li>一</li><li>二</li><li>三</li></ul>""";
        var values = Selectors.FieldValues("class.l@tag.li!1@textNodes", html);
        Assert.Equal(new[] { "一", "三" }, values);
    }

    [Fact]
    public void Chain_Last_Part_Falls_Back_To_Selector_When_Not_An_Axis()
    {
        // 末段 "tag.a" 不是取值轴 → 当选择器，取其文本（reader find_map 语义）
        const string html = """<div class="box"><a href="/x">链接文本</a></div>""";
        var values = Selectors.FieldValues("class.box@tag.a", html);
        Assert.Single(values);
        Assert.Equal("链接文本", values[0]);
    }

    [Fact]
    public void Chain_Attribute_Extractor_On_Last_Part()
    {
        const string html = """<div class="box"><a href="/x">链接</a></div>""";
        var values = Selectors.FieldValues("class.box@tag.a@href", html);
        Assert.Single(values);
        Assert.Equal("/x", values[0]);
    }

    // ---------- 3. textNodes 轴 ----------

    [Fact]
    public void TextNodes_Joins_Paragraphs_With_Newlines()
    {
        // body@textNodes：用户书源原样规则
        var values = Selectors.FieldValues("body@textNodes", ContentPage);

        Assert.Single(values);
        var text = values[0];
        // 段落之间必须是换行，不能糊成一行
        Assert.Contains("第一段正文。\n第二段正文。", text);
    }

    [Fact]
    public void Single_Selector_Without_Axis_Uses_TextNodes()
    {
        // reader select_all_text：单段无轴 → textNodes（不是 InnerText）
        var values = Selectors.FieldValues("class.content", ContentPage);

        Assert.Single(values);
        Assert.Contains("第一段正文。\n第二段正文。", values[0]);
    }

    [Fact]
    public void Text_Axis_Is_Space_Joined_Visible_Text()
    {
        var values = Selectors.FieldValues("class.content@p@text", ContentPage);
        Assert.Equal(3, values.Count);
        Assert.Equal("　　第一段正文。", values[0]);
    }

    // ---------- 4. 列表模式（目录/搜索结果） ----------

    [Fact]
    public void ListMode_Chain_Selects_Elements()
    {
        const string html = """
<ul class="chapter-list">
  <li><a href="/1.html">第一章</a></li>
  <li><a href="/2.html">第二章</a></li>
</ul>
""";
        // 用户书源常见写法：class.chapter-list@tag.li
        var fragments = Selectors.ListFragments("class.chapter-list@tag.li", html);
        Assert.Equal(2, fragments.Count);
        Assert.Contains("第一章", fragments[0]);
    }

    [Fact]
    public void ListMode_Drops_Trailing_Extractor()
    {
        const string html = """<ul class="l"><li><a href="/1.html">第一章</a></li><li><a href="/2.html">第二章</a></li></ul>""";

        // 末段 @text 是取值轴 → 列表项仍是元素（a），不能把它当选择器
        var fragments = Selectors.ListFragments("class.l@tag.li@tag.a@text", html);
        Assert.Equal(2, fragments.Count);
        Assert.Contains("href=\"/1.html\"", fragments[0]);
    }

    [Fact]
    public void ListMode_Index_Applies_To_Selected_Nodes()
    {
        const string html = """<div class="pic"><img src="a.jpg"/></div><div class="pic"><img src="b.jpg"/></div>""";
        var fragments = Selectors.ListFragments("class.pic.1", html);
        Assert.Single(fragments);
        Assert.Contains("b.jpg", fragments[0]);
    }

    // ---------- 5. 端到端：真实规则形态 → ParseContent ----------

    [Fact]
    public void ParseContent_Works_For_Every_Real_Rule_Shape()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            BookSourceName = "t",
            RuleContent = new ContentRule { Content = "class.content@p@textNodes" },
        };

        var engine = new RuleEngine();
        var text = engine.ParseContent(source, ContentPage, "https://example.com/1.html");

        Assert.Contains("第一段正文", text);
        Assert.Contains("第三段正文", text);
        Assert.Contains("\n", text); // 段落分行
    }

    [Fact]
    public void ParseContent_Empty_Rule_Falls_Back_To_Whole_Body()
    {
        var source = new BookSourceModel
        {
            BookSourceUrl = "https://example.com",
            RuleContent = new ContentRule { Content = string.Empty },
        };

        var text = new RuleEngine().ParseContent(source, ContentPage, "https://example.com/1.html");
        Assert.Contains("第一段正文", text);
    }
}

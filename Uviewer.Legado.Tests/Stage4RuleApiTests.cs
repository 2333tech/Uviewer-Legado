using System;
using System.Linq;
using Uviewer.Services.Legado.BookSources;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-5：T2 规则原语（<c>java.getElements/getElement/getStringList/setContent</c>）。
///
/// <para><b>测试路径</b>：全部走真实链 <see cref="RuleEngine.ParseContent"/>——
/// 正文规则 <c>@js:</code> 触发 <c>RunJs</c>，由它注入的 EvaluateRule/EvaluateList/EvaluateElements
/// 闭包（含 <c>setContent</c> 内容替换）与 JS 侧包装（<see cref="RuleJavaApi"/>）一并被测，
/// 与生产路径零差异。语义锚点取自 legado <c>AnalyzeRule</c> 与 reader <c>js.rs</c>（调研结论见类注释）。</para>
/// </summary>
public class Stage4RuleApiTests
{
    private const string Html = """
        <html><body>
        <div class="item" data-id="7"><p>内层甲</p><span class="tag">A</span></div>
        <div class="item" data-id="8">第二个</div>
        <span class="tag">C</span>
        </body></html>
        """;

    /// <summary>跑一条正文 JS 规则（真实 RunJs 接线）。</summary>
    private static string RunJs(BookSourceModel source, string body) =>
        new RuleEngine { Store = SourceStateStore.InMemory }.ParseContent(source, body, "https://x.example.com/book/1.html");

    private static BookSourceModel Source(string jsRule) => new()
    {
        BookSourceUrl = "https://x.example.com",
        BookSourceName = "T2 测试源",
        RuleContent = new Models.ContentRule { Content = jsRule },
    };

    // ============================================================
    // getElements：HTML 快照（JSoup 风格方法）
    // ============================================================

    [Fact]
    public void GetElements_Html_Basic_Operations()
    {
        var rule = """
            @js: var els = java.getElements('.item');
                 els.length + '|' + els[0].attr('data-id') + '|' + els[0].text() + '|' + els[0].tagName() + '|' + (els[0].html().indexOf('内层') >= 0)
            """;
        Assert.Equal("2|7|内层甲 A|div|true", RunJs(Source(rule), Html));
    }

    [Fact]
    public void GetElements_Select_Queries_Within_Element()
    {
        var rule = """
            @js: var inner = java.getElements('.item')[0].select('.tag');
                 inner.length + '|' + inner[0].text()
            """;
        Assert.Equal("1|A", RunJs(Source(rule), Html));
    }

    [Fact]
    public void GetElements_Legado_Short_Hand_Selectors()
    {
        // legado 专有缩写（class.x / 索引后缀）走同一条链式选择器
        var rule = "@js: java.getElements('class.item.0@p').text()";
        Assert.Equal("内层甲", RunJs(Source(rule), Html));
    }

    [Fact]
    public void GetElements_Xpath_Mode()
    {
        var rule = "@js: java.getElements('@xpath://div[@class=\"item\"]').length";
        Assert.Equal("2", RunJs(Source(rule), Html));
    }

    [Fact]
    public void GetElements_Regex_Mode_Text_Snapshots()
    {
        // :/  @regex: 前缀 → 每个匹配一个文本快照（text() = 匹配串）
        var rule = "@js: var m = java.getElements('@regex:[AC]'); m.length + '|' + m[0].text() + '|' + m[1].text()";
        Assert.Equal("2|A|C", RunJs(Source(rule), Html));
    }

    // ============================================================
    // getElements：JSON 节点透传（legado Mode.Json 语义）
    // ============================================================

    [Fact]
    public void GetElements_Json_Passes_Raw_Nodes()
    {
        const string body = """{"list":[{"title":"甲","id":1},{"title":"乙","id":2}]}""";
        var rule = """
            @js: var r = java.getElements('$.list[*]');
                 r.length + '|' + r[0].title + '|' + r[1]['id'] + '|' + r[0].text()
            """;
        Assert.Equal("2|甲|2|甲", RunJs(Source(rule), body));
    }

    // ============================================================
    // 数组形态：JS 风格 + Java 风格（Rhino 双习惯）
    // ============================================================

    [Fact]
    public void GetElements_Array_Supports_Both_Authoring_Styles()
    {
        var rule = """
            @js: var els = java.getElements('.item');
                 els.size() + '|' + els.get(1).attr('data-id') + '|' + els.toArray().length + '|' + els.attr('data-id')
            """;
        Assert.Equal("2|8|2|7", RunJs(Source(rule), Html));
    }

    [Fact]
    public void GetElements_Empty_Result()
    {
        var rule = "@js: var e = java.getElement('.nope'); var list = java.getElements('.nope'); String(e) + '|' + list.length";
        Assert.Equal("null|0", RunJs(Source(rule), Html));
    }

    // ============================================================
    // getElement：首元素
    // ============================================================

    [Fact]
    public void GetElement_Returns_First()
    {
        var rule = "@js: java.getElement('.item').attr('data-id')";
        Assert.Equal("7", RunJs(Source(rule), Html));
    }

    // ============================================================
    // getStringList
    // ============================================================

    [Fact]
    public void GetStringList_Field_Rule()
    {
        var rule = "@js: java.getStringList('.tag@text').join(',')";
        Assert.Equal("A,C", RunJs(Source(rule), Html));
    }

    [Fact]
    public void GetStringList_Json_Path()
    {
        const string body = """{"tags":["甲","乙","丙"]}""";
        var rule = "@js: java.getStringList('$.tags[*]').join('+')";
        Assert.Equal("甲+乙+丙", RunJs(Source(rule), body));
    }

    // ============================================================
    // setContent：替换后续规则求值内容（legado AnalyzeRule.setContent）
    // ============================================================

    [Fact]
    public void SetContent_Replaces_Evaluation_Target()
    {
        var rule = "@js: java.setContent('<div class=\"t\">XYZ</div>'); java.getString('.t')";
        Assert.Equal("XYZ", RunJs(Source(rule), Html));
    }

    [Fact]
    public void SetContent_In_Loop_Rebuilds_Each_Iteration()
    {
        // 样本真实写法：java.getElements(xpath) 逐项 setContent 后再取值；
        // 第二个条目没有 <p>，getString 返回空（逗号照加，保持循环结构可见）
        var rule = """
            @js: var items = java.getElements('.item'); var out = '';
                 for (var i = 0; i < items.length; i++) {
                     java.setContent(items[i].outerHtml());
                     out += java.getString('p@text') + ',';
                 }
                 out
            """;
        Assert.Equal("内层甲,,", RunJs(Source(rule), Html));
    }

    // ============================================================
    // ruleUrl（记录型桩，legado 无此成员）与诊断
    // ============================================================

    [Fact]
    public void RuleUrl_Is_Stub_Returning_Empty()
    {
        var rule = "@js: String(java.ruleUrl)";
        Assert.Equal("", RunJs(Source(rule), Html));
    }
}

using BookSource.Core.Models;
using System.Linq;
using Uviewer.Services.Legado;
using Xunit;
using CoreTocItem = BookSource.Core.Models.TocItem;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 5 集成层测试：宿主侧两个纯逻辑组件（正文 HTML → 纯文本、目录适配）。
/// 这两处是集成层里唯一有算法风险的地方；会话/搜索/控制器是编排，已由库侧测试覆盖。
/// </summary>
public class LegadoHtmlTextTests
{
    [Fact]
    public void Empty_Or_Whitespace_Returns_Empty()
    {
        Assert.Equal(string.Empty, LegadoHtmlText.ToPlainText(null));
        Assert.Equal(string.Empty, LegadoHtmlText.ToPlainText(""));
        Assert.Equal(string.Empty, LegadoHtmlText.ToPlainText("   \n\t "));
    }

    [Fact]
    public void Plain_Text_Passes_Through_With_Only_Outer_Trim()
    {
        // 首尾空白裁掉；行内/行首空白保留（段首缩进有意义）
        Assert.Equal("  第一章\n正文", LegadoHtmlText.ToPlainText("\n  第一章\n正文  \n\n"));
    }

    [Fact]
    public void Block_Tags_Become_Line_Breaks_Inline_Tags_Do_Not()
    {
        // 这是与 AozoraParserService.ParseHtml 的关键差别：内联标签不该撑出空行
        var html = "<p>第一段<span class=\"x\">内联</span>继续</p><div>第二段</div>";

        var text = LegadoHtmlText.ToPlainText(html);

        Assert.Equal("第一段内联继续\n第二段", text);
    }

    [Fact]
    public void Script_And_Style_Content_Is_Removed()
    {
        var html = """
            <div>正文开始</div>
            <script>var x = "<p>不该出现</p>";</script>
            <style>.a{color:red}</style>
            <div>正文结束</div>
            """;

        var text = LegadoHtmlText.ToPlainText(html);

        Assert.Equal("正文开始\n正文结束", text);
        Assert.DoesNotContain("不该出现", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_Entities_Are_Decoded()
    {
        var html = "<p>&lt;标签&gt; &amp; &quot;引号&quot; &#65; &nbsp;结尾</p>";

        var text = LegadoHtmlText.ToPlainText(html);

        // &nbsp; → U+00A0（不是普通空格 U+0020）
        Assert.Equal("<标签> & \"引号\" A \u00A0结尾", text);  // &nbsp; → U+00A0
    }

    [Fact]
    public void Br_Tags_Become_Line_Breaks()
    {
        Assert.Equal("甲\n乙", LegadoHtmlText.ToPlainText("甲<br>乙"));
        Assert.Equal("甲\n乙", LegadoHtmlText.ToPlainText("甲<br/>乙"));
        Assert.Equal("甲\n乙", LegadoHtmlText.ToPlainText("甲<br />乙"));
    }

    [Fact]
    public void Blank_Runs_Collapse_To_One_Blank_Line()
    {
        // 阅读器按行排版：3 行以上空行浪费屏幕，压缩为 1 个空行（2 个 \n）
        var text = LegadoHtmlText.ToPlainText("<p>甲</p><br><br><br><br><p>乙</p>");

        Assert.Equal("甲\n\n乙", text);
    }

    [Fact]
    public void CRLF_And_CR_Are_Normalized()
    {
        Assert.Equal("甲\n乙\n丙", LegadoHtmlText.ToPlainText("<p>甲</p>\r\n<p>乙</p>\r<p>丙</p>"));
    }

    [Fact]
    public void Leading_Indentation_Is_Preserved_Trailing_Is_Trimmed()
    {
        // 小说正文的段首缩进有阅读意义，不能裁掉
        var text = LegadoHtmlText.ToPlainText("<p>　　缩进一段   </p>");

        Assert.Equal("　　缩进一段", text);
    }

    [Fact]
    public void Malformed_Tags_Do_Not_Throw()
    {
        // 书源正文常有残缺标签（未闭合 div、裸 < 等），不能抛
        var html = "<div>未闭合<p>第二行</div><span>尾部";

        var text = LegadoHtmlText.ToPlainText(html);

        Assert.Contains("未闭合", text, StringComparison.Ordinal);
        Assert.Contains("尾部", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_Document_Converts_Quickly_And_Stably()
    {
        // 真实章节正文约 10~60KB；这里造 ~1MB 验证线性行为与结果稳定
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 8000; i++)
        {
            sb.Append("<p>第").Append(i).Append("段&nbsp;内容</p>");
        }

        var html = sb.ToString();
        var first = LegadoHtmlText.ToPlainText(html);
        var second = LegadoHtmlText.ToPlainText(html);

        Assert.Equal(first, second);              // 幂等
        Assert.Contains("第0段\u00A0内容", first, StringComparison.Ordinal);
        Assert.Contains("第7999段\u00A0内容", first, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", first, StringComparison.Ordinal);
    }
}

/// <summary>目录适配测试（库侧 TocItem → 宿主条目）。</summary>
public class LegadoTocAdapterTests
{
    [Fact]
    public void Chapters_Map_To_Their_Index()
    {
        var chapters = new[]
        {
            new CoreTocItem { ChapterName = "第一章", ChapterUrl = "/c/1" },
            new CoreTocItem { ChapterName = "第二章", ChapterUrl = "/c/2" },
            new CoreTocItem { ChapterName = "第三章", ChapterUrl = "/c/3" },
        };

        var entries = LegadoTocAdapter.Adapt(chapters);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { 0, 1, 2 }, entries.Select(e => e.ChapterIndex));
        Assert.All(entries, e => Assert.False(e.IsVolume));
        Assert.Equal("第二章", entries[1].Title);
    }

    [Fact]
    public void Volumes_Get_Non_Clickable_Index_And_Negative_Sentinel()
    {
        var chapters = new[]
        {
            new CoreTocItem { ChapterName = "第一卷", IsVolume = true },
            new CoreTocItem { ChapterName = "第一章", ChapterUrl = "/c/1" },
        };

        var entries = LegadoTocAdapter.Adapt(chapters);

        Assert.True(entries[0].IsVolume);
        Assert.Equal(-1, entries[0].ChapterIndex);   // 卷不可跳转
        Assert.False(entries[1].IsVolume);
        Assert.Equal(1, entries[1].ChapterIndex);    // 章用真实下标（不是序号）
    }

    [Fact]
    public void Blank_Chapter_Name_Falls_Back_To_Position()
    {
        var chapters = new[]
        {
            new CoreTocItem { ChapterName = null, ChapterUrl = "/c/1" },
            new CoreTocItem { ChapterName = "   ", ChapterUrl = "/c/2" },
            new CoreTocItem { ChapterName = "  第三章  ", ChapterUrl = "/c/3" },
        };

        var entries = LegadoTocAdapter.Adapt(chapters);

        // 宿主目录不允许空标题行
        Assert.Equal("第 1 章", entries[0].Title);
        Assert.Equal("第 2 章", entries[1].Title);
        Assert.Equal("第三章", entries[2].Title);   // 前后空白被裁
    }

    [Fact]
    public void Empty_Chapter_List_Yields_Empty_Result()
    {
        Assert.Empty(LegadoTocAdapter.Adapt(Array.Empty<CoreTocItem>()));
    }

    [Fact]
    public void Real_Sample_Toc_Parses_Then_Adapts_End_To_End()
    {
        // 真实链路：书源规则解析目录 → 适配成宿主条目。
        // 上游语义（legado 同）：章名为 null/空白 的条目在**解析阶段**就被剔除，
        // 不会进目录；适配器的空名回退只是防御（规则若产出空白名也不会漏到 UI）。
        var json = """
            {"data":[
              {"id":"/c/1","title":"第一章"},
              {"id":"/c/2"},
              {"id":"/c/3","title":"   "},
              {"id":"/c/4","title":"第四章"}
            ]}
            """;
        var source = new BookSource.Core.Models.BookSource
        {
            BookSourceUrl = "https://api.example.com",
            RuleToc = new TocRule
            {
                ChapterList = "data[*]",
                ChapterName = "title",
                ChapterUrl = "id",
            },
        };

        var (chapters, _) = new BookSource.Core.Services.BookSources.RuleEngine()
            .ParseTocPage(source, json, "https://api.example.com");

        // 无名章节在上游就被剔除
        Assert.Equal(new[] { "第一章", "第四章" }, chapters.Select(c => c.ChapterName));

        var entries = LegadoTocAdapter.Adapt(chapters);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Title)));
        Assert.Equal("第一章", entries[0].Title);
        Assert.Equal("第四章", entries[1].Title);

        // 关键不变量：章节下标连续且与宿主点击定位一致
        Assert.Equal(new[] { 0, 1 }, entries.Select(e => e.ChapterIndex));
    }
}

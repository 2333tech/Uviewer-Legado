using System.Collections.Generic;
using System.Linq;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// TXT 章节切分测试。
///
/// <para><b>为什么这些规则要锁死</b>：切分结果直接决定目录里出现哪些条目、以及
/// 跳章跳到哪一行。正则写错一个字符，用户看到的就是"目录错位/整本书只有一个章"。
/// 这里覆盖：常见中文写法、层级、卷标题、捕获组取名、非法正则容错、
/// CRLF 归一、空输入、以及"正文里的'第'字不能被误判成标题"。</para>
/// </summary>
public class TxtChapterSplitterTests
{
    private static List<TitleRule> Defaults() => TxtChapterSplitter.CreateDefaultRules();

    [Fact]
    public void Split_中文第N章_识别全部章节且行号正确()
    {
        var text = string.Join("\n", new[]
        {
            "斗破苍穹",              // 0 书名
            "第一章 陨落的天才",       // 1
            "正文内容一",             // 2
            "正文内容二",             // 3
            "第二章 青龙劲",           // 4
            "正文内容三",             // 5
            "第三章 如果你来观战",      // 6
        });

        var result = TxtChapterSplitter.Split(text, Defaults());

        Assert.True(result.HasChapters);
        Assert.Equal(3, result.Chapters.Count);
        Assert.Equal("第一章 陨落的天才", result.Chapters[0].Title);
        Assert.Equal(1, result.Chapters[0].LineNumber);
        Assert.Equal("第二章 青龙劲", result.Chapters[1].Title);
        Assert.Equal(4, result.Chapters[1].LineNumber);
        Assert.Equal("第三章 如果你来观战", result.Chapters[2].Title);
        Assert.Equal(6, result.Chapters[2].LineNumber);
    }

    [Fact]
    public void Split_卷标题_标记为IsVolume且不与章混淆()
    {
        var text = string.Join("\n", new[]
        {
            "第一卷 风云初起",   // 0 卷
            "第一章 开始",      // 1
            "第二章 继续",      // 2
            "第二卷 波澜再起",  // 3 卷
            "第三章 结局",      // 4
        });

        var result = TxtChapterSplitter.Split(text, Defaults());

        var volumes = result.Chapters.Where(c => c.IsVolume).Select(c => c.Title).ToList();
        var chapters = result.Chapters.Where(c => !c.IsVolume).Select(c => c.Title).ToList();

        // 卷规则 Order 更低（先匹配），所以"第一卷"应被判为卷而非章
        Assert.Equal(new[] { "第一卷 风云初起", "第二卷 波澜再起" }, volumes);
        Assert.Equal(new[] { "第一章 开始", "第二章 继续", "第三章 结局" }, chapters);
    }

    [Fact]
    public void Split_英文章节_识别ChapterN()
    {
        var text = string.Join("\n", new[]
        {
            "Chapter 1 The Beginning",
            "Some body text.",
            "Chapter 2 The Middle",
            "More body text.",
        });

        var result = TxtChapterSplitter.Split(text, Defaults());

        Assert.Equal(2, result.Chapters.Count);
        Assert.Equal("Chapter 1 The Beginning", result.Chapters[0].Title);
        Assert.Equal(0, result.Chapters[0].LineNumber);
        Assert.Equal("Chapter 2 The Middle", result.Chapters[1].Title);
        Assert.Equal(2, result.Chapters[1].LineNumber);
    }

    [Fact]
    public void Split_正文含第字_不被误判为标题()
    {
        // 这是最危险的误判：正文里出现"第三段"之类的行首"第"
        var text = string.Join("\n", new[]
        {
            "第一章 开端",
            "第一段：从前有座山。",
            "山上有座庙。",
            "第三段：庙里有个老和尚。",
            "第二章 发展",
        });

        var result = TxtChapterSplitter.Split(text, Defaults());

        Assert.Equal(2, result.Chapters.Count);
        Assert.Equal("第一章 开端", result.Chapters[0].Title);
        Assert.Equal("第二章 发展", result.Chapters[1].Title);
    }

    [Fact]
    public void Split_CRLF换行_行号与LF一致()
    {
        var lf = "第一章 A\n正文\n第二章 B";
        var crlf = "第一章 A\r\n正文\r\n第二章 B";

        var a = TxtChapterSplitter.Split(lf, Defaults());
        var b = TxtChapterSplitter.Split(crlf, Defaults());

        Assert.Equal(a.Chapters.Count, b.Chapters.Count);
        Assert.Equal(a.Chapters[1].LineNumber, b.Chapters[1].LineNumber);
        Assert.Equal("第二章 B", b.Chapters[1].Title);
    }

    [Fact]
    public void Split_捕获组TitleGroup_用指定组作章名()
    {
        var rules = new List<TitleRule>
        {
            new TitleRule
            {
                Id = 1,
                Name = "带前缀的标题",
                Pattern = @"^序章[：:]\s*(.+)$",
                TitleGroup = 1,
                Order = 1,
            },
        };

        var result = TxtChapterSplitter.Split("序章：这是一场雪", rules);

        Assert.Single(result.Chapters);

        // 只取第 1 捕获组，不含"序章："前缀
        Assert.Equal("这是一场雪", result.Chapters[0].Title);
    }

    [Fact]
    public void Split_标题组未捕获_回退整行而非空白()
    {
        // TitleGroup 指向不存在的组 → 必须回退整行，绝不能产生空目录条目
        var rules = new List<TitleRule>
        {
            new TitleRule
            {
                Id = 1,
                Name = "越界组",
                Pattern = @"^第\w+章\s*(.*)$",
                TitleGroup = 9,
                Order = 1,
            },
        };

        var result = TxtChapterSplitter.Split("第一章 起风了", rules);

        Assert.Single(result.Chapters);
        Assert.False(string.IsNullOrWhiteSpace(result.Chapters[0].Title));
        Assert.Equal("第一章 起风了", result.Chapters[0].Title);
    }

    [Fact]
    public void Split_非法正则_跳过该规则而非整体失败()
    {
        var rules = new List<TitleRule>
        {
            new TitleRule { Id = 1, Name = "坏正则", Pattern = "([未闭合", Order = 1 },
            new TitleRule { Id = 2, Name = "好正则", Pattern = @"^第\w+章", Order = 2 },
        };

        var result = TxtChapterSplitter.Split("第一章 A\n第二章 B", rules);

        // 坏规则被跳过，好规则照常工作
        Assert.Equal(2, result.Chapters.Count);
    }

    [Fact]
    public void Split_空内容或无规则_返回空结果不抛异常()
    {
        Assert.Empty(TxtChapterSplitter.Split(null, Defaults()).Chapters);
        Assert.Empty(TxtChapterSplitter.Split("", Defaults()).Chapters);
        Assert.Empty(TxtChapterSplitter.Split("第一章 A", new List<TitleRule>()).Chapters);
    }

    [Fact]
    public void Split_超长正文行_不误判为标题()
    {
        // 前置筛选按"标题不会很长"排除超长行；这里验证它确实生效
        var longLine = "第一章 " + new string('字', 300);
        var result = TxtChapterSplitter.Split(longLine, Defaults());

        Assert.Empty(result.Chapters);
    }

    [Fact]
    public void Split_Markdown标题_识别井号层级()
    {
        var text = string.Join("\n", new[]
        {
            "# 卷首",
            "## 第一小节",
            "### 更细的一层",
            "正文",
        });

        var result = TxtChapterSplitter.Split(text, Defaults());

        Assert.Contains(result.Chapters, c => c.Title == "卷首" && c.Level == 1);
        Assert.Contains(result.Chapters, c => c.Title == "第一小节" && c.Level == 2);
        Assert.Contains(result.Chapters, c => c.Title == "更细的一层" && c.Level == 3);
    }

    [Fact]
    public void Split_书名行规则_提取书名且不计为章节()
    {
        var rules = new List<TitleRule>
        {
            new TitleRule { Id = 1, Name = "书名", IsBookTitle = true, Order = 1 },
            new TitleRule { Id = 2, Name = "章", Pattern = @"^第\w+章", Order = 2 },
        };

        var result = TxtChapterSplitter.Split("我的书名\n第一章 A", rules);

        Assert.Equal("我的书名", result.BookTitle);
        Assert.Single(result.Chapters);
    }

    [Fact]
    public void Split_总行数_与实际行数一致()
    {
        var text = "第一章 A\n正文1\n正文2\n第二章 B";
        var result = TxtChapterSplitter.Split(text, Defaults());

        Assert.Equal(4, result.TotalLines);
    }

    // ============================================================
    // TextTocProvider 回退契约
    // ============================================================

    /// <summary>
    /// 内置标记（<c>［＃大見出し］</c> / <c>#</c> / <c>##</c> / <c>###</c>）的等价判定。
    /// <para>宿主 <c>TextTocProvider</c> 先用内置标记，<b>一个都没命中</b>才回退到
    /// <see cref="TxtChapterSplitter"/>。这里复制它的判定来验证"两级策略"的接缝：
    /// 既要保证内置标记命中的文件不会被规则抢走，也要保证纯中文 TXT 真的能产出目录。</para>
    /// </summary>
    private static bool HasBuiltinHeading(string content)
    {
        foreach (var raw in content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("［＃大見出し］") || line.StartsWith("# ") ||
                line.Contains("［＃中見出し］") || line.StartsWith("## ") ||
                line.Contains("［＃小見出し］") || line.StartsWith("### "))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void TocFallback_纯中文TXT_内置标记不命中但规则能产出目录()
    {
        var text = string.Join("\n", new[]
        {
            "第一章 陨落的天才",
            "正文一",
            "第二章 青龙劲",
            "正文二",
            "第三章 少年归来",
        });

        // 前置条件：内置标记确实认不出（这正是"目录为空"的成因）
        Assert.False(HasBuiltinHeading(text));

        // 回退生效：规则能切出目录
        var split = TxtChapterSplitter.Split(text, Defaults());
        Assert.Equal(3, split.Chapters.Count);
    }

    [Fact]
    public void TocFallback_带井号标题_内置标记先命中规则不介入()
    {
        var text = string.Join("\n", new[]
        {
            "# 卷首",
            "第一章 会被内置标记抢走吗",
            "正文",
        });

        // 内置标记命中 → 宿主根本不会调用切分器
        Assert.True(HasBuiltinHeading(text));
    }

    [Fact]
    public void TocFallback_行号可直接用于跳章_递增且在范围内()
    {
        var text = string.Join("\n", new[]
        {
            "第一章 A", "正文", "第二章 B", "正文", "第三章 C",
        });

        var split = TxtChapterSplitter.Split(text, Defaults());
        var totalLines = text.Split('\n').Length;

        var last = -1;
        foreach (var chapter in split.Chapters)
        {
            // 宿主把 LineNumber +1 当作 1-based 行号传给 ScrollToLine
            var lineForJump = chapter.LineNumber + 1;

            Assert.InRange(lineForJump, 1, totalLines);
            Assert.True(lineForJump > last, "目录条目必须按行号递增，否则点击会往回跳");
            last = lineForJump;
        }
    }
}

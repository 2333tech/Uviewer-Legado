using BookSource.Core.Models;
using Uviewer.Services.Legado;
using Xunit;
using TitleRule = BookSource.Core.Models.TitleRule;

namespace Uviewer.Legado.Tests;

/// <summary>
/// "打开文件时进行分章 + 生成目录"的行为测试。
///
/// <para><b>为什么测这个</b>：整本下载下来的书是纯文本，目录完全依赖
/// <see cref="TextTocBuilder"/> 按标题规则切分。用户打开这种文件时若目录是空的，
/// 就等于"下了一本书却不能跳章"。</para>
///
/// <para>本组锁定：
/// ① 标题规则能把中文 TXT 切成章；② 无标题行时目录为空（不硬造目录）；
/// ③ 内置标记（# / ［＃大見出し］）优先于标题规则；④ 卷标题进目录但不跳章；
/// ⑤ 规则按 Order 排序（卷先于章）。</para>
/// </summary>
public class TextTocBuilderTests
{
    private static TitleRule ChapterRule() => new()
    {
        Name = "中文章节",
        Pattern = @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*章.{0,60}$",
        TitleGroup = 0,
        Level = 1,
        IsVolume = false,
        IsBookTitle = false,
        IsEnabled = true,
        Order = 10,
    };

    private static TitleRule VolumeRule() => new()
    {
        Name = "中文卷",
        Pattern = @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*卷.{0,60}$",
        TitleGroup = 0,
        Level = 1,
        IsVolume = true,
        IsBookTitle = false,
        IsEnabled = true,
        Order = 5,
    };

    [Fact]
    public void 标题规则_能把中文TXT切成章()
    {
        var text = string.Join('\n',
            "第一章 风起",
            "正文一",
            "第二章 云涌",
            "正文二",
            "第三章 雷动",
            "正文三");

        var toc = TextTocBuilder.Build(text, new List<TitleRule> { ChapterRule() });

        Assert.Equal(3, toc.Count);
        Assert.Contains("第一章", toc[0].Title, StringComparison.Ordinal);
        Assert.Contains("第二章", toc[1].Title, StringComparison.Ordinal);
        Assert.Contains("第三章", toc[2].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void 章节行号可用于跳转()
    {
        var text = string.Join('\n',
            "第一章 风起",
            "正文一",
            "第二章 云涌",
            "正文二");

        var toc = TextTocBuilder.Build(text, new List<TitleRule> { ChapterRule() });

        // SourceLineNumber 是 1-based 行号，指向标题所在行
        Assert.Equal(1, toc[0].SourceLineNumber);
        Assert.Equal(3, toc[1].SourceLineNumber);
    }

    [Fact]
    public void 无标题行_目录为空_不硬造目录()
    {
        var text = string.Join('\n', "就是正文", "没有标题", "也没有标题");

        var toc = TextTocBuilder.Build(text, new List<TitleRule> { ChapterRule() });

        Assert.Empty(toc);
    }

    [Fact]
    public void 无规则且无内置标记_目录为空()
    {
        var text = string.Join('\n', "第一章 风起", "正文");

        var toc = TextTocBuilder.Build(text, null);

        Assert.Empty(toc);
    }

    [Fact]
    public void 内置标记优先于标题规则()
    {
        // 同时含内置标记（# 标题）与可匹配标题规则的行 → 内置标记先命中
        var text = string.Join('\n',
            "# Markdown 标题",
            "正文",
            "第一章 会被规则匹配");

        var toc = TextTocBuilder.Build(text, new List<TitleRule> { ChapterRule() });

        // 内置标记命中即不再走规则分支
        Assert.Single(toc);
        Assert.Contains("Markdown", toc[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void 卷标题进目录但不跳章()
    {
        var text = string.Join('\n',
            "第一卷 风起",
            "第一章 风起",
            "正文");

        var toc = TextTocBuilder.Build(text, new List<TitleRule> { VolumeRule(), ChapterRule() });

        Assert.Equal(2, toc.Count);

        // 卷：SourceLineNumber = -1（不可跳章，与 LegadoTocAdapter 约定一致）
        var volume = toc.First(t => t.Title.Contains("第一卷", StringComparison.Ordinal));
        Assert.Equal(-1, volume.SourceLineNumber);
        Assert.Equal(TextTocBuilder.LegadoVolumeTocTag, volume.Tag);

        // 章：正常行号
        var chapter = toc.First(t => t.Title.Contains("第一章", StringComparison.Ordinal));
        Assert.True(chapter.SourceLineNumber > 0);
        Assert.Null(chapter.Tag);
    }

    [Fact]
    public void 规则按Order排序_卷不会被章规则抢先匹配()
    {
        // 关键：章规则（Order=10）若先匹配，会把"第一卷"也当成章（若其 pattern 过宽）。
        // 这里用一个能同时匹配两种标题的"宽规则"来验证 Order 生效。
        var wideRule = new TitleRule
        {
            Name = "宽规则（章与卷都匹配）",
            Pattern = @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*[章卷].{0,60}$",
            TitleGroup = 0,
            Level = 1,
            IsVolume = false,
            IsBookTitle = false,
            IsEnabled = true,
            Order = 10,
        };

        var strictVolume = new TitleRule
        {
            Name = "卷规则（Order 更小，应优先）",
            Pattern = @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*卷.{0,60}$",
            TitleGroup = 0,
            Level = 1,
            IsVolume = true,
            IsBookTitle = false,
            IsEnabled = true,
            Order = 5,
        };

        var text = string.Join('\n',
            "第一卷 风起",
            "第一章 风起",
            "正文");

        var toc = TextTocBuilder.Build(text, new List<TitleRule> { wideRule, strictVolume });

        Assert.Equal(2, toc.Count);

        // 卷规则 Order 更小 → 抢先匹配 → 被识别为卷（不可跳章）
        var volume = toc.First(t => t.Title.Contains("第一卷", StringComparison.Ordinal));
        Assert.Equal(-1, volume.SourceLineNumber);
        Assert.Equal(TextTocBuilder.LegadoVolumeTocTag, volume.Tag);

        // 章仍是章
        var chapter = toc.First(t => t.Title.Contains("第一章", StringComparison.Ordinal));
        Assert.True(chapter.SourceLineNumber > 0);
        Assert.Null(chapter.Tag);
    }

    [Fact]
    public void 整本下载的典型形态_能切出完整章节目录()
    {
        // 模拟下载器写出的 txt 形态："第 N 章 标题" + 空行 + 正文
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 20; i++)
        {
            sb.Append($"第{i}章 标题{i}\n\n");
            sb.Append($"这是第{i}章的正文内容。\n\n");
        }

        var toc = TextTocBuilder.Build(sb.ToString(), new List<TitleRule> { ChapterRule() });

        Assert.Equal(20, toc.Count);
        Assert.Contains("第1章", toc[0].Title, StringComparison.Ordinal);
        Assert.Contains("第20章", toc[19].Title, StringComparison.Ordinal);

        // 行号单调递增（可用于跳章）
        for (var i = 1; i < toc.Count; i++)
        {
            Assert.True(toc[i].SourceLineNumber > toc[i - 1].SourceLineNumber);
        }
    }
}

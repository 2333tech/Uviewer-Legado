using System.Collections.Generic;
using System.Linq;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 替换规则的<b>标题 / 正文双通道</b>（legado <c>scopeTitle</c> / <c>scopeContent</c>）。
///
/// <para>要求：作用于标题的规则必须在"标题规则识别章节行之前"生效，
/// 且不能误伤正文；作用于正文的规则也不能去改标题行。</para>
/// </summary>
public class ReplaceRuleScopeChannelTests
{
    private static ContentPurifier PurifierOf(params ReplaceRule[] rules)
    {
        var p = new ContentPurifier();
        p.Rules = rules;
        return p;
    }

    private static ReplaceRule Rule(
        long id,
        string pattern,
        string replacement,
        bool isRegex = true,
        bool scopeTitle = false,
        bool scopeContent = true,
        string? scope = null,
        int order = 0)
        => new()
        {
            Id = id,
            Name = $"r{id}",
            Pattern = pattern,
            Replacement = replacement,
            IsRegex = isRegex,
            Scope = scope,
            ScopeTitle = scopeTitle,
            ScopeContent = scopeContent,
            Order = order,
            IsEnabled = true,
        };

    [Fact]
    public void Title_Only_Rule_Cleans_Title_And_Leaves_Body_Alone()
    {
        var p = PurifierOf(Rule(1, @"^【[^】]*】\s*", "", scopeTitle: true, scopeContent: false));

        Assert.Equal("第1章 风起", p.ApplyToTitle("【玄幻】第1章 风起", PurifyContext.Default));
        Assert.Equal("【玄幻】第1章 风起", p.Apply("【玄幻】第1章 风起", PurifyContext.Default));
    }

    [Fact]
    public void ApplyToTitle_Removes_Prefix_But_Apply_Leaves_It()
    {
        var p = PurifierOf(Rule(1, @"^【[^】]*】\s*", "", scopeTitle: true, scopeContent: false));

        Assert.Equal("第1章 风起", p.ApplyToTitle("【玄幻】第1章 风起", PurifyContext.Default));
        Assert.Equal("【玄幻】第1章 风起", p.Apply("【玄幻】第1章 风起", PurifyContext.Default));
    }

    [Fact]
    public void Content_Only_Rule_Does_Not_Touch_Title()
    {
        var p = PurifierOf(Rule(2, "广告", ""));

        Assert.Equal("第1章 广告位", p.ApplyToTitle("第1章 广告位", PurifyContext.Default));
        Assert.Equal("第1章 位", p.Apply("第1章 广告位", PurifyContext.Default));
    }

    [Fact]
    public void Title_Builder_Recognizes_Heading_Only_After_Title_Purification()
    {
        var text = "【玄幻】第1章 风起\n正文一\n【玄幻】第2章 云涌\n正文二";
        var rules = new List<TitleRule>
        {
            new()
            {
                Id = 1,
                Name = "第N章",
                Pattern = @"^第[0-9]+章.*$",
                IsEnabled = true,
                Order = 1,
                Level = 1,
            },
        };

        // 不预净化：带前缀的标题行匹配不到标题规则
        var withoutPurify = TextTocBuilder.Build(text, rules);
        Assert.Empty(withoutPurify);

        // 预净化（作用于标题的规则）：先去前缀 → 标题规则命中，且目录名也是净化后的
        var purifier = PurifierOf(Rule(1, @"^【[^】]*】\s*", "", scopeTitle: true, scopeContent: false));
        var withPurify = TextTocBuilder.Build(text, rules, line => purifier.ApplyToTitle(line, PurifyContext.Default));

        Assert.Equal(new[] { "第1章 风起", "第2章 云涌" }, withPurify.Select(e => e.Title).ToArray());
        Assert.Equal(new[] { 1, 3 }, withPurify.Select(e => e.SourceLineNumber).ToArray());
    }
}

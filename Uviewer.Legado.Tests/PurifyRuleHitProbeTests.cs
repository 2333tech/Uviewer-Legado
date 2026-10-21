using System.Linq;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 「替换净化管理」界面「本章生效」标记的判定口径（<see cref="ContentPurifier.FindEffectiveRuleIds"/>）。
///
/// <para>要求：探针结果必须与真实净化管线一致——只有<b>真的改写了文本</b>的启用规则才算生效，
/// 且判定顺序是"按管线顺序滚一遍文本"，因为后面的规则是在前面规则改写后的文本上匹配的。</para>
/// </summary>
public class PurifyRuleHitProbeTests
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
        string? scope = null,
        bool isRegex = false,
        int order = 0,
        bool isEnabled = true)
        => new()
        {
            Id = id,
            Name = $"r{id}",
            Pattern = pattern,
            Replacement = replacement,
            Scope = scope,
            IsRegex = isRegex,
            Order = order,
            IsEnabled = isEnabled,
        };

    [Fact]
    public void Only_Rules_That_Actually_Change_Text_Are_Hits()
    {
        var p = PurifierOf(
            Rule(1, "广告", "[净化]"),          // 命中并改写
            Rule(2, "不存在的串", "x"),          // 不命中
            Rule(3, "广", "广", isRegex: true)); // 命中但结果与原文相同 ⇒ 不算生效

        var hits = p.FindEffectiveRuleIds("广告广告正文", PurifyContext.Default);

        Assert.Equal(new long[] { 1 }, hits.ToArray());
    }

    [Fact]
    public void Rules_Are_Evaluated_Sequentially_On_Rewritten_Text()
    {
        // 规则 2 的匹配串只在规则 1 的产出里出现 ⇒ 两条都必须算命中
        var p = PurifierOf(
            Rule(1, "ab", "c", order: 0),
            Rule(2, "c", "d", order: 1),
            Rule(3, "a", "z", order: 2));   // 文本已被改写，"a" 不再存在 ⇒ 不命中

        var hits = p.FindEffectiveRuleIds("ab", PurifyContext.Default);

        Assert.Equal(new long[] { 1, 2 }, hits.ToArray());
        Assert.Equal("d", p.Apply("ab", PurifyContext.Default));
    }

    [Fact]
    public void Disabled_And_EmptyPattern_Rules_Are_Never_Hits()
    {
        var p = PurifierOf(
            Rule(1, "广告", "", isEnabled: false),
            Rule(2, "", "x"));

        Assert.Empty(p.FindEffectiveRuleIds("广告", PurifyContext.Default));
    }

    [Fact]
    public void Scope_Mismatch_Is_Not_A_Hit()
    {
        // 两条规则各管一段文本：作用域匹配的那条才命中（顺序执行，互不吞掉对方的匹配串）
        var p = PurifierOf(
            Rule(1, "广告甲", "", scope: "book:https://book.example/1"),
            Rule(2, "广告乙", "", scope: "source:https://src.example"));

        var wrongBook = p.FindEffectiveRuleIds(
            "广告甲广告乙", new PurifyContext("书名", "https://book.example/2", "https://src.example"));
        Assert.Equal(new long[] { 2 }, wrongBook.ToArray());

        var rightBook = p.FindEffectiveRuleIds(
            "广告甲广告乙", new PurifyContext("书名", "https://book.example/1", "https://src.example"));
        Assert.Equal(new long[] { 1, 2 }, rightBook.ToArray());
    }

    [Fact]
    public void UseReplaceRule_False_Disables_All_Hits()
    {
        var p = PurifierOf(Rule(1, "广告", ""));

        Assert.Empty(p.FindEffectiveRuleIds("广告", new PurifyContext(null, null, null, UseReplaceRule: false)));
    }

    [Fact]
    public void No_Rules_Or_Empty_Text_Returns_Empty()
    {
        var p = PurifierOf();
        Assert.Empty(p.FindEffectiveRuleIds("正文", PurifyContext.Default));

        var withRule = PurifierOf(Rule(1, "广告", ""));
        Assert.Empty(withRule.FindEffectiveRuleIds(string.Empty, PurifyContext.Default));
    }

    [Fact]
    public void Regex_Rule_With_Group_Reference_Is_A_Hit()
    {
        var p = PurifierOf(Rule(9, @"第(\d+)章", "Chapter $1", isRegex: true));

        var hits = p.FindEffectiveRuleIds("第12章 正文", PurifyContext.Default);

        Assert.Equal(new long[] { 9 }, hits.ToArray());
        Assert.Equal("Chapter 12 正文", p.Apply("第12章 正文", PurifyContext.Default));
    }
}

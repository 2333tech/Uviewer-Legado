using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 净化时机契约测试。
///
/// <para><b>契约</b>：抓取链路（<c>GetContentAsync</c> / <c>GetContentsIndexedAsync</c>）
/// <b>不做任何净化</b>，只返回原文；净化由宿主在"读取 → 渲染之间"执行，
/// 以便被一级菜单的"替换净化（总开关）"控制。</para>
///
/// <para><b>为什么这条契约很重要</b>：若引擎在抓取时就净化，缓存里存的会是净化后文本，
/// 关掉总开关就无从还原，只能重新走网络 —— 总开关形同虚设。</para>
///
/// <para>本组锁定：① 抓取返回原文（引擎完全不受规则影响）；
/// ② 渲染前调用净化才应用规则；③ 总开关关闭（<c>Rules = null</c>）时零开销原样返回；
/// ④ 开关来回切换同一份原文可反复净化与还原。</para>
/// </summary>
public class PurifyTimingContractTests
{
    private sealed class FixtureFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map;

        public FixtureFetcher(Dictionary<string, string> map) => _map = map;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            var hit = _map.FirstOrDefault(kv => url.Contains(kv.Key, StringComparison.Ordinal));
            return Task.FromResult((hit.Value ?? string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private static BookSourceModel MakeSource() => new()
    {
        BookSourceUrl = "https://api-bc.wtzw.com",
        BookSourceName = "阅读助手（测试）",
        RuleContent = new ContentRule { Content = "data.content" },
    };

    private const string ChapterJson = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>正文里有广告</p>\"}}";

    private static FixtureFetcher MakeFetcher() => new(new Dictionary<string, string>
    {
        ["/ch/1.html"] = ChapterJson,
    });

    /// <summary>构造一个带"去广告"规则的净化器（规则会删掉"广告"二字，长度必然变化）。</summary>
    private static ContentPurifier MakePurifier()
    {
        var manager = new ReplaceRuleManager();
        manager.Upsert(new ReplaceRule
        {
            Name = "去广告",
            Pattern = "广告",
            Replacement = "",
            IsRegex = false,
            IsEnabled = true,
        });

        return new ContentPurifier { Rules = manager.GetAll() };
    }

    [Fact]
    public async Task GetContentAsync_不净化_返回原文()
    {
        var engine = new BookSourceEngine(MakeFetcher(), MakePurifier());

        var content = await engine.GetContentAsync(MakeSource(), "https://api-bc.wtzw.com/ch/1.html");

        Assert.NotNull(content);

        // 关键断言：即使构造 engine 时给了带规则的 purifier，抓取结果仍是原文
        Assert.Contains("广告", content!.Content, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContentsIndexedAsync_不净化_返回原文()
    {
        var engine = new BookSourceEngine(MakeFetcher(), MakePurifier());

        var items = new[]
        {
            new TocItem { ChapterName = "第一章", ChapterUrl = "https://api-bc.wtzw.com/ch/1.html" },
        };

        var contents = await engine.GetContentsIndexedAsync(MakeSource(), items, "https://api-bc.wtzw.com/book/1");

        Assert.Single(contents);
        Assert.NotNull(contents[0]);
        Assert.Contains("广告", contents[0]!.Content, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task 渲染前净化_才应用规则()
    {
        var engine = new BookSourceEngine(MakeFetcher(), MakePurifier());

        // ① 抓取：原文
        var content = await engine.GetContentAsync(MakeSource(), "https://api-bc.wtzw.com/ch/1.html");
        Assert.NotNull(content);
        Assert.Contains("广告", content!.Content, System.StringComparison.Ordinal);

        // ② 读取 → 渲染之间：净化
        var display = MakePurifier().Apply(content.Content, PurifyContext.Default);

        Assert.DoesNotContain("广告", display, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task 总开关关闭_零开销原样返回()
    {
        var engine = new BookSourceEngine(MakeFetcher(), MakePurifier());
        var content = await engine.GetContentAsync(MakeSource(), "https://api-bc.wtzw.com/ch/1.html");
        Assert.NotNull(content);

        var raw = content!.Content;

        // 总开关关闭 → 宿主把 Rules 置 null（见 BookSourceService.ApplyReplaceRules）
        var purifier = MakePurifier();
        purifier.Rules = null;

        var display = purifier.Apply(raw, PurifyContext.Default);

        // 原样返回，且与"净化开启"时结果不同 —— 证明开关真的起作用
        Assert.Equal(raw, display);
        Assert.Contains("广告", display, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task 开关来回切换_同一份原文可反复净化与还原()
    {
        var engine = new BookSourceEngine(MakeFetcher(), MakePurifier());
        var content = await engine.GetContentAsync(MakeSource(), "https://api-bc.wtzw.com/ch/1.html");
        Assert.NotNull(content);

        var raw = content!.Content;
        var purifier = MakePurifier();

        // 开 → 净化
        Assert.DoesNotContain("广告", purifier.Apply(raw, PurifyContext.Default), System.StringComparison.Ordinal);

        // 关 → 还原原文
        purifier.Rules = null;
        Assert.Equal(raw, purifier.Apply(raw, PurifyContext.Default));

        // 再开 → 又能净化（原文始终未被覆盖，这正是缓存原文的目的）
        purifier.Rules = MakePurifier().Rules;
        Assert.DoesNotContain("广告", purifier.Apply(raw, PurifyContext.Default), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task 抓取结果与是否注入purifier无关()
    {
        // 不给 purifier
        var withoutPurifier = await new BookSourceEngine(MakeFetcher())
            .GetContentAsync(MakeSource(), "https://api-bc.wtzw.com/ch/1.html");

        // 给带规则的 purifier
        var withPurifier = await new BookSourceEngine(MakeFetcher(), MakePurifier())
            .GetContentAsync(MakeSource(), "https://api-bc.wtzw.com/ch/1.html");

        Assert.NotNull(withoutPurifier);
        Assert.NotNull(withPurifier);

        // 两者完全一致：抓取阶段与净化规则无关
        Assert.Equal(withoutPurifier!.Content, withPurifier!.Content);
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.Models;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Models = Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 书源筛选（关键词 / 类别 / 自定义分组）：书源管理与书源搜索共用同一套判定。
///
/// <para><b>为什么这套逻辑值得单测</b>：用户实际导入 3637 条书源（文本 3380 / 漫画 129 /
/// 音频 80 / 视频 15 / 文件 33，分组 173 个）。"按类别筛出漫画"、"按组搜索"
/// 一旦判定写错，用户看到的是"空的列表"，而不是一条报错——这种错误只能靠断言锁住。</para>
///
/// <para><b>口径</b>：<c>bookSourceGroup</c> 是逗号分隔的多值字符串（一条源可属于多个组）；
/// 类别对齐 legado 的 <c>bookSourceType</c>（0 文本 / 1 音频 / 2 图片(漫画) / 3 文件 / 4 视频）。</para>
/// </summary>
public class BookSourceFilterTests : IDisposable
{
    private readonly string _dir;

    public BookSourceFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uviewer-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    // ============================================================
    // 分组文本处理
    // ============================================================

    [Fact]
    public void Split_Accepts_Halfwidth_And_Fullwidth_Separators()
    {
        Assert.Equal(new[] { "A", "B", "C", "D" }, BookSourceGroups.Split("A，B;C；D"));
    }

    [Fact]
    public void Split_Trims_And_Dedupes_Case_Insensitively()
    {
        Assert.Equal(new[] { "小说源", "校验可用" }, BookSourceGroups.Split(" 小说源 , 校验可用 ,小说源, "));
        Assert.Equal(new[] { "Novel" }, BookSourceGroups.Split("Novel,novel,NOVEL"));
    }

    [Fact]
    public void Split_Returns_Empty_For_Null_Or_Blank()
    {
        Assert.Empty(BookSourceGroups.Split(null));
        Assert.Empty(BookSourceGroups.Split("   "));
        Assert.Empty(BookSourceGroups.Split(",,，;;"));
    }

    [Fact]
    public void Normalize_Unifies_Separators_And_Drops_Empties()
    {
        Assert.Equal("A,B", BookSourceGroups.Normalize(" A ；B "));
        Assert.Equal(string.Empty, BookSourceGroups.Normalize(null));
    }

    [Fact]
    public void Add_Appends_Only_When_Missing()
    {
        Assert.Equal("A,B", BookSourceGroups.Add("A", "B"));
        Assert.Equal("A,B", BookSourceGroups.Add("A,B", "B"));            // 幂等
        Assert.Equal("A,b", BookSourceGroups.Add("A,b", "B"));            // 忽略大小写（且不改写原有写法）
        Assert.Equal("新组", BookSourceGroups.Add(null, " 新组 "));        // 空串起手
        Assert.Equal("A", BookSourceGroups.Add("A", "  "));               // 空组名不写
    }

    [Fact]
    public void Remove_Drops_Only_The_Target_Group()
    {
        Assert.Equal("B", BookSourceGroups.Remove("A,B", "A"));
        Assert.Equal("B", BookSourceGroups.Remove("A,B", "a"));           // 忽略大小写
        Assert.Equal("A,B", BookSourceGroups.Remove("A,B", "C"));         // 不存在 = 无变化
        Assert.Equal(string.Empty, BookSourceGroups.Remove("A", "A"));    // 摘空返回空串
    }

    [Fact]
    public void Rename_Replaces_The_Group_In_Place()
    {
        Assert.Equal("X,B", BookSourceGroups.Rename("A,B", "A", "X"));
        Assert.Equal("a,B", BookSourceGroups.Rename("a,B", "x", "B"));    // 原名不存在 = 无变化
        Assert.Equal("A,B", BookSourceGroups.Rename("A,B", "A", "A"));    // 同名不改
    }

    [Fact]
    public void Contains_Matches_Any_Segment_Not_Substring()
    {
        Assert.True(BookSourceGroups.Contains("小说源,校验可用", "校验可用"));
        Assert.True(BookSourceGroups.Contains("Novel", "novel"));
        Assert.False(BookSourceGroups.Contains("校验可用", "校验"));      // 不做子串匹配
        Assert.False(BookSourceGroups.Contains(null, "A"));
    }

    [Fact]
    public void Collect_Counts_And_Sorts_By_Count_Then_Name()
    {
        var sources = new[]
        {
            Source("s1", group: "热门,校验可用"),
            Source("s2", group: "热门"),
            Source("s3", group: null),
        };

        var groups = BookSourceGroups.Collect(sources);

        Assert.Equal(("热门", 2), groups[0]);
        Assert.Equal(("校验可用", 1), groups[1]);
        Assert.Equal(2, groups.Count);   // 没有分组的源不产生空项
    }

    // ============================================================
    // 筛选判定
    // ============================================================

    [Fact]
    public void Empty_Filter_Matches_Everything()
    {
        var filter = new BookSourceFilter();

        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches(Source("任意")));
        Assert.True(new BookSourceFilter { Keyword = "   " }.IsEmpty);
    }

    [Fact]
    public void Filter_By_Kind_Uses_BookSourceType()
    {
        var filter = new BookSourceFilter { Kind = Models.BookSourceKind.Image };

        Assert.True(filter.Matches(Source("漫画源", kind: Models.BookSourceKind.Image)));
        Assert.False(filter.Matches(Source("文本源", kind: Models.BookSourceKind.Text)));
        Assert.False(filter.Matches(Source("音频源", kind: Models.BookSourceKind.Audio)));
    }

    [Fact]
    public void Filter_By_Group_Matches_Multi_Group_Sources()
    {
        var filter = new BookSourceFilter { Group = "校验可用" };

        Assert.True(filter.Matches(Source("多组源", group: "热门,校验可用")));
        Assert.True(filter.Matches(Source("单组源", group: "校验可用")));
        Assert.False(filter.Matches(Source("别组源", group: "热门")));
        Assert.False(filter.Matches(Source("无组源")));
    }

    [Fact]
    public void Filter_Keyword_Matches_Name_Url_Group_Comment_And_Kind_Label()
    {
        Assert.True(new BookSourceFilter { Keyword = "笔趣阁" }.Matches(Source("笔趣阁新站")));
        Assert.True(new BookSourceFilter { Keyword = "example" }.Matches(Source("任意")));       // URL
        Assert.True(new BookSourceFilter { Keyword = "校验" }.Matches(Source("任意", group: "校验可用")));
        Assert.True(new BookSourceFilter { Keyword = "备注词" }.Matches(Source("任意", comment: "这是备注词")));

        // 类别名也参与匹配：用户输入"漫画"与在下拉里选"漫画"应当同义
        Assert.True(new BookSourceFilter { Keyword = "漫画" }.Matches(Source("未知源", kind: Models.BookSourceKind.Image)));
        Assert.False(new BookSourceFilter { Keyword = "漫画" }.Matches(Source("未知源", kind: Models.BookSourceKind.Text)));
    }

    [Fact]
    public void Filter_Combines_Kind_Group_And_Keyword_With_And()
    {
        var filter = new BookSourceFilter { Kind = Models.BookSourceKind.Image, Group = "精选", Keyword = "漫画" };
        var ok = Source("漫画A", kind: Models.BookSourceKind.Image, group: "精选");

        Assert.True(filter.Matches(ok));

        // 三个条件任缺其一都不匹配（组不符 / 类别不符）
        Assert.False(filter.Matches(Source("漫画A", kind: Models.BookSourceKind.Image, group: "其它")));
        Assert.False(filter.Matches(Source("漫画A", kind: Models.BookSourceKind.Text, group: "精选")));

        // 关键词不符：类别名是"漫画"也救不了名字/分组都对不上的源
        Assert.False(new BookSourceFilter { Keyword = "小说" }.Matches(ok));
    }

    [Fact]
    public void Apply_Preserves_Import_Order()
    {
        var sources = new[]
        {
            Source("第三", kind: Models.BookSourceKind.Text, url: "https://c.example.com"),
            Source("第一", kind: Models.BookSourceKind.Image, url: "https://a.example.com"),
            Source("第二", kind: Models.BookSourceKind.Image, url: "https://b.example.com"),
        };

        var matched = new BookSourceFilter { Kind = Models.BookSourceKind.Image }.Apply(sources);

        Assert.Equal(new[] { "第一", "第二" }, matched.Select(s => s.BookSourceName));
    }

    [Fact]
    public void DescribeKind_Covers_Every_Legado_Type()
    {
        Assert.Equal("文本", BookSourceGroups.DescribeKind(Models.BookSourceKind.Text));
        Assert.Equal("音频", BookSourceGroups.DescribeKind(Models.BookSourceKind.Audio));
        Assert.Equal("漫画", BookSourceGroups.DescribeKind(Models.BookSourceKind.Image));
        Assert.Equal("文件", BookSourceGroups.DescribeKind(Models.BookSourceKind.File));
        Assert.Equal("视频", BookSourceGroups.DescribeKind(Models.BookSourceKind.Video));
    }

    [Fact]
    public void KindOptions_Start_With_All_And_Cover_Each_Type()
    {
        Assert.Null(BookSourceGroups.KindOptions[0].Kind);

        var kinds = BookSourceGroups.KindOptions
            .Where(o => o.Kind is not null)
            .Select(o => o.Kind!.Value)
            .ToList();

        Assert.Equal(5, kinds.Count);
        Assert.Contains(Models.BookSourceKind.Text, kinds);
        Assert.Contains(Models.BookSourceKind.Image, kinds);
        Assert.Contains(Models.BookSourceKind.Audio, kinds);
        Assert.Contains(Models.BookSourceKind.Video, kinds);
        Assert.Contains(Models.BookSourceKind.File, kinds);
    }

    // ============================================================
    // 搜索候选集：筛选落到真实服务上
    // ============================================================

    [Fact]
    public async Task SearchableSources_Respect_Kind_Group_And_Keyword()
    {
        var text = Source("小说源A", kind: Models.BookSourceKind.Text, group: "热门", url: "https://t.example.com");
        var manga = Source("漫画源B", kind: Models.BookSourceKind.Image, group: "漫画,精选", url: "https://m.example.com");
        var audio = Source("音频源C", kind: Models.BookSourceKind.Audio, group: "精选", url: "https://a.example.com");

        await using var service = await NewServiceAsync(text, manga, audio);

        Assert.Equal(3, service.GetSearchableSources().Count);

        var onlyManga = service.GetSearchableSources(new BookSourceFilter { Kind = Models.BookSourceKind.Image });
        Assert.Equal(new[] { "漫画源B" }, onlyManga.Select(s => s.BookSourceName));

        var onlyFeatured = service.GetSearchableSources(new BookSourceFilter { Group = "精选" });
        Assert.Equal(new[] { "漫画源B", "音频源C" }, onlyFeatured.Select(s => s.BookSourceName));

        var byKeyword = service.GetSearchableSources(new BookSourceFilter { Keyword = "小说源" });
        Assert.Equal(new[] { "小说源A" }, byKeyword.Select(s => s.BookSourceName));

        // 组合条件：类别=漫画 且 组=精选
        var combined = service.GetSearchableSources(
            new BookSourceFilter { Kind = Models.BookSourceKind.Image, Group = "精选" });
        Assert.Equal(new[] { "漫画源B" }, combined.Select(s => s.BookSourceName));
    }

    [Fact]
    public async Task SearchableSources_Keep_Audio_Sources_That_Cannot_Be_Read()
    {
        // 音频源的正文规则用了 webJs（阅读链路需要 WebView → ReadingSupported=false）。
        // 但本应用从不阅读音频：按"阅读能力"把它过滤掉，会让用户按"音频"筛选后一个源都不剩，
        // 误以为库里没有音频源。
        var audioNeedsWebView = Source(
            "音频需WebView", kind: Models.BookSourceKind.Audio, url: "https://aw.example.com");
        audioNeedsWebView.RuleContent = new Models.ContentRule { Content = "#content", WebJs = "window.scrollTo(0,0)" };

        // 同一条规则放到文本源上：文本要真的打开来读，必须被阅读能力过滤掉
        var textNeedsWebView = Source(
            "文本需WebView", kind: Models.BookSourceKind.Text, url: "https://tw.example.com");
        textNeedsWebView.RuleContent = new Models.ContentRule { Content = "#content", WebJs = "window.scrollTo(0,0)" };

        await using var service = await NewServiceAsync(audioNeedsWebView, textNeedsWebView);

        // 明确按音频筛选 → 音频源必须在
        var audioOnly = service.GetSearchableSources(new BookSourceFilter { Kind = Models.BookSourceKind.Audio });
        Assert.Equal(new[] { "音频需WebView" }, audioOnly.Select(s => s.BookSourceName));

        // 明确按文本筛选 → 需要 WebView 的文本源被排除（它读不了，搜了也没用）
        var textOnly = service.GetSearchableSources(new BookSourceFilter { Kind = Models.BookSourceKind.Text });
        Assert.Empty(textOnly);

        // 不筛选（全部类别）→ 维持既有口径：阅读链路不支持的源一律不进候选集
        Assert.Empty(service.GetSearchableSources());
    }

    // ============================================================
    // 辅助
    // ============================================================

    private async Task<BookSourceService> NewServiceAsync(params BookSourceModel[] sources)
    {
        // 直接写 booksources.json（不走导入），避免导入迁移规则干扰断言
        File.WriteAllText(
            Path.Combine(_dir, "booksources.json"),
            BookSourceSerializer.SerializeList(sources));

        var runtime = new LegadoRuntime(_dir, new StaticFetcher());
        var service = new BookSourceService(_dir, runtime);
        await service.EnsureInitializedAsync();
        return service;
    }

    /// <summary>一条"能搜索、阅读链路干净"的源（关键字段给足，能力评估才判定为可读）。</summary>
    private static BookSourceModel Source(
        string name,
        Models.BookSourceKind kind = Models.BookSourceKind.Text,
        string? group = null,
        string? comment = null,
        string? url = null) =>
        new()
        {
            BookSourceUrl = url ?? $"https://{name}.example.com",
            BookSourceName = name,
            BookSourceGroup = group,
            BookSourceComment = comment,
            BookSourceType = (int)kind,
            SearchUrl = "https://x.example.com/search?q={{key}}",
            RuleSearch = new Models.SearchRule
            {
                BookList = "$.books[*]",
                Name = "$.name",
                Author = "$.author",
                BookUrl = "$.bookUrl",
            },
            RuleToc = new Models.TocRule { ChapterList = "#list a", ChapterName = "text", ChapterUrl = "href" },
            RuleContent = new Models.ContentRule { Content = "#content" },
        };

    private sealed class StaticFetcher : IBookSourceFetcher
    {
        private const string Body = "{\"books\":[{\"name\":\"书A\",\"author\":\"作者\",\"bookUrl\":\"https://x.example.com/1\"}]}";

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((Body, url));

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }
}

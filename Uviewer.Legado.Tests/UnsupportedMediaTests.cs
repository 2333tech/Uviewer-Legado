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
/// 书籍搜索的「不支持音视频」标注（2026-10-21 用户约定）：
/// 搜索结果来自音频（<c>bookSourceType=1</c>）/视频（<c>4</c>）书源时，
/// 卡片上直接显示黄色三角感叹号 + 「不支持音视频」。
///
/// <para><b>约定边界</b>：本应用**始终不实现音视频播放**；搜索结果照常展示（不隐藏——
/// 用户可能正想知道"这个源里有这本书"），只加标注。图片书源（<c>=2</c>）不在"音视频"字面
/// 范围内，不标注。</para>
/// <para>本测试锁的是**数据判定**（UI 徽标由 <c>BookSourceSearchDialog</c> 纯代码构建，
/// 无法无头断言）：哪个结果会被标、哪个不会被标。</para>
/// </summary>
public class UnsupportedMediaTests : IDisposable
{
    private readonly string _dir;

    public UnsupportedMediaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uviewer-media-tests", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task Search_Hits_From_Audio_And_Video_Sources_Are_Flagged()
    {
        var textSource = Source("文本源", kind: Models.BookSourceKind.Text);
        var audioSource = Source("音频源", kind: Models.BookSourceKind.Audio);
        var videoSource = Source("视频源", kind: Models.BookSourceKind.Video);
        var imageSource = Source("图片源", kind: Models.BookSourceKind.Image);

        // 直接构造服务数据目录里的 booksources.json（不经过导入，避免迁移/过滤干扰）
        File.WriteAllText(
            Path.Combine(_dir, "booksources.json"),
            BookSourceSerializer.SerializeList(new[] { textSource, audioSource, videoSource, imageSource }));

        var fetcher = new StaticSearchFetcher();
        var runtime = new LegadoRuntime(_dir, fetcher);
        await using var service = new BookSourceService(_dir, runtime);
        await service.EnsureInitializedAsync();

        var groups = await new BookSourceSearchService(service).SearchAsync("书");
        var hits = groups.SelectMany(g => g.Hits).ToList();

        Assert.Equal(4, hits.Count);
        Assert.False(hits.Single(h => h.SourceName == "文本源").IsAudioVideo);
        Assert.True(hits.Single(h => h.SourceName == "音频源").IsAudioVideo);
        Assert.True(hits.Single(h => h.SourceName == "视频源").IsAudioVideo);
        Assert.False(hits.Single(h => h.SourceName == "图片源").IsAudioVideo); // 图片不是"音视频"
    }

    /// <summary>构造一个"能搜索、阅读链路干净"的原始条目模型（书源类型由 kind 决定）。</summary>
    private static BookSourceModel Source(string name, Models.BookSourceKind kind) =>
        new()
        {
            BookSourceUrl = $"https://{kind.ToString().ToLowerInvariant()}.example.com",
            BookSourceName = name,
            BookSourceType = (int)kind,
            SearchUrl = $"https://{kind.ToString().ToLowerInvariant()}.example.com/search?q={{{{key}}}}",
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

    /// <summary>任何搜索请求都返回同一份结果（离线）。</summary>
    private sealed class StaticSearchFetcher : IBookSourceFetcher
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

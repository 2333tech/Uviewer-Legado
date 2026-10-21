using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// <c>GetContentsIndexedAsync</c> 测试（整本下载的正确性基础）。
///
/// <para><b>为什么必须单独测</b>：整本下载把「第 i 章正文」写进「第 i 章标题」之后，
/// 因此结果数组<b>必须与入参目录按下标一一对应</b>。已有的
/// <c>GetContentsAsync</c> 会过滤掉 null（失败章被挤掉），一旦中间某章失败，
/// 后面每一章的正文都会配错标题——书能打开，但内容整体错位一章，且极难察觉。
/// 本组测试锁定"保留空洞、不压缩"这一语义。</para>
/// </summary>
public class IndexedContentFetchTests
{
    /// <summary>缺失 URL 时抛错的取数替身（用于制造"单章失败"）。</summary>
    private sealed class ThrowingFetcher : IBookSourceFetcher
    {
        private readonly string _failUrlFragment;

        public ThrowingFetcher(string failUrlFragment) => _failUrlFragment = failUrlFragment;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            if (url.Contains(_failUrlFragment, System.StringComparison.Ordinal))
            {
                throw new HttpRequestException($"模拟失败：{url}");
            }

            var body = $"<html><body><div id=\"content\"><p>正文-{url}</p></div></body></html>";
            return Task.FromResult((body, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private static BookSourceModel NewSource() => new()
    {
        BookSourceUrl = "https://book.example.com",
        RuleContent = new ContentRule { Content = "@css:#content@html" },
    };

    [Fact]
    public async Task IndexedFetch_全部成功_长度与目录一致且下标对齐()
    {
        var source = NewSource();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 12; i++)
        {
            chapters.Add(new TocItem
            {
                Index = i,
                ChapterName = $"第{i}章",
                ChapterUrl = $"https://book.example.com/c/{i}",
            });
        }

        var results = await new BookSourceEngine(new ThrowingFetcher("/__never__/"))
            .GetContentsIndexedAsync(source, chapters, null, 4, null);

        Assert.Equal(chapters.Count, results.Length);
        for (var i = 0; i < chapters.Count; i++)
        {
            Assert.NotNull(results[i]);
            Assert.Contains($"/c/{i}", results[i]!.Content, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IndexedFetch_中间章失败_保留空洞且后续不错位()
    {
        // 让第 5 章抓取抛错：压缩型 API 会把第 6 章挪到下标 5，导致标题/正文错位
        var source = NewSource();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 10; i++)
        {
            chapters.Add(new TocItem
            {
                Index = i,
                ChapterName = $"第{i}章",
                ChapterUrl = $"https://book.example.com/c/{i}",
            });
        }

        var results = await new BookSourceEngine(new ThrowingFetcher("/c/5"))
            .GetContentsIndexedAsync(source, chapters, null, 4, null);

        // 长度不变（关键：不压缩）
        Assert.Equal(10, results.Length);

        // 失败章留 null
        Assert.Null(results[5]);

        // 失败章之后的每一章仍留在原下标上——这正是整本下载不串章的保证
        Assert.NotNull(results[6]);
        Assert.Contains("/c/6", results[6]!.Content, System.StringComparison.Ordinal);
        Assert.NotNull(results[9]);
        Assert.Contains("/c/9", results[9]!.Content, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexedFetch_空目录_返回空数组()
    {
        var results = await new BookSourceEngine(new ThrowingFetcher("/__never__/"))
            .GetContentsIndexedAsync(NewSource(), new List<TocItem>());

        Assert.Empty(results);
    }

    [Fact]
    public async Task IndexedFetch_取消时抛出且不吞异常()
    {
        var source = NewSource();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 20; i++)
        {
            chapters.Add(new TocItem { Index = i, ChapterUrl = $"https://book.example.com/c/{i}" });
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new BookSourceEngine(new ThrowingFetcher("/__never__/"))
                .GetContentsIndexedAsync(source, chapters, null, 4, null, cts.Token));
    }

    [Fact]
    public async Task IndexedFetch_进度回调_次数等于章节数()
    {
        var source = NewSource();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 9; i++)
        {
            chapters.Add(new TocItem { Index = i, ChapterUrl = $"https://book.example.com/c/{i}" });
        }

        var done = 0;
        // 同步 IProgress：Progress<T> 会把回调抛到线程池，测试里不能直接断言
        var counter = new CountingProgress(p => Interlocked.Increment(ref done));

        await new BookSourceEngine(new ThrowingFetcher("/__never__/"))
            .GetContentsIndexedAsync(source, chapters, null, 4, counter);

        Assert.Equal(9, done);
    }

    private sealed class CountingProgress : IProgress<int>
    {
        private readonly Action<int> _handler;
        public CountingProgress(Action<int> handler) => _handler = handler;
        public void Report(int value) => _handler(value);
    }
}

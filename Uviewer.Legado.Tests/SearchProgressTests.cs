using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 多源搜索进度（<c>SearchProgress</c>）与并发行为测试。
/// <para>动机：阶段 5 集成时被反馈"搜索一直显示搜索中、找不到条目"。根因之一是
/// 多源搜索长达数十秒却没有任何进度反馈（280 源 / 16 并发 / 单源 15s 超时），
/// 用户无法区分"正在搜"与"卡死"。本类钉住进度回调的契约：
/// ① 单调递增；② 恰好推进到总数；③ 单源失败也推进（否则进度会停在 N-1）。</para>
/// </summary>
public class SearchProgressTests
{
    /// <summary>可注入的假 fetcher：按 URL 返回预置响应，无需联网。</summary>
    private sealed class StubFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public StubFetcher Register(string url, string body)
        {
            _map[url] = body;
            return this;
        }

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var kv in _map)
            {
                if (url.Contains(kv.Key, StringComparison.Ordinal))
                {
                    return Task.FromResult((kv.Value, url));
                }
            }

            return Task.FromResult((string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    }

    /// <summary>收集进度回调（同步实现，避免 Progress&lt;T&gt; 回调抛到线程池）。</summary>
    private sealed class SyncProgress : IProgress<SearchProgress>
    {
        private readonly ConcurrentQueue<SearchProgress> _items = new();

        public IReadOnlyList<SearchProgress> Items => _items.ToArray();

        public void Report(SearchProgress value) => _items.Enqueue(value);
    }

    /// <param name="url">书源 URL，同时用作搜索域名，保证每个源请求各自的站点。</param>
    private static BookSourceModel SourceWithSearch(string name, string url) => new()
    {
        BookSourceUrl = url,
        BookSourceName = name,
        Enabled = true,
        // 搜索 URL 用该源自己的域名：AbsoluteUrl 会去掉 query，若所有源共用同一 host
        // 三个源实际请求的是同一个地址，测试就测不出"每源各自搜索"。
        SearchUrl = url.TrimEnd('/') + "/search?key={{key}}",
        RuleSearch = new SearchRule
        {
            BookList = "data[*]",
            Name = "title",
            BookUrl = "id",
        },
    };

    [Fact]
    public void SearchProgress_ToString_Is_X_Over_Y()
    {
        Assert.Equal("3/186", new SearchProgress(3, 186).ToString());
        Assert.Equal("0/0", new SearchProgress(0, 0).ToString());
    }

    [Fact]
    public async Task SearchAllAsync_Reports_Monotonic_Progress_Reaching_Total()
    {
        var fetcher = new StubFetcher();
        fetcher.Register("host1", """{"data":[{"id":"/b/1","title":"甲"}]}""");
        fetcher.Register("host2", """{"data":[{"id":"/b/2","title":"乙"}]}""");
        fetcher.Register("host3", """{"data":[]}""");

        var sources = new[]
        {
            SourceWithSearch("源1", "https://host1"),
            SourceWithSearch("源2", "https://host2"),
            SourceWithSearch("源3", "https://host3"),
        };

        var progress = new SyncProgress();
        var service = new SearchService(fetcher);
        var results = await service.SearchAllAsync(sources, "关键词", null, progress);

        var items = progress.Items;
        Assert.NotEmpty(items);

        // ① 首条是 0/总数（总数必须一开始就已知，UI 才能画出进度条）
        Assert.Equal(new SearchProgress(0, 3), items[0]);

        // ② 单调递增且不超过总数
        var completed = items.Select(i => i.Completed).ToList();
        for (var i = 1; i < completed.Count; i++)
        {
            Assert.True(completed[i] >= completed[i - 1], "进度必须单调不减");
            Assert.True(completed[i] <= 3);
        }

        // ③ 恰好推进到总数（不多不少）——停在 N-1 说明有源没推进，用户会以为卡住
        Assert.Equal(3, completed[^1]);
        Assert.All(items, i => Assert.Equal(3, i.Total));

        // 结果本身不受进度影响：保序合并
        Assert.Equal(2, results.Count);
        Assert.Equal("甲", results[0].Name);
        Assert.Equal("乙", results[1].Name);
    }

    [Fact]
    public async Task SearchAllAsync_Failing_Source_Still_Advances_Progress()
    {
        // host2 映射到一个非法 URL 模板 → SearchAsync 抛异常。
        // 关键：该源必须被吞掉并推进进度，否则 UI 永远停在 (总数-1)/总数。
        var fetcher = new StubFetcher();
        fetcher.Register("good", """{"data":[{"id":"/b/1","title":"甲"}]}""");

        var sources = new List<BookSourceModel> { SourceWithSearch("好源", "https://good") };

        // 造一个会在运行期抛异常的源：让 fetcher 对该 URL 抛网络异常
        var throwing = new ThrowingFetcher(fetcher, "https://boom");
        var service = new SearchService(throwing);
        sources.Add(SourceWithSearch("会抛的源", "https://boom"));

        var progress = new SyncProgress();
        var results = await service.SearchAllAsync(sources, "关键词", null, progress);

        // 一个源的结果保留，抛异常的源被吞掉
        Assert.Single(results);
        Assert.Equal("甲", results[0].Name);

        // 进度仍推进到总数
        Assert.Equal(2, progress.Items[^1].Completed);
    }

    [Fact]
    public async Task SearchAllAsync_Empty_Input_Reports_Zero_Over_Zero()
    {
        var progress = new SyncProgress();
        var service = new SearchService(new StubFetcher());

        var results = await service.SearchAllAsync(Array.Empty<BookSourceModel>(), "x", null, progress);

        Assert.Empty(results);
        Assert.Equal(new SearchProgress(0, 0), Assert.Single(progress.Items));
    }

    [Fact]
    public async Task SearchAllAsync_All_Sources_Filtered_Out_Reports_Zero_Over_Zero()
    {
        // 无 searchUrl 的源在预过滤阶段就被剔除 → 不进入进度统计
        var progress = new SyncProgress();
        var service = new SearchService(new StubFetcher());
        var noSearch = new BookSourceModel { BookSourceUrl = "https://x", BookSourceName = "无搜索" };

        var results = await service.SearchAllAsync(new[] { noSearch }, "x", null, progress);

        Assert.Empty(results);
        Assert.Equal(new SearchProgress(0, 0), Assert.Single(progress.Items));
    }

    [Fact]
    public async Task SearchAllAsync_Timeout_Exception_Does_Not_Abort_Whole_Search()
    {
        // 回归测试（真实 bug）：HttpClient 超时抛的是 TaskCanceledException，
        // 它继承自 OperationCanceledException。若 catch (OperationCanceledException) 直接
        // rethrow（"用户取消才传播"的直觉写法），那么 204 个源里任意一个卡满 15s
        // 就会让整次搜索抛异常——UI 表现为"一直搜索中、找不到条目"。
        // 正确做法：只有 cancellationToken.IsCancellationRequested 为真才算用户取消。
        var fetcher = new TimeoutThenOkFetcher();
        var sources = new[]
        {
            SourceWithSearch("慢源", "https://slow"),
            SourceWithSearch("快源", "https://fast"),
        };

        var service = new SearchService(fetcher);
        var results = await service.SearchAllAsync(sources, "关键词", null, null);

        // 慢源超时被吞掉，快源的结果照样返回——整次搜索不抛异常
        Assert.Single(results);
        Assert.Equal("甲", results[0].Name);
    }

    /// <summary>对 slow 域抛 TaskCanceledException（模拟 HttpClient 超时），其余正常返回。</summary>
    private sealed class TimeoutThenOkFetcher : IBookSourceFetcher
    {
        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null,
            CancellationToken cancellationToken = default)
        {
            if (url.Contains("slow", StringComparison.Ordinal))
            {
                // 注意：不是传已取消的 token，而是像 HttpClient 超时那样构造异常
                throw new TaskCanceledException("模拟请求超时");
            }

            return Task.FromResult(("""{"data":[{"id":"/b/1","title":"甲"}]}""", url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    }

    [Fact]
    public async Task SearchAllAsync_Respects_Concurrency_Limit()
    {
        var sources = Enumerable.Range(1, 8)
            .Select(i => SourceWithSearch($"源{i}", $"https://h{i}"))
            .ToList();

        var fetcher = new ConcurrencyTrackingFetcher();
        var service = new SearchService(fetcher);

        // 限流设为 3：任何时刻在途请求不应超过 3
        await service.SearchAllAsync(sources, "x", 3, null);

        Assert.True(
            fetcher.MaxConcurrent <= 3,
            $"并发峰值 {fetcher.MaxConcurrent} 超过了限流 3");
    }

    [Fact]
    public async Task SearchAllAsync_Output_Order_Matches_Input_Order()
    {
        // 与串行结果一致：并发不改变顺序（reader 的合并语义）
        var fetcher = new StubFetcher();
        for (var i = 1; i <= 5; i++)
        {
            fetcher.Register($"h{i}", $$"""{"data":[{"id":"/b/{{i}}","title":"书{{i}}"}]}""");
        }

        var sources = Enumerable.Range(1, 5)
            .Select(i => SourceWithSearch($"源{i}", $"https://h{i}"))
            .ToList();

        var service = new SearchService(fetcher);
        var results = await service.SearchAllAsync(sources, "x", null, null);

        Assert.Equal(
            new[] { "书1", "书2", "书3", "书4", "书5" },
            results.Select(r => r.Name));
    }

    /// <summary>对指定 URL 抛异常，其余走内层 fetcher。</summary>
    private sealed class ThrowingFetcher : IBookSourceFetcher
    {
        private readonly StubFetcher _inner;
        private readonly string _throwOn;

        public ThrowingFetcher(StubFetcher inner, string throwOn)
        {
            _inner = inner;
            _throwOn = throwOn;
        }

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null,
            CancellationToken cancellationToken = default)
        {
            if (url.Contains(_throwOn, StringComparison.Ordinal))
            {
                throw new HttpRequestException("模拟的网络故障");
            }

            return _inner.FetchAsync(url, spec, sourceKey, cancellationToken);
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    }

    /// <summary>记录在途请求峰值，用于验证限流生效。</summary>
    private sealed class ConcurrencyTrackingFetcher : IBookSourceFetcher
    {
        private int _current;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public async Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null,
            CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _current);
            // 记录峰值（CAS 循环）
            int observed;
            while (now > (observed = Volatile.Read(ref _max))
                && Interlocked.CompareExchange(ref _max, now, observed) != observed)
            {
                // retry
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                return ("""{"data":[]}""", url);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    }
}
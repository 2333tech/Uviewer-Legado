using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Models;
using BookSource.Core.Services.BookSources;
using BookSourceModel = BookSource.Core.Models.BookSource;
using CoreSearchResult = BookSource.Core.Models.SearchResult;
using CoreTocItem = BookSource.Core.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源搜索：一处搜索多个启用书源中的<b>书籍</b>（按书名/作者关键词），
    /// 结果按书源归组。关键词经各书源的 <c>searchUrl</c> 模板插值后请求对应站点，
    /// <b>不是</b>拿书源名去做匹配。
    /// </summary>
    public sealed class BookSourceSearchService
    {
        private readonly BookSourceService _service;

        public BookSourceSearchService(BookSourceService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>底层书源服务（对话框据此构造下载服务）。</summary>
        public BookSourceService Service => _service;

        /// <summary>书源分组（一个书源一组，组内是该源命中的书）。</summary>
        public sealed record SearchGroup(
            string SourceName,
            string SourceUrl,
            IReadOnlyList<BookSearchHit> Hits);

        /// <summary>单条搜索命中（书名/作者/书籍 URL + 所属书源）。</summary>
        public sealed record BookSearchHit(
            string Name,
            string Author,
            string BookUrl,
            string SourceName,
            string SourceUrl);

        /// <summary>
        /// 在全部启用书源中搜索书籍。
        /// <para><paramref name="progress"/> 回报「已完成/总数」，UI 显示为 "X/X"。
        /// 多源搜索常在数十秒量级（数百源 / 16 并发 / 单源 15s 超时），没有进度
        /// 用户无法区分"正在搜"与"卡死"——这正是本功能第一版被反馈"一直搜索中"的原因之一。</para>
        /// <para>结果<b>边搜边出</b>：<paramref name="onGroup"/> 每命中一个书源立即回调一次
        /// （工作线程，UI 需自行切回 UI 线程），不等全部搜完才刷列表。
        /// <b>这是"搜索永远 0 命中"的根因修复</b>：实测 204 源全量搜索需 ~78s，
        /// 若等 SearchAsync 返回再一次性渲染，用户看到的就是"搜了 78 秒 0 条结果"。
        /// 首个命中通常在 250ms 内出现，流式渲染能让结果立刻可见。</para>
        /// </summary>
        public async Task<IReadOnlyList<SearchGroup>> SearchAsync(
            string keyword,
            int? maxConcurrency = null,
            IProgress<SearchProgress>? progress = null,
            Action<SearchGroup>? onGroup = null,
            CancellationToken token = default)
        {
            var sources = _service.GetSearchableSources();
            if (string.IsNullOrWhiteSpace(keyword) || sources.Count == 0)
            {
                progress?.Report(new SearchProgress(0, 0));
                return Array.Empty<SearchGroup>();
            }

            // 限流与库侧 BookSourceLimits 对齐（默认 16）。这里自己控流而不用库的
            // SearchAllAsync，是因为要"边搜边出"——后者一次性返回全部结果，途中无中间态可展示。
            var limit = Math.Clamp(
                maxConcurrency ?? BookSourceLimits.DefaultConcurrency, 1, BookSourceLimits.MaxConcurrency);
            using var sem = new SemaphoreSlim(limit, limit);

            var channel = System.Threading.Channels.Channel.CreateUnbounded<SearchGroup>();
            var completed = 0;
            progress?.Report(new SearchProgress(0, sources.Count));

            var workers = sources.Select(async source =>
            {
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var results = await SearchOneAsync(source, keyword, token).ConfigureAwait(false);

                    // 无论命中与否都推进进度：用户关心的是"搜过几个源"，不是"找到几条"
                    progress?.Report(new SearchProgress(Interlocked.Increment(ref completed), sources.Count));

                    if (results.Count > 0)
                    {
                        await channel.Writer
                            .WriteAsync(BuildGroup(source, results), token)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    sem.Release();
                }
            }).ToArray();

            // 汇总协程：全部 worker 结束后关闭 Channel，ReadAllAsync 随之正常结束
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, token);

            var groups = new List<SearchGroup>();
            await foreach (var group in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                groups.Add(group);

                // 单个回调异常不能带崩整次搜索（回调是 UI 代码）
                try
                {
                    onGroup?.Invoke(group);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Legado onGroup callback failed: {ex.Message}");
                }
            }

            return groups;
        }

        /// <summary>把库侧搜索结果整形为一个书源分组。</summary>
        private static SearchGroup BuildGroup(BookSourceModel source, List<CoreSearchResult> results) =>
            new(source.BookSourceName, source.BookSourceUrl,
                results.Select(r => new BookSearchHit(
                    r.Name ?? string.Empty,
                    r.Author ?? string.Empty,
                    r.BookUrl ?? string.Empty,
                    source.BookSourceName,
                    source.BookSourceUrl)).ToList());

        /// <summary>
        /// 单源搜索：书源级异常不外抛（一个源挂掉不该让整次搜索失败），
        /// 失败时该源贡献空结果，与"该源没有命中"对 UI 等价。
        /// </summary>
        private async Task<List<CoreSearchResult>> SearchOneAsync(
            BookSourceModel source, string keyword, CancellationToken token)
        {
            try
            {
                var search = new SearchService();
                return await search.SearchAsync(source, keyword, 1, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 只有"用户主动取消"才向上传播；HttpClient 超时抛的 TaskCanceledException
                // 会走到下面的兜底（与库侧同一处理，否则单个卡住的源会炸掉整次搜索）
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Legado search failed for {source.BookSourceName}: {ex.Message}");
                return new List<CoreSearchResult>();
            }
        }

        /// <summary>用搜索结果直接打开一本书（深链：不需再走搜索结果页）。</summary>
        public BookSourceReadingSession CreateSession(BookSearchHit hit)
        {
            var source = _service.Sources.GetByUrl(hit.SourceUrl)
                ?? throw new InvalidOperationException($"书源不存在：{hit.SourceUrl}");

            return new BookSourceReadingSession(_service, source, hit.BookUrl);
        }
    }
}
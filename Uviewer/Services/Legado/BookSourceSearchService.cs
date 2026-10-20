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
    /// 书源搜索：一处搜索多个启用书源，结果按书源归组。
    /// <para>并发策略沿用库侧 <c>SearchService.SearchAllAsync</c>（信号量限流 16，
    /// 按输入书源顺序合并 ⇒ 与串行结果一致）；此处只做结果整形与能力标注。</para>
    /// </summary>
    public sealed class BookSourceSearchService
    {
        private readonly BookSourceService _service;

        public BookSourceSearchService(BookSourceService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>搜索结果分组（一个书源一组，组内是该源命中的书）。</summary>
        public sealed record SearchGroup(
            string SourceName,
            string SourceUrl,
            IReadOnlyList<BookSearchHit> Hits);

        /// <summary>单条搜索命中（书名/作者/详情 URL）。</summary>
        public sealed record BookSearchHit(
            string Name,
            string Author,
            string BookUrl,
            string SourceName,
            string SourceUrl);

        /// <summary>
        /// 在全部启用且当前引擎可跑通的书源中搜索。
        /// <para>结果按书源分组并丢掉空组，避免 UI 出现"某源 0 条"的噪声标题。</para>
        /// </summary>
        public async Task<IReadOnlyList<SearchGroup>> SearchAsync(
            string keyword, int? maxConcurrency = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Array.Empty<SearchGroup>();
            }

            var sources = _service.Sources.GetEnabled()
                .Where(s => !string.IsNullOrWhiteSpace(s.SearchUrl) && s.RuleSearch is not null)
                .ToList();
            if (sources.Count == 0)
            {
                return Array.Empty<SearchGroup>();
            }

            // 每源一个任务，库侧按 BookSourceLimits 限流；这里不额外串行化
            var tasks = sources.Select(async s =>
            {
                var results = await SearchOneAsync(s, keyword, token).ConfigureAwait(false);
                return (Source: s, Results: results);
            }).ToArray();

            var all = await Task.WhenAll(tasks).ConfigureAwait(false);

            var groups = new List<SearchGroup>(all.Length);
            foreach (var (source, results) in all)
            {
                if (results.Count == 0)
                {
                    continue;
                }

                groups.Add(new SearchGroup(
                    source.BookSourceName,
                    source.BookSourceUrl,
                    results.Select(r => new BookSearchHit(
                        r.Name ?? string.Empty,
                        r.Author ?? string.Empty,
                        r.BookUrl ?? string.Empty,
                        source.BookSourceName,
                        source.BookSourceUrl)).ToList()));
            }

            return groups;
        }

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Legado search failed for {source.BookSourceName}: {ex.Message}");
                return new List<CoreSearchResult>();
            }
        }

        /// <summary>用搜索结果直接打开一本书（深链：B 类源不经搜索也能读）。</summary>
        public BookSourceReadingSession CreateSession(BookSearchHit hit)
        {
            var source = _service.Sources.GetByUrl(hit.SourceUrl)
                ?? throw new InvalidOperationException($"书源不存在：{hit.SourceUrl}");

            return new BookSourceReadingSession(_service, source, hit.BookUrl);
        }
    }
}

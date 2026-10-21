using Uviewer.Services.Legado.Models;
// 根命名空间为 Uviewer.Services.Legado，类型 BookSource 不能作简单名（CS0118），用别名。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 搜索编排服务（阶段 1 入口）：编译 searchUrl → 抓取 → 规则引擎解析 → SearchResult 列表。
/// 可注入 IBookSourceFetcher（测试用静态内容）。
/// <para>并发模型：每次 <see cref="SearchAsync"/> 调用内部新建 RuleEngine / UrlAnalyzer，
/// 使并发搜索多书源时各自持有独立状态（Jint 引擎非线程安全），无需加锁。</para>
/// </summary>
public sealed class SearchService
{
    private readonly IBookSourceFetcher _fetcher;

    /// <summary>脚本状态仓库（阶段 4-2）：搜索链里 <c>source.getVariable</c>/<c>java.put</c> 的落点。</summary>
    private readonly SourceStateStore _store;

    /// <summary>Cookie 仓库（阶段 4-3）：与阅读链/静态请求共用，登录态才能贯通。</summary>
    private readonly ISourceCookieStore _cookies;

    public SearchService(
        IBookSourceFetcher? fetcher = null,
        SourceStateStore? store = null,
        ISourceCookieStore? cookies = null)
    {
        _fetcher = fetcher ?? new HttpBookSourceFetcher();
        _store = store ?? SourceStateStore.InMemory;
        _cookies = cookies ?? SourceCookieStore.Memory;
    }

    /// <summary>
    /// 按关键词在指定书源上搜索（page 从 1 开始）。步骤：
    /// ① 无 searchUrl 或 ruleSearch → 空列表；② 编译 URL 模板（七步管线，见 UrlAnalyzer）；
    /// ③ 请求 URL 绝对化（absolute_url：去 query+fragment，与解析用的 resolve_url 分工）；
    /// ④ 抓取；⑤ 规则引擎解析（新建实例，防并发共享 Jint）。
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(
        BookSourceModel source, string key, int page = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.SearchUrl) || source.RuleSearch is null)
        {
            return new List<SearchResult>();
        }

        var spec = new UrlAnalyzer().BuildSpec(source.SearchUrl, key, page, source.BookSourceUrl);
        if (string.IsNullOrWhiteSpace(spec.Url))
        {
            return new List<SearchResult>();
        }

        // 请求 URL 绝对化（reader：发起请求用 absolute_url）
        var url = UrlUtil.AbsoluteUrl(source.BookSourceUrl, spec.Url);
        if (url.Length == 0)
        {
            return new List<SearchResult>();
        }

        // 阶段 4-3：限速随请求走（与阅读链共用同一把书源闸门）
        spec = spec with { ConcurrentRate = source.ConcurrentRate };

        var (content, baseUrl) = await _fetcher
            .FetchAsync(url, spec, source.BookSourceUrl, cancellationToken).ConfigureAwait(false);

        return new RuleEngine
        {
            Store = _store,
            Fetcher = _fetcher,
            Cookies = _cookies,
        }.SearchBooks(source, content, baseUrl);
    }

    /// <summary>
    /// 并发搜索多个书源（典型场景：在全部启用书源中检索）。返回所有书源结果汇总。
    /// <para>并发控制：信号量限制同时进行的请求数（默认 16，见 BookSourceLimits）——
    /// 与 reader 的导出并发度对齐。<paramref name="maxConcurrency"/> 为 null 时用默认值，
    /// 否则调用方可在 1~20 间调节。</para>
    /// <para>结果合并：按输入书源顺序拼接（Task.WhenAll 保序），与串行搜索结果一致。</para>
    /// <para><paramref name="progress"/> 每完成一个书源回调一次
    /// <c>(已完成, 总数)</c>——多源搜索动辄几十秒，UI 必须能显示"X/X"进度，
    /// 否则用户无法区分"正在搜"与"卡死"。回调在完成时的工作线程上触发，
    /// 调用方负责切回 UI 线程（对标 IProgress 的约定）。</para>
    /// </summary>
    public async Task<List<SearchResult>> SearchAllAsync(
        IEnumerable<BookSourceModel> sources,
        string key,
        int? maxConcurrency = null,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 预过滤：无 searchUrl / ruleSearch 的书源不参与
        var list = sources
            .Where(s => !string.IsNullOrWhiteSpace(s.SearchUrl) && s.RuleSearch is not null)
            .ToList();
        if (list.Count == 0)
        {
            progress?.Report(new SearchProgress(0, 0));
            return new List<SearchResult>();
        }

        var limit = Math.Clamp(maxConcurrency ?? BookSourceLimits.DefaultConcurrency, 1, BookSourceLimits.MaxConcurrency);
        using var sem = new SemaphoreSlim(limit, limit);
        var done = 0;
        progress?.Report(new SearchProgress(0, list.Count));

        var tasks = list.Select(async s =>
        {
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SearchAsync(s, key, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 只有"用户主动取消"才向上传播
                throw;
            }
            catch
            {
                // 其余一律吞掉并返回空结果：单源失败（站点失效 / DNS 失败 / SSL 错误 /
                // **HttpClient 超时抛的 TaskCanceledException**）不该让整次搜索失败。
                // 特别注意超时：TaskCanceledException 继承自 OperationCanceledException，
                // 若不加 when 过滤就会被当成"用户取消"重新抛出——204 个源里任意一个
                // 卡满 15s 就会让整次搜索崩掉，表现为 UI 一直搜不到结果。
                return new List<SearchResult>();
            }
            finally
            {
                sem.Release();
                progress?.Report(new SearchProgress(Interlocked.Increment(ref done), list.Count));
            }
        });

        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        var result = new List<SearchResult>(batches.Sum(b => b.Count));
        foreach (var b in batches)
        {
            result.AddRange(b);
        }

        return result;
    }
}

/// <summary>多源搜索进度（已完成书源数 / 总书源数；UI 以 "X/X" 形式展示）。</summary>
/// <param name="Completed">已返回（命中或失败）的书源数量。</param>
/// <param name="Total">参与本次搜索的书源总数。</param>
public readonly record struct SearchProgress(int Completed, int Total)
{
    /// <summary>"3/186" 形式，供 UI 直接显示。</summary>
    public override string ToString() => $"{Completed}/{Total}";
}

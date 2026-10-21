namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 并发与容量上限（数值对齐 reader api/commands 的常量）。
/// <para>概念区分（重要）：reader 的 <c>concurrentRate</c> 是**限速**（毫秒/窗口），不是并发度；
/// 并发度来自调用方——导出 16、批量缓存章节 24（这里取 16）、可用性扫描上限 20。
/// 本类的常量就是后者：真正的并行度上限。</para>
/// </summary>
public static class BookSourceLimits
{
    /// <summary>默认并发度（reader export.rs:176 EXPORT_CONCURRENCY = 16）。</summary>
    public const int DefaultConcurrency = 16;

    /// <summary>并发上限（reader MAX_AVAILABLE_CONCURRENT_COUNT = 20；调用方可用 maxConcurrency 在此之间调节）。</summary>
    public const int MaxConcurrency = 20;

    /// <summary>
    /// 多源书籍搜索的默认并发度。
    /// <para><b>为什么比 DefaultConcurrency 高</b>：一次搜索会同时请求几十上百个<b>不同站点</b>，
    /// 全局限速闸门（<c>RateLimitedFetcher</c>）按源隔离——每个源自身仍是 1 个并发请求，
    /// 调大全局并发不会放大对任何单一站点的压力，只缩短"最慢的那批源"拖住总时长的等待。
    /// 32 是经验值：实测数百源搜索总时长约减半，内存/套接字开销可忽略（每路只是一个挂起的 Task）。</para>
    /// </summary>
    public const int SearchConcurrency = 32;

    /// <summary>多源搜索的并发硬上限（调用方可用 maxConcurrency 在 1..此值 之间调节）。</summary>
    public const int SearchMaxConcurrency = 64;

    /// <summary>整本下载的默认章节抓取并发度（下载是打同一站点的长任务，比搜索保守）。</summary>
    public const int DownloadConcurrencyDefault = 8;

    /// <summary>整本下载的并发硬上限：打的是同一个站点，调太高容易触发风控/封禁。</summary>
    public const int DownloadMaxConcurrency = 16;

    /// <summary>
    /// 目录多页并发度。reader 里多页是串行 for 循环，这里改为有界并行
    /// （结果与串行一致：按 URL 顺序合并，见 BookSourceEngine.GetTocAsync）；
    /// 值取小（4）以照顾站点承受能力——多页目录抓取本质是打同一个站点的连续分页。
    /// </summary>
    public const int TocPageConcurrency = 4;
}

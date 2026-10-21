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
    /// 目录多页并发度。reader 里多页是串行 for 循环，这里改为有界并行
    /// （结果与串行一致：按 URL 顺序合并，见 BookSourceEngine.GetTocAsync）；
    /// 值取小（4）以照顾站点承受能力——多页目录抓取本质是打同一个站点的连续分页。
    /// </summary>
    public const int TocPageConcurrency = 4;
}

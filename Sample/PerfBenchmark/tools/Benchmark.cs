using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;

namespace PerfBench;

/// <summary>
/// 基准测量：量化"全书规模处理"与"窗口规模处理"的成本差，
/// 用数据决定是否值得引入"只处理前 1-2 章 / 后 3-5 章"的按需加载优化。
///
/// <para><b>要回答的问题</b>：一次净化/排版操作，如果作用于整本书 vs 只作用于
/// 目标章附近的一个窗口，耗时差多少？差得越多，按需优化越值得；差得少，
/// 引入窗口机制反而增加复杂度却不解决问题——那就<b>不优化</b>。</para>
///
/// <para><b>测量维度</b>：
/// <list type="bullet">
/// <item>① 净化：全书 N 章 vs 窗口 7 章（1-2 前 + 3-5 后）的耗时与分配量；</item>
/// <item>② 章节切分（TXT 分章）：全书 vs 窗口；</item>
/// <item>③ 文本改写（模拟排版修改：把每章正文重新拼装）；</item>
/// <item>④ 端到端模拟"用户点下一章"：全量刷新 vs 窗口刷新。</item>
/// </list></para>
/// </summary>
internal static class Benchmark
{
    /// <summary>窗口大小：前 2 章 + 后 5 章 = 7 章（用户要求：前方 1-2、后续 3-5）。</summary>
    private const int WindowBefore = 2;
    private const int WindowAfter = 5;

    public static async Task RunAsync(string sampleDir, string rulesDir, string resultsDir)
    {
        Directory.CreateDirectory(resultsDir);

        // ── 载入样例 ────────────────────────────────────────────
        var manifestPath = Path.Combine(sampleDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine("找不到样例 manifest，请先运行 sample 生成。");
            return;
        }

        var manifest = JsonSerializer.Deserialize<SampleGenerator.Manifest>(
            await File.ReadAllTextAsync(manifestPath))!;

        var chaptersDir = Path.Combine(sampleDir, "chapters");
        var chapterFiles = manifest.Chapters
            .OrderBy(c => c.Index)
            .Select(c => Path.Combine(chaptersDir, c.File))
            .Where(File.Exists)
            .ToList();

        var chapterHtml = new List<string>(chapterFiles.Count);
        foreach (var f in chapterFiles)
        {
            chapterHtml.Add(await File.ReadAllTextAsync(f));
        }

        var fullBookHtml = File.Exists(Path.Combine(sampleDir, "full-book.html"))
            ? await File.ReadAllTextAsync(Path.Combine(sampleDir, "full-book.html"))
            : string.Join("\n", chapterHtml);

        // 章节切分场景要的是"带章节标题的整本文本"——
        // 抓下来的正文里标题已被引擎剥掉，直接拿来切分会得到 0 章（测不出东西）。
        // 故按目录里的真实章名重建一份带标题的整本（贴近本地 TXT 的形态）。
        var titledBook = new StringBuilder();
        for (var i = 0; i < chapterHtml.Count && i < manifest.Chapters.Count; i++)
        {
            titledBook.Append(manifest.Chapters[i].Name).Append('\n');
            titledBook.Append(chapterHtml[i]).Append('\n');
        }

        var fullBookText = titledBook.ToString();

        // ── 载入规则 ────────────────────────────────────────────
        // 规则管理器只能从"自己的文件"加载，故把样例规则复制成临时文件再加载
        // （复制而非直接指过去：避免误调用 SaveAsync 把样例覆盖掉）。
        var tmpReplace = Path.Combine(rulesDir, ".tmp-replace-rules.json");
        var tmpTitle = Path.Combine(rulesDir, ".tmp-title-rules.json");
        File.Copy(Path.Combine(rulesDir, "replace-rules.json"), tmpReplace, overwrite: true);
        File.Copy(Path.Combine(rulesDir, "title-rules.json"), tmpTitle, overwrite: true);

        var replaceRules = new ReplaceRuleManager(tmpReplace);
        await replaceRules.LoadAsync();
        var titleRules = new TitleRuleManager(tmpTitle);
        await titleRules.LoadAsync();

        var rules = replaceRules.GetAll();
        var titleRuleList = titleRules.GetAll();

        Console.WriteLine($"样例：《{manifest.BookName}》{manifest.SavedChapters} 章 / {manifest.TotalBytes / 1024} KB");
        Console.WriteLine($"规则：净化 {rules.Count} 条（启用 {rules.Count(r => r.IsEnabled)}），" +
                          $"标题 {titleRuleList.Count} 条（启用 {titleRuleList.Count(r => r.IsEnabled)}）");
        if (rules.Count == 0)
        {
            Console.WriteLine("警告：净化规则为 0，净化场景将退化为空转快路径，数字无意义。");
        }

        Console.WriteLine($"窗口：前 {WindowBefore} 章 + 后 {WindowAfter} 章 = {WindowBefore + WindowAfter + 1} 章\n");

        var results = new List<BenchResult>();

        // ══════════════════════════════════════════════════════
        // ① 净化：全书 vs 窗口
        // ══════════════════════════════════════════════════════
        var purifier = new ContentPurifier { Rules = rules };
        var ctx = new PurifyContext(manifest.BookName, manifest.BookUrl, manifest.SourceName, null);

        results.Add(Measure("purify_all_chapters", () =>
        {
            var sb = new StringBuilder();
            foreach (var html in chapterHtml)
            {
                sb.Append(purifier.Apply(html, in ctx));
            }

            return sb.Length;
        }));

        results.Add(Measure("purify_window_7_chapters", () =>
        {
            // 取中间一章为目标，窗口 = 前 2 + 后 5
            var target = chapterHtml.Count / 2;
            var from = Math.Max(0, target - WindowBefore);
            var to = Math.Min(chapterHtml.Count, target + WindowAfter + 1);

            var sb = new StringBuilder();
            for (var i = from; i < to; i++)
            {
                sb.Append(purifier.Apply(chapterHtml[i], in ctx));
            }

            return sb.Length;
        }));

        // ══════════════════════════════════════════════════════
        // ② 章节切分（TXT 分章）：全书 vs 窗口
        // ══════════════════════════════════════════════════════
        var enabledTitleRules = titleRules.GetEnabledOrdered();

        results.Add(Measure("split_all_chapters", () =>
        {
            var r = TxtChapterSplitter.Split(fullBookText, enabledTitleRules);
            return r.Chapters.Count;
        }));

        results.Add(Measure("split_window_7_chapters", () =>
        {
            // 只对窗口内的章节文本做切分（标题 + 正文一起）
            var target = chapterHtml.Count / 2;
            var from = Math.Max(0, target - WindowBefore);
            var to = Math.Min(chapterHtml.Count, target + WindowAfter + 1);

            var windowText = new StringBuilder();
            for (var i = from; i < to && i < manifest.Chapters.Count; i++)
            {
                windowText.Append(manifest.Chapters[i].Name).Append('\n');
                windowText.Append(chapterHtml[i]).Append('\n');
            }

            var r = TxtChapterSplitter.Split(windowText.ToString(), enabledTitleRules);
            return r.Chapters.Count;
        }));

        // ══════════════════════════════════════════════════════
        // ③ 文本改写（模拟排版修改：整本重新拼装 vs 只改窗口）
        // ══════════════════════════════════════════════════════
        results.Add(Measure("rewrite_all_chapters", () =>
        {
            var sb = new StringBuilder(fullBookHtml.Length);
            foreach (var html in chapterHtml)
            {
                sb.Append(html.Replace("　", " "));
            }

            return sb.Length;
        }));

        results.Add(Measure("rewrite_window_7_chapters", () =>
        {
            var target = chapterHtml.Count / 2;
            var from = Math.Max(0, target - WindowBefore);
            var to = Math.Min(chapterHtml.Count, target + WindowAfter + 1);
            var sb = new StringBuilder();
            for (var i = from; i < to; i++)
            {
                sb.Append(chapterHtml[i].Replace("　", " "));
            }

            return sb.Length;
        }));

        // ══════════════════════════════════════════════════════
        // ④ 端到端模拟"点下一章"：全书重建 vs 窗口重建
        // ══════════════════════════════════════════════════════
        results.Add(Measure("next_chapter_full_refresh", () =>
        {
            // 模拟：整本内容重新净化 + 重新切分 + 重新排版
            var sb = new StringBuilder();
            for (var i = 0; i < chapterHtml.Count; i++)
            {
                sb.Append(manifest.Chapters[i].Name).Append('\n');
                sb.Append(purifier.Apply(chapterHtml[i], in ctx)).Append('\n');
            }

            var r = TxtChapterSplitter.Split(sb.ToString(), enabledTitleRules);
            return r.Chapters.Count;
        }));

        results.Add(Measure("next_chapter_window_refresh", () =>
        {
            // 模拟：只重算目标附近窗口
            var target = chapterHtml.Count / 2;
            var from = Math.Max(0, target - WindowBefore);
            var to = Math.Min(chapterHtml.Count, target + WindowAfter + 1);

            var sb = new StringBuilder();
            for (var i = from; i < to; i++)
            {
                sb.Append(purifier.Apply(chapterHtml[i], in ctx));
            }

            var r = TxtChapterSplitter.Split(sb.ToString(), enabledTitleRules);
            return r.Chapters.Count;
        }));

        // ══════════════════════════════════════════════════════
        // ⑤ 切换净化开关：全书重算 vs 窗口重算（本次优化的直接目标）
        // ══════════════════════════════════════════════════════
        results.Add(Measure("toggle_purify_full_recompute", () =>
        {
            // 全书重算：每章都重新过一遍正则替换
            var total = 0;
            foreach (var html in chapterHtml)
            {
                total += purifier.Apply(html, in ctx).Length;
            }

            return total;
        }));

        results.Add(Measure("toggle_purify_window_recompute", () =>
        {
            // 窗口重算：只重算当前章前后（与实现一致：前 2 + 后 4 + 当前 = 7 章）
            var target = chapterHtml.Count / 2;
            var from = Math.Max(0, target - 2);
            var to = Math.Min(chapterHtml.Count - 1, target + 4);

            var total = 0;
            for (var i = from; i <= to; i++)
            {
                total += purifier.Apply(chapterHtml[i], in ctx).Length;
            }

            return total;
        }));

        // ── 输出 ────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══ 基准结果 ══════════════════════════════════════════════");
        Console.WriteLine($"{"场景":<32}{"耗时(ms)":>12}{"分配(MB)":>12}{"结果量":>12}");
        Console.WriteLine(new string('─', 70));

        foreach (var r in results)
        {
            Console.WriteLine($"{r.Name:<32}{r.ElapsedMs,12:F1}{r.AllocatedMb,12:F2}{r.ResultSize,12}");
        }

        var report = new BenchReport
        {
            Sample = new BenchSampleInfo
            {
                BookName = manifest.BookName,
                SourceName = manifest.SourceName,
                Chapters = manifest.SavedChapters,
                TotalBytes = manifest.TotalBytes,
                FullBookBytes = Encoding.UTF8.GetByteCount(fullBookHtml),
                ReplaceRuleCount = rules.Count,
                TitleRuleCount = titleRuleList.Count,
            },
            Window = new BenchWindowInfo { Before = WindowBefore, After = WindowAfter },
            Results = results,
        };

        var outPath = Path.Combine(resultsDir, "benchmark.json");
        await File.WriteAllTextAsync(outPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        // ── 结论 ────────────────────────────────────────────────
        Console.WriteLine();
        const double WorthThreshold = 2.0;

        var full = results.FirstOrDefault(r => r.Name == "next_chapter_full_refresh");
        var win = results.FirstOrDefault(r => r.Name == "next_chapter_window_refresh");
        if (full is not null && win is not null && win.ElapsedMs > 0)
        {
            var ratio = full.ElapsedMs / win.ElapsedMs;
            var allocRatio = full.AllocatedMb <= 0 ? 0 : full.AllocatedMb / Math.Max(0.0001, win.AllocatedMb);

            Console.WriteLine($"端到端（点下一章）耗时比：全书 / 窗口 = {ratio:F1}×");
            Console.WriteLine($"端到端分配量比：全书 / 窗口 = {allocRatio:F1}×");
        }

        var toggleFull = results.FirstOrDefault(r => r.Name == "toggle_purify_full_recompute");
        var toggleWin = results.FirstOrDefault(r => r.Name == "toggle_purify_window_recompute");
        if (toggleFull is not null && toggleWin is not null && toggleWin.ElapsedMs > 0)
        {
            var ratio = toggleFull.ElapsedMs / toggleWin.ElapsedMs;
            Console.WriteLine($"切换净化开关耗时比：全书 / 窗口 = {ratio:F1}×");
        }

        // 判定：两个关键场景都达到阈值才认为值得优化
        var ratios = new List<double>();
        if (full is not null && win is not null && win.ElapsedMs > 0)
        {
            ratios.Add(full.ElapsedMs / win.ElapsedMs);
        }

        if (toggleFull is not null && toggleWin is not null && toggleWin.ElapsedMs > 0)
        {
            ratios.Add(toggleFull.ElapsedMs / toggleWin.ElapsedMs);
        }

        if (ratios.Count > 0)
        {
            var minRatio = ratios.Min();
            if (minRatio >= WorthThreshold)
            {
                Console.WriteLine($"\n结论：【值得优化】最差场景窗口方案仍快 {minRatio:F1}×" +
                                 $"（阈值 {WorthThreshold:F1}×）→ 已应用窗口式按需加载。");
            }
            else
            {
                Console.WriteLine($"\n结论：【不优化】最差场景窗口方案仅快 {minRatio:F1}×，" +
                                 $"未达阈值 {WorthThreshold:F1}× → 不引入窗口机制。");
            }
        }

        Console.WriteLine($"\n结果已写入：{outPath}");

        // 清理临时规则文件
        TryDelete(Path.Combine(rulesDir, ".tmp-replace-rules.json"));
        TryDelete(Path.Combine(rulesDir, ".tmp-title-rules.json"));
    }

    /// <summary>跑一次场景并记录耗时/分配量。</summary>
    private static BenchResult Measure(string name, Func<int> action)
    {
        // 预热一次（JIT + 正则缓存），避免把首次编译算进耗时
        action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var size = action();
        sw.Stop();
        var after = GC.GetTotalAllocatedBytes(precise: true);

        var result = new BenchResult
        {
            Name = name,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            AllocatedMb = (after - before) / 1024.0 / 1024.0,
            ResultSize = size,
        };

        Console.WriteLine($"  [跑] {name}: {result.ElapsedMs:F1}ms / {result.AllocatedMb:F2}MB");
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响基准结论
        }
    }

    internal sealed record BenchResult
    {
        public string Name { get; init; } = string.Empty;
        public double ElapsedMs { get; init; }
        public double AllocatedMb { get; init; }
        public int ResultSize { get; init; }
    }

    internal sealed record BenchSampleInfo
    {
        public string BookName { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public int Chapters { get; init; }
        public long TotalBytes { get; init; }
        public int FullBookBytes { get; init; }
        public int ReplaceRuleCount { get; init; }
        public int TitleRuleCount { get; init; }
    }

    internal sealed record BenchWindowInfo
    {
        public int Before { get; init; }
        public int After { get; init; }
    }

    internal sealed record BenchReport
    {
        public BenchSampleInfo Sample { get; init; } = new();
        public BenchWindowInfo Window { get; init; } = new();
        public List<BenchResult> Results { get; init; } = new();
    }
}

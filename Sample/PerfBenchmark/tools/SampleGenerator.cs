using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace PerfBench;

/// <summary>
/// 样例生成：从<b>真实书源</b>抓取真实小说正文，落地成基准用的样例集。
///
/// <para>为什么必须用真实文本：净化规则的成本与文本形态强相关（HTML 标签密度、
/// 实体数量、行长）。用合成文本测出来的数字不能代表真实卡顿场景。</para>
///
/// <para>产物：
/// <list type="bullet">
/// <item><c>text/chapters/*.html</c> —— 抓到的原始章节 HTML（未经净化）；</item>
/// <item><c>text/full-book.html</c> —— 合成整本（章节按序拼接），供"全书规模"场景测量；</item>
/// <item><c>text/manifest.json</c> —— 元信息（书名/书源/章数/字节数）。</item>
/// </list></para>
/// </summary>
internal static class SampleGenerator
{
    /// <summary>生成样例集。</summary>
    /// <param name="outputDir">样例输出目录（Sample/PerfBenchmark/text）。</param>
    /// <param name="bookSourcePath">书源 JSON 路径。</param>
    /// <param name="keyword">搜索关键词。</param>
    /// <param name="chapterCount">要抓的章节数。</param>
    public static async Task<int> GenerateAsync(
        string outputDir, string bookSourcePath, string keyword, int chapterCount)
    {
        var chaptersDir = Path.Combine(outputDir, "chapters");
        Directory.CreateDirectory(chaptersDir);

        var json = await File.ReadAllTextAsync(bookSourcePath);
        var all = JsonSerializer.Deserialize<List<BookSourceModel>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        // 挑一个"纯规则"源：不含 @js / @get: 变量桥，正文可稳定抓取（阶段 4 之外的源）
        var candidates = all
            .Where(s => s.Enabled != false && !string.IsNullOrWhiteSpace(s.SearchUrl) && s.RuleSearch is not null)
            .Where(s => !BookSourceCapabilities.ReadingNeedsStage4(s))
            .Where(s => !string.IsNullOrEmpty(s.RuleContent?.Content)
                        && !s.RuleContent!.Content!.Contains("@js", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"候选纯规则源：{candidates.Count} 个");

        var search = new SearchService();
        var engine = new BookSourceEngine();

        foreach (var source in candidates)
        {
            try
            {
                var hits = await search.SearchAsync(source, keyword, 1);
                var hit = hits.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.BookUrl));
                if (hit is null)
                {
                    continue;
                }

                var info = await engine.GetBookInfoAsync(source, hit.BookUrl!, null);
                var tocUrl = string.IsNullOrWhiteSpace(info?.TocUrl) ? hit.BookUrl! : info!.TocUrl!;
                var chapters = await engine.GetTocAsync(source, tocUrl);
                var targets = chapters
                    .Where(c => !c.IsVolume && !string.IsNullOrWhiteSpace(c.ChapterUrl))
                    .Take(chapterCount)
                    .ToList();

                if (targets.Count == 0)
                {
                    continue;
                }

                Console.WriteLine($"命中《{hit.Name}》（{source.BookSourceName}），目录 {chapters.Count} 章，抓取前 {targets.Count} 章…");

                var sw = Stopwatch.StartNew();
                var saved = 0;
                var totalBytes = 0L;
                var manifest = new List<ChapterManifest>();

                for (var i = 0; i < targets.Count; i++)
                {
                    var content = await engine.GetContentAsync(source, targets[i].ChapterUrl!, hit.BookUrl);
                    if (content is null || string.IsNullOrWhiteSpace(content.Content))
                    {
                        continue;
                    }

                    var file = Path.Combine(chaptersDir, $"ch{i + 1:D4}.html");
                    await File.WriteAllTextAsync(file, content.Content, new UTF8Encoding(false));

                    var bytes = Encoding.UTF8.GetByteCount(content.Content);
                    totalBytes += bytes;
                    saved++;

                    manifest.Add(new ChapterManifest(
                        i + 1,
                        targets[i].ChapterName ?? $"第{i + 1}章",
                        $"ch{i + 1:D4}.html",
                        bytes,
                        targets[i].ChapterUrl!));

                    if (saved % 10 == 0)
                    {
                        Console.WriteLine($"  已保存 {saved}/{targets.Count} 章（{sw.ElapsedMilliseconds}ms）");
                    }
                }

                if (saved < 5)
                {
                    Console.WriteLine($"  有效章节太少（{saved}），换下一个源");
                    continue;
                }

                // 合成整本：章节按序拼接（模拟"一次性加载全书"的极端场景）
                var full = new StringBuilder();
                foreach (var m in manifest)
                {
                    var body = await File.ReadAllTextAsync(Path.Combine(chaptersDir, m.File));
                    full.Append(body).Append('\n');
                }

                await File.WriteAllTextAsync(Path.Combine(outputDir, "full-book.html"), full.ToString(), new UTF8Encoding(false));

                var meta = new Manifest
                {
                    BookName = hit.Name,
                    Author = hit.Author,
                    SourceName = source.BookSourceName,
                    BookUrl = hit.BookUrl,
                    TotalChaptersInToc = chapters.Count,
                    SavedChapters = saved,
                    TotalBytes = totalBytes,
                    FetchMs = sw.ElapsedMilliseconds,
                    Chapters = manifest,
                };

                await File.WriteAllTextAsync(
                    Path.Combine(outputDir, "manifest.json"),
                    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));

                Console.WriteLine($"\n✓ 样例生成完成：{saved} 章 / {totalBytes / 1024} KB，用时 {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"  目录：{outputDir}");
                return saved;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {source.BookSourceName}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Console.WriteLine("所有源都失败，未生成样例。");
        return 0;
    }

    internal sealed record ChapterManifest(int Index, string Name, string File, int Bytes, string Url);

    internal sealed record Manifest
    {
        public string BookName { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public string BookUrl { get; init; } = string.Empty;
        public int TotalChaptersInToc { get; init; }
        public int SavedChapters { get; init; }
        public long TotalBytes { get; init; }
        public long FetchMs { get; init; }
        public List<ChapterManifest> Chapters { get; init; } = new();
    }
}

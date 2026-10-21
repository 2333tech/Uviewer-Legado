using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.Models;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Models = Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 4-4：原始条目能力审计（方案 v2 §2.2 / §4-4 验收口径）。
///
/// <para>三条硬约定：</para>
/// <list type="number">
/// <item><b>原始条目口径</b>：一律从 <c>Sample/bookSource_202610192010.json</c> 直读
///   （<see cref="SampleData"/>），不读用户导入副本 <c>booksources.json</c>；</item>
/// <item><b>D1 不许复活</b>：搜索候选过滤（<c>GetSearchableSources</c>）不得误杀任何
///   "阅读链路当前可跑"的源——2026-10-21 前的旧口径按"出现 java.*"整源排除，误杀 12 条；</item>
/// <item><b>导入链路不漂移</b>：同一书源经导入/落盘/重载后，能力评估必须与原始条目完全一致
///   （方案验收「booksources.json 与原始条目的能力评估差异 = 0」）。</item>
/// </list>
/// </summary>
public class Stage4AuditTests
{
    // ============================================================
    // ① 搜索候选过滤 = 能力模型（D1 验收：无误杀）
    // ============================================================

    [Fact]
    public async Task Search_Filter_Passes_Every_ReadingCapable_Source()
    {
        var dir = TempDir();
        try
        {
            // 期望值用**直算**评估得出；服务内部走**缓存**评估——两者相等同时验证了
            // 缓存一致性与"过滤器不比能力模型多杀一条"。
            var raw = await SampleData.AllAsync();
            Assert.Equal(SampleData.TotalCount, raw.Count);

            var expected = raw
                .Where(s => s.IsEnabled
                    && !string.IsNullOrWhiteSpace(s.SearchUrl)
                    && s.RuleSearch is not null
                    && BookSourceCapabilities.AssessDetailed(s).ReadingSupported)
                .Select(s => s.BookSourceUrl)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            // 方案 v2 的验收线：旧口径放行 277、精算 289；4-3 落地后只许更多，不许退回
            Assert.True(expected.Count >= 289, $"搜索候选仅 {expected.Count} 条，低于方案验收线 289");

            // 用真实服务链（临时数据目录写入原始条目）取过滤结果
            await File.WriteAllTextAsync(
                Path.Combine(dir, "booksources.json"),
                BookSourceSerializer.SerializeList(raw));

            await using var service = new BookSourceService(dir);
            await service.EnsureInitializedAsync();

            var actual = service.GetSearchableSources()
                .Select(s => s.BookSourceUrl)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(expected.Count, actual.Count);

            // 误杀明细：可跑但被过滤排除的源（D1 的定义）
            var killed = expected.Except(actual).OrderBy(u => u, StringComparer.Ordinal).ToList();
            Assert.True(killed.Count == 0, $"过滤误杀 {killed.Count} 条可跑源：{string.Join(", ", killed.Take(5))}");

            // 反向也不许多放：被过滤放行但直算判不可跑的源
            var leaked = actual.Except(expected).OrderBy(u => u, StringComparer.Ordinal).ToList();
            Assert.True(leaked.Count == 0, $"过滤多放 {leaked.Count} 条不可跑源：{string.Join(", ", leaked.Take(5))}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ============================================================
    // ② 导入链路不漂移（差异 = 0）
    // ============================================================

    [Fact]
    public async Task Import_Preserves_Capability_Assessment()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"uviewer-audit-{Guid.NewGuid():N}.json");
        try
        {
            var rawByUrl = (await SampleData.AllAsync())
                .GroupBy(s => s.BookSourceUrl, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var manager = new BookSourceManager(tmp);
            await manager.ImportFromFileAsync(SampleData.FilePath);
            var imported = manager.GetAll().ToList();

            // 导入不丢源：条目数与原始条目主键数一致（有重复 URL 会在这里暴露）
            Assert.Equal(rawByUrl.Count, imported.Count);

            var diff = 0;
            foreach (var source in imported)
            {
                Assert.True(rawByUrl.ContainsKey(source.BookSourceUrl), $"导入出现未知主键：{source.BookSourceUrl}");
                if (!SameReport(
                        BookSourceCapabilities.AssessDetailed(source),
                        BookSourceCapabilities.AssessDetailed(rawByUrl[source.BookSourceUrl])))
                {
                    diff++;
                    if (diff <= 3)
                    {
                        Console.WriteLine($"[drift] {source.BookSourceUrl}");
                    }
                }
            }

            Assert.Equal(0, diff);
            manager.Clear();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ============================================================
    // ③ 缓存评估 = 直算评估 + 内容变更即时失效
    // ============================================================

    [Fact]
    public async Task Cached_Assessment_Matches_Direct_And_Tracks_Mutation()
    {
        var raw = await SampleData.AllAsync();

        // 全量：缓存路径与直算路径逐源一致（指纹覆盖所有被读字段才可能全绿）
        foreach (var source in raw)
        {
            var direct = BookSourceCapabilities.AssessDetailed(source);
            var cached = BookSourceCapabilities.AssessCached(source);
            Assert.True(SameReport(direct, cached), $"缓存与直算不一致：{source.BookSourceUrl}");
        }

        // 变更：同一实例上改脚本内容 → 指纹变 → 下次评估必须反映新内容（不许返回旧报告）
        var target = raw[0];
        target.RuleContent ??= new Models.ContentRule();
        target.RuleContent.Content = "<js>java.openUrl(baseUrl)</js>";

        var after = BookSourceCapabilities.AssessCached(target);
        Assert.Contains("openUrl", after.ReadingMissingApis);
        Assert.False(after.ReadingSupported);
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>逐字段比较两份能力报告（record 对 List 成员只比较引用，必须手工比内容）。</summary>
    private static bool SameReport(SourceCapabilityReport a, SourceCapabilityReport b) =>
        a.Flags == b.Flags
        && a.RequiredTier == b.RequiredTier
        && a.NeedsLogin == b.NeedsLogin
        && a.NeedsWebView == b.NeedsWebView
        && a.ReadingNeedsWebView == b.ReadingNeedsWebView
        && a.HasRemoteJsLib == b.HasRemoteJsLib
        && a.ReadingSupported == b.ReadingSupported
        && a.AllSupported == b.AllSupported
        && SameSequence(a.MissingApis, b.MissingApis)
        && SameSequence(a.ReadingMissingApis, b.ReadingMissingApis)
        && SameSequence(a.MissingGlobals, b.MissingGlobals)
        && SameSequence(a.ReadingMissingGlobals, b.ReadingMissingGlobals);

    private static bool SameSequence(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uviewer-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录删除失败不影响测试结果（CI 上偶发的句柄延迟释放）
        }
    }
}

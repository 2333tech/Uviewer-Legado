using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace PerfBench;

/// <summary>
/// 净化时机实证：用可观测的文本长度变化，端到端验证
/// <b>"抓取不净化 → 渲染前净化"</b> 这条契约确实成立。
///
/// <para>验证链路：<c>GetContentAsync</c>（抓取）→ <c>PurifyForDisplay</c>（渲染前）
/// → 总开关关闭 → 再净化。<c>bookUrl</c> 用一个带"广告"字样的本地文件路径模拟。</para>
/// </summary>
internal static class PurifyTimingProof
{
    private sealed class OneShotFetcher : IBookSourceFetcher
    {
        private readonly string _body;

        public OneShotFetcher(string body) => _body = body;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult((_body, url));

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("══ 净化时机实证（抓取 → 渲染前） ══════════════════════");

        // 会删掉"广告"的规则 —— 长度必然变化，便于观测
        var manager = new ReplaceRuleManager();
        manager.Upsert(new ReplaceRule
        {
            Name = "去广告",
            Pattern = "广告",
            Replacement = "",
            IsRegex = false,
            IsEnabled = true,
        });

        var source = new BookSourceModel
        {
            BookSourceUrl = "https://api.example.com",
            BookSourceName = "实证用源",
            RuleContent = new ContentRule { Content = "data.content" },
        };

        const string body = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>正文里有广告</p>\"}}";
        var engine = new BookSourceEngine(new OneShotFetcher(body), new ContentPurifier { Rules = manager.GetAll() });

        // ── ① 抓取阶段 ────────────────────────────────────────
        var fetched = await engine.GetContentAsync(source, "https://api.example.com/ch/1", "https://api.example.com/book/1");
        var raw = fetched?.Content ?? string.Empty;

        Console.WriteLine();
        Console.WriteLine($"① 抓取 GetContentAsync : {raw}   (长度 {raw.Length})");
        Console.WriteLine($"   是否仍是原文       : {(raw.Contains("广告", StringComparison.Ordinal) ? "是（未净化）" : "否")}");

        // ── ② 读取 → 渲染之间 ─────────────────────────────────
        var purified = engine.PurifyForDisplay(raw, "https://api.example.com/book/1", source.BookSourceUrl);
        Console.WriteLine();
        Console.WriteLine($"② 渲染前 PurifyForDisplay: {purified}   (长度 {purified.Length})");
        Console.WriteLine($"   是否已净化    : {(purified.Contains("广告", StringComparison.Ordinal) ? "否" : "是")}");

        // ── ③ 总开关关闭 ──────────────────────────────────────
        var offPurifier = new ContentPurifier { Rules = null };
        var engineOff = new BookSourceEngine(new OneShotFetcher(body), offPurifier);
        var rawAgain = (await engineOff.GetContentAsync(source, "https://api.example.com/ch/1"))?.Content ?? string.Empty;
        var displayOff = engineOff.PurifyForDisplay(rawAgain, "https://api.example.com/book/1", source.BookSourceUrl);

        Console.WriteLine();
        Console.WriteLine($"③ 总开关关闭后 : {displayOff}   (长度 {displayOff.Length})");
        Console.WriteLine($"   是否等于原文        : {(displayOff == rawAgain ? "是（开关生效）" : "否")}");

        // ── 结论 ─────────────────────────────────────────────
        var fetchIsRaw = raw.Contains("广告", StringComparison.Ordinal);
        var purifyWorks = !purified.Contains("广告", StringComparison.Ordinal);
        var switchWorks = displayOff == rawAgain;

        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────");
        Console.WriteLine(fetchIsRaw && purifyWorks && switchWorks
            ? "→ 契约成立：抓取不净化，净化发生在读取→渲染之间，且受总开关控制。"
            : "→ 契约不成立（抓取已净化 / 渲染前未净化 / 开关无效）。");
    }
}
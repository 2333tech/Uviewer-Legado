using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源限速装饰器（阶段 4-3）：按 <see cref="RequestSpec.ConcurrentRate"/> 约束**发起**节奏。
///
/// <para><b>语义（对齐 reader <c>book_service.rs</c> 的 <c>wait_for_rate</c>）</b>：</para>
/// <list type="bullet">
/// <item><c>"N"</c>（串行）：同一书源同时只允许 1 个在途请求，且两次**发起**之间至少间隔 N 毫秒；</item>
/// <item><c>"N/M"</c>（窗口）：每 M 毫秒窗口内最多发起 N 个请求（滑动窗口，不串行化）；</item>
/// <item>空 / <c>"0"</c> / 非法值：不限速（reader 同）。</item>
/// </list>
///
/// <para><b>为什么是装饰器</b>：阶段 4 还要接 WebView 取数（<c>WebViewFetcher</c>）与登录导出，
/// 它们都实现 <see cref="IBookSourceFetcher"/>，套上本类即自动获得限速；引擎/搜索/JS 桥完全不感知。</para>
///
/// <para><b>限速在"发起"处</b>：静态请求、<c>java.ajax/post/connect</c>、将来的 WebView 导航都过闸；
/// WebView 内部子资源不过（reader 同）。<b>图片字节（<see cref="FetchBytesAsync"/>）不过闸</b>——
/// 它没有 <see cref="RequestSpec"/>，且封面/插图往往几十张，按书源间隔串行会让阅读器白屏很久；
/// 这是与"严格全局限速"的有意差异（方案 §6.7 的取舍）。</para>
///
/// <para><b>并行与正确性</b>：闸门状态按书源键隔离（<c>ConcurrentDictionary</c>），
/// 不同书源互不阻塞；同源的等待/释放全部在锁内完成。等待可被取消（<see cref="Task.Delay(int, CancellationToken)"/>），
/// 不会出现"点了取消还要等 10 秒限速"。</para>
/// </summary>
public sealed class RateLimitedFetcher : IBookSourceFetcher, IRichResponseFetcher
{
    private readonly IBookSourceFetcher _inner;
    private readonly ConcurrentDictionary<string, SourceRateGate> _gates = new(StringComparer.Ordinal);

    /// <param name="inner">实际取数实现（HTTP / WebView / 测试替身）。</param>
    public RateLimitedFetcher(IBookSourceFetcher inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>被装饰的取数实现（诊断/测试用）。</summary>
    public IBookSourceFetcher Inner => _inner;

    /// <inheritdoc />
    public async Task<(string Content, string BaseUrl)> FetchAsync(
        string url,
        RequestSpec? spec = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default)
    {
        var gate = GateFor(sourceKey, spec?.ConcurrentRate);
        if (gate is null)
        {
            return await _inner.FetchAsync(url, spec, sourceKey, cancellationToken).ConfigureAwait(false);
        }

        return await gate.RunAsync(
            () => _inner.FetchAsync(url, spec, sourceKey, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <para>图片不过闸（理由见类注释）。</para>
    public Task<byte[]?> FetchBytesAsync(
        string url,
        string? sourceKey = null,
        string? referer = null,
        CancellationToken cancellationToken = default) =>
        _inner.FetchBytesAsync(url, sourceKey, referer, cancellationToken);

    /// <inheritdoc />
    public async Task<FetchResponse> FetchDetailedAsync(
        string url,
        RequestSpec? spec = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default)
    {
        var gate = GateFor(sourceKey, spec?.ConcurrentRate);

        if (_inner is IRichResponseFetcher rich)
        {
            return gate is null
                ? await rich.FetchDetailedAsync(url, spec, sourceKey, cancellationToken).ConfigureAwait(false)
                : await gate.RunAsync(
                    () => rich.FetchDetailedAsync(url, spec, sourceKey, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
        }

        // 测试替身没有状态码：退化为"有内容 = 200 / 空 = 0"。生产路径永远走上面的富抓取分支。
        var (content, baseUrl) = gate is null
            ? await _inner.FetchAsync(url, spec, sourceKey, cancellationToken).ConfigureAwait(false)
            : await gate.RunAsync(
                () => _inner.FetchAsync(url, spec, sourceKey, cancellationToken),
                cancellationToken).ConfigureAwait(false);

        return new FetchResponse(
            content,
            baseUrl,
            content.Length > 0 ? 200 : 0,
            EmptyHeaders);
    }

    /// <summary>空响应头（测试替身路径复用，避免每次分配）。</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(0);

    /// <summary>
    /// 取该请求对应的闸门（同源不同限速配置会各建一个；配置为空返回 null = 不过闸）。
    /// <para>键里带限速配置：书源在运行期改了限速，不会继续用旧间隔的闸门。</para>
    /// </summary>
    private SourceRateGate? GateFor(string? sourceKey, string? rate)
    {
        var gate = SourceRateGate.Create(rate);
        if (gate is null)
        {
            return null;
        }

        var key = SourceStateStore.Normalize(sourceKey) + "\u0001" + rate!.Trim();
        return _gates.GetOrAdd(key, gate);
    }
}

/// <summary>
/// 单书源限速闸门（<see cref="RateLimitedFetcher"/> 的内部状态机，公开以便单测直接驱动）。
/// </summary>
public sealed class SourceRateGate
{
    private readonly object _gate = new();

    // 串行模式（"N"）：in_flight + 上次发起时刻。
    private bool _inFlight;
    private long _lastStartTicks;

    // 窗口模式（"N/M"）：窗口内各次发起的时刻（TickCount64）。
    private readonly Queue<long> _windowStarts = new();

    private SourceRateGate(bool isWindow, int limit, int windowMs)
    {
        IsWindow = isWindow;
        Limit = limit;
        WindowMs = windowMs;
    }

    /// <summary>是否窗口模式（false = 串行模式）。</summary>
    public bool IsWindow { get; }

    /// <summary>窗口模式的窗口内上限；串行模式恒为 1。</summary>
    public int Limit { get; }

    /// <summary>串行模式的间隔（毫秒）或窗口模式的窗口长度（毫秒）。</summary>
    public int WindowMs { get; }

    /// <summary>
    /// 解析限速配置：<c>"N"</c> → 串行（间隔 N ms）；<c>"N/M"</c> → 窗口（N 次 / M ms）。
    /// <para>空 / 0 / 负数 / 非数字 → null（不限速）。解析口径对齐 reader <c>parse_window_rate</c>：
    /// 用第一个 <c>/</c> 切分、两侧 trim。</para>
    /// </summary>
    public static SourceRateGate? Create(string? rate)
    {
        var text = (rate ?? string.Empty).Trim();
        if (text.Length == 0 || text == "0")
        {
            return null;
        }

        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            if (int.TryParse(text.AsSpan(0, slash).Trim(), out var limit)
                && int.TryParse(text.AsSpan(slash + 1).Trim(), out var window)
                && limit > 0
                && window > 0)
            {
                return new SourceRateGate(isWindow: true, limit, window);
            }

            return null;
        }

        return int.TryParse(text, out var interval) && interval > 0
            ? new SourceRateGate(isWindow: false, limit: 1, interval)
            : null;
    }

    /// <summary>
    /// 等闸门放行 → 执行 → （串行模式）释放。取消时**不执行** action 并抛
    /// <see cref="OperationCanceledException"/>；串行模式只在真正占到 slot 后才需要释放。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (IsWindow)
        {
            await WaitWindowAsync(cancellationToken).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }

        await AcquireSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            ReleaseSerial();
        }
    }

    /// <summary>等串行 slot：同源同时只 1 个在途，且距上次发起 ≥ 间隔。</summary>
    private async Task AcquireSerialAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long waitMs;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                var sinceLast = _lastStartTicks == 0 ? long.MaxValue : now - _lastStartTicks;
                if (!_inFlight && sinceLast >= WindowMs)
                {
                    _inFlight = true;
                    _lastStartTicks = now;
                    return;
                }

                // 在途 → 等一个间隔再试；不在途但间隔未到 → 等剩余时间
                waitMs = _inFlight ? WindowMs : Math.Max(1, WindowMs - sinceLast);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, int.MaxValue)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ReleaseSerial()
    {
        lock (_gate)
        {
            _inFlight = false;
        }
    }

    /// <summary>等窗口 slot：把本刻加入窗口，若窗口已满则等到最早一次发起滑出窗口。</summary>
    private async Task WaitWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long waitMs;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                while (_windowStarts.Count > 0 && now - _windowStarts.Peek() >= WindowMs)
                {
                    _windowStarts.Dequeue();
                }

                if (_windowStarts.Count < Limit)
                {
                    _windowStarts.Enqueue(now);
                    return;
                }

                waitMs = Math.Max(1, WindowMs - (now - _windowStarts.Peek()));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, int.MaxValue)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>当前是否被占用（诊断/测试用；串行模式看 in_flight，窗口模式看在窗口内的次数）。</summary>
    public int OccupiedSlots
    {
        get
        {
            lock (_gate)
            {
                if (!IsWindow)
                {
                    return _inFlight ? 1 : 0;
                }

                var now = Environment.TickCount64;
                var count = 0;
                foreach (var start in _windowStarts)
                {
                    if (now - start < WindowMs)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }
}

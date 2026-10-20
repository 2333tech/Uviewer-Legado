using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Models;
using BookSource.Core.Services.BookSources;
using BookSourceModel = BookSource.Core.Models.BookSource;
using CoreChapterContent = BookSource.Core.Models.ChapterContent;
using CoreTocItem = BookSource.Core.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源阅读会话：把"一本书"在阅读器里的生命周期收敛到一个对象中。
    ///
    /// <para><b>职责</b>：详情 → 目录（含分页） → 按需取章节正文 → 净化 → 转纯文本 → 交给
    /// <c>DocumentReaderController</c> 的文本管线渲染；同时维护断点续读位置。</para>
    ///
    /// <para><b>性能设计</b>（对应"执行效率 / 资源占用"要求）：
    /// <list type="number">
    /// <item>章节正文 <b>LRU 缓存</b>（默认 16 章，内存有界）：翻页来回时零网络往返；
    /// 缓存键为章节下标（同一本书的目录是稳定序列，下标即身份）。</item>
    /// <item>相邻章节 <b>预取</b>：打开第 N 章时后台拉第 N+1 章，用户翻页几乎瞬时；
    /// 预取任务可取消（快速连续翻页时旧预取立即作废），且复用同一 <see cref="BookSourceEngine"/>
    /// （该引擎的取数客户端带连接池，重建代价高）。</item>
    /// <item>CPU 密集的正文转换（HTML → 纯文本、解码）全部在 <c>Task.Run</c> 上做，
    /// 不阻塞 UI 线程（对标 reader 的 <c>spawn_blocking</c>）。</item>
    /// <item>目录分页在库侧并行抓取（阶段 2 已实现），此处只做结果合并。</item>
    /// </list></para>
    ///
    /// <para><b>线程模型</b>：本类<b>不是</b>线程安全的——约定只在 UI 线程驱动
    /// （与 <c>DocumentReaderController</c> 的既有约定一致）；仅"预取"这一条路径在后台线程写缓存，
    /// 缓存本身用锁保护。</para>
    /// </summary>
    public sealed class BookSourceReadingSession : IDisposable
    {
        /// <summary>章节正文 LRU 缓存容量（章）。16 章按平均 30KB/章约 480KB，内存有界。</summary>
        private const int ChapterCacheCapacity = 16;

        /// <summary>
        /// 预取窗口：<b>前方 2 章 + 后续 4 章</b>（用户要求"前方 1-2、后续 3-5"）。
        /// <para>为什么不是只预取下一章：连读小说时用户会连续翻 3~5 章，
        /// 只预取 1 章时每次翻页都要等一次网络往返（实测单章 100~400ms），
        /// 窗口式预取让"连续翻页"几乎零等待。</para>
        /// <para><b>窗口是有界的</b>：前后共 6 章 + 当前章 = 7 章，远小于整本
        /// （实测 1663 章的书本）。这正是"不加载全部文本"的落点。</para>
        /// </summary>
        private const int PrefetchBefore = 2;

        /// <summary>预取窗口的后向章数（后续 3~5 取 4）。</summary>
        private const int PrefetchAfter = 4;

        /// <summary>
        /// 预取窗口的并发上限。
        /// <para>刻意保守（4）：窗口是 6 章，一次并发 6 个请求对站点有压力；
        /// 4 并发在"整窗就绪"的等待时间与站点压力之间取平衡。</para>
        /// </summary>
        private const int PrefetchConcurrency = 4;

        private readonly BookSourceService _service;
        private readonly BookSourceModel _source;
        private readonly string _bookUrl;

        // LRU：Dictionary 保 O(1) 命中，LinkedList 保 O(1) 淘汰；整体一把锁
        private readonly object _cacheGate = new();
        private readonly Dictionary<int, string> _chapterCache = new();
        private readonly LinkedList<int> _chapterLru = new();

        /// <summary>
        /// <b>未净化</b>的章节 HTML 缓存（下标 → 原文）。
        /// <para>存在的意义：开关"替换净化"时，<b>就地重新净化</b>而不必重新抓取网络。
        /// 缓存原文（而非净化后的文本），才能"关掉净化 → 重新净化 → 再打开净化"反复切换
        /// 而始终零网络请求。</para>
        /// <para>容量比净化文本缓存小（4 章）：只有"当前章 + 邻近几章"会被切换后重算，
        /// 超出窗口的章节在下次读到时按需重新抓取即可。</para>
        /// </summary>
        private readonly object _rawGate = new();
        private readonly Dictionary<int, string> _rawCache = new();
        private readonly LinkedList<int> _rawLru = new();

        /// <summary>原文缓存容量（章）。</summary>
        private const int RawCacheCapacity = 8;

        private CancellationTokenSource? _prefetchCts;

        public BookSourceReadingSession(BookSourceService service, BookSourceModel source, string bookUrl)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _bookUrl = bookUrl ?? throw new ArgumentNullException(nameof(bookUrl));
        }

        /// <summary>书源（用于引擎调用与能力提示）。</summary>
        public BookSourceModel Source => _source;

        /// <summary>书籍 URL（进度主键的另一半）。</summary>
        public string BookUrl => _bookUrl;

        /// <summary>书名（详情解析后填充，供 UI 展示）。</summary>
        public string BookName { get; private set; } = string.Empty;

        /// <summary>目录（加载后填充）。</summary>
        public IReadOnlyList<CoreTocItem> Chapters { get; private set; } = Array.Empty<CoreTocItem>();

        /// <summary>
        /// 该书源的阅读链路是否依赖阶段 4 能力（java.*/WebView）。UI 应在打开前提示，
        /// 避免用户点开只得到空目录/空正文却不知道原因。
        /// </summary>
        public bool RequiresStage4 => BookSourceCapabilities.ReadingNeedsStage4(_source);

        /// <summary>打开书籍：取详情 → 拉完整目录（含分页）→ 恢复上次阅读位置。</summary>
        public async Task<OpenResult> OpenAsync(CancellationToken token = default)
        {
            // ① 详情（失败不阻断：书名可退化为书源名，目录仍可拉）
            var info = await _service.Engine
                .GetBookInfoAsync(_source, _bookUrl, null, token).ConfigureAwait(false);
            BookName = FirstNonEmpty(info?.Name, NameFromUrl(_bookUrl), _source.BookSourceName);

            // ② 目录：优先用详情页给出的 tocUrl，退化时用书籍 URL 本身
            var tocUrl = FirstNonEmpty(info?.TocUrl, _bookUrl);
            var chapters = await _service.Engine
                .GetTocAsync(_source, tocUrl, token).ConfigureAwait(false);
            Chapters = chapters;

            // ③ 断点续读：命中则回到上次章节，否则从第一章开始
            var saved = _service.GetReadingPosition(_source.BookSourceUrl, _bookUrl);
            var startIndex = 0;
            if (saved is not null && saved.ChapterIndex >= 0 && saved.ChapterIndex < chapters.Count)
            {
                startIndex = saved.ChapterIndex;
            }

            return new OpenResult(info, chapters, startIndex);
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 章正文并转成阅读器可渲染的纯文本。
        ///
        /// <para><b>按需加载（不加载全书）</b>：任何时刻只处理"当前章 + 预取窗口"内的内容。
        /// 一本 1663 章的书，内存里始终只有 7 章量级的文本，
        /// 翻到第 900 章也不会把前 899 章重新处理一遍。</para>
        ///
        /// <para>步骤：
        /// ① 净化文本缓存命中 → 直接返回（零 IO）；
        /// ② 原文缓存命中（换过净化开关时）→ <b>就地重新净化</b>（零网络）；
        /// ③ 都未命中 → 引擎抓正文（取未净化原文，入原文缓存）→ 净化 → 转纯文本；
        /// ④ 触发<b>窗口式预取</b>（前 2 + 后 4）。</para>
        /// </summary>
        public async Task<string> GetChapterTextAsync(int index, CancellationToken token = default)
        {
            if (index < 0 || index >= Chapters.Count)
            {
                return string.Empty;
            }

            if (TryGetCached(index, out var cached))
            {
                // 缓存命中：调整 LRU 顺序 + 仍要保证预取窗口就绪（可能被取消过）
                Touch(index);
                SchedulePrefetch(index, token);
                return cached;
            }

            // 原文命中：换净化开关后无需重新抓取，直接就地重新净化
            if (TryGetRaw(index, out var raw))
            {
                var reprocessed = await Task.Run(
                    () => ConvertToPlainText(raw), token).ConfigureAwait(false);
                PutCached(index, reprocessed);
                SchedulePrefetch(index, token);
                return reprocessed;
            }

            var chapter = Chapters[index];
            var content = await _service.Engine
                .GetContentAsync(_source, chapter.ChapterUrl ?? string.Empty, _bookUrl, token)
                .ConfigureAwait(false);
            if (content is null)
            {
                return string.Empty;
            }

            // 原文入缓存：之后切换净化开关可就地重算，不必再走网络
            var rawHtml = content.Content ?? string.Empty;
            PutRaw(index, rawHtml);

            // HTML → 纯文本是 CPU 密集操作（正则 + 实体解码），移到线程池避免卡 UI
            var text = await Task.Run(
                () => ConvertToPlainText(rawHtml), token).ConfigureAwait(false);

            PutCached(index, text);
            SchedulePrefetch(index, token);
            return text;
        }

        /// <summary>
        /// <b>读取 → 渲染之间</b>的净化入口：原文（未净化）→ 净化 → 纯文本。
        ///
        /// <para><b>这是全应用唯一的正文净化时机</b>（书源阅读路径）。抓取阶段一律
        /// 只拿原文（<c>GetContentAsync</c> 内部不净化），净化放在这里有三个理由：
        /// <list type="bullet">
        /// <item><b>可逆</b>：缓存里存的是原文，关掉总开关即刻还原，无需重抓网络；</item>
        /// <item><b>受总开关控制</b>：<c>Purifier.Rules</c> 在总开关关闭时被置 null，
        /// 此处即整段空转、零开销；</item>
        /// <item><b>紧贴渲染</b>：净化与"送去渲染"是同一个动作，不会出现
        /// "抓取时净化了、渲染时却用另一份文本"的错位。</item>
        /// </list></para>
        /// </summary>
        private string ConvertToPlainText(string rawHtml)
        {
            // 净化走统一入口（受一级菜单"替换净化（总开关）"控制），之后再转纯文本
            var purified = _service.PurifyForDisplay(rawHtml, BookName, _bookUrl, _source.BookSourceUrl);
            return LegadoHtmlText.ToPlainText(purified);
        }

        /// <summary>
        /// 切换净化开关后，<b>只重算窗口内</b>的章节（当前章 + 前后邻近）。
        /// <para><b>为什么只算窗口</b>：全书重算是 O(全书) 的正则替换（实测 60 章 60ms、
        /// 1663 章按比例约 1.7s），每次开关都等 1.7s 不可接受；
        /// 而窗口内的章节正是用户马上会读到的部分。窗口外的章节在读到时
        /// 按需重算（原文仍在缓存里，同样零网络）。</para>
        /// <para>返回被重算的章下标集合，供上层决定是否需要重绘。</para>
        /// </summary>
        public IReadOnlyList<int> RepurifyWindow(int centerIndex, int before, int after)
        {
            var touched = new List<int>();

            var from = Math.Max(0, centerIndex - before);
            var to = Math.Min(Chapters.Count - 1, centerIndex + after);

            for (var i = from; i <= to; i++)
            {
                if (!TryGetRaw(i, out var raw))
                {
                    // 原文不在缓存：这章下次读到时按需重新抓取 + 净化，不在这里补抓
                    continue;
                }

                var text = ConvertToPlainText(raw);
                PutCached(i, text);
                touched.Add(i);
            }

            return touched;
        }

        /// <summary>
        /// 批量抓取若干章正文，<b>与入参下标一一对应</b>（失败章为 null，不压缩数组）。
        /// <para>整本下载用：需要"第 i 章正文配第 i 章标题"，若用会过滤 null 的
        /// <c>GetContentsAsync</c>，中间一章失败就会让后面所有章名错位一格。</para>
        /// <para>并发度固定 8：整本下载是长时间后台任务，比搜索更保守，避免把站点压太狠。</para>
        /// </summary>
        public Task<BookSource.Core.Models.ChapterContent?[]> FetchContentsAsync(
            IReadOnlyList<CoreTocItem> chapters, CancellationToken token = default) =>
            _service.Engine.GetContentsIndexedAsync(
                _source, chapters, _bookUrl, BatchFetchConcurrency, null, token);

        /// <summary>整本下载时的章节抓取并发度（比搜索的 16 保守，下载是长任务）。</summary>
        private const int BatchFetchConcurrency = 8;

        /// <summary>记录"读到第几章"（仅内存；落盘由 <see cref="BookSourceService"/> 在切书时统一做）。</summary>
        public void ReportPosition(int index)        {
            if (index < 0 || index >= Chapters.Count)
            {
                return;
            }

            var chapter = Chapters[index];
            _service.SaveReadingPosition(new ReadingPosition
            {
                BookSourceUrl = _source.BookSourceUrl,
                BookUrl = _bookUrl,
                BookName = BookName,
                Author = string.Empty,
                ChapterIndex = index,
                ChapterTitle = chapter.ChapterName ?? string.Empty,
            });
        }

        /// <summary>关书/切书：取消在途预取并把进度落盘。</summary>
        public async Task CloseAsync()
        {
            CancelPrefetch();
            try
            {
                await _service.FlushReadingProgressAsync().ConfigureAwait(false);
            }
            catch
            {
                // 落盘失败不阻断关书（下次会话可能丢进度，但不能卡住 UI）
            }
        }

        // ============================================================
        // 预取（窗口式）
        // ============================================================

        /// <summary>
        /// 窗口式预取：抓取"当前章前 <see cref="PrefetchBefore"/> 章 + 后 <see cref="PrefetchAfter"/> 章"。
        ///
        /// <para><b>为什么是窗口而不是单章</b>：连读小说时用户会连续翻 3~5 章。
        /// 只预取下一章时每次翻页都要等一次网络往返（实测单章 100~400ms），
        /// 窗口式预取让"连续翻页"几乎零等待。</para>
        ///
        /// <para><b>仍然是按需，不是全书</b>：窗口最多 6 章（+ 当前章），
        /// 一本 1663 章的书在任何时刻只涉及 7 章量级的抓取与处理。</para>
        ///
        /// <para>实现：并发上限 <see cref="PrefetchConcurrency"/>；
        /// 窗口内已在缓存的章直接跳过；新任务启动前取消上一个预取
        /// （快速翻页时旧目标已无意义）。</para>
        /// </summary>
        private void SchedulePrefetch(int currentIndex, CancellationToken callerToken)
        {
            var from = Math.Max(0, currentIndex - PrefetchBefore);
            var to = Math.Min(Chapters.Count - 1, currentIndex + PrefetchAfter);

            // 收集窗口内尚未缓存的章
            var targets = new List<int>();
            lock (_cacheGate)
            {
                for (var i = from; i <= to; i++)
                {
                    if (i != currentIndex && !_chapterCache.ContainsKey(i))
                    {
                        targets.Add(i);
                    }
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            CancelPrefetch();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _prefetchCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var gate = new SemaphoreSlim(PrefetchConcurrency, PrefetchConcurrency);

                    var tasks = targets.Select(async target =>
                    {
                        await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                        try
                        {
                            await PrefetchOneAsync(target, cts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    });

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 预取被取消：正常路径，不记录
                }
                catch (Exception ex)
                {
                    // 预取失败不影响当前阅读（用户真翻到那章时会同步重试并看到错误）
                    System.Diagnostics.Debug.WriteLine($"Legado prefetch failed: {ex.Message}");
                }
            }, cts.Token);
        }

        /// <summary>
        /// 预取单章：抓<b>未净化原文</b>入原文缓存 → 就地净化 → 入文本缓存。
        /// <para>存原文而非净化后文本：这样之后切换净化开关时，这几章可以就地重算，
        /// 不必重新走网络。</para>
        /// </summary>
        private async Task PrefetchOneAsync(int index, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            lock (_cacheGate)
            {
                if (_chapterCache.ContainsKey(index))
                {
                    return;
                }
            }

            if (TryGetRaw(index, out _))
            {
                // 原文已在缓存：只需补一次净化文本
                var fromRaw = ConvertToPlainText(GetRawOrEmpty(index));
                if (!token.IsCancellationRequested)
                {
                    PutCached(index, fromRaw);
                }

                return;
            }

            var chapter = Chapters[index];
            var content = await _service.Engine
                .GetContentAsync(_source, chapter.ChapterUrl ?? string.Empty, _bookUrl, token)
                .ConfigureAwait(false);
            if (token.IsCancellationRequested || content is null)
            {
                return;
            }

            var rawHtml = content.Content ?? string.Empty;
            PutRaw(index, rawHtml);

            var text = ConvertToPlainText(rawHtml);
            if (!token.IsCancellationRequested)
            {
                PutCached(index, text);
            }
        }

        private void CancelPrefetch()
        {
            var cts = Interlocked.Exchange(ref _prefetchCts, null);
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // 已释放的 CTS：忽略
            }
        }

        // ============================================================
        // LRU 缓存
        // ============================================================

        private bool TryGetCached(int index, out string text)
        {
            lock (_cacheGate)
            {
                if (_chapterCache.TryGetValue(index, out var value))
                {
                    text = value;
                    return true;
                }
            }

            text = string.Empty;
            return false;
        }

        private void PutCached(int index, string text)
        {
            lock (_cacheGate)
            {
                if (_chapterCache.ContainsKey(index))
                {
                    return;
                }

                _chapterCache[index] = text;
                _chapterLru.AddLast(index);

                while (_chapterLru.Count > ChapterCacheCapacity)
                {
                    var oldest = _chapterLru.First!.Value;
                    _chapterLru.RemoveFirst();
                    _chapterCache.Remove(oldest);
                }
            }
        }

        /// <summary>命中后把该章移到 LRU 尾部（最近用到的不被淘汰）。</summary>
        private void Touch(int index)
        {
            lock (_cacheGate)
            {
                var node = _chapterLru.Find(index);
                if (node is null)
                {
                    return;
                }

                _chapterLru.Remove(node);
                _chapterLru.AddLast(node);
            }
        }

        // ============================================================
        // 原文缓存（未净化 HTML）—— 供"切换净化开关就地重算"用
        // ============================================================

        /// <summary>取原文缓存命中。</summary>
        private bool TryGetRaw(int index, out string raw)
        {
            lock (_rawGate)
            {
                if (_rawCache.TryGetValue(index, out var value))
                {
                    raw = value;
                    return true;
                }
            }

            raw = string.Empty;
            return false;
        }

        /// <summary>取原文（未命中返回空串）。</summary>
        private string GetRawOrEmpty(int index)
        {
            TryGetRaw(index, out var raw);
            return raw;
        }

        /// <summary>原文入缓存（LRU 淘汰）。</summary>
        private void PutRaw(int index, string raw)
        {
            lock (_rawGate)
            {
                if (_rawCache.ContainsKey(index))
                {
                    return;
                }

                _rawCache[index] = raw;
                _rawLru.AddLast(index);

                while (_rawLru.Count > RawCacheCapacity)
                {
                    var oldest = _rawLru.First!.Value;
                    _rawLru.RemoveFirst();
                    _rawCache.Remove(oldest);
                }
            }
        }

        public void Dispose() => CancelPrefetch();

        /// <summary>取第一个非空白字符串（全空则返回空串）。</summary>
        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v!;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 书名兜底：取书籍 URL 末段（去扩展名与查询串）。
        /// 与库侧 <c>BookSourceEngine.NameFromUrl</c> 同策略，独立实现避免把库内部细节
        /// 暴露到宿主（该方法是 internal）。
        /// </summary>
        private static string NameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            try
            {
                var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? uri.AbsolutePath
                    : url;

                var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                var dot = last.LastIndexOf('.');
                if (dot > 0)
                {
                    last = last[..dot];
                }

                return Uri.UnescapeDataString(last);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>打开结果。</summary>
        public sealed record OpenResult(
            BookSource.Core.Models.BookInfo? Info,
            IReadOnlyList<CoreTocItem> Chapters,
            int StartIndex);
    }
}
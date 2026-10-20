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

        /// <summary>预取窗口：只预取紧邻的下一章（更远的前瞻命中率低、白费带宽）。</summary>
        private const int PrefetchDistance = 1;

        private readonly BookSourceService _service;
        private readonly BookSourceModel _source;
        private readonly string _bookUrl;

        // LRU：Dictionary 保 O(1) 命中，LinkedList 保 O(1) 淘汰；整体一把锁
        private readonly object _cacheGate = new();
        private readonly Dictionary<int, string> _chapterCache = new();
        private readonly LinkedList<int> _chapterLru = new();

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
        /// <para>步骤：① 缓存命中直接返回（零 IO）；② 未命中 → 引擎取正文（阶段 2 已净化）；
        /// ③ CPU 密集的 HTML → 文本转换放后台线程；④ 入缓存；⑤ 触发下一章预取。</para>
        /// </summary>
        public async Task<string> GetChapterTextAsync(int index, CancellationToken token = default)
        {
            if (index < 0 || index >= Chapters.Count)
            {
                return string.Empty;
            }

            if (TryGetCached(index, out var cached))
            {
                // 缓存命中：调整 LRU 顺序 + 仍要保���下一章预取（可能被取消过）
                Touch(index);
                SchedulePrefetch(index, token);
                return cached;
            }

            var chapter = Chapters[index];
            var content = await _service.Engine
                .GetContentAsync(_source, chapter.ChapterUrl ?? string.Empty, _bookUrl, token)
                .ConfigureAwait(false);
            if (content is null)
            {
                return string.Empty;
            }

            // HTML → 纯文本是 CPU 密集操作（正则 + 实体解码），移到线程池避免卡 UI
            var text = await Task.Run(
                () => LegadoHtmlText.ToPlainText(content.Content),
                token).ConfigureAwait(false);

            PutCached(index, text);
            SchedulePrefetch(index, token);
            return text;
        }

        /// <summary>记录"读到第几章"（仅内存；落盘由 <see cref="BookSourceService"/> 在切书时统一做）。</summary>
        public void ReportPosition(int index)
        {
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
        // 预取
        // ============================================================

        /// <summary>
        /// 预取下一章：立即取消上一个预取任务（快速翻页时旧目标已无意义），
        /// 新任务在后台跑；命中缓存时直接返回不启任务。
        /// <para>刻意"每次都新建 CTS"：预取结果只写缓存，语义上幂等，
        /// 取消-重启的代价远小于维护一条复杂的可复用队列。</para>
        /// </summary>
        private void SchedulePrefetch(int currentIndex, CancellationToken callerToken)
        {
            var next = currentIndex + PrefetchDistance;
            if (next < 0 || next >= Chapters.Count)
            {
                CancelPrefetch();
                return;
            }

            lock (_cacheGate)
            {
                if (_chapterCache.ContainsKey(next))
                {
                    return;
                }
            }

            CancelPrefetch();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _prefetchCts = cts;

            var chapter = Chapters[next];
            _ = Task.Run(async () =>
            {
                try
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

var content = await _service.Engine
                    .GetContentAsync(_source, chapter.ChapterUrl ?? string.Empty, _bookUrl, cts.Token)
                    .ConfigureAwait(false);
                if (cts.IsCancellationRequested || content is null)
                {
                    return;
                }

                    var text = LegadoHtmlText.ToPlainText(content.Content);
                    if (!cts.IsCancellationRequested)
                    {
                        PutCached(next, text);
                    }
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Models;
using BookSource.Core.Services;
using BookSource.Core.Services.BookSources;
using BookSourceModel = BookSource.Core.Models.BookSource;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源功能门面（阶段 5 集成入口）：把 <c>BookSource.Core</c> 的四个独立存储/引擎
    /// 聚合成阅读器可直接使用的单一服务，并负责它们的生命周期与线程安全。
    ///
    /// <para><b>组成</b>：
    /// <list type="bullet">
    /// <item><see cref="BookSourceManager"/> —— 书源导入/查询（阶段 0）；</item>
    /// <item><see cref="ReplaceRuleManager"/> + <see cref="ContentPurifier"/> —— 正文净化（阶段 3）；</item>
    /// <item><see cref="BookSourceEngine"/> —— 详情/目录/正文/图片编排（阶段 2，持有净化管线）；</item>
    /// <item><see cref="BookSourceReadingStore"/> —— 断点续读（阶段 5 新增）。</item>
    /// </list></para>
    ///
    /// <para><b>线程模型</b>：本类不持锁。四个被持有的组件各自线程安全（库侧已保证），
    /// 因此 UI 线程读、后台线程搜索/抓取都直接调用，无需外部同步。</para>
    ///
    /// <para><b>初始化</b>：<see cref="EnsureInitializedAsync"/> 幂等且并发安全
    /// （同一批初始化只跑一次，N 个并发调用者共享同一个任务），避免启动时多窗口/多入口
    /// 重复读三个 JSON 文件。</para>
    /// </summary>
    public sealed class BookSourceService : IAsyncDisposable
    {
        private readonly BookSourceManager _sources;
        private readonly ReplaceRuleManager _replaceRules;
        private readonly ContentPurifier _purifier = new();
        private readonly BookSourceReadingStore _readingStore;
        private readonly BookSourceEngine _engine;
        private readonly SearchService _search;

        // 初始化任务缓存：幂等 + 并发去重（多个调用者 await 同一个 Task）
        private readonly object _initGate = new();
        private Task? _initTask;

        private int _disposed;

        public BookSourceService(string? dataDirectory = null)
        {
            var dir = string.IsNullOrWhiteSpace(dataDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Uviewer"
                : dataDirectory!;

            _sources = new BookSourceManager(Path.Combine(dir, "booksources.json"));
            _replaceRules = new ReplaceRuleManager(Path.Combine(dir, "replaceRules.json"));
            _readingStore = new BookSourceReadingStore(Path.Combine(dir, "readingProgress.json"));

            // 引擎在构造时就绑定净化器实例：净化器的规则列表可在加载后整体替换
            //（ContentPurifier 内部按引用比较失效编译快照），无需重建引擎。
            _engine = new BookSourceEngine(purifier: _purifier);

            // 搜索服务自建引擎池：每次搜索新建 RuleEngine（Jint 非线程安全），
            // 并发度由 BookSourceLimits 控制。
            _search = new SearchService();
        }

        /// <summary>书源管理（导入/查询/启停）。</summary>
        public BookSourceManager Sources => _sources;

        /// <summary>替换规则存储（阶段 3）。</summary>
        public ReplaceRuleManager ReplaceRules => _replaceRules;

        /// <summary>净化管线实例（已注入引擎）。</summary>
        public ContentPurifier Purifier => _purifier;

        /// <summary>阅读进度存储（断点续读）。</summary>
        public BookSourceReadingStore ReadingStore => _readingStore;

        /// <summary>取数/解析编排引擎（详情、目录、正文、图片）。</summary>
        public BookSourceEngine Engine => _engine;

        /// <summary>是否已初始化（UI 据此决定是否显示"加载中"）。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// 幂等初始化：加载书源 / 替换规则 / 阅读进度三个文件，并把替换规则灌入净化器。
        /// <para>并发去重：首次调用创建任务并缓存；后续调用（含并发）直接 await 同一任务，
        /// 避免重复 IO 与重复解析。</para>
        /// </summary>
        public Task EnsureInitializedAsync(CancellationToken token = default)
        {
            lock (_initGate)
            {
                if (_initTask is null)
                {
                    _initTask = InitializeCoreAsync(token);
                }

                return _initTask;
            }
        }

        private async Task InitializeCoreAsync(CancellationToken token)
        {
            // 三个存储相互独立：并发加载（各读各的文件，无共享写），总耗时取最慢那个
            await Task.WhenAll(
                _sources.LoadAsync(token),
                _replaceRules.LoadAsync(token),
                _readingStore.LoadAsync(token)).ConfigureAwait(false);

            // 净化器接管规则快照：此后 ReplaceRules 的变更由调用方显式 ApplyReplaceRules 同步
            ApplyReplaceRules();
            IsInitialized = true;
        }

        /// <summary>
        /// 把替换规则存储的当前内容灌入净化器（整体替换 → 净化器按引用比较重建编译快照）。
        /// 在导入/删除替换规则后调用；重复调用是幂等的（内容相同则引用不同也会重建一次，
        /// 代价仅一次 O(规则数) 编译）。
        /// </summary>
        public void ApplyReplaceRules() => _purifier.Rules = _replaceRules.GetAll();

        /// <summary>
        /// 并发搜索全部启用书源（阶段 1）。<paramref name="maxConcurrency"/> 为 null 时用
        /// <see cref="BookSourceLimits.DefaultConcurrency"/>（16）。
        /// <para>前置过滤：跳过需要阶段 4 能力（java.*/WebView）的书源——它们搜索必然拿到
        /// 空结果或错结果，混进来只会污染结果列表（能力评估见 <see cref="BookSourceCapabilities"/>）。</para>
        /// </summary>
        public Task<List<SearchResult>> SearchAllAsync(
            string keyword, int? maxConcurrency = null, CancellationToken token = default)
        {
            var candidates = _sources.GetEnabled()
                .Where(s => !string.IsNullOrWhiteSpace(s.SearchUrl))
                .Where(s => !BookSourceCapabilities.ReadingNeedsStage4(s))
                .ToList();

            return _search.SearchAllAsync(candidates, keyword, maxConcurrency, token);
        }

        /// <summary>
        /// 记录阅读进度（仅内存）。批量翻章时逐章写内存是廉价的 O(1) 操作；
        /// 落盘由 <see cref="FlushReadingProgressAsync"/> 在关书/切书时统一做。
        /// </summary>
        public void SaveReadingPosition(ReadingPosition position)
        {
            if (position is null || string.IsNullOrEmpty(position.BookSourceUrl))
            {
                return;
            }

            _readingStore.Upsert(position.Touch());
        }

        /// <summary>取已保存的阅读进度（无则 null）。</summary>
        public ReadingPosition? GetReadingPosition(string bookSourceUrl, string bookUrl) =>
            _readingStore.Get(bookSourceUrl, bookUrl);

        /// <summary>落盘阅读进度（关书/切书/退出阅读器时调用）。</summary>
        public Task FlushReadingProgressAsync(CancellationToken token = default) =>
            _readingStore.SaveAsync(token);

        /// <summary>
        /// 落盘替换规则（导入/编辑后调用）。先同步净化器再写盘，
        /// 保证"内存已生效但落盘失败"时用户看到的行为与界面一致。
        /// </summary>
        public async Task SaveReplaceRulesAsync(CancellationToken token = default)
        {
            ApplyReplaceRules();
            await _replaceRules.SaveAsync(token).ConfigureAwait(false);
        }

        /// <summary>供 UI 使用的书源清单（含能力标记，用于置灰"需完整 JS 支持"的源）。</summary>
        public IReadOnlyList<BookSourceModel> GetAllSources() => _sources.GetAll();

        /// <summary>已启用且当前引擎可完整跑通的书源（UI 默认只列这些，避免"点了没反应"）。</summary>
        public IReadOnlyList<BookSourceModel> GetUsableSources() =>
            _sources.GetEnabled().Where(s => BookSourceCapabilities.IsFullySupported(s)).ToList();

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            // 落盘进度：进程退出/对象释放时的最后保险（正常路径已在切书时 flush）
            try
            {
                _readingStore.SaveAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // 释放期不抛：读进度丢失不影响本次会话的阅读
            }

            return ValueTask.CompletedTask;
        }
    }
}
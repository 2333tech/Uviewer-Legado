using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源功能门面（阶段 5 集成入口）：把 <c>Uviewer.Services.Legado</c> 的四个独立存储/引擎
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
        private readonly TitleRuleManager _titleRules;
        private readonly ContentPurifier _purifier = new();
        private readonly BookSourceReadingStore _readingStore;
        private readonly BookSourceEngine _engine;

        /// <summary>
        /// 书源 JS 状态仓库（阶段 4-2）：<c>java.put/get</c>、<c>source/book/cache/cookie</c> 的落点。
        /// <para>目录 = <c>&lt;数据目录&gt;\legado\sources\&lt;sha1(书源URL)&gt;.json</c>。
        /// 引擎与搜索服务共用同一实例，故"搜索/目录链写的变量，阅读链读得到"。</para>
        /// </summary>
        private readonly SourceStateStore _jsStateStore;

        /// <summary>
        /// 替换净化总开关（用户级，持久化到 <c>legadoSettings.json</c>）。
        /// <para><b>与逐条规则开关的关系</b>：总开关关掉 = 整条净化管线空转
        /// （等价于"没有任何规则生效"），但<b>不改动</b>各条规则的 <c>IsEnabled</c>。
        /// 这样"临时全局暂停"不会破坏用户逐条的配置，重新打开总开关即刻恢复。</para>
        /// </summary>
        private volatile bool _purifyEnabled = true;

        /// <summary>
        /// 整本下载的落盘目录（用户级，持久化到 <c>legadoSettings.json</c> 的
        /// <c>downloadDirectory</c>）。默认 = Windows 的"下载"文件夹
        /// （<c>%USERPROFILE%\Downloads</c>）——这是用户对"下载"的最自然预期，
        /// 也免去第一次用书源下载时去翻 <c>%LocalAppData%</c> 这种隐藏目录。
        /// </summary>
        private volatile string _downloadDirectory = DefaultDownloadDirectory;

        /// <summary>总开关等 Legado 侧设置的落盘路径。</summary>
        private readonly string _settingsPath;
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
            _titleRules = new TitleRuleManager(Path.Combine(dir, "titleRules.json"));
            _readingStore = new BookSourceReadingStore(Path.Combine(dir, "readingProgress.json"));
            _settingsPath = Path.Combine(dir, "legadoSettings.json");

            // 引擎在构造时就绑定净化器实例：净化器的规则列表可在加载后整体替换
            //（ContentPurifier 内部按引用比较失效编译快照），无需重建引擎。
            // 阶段 4-2：JS 状态仓库随数据目录走（<数据目录>\legado\sources），引擎与搜索共用同一实例，
            // 这样"搜索链写下的变量 → 阅读链读得到"，且重启后仍在。
            _jsStateStore = new SourceStateStore(Path.Combine(dir, "legado", "sources"));
            _engine = new BookSourceEngine(purifier: _purifier, store: _jsStateStore);

            // 搜索服务自建引擎池：每次搜索新建 RuleEngine（Jint 非线程安全），
            // 并发度由 BookSourceLimits 控制。
            _search = new SearchService(store: _jsStateStore);
        }

        /// <summary>书源管理（导入/查询/启停）。</summary>
        public BookSourceManager Sources => _sources;

        /// <summary>替换规则存储（阶段 3）。</summary>
        public ReplaceRuleManager ReplaceRules => _replaceRules;

        /// <summary>标题 / 章节划分规则存储（TXT 分章与目录）。</summary>
        public TitleRuleManager TitleRules => _titleRules;

        /// <summary>净化管线实例（已注入引擎）。</summary>
        public ContentPurifier Purifier => _purifier;

        /// <summary>阅读进度存储（断点续读）。</summary>
        public BookSourceReadingStore ReadingStore => _readingStore;

        /// <summary>取数/解析编排引擎（详情、目录、正文、图片）。</summary>
        public BookSourceEngine Engine => _engine;

        /// <summary>搜索服务（与引擎共用 JS 状态仓库，供搜索对话框复用同一实例）。</summary>
        // EN: Shared search service so the search chain writes variables to the same JS state store.
        public SearchService Search => _search;

        /// <summary>书源 JS 状态仓库（阶段 4-2；退出登录/清理书源状态时使用）。</summary>
        // EN: Per-source JS state store (variables, cookies, cache) backed by files on disk.
        public SourceStateStore JsStateStore => _jsStateStore;

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
            // 四个存储相互独立：并发加载（各读各的文件，无共享写），总耗时取最慢那个
            await Task.WhenAll(
                _sources.LoadAsync(token),
                _replaceRules.LoadAsync(token),
                _titleRules.LoadAsync(token),
                _readingStore.LoadAsync(token)).ConfigureAwait(false);

            // 首次启动且用户从未导入过分章规则时写入内置默认规则。
            // 否则 TXT 目录功能对"什么都没配过的用户"完全不可用——而这恰恰是最常见状态。
            await EnsureDefaultTitleRulesAsync().ConfigureAwait(false);

            // 读回总开关等 Legado 侧设置（在 ApplyReplaceRules 之前，保证首次 Apply 就带上正确状态）
            LoadSettings();

            // 净化器接管规则快照：此后 ReplaceRules 的变更由调用方显式 ApplyReplaceRules 同步
            ApplyReplaceRules();
            IsInitialized = true;
        }

        // ============================================================
        // 替换净化总开关
        // ============================================================

        /// <summary>
        /// 替换净化总开关是否开启（默认开）。
        /// <para>关掉后整条净化管线空转（等价于"没有任何规则生效"），
        /// 但各条规则自身的 <c>IsEnabled</c> 不受影响——随时可以恢复。</para>
        /// </summary>
        public bool PurifyEnabled => _purifyEnabled;

        /// <summary>切换替换净化总开关并落盘。返回切换后的新状态。</summary>
        public async Task<bool> SetPurifyEnabledAsync(bool enabled, CancellationToken token = default)
        {
            if (_purifyEnabled == enabled)
            {
                return _purifyEnabled;
            }

            _purifyEnabled = enabled;

            // 立即让净化器生效：开 → 灌入规则快照；关 → 置空（管线空转）
            ApplyReplaceRules();

            await SaveSettingsAsync(token).ConfigureAwait(false);
            return _purifyEnabled;
        }

/// <summary>
        /// 把规则快照灌进净化器。
        /// <para>总开关关闭时<b>置空</b>（而不是仍然灌入）——净化器据此整段跳过，
        /// 这是"总开关"能做到"零开销"的关键。</para>
        /// </summary>
        public void ApplyReplaceRules() =>
            _purifier.Rules = _purifyEnabled ? _replaceRules.GetAll() : null;

        /// <summary>
        /// <b>读取 → 渲染之间</b>的统一净化入口（受一级菜单"替换净化（总开关）"控制）。
        ///
        /// <para><b>这是全应用唯一的正文净化时机</b>：
        /// 抓取阶段（<c>BookSourceEngine.GetContentAsync</c> /
        /// <c>GetContentsIndexedAsync</c>）一律<b>只返回原文</b>，不做任何净化；
        /// 无论文本来自书源网络还是本地文件，都由本方法在送去渲染之前净化一次。</para>
        ///
        /// <para>这样设计的三个直接后果：
        /// <list type="bullet">
        /// <item><b>总开关真正可逆</b>：源头永远是原文，开关切换只是"净化 or 不净化"，
        /// 不需要重新抓取网络；</item>
        /// <item><b>零开销</b>：总开关关闭时 <c>Purifier.Rules</c> 为 null，
        /// <c>Apply</c> 走快路径原样返回；</item>
        /// <item><b>时机一致</b>：不会出现"抓取时净化了一份、渲染时又用另一份"的错位。</item>
        /// </list></para>
        /// </summary>
        /// <param name="text">待净化的正文（可以是 HTML 或纯文本）。</param>
        /// <param name="bookName">书名（<c>name;</c> 作用域用）。</param>
        /// <param name="bookUrl">书籍 URL（<c>book;</c> 作用域用）。</param>
        /// <param name="sourceUrl">书源 URL（<c>source;</c> 作用域用）。</param>
  public string PurifyForDisplay(string text, string? bookName = null, string? bookUrl = null, string? sourceUrl = null)
        {
            if (string.IsNullOrEmpty(text))
    {
      return text;
            }

            var ctx = new PurifyContext(bookName, bookUrl, sourceUrl, null);
  return _purifier.Apply(text, in ctx);
        }

        // EN: Title channel purification entry (scopeTitle rules only).
        /// <summary>
        /// <b>标题通道</b>净化：只应用"作用于标题"的替换规则（legado <c>scopeTitle</c>）。
        /// <para>在标题 / 分章规则识别章节行<b>之前</b>逐行调用，让"先去前缀再识别"这类标题能命中规则。</para>
        /// </summary>
        public string PurifyTitleLine(string line, string? bookName = null, string? bookUrl = null, string? sourceUrl = null)
        {
            if (string.IsNullOrEmpty(line))
            {
                return line;
            }

            var ctx = new PurifyContext(bookName, bookUrl, sourceUrl, null);
            return _purifier.ApplyToTitle(line, in ctx);
        }

        // EN: Probes which replace rules actually apply to the given chapter text (empty when purification is off).
        /// <summary>
        /// 探测当前章节上<b>真正生效</b>的替换规则（给"替换净化管理"界面打「本章生效」标记用）。
        /// <para>判定口径与 <see cref="PurifyForDisplay"/> 完全一致：按管线顺序逐条应用、比较前后文本，
        /// 只有真的改写了文本的规则算生效。</para>
        /// <para>总开关关闭时返回空集合——此时净化整段不生效，界面不该标"生效"。</para>
        /// </summary>
        /// <param name="text">探测文本（当前章节的<b>原文</b>）。</param>
        public IReadOnlyList<long> FindEffectiveReplaceRuleIds(
            string text, string? bookName = null, string? bookUrl = null, string? sourceUrl = null)
        {
            if (string.IsNullOrEmpty(text) || !_purifyEnabled)
            {
                return Array.Empty<long>();
            }

            var ctx = new PurifyContext(bookName, bookUrl, sourceUrl, null);
            return _purifier.FindEffectiveRuleIds(text, in ctx);
        }

        // ============================================================
        // Legado 侧设置持久化（替换净化总开关 + 整本下载目录）
        // ============================================================

        /// <summary>
        /// Windows 默认的"下载"文件夹（<c>%USERPROFILE%\Downloads</c>）。
        /// <para>实现在 <see cref="DownloadPathRules"/>（纯逻辑、可被集成测试链接），
        /// 本属性只是给服务层与 UI 一个方便入口。</para>
        /// </summary>
        public static string DefaultDownloadDirectory => DownloadPathRules.DefaultDirectory;

        /// <summary>
        /// 校验并规范化用户输入的下载目录。详见 <see cref="DownloadPathRules.Normalize"/>。
        /// </summary>
        /// <exception cref="ArgumentException">路径为空、非法或指向一个文件。</exception>
        public static string NormalizeDownloadDirectory(string? path) => DownloadPathRules.Normalize(path);

        /// <summary>整本下载的落盘目录（默认 <see cref="DefaultDownloadDirectory"/>）。</summary>
        public string DownloadDirectory => _downloadDirectory;

        /// <summary>
        /// 设置下载目录并落盘。返回规范化后的路径（UI 可直接回填文本框）。
        /// <para>只做配置，不创建目录：路径可能指向尚未存在的文件夹，
        /// 等到真正下载时（<c>BookSourceDownloadService</c>）才 <c>CreateDirectory</c>。</para>
        /// </summary>
        public async Task<string> SetDownloadDirectoryAsync(string path, CancellationToken token = default)
        {
            var normalized = NormalizeDownloadDirectory(path);
            if (string.Equals(normalized, _downloadDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return _downloadDirectory;
            }

            _downloadDirectory = normalized;
            await SaveSettingsAsync(token).ConfigureAwait(false);
            return _downloadDirectory;
        }

        /// <summary>读回 Legado 侧设置。文件不存在 = 用默认值（总开关开 + 默认下载目录）。</summary>
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    _purifyEnabled = true;
                    _downloadDirectory = DefaultDownloadDirectory;
                    return;
                }

                var json = File.ReadAllText(_settingsPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("purifyEnabled", out var v) &&
                    v.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    _purifyEnabled = true;
                }
                else if (doc.RootElement.TryGetProperty("purifyEnabled", out var v2) &&
                         v2.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    _purifyEnabled = false;
                }

                // 下载目录：缺失/空/非法 → 保持默认值（配置被手改坏不该让下载不可用）
                if (doc.RootElement.TryGetProperty("downloadDirectory", out var dir) &&
                    dir.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    _downloadDirectory = DownloadPathRules.Resolve(dir.GetString());
                }
            }
            catch (Exception ex)
            {
                // 设置文件损坏：回落到默认值，不阻断启动
                System.Diagnostics.Debug.WriteLine($"Load legado settings failed: {ex.Message}");
                _purifyEnabled = true;
                _downloadDirectory = DefaultDownloadDirectory;
            }
        }

        /// <summary>落盘 Legado 侧设置（原子替换：tmp + File.Move）。</summary>
        private async Task SaveSettingsAsync(CancellationToken token = default)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var temp = _settingsPath + ".tmp";
                // 配置文件也统一 LF：Windows 上 Path/JSON 本身不敏感，但统一换行能避免
                // "同一份配置在不同来源（Android 导出 / 本机写入）之间逐字节 diff"的噪音。
                // 路径用 JsonSerializer.Serialize 转义（反斜杠必须成对），不手拼字符串。
                var dirJson = System.Text.Json.JsonSerializer.Serialize(_downloadDirectory ?? string.Empty);
                var json = $"{{\n  \"purifyEnabled\": {(_purifyEnabled ? "true" : "false")},\n  \"downloadDirectory\": {dirJson}\n}}\n";
                await File.WriteAllTextAsync(temp, json, token).ConfigureAwait(false);
                File.Move(temp, _settingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save legado settings failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 首次运行时灌入内置分章规则（仅当用户规则为空）。
        /// <para><b>不覆盖用户数据</b>：只要用户导入过自己的规则（含全部停用），
        /// 一律不再写入——否则用户删掉的默认规则会在下次启动时复活。</para>
        /// </summary>
        private async Task EnsureDefaultTitleRulesAsync()
        {
            if (_titleRules.Count > 0)
            {
                return;
            }

            foreach (var rule in TxtChapterSplitter.CreateDefaultRules())
            {
                _titleRules.Upsert(rule);
            }

            await _titleRules.SaveAsync().ConfigureAwait(false);
        }

        /// <summary>
    /// 并发搜索全部启用书源（阶段 1）。<paramref name="maxConcurrency"/> 为 null 时用
    /// <see cref="BookSourceLimits.DefaultConcurrency"/>（16）。
    /// <para>前置过滤：跳过需要阶段 4 能力（java.*/WebView）的书源——它们搜索必然拿到
    /// 空结果或错结果，混进来只会污染结果列表（能力评估见 <see cref="BookSourceCapabilities"/>）。</para>
    /// <para><paramref name="progress"/> 以「已完成/总数」回报，UI 需显示为 "X/X"。
    /// 多源搜索常在数十秒量级，没有进度用户无法区分"正在搜"与"卡死"。</para>
    /// </summary>
    public Task<List<SearchResult>> SearchAllAsync(
        string keyword,
        int? maxConcurrency = null,
        IProgress<SearchProgress>? progress = null,
        CancellationToken token = default)
    {
        var candidates = GetSearchableSources();
        return _search.SearchAllAsync(candidates, keyword, maxConcurrency, progress, token);
    }

    /// <summary>
    /// 可参与搜索的书源：已启用 + 有 searchUrl/ruleSearch + 阅读链路不需阶段 4。
    /// <para>这里只按**阅读链路**（ruleToc/ruleContent）过滤，而不是要求整源完整——
    /// 一个源可能搜索规则用了 java.*（阶段 4 未做）但目录/正文是干净的，
    /// 这种源仍应让用户能搜到并深链阅读，只是结果可能不准。全源严格过滤会砍掉过多可用源。</para>
    /// </summary>
    public List<BookSourceModel> GetSearchableSources() => _sources.GetEnabled()
        .Where(s => !string.IsNullOrWhiteSpace(s.SearchUrl) && s.RuleSearch is not null)
        .Where(s => !BookSourceCapabilities.ReadingNeedsStage4(s))
        .ToList();

    // ============================================================
    // 书源管理（查看 / 编辑 / 导入 / 启停）
    // ============================================================

    /// <summary>
    /// 从 JSON 文件导入书源（支持多书源数组，如 Sample/*.json）。
    /// <para>走库侧流式导入（UTF-8 结构扫描 + 256 条/批合入，峰值内存有界），
    /// 因此 5MB/400+ 源的大文件也不会把内存顶爆。返回新增/更新/跳过数量。</para>
    /// <para>导入后<b>无需重启</b>：管理器是内存权威 + 惰性快照，下次读即见新数据。</para>
    /// </summary>
    public async Task<ImportResult> ImportFromFileAsync(string path, CancellationToken token = default)
    {
        var result = await _sources.ImportFromFileAsync(path, token).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 保存单个书源（同 URL 覆盖）。用于管理器里的"编辑后保存"。
    /// <para>BookSource 的按主键 Upsert 语义天然是覆盖：<see cref="BookSourceManager.Upsert"/>
    /// 对已存在的 URL 保留原插入序号，因此落盘顺序仍等于导入顺序（确定性）。</para>
    /// </summary>
    public void SaveSource(BookSourceModel source)
    {
        if (source is null || string.IsNullOrEmpty(source.BookSourceUrl))
        {
            throw new ArgumentException("书源缺少 bookSourceUrl，无法保存", nameof(source));
        }

        _sources.Upsert(source);
    }

    /// <summary>删除书源（按 URL）。</summary>
    public bool RemoveSource(string bookSourceUrl) => _sources.Remove(bookSourceUrl);

    /// <summary>启用/禁用书源（改 Enabled 后落盘）。</summary>
    public async Task SetSourceEnabledAsync(BookSourceModel source, bool enabled, CancellationToken token = default)
    {
        if (source.Enabled == enabled)
        {
            return;
        }

        source.Enabled = enabled;
        _sources.Upsert(source);
        await _sources.SaveAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除书源并落盘（管理器里的"删除"按钮）。
    /// <para>同时清掉该源相关的阅读进度——书源没了，进度键（源 URL + 书籍 URL）永远不会再被读到，
    /// 留着只是垃圾数据。</para>
    /// </summary>
    public async Task RemoveSourceAsync(string bookSourceUrl, CancellationToken token = default)
    {
        if (_sources.Remove(bookSourceUrl))
        {
            foreach (var p in _readingStore.Positions.Where(p => p.BookSourceUrl == bookSourceUrl).ToList())
            {
                _readingStore.Remove(p.BookSourceUrl, p.BookUrl);
            }

            await _sources.SaveAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>编辑后保存单个书源并落盘（管理器"保存"按钮）。</summary>
    public Task SaveSourceAsync(BookSourceModel source, CancellationToken token = default)
    {
        SaveSource(source);
        return _sources.SaveAsync(token);
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
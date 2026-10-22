using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uviewer.Models;
using Uviewer.Services;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.WebView;
using Windows.Storage;

namespace Uviewer
{
    /// <summary>
    /// 书源阅读（阶段 5 集成）控制器：把 <see cref="BookSourceReadingSession"/> 接到既有的
    /// 文本渲染管线上。
    ///
    /// <para><b>复用策略（零侵入既有渲染代码）</b>：书源章节正文被转成纯文本后，
    /// 直接走 <c>DisplayLoadedText</c> 这条与本地 txt 完全相同的路径——
    /// 于是横排/纵排/青空（aozora）三种排版、字号/字体/主题、进度条、搜索高亮
    /// 全部自动生效，不需要为书源另写一套渲染。</para>
    ///
    /// <para><b>生命周期</b>：一次只保持一本会话；打开新书前先 <see cref="CloseLegadoAsync"/>
    /// 关掉旧书（取消预取 + 落盘进度），避免两个会话的预取任务互相抢网络。</para>
    ///
    /// <para><b>并发/取消</b>：每次打开/跳章都递增 <c>_legadoGeneration</c>；
    /// 网络返回后先比对代数，代数已变（用户又点了别的章）就丢弃结果，
    /// 防止"慢响应覆盖快响应"的经典竞态。</para>
    /// </summary>
    internal sealed partial class DocumentReaderController
    {
        private BookSourceService? _bookSourceService;
        private BookSourceReadingSession? _legadoSession;

        /// <summary>WebView2 宿主（阶段 4-7，懒建；图形验证码窗口 + 离屏 <c>java.webView</c> 取数）。</summary>
        // EN: Lazy WebView2 host wired into the Legado runtime for captcha/Cloudflare flows.
        private WebView2Host? _webViewHost;

        /// <summary>适配后的目录项（供点击分发与高亮定位复用，避免重复转换）。</summary>
        private List<Uviewer.Models.TocItem>? _legadoTocItems;

        /// <summary>打开代次：只有最新一次打开/跳章的结果才允许渲染。</summary>
        private int _legadoGeneration;

        /// <summary>当前章节下标（-1 = 未打开书源）。</summary>
        private int _legadoChapterIndex = -1;

        /// <summary>当前书源书名（显示在标题栏）。</summary>
        internal string LegadoBookName { get; private set; } = string.Empty;

        /// <summary>
        /// 书源服务（首次访问时惰性构造；同时把 WebView2 宿主注入运行时）。
        /// <para>WebView2 运行时缺失时 <see cref="WebView2Host.IsAvailable"/>=false，
        /// 引擎自动回退静态取数 + 诊断（<see cref="WebViewFetchDecorator"/>），不影响无 WebView 源。</para>
        /// </summary>
        // EN: Lazily created source service; wires the WebView2 host into the shared runtime.
        internal BookSourceService BookSources
        {
            get
            {
                if (_bookSourceService is null)
                {
                    _bookSourceService = new BookSourceService();
                    _webViewHost = new WebView2Host(DispatcherQueue, () => RootGrid);
                    _bookSourceService.Runtime.WebViewHost = _webViewHost;
                }

                return _bookSourceService;
            }
        }

        /// <summary>书源会话（未打开书时 null）。</summary>
        internal BookSourceReadingSession? LegadoSession => _legadoSession;

        /// <summary>
        /// 当前启用的标题/章节划分规则（供 TXT 目录生成）。
        /// <para>存储可能尚未加载（BookSources 首次访问才构造），故这里容忍未初始化状态：
        /// 返回空列表时 <c>TextTocProvider</c> 会退回到内置标记识别，功能不受影响。</para>
        /// </summary>
        internal List<Uviewer.Services.Legado.Models.TitleRule> GetEnabledTitleRules()
        {
            var service = _bookSourceService;
            return service is null || !service.IsInitialized
                ? new List<Uviewer.Services.Legado.Models.TitleRule>()
                : service.TitleRules.GetEnabledOrdered();
        }

        // EN: Single place that builds the TXT TOC provider, always wired with title-line purification.
        /// <summary>
        /// 构造 TXT 目录提供器：统一挂上「标题行预净化」。
        /// <para>作用于标题的替换规则（legado <c>scopeTitle</c>）必须在标题/分章规则<b>之前</b>生效，
        /// 否则"【玄幻】第12章 风起"这类带前缀的标题根本匹配不到标题规则。</para>
        /// </summary>
        internal TextTocProvider CreateTextTocProvider(string content, List<AozoraBindingModel>? blocks = null) =>
            new(content, blocks, GetEnabledTitleRules(), PurifyTitleLineForToc);

        // EN: Applies title-scope replace rules to a heading line before title-rule matching.
        /// <summary>
        /// 标题行预净化委托。
        /// <para>刻意<b>不</b>主动构造 <see cref="BookSourceService"/>：书源数据尚未加载时
        /// 等价于"没有标题规则"，直接原样返回，避免渲染路径上意外触发一次 IO。</para>
        /// </summary>
        private string PurifyTitleLineForToc(string line)
        {
            var service = _bookSourceService;
            return service is null || !service.IsInitialized
                ? line
                : service.PurifyTitleLine(line, ReplaceScopeBookName, ReplaceScopeBookUrl, ReplaceScopeSourceUrl);
        }

        // EN: Rebuilds the TOC after title-rule edits; rebuilds file cache only when the chapter list really changed.
        /// <summary>
        /// 用最新的标题规则重建当前 TXT 的目录（导入/开关规则后调用）。
        /// <para>必须重建 provider 而不是只 LoadToc —— provider 在构造时就固定了规则快照，
        /// 规则变了不重建就还是旧结果。</para>
        /// </summary>
        internal async Task RefreshTextTocFromTitleRulesAsync()
        {
            if (string.IsNullOrEmpty(_currentTextContent))
            {
                return;
            }

            // 书源阅读模式下目录来自书源会话，不受标题规则影响
            if (_legadoSession is not null)
            {
                return;
            }

            // 标题规则优先级最高：先记下"改之前的目录"，用来判断目录是否真的变了
            var before = _tocService.CurrentToc.Select(t => t.HeadingText).ToArray();

            if (TextCacheActive)
            {
                // 内存虚拟窗口（无磁盘缓存）：不能落盘——只丢掉旧章节表，用新规则重新装配窗口
                if (IsVirtualTextCache)
                {
                    InvalidateVirtualPurifiedChapters();
                    // 中心章按可见章取（不是 _textCacheCenter）：窗口内跳章后 center
                    // 不更新，按它重开窗口会把读者弹回打开窗口时的旧章
                    var center = GetCurrentVisibleWindowChapter();
                    var offset = GetCurrentChapterLineOffset();
                    ClearTextCacheWindowState();
                    if (await TryOpenVirtualWindowAsync(center, offset).ConfigureAwait(true))
                    {
                        ShowNotification(
                            $"目录已按新标题规则重建：{_tocService.CurrentToc.Count} 章", "\uE73E", "Green");
                    }

                    return;
                }

                // 磁盘章节缓存：目录来自缓存 ⇒ 用新规则重建缓存（保留阅读位置），再按原章重开窗口
                await SaveTextCacheAsync(preservePosition: true).ConfigureAwait(true);
                return;
            }

            // 阅读模式未开启：本来就没有由标题规则生成的目录，无需重建
            if (!_isAozoraMode)
            {
                return;
            }

            _tocService.SetProvider(CreateTextTocProvider(_currentTextContent));
            await _tocService.LoadTocAsync().ConfigureAwait(true);

            // 目录没变 ⇒ 什么都不做（连通知都不发，避免无意义打扰）
            var after = _tocService.CurrentToc.Select(t => t.HeadingText).ToArray();
            if (before.SequenceEqual(after))
            {
                return;
            }

            ShowNotification($"目录已按新标题规则重建：{after.Length} 章", "\uE73E", "Green");
        }

        /// <summary>是否正处于书源阅读模式。</summary>
        internal bool IsLegadoMode => _legadoSession is not null;

    // EN: The only purify entry for local text; skipped entirely when reading mode is off.
        /// <summary>
        /// <b>读取 → 渲染之间</b>的统一净化（受一级菜单"替换净化（总开关）"控制）。
        ///
        /// <para><b>净化时机</b>：抓取阶段一律只拿原文（<c>BookSourceEngine</c> 内部
        /// 已不做任何净化），本方法在文本<b>送去渲染之前</b>净化一次。
        /// 三个来源共用这一个入口：书源章节、整本下载后重新打开的 txt、压缩包内 txt。</para>
    ///
  /// <para><b>为什么总开关在这里才生效</b>：源头永远是原文，所以开关切换只是
        /// "净化 / 不净化"的选择——关掉即刻还原原文，<b>不需要重新抓取网络</b>；
        /// 打开时也只是多一遍正则替换（实测 60 章约 37ms，窗口内约 7ms）。</para>
    ///
        /// <para>书源章节走的是 <see cref="BookSourceReadingSession.ConvertToPlainText"/>，
        /// 那里也调同一入口 <see cref="BookSourceService.PurifyForDisplay"/>，
/// 但净化发生在 <c>HTML → 纯文本</c> <b>转换之前</b>——替换规则里有大量
        /// 针对 HTML 标签的规则（去 script / 去注释），先转纯文本就再也匹配不到了。</para>
        /// </summary>
    internal string PurifyTextBeforeDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
      return text;
        }

        // 阅读模式（顶栏 A 按钮）关闭时不做替换净化：模式关闭 = 纯文本浏览原始内容，
        // 开关即"净化 / 不净化"的唯一入口，关掉即刻还原原文，不需要重新读盘。
        if (!_isAozoraMode)
        {
            return text;
        }

        // 注意：<b>不要</b>用"_legadoSession 非空"来跳过净化。
        // 本方法只被本地文件 / 压缩包条目这两条路径调用，它们的内容与书源章节无关；
        // 而"书源阅读中又打开本地文件"时会话可能仍然活着——若据此跳过，
        // 本地文件就不会被净化（总开关对它失效），是个隐蔽的错。
        // 书源章节走的是 BookSourceReadingSession.ConvertToPlainText（在那里净化），
        // 两条路径内容来源不同、互不重叠，不存在二次净化。

        var service = _bookSourceService;
   if (service is null || !service.IsInitialized)
{
            // 书源数据尚未加载：不净化，也不阻塞打开文件
            return text;
        }

        return service.PurifyForDisplay(text, null, null, null);
    }

        /// <summary>
        /// 打开一本书（深链入口：搜索结果点击 / 收藏夹里的书源链接）。
        /// <para>步骤：① 关旧书；② 初始化存储；③ 建会话并打开（详情+目录）；
        /// ④ 切文本模式；⑤ 渲染起始章；⑥ 装目录。</para>
        /// </summary>
        internal async Task<bool> OpenLegadoBookAsync(
            string bookSourceUrl, string bookUrl, CancellationToken token = default)
        {
            var service = BookSources;
            await service.EnsureInitializedAsync(token).ConfigureAwait(true);

            var source = service.Sources.GetByUrl(bookSourceUrl);
            if (source is null)
            {
                ShowNotification($"书源不存在：{bookSourceUrl}", "\uE783", "Red");
                return false;
            }

            // 能力预检：阅读链路依赖阶段 4 的源明确告知，而不是让用户对着空目录发呆
            if (BookSourceCapabilities.ReadingNeedsStage4(source))
            {
                ShowNotification(
                    $"“{source.BookSourceName}”需要完整 JavaScript 支持（阶段 4），阅读可能不完整",
                    "\uE7BA", "Orange");
            }

            await CloseLegadoAsync().ConfigureAwait(true);

            // 打开文本书源 ⇒ 必然离开漫画（漫画会话持有归档句柄与清单写闸）
            await CloseMangaAsync().ConfigureAwait(true);

            var session = new BookSourceReadingSession(service, source, bookUrl);
            var generation = ++_legadoGeneration;

            ShowNotification($"正在打开《{source.BookSourceName}》…", "\uE8F1", "Gray");

            Uviewer.Services.Legado.Models.BookInfo? info = null;
            IReadOnlyList<Uviewer.Services.Legado.Models.TocItem> chapters = Array.Empty<Uviewer.Services.Legado.Models.TocItem>();
            try
            {
                var opened = await session.OpenAsync(token).ConfigureAwait(true);
                info = opened.Info;
                chapters = opened.Chapters;
            }
            catch (OperationCanceledException)
            {
                session.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                // 网络/DNS/SSL 失败：说清是"取不到"而不是含糊的"不适配"
                ShowNotification($"打开失败：{ex.Message}", "\uE783", "Red");
                session.Dispose();
                return false;
            }

            if (generation != _legadoGeneration || token.IsCancellationRequested)
            {
                session.Dispose();
                return false;
            }

            if (chapters.Count == 0)
            {
                // 区分两种失败，给出可执行的下一步（换源 vs 稍后重试）
                ShowNotification(
                    BookSourceCapabilities.ReadingNeedsStage4(source)
                        ? $"“{source.BookSourceName}”的目录依赖完整 JavaScript 支持（阶段 4），当前取不到章节，请换一个书源"
                        : $"“{source.BookSourceName}”未返回章节（站点可能已失效或改版），请换一个书源",
                    "\uE783",
                    "Red");
                session.Dispose();
                return false;
            }

            _legadoSession = session;
            LegadoBookName = session.BookName;

            // 清掉上一个**本地**文本源的路径字段：HasLocalTextSource() 据此判断"有没有本地
            // 文本可走窗口路径"，书源阅读中它必须为 false——否则读书源时开关一次阅读模式，
            // wantVirtualWindow 会按旧本地路径命中磁盘章节缓存，把旧文件的窗口文本装配进来
            // （会话还活着、目录还是书源目录，内容却是旧文件；无缓存时还会按旧路径的保存行
            // 定位，甚至给书源章节套上 .json/.log/.csv 的纯文本锁定）。
            _currentTextFilePath = null;
            _currentTextArchiveEntryKey = null;

            // 断点续读的起始章：命中进度则回到上次位置，否则从第一章开始
            var startIndex = 0;
            var saved = service.GetReadingPosition(source.BookSourceUrl, bookUrl);
            if (saved is not null && saved.ChapterIndex >= 0 && saved.ChapterIndex < chapters.Count)
            {
                startIndex = saved.ChapterIndex;
            }

            // 目录先行：让用户马上看到章节列表，点击任意章都能跳
            InstallLegadoToc(chapters);

            SwitchToTextMode();

            // 书源书恒为阅读模式：它自带净化链路 + 书源目录，与"本地 TXT 是否有缓存"无关。
            // 置位后再渲染首章，顶栏 A 按钮、净化/缓存菜单项、目录按钮才会同步成"可用"。
            _isAozoraMode = true;
            _currentTextRawContent = null;   // 书源正文没有"原文/净化"双态，避免切换模式时误用旧本地文本
            ApplyReadingModeUiState();

            await ShowLegadoChapterAsync(startIndex, token).ConfigureAwait(true);
            return true;
        }

        /// <summary>
        /// 跳到第 <paramref name="index"/> 章并渲染。
        /// <para>渲染前会先取消上一次的加载（代数 + 全局 CTS），保证"最后一次点击生效"。</para>
        /// </summary>
        internal async Task ShowLegadoChapterAsync(int index, CancellationToken token = default)
        {
            if (_legadoSession is null)
            {
                return;
            }

            var generation = ++_legadoGeneration;

            CancelAndResetGlobalTextCts();
            var local = _globalTextCts!.Token;

            int loadGeneration = BeginTextContentLoad();
            _isNavigatingRecent = true;

            // linked CTS 必须 using：旧实现每次跳章新建一个 linked CTS 既不释放也不字段持有，
            // 它向两个父 token 注册的回调一直被父源引用（_globalTextCts 下次替换才可回收）——
            // 连续跳章 = 回调列表无界增长的内存泄漏。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, local);
            try
            {
                var text = await _legadoSession
                    .GetChapterTextAsync(index, linked.Token)
                    .ConfigureAwait(true);

                if (generation != _legadoGeneration || local.IsCancellationRequested)
                {
                    return;
                }

                _legadoChapterIndex = index;

                var title = index >= 0 && index < _legadoSession.Chapters.Count
                    ? _legadoSession.Chapters[index].ChapterName
                    : null;

                // 走与本地 txt 相同的渲染路径：章节标题作为首行，正文紧随其后
                var content = string.IsNullOrWhiteSpace(title)
                    ? text
                    : $"{title}\n\n{text}";

                // 书名 + 章名作为"文件名"，让最近记录/进度 UI 有可读标识
                var displayName = $"{LegadoBookName} - {title}";
                _currentTextRawContent = null;   // 章节正文本身就是最终形态（已在会话内净化）
                await DisplayLoadedText(content, displayName, null, local).ConfigureAwait(true);

                if (generation != _legadoGeneration || local.IsCancellationRequested)
                {
                    return;
                }

                await RevealTextContentAsync(loadGeneration, local).ConfigureAwait(true);
                SyncLegadoTocSelection(index);
            }
            catch (OperationCanceledException)
            {
                RevealTextContentAfterFailure(loadGeneration);
            }
            catch (Exception ex)
            {
                RevealTextContentAfterFailure(loadGeneration);
                ShowNotification($"章节加载失败：{ex.Message}", "\uE783", "Red");
            }
            finally
            {
                _isNavigatingRecent = false;
            }
        }

        /// <summary>上一章 / 下一章（工具栏与键盘共用）。</summary>
        internal Task LegadoPreviousChapterAsync() =>
            _legadoChapterIndex > 0 ? ShowLegadoChapterAsync(_legadoChapterIndex - 1) : Task.CompletedTask;

        internal Task LegadoNextChapterAsync()
        {
            if (_legadoSession is null || _legadoChapterIndex + 1 >= _legadoSession.Chapters.Count)
            {
                return Task.CompletedTask;
            }

            return ShowLegadoChapterAsync(_legadoChapterIndex + 1);
        }

        /// <summary>
        /// 关书：取消预取、落盘进度、断开模式。
        /// <para>切走（打开本地文件/EPUB）时由 <c>SwitchToImageMode</c> 等既有路径调用。</para>
        /// </summary>
        internal async Task CloseLegadoAsync()
        {
            // 代次 +1：让所有在途的打开/跳章立刻失效
            ++_legadoGeneration;

            var session = _legadoSession;
            var lastIndex = _legadoChapterIndex;

            _legadoTocItems = null;

            // 先用"关闭前的最后一章"记进度，再清状态（顺序不能反，否则章号已丢）
            if (session is not null && lastIndex >= 0)
            {
                session.ReportPosition(lastIndex);
            }

            _legadoSession = null;
            _legadoChapterIndex = -1;
            LegadoBookName = string.Empty;

            if (session is null)
            {
                return;
            }

            await session.CloseAsync().ConfigureAwait(true);
            session.Dispose();
        }

        // ============================================================
        // 目录接线
        // ============================================================

        private void InstallLegadoToc(IReadOnlyList<Uviewer.Services.Legado.Models.TocItem> chapters)
        {
            // 库侧目录 → 中立条目 → 宿主 TocItem（SourceLineNumber 复用为章节下标）
            _legadoTocItems = LegadoTocAdapter.Adapt(chapters)
                .Select(e => new Uviewer.Models.TocItem
                {
                    HeadingText = e.Title,
                    HeadingLevel = e.IsVolume ? 0 : 1,
                    SourceLineNumber = e.ChapterIndex,
                    Tag = e.IsVolume ? LegadoVolumeTag : LegadoChapterTag,
                })
                .ToList();

            _tocService.SetProvider(new StaticTocProvider(_legadoTocItems));

            // ⚠ 必须紧接着 LoadTocAsync：SetProvider 只换 provider 并清空 CurrentToc，
            // 不加载的话 CurrentToc 恒为空 —— 顶栏目录按钮（TocButton_Click）读的正是
            // CurrentToc，空列表会被替换成"无目录"占位，表现为"目录装好了但下拉里没有"。
            // StaticTocProvider 是内存快照，GetTocAsync 立即完成，没有额外开销。
            _ = _tocService.LoadTocAsync();

            MainToolbar.SetTextTocItems(_legadoTocItems);
        }

        private void SyncLegadoTocSelection(int index)
        {
            // 高亮当前章（列表尚未构造出该 index 对应项时跳过：ScrollIntoView 收不到 null）
            if (_legadoTocItems is not null && index >= 0 && index < _legadoTocItems.Count)
            {
                MainToolbar.ScrollTextTocIntoView(_legadoTocItems[index]);
            }
        }

        /// <summary>目录条目 Tag：普通章（可点击跳章）。</summary>
        internal const string LegadoChapterTag = "LegadoChapter";

        /// <summary>目录条目 Tag：卷标题（不可点击）。</summary>
        internal const string LegadoVolumeTag = "LegadoVolume";

        /// <summary>目录点击分发：书源章节 → 跳章；卷/其他类型沿用既有行为。</summary>
        internal bool TryHandleLegadoTocClick(Uviewer.Models.TocItem item)
        {
            if (item.Tag?.ToString() != LegadoChapterTag || _legadoSession is null)
            {
                return false;
            }

            _ = ShowLegadoChapterAsync(item.SourceLineNumber);
            return true;
        }

        /// <summary>
        /// 书源搜索入口（工具栏/菜单调用）：弹搜索对话框，点结果即深链打开该书。
        /// <para>存储初始化放在弹框之前完成：这样对话框打开时书源已在手，
        /// 首次搜索不必等 IO。用户看到的是"搜索框已就绪"，而不是"点了没反应"。</para>
        /// </summary>
        internal async Task ShowBookSourceSearchAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var search = new BookSourceSearchService(service);

            // 独立窗口（非模态）：Show() 创建 Window 并激活后立即返回，主窗口不被锁定。
            // 字体按钮复用主窗口顶栏右侧的"界面字体"选择（同一套字体选择器与全局设置，
            // 切换字体族而非字号）；窗口内容跟随当前 UI 字体族。
            var searchWindow = new BookSourceSearchWindow(
                RootGrid,
                search,
                async hit =>
                {
                    // 图片书源自动进漫画模式，文本书源走原文本阅读（见 OpenBookAsync）
                    await OpenBookAsync(hit.SourceUrl, hit.BookUrl).ConfigureAwait(true);
                },
                async filePath =>
                {
                    await OpenDownloadedBookAsync(filePath).ConfigureAwait(true);
                },
                showUiFontPickerAsync: ShowUiFontPickerDialog,
                getUiFontFamily: () => SettingsManager.UIFontFamily);

            searchWindow.Show();
        }

        // ============================================================
        // 净化规则 / 标题章节规则（设置菜单二级项）
        // ============================================================

        /// <summary>导入净化（替换）规则 JSON。</summary>
        internal async Task ImportReplaceRulesAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var rules = new ReplaceRuleService(service, RootGrid, GetWindowHandle, NotifyFromRuleService);
            await rules.PickAndImportAsync().ConfigureAwait(true);
        }

        // EN: Replace-rule manager dialog: probes per-rule hits for the current chapter and refreshes the view on close.
        /// <summary>查看并逐条编辑已导入的净化规则（主从界面，与书源管理同构）。</summary>
        internal async Task ShowReplaceRulesAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            // ①「本章生效」标记：打开窗口前拿**当前章节原文**跑一遍净化管线（后台线程，避免卡 UI）。
            //    口径与真实净化完全一致——只有真的改写了本章文本的规则才会带标记。
            var probeText = GetCurrentChapterRawTextForRuleProbe();
            IReadOnlyCollection<long> effectiveIds = Array.Empty<long>();
            if (!string.IsNullOrEmpty(probeText) && IsReplacePurifyEffective())
            {
                effectiveIds = await Task.Run(() => service.FindEffectiveReplaceRuleIds(
                    probeText, ReplaceScopeBookName, ReplaceScopeBookUrl, ReplaceScopeSourceUrl)).ConfigureAwait(true);
            }

            // ② 关窗后是否需要刷新正文：比"改前 / 改后"的净化规则指纹。
            //    比用"点了保存"当信号更准——只改名称/分组不影响净化（不刷新），
            //    导入 / 删除 / 改匹配串 / 改替换串 / 改开关都会改变指纹（刷新）。
            var rulesFingerprintBefore = TextCacheLogic.ComputeReplaceRulesFingerprint(service.ReplaceRules.GetAll());

            var rules = new ReplaceRuleService(service, RootGrid, GetWindowHandle, NotifyFromRuleService);
            var dialog = new RuleManagerDialog<Uviewer.Services.Legado.Models.ReplaceRule>(
                RootGrid,
                new RuleManagerConfig<Uviewer.Services.Legado.Models.ReplaceRule>
                {
                    DialogTitle = "替换净化规则管理",
                    ItemNoun = "净化规则",
                    EmptyHint = "尚未导入任何净化规则。\n请先用「导入净化规则 JSON…」从 legado / reader 导出的规则文件导入。",
                    ListItems = () => rules.ListWithMarkers(effectiveIds),
                    NameOf = ReplaceRuleService.NameOf,
                    DetailOf = ReplaceRuleService.DetailOf,
                    IdOf = r => r.Id,
                    GetById = rules.GetById,
                    Clone = ReplaceRuleService.Clone,
                    CreateNew = rules.CreateNew,
                    Validate = ReplaceRuleService.Validate,
                    SaveAsync = rules.SaveAsync,
                    DeleteAsync = rules.DeleteAsync,
                    SetEnabledAsync = rules.SetEnabledAsync,
                    OnImportRequested = async () =>
                    {
                        // 与书源管理里的"导入"一致：关掉对话框 → 导入 → 再开回来显示新条目
                        await ImportReplaceRulesAsync().ConfigureAwait(true);
                        await ShowReplaceRulesAsync().ConfigureAwait(true);
                    },
                    Notify = NotifyFromRuleService,
                });

            await dialog.ShowAsync().ConfigureAwait(true);

            // ③ 规则真的变了才刷新，且只重算预处理窗口（书源 RepurifyWindow / 缓存窗口重装配）
            var rulesFingerprintAfter = TextCacheLogic.ComputeReplaceRulesFingerprint(service.ReplaceRules.GetAll());
            if (!string.Equals(rulesFingerprintBefore, rulesFingerprintAfter, StringComparison.Ordinal))
            {
                await RefreshReadingAfterReplaceRulesChangedAsync().ConfigureAwait(true);
            }
        }

        /// <summary>导入标题 / 章节划分规则 JSON。</summary>
        internal async Task ImportTitleRulesAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var rules = new TitleRuleService(
                service, RootGrid, GetWindowHandle, NotifyFromRuleService,
                () => RefreshTextTocFromTitleRulesAsync());

            await rules.PickAndImportAsync().ConfigureAwait(true);
        }

        /// <summary>查看并逐条编辑已导入的标题 / 章节规则（主从界面，与书源管理同构）。</summary>
        internal async Task ShowTitleRulesAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var rules = new TitleRuleService(
                service, RootGrid, GetWindowHandle, NotifyFromRuleService,
                () => RefreshTextTocFromTitleRulesAsync());

            var dialog = new RuleManagerDialog<Uviewer.Services.Legado.Models.TitleRule>(
                RootGrid,
                new RuleManagerConfig<Uviewer.Services.Legado.Models.TitleRule>
                {
                    DialogTitle = "标题 / 章节划分规则管理",
                    ItemNoun = "标题规则",
                    EmptyHint = "尚未导入任何标题规则。\n内置默认规则已覆盖“第一章 xxx / Chapter N / Markdown 标题”等常见写法；\n需要更多写法时用「导入标题规则 JSON…」补充。",
                    ListItems = rules.List,
                    NameOf = TitleRuleService.NameOf,
                    DetailOf = TitleRuleService.DetailOf,
                    IdOf = r => r.Id,
                    GetById = rules.GetById,
                    Clone = TitleRuleService.Clone,
                    CreateNew = rules.CreateNew,
                    Validate = TitleRuleService.Validate,
                    SaveAsync = rules.SaveAsync,
                    DeleteAsync = rules.DeleteAsync,
                    SetEnabledAsync = rules.SetEnabledAsync,
                    OnImportRequested = async () =>
                    {
                        // 与书源管理里的"导入"一致：关掉对话框 → 导入 → 再开回来显示新条目
                        await ImportTitleRulesAsync().ConfigureAwait(true);
                        await ShowTitleRulesAsync().ConfigureAwait(true);
                    },
                    Notify = NotifyFromRuleService,
                });

            await dialog.ShowAsync().ConfigureAwait(true);
        }

        private void NotifyFromRuleService(string message, string icon, string color) =>
            ShowNotification(message, icon, color);

        /// <summary>
        /// 启动后把持久化的"替换净化总开关"状态回写到菜单勾选框。
        /// <para>不阻塞启动：后台读一次小 JSON 即可，读完再回写勾选。
        /// 用户在这之前手动点过也没关系——回写的是存储里的真实状态。</para>
        /// </summary>
        internal async Task SyncPurifyEnabledMenuAsync()
        {
            try
            {
                var service = BookSources;
                await service.EnsureInitializedAsync().ConfigureAwait(true);
                MainToolbar.SetLegadoPurifyEnabled(service.PurifyEnabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync purify switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换替换净化总开关（一级菜单项）。
        /// <para>关掉后净化管线整段空转（零开销），但各条规则自身的开关状态<b>不被改动</b>——
        /// 重新打开总开关即恢复用户原本的逐条配置。</para>
        ///
    /// <para><b>只重算窗口，不重算全书</b>：当前章 + 前后邻近各若干章（见
    /// <see cref="BookSourceReadingSession.RepurifyWindow"/>）。全书重算是 O(全书)
    /// 的正则替换（实测 60 章 60ms，1663 章按比例约 1.7s），每次开关都等 1.7s 不可接受。
    /// 窗口外的章节在读到时按需重算（原文仍在缓存里，同样零网络）。</para>
        /// </summary>
        internal async Task SetPurifyEnabledAsync(bool enabled)
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var now = await service.SetPurifyEnabledAsync(enabled).ConfigureAwait(true);

            // 回写菜单勾选状态（落盘失败时以实际状态为准）
            MainToolbar.SetLegadoPurifyEnabled(now);

            // 正在读书源：就地重算窗口内的章并重绘当前章（用户立刻能看到效果）
            if (_legadoSession is not null && _legadoChapterIndex >= 0)
            {
                var touched = _legadoSession.RepurifyWindow(_legadoChapterIndex, RepurifyWindowBefore, RepurifyWindowAfter);
                if (touched.Count > 0)
          {
  await ShowLegadoChapterAsync(_legadoChapterIndex).ConfigureAwait(true);
         }
            }
    else if (!string.IsNullOrEmpty(_currentTextContent))
        {
   // 正在看本地文件（含整本下载下来的书）：重新读一遍 + 重新净化。
   // 走"重载"通路而不是就地改 _currentTextContent —— 后者存的是**已净化**的文本，
            // 已经丢掉了原文，无法还原（这正是"源头存原文"的意义所在）。
            await ReloadCurrentTextAsync().ConfigureAwait(true);
        }

      ShowNotification(
       now ? "已开启替换净化" : "已关闭替换净化（各条规则配置已保留）",
    now ? "\uE73E" : "\uE711",
          now ? "Green" : "Gray");
  }

        /// <summary>
        /// 重新加载当前文本（用于净化总开关切换后重算本地文件内容）。
        /// <para>复用既有的三条重载通路：本地文件 / 压缩包条目 / 内存文本。</para>
  /// </summary>
        private async Task ReloadCurrentTextAsync()
    {
            try
  {
        if (!string.IsNullOrEmpty(_currentTextFilePath))
      {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_currentTextFilePath);
   await LoadTextFileAsync(file).ConfigureAwait(true);
       return;
    }

      if (_currentTextArchiveEntryKey is not null && _archiveSession.HasArchive)
         {
     // 压缩包条目：按既有范式构造一个 ImageEntry 再重载（见 EncodingItem_Click）
        var entry = new ImageEntry
    {
      ArchiveEntryKey = _currentTextArchiveEntryKey,
         DisplayName = FileNameText.Text,
            };
   await LoadTextFromArchiveEntryAsync(entry).ConfigureAwait(true);
     return;
         }
 }
            catch (Exception ex)
       {
       System.Diagnostics.Debug.WriteLine($"Reload current text failed: {ex.Message}");
   }
        }

        // ============================================================
        // 阅读窗口约定（项目默认）：前 2 后 5 + 当前章 = 8 章。
        // 书源预取/重净化、本地章节缓存（TextCacheFormat.WindowBefore/After）统一用这个窗口。
        // ============================================================

        /// <summary>窗口：当前章之前重算的章数（约定值 2）。</summary>
        private const int RepurifyWindowBefore = 2;

        /// <summary>窗口：当前章之后重算的章数（约定值 5，与 TextCacheFormat.WindowAfter 一致）。</summary>
        private const int RepurifyWindowAfter = 5;
        /// <para>刻意走 <see cref="LoadTextFileAsync"/> 这条<b>本地文件</b>通路而不是
        /// <c>OpenLegadoBookAsync</c>：下载的意义就是"从此不依赖网络与书源"，
        /// 打开已下好的文件才能兑现这一点。副作用一并复用：进最近记录、侧栏选中、
        /// 断点续读、全文高亮、三种排版，全部零新增代码。</para>
        /// </summary>
        internal async Task OpenDownloadedBookAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ShowNotification("下载文件不存在", "\uE783", "Red");
                return;
            }

            // 漫画清单（.uvmanga）：走漫画离线打开，不能当文本读
            if (MangaLibrary.LooksLikeMangaManifest(filePath))
            {
                await OpenMangaManifestAsync(filePath).ConfigureAwait(true);
                return;
            }

            // 先退出书源阅读模式：避免旧会话的预取任务继续抢网络/内存
            await CloseLegadoAsync().ConfigureAwait(true);

            // 打开本地文本 ⇒ 离开漫画
            await CloseMangaAsync().ConfigureAwait(true);

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                await LoadTextFileAsync(file).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShowNotification($"打开下载文件失败：{ex.Message}", "\uE783", "Red");
            }
        }

        /// <summary>
        /// 书源管理入口：图形化查看/编辑全部已导入的书源条目。
        /// <para>对话框里的"导入 JSON"按钮会先关闭管理器再调导入，导入完成后
        /// 重新打开管理器——这样用户能立刻看到新条目，不必手动重开。</para>
        /// </summary>
        internal async Task ShowBookSourceManagerAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var dialog = new BookSourceManagerDialog(
                RootGrid,
                service,
                async _ =>
                {
                    // 从管理器里点"导入"：关掉管理器 → 导入 → 再开回来显示新条目
                    await ImportBookSourcesAsync().ConfigureAwait(true);
                    await ShowBookSourceManagerAsync().ConfigureAwait(true);
                },
                NotifyFromRuleService);

            await dialog.ShowAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// 导入书源 JSON（多书源数组文件，如 Sample/*.json）。
        /// <para>导入用库侧流式实现（5MB/400+ 源峰值内存有界），结果以通知形式反馈
        /// 新增/更新/跳过数量。导入后管理器里的内存快照自动失效，下次读取即见新数据。</para>
        /// </summary>
        internal async Task ImportBookSourcesAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var importer = new BookSourceImportService(
                service,
                RootGrid,
                () => GetWindowHandle(),
                (msg, icon, color) => ShowNotification(msg, icon, color));

            await importer.PickAndImportAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// 整本下载的目录设置（设置 →「书源管理」→「下载设置」）。
        /// <para>布局：说明文字 + 一行「完整路径文本框 | 浏览…」+ 校验提示。宽度给足 560，
        /// 因为 <c>D:\Books\Legado\Nihongo\...</c> 这类路径一屏能看完比省空间重要。</para>
        /// <para><b>浏览按钮复用 <see cref="JsonPicker.PickFolderAsync"/></b>：与「导入书源 JSON」
        /// 走同一套 <c>InitializeWithWindow</c> 绑定与错误提示——句柄来源、失败行为只有一份定义。</para>
        /// <para><b>保存语义</b>：只在点「保存」时落盘（与外部程序路径设置一致），
        /// 取消/关闭不留痕迹；落盘前用 <see cref="BookSourceService.NormalizeDownloadDirectory"/>
        /// 校验，非法输入让对话框留在原地而不是先关再报错。</para>
        /// </summary>
        internal async Task ShowDownloadSettingsAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            const double DesiredContentWidth = 560;

            var palette = LegadoTheme.For(RootGrid);

            // WinUI 铁律：AcceptsReturn/TextWrapping 必须在 Text **之前**设——
            // 对象初始化器按书写顺序执行，WinUI 在 AcceptsReturn=False（默认）时
            // 会按单行规范化 Text，多行串被砍在第一个换行符（之后再打开不还原）。
            var input = new TextBox
            {
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,

                // 纵向拉伸：与右侧"浏览"按钮同高，由 Grid 行高统一决定（不硬编高度值）
                VerticalAlignment = VerticalAlignment.Stretch,

                Text = service.DownloadDirectory,
                PlaceholderText = @"例如 D:\Books",
            };

            // 按钮沿用项目默认 Button 样式（对话框内的"导入/保存/删除"都是默认样式，
            // 不用强调色/工具栏样式），只把内边距改成纯横向并给最小宽度。
            var browseButton = new Button
            {
                Content = "浏览…",
                Padding = new Thickness(16, 0, 16, 0),
                MinWidth = 88,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetColumn(browseButton, 1);

            var pathRow = new Grid { ColumnSpacing = 8 };
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathRow.Children.Add(input);
            pathRow.Children.Add(browseButton);

            var error = new TextBlock
            {
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,

                // 文字色必须灰度（见 LegadoTheme 注释），警示语义靠前置 ⚠ 图形
                Foreground = palette.TextSecondary,
            };

            browseButton.Click += async (_, _) =>
            {
                var picked = await JsonPicker
                    .PickFolderAsync(() => GetWindowHandle(), (msg, icon, color) => ShowNotification(msg, icon, color), input.Text)
                    .ConfigureAwait(true);

                if (picked is null)
                {
                    return;   // 用户取消（失败原因已由 picker 提示）
                }

                input.Text = picked;
                error.Visibility = Visibility.Collapsed;
            };

            input.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;

            bool TryValidate(out string message)
            {
                try
                {
                    input.Text = BookSourceService.NormalizeDownloadDirectory(input.Text);
                    message = string.Empty;
                    return true;
                }
                catch (ArgumentException ex)
                {
                    message = $"⚠ {ex.Message}";
                    return false;
                }
            }

            var panel = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock
                    {
                        Text = "整本下载的书籍会保存到此目录（目录不存在时在首次下载时自动创建）。"
                             + $"默认：{BookSourceService.DefaultDownloadDirectory}"
                             + "\n下载结果一律合并保存为单个 TXT 文件（一章一段、章名独立成行），"
                             + "文件头带原始链接标记，支持「在线更新」差分续更。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = palette.TextSecondary,
                    },
                    pathRow,
                    error,
                },
            };

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "下载设置",
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            // 尺寸走资源键（属性无效），内容矮就矮着
            LegadoDialogLayout.Fit(dialog, RootGrid, DesiredContentWidth, preferredContentHeight: 240);

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (TryValidate(out var message))
                {
                    return;
                }

                error.Text = message;
                error.Visibility = Visibility.Visible;
                input.Focus(FocusState.Programmatic);
                args.Cancel = true;   // 非法路径不关闭对话框
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string saved;
            try
            {
                saved = await service.SetDownloadDirectoryAsync(input.Text).ConfigureAwait(true);
            }
            catch (ArgumentException ex)
            {
                ShowNotification(ex.Message, "\uE783", "Red");
                return;
            }

            ShowNotification($"下载目录已设为：{saved}", "\uE73E", "Green");
        }

        /// <summary>
        /// 并行设置对话框：输入式调整「多源搜索」与「整本下载」的并发数。
        /// <para><b>范围语义</b>：搜索打不同站点可放开（1–64），下载打同一站点须保守（1–16）；
        /// 提示文本把范围写清楚，越界输入在保存时钳制到边界（不弹错、不拒收）。
        /// 数值立即持久化（<c>legadoSettings.json</c>），下一次搜索 / 下载即生效——
        /// 进行中的任务不受影响（并发闸门在每次任务开始时读取）。</para>
        /// </summary>
        internal async Task ShowParallelSettingsAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            const double DesiredContentWidth = 560;

            var palette = LegadoTheme.For(RootGrid);

            // 数字输入：NumberBox 自带前后步进与输入校验（SpinButtonPlacementMode 内联），
            // 比 TextBox + 手写 int.TryParse 少一大段胶水代码。
            var searchInput = new Microsoft.UI.Xaml.Controls.NumberBox
            {
                Value = service.SearchConcurrency,
                MinWidth = 120,
                SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Inline,
                SmallChange = 4,
                LargeChange = 16,
            };

            var downloadInput = new Microsoft.UI.Xaml.Controls.NumberBox
            {
                Value = service.DownloadConcurrency,
                MinWidth = 120,
                SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Inline,
                SmallChange = 1,
                LargeChange = 4,
            };

            // 两行"标签 + 输入框"：标签靠左、输入靠右对齐成列，比纯竖排省高度
            Grid Row(string label, FrameworkElement editor, string range)
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var rangeText = new TextBlock
                {
                    Text = range,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.75,
                    FontSize = 12,

                    // 文字色必须灰度（LegadoTheme 约定），语义靠文案本身表达
                    Foreground = palette.TextSecondary,
                };
                Grid.SetColumn(text, 0);
                Grid.SetColumn(editor, 1);
                Grid.SetColumn(rangeText, 2);
                grid.Children.Add(text);
                grid.Children.Add(editor);
                grid.Children.Add(rangeText);
                return grid;
            }

            var panel = new StackPanel
            {
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock
                    {
                        Text = "并发数立即保存，下一次搜索 / 下载生效；进行中的任务不受影响。"
                             + "并发越高越快，但对站点压力也越大。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = palette.TextSecondary,
                    },
                    Row("多源搜索", searchInput, "1 – 64（打不同站点，可放开）"),
                    Row("整本下载", downloadInput, "1 – 16（打同一站点，须保守）"),
                },
            };

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "并行设置",
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            LegadoDialogLayout.Fit(dialog, RootGrid, DesiredContentWidth, preferredContentHeight: 260);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            // NumberBox 非法输入（空/非数字）时 Value 会回落为 NaN —— 兜底回当前值
            var search = double.IsNaN(searchInput.Value) ? service.SearchConcurrency : (int)searchInput.Value;
            var download = double.IsNaN(downloadInput.Value) ? service.DownloadConcurrency : (int)downloadInput.Value;

            var (s, d) = await service.SetParallelismAsync(search, download).ConfigureAwait(true);
            ShowNotification($"并行数已保存：搜索 {s} / 下载 {d}", "\uE73E", "Green");
        }

        /// <summary>
        /// 在线更新（书源管理二级菜单入口）：对<b>当前打开的文档</b>执行差分更新。
        /// <para>只有带原始链接标记的书源下载 TXT 才能更新（标记在下载时写入文件头，
        /// 目录快照在同目录 sidecar）。差分更新只联网下载新增/变化章；远程目录大规模修改时
        /// 引擎侧会自动另存新文件（原文件不动）。更新成功后自动重载当前文件——
        /// 旧章节缓存因文件变化失效，重载后按"下载文件"口径重建阅读模式与缓存。</para>
        /// </summary>
        internal async Task ShowOnlineUpdateAsync()
        {
            // 漫画：清单（book.uvmanga）即身份来源，走漫画自己的差分更新
            if (IsMangaMode)
            {
                await UpdateMangaAsync().ConfigureAwait(true);
                return;
            }

            var path = _currentTextFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ShowNotification("请先打开书源下载的 TXT 文件再在线更新", "\uE783", "Red");
                return;
            }

            if (!DownloadedBookMarker.IsDownloadedFile(path))
            {
                ShowNotification("当前文件不是书源下载的文本（缺原始链接标记），无法在线更新", "\uE783", "Red");
                return;
            }

            ShowNotification("正在检查远程目录…", "\uE8F1", "Gray");

            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);
            var downloads = new BookSourceDownloadService(service);
            var result = await downloads.UpdateAsync(path).ConfigureAwait(true);

            if (!result.Success)
            {
                ShowNotification(result.Message, "\uE783", "Red");
                return;
            }

            if (result.MassiveResaved)
            {
                // 大改另存：原文件保留，新文件另存——不自动切换，由用户决定是否打开
                ShowNotification(result.Message, "\uE73E", "Green");
                return;
            }

            ShowNotification($"{result.Message}，正在重新加载…", "\uE73E", "Green");

            // 重载当前文件：刷新内容与章节缓存（旧缓存因文件变化自动失效）
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(true);
                await LoadTextFileAsync(file).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShowNotification($"更新完成，但重新加载失败：{ex.Message}", "\uE783", "Red");
            }
        }

        /// <summary>
        /// 取主窗口句柄（FileOpenPicker 在 WinUI3 里必须绑定窗口才能正确弹出）。
        /// <para><b>不能用 <c>AppWindow</c> 换算</b>：<c>WindowNative.GetWindowHandle</c> 只有一个
        /// <c>(object)</c> 重载，内部转 <c>IWindowNative</c>；<c>Microsoft.UI.Windowing.AppWindow</c>
        /// 不实现该接口，换出来是无效句柄 → picker 抛
        /// <c>Invalid window handle (0x80070578)</c>。这里用宿主从真正的 <c>Window</c> 取到的 HWND。</para>
        /// </summary>
        private nint GetWindowHandle() => _appStateHost.WindowHandle;

        /// <summary>
        /// 静态目录提供器：书源目录在打开时已全部取回（阶段 2 的分页并行已完成），
        /// 不需要 provider 再去拉一次。
        /// </summary>
        private sealed class StaticTocProvider : ITocProvider
        {
            private readonly List<Uviewer.Models.TocItem> _items;

            public StaticTocProvider(List<Uviewer.Models.TocItem> items) => _items = items;

            public Task<List<Uviewer.Models.TocItem>> GetTocAsync(CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(_items);
            }
        }
    }
}
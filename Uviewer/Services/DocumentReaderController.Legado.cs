using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Services.BookSources;
using Uviewer.Services;
using Uviewer.Services.Legado;

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

        /// <summary>适配后的目录项（供点击分发与高亮定位复用，避免重复转换）。</summary>
        private List<Uviewer.Models.TocItem>? _legadoTocItems;

        /// <summary>打开代次：只有最新一次打开/跳章的结果才允许渲染。</summary>
        private int _legadoGeneration;

        /// <summary>当前章节下标（-1 = 未打开书源）。</summary>
        private int _legadoChapterIndex = -1;

        /// <summary>当前书源书名（显示在标题栏）。</summary>
        internal string LegadoBookName { get; private set; } = string.Empty;

        /// <summary>书源服务（首次访问时惰性构造）。</summary>
        internal BookSourceService BookSources => _bookSourceService ??= new BookSourceService();

        /// <summary>书源会话（未打开书时 null）。</summary>
        internal BookSourceReadingSession? LegadoSession => _legadoSession;

        /// <summary>是否正处于书源阅读模式。</summary>
        internal bool IsLegadoMode => _legadoSession is not null;

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

            var session = new BookSourceReadingSession(service, source, bookUrl);
            var generation = ++_legadoGeneration;

            ShowNotification($"正在打开《{source.BookSourceName}》…", "\uE8F1", "Gray");

            var opened = await session.OpenAsync(token).ConfigureAwait(true);
            if (generation != _legadoGeneration || token.IsCancellationRequested)
            {
                session.Dispose();
                return false;
            }

            if (opened.Chapters.Count == 0)
            {
                ShowNotification("目录为空：书源规则可能不适配当前网站", "\uE783", "Red");
                session.Dispose();
                return false;
            }

            _legadoSession = session;
            LegadoBookName = session.BookName;

            // 目录先行：让用户马上看到章节列表，点击任意章都能跳
            InstallLegadoToc(opened.Chapters);

            SwitchToTextMode();
            await ShowLegadoChapterAsync(opened.StartIndex, token).ConfigureAwait(true);
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
            try
            {
                var text = await _legadoSession
                    .GetChapterTextAsync(index, CancellationTokenSource.CreateLinkedTokenSource(token, local).Token)
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

        private void InstallLegadoToc(IReadOnlyList<BookSource.Core.Models.TocItem> chapters)
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
            var dialog = new BookSourceSearchDialog(
                RootGrid,
                search,
                async hit =>
                {
                    await OpenLegadoBookAsync(hit.SourceUrl, hit.BookUrl).ConfigureAwait(true);
                });

            await dialog.ShowAsync().ConfigureAwait(true);
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
                });

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

        /// <summary>取主窗口句柄（FileOpenPicker 在 WinUI3 里必须绑定窗口才能正确弹出）。</summary>
        private nint GetWindowHandle()
        {
            var window = AppWindow;
            return window is null ? nint.Zero : WinRT.Interop.WindowNative.GetWindowHandle(window);
        }

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
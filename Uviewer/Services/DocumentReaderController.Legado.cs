using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Services.BookSources;
using Uviewer.Models;
using Uviewer.Services;
using Uviewer.Services.Legado;
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

        /// <summary>
        /// 当前启用的标题/章节划分规则（供 TXT 目录生成）。
        /// <para>存储可能尚未加载（BookSources 首次访问才构造），故这里容忍未初始化状态：
        /// 返回空列表时 <c>TextTocProvider</c> 会退回到内置标记识别，功能不受影响。</para>
        /// </summary>
        internal List<BookSource.Core.Models.TitleRule> GetEnabledTitleRules()
        {
            var service = _bookSourceService;
            return service is null || !service.IsInitialized
                ? new List<BookSource.Core.Models.TitleRule>()
                : service.TitleRules.GetEnabledOrdered();
        }

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

            // 章节窗口模式：标题规则变了 ⇒ 标题规则指纹变 ⇒ 缓存失效，
            // 直接重载（重载会走整本路径，下次打开才按新规则建缓存）
            if (TextCacheActive)
            {
                await ReloadCurrentTextAsync().ConfigureAwait(true);
                return;
            }

            _tocService.SetProvider(new TextTocProvider(_currentTextContent, null, GetEnabledTitleRules()));
            await _tocService.LoadTocAsync().ConfigureAwait(true);
        }

        /// <summary>是否正处于书源阅读模式。</summary>
        internal bool IsLegadoMode => _legadoSession is not null;

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

            var session = new BookSourceReadingSession(service, source, bookUrl);
            var generation = ++_legadoGeneration;

            ShowNotification($"正在打开《{source.BookSourceName}》…", "\uE8F1", "Gray");

            BookSource.Core.Models.BookInfo? info = null;
            IReadOnlyList<BookSource.Core.Models.TocItem> chapters = Array.Empty<BookSource.Core.Models.TocItem>();
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
            var dialog = new BookSourceSearchDialog(
                RootGrid,
                search,
                async hit =>
                {
                    await OpenLegadoBookAsync(hit.SourceUrl, hit.BookUrl).ConfigureAwait(true);
                },
                async filePath =>
                {
                    await OpenDownloadedBookAsync(filePath).ConfigureAwait(true);
                });

            await dialog.ShowAsync().ConfigureAwait(true);
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

        /// <summary>查看并逐条编辑已导入的净化规则（主从界面，与书源管理同构）。</summary>
        internal async Task ShowReplaceRulesAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var rules = new ReplaceRuleService(service, RootGrid, GetWindowHandle, NotifyFromRuleService);
            var dialog = new RuleManagerDialog<BookSource.Core.Models.ReplaceRule>(
                RootGrid,
                new RuleManagerConfig<BookSource.Core.Models.ReplaceRule>
                {
                    DialogTitle = "替换净化规则管理",
                    ItemNoun = "净化规则",
                    EmptyHint = "尚未导入任何净化规则。\n请先用「导入净化规则 JSON…」从 legado / reader 导出的规则文件导入。",
                    ListItems = rules.List,
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

            var dialog = new RuleManagerDialog<BookSource.Core.Models.TitleRule>(
                RootGrid,
                new RuleManagerConfig<BookSource.Core.Models.TitleRule>
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

        /// <summary>切换净化后重算窗口：当前章之前重算 2 章、之后重算 4 章（与预取窗口一致）。</summary>
        private const int RepurifyWindowBefore = 2;

        /// <summary>切换净化后重算窗口：当前章之后重算 4 章。</summary>
        private const int RepurifyWindowAfter = 4;
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

            // 先退出书源阅读模式：避免旧会话的预取任务继续抢网络/内存
            await CloseLegadoAsync().ConfigureAwait(true);

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
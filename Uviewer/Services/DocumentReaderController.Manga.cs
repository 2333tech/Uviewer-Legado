using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;
using Uviewer.Services;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.Models;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer
{
    /// <summary>
    /// 漫画阅读（图片书源）控制器：把 <see cref="MangaReadingSession"/> 接到宿主的
    /// 归档/图片查看链路上。
    ///
    /// <para><b>为什么复用"归档阅读"而不是另写一套渲染</b>：漫画落盘形态是
    /// "一章一个 CBZ"（见 <see cref="MangaLibrary"/>），而宿主本来就能把
    /// 归档里的图片当作导航序列打开（<c>ArchiveDocumentController</c>）。
    /// 于是缩放/双击/并排/锐化/预加载/键盘翻页全部零新增代码，
    /// 与用户自己打开一个 .cbz 的体验完全一致。</para>
    ///
    /// <para><b>分章加载</b>：图片导航的序列始终是"当前章这一包"。
    /// 翻到章末（或章首再往前）时由 <see cref="NavigateMangaBeyondEdgeAsync"/>
    /// 接管：下载下一章的章包并切换——用户感觉是连续翻页，实际是按章换包。</para>
    ///
    /// <para><b>目录</b>：章节列表来自书源目录（与文本阅读同一套 <c>TocItem</c>），
    /// 打开漫画即装入顶栏目录下拉（复用 PDF 目录那个按钮位，两者互斥不会同时出现）。</para>
    /// </summary>
    internal sealed partial class DocumentReaderController
    {
        private MangaReadingSession? _mangaSession;
        private List<Uviewer.Models.TocItem>? _mangaTocItems;
        private int _mangaChapterIndex = -1;
        private int _mangaGeneration;

        /// <summary>后台「下载全本」的取消源（再点一次菜单 = 取消；关书 = 取消）。</summary>
        private CancellationTokenSource? _mangaFullDownloadCts;

        /// <summary>是否正在读漫画（图片书源的漫画模式）。</summary>
        internal bool IsMangaMode => _mangaSession is not null;

        /// <summary>当前漫画书名（标题栏/通知用）。</summary>
        internal string MangaBookName => _mangaSession?.BookName ?? string.Empty;

        /// <summary>当前漫画书目清单路径（<c>book.uvmanga</c>；在线更新用）。</summary>
        internal string? MangaManifestPath => _mangaSession?.ManifestPath;

        /// <summary>漫画库根目录（跟随下载设置）。</summary>
        private string MangaRootDirectory => MangaLibrary.ResolveRoot(BookSources.DownloadDirectory);

        // ============================================================
        // 打开
        // ============================================================

        /// <summary>
        /// 打开一本书（搜索结果点击入口）：<b>图片书源自动进入漫画模式</b>，
        /// 文本书源仍走原来的文本阅读。
        /// <para>"打开时自动启用目录"的落点就在这里——目录在打开流程内装好，
        /// 不依赖用户先开阅读模式（漫画本来就不是文本阅读模式）。</para>
        /// </summary>
        internal async Task<bool> OpenBookAsync(string bookSourceUrl, string bookUrl, CancellationToken token = default)
        {
            var service = BookSources;
            await service.EnsureInitializedAsync(token).ConfigureAwait(true);

            var source = service.Sources.GetByUrl(bookSourceUrl);
            if (source is not null && source.SourceKind == BookSourceKind.Image)
            {
                return await OpenMangaBookAsync(source, bookUrl, token).ConfigureAwait(true);
            }

            return await OpenLegadoBookAsync(bookSourceUrl, bookUrl, token).ConfigureAwait(true);
        }

        /// <summary>
        /// 在线打开一本漫画：建会话 → 拉目录（写进 <c>book.uvmanga</c>）→ 装目录 →
        /// 下载起始章并显示 → 后台按"前二后五"预取。
        /// </summary>
        private async Task<bool> OpenMangaBookAsync(
            BookSourceModel source, string bookUrl, CancellationToken token)
        {
            await CloseLegadoAsync().ConfigureAwait(true);
            await CloseMangaAsync().ConfigureAwait(true);

            var session = new MangaReadingSession(BookSources, source, bookUrl, MangaRootDirectory);
            var generation = ++_mangaGeneration;

            ShowNotification($"正在打开漫画《{source.BookSourceName}》…", "\uE8F1", "Gray");

            MangaReadingSession.OpenResult opened;
            try
            {
                opened = await session.OpenOnlineAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                session.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                session.Dispose();
                ShowNotification($"打开漫画失败：{ex.Message}", "\uE783", "Red");
                return false;
            }

            if (generation != _mangaGeneration || token.IsCancellationRequested)
            {
                session.Dispose();
                return false;
            }

            if (opened.Chapters.Count == 0)
            {
                session.Dispose();
                ShowNotification(
                    $"“{source.BookSourceName}”未返回漫画目录（站点可能已失效或不是图片源），请换一个书源",
                    "\uE783", "Red");
                return false;
            }

            _mangaSession = session;
            InstallMangaToc(opened.Chapters);

            var ok = await ShowMangaChapterAsync(opened.ChapterIndex, opened.PageIndex, token).ConfigureAwait(true);
            if (!ok)
            {
                return false;
            }

            // 左侧资源管理器定位到"这本书的本地文件夹"：漫画的落盘形态就是
            // "一个文件夹 + 每章一个 CBZ"，用户下载完最想看到的是这些章包本身。
            NavigateExplorerToMangaFolder(session);

            session.PrefetchWindowAsync(opened.ChapterIndex);
            return true;
        }

        /// <summary>
        /// 离线打开一本已下载的漫画：<c>.uvmanga</c> 即入口（类似 .slnx 声明项目根）。
        /// <para>只读清单，不联网也不依赖书源是否还在。</para>
        /// </summary>
        internal async Task<bool> OpenMangaManifestAsync(string manifestPath, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                ShowNotification("漫画清单不存在", "\uE783", "Red");
                return false;
            }

            await CloseLegadoAsync().ConfigureAwait(true);
            await CloseMangaAsync().ConfigureAwait(true);

            MangaReadingSession session;
            try
            {
                session = MangaReadingSession.OpenLocal(manifestPath);
            }
            catch (Exception ex)
            {
                ShowNotification($"打开漫画失败：{ex.Message}", "\uE783", "Red");
                return false;
            }

            _mangaSession = session;
            InstallMangaToc(session.Chapters);

            var (chapterIndex, pageIndex) = session.Position;
            var ok = await ShowMangaChapterAsync(chapterIndex, pageIndex, token).ConfigureAwait(true);
            if (ok)
            {
                NavigateExplorerToMangaFolder(session);
                ShowNotification($"已打开漫画：{session.BookName}", "\uE73E", "Green");
            }

            return ok;
        }

        /// <summary>
        /// 让左侧资源管理器跳到这本漫画的文件夹（目录不存在则什么都不做）。
        /// <para>判重交给宿主：<c>ShouldLoadExplorerFolder</c> 已在同一个文件夹时跳过重载。</para>
        /// </summary>
        private void NavigateExplorerToMangaFolder(MangaReadingSession session)
        {
            try
            {
                if (Directory.Exists(session.BookFolder))
                {
                    _appStateHost.LoadExplorerFolder(session.BookFolder);
                }
            }
            catch (Exception ex)
            {
                // 侧栏导航失败不影响阅读（文件夹可能被独占/被删）
                System.Diagnostics.Debug.WriteLine($"Load manga folder failed: {ex.Message}");
            }
        }

        // ============================================================
        // 显示 / 导航
        // ============================================================

        /// <summary>
        /// 显示第 <paramref name="chapterIndex"/> 章（必要时先下载该章章包）。
        /// </summary>
        /// <param name="pageIndex">章内页下标；传负数表示"最后一页"（上一章回退用）。</param>
        internal async Task<bool> ShowMangaChapterAsync(
            int chapterIndex, int pageIndex = 0, CancellationToken token = default)
        {
            var session = _mangaSession;
            if (session is null)
            {
                return false;
            }

            var chapters = session.Chapters;
            if (chapters.Count == 0)
            {
                return false;
            }

            chapterIndex = Math.Clamp(chapterIndex, 0, chapters.Count - 1);
            var generation = ++_mangaGeneration;

            var chapterName = chapters[chapterIndex].Name;
            ShowNotification($"正在准备第 {chapterIndex + 1} 章：{chapterName}…", "\uE896", "Gray");

            // 下载进度只在后台线程回调：Progress<T> 会把它切回 UI 线程
            var progress = new Progress<MangaReadingSession.MangaImageProgress>(p =>
            {
                if (generation != _mangaGeneration)
                {
                    return;
                }

                ShowNotification($"{p.ChapterName}：{p.Done}/{p.Total} 页", "\uE896", "Gray");
            });

            MangaReadingSession.ChapterArchiveResult result;
            try
            {
                result = await session
                    .EnsureChapterAsync(chapterIndex, progress, token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                ShowNotification($"第 {chapterIndex + 1} 章下载失败：{ex.Message}", "\uE783", "Red");
                return false;
            }

            if (generation != _mangaGeneration)
            {
                return false;
            }

            if (!result.HasPages)
            {
                ShowNotification(
                    session.IsOffline
                        ? $"第 {chapterIndex + 1} 章尚未下载（离线模式不会联网）"
                        : $"第 {chapterIndex + 1} 章没有取到图片（源站可能限制访问）",
                    "\uE783", "Red");
                return false;
            }

            var page = pageIndex < 0 ? result.PageNames.Count - 1 : pageIndex;
            page = Math.Clamp(page, 0, result.PageNames.Count - 1);

            _mangaChapterIndex = chapterIndex;

            // 打开章包：起始条目 = 该页在归档里的文件名
            await OpenArchiveAsync(result.ArchivePath, result.PageNames[page]).ConfigureAwait(true);

            // 归档链路会把窗口切到图片模式；漫画需要把"目录/上下章"重新点亮
            ApplyMangaReadingUiState();

            SyncMangaTocSelection(chapterIndex);
            session.SavePosition(chapterIndex, page);

            if (!result.Complete && result.FailedPages > 0)
            {
                ShowNotification(
                    $"第 {chapterIndex + 1} 章 {result.PageNames.Count} 页中有 {result.FailedPages} 页未取到（可稍后重试）",
                    "\uE7BA", "Orange");
            }

            return true;
        }

        /// <summary>
        /// 上一章 / 下一章（顶栏 ↑↓ 与目录跳转共用）。
        /// 返回 false 表示当前不是漫画模式（调用方继续走原有逻辑）。
        /// </summary>
        internal async Task<bool> NavigateMangaChapterAsync(bool forward)
        {
            var session = _mangaSession;
            if (session is null)
            {
                return false;
            }

            var target = _mangaChapterIndex + (forward ? 1 : -1);
            if (target < 0 || target >= session.Chapters.Count)
            {
                return true;   // 首/末章：吞掉按键，避免顺手翻到别的文件
            }

            // 上一章定位到末页（与文本阅读"上一章停在章尾"的直觉一致）
            await ShowMangaChapterAsync(target, forward ? 0 : -1).ConfigureAwait(true);
            return true;
        }

        /// <summary>
        /// 图片导航到达章边界时的续章钩子（由 <c>ImageNavigationCoordinator</c> 在
        /// "不能再往前/往后翻"时调用）。
        /// <para>返回 true = 已接管（换了章包并显示）；false = 让宿主走默认行为
        /// （例如提示"已是最后一页"）。</para>
        /// </summary>
        internal async Task<bool> NavigateMangaBeyondEdgeAsync(bool forward)
        {
            var session = _mangaSession;
            if (session is null)
            {
                return false;
            }

            var target = _mangaChapterIndex + (forward ? 1 : -1);
            if (target < 0 || target >= session.Chapters.Count)
            {
                return false;
            }

            return await ShowMangaChapterAsync(target, forward ? 0 : -1).ConfigureAwait(true);
        }

        /// <summary>
        /// 把当前可见页写进漫画清单（图片导航每次翻页后由宿主调用）。
        /// </summary>
        internal void SaveMangaPosition()
        {
            var session = _mangaSession;
            if (session is null || _mangaChapterIndex < 0)
            {
                // 章未就位（正在打开/正在换包）时不写：写 -1 会覆盖真实进度成"第 1 章第 0 页"
                return;
            }

            session.SavePosition(_mangaChapterIndex, Math.Max(0, _currentIndex));
        }

        // ============================================================
        // 目录
        // ============================================================

        /// <summary>装漫画目录（与书源文本目录同构，只是 Tag 换成漫画章标记）。</summary>
        private void InstallMangaToc(IReadOnlyList<MangaChapterEntry> chapters)
        {
            _mangaTocItems = chapters
                .Select(c => new Uviewer.Models.TocItem
                {
                    HeadingText = c.Name,
                    HeadingLevel = c.IsVolume ? 0 : 1,
                    SourceLineNumber = c.Index,
                    Tag = c.IsVolume ? LegadoVolumeTag : MangaChapterTag,
                })
                .ToList();

            // 顶栏目录下拉（复用 PDF 目录按钮位：漫画与 PDF 不可能同时打开）
            MainToolbar.SetPdfTocTitle($"{MangaBookName} 目录");
            MainToolbar.SetPdfTocItems(_mangaTocItems);
            MainToolbar.SetPdfTocVisible(true);
        }

        /// <summary>打开顶栏漫画目录下拉。</summary>
        internal void ShowMangaToc()
        {
            if (_mangaTocItems is null)
            {
                return;
            }

            MainToolbar.SetPdfTocTitle($"{MangaBookName} 目录");
            MainToolbar.SetPdfTocItems(_mangaTocItems);
            MainToolbar.SetPdfTocVisible(true);
            MainToolbar.ShowPdfTocFlyout();
            SyncMangaTocSelection(_mangaChapterIndex);
        }

        /// <summary>目录点击：跳到该章（入参是目录下拉里被点中的条目，非 TocItem 时返回 false）。</summary>
        internal bool TryHandleMangaTocClick(object? clickedItem)
        {
            if (clickedItem is not Uviewer.Models.TocItem item ||
                item.Tag?.ToString() != MangaChapterTag ||
                _mangaSession is null)
            {
                return false;
            }

            _ = ShowMangaChapterAsync(item.SourceLineNumber, 0);
            MainToolbar.HidePdfTocFlyout();
            return true;
        }

        private void SyncMangaTocSelection(int chapterIndex)
        {
            if (_mangaTocItems is not null && chapterIndex >= 0 && chapterIndex < _mangaTocItems.Count)
            {
                MainToolbar.ScrollPdfTocIntoView(_mangaTocItems[chapterIndex]);
            }
        }

        /// <summary>目录条目 Tag：漫画章（可点击跳章）。</summary>
        internal const string MangaChapterTag = "MangaChapter";

        // ============================================================
        // UI 状态
        // ============================================================

        /// <summary>
        /// 图片模式下重新点亮漫画需要的 UI：目录可点、↑↓ 语义为"上一章/下一章"。
        /// <para><c>SwitchToImageMode</c> 是通用图片路径，会把阅读模式相关状态全部关掉；
        /// 漫画虽然显示的是图片，但仍然"有目录、有章节"——所以显示完这一章必须补回来。</para>
        /// </summary>
        private void ApplyMangaReadingUiState()
        {
            MainToolbar.SetReadingModeActive(true);
            MainToolbar.SetFileNavigationMode(true);
            MainToolbar.SetPdfTocVisible(true);
            Title = string.IsNullOrEmpty(MangaBookName) ? Strings.WindowTitle : $"{MangaBookName} - Uviewer";

            // 图片工具条里的"当前目录"提示跟随（与 PDF 同一位置）
            MainToolbar.SetPdfTocTitle($"{MangaBookName} 目录");
        }

        // ============================================================
        // 关闭
        // ============================================================

        /// <summary>关掉当前漫画（保存位置、释放会话、收起目录按钮）。</summary>
        internal async Task CloseMangaAsync()
        {
            ++_mangaGeneration;

            // 后台「下载全本」先取消：它持有同一份清单与章包句柄，必须比会话先停
            var fullDownload = _mangaFullDownloadCts;
            _mangaFullDownloadCts = null;
            if (fullDownload is not null)
            {
                try
                {
                    fullDownload.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 已自行结束并释放，忽略
                }
            }

            var session = _mangaSession;
            _mangaSession = null;
            _mangaChapterIndex = -1;
            _mangaTocItems = null;

            if (session is not null)
            {
                var (chapter, page) = session.Position;
                session.SavePosition(chapter, page);
                session.Dispose();
            }

            MainToolbar.SetPdfTocVisible(false);
            await Task.CompletedTask;
        }

        // ============================================================
        // 下载全本 / 在线更新（二级菜单）
        // ============================================================

        /// <summary>
        /// 「下载全本（漫画）」：把清单里全部尚未就绪的章补成 CBZ 章包。
        /// <para><b>为什么改成后台</b>（2026-10-23 用户要求）：一章几十 MB，整本几百 MB，
        /// 逐章 await 虽然不冻结 UI，但"下载中"的通知刷屏、且用户无法在下载期间做别的事
        /// （再点菜单会被当成重复请求）。现在的口径：</para>
        /// <list type="bullet">
        /// <item>立即返回，下载在后台跑；主窗口可继续阅读/翻页/换书；</item>
        /// <item>进度通知<b>按章节流</b>（同一章只报"开始/结束"两点，页级进度丢掉）——
        /// 否则一章 40 页就是 40 次通知，整本几千次，通知栏会变成弹幕；</item>
        /// <item>下载中再点一次菜单 = <b>取消</b>；关书（<see cref="CloseMangaAsync"/>）也会取消；</item>
        /// <item>结束后<b>不</b>强制重显当前章：那样会打断正在看的页面（换包 = 重开归档）。</item>
        /// </list>
        /// </summary>
        internal Task DownloadMangaAsync()
        {
            var session = _mangaSession;
            if (session is null)
            {
                ShowNotification("请先打开一本漫画，再下载全本", "\uE783", "Red");
                return Task.CompletedTask;
            }

            if (session.IsOffline)
            {
                ShowNotification("当前是离线打开的漫画，无法重新下载", "\uE783", "Red");
                return Task.CompletedTask;
            }

            // 再点一次 = 取消在途的全本下载
            if (_mangaFullDownloadCts is { } running)
            {
                running.Cancel();
                ShowNotification("正在取消「下载全本」…", "\uE895", "Gray");
                return Task.CompletedTask;
            }

            var cts = new CancellationTokenSource();
            _mangaFullDownloadCts = cts;
            _ = RunFullDownloadAsync(session, cts);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 后台执行全本下载（<b>不</b>在 UI 线程等）：进度按章节流，结束时统一清理取消源。
        /// <para>会话可能在下载途中被关掉（<see cref="CloseMangaAsync"/> 会先取消本任务），
        /// 因此每章开始前都检查取消，且异常一律收敛成通知——后台任务抛异常会静默丢失。</para>
        /// </summary>
        private async Task RunFullDownloadAsync(MangaReadingSession session, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var total = session.Chapters.Count(c => !c.IsVolume);
            ShowNotification($"已开始下载全本（共 {total} 章，可再次点本菜单取消）", "\uE896", "Gray");

            var lastChapter = string.Empty;
            var progress = new Progress<MangaDownloadService.MangaDownloadProgress>(p =>
            {
                // 章级节流：同一章只在第一次回调时报一次，避免每页一条通知
                if (string.Equals(lastChapter, p.ChapterName, StringComparison.Ordinal))
                {
                    return;
                }

                lastChapter = p.ChapterName;
                ShowNotification(
                    $"下载全本 {p.ChaptersDone}/{p.ChaptersTotal} 章：{p.ChapterName}",
                    "\uE896", "Gray");
            });

            var downloads = new MangaDownloadService(BookSources);
            MangaDownloadService.MangaDownloadResult result;
            try
            {
                result = await downloads
                    .DownloadAllAsync(session, progress, token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                result = new MangaDownloadService.MangaDownloadResult(false, true, string.Empty, "「下载全本」已取消");
            }
            catch (Exception ex)
            {
                result = new MangaDownloadService.MangaDownloadResult(false, false, string.Empty, $"「下载全本」失败：{ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_mangaFullDownloadCts, cts))
                {
                    _mangaFullDownloadCts = null;
                }

                cts.Dispose();
            }

            ShowNotification(
                result.Message,
                result.Success ? "\uE73E" : "\uE783",
                result.Success ? "Green" : "Red");
        }

        /// <summary>
        /// 「在线更新（漫画）」：按清单里的书源识别信息重取目录，补齐新增章的图片（前 5 章）。
        /// </summary>
        internal async Task UpdateMangaAsync()
        {
            var path = MangaManifestPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ShowNotification("请先打开一本已下载的漫画（.uvmanga）再在线更新", "\uE783", "Red");
                return;
            }

            var manifest = MangaLibrary.ReadManifest(path);
            if (manifest is null)
            {
                ShowNotification("漫画清单损坏，无法在线更新", "\uE783", "Red");
                return;
            }

            ShowNotification("正在检查远程漫画目录…", "\uE8F1", "Gray");

            var downloads = new MangaDownloadService(BookSources);
            var progress = new Progress<MangaDownloadService.MangaDownloadProgress>(p =>
                ShowNotification(
                    $"更新第 {p.ChaptersDone}/{p.ChaptersTotal} 章：{p.ChapterName}（{p.PagesDone}/{p.PagesTotal} 页）",
                    "\uE896", "Gray"));

            var result = await downloads
                .UpdateAsync(path, MangaLibrary.InitialChapterImageCount, progress)
                .ConfigureAwait(true);

            ShowNotification(
                result.Message,
                result.Success ? "\uE73E" : "\uE783",
                result.Success ? "Green" : "Red");

            if (result.Success && _mangaSession is not null)
            {
                // 目录可能新增了章：重建下拉
                InstallMangaToc(_mangaSession.Chapters);
            }
        }
    }
}

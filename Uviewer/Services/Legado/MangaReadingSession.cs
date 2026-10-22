using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.Models;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using CoreTocItem = Uviewer.Services.Legado.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 漫画阅读会话：把"一本图片书源漫画"的生命周期收敛到一个对象里。
    ///
    /// <para><b>与 <see cref="BookSourceReadingSession"/> 的分工</b>：那个会话把章节正文
    /// 转成纯文本喂给文本渲染管线；本会话把章节里的图片<b>边下边打成 CBZ</b>，
    /// 再把章包路径交给宿主的归档/图片查看链路。两者共用同一个
    /// <see cref="BookSourceService"/>（同一套书源规则、Cookie、限速与 JS 状态）。</para>
    ///
    /// <para><b>按需下载（本类存在的核心理由）</b>：漫画单页常 300KB~2MB，一本几百章
    /// 直接整本抓 = 一次几百 MB 请求量，几乎必然触发源站风控。所以：</para>
    /// <list type="bullet">
    /// <item>首次打开只抓 <see cref="MangaLibrary.InitialChapterImageCount"/> 章；</item>
    /// <item>切换章节 / 目录跳转时按"前二后五"窗口补齐（<see cref="MangaLibrary.BuildWindow"/>，
    /// 目标章排第一位——用户先看到自己点的那章）；</item>
    /// <item>已完成的章直接读盘（只 stat 一个 <c>.cbz</c>），零网络；
    /// 半包（上次中断）只补缺页。</item>
    /// </list>
    ///
    /// <para><b>风控保护</b>：图片请求在引擎侧<b>不过</b>书源限速闸门（见阶段 4-3 的口径），
    /// 因此本类自己做三件事：① 同一本书的章节下载串行（<see cref="_chapterGate"/>）；
    /// ② 章内图片并发只有 <see cref="ImageFetchConcurrency"/>；③ 每张之间固定间隔
    /// <see cref="ImageIntervalMs"/>。宁可慢一点，也不要让"读到第 50 章就被封"。</para>
    ///
    /// <para><b>线程</b>：非线程安全对象；宿主只在 UI 线程驱动。唯一的后台路径是
    /// <see cref="PrefetchWindowAsync"/> 派生的任务，它只写磁盘与清单（清单写有闸）。</para>
    /// </summary>
    public sealed class MangaReadingSession : IDisposable
    {
        /// <summary>章内图片下载并发（同一站点，刻意压到 3）。</summary>
        private const int ImageFetchConcurrency = 3;

        /// <summary>同一章内相邻两张图之间的最小间隔（毫秒）：给源站留出喘息，降低风控概率。</summary>
        private const int ImageIntervalMs = 120;

        /// <summary>单张图片字节上限（超过即判失败）：防止误抓视频/压缩包把磁盘写满。</summary>
        private const long MaxImageBytes = 32L * 1024 * 1024;

        private readonly BookSourceService? _service;
        private readonly BookSourceModel? _source;
        private readonly string _bookFolder;

        /// <summary>书级清单（目录 / 书源识别 / 阅读位置的唯一权威副本）。</summary>
        public MangaManifest Manifest { get; }

        /// <summary>清单 / 位置 / 章节状态的总闸（保证一次只写一份一致快照）。</summary>
        private readonly object _gate = new();

        /// <summary>章节下载串行闸（同一本书同一时刻只下载一章的图片）。</summary>
        private readonly SemaphoreSlim _chapterGate = new(1, 1);

        private CancellationTokenSource? _prefetchCts;
        private int _savedChapter = -1;
        private int _savedPage = -1;

        /// <summary>
        /// 打开一本<b>在线</b>漫画（书源 + 书籍 URL）。清单存在则续用（保留已下载章与阅读位置）。
        /// </summary>
        public MangaReadingSession(
            BookSourceService service, BookSourceModel source, string bookUrl, string bookRootDirectory)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _source = source ?? throw new ArgumentNullException(nameof(source));

            var folder = MangaLibrary.BuildBookFolder(
                bookRootDirectory,
                string.IsNullOrWhiteSpace(source.BookSourceName) ? "漫画" : source.BookSourceName,
                bookUrl);

            _bookFolder = folder;
            Manifest = MangaLibrary.ReadManifest(MangaLibrary.ManifestPathOf(folder)) ?? new MangaManifest
            {
                Uuid = Guid.NewGuid().ToString("N"),
                SourceUrl = source.BookSourceUrl ?? string.Empty,
                SourceName = source.BookSourceName ?? string.Empty,
                BookUrl = bookUrl ?? string.Empty,
                CreatedAt = MangaLibrary.NowStamp(),
                UpdatedAt = MangaLibrary.NowStamp(),
            };

            // 书名以清单为准（历史书名不被"当前页标题"覆盖，免得文件夹名与显示名对不上）
            if (string.IsNullOrWhiteSpace(Manifest.Title))
            {
                Manifest.Title = NameFromUrl(bookUrl);
            }

            _savedChapter = Manifest.Position.ChapterIndex;
            _savedPage = Manifest.Position.PageIndex;
        }

        /// <summary>
        /// 打开一本<b>已下载</b>的漫画（只读清单，不联网）。清单损坏/不存在时抛
        /// <see cref="InvalidDataException"/>——离线打开没有"重建"的退路，必须让上层报错。
        /// </summary>
        public static MangaReadingSession OpenLocal(string manifestPath, MangaManifest? preloaded = null)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new ArgumentException("清单路径为空", nameof(manifestPath));
            }

            var manifest = preloaded ?? MangaLibrary.ReadManifest(manifestPath)
                ?? throw new InvalidDataException("漫画清单无法解析（不是有效的 .uvmanga 文件）");

            var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty;
            return new MangaReadingSession(folder, manifest);
        }

        /// <summary>离线实例的私有构造（无书源、无网络）。</summary>
        private MangaReadingSession(string bookFolder, MangaManifest manifest)
        {
            _bookFolder = bookFolder;
            Manifest = manifest;
            _savedChapter = manifest.Position.ChapterIndex;
            _savedPage = manifest.Position.PageIndex;
        }

        /// <summary>
        /// 打开一本<b>已存在</b>的漫画（在线更新用）：清单路径优先于"按书名重算文件夹"。
        /// <para>必须走这个入口而不是构造函数：用户可能改过文件夹名/移动过目录，
        /// 按书名重算会落到另一个空文件夹，"在线更新"看起来成功却什么都没更新。</para>
        /// </summary>
        public static MangaReadingSession OpenExisting(
            BookSourceService service, BookSourceModel source, string manifestPath)
        {
            var manifest = MangaLibrary.ReadManifest(manifestPath)
                ?? throw new InvalidDataException("漫画清单无法解析（不是有效的 .uvmanga 文件）");

            var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty;
            if (string.IsNullOrEmpty(folder))
            {
                throw new InvalidDataException("清单所在目录无法解析");
            }

            var session = new MangaReadingSession(service, source, folder, manifest);
            return session;
        }

        /// <summary>在线实例的私有构造（已有清单 + 指定书根目录）。</summary>
        private MangaReadingSession(BookSourceService service, BookSourceModel source, string bookFolder, MangaManifest manifest)
        {
            _service = service;
            _source = source;
            _bookFolder = bookFolder;
            Manifest = manifest;
            _savedChapter = manifest.Position.ChapterIndex;
            _savedPage = manifest.Position.PageIndex;
        }

        /// <summary>书根目录（漫画文件夹）。</summary>
        public string BookFolder => _bookFolder;

        /// <summary>书级清单路径（<c>book.uvmanga</c>）。</summary>
        public string ManifestPath => MangaLibrary.ManifestPathOf(_bookFolder);

        /// <summary>离线实例（无书源，不联网）。</summary>
        public bool IsOffline => _service is null;

        /// <summary>书名。</summary>
        public string BookName => Manifest.Title;

        /// <summary>书源（离线实例为 null）。</summary>
        public BookSourceModel? Source => _source;

        /// <summary>目录快照（拷贝，避免调用方枚举时被后台合并改到）。</summary>
        public IReadOnlyList<MangaChapterEntry> Chapters
        {
            get
            {
                lock (_gate)
                {
                    return Manifest.Chapters.ToArray();
                }
            }
        }

        /// <summary>阅读位置（章下标，页下标）。</summary>
        public (int ChapterIndex, int PageIndex) Position
        {
            get
            {
                lock (_gate)
                {
                    return (Manifest.Position.ChapterIndex, Manifest.Position.PageIndex);
                }
            }
        }

        // ============================================================
        // 打开 / 目录
        // ============================================================

        /// <summary>
        /// 在线打开：取详情 → 拉目录 → 合并进清单并落盘。
        /// <para>合并口径（<see cref="MergeToc"/>）：按章节 URL 复用旧的章包名与下载状态，
        /// 于是"源站新增一章"只增一条、"改名"只改显示名，已下好的章包不会因重新打开而丢。</para>
        /// </summary>
        public async Task<OpenResult> OpenOnlineAsync(CancellationToken token = default)
        {
            if (_service is null || _source is null)
            {
                throw new InvalidOperationException("离线会话不能刷新目录");
            }

            var info = await _service.Engine
                .GetBookInfoAsync(_source, Manifest.BookUrl, null, token)
                .ConfigureAwait(false);

            var title = FirstNonEmpty(info?.Name, Manifest.Title, NameFromUrl(Manifest.BookUrl));
            var cover = FirstNonEmpty(info?.CoverUrl, Manifest.CoverUrl);

            var tocUrl = FirstNonEmpty(info?.TocUrl, Manifest.BookUrl);
            var book = JsBookState.FromBookInfo(info, Manifest.BookUrl, title);
            var toc = await _service.Engine
                .GetTocAsync(_source, tocUrl, token, book)
                .ConfigureAwait(false);

            int added;
            lock (_gate)
            {
                Manifest.Title = title;
                Manifest.CoverUrl = cover;
                Manifest.SourceUrl = _source.BookSourceUrl ?? Manifest.SourceUrl;
                Manifest.SourceName = _source.BookSourceName ?? Manifest.SourceName;
                Manifest.UpdatedAt = MangaLibrary.NowStamp();
                added = MergeToc(Manifest, toc);
                MangaLibrary.WriteManifest(ManifestPath, Manifest);
            }

            var position = Position;
            return new OpenResult(
                Manifest.Chapters.ToArray(),
                ClampChapter(position.ChapterIndex),
                position.PageIndex,
                added);
        }

        /// <summary>
        /// 把一份新目录合并进清单，返回"新增章数"。
        /// <para>纯函数（只改传入的 manifest），便于测试与离线复用。</para>
        /// <list type="number">
        /// <item>按章节 URL 建旧条目索引（URL 是源站唯一稳定标识，章名会变）；</item>
        /// <item>新目录逐条：命中则沿用 <c>Archive</c>（已下载章包）与下载状态；</item>
        /// <item>未命中则分配新章包名；已存在的同名旧包也不复用（不同章的图片不能混）。</item>
        /// </list>
        /// </summary>
        public static int MergeToc(MangaManifest manifest, IReadOnlyList<CoreTocItem> toc)
        {
            if (manifest is null || toc is null)
            {
                return 0;
            }

            var previous = new Dictionary<string, MangaChapterEntry>(StringComparer.Ordinal);
            foreach (var entry in manifest.Chapters)
            {
                if (!string.IsNullOrWhiteSpace(entry.Url))
                {
                    previous[entry.Url] = entry;
                }
            }

            var merged = new List<MangaChapterEntry>(toc.Count);
            var added = 0;
            for (var i = 0; i < toc.Count; i++)
            {
                var item = toc[i];
                var url = item.ChapterUrl ?? string.Empty;
                var name = System.Net.WebUtility.HtmlDecode(item.ChapterName ?? string.Empty).Trim();

                if (previous.TryGetValue(url, out var old))
                {
                    merged.Add(new MangaChapterEntry
                    {
                        Index = i,
                        Name = name.Length > 0 ? name : old.Name,
                        Url = url,
                        Archive = old.Archive,
                        Downloaded = old.Downloaded,
                        Pages = old.Pages,
                        Complete = old.Complete,
                        IsVolume = item.IsVolume,
                    });
                    continue;
                }

                added++;
                merged.Add(new MangaChapterEntry
                {
                    Index = i,
                    Name = name.Length > 0 ? name : $"第 {i + 1} 章",
                    Url = url,
                    Archive = MangaLibrary.BuildChapterArchiveName(i, name),
                    Downloaded = 0,
                    Pages = 0,
                    Complete = false,
                    IsVolume = item.IsVolume,
                });
            }

            manifest.Chapters = merged;

            // 位置下标沿用旧值但按范围钳制：目录缩短时不能指向不存在的章
            var position = manifest.Position ??= new MangaPosition();
            if (position.ChapterIndex >= merged.Count)
            {
                position.ChapterIndex = Math.Max(0, merged.Count - 1);
                position.PageIndex = 0;
            }

            return added;
        }

        // ============================================================
        // 章节章包
        // ============================================================

        /// <summary>
        /// 确保第 <paramref name="index"/> 章的章包（<c>.cbz</c>）可用，返回章包路径与页条目名。
        /// <para>快路径（零网络）：<c>.cbz</c> 已存在且页数达标 → 直接列条目返回。</para>
        /// <para>慢路径：抓章节页 → 取图片 URL 列表 → 只下载尚缺的页 → 边下边写进
        /// <c>.cbz.part</c> → 全部到位才改名成 <c>.cbz</c>。</para>
        /// <para>离线会话只读盘：没有的章包不会去"补"，返回空结果（上层提示）。</para>
        /// </summary>
        public async Task<ChapterArchiveResult> EnsureChapterAsync(
            int index,
            IProgress<MangaImageProgress>? progress = null,
            CancellationToken token = default,
            bool cancelPrefetch = true)
        {
            if (cancelPrefetch)
            {
                // 用户显式要的这一章优先：立即作废后台预取（在途那章保留半包，下次只补缺）
                CancelPrefetch();
            }

            MangaChapterEntry? entry;
            lock (_gate)
            {
                entry = index >= 0 && index < Manifest.Chapters.Count ? Manifest.Chapters[index] : null;
            }

            if (entry is null || entry.IsVolume)
            {
                return ChapterArchiveResult.Empty;
            }

            var archivePath = ArchivePath(entry);
            var existingPages = MangaLibrary.ListArchivePages(archivePath);
            if (existingPages.Count > 0 && entry.Complete)
            {
                return new ChapterArchiveResult(archivePath, existingPages, true, 0);
            }

            if (IsOffline)
            {
                return existingPages.Count == 0
                    ? ChapterArchiveResult.Empty
                    : new ChapterArchiveResult(archivePath, existingPages, false, 0);
            }

            await _chapterGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // 双检：等闸期间可能已被预取任务下完
                existingPages = MangaLibrary.ListArchivePages(archivePath);
                var entryNow = Chapters.FirstOrDefault(c => c.Index == index) ?? entry;
                if (existingPages.Count > 0 && entryNow.Complete)
                {
                    return new ChapterArchiveResult(archivePath, existingPages, true, 0);
                }

                return await DownloadChapterAsync(entryNow, archivePath, progress, token).ConfigureAwait(false);
            }
            finally
            {
                _chapterGate.Release();
            }
        }

        /// <summary>
        /// 后台预取窗口：以 <paramref name="center"/> 为中心按"前二后五"补齐，
        /// 目标章排第一（<see cref="MangaLibrary.BuildWindow"/>）。
        /// <para>失败/取消一律静默——预取只是"让下一章打开更快"，不是功能路径。</para>
        /// </summary>
        public void PrefetchWindowAsync(int center)
        {
            if (IsOffline)
            {
                return;
            }

            var chapters = Chapters;
            var plan = MangaLibrary.BuildWindow(center, chapters.Count)
                .Where(i => i >= 0 && i < chapters.Count && !string.IsNullOrWhiteSpace(chapters[i].Url))
                .ToArray();
            if (plan.Length == 0)
            {
                return;
            }

            CancelPrefetch();
            var cts = new CancellationTokenSource();
            _prefetchCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var chapterIndex in plan)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        await EnsureChapterAsync(chapterIndex, null, cts.Token, cancelPrefetch: false)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Manga prefetch failed: {ex.Message}");
                }
            }, cts.Token);
        }

        /// <summary>
        /// 下载单章并打成章包（调用方必须已持有 <see cref="_chapterGate"/>）。
        /// <para>章节页只抓一次：图片 URL 列表写进包内 <c>pages.json</c>，之后续下不再重复请求章节页。</para>
        /// </summary>
        private async Task<ChapterArchiveResult> DownloadChapterAsync(
            MangaChapterEntry entry,
            string archivePath,
            IProgress<MangaImageProgress>? progress,
            CancellationToken token)
        {
            var partialPath = archivePath + ".part";

            // 半包归位：已存在 .cbz（上次部分完成）→ 先当作续下源
            if (File.Exists(archivePath) && !File.Exists(partialPath))
            {
                TryMove(archivePath, partialPath);
            }

            // 包内已有的页清单（续下时复用 URL 映射，不重抓章节页）
            var config = File.Exists(partialPath) ? MangaLibrary.ReadChapterMeta(partialPath) : null;

            if (config is null || config.Images.Count == 0)
            {
                var content = await _service!.Engine.GetContentAsync(
                    _source!,
                    entry.Url,
                    Manifest.BookUrl,
                    token,
                    JsBookState.FromBookUrl(Manifest.BookUrl)
                        .WithChapter(entry.Index, entry.Name, Manifest.Chapters.Count))
                    .ConfigureAwait(false);

                var urls = MangaLibrary.NormalizeImageUrls(content?.ImageUrls);
                if (urls.Count == 0)
                {
                    UpdateChapterState(entry, 0, 0, complete: false);
                    return ChapterArchiveResult.Empty;
                }

                config = new MangaChapterImages
                {
                    Index = entry.Index,
                    Name = entry.Name,
                    Url = entry.Url,
                    FetchedAt = MangaLibrary.NowStamp(),
                    Images = urls.Select(u => new MangaImageRecord { Url = u }).ToList(),
                };
            }

            var total = config.Images.Count;
            var done = 0;
            var failed = 0;

            using var writer = new MangaLibrary.ChapterArchiveWriter(partialPath);

            // 已落盘的页（续下）：按文件名跳过
            for (var i = 0; i < total; i++)
            {
                var record = config.Images[i];
                if (!string.IsNullOrEmpty(record.File) && writer.ExistingPageNames.Contains(record.File))
                {
                    done++;
                    record.Bytes = Math.Max(record.Bytes, 1);
                }
                else
                {
                    record.File = string.Empty;
                    record.Bytes = 0;
                }
            }

            progress?.Report(new MangaImageProgress(entry.Index, entry.Name, done, total));

            var pending = Enumerable.Range(0, total)
                .Where(i => string.IsNullOrEmpty(config.Images[i].File))
                .ToList();

            if (pending.Count > 0)
            {
                // 写 zip 不是线程安全的：下载并行、落盘串行（锁内只做一次内存拷贝 + 条目写入）
                var writeGate = new object();
                using var throttle = new SemaphoreSlim(ImageFetchConcurrency, ImageFetchConcurrency);

                await Parallel.ForEachAsync(
                    pending,
                    new ParallelOptions { MaxDegreeOfParallelism = ImageFetchConcurrency, CancellationToken = token },
                    async (i, ct) =>
                    {
                        await throttle.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var record = config.Images[i];
                            var bytes = await _service!.Engine
                                .GetImageAsync(_source!, record.Url, ct)
                                .ConfigureAwait(false);

                            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
                            {
                                Interlocked.Increment(ref failed);
                                return;
                            }

                            var extension = MangaLibrary.SniffImageExtension(bytes, record.Url);
                            var fileName = MangaLibrary.PageFileName(i, extension);

                            lock (writeGate)
                            {
                                if (writer.TryAddPage(fileName, bytes))
                                {
                                    record.File = fileName;
                                    record.Bytes = bytes.Length;
                                }
                                else
                                {
                                    // 同名但内容不同（同一页被写成两种后缀）：直接用已存在的那条
                                    record.File = fileName;
                                    record.Bytes = Math.Max(record.Bytes, 1);
                                }
                            }

                            var completed = Interlocked.Increment(ref done);
                            progress?.Report(new MangaImageProgress(entry.Index, entry.Name, completed, total));
                        }
                        finally
                        {
                            throttle.Release();
                            if (ImageIntervalMs > 0)
                            {
                                try
                                {
                                    await Task.Delay(ImageIntervalMs, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                }
                            }
                        }
                    }).ConfigureAwait(false);
            }

            var complete = failed == 0 && done >= total;
            config.Complete = complete;

            // 元数据最后写：ComicInfo 给第三方阅读器，pages.json 给本程序续下/换源
            writer.PutText(MangaFormat.PagesEntryName, MangaLibrary.SerializeChapterMeta(config));
            writer.PutText(
                MangaFormat.ComicInfoEntryName,
                MangaLibrary.BuildComicInfoXml(
                    Manifest.Title,
                    entry.Name,
                    entry.Index + 1,
                    total,
                    Manifest.SourceName,
                    Manifest.SourceUrl,
                    Manifest.BookUrl,
                    Manifest.Uuid,
                    entry.Url));
            writer.Dispose();

            var pages = MangaLibrary.ListArchivePages(partialPath);
            if (pages.Count == 0)
            {
                // 一页都没成功：留 .part 供下次续下（不产生空 .cbz 让用户以为下好了）
                UpdateChapterState(entry, total, 0, complete: false);
                return ChapterArchiveResult.Empty;
            }

            // 全部到位 → 正式章包；部分失败保留半包名（下次 Ensure 会续补）
            var finalPath = complete ? archivePath : partialPath;
            if (complete)
            {
                TryMove(partialPath, archivePath);
            }

            UpdateChapterState(entry, total, pages.Count, complete);
            return new ChapterArchiveResult(finalPath, pages, complete, failed);
        }

        /// <summary>更新书级清单里某章的状态并落盘（章状态影响"要不要再抓"的判断）。</summary>
        private void UpdateChapterState(MangaChapterEntry entry, int pages, int downloaded, bool complete)
        {
            lock (_gate)
            {
                var target = Manifest.Chapters.FirstOrDefault(c => c.Index == entry.Index);
                if (target is not null)
                {
                    target.Pages = Math.Max(target.Pages, pages);
                    target.Downloaded = Math.Max(target.Downloaded, downloaded);
                    target.Complete = complete;
                    if (string.IsNullOrEmpty(target.Archive))
                    {
                        target.Archive = entry.Archive;
                    }
                }

                Manifest.UpdatedAt = MangaLibrary.NowStamp();
                try
                {
                    MangaLibrary.WriteManifest(ManifestPath, Manifest);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Manga manifest write failed: {ex.Message}");
                }
            }
        }

        // ============================================================
        // 位置
        // ============================================================

        /// <summary>
        /// 保存阅读位置（章 + 页）。相同位置重复调用不落盘（翻页回调会连续触发）。
        /// </summary>
        public void SavePosition(int chapterIndex, int pageIndex)
        {
            lock (_gate)
            {
                if (chapterIndex == _savedChapter && pageIndex == _savedPage)
                {
                    return;
                }

                Manifest.Position.ChapterIndex = Math.Max(0, chapterIndex);
                Manifest.Position.PageIndex = Math.Max(0, pageIndex);
                _savedChapter = Manifest.Position.ChapterIndex;
                _savedPage = Manifest.Position.PageIndex;

                try
                {
                    MangaLibrary.WriteManifest(ManifestPath, Manifest);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Manga position save failed: {ex.Message}");
                }
            }
        }

        /// <summary>把章下标钳制到有效范围（空目录返回 0）。</summary>
        public int ClampChapter(int index)
        {
            var count = Manifest.Chapters.Count;
            return count == 0 ? 0 : Math.Clamp(index, 0, count - 1);
        }

        /// <summary>章包绝对路径（<c>Archive</c> 为空时按章名下推一个并回写）。</summary>
        public string ArchivePath(MangaChapterEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Archive))
            {
                entry.Archive = MangaLibrary.BuildChapterArchiveName(entry.Index, entry.Name);
            }

            return Path.Combine(_bookFolder, entry.Archive);
        }

        /// <summary>某章已落盘的页条目（不联网、不等闸；供离线快速取用）。</summary>
        public IReadOnlyList<string> GetChapterPages(int index)
        {
            MangaChapterEntry? entry;
            lock (_gate)
            {
                entry = index >= 0 && index < Manifest.Chapters.Count ? Manifest.Chapters[index] : null;
            }

            return entry is null
                ? Array.Empty<string>()
                : MangaLibrary.ListArchivePages(ArchivePath(entry));
        }

        /// <summary>取消在途预取。</summary>
        public void CancelPrefetch()
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
            }
        }

        public void Dispose()
        {
            // 刻意不 Dispose 章节闸：预取任务可能正卡在 WaitAsync，
            // 释放信号量会让它抛 ObjectDisposedException（不是"取消"，无法被吞）。
            CancelPrefetch();
        }

        private static void TryMove(string from, string to)
        {
            try
            {
                File.Move(from, to, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga archive move failed: {ex.Message}");
            }
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!;
                }
            }

            return string.Empty;
        }

        /// <summary>书名兜底：书籍 URL 末段（去扩展名/查询串）。</summary>
        private static string NameFromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "漫画";
            }

            try
            {
                var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url!;
                var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                var dot = last.LastIndexOf('.');
                if (dot > 0)
                {
                    last = last[..dot];
                }

                var name = Uri.UnescapeDataString(last);
                return string.IsNullOrWhiteSpace(name) ? "漫画" : name;
            }
            catch
            {
                return "漫画";
            }
        }

        /// <summary>打开结果：目录 + 起始位置 + 本次新增章数。</summary>
        public sealed record OpenResult(
            IReadOnlyList<MangaChapterEntry> Chapters,
            int ChapterIndex,
            int PageIndex,
            int AddedChapters);

        /// <summary>章包结果：路径 + 页条目名（按页序）+ 是否齐全 + 失败页数。</summary>
        public sealed record ChapterArchiveResult(
            string ArchivePath,
            IReadOnlyList<string> PageNames,
            bool Complete,
            int FailedPages)
        {
            /// <summary>空结果（无章/无图/离线未下载）。</summary>
            public static ChapterArchiveResult Empty { get; } =
                new(string.Empty, Array.Empty<string>(), false, 0);

            /// <summary>是否可用（至少有一页）。</summary>
            public bool HasPages => PageNames.Count > 0;
        }

        /// <summary>单章下载进度（工作线程回调；宿主自行切回 UI 线程）。</summary>
        public sealed record MangaImageProgress(
            int ChapterIndex,
            string ChapterName,
            int Done,
            int Total);
    }
}

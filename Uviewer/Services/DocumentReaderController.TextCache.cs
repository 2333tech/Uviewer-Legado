using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;
using Uviewer.Services;
using Uviewer.Services.Legado;
using Windows.Storage;

namespace Uviewer
{
    /// <summary>
    /// 本地文本的<b>章节缓存 / 窗口阅读模式</b>（菜单项「章节缓存」开关）。
    ///
    /// <para><b>要解决的事</b>：重净化规则集下整本净化一本大 TXT 要 10s 量级
    /// （PCRE 回溯 + <c>@js:</c> 求值），每次打开都重复付这笔钱。开启缓存后：</para>
    /// <list type="number">
    /// <item>保存缓存（首次点选菜单）：读原文 → 按标题规则切出<b>完整目录 + 章节分化</b>
    /// → 全量净化一遍校验章数（净化后名与原文名数量一致时采用净化后名）→ 落盘；</item>
    /// <item>之后每次打开：读原文（快）→ 从缓存取目录 → 只净化阅读位置前后
    /// <see cref="TextCacheFormat.WindowBefore"/> + <see cref="TextCacheFormat.WindowAfter"/> 章
    /// → 装配成显示文本。无缓存位置时从第一章开始向后数章；</item>
    /// <item>切换章节（目录点击 / 滚动越过窗口）：以新章为中心重开窗口，
    /// 前后章同步净化（与书源阅读的预渲染窗口同规格）。</item>
    /// </list>
    ///
    /// <para><b>可逆性</b>：缓存随时可删（再点一次菜单项）；删掉后重新打开文件即回到整本路径。
    /// 文件大小/写入时间/替换规则指纹/标题规则指纹任一变化，缓存自动失效——不会端上过期的净化结果。
    /// 总开关关闭时窗口模式照常工作（只是逐章净化变成恒等，显示原文）。</para>
    /// </summary>
    internal sealed partial class DocumentReaderController
    {
        private TextCacheStore? _textCacheStore;
        private TextCacheEntry? _textCacheEntry;

        /// <summary>规范化后的原文（窗口切片的基底；仅窗口模式持有）。</summary>
        private string? _textCacheNormalizedRaw;

        /// <summary>当前源的缓存键（与 <see cref="_textCacheEntry"/> 同生命周期）。</summary>
        private string? _textCacheSourceKey;

        private int _textCacheWindowFrom;
        private int _textCacheWindowTo;   // 开区间
        private int _textCacheCenter = -1;

        /// <summary>每章标题在装配文本里的 1-based 行号（卷 = -1）；null = 非窗口模式。</summary>
        private int[]? _textCacheHeadingLines;

        /// <summary>滚动跟踪：上次检查时的当前章（避免每像素滚动都重算）。</summary>
        private int _textCacheLastScrollChapter = -1;

        /// <summary>窗口切换代次：在途的装配完成后若代数已变则丢弃，防慢响应覆盖新窗口。</summary>
        private int _textCacheSwitchGeneration;

        /// <summary>窗口切换进行中（滚动驱动的重开要等它做完）。</summary>
        private bool _textCacheSwitching;

        /// <summary>
        /// 本会话内装配失败过的源键：失败一次即回退整本路径，且不再重试缓存
        ///（否则"失败 → 回退 → 又重新命中缓存 → 再失败"会成死循环）。
        /// 不删用户的缓存文件——重启应用后仍会再给缓存一次机会。
        /// </summary>
        private readonly HashSet<string> _textCacheBrokenKeys = new(StringComparer.Ordinal);

        private TextCacheStore TextCacheStore => _textCacheStore ??= new TextCacheStore();

        /// <summary>是否正处于章节窗口模式（缓存命中且会话存活）。</summary>
        internal bool TextCacheActive => _textCacheEntry is not null && _textCacheHeadingLines is not null;

        // ============================================================
        // 打开路径接线
        // ============================================================

        /// <summary>
        /// 尝试用章节缓存打开当前文本。在读原文之后、整本净化之前调用。
        /// <returns>true = 已按窗口模式打开（调用方只需补 reveal/侧栏等收尾）；false = 无有效缓存，走整本路径。</returns>
        /// </summary>
        internal async Task<bool> TryOpenWithTextCacheAsync(
            string rawContent, string displayName, string? uniquePath, CancellationToken token)
        {
            ClearTextCacheWindowState();

            var key = TextCacheLogic.ComputeSourceKey(
                _currentTextFilePath, _archiveSession.CurrentPath, _currentTextArchiveEntryKey);
            if (string.IsNullOrEmpty(key) || _textCacheBrokenKeys.Contains(key))
            {
                return false;
            }

            var service = BookSources;
            await service.EnsureInitializedAsync(token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return false;
            }

            var entry = TextCacheStore.Load(key);
            if (entry is null || !IsTextCacheUsable(entry, key))
            {
                return false;
            }

            _textCacheEntry = entry;
            _textCacheSourceKey = key;
            _textCacheNormalizedRaw = TextCacheLogic.NormalizeNewlines(rawContent ?? string.Empty);

            // 起始章：缓存的上次阅读章名 → 找不到（或没有）就从第一章开始
            var center = TextCacheLogic.FindChapterIndex(entry.Chapters, entry.LastChapterName);
            if (center < 0)
            {
                center = 0;
            }

            await ShowTextCacheWindowAsync(center, entry.LastLineInChapter, token).ConfigureAwait(true);
            return true;
        }

        /// <summary>
        /// 以 <paramref name="center"/> 章为中心重开窗口（切换章节的同步入口）。
        /// 前后章同步净化后装配成新的显示文本。
        /// </summary>
        internal async Task ShowTextCacheWindowAsync(int center, int lineInChapter = 0, CancellationToken token = default)
        {
            var entry = _textCacheEntry;
            var normalized = _textCacheNormalizedRaw;
            if (entry is null || normalized is null || entry.Chapters.Count == 0)
            {
                return;
            }

            var generation = ++_textCacheSwitchGeneration;
            _textCacheSwitching = true;

            var (from, to) = TextCacheLogic.ComputeWindow(
                entry.Chapters.Count, center, TextCacheFormat.WindowBefore, TextCacheFormat.WindowAfter);

            var service = BookSources;
            var purifyEnabled = service.IsInitialized && service.PurifyEnabled;

            WindowAssembly assembly;
            try
            {
                // 逐章切片 + 净化 + 装配放后台：8 章 × 几十毫秒，UI 线程不阻塞
                assembly = await Task.Run(() =>
                {
                    var purify = new Func<string, string>(slice =>
                        purifyEnabled && service.IsInitialized ? service.PurifyForDisplay(slice) : slice);
                    return TextCacheLogic.AssembleWindow(normalized, entry.Chapters, from, to, purify);
                }, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 用户已经切走了：安静退出，别把取消渲染成"加载失败"
                _textCacheSwitching = false;
                return;
            }
            catch (Exception ex)
            {
                // 装配/净化异常：本会话内拉黑该源并回退整本路径（缓存路径不该把阅读卡死，
                // 但也不能"回退后又命中同一缓存再失败"成死循环）
                _textCacheSwitching = false;
                if (!string.IsNullOrEmpty(_textCacheSourceKey))
                {
                    _textCacheBrokenKeys.Add(_textCacheSourceKey);
                }

                ClearTextCacheWindowState();
                System.Diagnostics.Debug.WriteLine($"Text cache window failed: {ex.Message}");
                ShowNotification($"章节窗口加载失败，已回退整本加载：{ex.Message}", "\uE783", "Orange");
                _ = ReloadCurrentTextAsync();
                return;
            }

            if (token.IsCancellationRequested || generation != _textCacheSwitchGeneration)
            {
                return;
            }

            _textCacheWindowFrom = from;
            _textCacheWindowTo = to;
            _textCacheCenter = Math.Clamp(center, 0, entry.Chapters.Count - 1);
            _textCacheHeadingLines = assembly.HeadingLines;

            // 目标行：该章标题行 + 章内偏移（无位置记录时 = 标题行，即从本章开头读）
            var k = _textCacheCenter - from;
            var headingLine = k >= 0 && k < assembly.HeadingLines.Length ? assembly.HeadingLines[k] : 1;
            var targetLine = headingLine > 0
                ? headingLine + Math.Max(0, lineInChapter)
                : 1;

            _aozoraPendingTargetLine = targetLine > 1 ? targetLine : 1;

            var displayName = !string.IsNullOrEmpty(_currentTextFilePath)
                ? Path.GetFileName(_currentTextFilePath)
                : FileNameText.Text;
            var uniquePath = _currentTextFilePath ?? _currentTextArchiveEntryKey;

            await DisplayLoadedText(assembly.Text, displayName, uniquePath, token).ConfigureAwait(true);

            if (token.IsCancellationRequested || generation != _textCacheSwitchGeneration)
            {
                return;
            }

            _tocService.SetProvider(new TextCacheTocProvider(BuildTextCacheTocItems()));
            _ = _tocService.LoadTocAsync(token);

            _textCacheLastScrollChapter = _textCacheCenter;
            _textCacheSwitching = false;

            SaveTextCachePosition(_textCacheCenter, Math.Max(0, lineInChapter));
            UpdateTextCacheMenuState();
        }

        /// <summary>装配完整目录（全部章；窗口内的带装配行号，窗口外 -1 由点击分支重开窗口）。</summary>
        private List<TocItem> BuildTextCacheTocItems()
        {
            var entry = _textCacheEntry!;
            var items = new List<TocItem>(entry.Chapters.Count);
            for (var i = 0; i < entry.Chapters.Count; i++)
            {
                var chapter = entry.Chapters[i];
                var line = -1;
                if (!chapter.IsVolume && i >= _textCacheWindowFrom && i < _textCacheWindowTo)
                {
                    var k = i - _textCacheWindowFrom;
                    line = k < _textCacheHeadingLines!.Length ? _textCacheHeadingLines[k] : -1;
                }

                items.Add(new TocItem
                {
                    HeadingText = chapter.Name,
                    HeadingLevel = chapter.Level,
                    SourceLineNumber = line,
                    Tag = i,   // boxed int：章下标（卷也是下标，点击时解析到下一个非卷章）
                });
            }

            return items;
        }

        /// <summary>
        /// 目录点击分发（窗口模式）：卷 → 跳到其后第一个非卷章；窗口内 → 直接滚到该章标题行；
        /// 窗口外 → 以该章为中心重开窗口（前后章同步净化）。
        /// </summary>
        internal bool TryHandleTextCacheTocClick(TocItem item)
        {
            if (!TextCacheActive || item.Tag is not int index)
            {
                return false;
            }

            var chapters = _textCacheEntry!.Chapters;
            var target = TextCacheLogic.ResolveClickTarget(chapters, index);
            if (target < 0)
            {
                return true; // 卷后面没有章了：吞掉点击，不给"点了没反应"的困惑
            }

            if (target >= _textCacheWindowFrom && target < _textCacheWindowTo)
            {
                var k = target - _textCacheWindowFrom;
                var line = k < _textCacheHeadingLines!.Length ? _textCacheHeadingLines[k] : -1;
                if (line > 0)
                {
                    if (_isVerticalMode)
                    {
                        _ = PrepareVerticalTextAsync(line);
                    }
                    else if (_isAozoraMode)
                    {
                        JumpToAozoraLine(line);
                    }
                    else
                    {
                        ScrollToLine(line);
                    }

                    return true;
                }
            }

            _ = ShowTextCacheWindowAsync(target);
            return true;
        }

        /// <summary>
        /// 滚动驱动的窗口滑动（<see cref="TextScrollViewer_ViewChanged"/> 里调）。
        /// 当前章滚出窗口 → 以它为中心重开；仍在窗口内 → 只更新阅读位置（章名 + 章内行）。
        /// </summary>
        internal void TextCacheScrollCheck()
        {
            if (!TextCacheActive || _textCacheSwitching || _textCacheHeadingLines is null)
            {
                return;
            }

            var topLine = GetTopVisibleLineIndex();
            var current = TextCacheLogic.CurrentChapterInWindow(
                _textCacheHeadingLines, _textCacheWindowFrom, topLine);
            if (current < 0)
            {
                return;
            }

            if (current < _textCacheWindowFrom || current >= _textCacheWindowTo)
            {
                // 滚出窗口：重开（装配在后台，完成后整页替换并滚到该章标题行）
                _ = ShowTextCacheWindowAsync(current);
                return;
            }

            if (current != _textCacheLastScrollChapter)
            {
                _textCacheLastScrollChapter = current;
                SaveTextCachePosition(current, TextCacheLogic.LineOffsetInChapter(
                    _textCacheHeadingLines[current - _textCacheWindowFrom], topLine));
            }
        }

        // ============================================================
        // 位置 / 菜单
        // ============================================================

        /// <summary>保存阅读位置（章名 + 章内行偏移）进缓存文件。失败静默——位置丢了不影响阅读。</summary>
        private void SaveTextCachePosition(int chapterIndex, int lineInChapter)
        {
            var entry = _textCacheEntry;
            var key = _textCacheSourceKey;
            if (entry is null || string.IsNullOrEmpty(key) || chapterIndex < 0 || chapterIndex >= entry.Chapters.Count)
            {
                return;
            }

            var name = entry.Chapters[chapterIndex].Name;
            _ = Task.Run(async () =>
            {
                try
                {
                    await TextCacheStore.UpdatePositionAsync(key, name, lineInChapter).ConfigureAwait(false);
                }
                catch
                {
                    // 位置保存失败不影响阅读
                }
            });
        }

        /// <summary>切文档前把当前位置刷进缓存（下一步就要换源键了）。</summary>
        internal void FlushTextCachePosition()
        {
            if (!TextCacheActive || _textCacheHeadingLines is null)
            {
                return;
            }

            var topLine = GetTopVisibleLineIndex();
            var current = TextCacheLogic.CurrentChapterInWindow(
                _textCacheHeadingLines, _textCacheWindowFrom, topLine);
            if (current >= 0)
            {
                SaveTextCachePosition(current, TextCacheLogic.LineOffsetInChapter(
                    _textCacheHeadingLines[current - _textCacheWindowFrom], topLine));
            }
        }

        /// <summary>清窗口会话状态（不删缓存文件；换源 / 缓存失效时调）。</summary>
        internal void ClearTextCacheWindowState()
        {
            _textCacheEntry = null;
            _textCacheNormalizedRaw = null;
            _textCacheSourceKey = null;
            _textCacheHeadingLines = null;
            _textCacheWindowFrom = 0;
            _textCacheWindowTo = 0;
            _textCacheCenter = -1;
            _textCacheLastScrollChapter = -1;
            _textCacheSwitching = false;
        }

        /// <summary>
        /// 菜单项点击：有有效缓存 → 删；没有 → 保存（读原文 → 切章 → 全量净化校验 → 落盘）。
        /// </summary>
        internal async Task ToggleTextCacheAsync()
        {
            var service = BookSources;
            await service.EnsureInitializedAsync().ConfigureAwait(true);

            var key = TextCacheLogic.ComputeSourceKey(
                _currentTextFilePath, _archiveSession.CurrentPath, _currentTextArchiveEntryKey);
            if (string.IsNullOrEmpty(key))
            {
                ShowNotification("当前没有打开的文本文件", "\uE783", "Red");
                return;
            }

            if (IsTextCacheUsable(TextCacheStore.Load(key), key))
            {
                TextCacheStore.Delete(key);
                UpdateTextCacheMenuState();
                ShowNotification(
                    "已删除该文件的章节缓存（当前会话仍按窗口显示，重新打开文件后按整本净化）",
                    "\uE74D", "Gray");
                return;
            }

            await SaveTextCacheAsync().ConfigureAwait(true);
        }

        /// <summary>保存缓存：完整目录 + 章节分化 + 当前阅读位置。全量净化只用于校验章名，不落盘。</summary>
        private async Task SaveTextCacheAsync()
        {
            var raw = await ReadCurrentTextRawAsync().ConfigureAwait(true);
            if (raw is null)
            {
                ShowNotification("读不到当前文件的原文", "\uE783", "Red");
                return;
            }

            var service = BookSources;
            var titleRules = service.TitleRules.GetEnabledOrdered();
            var chapters = TextCacheLogic.BuildChapterMap(raw, titleRules);
            if (chapters.Count == 0)
            {
                ShowNotification(
                    "未识别到任何章节标题，无法生成目录缓存。可先导入匹配本书写法的标题规则（如「第N节」）再试。",
                    "\uE783", "Red");
                return;
            }

            var key = TextCacheLogic.ComputeSourceKey(
                _currentTextFilePath, _archiveSession.CurrentPath, _currentTextArchiveEntryKey);
            var (size, mtime) = GetTextSourceMetadata();

            ShowNotification($"正在生成缓存（全量净化校验 {chapters.Count} 章）…", "\uE8F1", "Gray");

            // 全量净化校验：净化后重新分章，数量一致 ⇒ 采用净化后的章名（铁律"净化在前、分章在后"的名字版）
            var mismatch = await Task.Run(() =>
            {
                var pure = service.PurifyForDisplay(raw);
                var pureSplit = TextTocBuilder.Build(
                    TextCacheLogic.NormalizeNewlines(pure), titleRules);
                if (pureSplit.Count == chapters.Count)
                {
                    for (var i = 0; i < chapters.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(pureSplit[i].Title))
                        {
                            chapters[i] = chapters[i] with { Name = pureSplit[i].Title };
                        }
                    }

                    return false;
                }

                return true;
            }).ConfigureAwait(true);

            var entry = new TextCacheEntry
            {
                SourceKey = key,
                FileSize = size,
                FileLastWriteUtcTicks = mtime,
                ReplaceRulesFingerprint = TextCacheLogic.ComputeReplaceRulesFingerprint(service.ReplaceRules.GetAll()),
                TitleRulesFingerprint = TextCacheLogic.ComputeTitleRulesFingerprint(service.TitleRules.GetAll()),
                SavedAtUtcTicks = DateTime.UtcNow.Ticks,
                Chapters = chapters,
                LastChapterName = string.Empty,
                LastLineInChapter = 0,
            };

            await TextCacheStore.SaveAsync(entry).ConfigureAwait(true);

            // 立即切到窗口模式（省一次"保存后还要重开文件"）
            ClearTextCacheWindowState();
            _textCacheEntry = entry;
            _textCacheSourceKey = key;
            _textCacheNormalizedRaw = TextCacheLogic.NormalizeNewlines(raw);
            await ShowTextCacheWindowAsync(0).ConfigureAwait(true);

            UpdateTextCacheMenuState();
            var message = mismatch
                ? $"已缓存：{chapters.Count} 章（注意：净化后仅识别到部分标题，目录名按原文生成）"
                : $"已缓存：{chapters.Count} 章，之后打开只净化前后章节";
            ShowNotification(message, "\uE73E", "Green");
        }

        /// <summary>重新读当前源的原文（保存缓存用；显示中的是净化后文本，不能反推）。</summary>
        private async Task<string?> ReadCurrentTextRawAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentTextFilePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(_currentTextFilePath).AsTask().ConfigureAwait(true);
                    return await _textDocumentLoadService
                        .ReadLocalFileAsync(file, _settingsManager.EncodingName).ConfigureAwait(true);
                }

                if (!string.IsNullOrEmpty(_currentTextArchiveEntryKey))
                {
                    return await _textDocumentLoadService
                        .ReadArchiveEntryAsync(
                            new ImageEntry { ArchiveEntryKey = _currentTextArchiveEntryKey, DisplayName = FileNameText.Text },
                            _settingsManager.EncodingName)
                        .ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Read raw for cache failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>当前源的文件元数据（本地文件 / 压缩包文件；取不到返回 0,0 → 缓存判无效）。</summary>
        private (long Size, long MtimeTicks) GetTextSourceMetadata()
        {
            try
            {
                var path = !string.IsNullOrEmpty(_currentTextFilePath)
                    ? _currentTextFilePath
                    : _archiveSession.CurrentPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return (0, 0);
                }

                var info = new FileInfo(path);
                return (info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                return (0, 0);
            }
        }

        /// <summary>缓存可用性：键 + 文件元数据 + 两条规则指纹全部对上。</summary>
        private bool IsTextCacheUsable(TextCacheEntry? entry, string key)
        {
            if (entry is null)
            {
                return false;
            }

            var service = _bookSourceService;
            if (service is null || !service.IsInitialized)
            {
                return false;
            }

            var (size, mtime) = GetTextSourceMetadata();
            return TextCacheLogic.IsValid(
                entry,
                key,
                size,
                mtime,
                TextCacheLogic.ComputeReplaceRulesFingerprint(service.ReplaceRules.GetAll()),
                TextCacheLogic.ComputeTitleRulesFingerprint(service.TitleRules.GetAll()));
        }

        /// <summary>把菜单勾选/可用状态刷成当前真实状态（文本源 + 有效缓存）。</summary>
        internal void UpdateTextCacheMenuState()
        {
            var key = TextCacheLogic.ComputeSourceKey(
                _currentTextFilePath, _archiveSession.CurrentPath, _currentTextArchiveEntryKey);
            var hasSource = !string.IsNullOrEmpty(key);
            var cached = hasSource && IsTextCacheUsable(TextCacheStore.Load(key), key);
            MainToolbar.SetTextCacheState(cached, hasSource);
        }

        /// <summary>
        /// 窗口模式的目录提供器：打开时目录已全部取回（缓存里就是完整目录），
        /// 不需要再去扫正文——与书源的 <c>StaticTocProvider</c> 同定位。
        /// </summary>
        private sealed class TextCacheTocProvider : ITocProvider
        {
            private readonly List<TocItem> _items;

            public TextCacheTocProvider(List<TocItem> items) => _items = items;

            public Task<List<TocItem>> GetTocAsync(CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(_items);
            }
        }
    }
}

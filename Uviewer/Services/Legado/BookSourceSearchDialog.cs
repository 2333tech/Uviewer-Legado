using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services;
using Uviewer.Services.Legado;
using CoreTocItem = Uviewer.Services.Legado.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书籍搜索对话框（纯代码构建，不改 XAML）。
    ///
    /// <para><b>搜索的是什么</b>：书名 / 作者关键词 —— 由每个书源的 <c>searchUrl</c>
    /// 模板（<c>{{key}}</c> / <c>{key}</c>）插值后请求对应网站，解析其结果列表。
    /// <b>不是</b>搜索书源名称。</para>
    ///
    /// <para><b>结果必须流式渲染</b>（重要）：实测 204 个书源全量搜索约 78s，而首个命中
    /// 通常在 250ms 内出现。若等 <c>SearchAsync</c> 返回后再统一渲染，用户在整个 78s 里看到的
    /// 就是"已搜索 X/X · 命中 0 本"的空列表——这正是"搜索永远 0 命中"的真实成因。
    /// 现在每命中一个书源立刻追加卡片。</para>
    ///
    /// <para><b>每条结果的呈现</b>（用户指定）：
    /// 圆角矩形卡片，左侧两行文字（第一行"书名 | 作者"，第二行小一号"书源名 | 章节名"），
    /// 右侧一个下载按钮——点一下开始下载并自动打开，再点一下取消下载，按钮配色随之切换。</para>
    /// </summary>
    public sealed class BookSourceSearchDialog
    {
        /// <summary>
        /// 内容区期望宽度（px）。结果卡片要并排显示两行长文本（书名|作者、书源 + 最新章节）
        /// 与右侧按钮，宽度不足会立刻触发省略号，反而看不清是哪本书。
        /// <para>取 680：书源名（如"趣悦小说（优++）"）+ 最新章节名本身就是长文本，
        /// 压缩它们等于牺牲可用性。</para>
        /// </summary>
        private const double ContentWidth = 680;

        /// <summary>
        /// 结果滚动区期望高度（px）：约 18 张卡片（原 340px ≈ 6 张的 3 倍）。
        /// <para>多源搜索一次常命中几十到几百本，6 张的可视区等于"搜完还得roll很多屏"。
        /// 实际高度由 <see cref="LegadoDialogLayout"/> 按窗口高度收敛（小窗口下自动变矮）。</para>
        /// </summary>
        private const double ContentHeight = 1020;

        private readonly FrameworkElement _rootElement;
        private readonly BookSourceSearchService _search;
        private readonly Func<BookSourceSearchService.BookSearchHit, Task> _onOpenBook;
        private readonly Func<string, Task> _onOpenDownloaded;
        private CancellationTokenSource? _cts;

        /// <summary>
        /// 补"最新章节名"的全局并发闸门。
        /// <para>搜索一次能返回几百条，若每条都并发拉目录，会瞬间打出上百个请求
        /// —— 既把站点打崩，也会让进度条看起来卡死。限到 4 个并发。</para>
        /// </summary>
        private static readonly SemaphoreSlim _tocFetchGate = new(4, 4);

        public BookSourceSearchDialog(
            FrameworkElement rootElement,
            BookSourceSearchService search,
            Func<BookSourceSearchService.BookSearchHit, Task> onOpenBook,
            Func<string, Task>? onOpenDownloaded = null)
        {
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _onOpenBook = onOpenBook ?? throw new ArgumentNullException(nameof(onOpenBook));

            // 未注入时退化为"下载完仍按在线方式打开"，保证调用方不必关心
            _onOpenDownloaded = onOpenDownloaded ?? (_ => Task.CompletedTask);
        }

        /// <summary>弹出对话框；<paramref name="initialKeyword"/> 非空时带关键词并立即开搜。</summary>
        public async Task ShowAsync(string? initialKeyword = null)
        {
            var service = _search.Service;

            // 配色按实际主题显式取值：亮/暗下文字与底色都有足够对比度
            var palette = LegadoTheme.For(_rootElement);

            var input = new TextBox
            {
                PlaceholderText = "输入书名或作者（回车搜索）",
                Text = initialKeyword ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var progressText = new TextBlock { Text = string.Empty, Opacity = 0.75, FontSize = 12 };
            var status = new TextBlock
            {
                Text = "就绪。搜索会并发查询所有已启用书源。",
                TextWrapping = TextWrapping.Wrap,
            };

            var searchButton = new Button { Content = "搜索书籍", MinWidth = 96 };

            var progressBar = new ProgressBar
            {
                IsIndeterminate = false,
                Visibility = Visibility.Collapsed,
                Height = 3,
            };

            // 结果容器：ScrollViewer + StackPanel，逐条追加圆角卡片
            var resultPanel = new StackPanel { Spacing = 6 };
            var scroller = new ScrollViewer
            {
                Content = resultPanel,
                Height = ContentHeight,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            int completed = 0, totalSources = 0, hitCount = 0;
            var running = false;

            // 相关性排序用的落位表：卡片按评分降序插入（同分保持"先到先得"，流式渲染不跳字）
            var ranked = new List<(int Score, long Seq, UIElement Card)>();
            long sequence = 0;

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 0);
            Grid.SetColumn(searchButton, 1);
            header.Children.Add(input);
            header.Children.Add(searchButton);

            var panel = new StackPanel
            {
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            panel.Children.Add(new TextBlock
            {
                Text = "在所有已启用书源中搜索书籍（按书名/作者）",
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
            panel.Children.Add(header);
            panel.Children.Add(progressBar);
            panel.Children.Add(progressText);
            panel.Children.Add(status);
            panel.Children.Add(scroller);

            var dialog = new ContentDialog
            {
                XamlRoot = _rootElement.XamlRoot,
                RequestedTheme = _rootElement.ActualTheme,
                Title = "搜索书籍（书源）",
                Content = panel,
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            // 结果卡片要放"书名 | 作者"+"书源名 | 最新章节名"两行长文本 + 右侧下载按钮，
            // 默认 548px 的对话框装不下。这里放宽窗口（不缩字号/不缩文本），并按窗口收敛。
            // 尺寸机制（资源键而非属性）见 LegadoDialogLayout 类注释。
            LegadoDialogLayout.Fit(dialog, _rootElement, ContentWidth, ContentHeight);
            scroller.Height = LegadoDialogLayout.ScrollViewportHeight(_rootElement, ContentHeight);

            var downloads = new BookSourceDownloadService(service);
            var dispatcher = _rootElement.DispatcherQueue;

            var runSearch = new Func<Task>(async () =>
            {
                var keyword = input.Text?.Trim() ?? string.Empty;
                if (keyword.Length == 0 || running)
                {
                    return;
                }

                _cts?.Cancel();
                _cts?.Dispose();
                var cts = new CancellationTokenSource();
                _cts = cts;

                running = true;
                searchButton.IsEnabled = false;
                input.IsEnabled = false;
                completed = 0;
                totalSources = 0;
                hitCount = 0;
                resultPanel.Children.Clear();
                ranked.Clear();
                sequence = 0;
                progressBar.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                progressText.Text = string.Empty;
                status.Text = $"正在搜索“{keyword}”…";

                var stopped = false;

                try
                {
                    void RefreshProgress()
                    {
                        if (totalSources > 0)
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Maximum = totalSources;
                            progressBar.Value = completed;
                        }

                        progressText.Text = totalSources > 0
                            ? $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本"
                            : "正在准备书源…";
                    }

                    var progress = new Progress<SearchProgress>(p =>
                    {
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        totalSources = p.Total;
                        completed = p.Completed;
                        RefreshProgress();
                    });

                    // 流式回调：每命中一个书源立刻在 UI 线程追加卡片（不等全部搜完）
                    void OnGroup(BookSourceSearchService.SearchGroup group)
                    {
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        dispatcher.TryEnqueue(() =>
                        {
                            if (cts.IsCancellationRequested)
                            {
                                return;
                            }

                            foreach (var hit in group.Hits)
                            {
                                hitCount++;

                                // 相关性降序插入：完全匹配的书名永远在最上面，
                                // 而不是"哪个源先返回就排前面"
                                var card = BuildResultCard(hit, downloads, palette, cts.Token, status);
                                InsertByRelevance(resultPanel, ranked, hit, keyword, card, sequence++);
                            }

                            RefreshProgress();
                        });
                    }

                    var groups = await _search.SearchAsync(keyword, null, progress, OnGroup, cts.Token);

                    if (cts.IsCancellationRequested)
                    {
                        stopped = true;
                        return;
                    }

                    // 进度条收尾（确保走到 100%）
                    if (totalSources > 0)
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = totalSources;
                    }

                    progressText.Text = $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本";
                    status.Text = hitCount == 0
                        ? $"“{keyword}”没有结果。书源站点可能已失效或改版（{totalSources} 个源全部查询完毕）。"
                        : $"“{keyword}”命中 {hitCount} 本，来自 {groups.Count} 个书源。点击卡片打开，或点右侧按钮下载整本。";
                }
                catch (OperationCanceledException)
                {
                    status.Text = "搜索已取消。";
                }
                catch (Exception ex)
                {
                    status.Text = $"搜索失败：{ex.Message}";
                }
                finally
                {
                    if (!stopped)
                    {
                        running = false;
                    }

                    searchButton.IsEnabled = true;
                    input.IsEnabled = true;
                    progressBar.Visibility = Visibility.Collapsed;
                }
            });

            searchButton.Click += async (s, e) => await runSearch();
            input.KeyDown += async (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    await runSearch();
                }
            };

            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ============================================================
        // 结果卡片
        // ============================================================

        /// <summary>
        /// 单条结果的圆角卡片：左两行文字 + 右侧下载按钮。
        /// <para>第一行"书名 | 作者"用标准字号；第二行"书源名 | 最新章节"小一号、降透明度，
        /// 形成主次层级。点击卡片打开书籍；点按钮则下载整本并自动打开。</para>
        /// <para><b>宽度</b>：卡片宽度由外层 <c>ScrollViewer</c> 的可用宽度决定，
        /// 文字列用 <c>GridUnitType.Star</c> + <c>TextTrimming</c> 收缩，
        /// 因此书名再长也只会在自己那列内省略，不会把右侧按钮顶出对话框。</para>
        /// </summary>
        private FrameworkElement BuildResultCard(
            BookSourceSearchService.BookSearchHit hit,
            BookSourceDownloadService downloads,
            LegadoPalette palette,
            CancellationToken searchToken,
            TextBlock status)
        {
            var primary = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(hit.Author) ? hit.Name : $"{hit.Name} | {hit.Author}",
                FontSize = 14,
                Foreground = palette.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // 音视频书源：直接在结果上标注「⚠ 不支持音视频」（约定：本应用始终不实现音视频，
            // 但也不隐藏结果——用户可能正想知道"这个源里有这本书"）。标题列仍是 Star，
            // 徽标 Auto，长书名照旧在自己的列里省略，不会把徽标/下载按钮顶出去。
            FrameworkElement titleRow = primary;
            if (hit.IsAudioVideo)
            {
                var titleGrid = new Grid { ColumnSpacing = 8 };
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(primary, 0);
                var badge = BuildUnsupportedMediaBadge(palette);
                Grid.SetColumn(badge, 1);
                titleGrid.Children.Add(primary);
                titleGrid.Children.Add(badge);
                titleRow = titleGrid;
            }

            // 第二行拆成"书源名（纯文字）+ 最新章节（可点看目录）"两段。
            // 落后一版把整行做成 IsHitTestVisible=false，点章节名会落到卡片的"打开书籍"上——
            // 用户只想先看看目录，却被直接打开了正文（部分源正文取不到，更像是结果坏了）。
            var sourceLabel = new TextBlock
            {
                Text = hit.SourceName,
                FontSize = 12,
                Foreground = palette.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var latestHolder = new LatestChapterHolder();
            var latestLabel = new TextBlock
            {
                Text = "｜正在获取最新章节…",
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(latestLabel, "点击查看该书目录");

            // 悬停仅提亮文字（不用按钮样式：这是"信息 + 可点"，不是操作按钮）
            latestLabel.PointerEntered += (s2, e2) => latestLabel.Foreground = palette.TextPrimary;
            latestLabel.PointerExited += (s2, e2) => latestLabel.Foreground = palette.TextSecondary;
            latestLabel.PointerPressed += (s2, e2) =>
            {
                if (!e2.GetCurrentPoint(latestLabel).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                // 音视频结果不拉目录（见下方 FillLatest 的说明）
                if (hit.IsAudioVideo)
                {
                    e2.Handled = true;
                    return;
                }

                // 标记已处理：不再冒泡到卡片的"打开书籍"
                e2.Handled = true;
                ShowChapterListFlyout(latestLabel, latestHolder, hit, palette);
            };

            var secondaryRow = new Grid { ColumnSpacing = 0 };
            secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(sourceLabel, 0);
            Grid.SetColumn(latestLabel, 1);
            secondaryRow.Children.Add(sourceLabel);
            secondaryRow.Children.Add(latestLabel);

            var textStack = new Grid { VerticalAlignment = VerticalAlignment.Center };
            textStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            textStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(titleRow, 0);
            Grid.SetRow(secondaryRow, 1);
            textStack.Children.Add(titleRow);
            textStack.Children.Add(secondaryRow);

            var downloadButton = BuildDownloadButton(hit, downloads, latestLabel, palette, searchToken);

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(downloadButton, 1);
            grid.Children.Add(textStack);
            grid.Children.Add(downloadButton);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 8, 8),
                Background = palette.Surface,
                BorderBrush = palette.SurfaceBorder,
                BorderThickness = new Thickness(1),

                // 关键：不写死宽度，让卡片跟随 ScrollViewer 的可用宽度。
                // 之前右侧按钮溢出，就是外层容器宽度没约束住。
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = grid,
            };

            // 点击卡片（而非按钮）直接深链打开
            card.PointerPressed += async (s, e) =>
            {
                if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                // 点在下载按钮上时不触发打开（按钮自己处理）
                if (downloadButton.IsPointerOver)
                {
                    return;
                }

                // 音视频结果只标注、不打开：本应用始终不实现音视频，打开只会得到"看起来坏掉"的文本会话
                if (hit.IsAudioVideo)
                {
                    status.Text = $"「{hit.Name}」来自音视频书源（{hit.SourceName}），本应用只支持文本阅读。";
                    return;
                }

                OpenHit(hit);
            };

            // 后台补最新章节名（不阻塞 UI；失败静默，保留占位文案）。
            // 音视频结果不拉目录：本应用始终不处理音视频，没必要为"不读的结果"再打一次站点请求。
            if (hit.IsAudioVideo)
            {
                latestLabel.Text = "｜音视频结果，不提供目录";
                latestLabel.IsHitTestVisible = false;
                ToolTipService.SetToolTip(latestLabel, "音视频结果不支持目录/阅读");
            }
            else
            {
                _ = FillLatestChapterAsync(hit, latestLabel, latestHolder, searchToken);
            }

            return card;
        }

        /// <summary>
        /// 音视频结果徽标：黄色三角感叹号 + 「不支持音视频」。
        /// <para><b>为什么标黄而不是标红</b>：这不是错误，而是产品边界（本应用只做文本阅读）；
        /// 红色会被误读成"这条结果坏了/站点失效"。</para>
        /// <para>配色遵循弹窗铁律：文字一律灰度，彩色只留给语义 Emoji（⚠ 取 0xE88A00，
        /// 与书源管理列表的能力徽标同一色）；<c>IsHitTestVisible=false</c>，
        /// 让点击穿透到卡片（不改变"点卡片打开"的既有行为）。</para>
        /// </summary>
        private static TextBlock BuildUnsupportedMediaBadge(LegadoPalette palette)
        {
            var badge = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            badge.Inlines.Add(new Run
            {
                Text = "⚠ ",
                Foreground = LegadoTheme.Rgb(0xE8, 0x8A, 0x00),
            });
            badge.Inlines.Add(new Run
            {
                Text = "不支持音视频",
                Foreground = palette.TextSecondary,
            });

            ToolTipService.SetToolTip(badge, "该结果来自音视频类书源（bookSourceType 1 音频 / 4 视频）。本应用只处理文本，音视频不在支持范围内。");
            return badge;
        }

        /// <summary>
        /// 拉取该书的目录，把<b>最新章节名</b>填进第二行。
        /// <para><b>刻意单源串行 + 低优先级</b>：搜索可能返回几百条，若每条都并发拉目录，
        /// 会瞬间打出上百个请求把站点打崩、也把进度条卡住。这里限制全局并发，
        /// 并让失败保持占位文案（用户仍可点卡片进去看完整目录）。</para>
        /// </summary>
        private async Task FillLatestChapterAsync(
            BookSourceSearchService.BookSearchHit hit,
            TextBlock latestLabel,
            LatestChapterHolder holder,
            CancellationToken token)
        {
            try
            {
                // 同一时刻最多 4 个目录请求
                await _tocFetchGate.WaitAsync(token).ConfigureAwait(false);
                IReadOnlyList<CoreTocItem> chapters;
                try
                {
                    var session = _search.CreateSession(hit);
                    var opened = await session.OpenAsync(token).ConfigureAwait(false);
                    chapters = opened.Chapters;
                    holder.Chapters = chapters;
                }
                finally
                {
                    _tocFetchGate.Release();
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                var latest = chapters.Count > 0 ? chapters[^1].ChapterName ?? string.Empty : string.Empty;
                var text = string.IsNullOrWhiteSpace(latest) ? "｜目录为空" : $"｜最新：{latest}";

                _rootElement.DispatcherQueue.TryEnqueue(() =>
                {
                    // 下载中显示的是实时进度，别被迟到的目录结果覆盖掉
                    if (latestLabel.Text.Contains("下载中"))
                    {
                        return;
                    }

                    latestLabel.Text = text;
                    ToolTipService.SetToolTip(latestLabel, "点击查看该书目录（" + text.TrimStart('｜') + "）");
                });
            }
            catch (Exception ex)
            {
                // 目录拉取失败不影响结果卡片（点击标签会显示失败原因）
                holder.Error = $"目录获取失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 下载按钮的三态机（用户明确要求）：
        /// <list type="number">
        /// <item><b>下载</b>（Idle）→ 第一次按下开始下载，按钮变<b>取消</b>（警示色）；</item>
        /// <item><b>取消</b>（Downloading）→ 再按一次才进入<b>取消中…</b> 并真正发取消信号；</item>
        /// <item><b>取消中…</b>（Cancelling）→ 期间禁用按钮，直到下载任务真正结束才回到<b>下载</b>。</item>
        /// </list>
        /// <para>早期实现按下即显示"取消中…"，用户根本分不清
        /// "正在下载、可以点取消"与"已经请求取消"——这两件事的状态意义完全不同。</para>
        /// </summary>
        private Button BuildDownloadButton(
            BookSourceSearchService.BookSearchHit hit,
            BookSourceDownloadService downloads,
            TextBlock latestLabel,
            LegadoPalette palette,
            CancellationToken searchToken)
        {
            // 音视频结果不提供下载：文本下载器拿不到可读正文（调用只会产出空书/错误提示）。
            // 禁用按钮而不是隐藏，让"为什么不能下"有悬停说明，也保持卡片右侧布局稳定。
            if (hit.IsAudioVideo)
            {
                var blocked = new Button
                {
                    Content = "下载",
                    MinWidth = 64,
                    Padding = new Thickness(10, 4, 10, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsEnabled = false,
                };
                ToolTipService.SetToolTip(blocked, "音视频结果不支持下载（本应用只处理文本类书源）");
                return blocked;
            }

            var button = new Button
            {
                Content = "下载",
                MinWidth = 64,
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = palette.Accent,
                Foreground = palette.OnAccent,
            };
            ToolTipService.SetToolTip(button, "下载整本书到本地（下载中可按取消）");

            CancellationTokenSource? downloadCts = null;
            var state = DownloadState.Idle;
            var downloadedPath = string.Empty;

            button.Click += async (s, e) =>
            {
            switch (state)
            {
                case DownloadState.Downloading:
                    // 第一次按"取消"：进入取消中（等任务真正收尾）
                    SetButton(DownloadState.Cancelling, "取消中…", palette.Warning);
                    downloadCts?.Cancel();
                    return;

                case DownloadState.Cancelling:
                    // 已经在取消：忽略重复点击（按钮此时也是禁用的）
                    return;

                case DownloadState.Done:
                    // 已下载：再次点击直接打开本地文件（不重新下载）
                    await OpenDownloaded(downloadedPath);
                    return;

                default:
                    await StartDownloadAsync();
                    return;
            }
            };

            void SetButton(DownloadState next, string label, Brush? background)
            {
                state = next;
                button.Content = label;
                button.Background = background ?? button.Background;

                // 取消中禁用：否则用户会以为还能再按一次
                button.IsEnabled = next != DownloadState.Cancelling;
            }

            async Task StartDownloadAsync()
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(searchToken);
                downloadCts = cts;

                SetButton(DownloadState.Downloading, "取消", palette.Warning);

                BookSourceReadingSession session;
                try
                {
                    session = _search.CreateSession(hit);
                }
                catch (Exception ex)
                {
                    // 书源已被删除/URL 对不上：立刻复位按钮，不要停在"下载中"
                    SetButton(DownloadState.Idle, "下载", palette.Accent);
                    latestLabel.Text = $"｜无法下载：{ex.Message}";
                    return;
                }

                // 进度经 IProgress 回到 UI 线程更新第二行文字
                var progress = new Progress<BookSourceDownloadService.DownloadProgress>(p =>
                {
                    latestLabel.Text = string.Format(
                        CultureInfo.CurrentCulture,
                        "｜下载中 {0}/{1}  {2}",
                        p.Done,
                        p.Total,
                        p.ChapterName);
                });

                BookSourceDownloadService.DownloadResult result;
                try
                {
                    result = await downloads.DownloadAsync(session, progress, cts.Token);
                }
                catch (Exception ex)
                {
                    result = new BookSourceDownloadService.DownloadResult(false, false, string.Empty, ex.Message);
                }
                finally
                {
                    cts.Dispose();
                    if (ReferenceEquals(downloadCts, cts))
                    {
                        downloadCts = null;
                    }
                }

                if (result.Cancelled)
                {
                    SetButton(DownloadState.Idle, "下载", palette.Accent);
                    latestLabel.Text = "｜已取消下载";
                    return;
                }

                if (!result.Success)
                {
                    SetButton(DownloadState.Idle, "重试", palette.Accent);
                    latestLabel.Text = $"｜{result.Message}";
                    return;
                }

                // 完成：按钮变"已下载"，并自动打开刚下好的本地文件
                downloadedPath = result.FilePath;
                SetButton(DownloadState.Done, "已下载", palette.Success);
                latestLabel.Text = $"｜已下载 {result.Message}";
                await OpenDownloaded(result.FilePath);
            }

            return button;
        }

        /// <summary>
        /// 按文本相关性把卡片插到正确位置（降序；同分保持"先到先得"）。
        /// <para>用二分查找定位插入点：几百条结果 × 每命中一个源插一批，
        /// 线性扫描的代价会随列表长度平方增长（实测 500 条时卡顿明显）。</para>
        /// </summary>
        private static void InsertByRelevance(
            StackPanel panel,
            List<(int Score, long Seq, UIElement Card)> ranked,
            BookSourceSearchService.BookSearchHit hit,
            string keyword,
            UIElement card,
            long sequence)
        {
            var score = SearchRelevance.Score(hit.Name, hit.Author, keyword);

            // 找第一个"评分更低"的位置（同分排在后到者之后 → 先到先得）
            var low = 0;
            var high = ranked.Count;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (ranked[mid].Score >= score)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            ranked.Insert(low, (score, sequence, card));
            panel.Children.Insert(low, card);
        }

        /// <summary>
        /// 弹窗显示该书目录（无按钮样式：纯文字列表，与顶栏目录下拉同一观感）。
        /// <para>目录在 <see cref="FillLatestChapterAsync"/> 里已经拉过一次并缓存在
        /// <see cref="LatestChapterHolder"/> 中，这里零网络开销；未取到则显示原因。</para>
        /// </summary>
        private void ShowChapterListFlyout(
            FrameworkElement anchor,
            LatestChapterHolder holder,
            BookSourceSearchService.BookSearchHit hit,
            LegadoPalette palette)
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = false,
            };

            if (holder.Chapters is { Count: > 0 })
            {
                foreach (var chapter in holder.Chapters)
                {
                    list.Items.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(chapter.ChapterName) ? "(无标题)" : chapter.ChapterName,
                        FontSize = 14,
                        Foreground = palette.TextPrimary,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                        Margin = new Thickness(0, 3, 0, 3),

                        // 纯展示：不响应点击，也就不会被误认为是按钮
                        IsHitTestVisible = false,
                    });
                }
            }
            else
            {
                list.Items.Add(new TextBlock
                {
                    Text = holder.Error ?? "目录尚未取到（可稍后再试，或点卡片直接打开）",
                    FontSize = 12,
                    Foreground = palette.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    IsHitTestVisible = false,
                });
            }

            var panel = new Grid { Width = 320, MaxHeight = 420 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = $"目录 · {hit.Name}",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = palette.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 10),
            };

            var count = new TextBlock
            {
                Text = holder.Chapters is { Count: > 0 } ? $"共 {holder.Chapters.Count} 章" : string.Empty,
                FontSize = 11,
                Foreground = palette.TextSecondary,
                Margin = new Thickness(0, -6, 0, 8),
            };

            var header = new StackPanel();
            header.Children.Add(title);
            header.Children.Add(count);
            Grid.SetRow(header, 0);
            Grid.SetRow(list, 1);
            panel.Children.Add(header);
            panel.Children.Add(list);

            // Flyout 没有 RequestedTheme（只有 FrameworkElement 有）；主题随 XamlRoot 继承
            var flyout = new Flyout
            {
                XamlRoot = _rootElement.XamlRoot,
                Placement = FlyoutPlacementMode.Bottom,
                Content = panel,
            };

            flyout.ShowAt(anchor);
        }

        /// <summary>
        /// 一条结果卡的目录状态：<see cref="FillLatestChapterAsync"/> 写入，
        /// 点"最新章节"弹目录时直接复用（不重复请求）。
        /// </summary>
        private sealed class LatestChapterHolder
        {
            public IReadOnlyList<CoreTocItem>? Chapters { get; set; }

            public string? Error { get; set; }
        }

        /// <summary>打开已下载的本地文件（关闭搜索框后打开，避免挡住阅读区）。</summary>
        private async Task OpenDownloaded(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                await _onOpenDownloaded(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Legado open downloaded failed: {ex.Message}");
            }
        }

        /// <summary>打开一本书（关掉对话框再深链打开，避免对话框挡住阅读区）。</summary>
        private async void OpenHit(BookSourceSearchService.BookSearchHit hit)
        {
            try
            {
                await _onOpenBook(hit);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Legado open failed: {ex.Message}");
            }
        }

        private enum DownloadState { Idle, Downloading, Cancelling, Done }

    }
}

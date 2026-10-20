using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Services.BookSources;
using Uviewer.Services;
using Uviewer.Services.Legado;

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
        /// 内容区期望宽度（px）。结果卡片要并排显示两行长文本（书名|作者、书源|最新章节）
        /// 与右侧下载按钮，宽度不足会立刻触发省略号，反而看不清是哪本书。
        /// <para>取 620 而非更小：书源名（如"趣悦小说（优++）"）+ 最新章节名
        /// 本身就是长文本，压缩它们等于牺牲可用性。</para>
        /// </summary>
        private const double ContentWidth = 620;

        /// <summary>
        /// 结果滚动区期望高度（px）：约 6 张卡片，便于纵向扫读与比较。
        /// 实际高度由 <see cref="LegadoDialogLayout"/> 按窗口高度收敛。
        /// </summary>
        private const double ContentHeight = 340;

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
            LegadoDialogLayout.Fit(dialog, _rootElement, ContentWidth);
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
                                resultPanel.Children.Add(BuildResultCard(hit, downloads, palette, cts.Token));
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
            CancellationToken searchToken)
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

            var secondary = new TextBlock
            {
                // 默认文案；拿到目录后会替换为"书源名 | 最新章节名"
                Text = $"{hit.SourceName} | 正在获取最新章节…",
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,

                // 第二行是纯信息：点它不该触发"打开书籍"。
                //（早期版本整卡统一处理点击，落在章节名上时用户看到的是书籍被打开，
                //  而部分源本就打不开 → 误以为是这条结果坏了。）
                IsHitTestVisible = false,
            };

            var textStack = new Grid { VerticalAlignment = VerticalAlignment.Center };
            textStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            textStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(primary, 0);
            Grid.SetRow(secondary, 1);
            textStack.Children.Add(primary);
            textStack.Children.Add(secondary);

            var downloadButton = BuildDownloadButton(hit, downloads, secondary, palette, searchToken);

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

                OpenHit(hit);
            };

            // 后台补最新章节名（不阻塞 UI；失败静默，保留占位文案）
            _ = FillLatestChapterAsync(hit, secondary, searchToken);

            return card;
        }

        /// <summary>
        /// 拉取该书的目录，把<b>最新章节名</b>填进第二行。
        /// <para><b>刻意单源串行 + 低优先级</b>：搜索可能返回几百条，若每条都并发拉目录，
        /// 会瞬间打出上百个请求把站点打崩、也把进度条卡住。这里限制全局并发，
        /// 并让失败保持占位文案（用户仍可点卡片进去看完整目录）。</para>
        /// </summary>
        private async Task FillLatestChapterAsync(
            BookSourceSearchService.BookSearchHit hit,
            TextBlock secondary,
            CancellationToken token)
        {
            try
            {
                // 同一时刻最多 4 个目录请求
                await _tocFetchGate.WaitAsync(token).ConfigureAwait(false);
                string latest;
                try
                {
                    var session = _search.CreateSession(hit);
                    var opened = await session.OpenAsync(token).ConfigureAwait(false);
                    latest = opened.Chapters.Count > 0
                        ? opened.Chapters[^1].ChapterName ?? string.Empty
                        : string.Empty;
                }
                finally
                {
                    _tocFetchGate.Release();
                }

                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(latest))
                {
                    return;
                }

                var text = $"{hit.SourceName} | 最新：{latest}";
                _rootElement.DispatcherQueue.TryEnqueue(() =>
                {
                    // 下载中显示的是实时进度，别被迟到的目录结果覆盖掉
                    if (secondary.Text.Contains("下载中"))
                    {
                        return;
                    }

                    secondary.Text = text;
                    ToolTipService.SetToolTip(secondary, text);
                });
            }
            catch
            {
                // 目录拉取失败不影响结果卡片（用户点进去仍会尝试打开）
            }
        }

        /// <summary>
        /// 下载按钮：未下载时显示"下载"（强调色），下载中显示"取消"（警示色），
        /// 点击切换状态——下载中点击即取消，已下载点击则重新打开。
        /// </summary>
        private Button BuildDownloadButton(
            BookSourceSearchService.BookSearchHit hit,
            BookSourceDownloadService downloads,
            TextBlock secondary,
            LegadoPalette palette,
            CancellationToken searchToken)
        {
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
            ToolTipService.SetToolTip(button, "下载整本书到本地（下载中点击可取消）");

            CancellationTokenSource? downloadCts = null;
            var state = DownloadState.Idle;
            var downloadedPath = string.Empty;

            button.Click += async (s, e) =>
            {
            switch (state)
            {
                case DownloadState.Downloading:
                    // 再按一次 = 取消
                    downloadCts?.Cancel();
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
            }

            async Task StartDownloadAsync()
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(searchToken);
                downloadCts = cts;

                SetButton(DownloadState.Downloading, "取消中…", palette.Warning);

                BookSourceReadingSession session;
                try
                {
                    session = _search.CreateSession(hit);
                }
                catch (Exception ex)
                {
                    // 书源已被删除/URL 对不上：立刻复位按钮，不要停在"下载中"
                    SetButton(DownloadState.Idle, "下载", palette.Accent);
                    secondary.Text = $"{hit.SourceName} | 无法下载：{ex.Message}";
                    return;
                }

                // 进度经 IProgress 回到 UI 线程更新第二行文字
                var progress = new Progress<BookSourceDownloadService.DownloadProgress>(p =>
                {
                    secondary.Text = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0} | 下载中 {1}/{2}  {3}",
                        hit.SourceName,
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
                    secondary.Text = $"{hit.SourceName} | 点击查看目录";
                    return;
                }

                if (!result.Success)
                {
                    SetButton(DownloadState.Idle, "重试", palette.Accent);
                    secondary.Text = $"{hit.SourceName} | {result.Message}";
                    return;
                }

                // 完成：按钮变"已下载"，并自动打开刚下好的本地文件
                downloadedPath = result.FilePath;
                SetButton(DownloadState.Done, "已下载", palette.Success);
                secondary.Text = $"{hit.SourceName} | 已下载 {result.Message}";
                await OpenDownloaded(result.FilePath);
            }

            return button;
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

        private enum DownloadState { Idle, Downloading, Done }

    }
}

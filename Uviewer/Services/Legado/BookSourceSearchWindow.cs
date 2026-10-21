using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Windows.Graphics;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using CoreTocItem = Uviewer.Services.Legado.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源搜索窗口（<b>独立 Window，非模态</b>）：打开后主窗口不被锁定。
    /// <para>布局：<b>左侧</b>=结果列表 + 简介区（点选条目后展开）；<b>右侧</b>=第一章预览
    /// （顶栏 + 正文滚动区）。点选条目会<b>立刻插队</b>后台探测队列并优先加载。</para>
    ///
    /// <para><b>结果状态标记</b>（下载按钮左侧）：
    /// ❌ 错误 = 条目无法访问（详情/目录/第一章请求失败）；⚠ 警告 = 可访问但有问题
    /// （问题只有两类：目录为空、正文第一章为空）。标记由后台探测队列产出——探测在
    /// "补最新章节"的那次请求（详情+目录）之上多抓一次第一章正文，这是警告标记的信息来源，
    /// 属于用户明确要求的成本；探得的第一章原文同时成为预览缓存（点选后零额外网络）。</para>
    ///
    /// <para><b>预览顶栏</b>（最右四个按钮：上一页/下一页/启用替换净化/字体修改）：
    /// 上一页/下一页 = 预览区滚动翻页；启用替换净化 = 引擎净化管线（<c>PurifyForDisplay</c>）
    /// 的预览级开关，重渲染第一章；字体修改 = <b>复用主窗口顶栏右侧的"界面字体"选择
    /// （切换字体族而非字号）</b>，选完整个搜索窗口（含预览）跟随新字体。
    /// 所有按钮沿用宿主默认样式与 <see cref="LegadoTheme"/> 配色，图标复用宿主顶栏同款字形。</para>
    /// </summary>
    public sealed class BookSourceSearchWindow
    {
        /// <summary>窗口期望尺寸（px）。左右两列布局需要比旧对话框更宽。</summary>
        private const int WindowWidth = 1220;
        private const int WindowHeight = 820;
        private const int ResultsMinWidth = 400;
        private const int IntroMaxHeight = 250;

        private readonly FrameworkElement _themeSource;
        private readonly BookSourceSearchService _search;
        private readonly Func<BookSourceSearchService.BookSearchHit, Task> _onOpenBook;
        private readonly Func<string, Task> _onOpenDownloaded;
        private readonly Func<Task>? _showUiFontPickerAsync;
        private readonly Func<string>? _getUiFontFamily;

        /// <summary>独立窗口与其内容根（创建于 <see cref="Show"/>；非空 = 窗口已打开）。</summary>
        private Window? _window;
        private PriorityWorkQueue? _probeQueue;

        /// <summary>窗口自己的主题元素（palette / Flyout XamlRoot / Dispatcher 都取自它）。</summary>
        private FrameworkElement? _hostElement;

        private CancellationTokenSource? _cts;

        // ── 窗口内控件（Show 里创建；卡片/探测回调经字段访问） ──
        private TextBlock? _statusText;
        private TextBlock? _introTitle;
        private TextBlock? _introMeta;
        private TextBlock? _introBody;
        private ScrollViewer? _introScroll;
        private FrameworkElement? _introPanel;
        private TextBlock? _previewChapterName;
        private TextBlock? _previewText;
        private ScrollViewer? _previewScroll;

        /// <summary>当前预览的原文（书源引擎原始返回）与开关状态。</summary>
        private string? _previewRawChapter;
        private bool _previewPurify = true;
        private CoreTocItem? _previewChapter;
        private BookSourceReadingSession? _previewSession;

        /// <summary>当前选中（高亮）的结果卡片。</summary>
        private Border? _selectedCard;

        /// <summary>跟随宿主"界面字体"的文字控件（WinUI 的 FontFamily 在 Control/TextBlock 上，
        /// Panel 不具备也不沿可视树继承——所以收集显式清单逐个应用）。</summary>
        private readonly List<TextBlock> _fontTargets = new();

        /// <summary>搜索进行中（取消按钮/重复回车守卫）。</summary>
        private bool _searchRunning;

        public BookSourceSearchWindow(
            FrameworkElement themeSource,
            BookSourceSearchService search,
            Func<BookSourceSearchService.BookSearchHit, Task> onOpenBook,
            Func<string, Task>? onOpenDownloaded = null,
            Func<Task>? showUiFontPickerAsync = null,
            Func<string>? getUiFontFamily = null)
        {
            _themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _onOpenBook = onOpenBook ?? throw new ArgumentNullException(nameof(onOpenBook));

            // 未注入时退化为"下载完仍按在线方式打开"，保证调用方不必关心
            _onOpenDownloaded = onOpenDownloaded ?? (_ => Task.CompletedTask);
            _showUiFontPickerAsync = showUiFontPickerAsync;
            _getUiFontFamily = getUiFontFamily;
        }

        // ============================================================
        // 窗口与布局
        // ============================================================

        /// <summary>
        /// 打开搜索窗口（<b>非模态</b>）：创建独立 Window 并激活后立即返回，
        /// 主窗口保持可用（旧版 ContentDialog 模态会锁住主窗口，正是本次改造的起因）。
        /// 重复调用 = 把已有窗口带到前台（不重复开窗）。
        /// </summary>
        public void Show(string? initialKeyword = null)
        {
            if (_window is not null)
            {
                _window.Activate();
                return;
            }

            var palette = LegadoTheme.For(_themeSource);
            _probeQueue = new PriorityWorkQueue(maxConcurrency: 4);

            // ── 头部：关键词输入 + 搜索/取消按钮 ──
            var input = new TextBox
            {
                PlaceholderText = "输入书名或作者（回车搜索）",
                Text = initialKeyword ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var progressText = new TextBlock { Text = string.Empty, Opacity = 0.75, FontSize = 12 };
            var status = new TextBlock
            {
                Text = "就绪。搜索会并发查询所有已启用书源；点选结果可预览简介与第一章。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = palette.TextSecondary,
            };
            _statusText = status;
            _fontTargets.Add(status);
            var searchButton = new Button { Content = "搜索书籍", MinWidth = 96 };
            var cancelButton = new Button
            {
                Content = "取消",
                MinWidth = 64,
                Visibility = Visibility.Collapsed,
            };

            var progressBar = new ProgressBar
            {
                IsIndeterminate = false,
                Visibility = Visibility.Collapsed,
                Height = 3,
            };

            // ── 左列：结果列表 + 简介区 ──
            var resultPanel = new StackPanel { Spacing = 6 };
            var scroller = new ScrollViewer
            {
                Content = resultPanel,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var introTitle = new TextBlock
            {
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = palette.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _introTitle = introTitle;
            _fontTargets.Add(introTitle);
            var introMeta = new TextBlock
            {
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
            };
            _introMeta = introMeta;
            _fontTargets.Add(introMeta);
            var introBody = new TextBlock
            {
                FontSize = 12,
                Foreground = palette.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            _introBody = introBody;
            _fontTargets.Add(introBody);
            var introScroll = new ScrollViewer
            {
                Content = introBody,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                MaxHeight = IntroMaxHeight - 76,
            };
            _introScroll = introScroll;
            var introStack = new StackPanel { Spacing = 4 };
            introStack.Children.Add(introTitle);
            introStack.Children.Add(introMeta);
            introStack.Children.Add(introScroll);
            var introPanel = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = palette.Surface,
                BorderBrush = palette.SurfaceBorder,
                BorderThickness = new Thickness(1),
                Child = introStack,
                Visibility = Visibility.Collapsed,
            };
            _introPanel = introPanel;

            // ── 右列：预览顶栏 + 第一章正文 ──
            var openInHostButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE8A7", FontSize = 14 },   // OpenFile：与宿主"打开"同字形
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(openInHostButton, "在主窗口打开这本书（进入完整阅读）");

            var previewChapterName = new TextBlock
            {
                Text = "第一章预览",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = palette.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _previewChapterName = previewChapterName;
            _fontTargets.Add(previewChapterName);

            var prevPageButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE76B", FontSize = 14 },   // 与宿主 PrevPageButton 同字形
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(prevPageButton, "上一页（预览区向上翻一屏）");
            var nextPageButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE76C", FontSize = 14 },   // 与宿主 NextPageButton 同字形
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(nextPageButton, "下一页（预览区向下翻一屏）");

            var purifyToggle = new ToggleButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE72C", FontSize = 13 },   // Refresh：重新净化渲染的语义
                        new TextBlock { Text = "替换净化", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                IsChecked = true,
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(purifyToggle, "对预览文本应用替换净化规则（引擎净化管线，与主窗口同一套）");

            var fontButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE18F", FontSize = 14 },   // Font：与宿主"界面字体"菜单同字形
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(fontButton, "更改界面字体（复用主窗口顶栏的字体选择；改的是字体族，不是字号）");

            var previewTopBar = new Grid { ColumnSpacing = 6 };
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(openInHostButton, 0);
            Grid.SetColumn(previewChapterName, 1);
            Grid.SetColumn(prevPageButton, 2);
            Grid.SetColumn(nextPageButton, 3);
            Grid.SetColumn(purifyToggle, 4);
            Grid.SetColumn(fontButton, 5);
            previewTopBar.Children.Add(openInHostButton);
            previewTopBar.Children.Add(previewChapterName);
            previewTopBar.Children.Add(prevPageButton);
            previewTopBar.Children.Add(nextPageButton);
            previewTopBar.Children.Add(purifyToggle);
            previewTopBar.Children.Add(fontButton);

            var previewText = new TextBlock
            {
                Text = "点选左侧结果即可在此预览简介与第一章。",
                FontSize = 14,
                LineHeight = 24,
                Foreground = palette.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            _previewText = previewText;
            _fontTargets.Add(previewText);
            var previewScroll = new ScrollViewer
            {
                Content = previewText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(4, 8, 8, 8),
            };
            _previewScroll = previewScroll;

            var previewPanel = new Grid { RowSpacing = 6 };
            previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            previewPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(previewTopBar, 0);
            Grid.SetRow(previewScroll, 1);
            previewPanel.Children.Add(previewTopBar);
            previewPanel.Children.Add(previewScroll);

            // ── body：左右两列 ──
            var body = new Grid { ColumnSpacing = 10 };
            body.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = ResultsMinWidth,
                MaxWidth = 560,
            });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            var leftStack = new Grid { RowSpacing = 8 };
            leftStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            leftStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroller, 0);
            Grid.SetRow(introPanel, 1);
            leftStack.Children.Add(scroller);
            leftStack.Children.Add(introPanel);
            Grid.SetColumn(leftStack, 0);
            Grid.SetColumn(previewPanel, 1);
            body.Children.Add(leftStack);
            body.Children.Add(previewPanel);

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 0);
            Grid.SetColumn(searchButton, 1);
            Grid.SetColumn(cancelButton, 2);
            header.Children.Add(input);
            header.Children.Add(searchButton);
            header.Children.Add(cancelButton);

            var root = new Grid
            {
                RowSpacing = 8,
                Padding = new Thickness(14, 12, 14, 14),
                Background = palette.Surface,
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(header, 0);
            var progressRow = new StackPanel { Spacing = 2 };
            progressRow.Children.Add(progressBar);
            progressRow.Children.Add(progressText);
            Grid.SetRow(progressRow, 1);
            Grid.SetRow(status, 2);
            Grid.SetRow(body, 3);
            root.Children.Add(header);
            root.Children.Add(progressRow);
            root.Children.Add(status);
            root.Children.Add(body);

            // 窗口主题与字体跟随宿主：内容根按宿主实际主题取色（上面 palette），
            // 字体族用宿主"界面字体"设置——选字体后主窗口的 ApplyUiFont 不覆盖本窗口，这里手动跟随
            ApplyWindowFont();

            var window = new Window
            {
                Title = "书源搜索",
                Content = root,
                SystemBackdrop = new MicaBackdrop(),
            };
            _window = window;
            _hostElement = root;

            try
            {
                window.AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));

                // 该 WinAppSDK 版本没有 AppWindow.Center()：按主显示器工作区手动居中
                var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                window.AppWindow.Move(new PointInt32(
                    workArea.X + Math.Max(0, (workArea.Width - WindowWidth) / 2),
                    workArea.Y + Math.Max(0, (workArea.Height - WindowHeight) / 2)));
            }
            catch
            {
                // 尺寸/居中失败不影响功能（默认尺寸也可用）
            }

            // ── 行为接线 ──
            var dispatcher = root.DispatcherQueue;
            int completed = 0, totalSources = 0, hitCount = 0;
            var ranked = new List<(int Score, long Seq, UIElement Card)>();
            long sequence = 0;
            var cardContexts = new List<CardContext>();
            var downloads = new BookSourceDownloadService(_search.Service);

            var runSearch = new Func<Task>(async () =>
            {
                var keyword = input.Text?.Trim() ?? string.Empty;
                if (keyword.Length == 0 || _searchRunning)
                {
                    return;
                }

                _cts?.Cancel();
                _cts?.Dispose();
                var cts = new CancellationTokenSource();
                _cts = cts;

                _searchRunning = true;
                searchButton.IsEnabled = false;
                input.IsEnabled = false;
                cancelButton.Visibility = Visibility.Visible;
                cancelButton.IsEnabled = true;
                cancelButton.Content = "取消";
                completed = 0;
                totalSources = 0;
                hitCount = 0;
                resultPanel.Children.Clear();
                ranked.Clear();
                sequence = 0;
                cardContexts.Clear();
                ClearPreview();
                _selectedCard = null;
                introPanel.Visibility = Visibility.Collapsed;
                progressBar.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                _fontTargets.Add(progressText);
            progressText.Text = string.Empty;
                status.Text = $"正在搜索“{keyword}”…";

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

                                // 相关性降序插入：完全匹配的书名永远在最上面
                                var context = BuildResultCard(hit, downloads, palette, cts.Token);
                                InsertByRelevance(resultPanel, ranked, hit, keyword, context.Card, sequence++);
                                cardContexts.Add(context);
                            }

                            RefreshProgress();
                        });
                    }

                    var groups = await _search.SearchAsync(keyword, null, progress, OnGroup, cts.Token);

                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (totalSources > 0)
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = totalSources;
                    }

                    progressText.Text = $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本";
                    status.Text = hitCount == 0
                        ? $"“{keyword}”没有结果。书源站点可能已失效或改版（{totalSources} 个源全部查询完毕）。"
                        : $"“{keyword}”命中 {hitCount} 本，来自 {groups.Count} 个书源。点选结果预览简介与第一章，点右侧按钮下载整本。";
                }
                catch (OperationCanceledException)
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        status.Text = "搜索已取消。已命中的结果保留在列表里。";
                    }
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        status.Text = $"搜索失败：{ex.Message}";
                    }
                }
                finally
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        _searchRunning = false;
                        searchButton.IsEnabled = true;
                        input.IsEnabled = true;
                        cancelButton.Visibility = Visibility.Collapsed;
                        progressBar.Visibility = Visibility.Collapsed;
                    }
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
            cancelButton.Click += (s, e) =>
            {
                _cts?.Cancel();
                cancelButton.IsEnabled = false;
                cancelButton.Content = "取消中…";
                input.IsEnabled = true;
                input.Focus(FocusState.Programmatic);
            };

            // ── 预览行为 ──
            openInHostButton.Click += async (s, e) =>
            {
                if (_previewSession is not { } session)
                {
                    return;
                }

                // 深链打开：主窗口进入完整阅读（窗口独立，主窗口切换不锁搜索窗）
                var hit = new BookSourceSearchService.BookSearchHit(
                    session.BookName, string.Empty, session.BookUrl,
                    session.Source.BookSourceName, session.Source.BookSourceUrl);
                try
                {
                    await _onOpenBook(hit);
                }
                catch (Exception ex)
                {
                    status.Text = $"打开失败：{ex.Message}";
                }
            };

            prevPageButton.Click += (s, e) => PagePreview(previewScroll, up: true);
            nextPageButton.Click += (s, e) => PagePreview(previewScroll, up: false);

            purifyToggle.Click += async (s, e) =>
            {
                _previewPurify = purifyToggle.IsChecked == true;
                if (_previewRawChapter is null)
                {
                    return;
                }

                previewText.Text = "正在重新渲染…";
                var service = _search.Service;
                var raw = _previewRawChapter;
                var purify = _previewPurify;
                var text = await Task.Run(() => RenderChapterText(service, raw, purify)).ConfigureAwait(true);
                previewText.Text = text;
                previewScroll.ChangeView(null, 0, null, disableAnimation: true);
            };

            fontButton.Click += async (s, e) =>
            {
                // 复用主窗口顶栏右侧的"界面字体"选择（同一套字体选择器与全局设置；
                // 切换的是字体族而非字号）。选完主窗口的 ApplyUiFont 不覆盖本窗口，这里手动跟随。
                if (_showUiFontPickerAsync is null)
                {
                    status.Text = "当前模式未接入字体选择。";
                    return;
                }

                await _showUiFontPickerAsync().ConfigureAwait(true);
                ApplyWindowFont();
            };

            // 窗口关闭 = 取消搜索与全部探测、释放会话（独立窗口无主窗口生命周期牵连）
            window.Closed += (s, e) =>
            {
                _cts?.Cancel();
                _probeQueue?.Dispose();
                foreach (var context in cardContexts)
                {
                    context.Holder.Session?.Dispose();
                }

                _window = null;
                _hostElement = null;
            };

            window.Activate();

            // 带关键词打开 → 立即开搜（fire-and-forget；窗口已激活，主窗口未锁）
            if (!string.IsNullOrWhiteSpace(initialKeyword))
            {
                _ = runSearch();
            }
        }

        // ============================================================
        // 结果卡片
        // ============================================================

        /// <summary>一条结果卡的完整上下文（UI + 探测状态），预览/标记都经它读写。</summary>
        private sealed class CardContext
        {
            public CardContext(
                BookSourceSearchService.BookSearchHit hit,
                Border card,
                LatestChapterHolder holder,
                Grid statusHolder)
            {
                Hit = hit;
                Card = card;
                Holder = holder;
                StatusHolder = statusHolder;
            }

            public BookSourceSearchService.BookSearchHit Hit { get; }

            public Border Card { get; }

            public LatestChapterHolder Holder { get; }

            /// <summary>状态标记槽位（下载按钮左侧）：错误 ❌ / 警告 ⚠ FontIcon 放这里。</summary>
            public Grid StatusHolder { get; }
        }

        /// <summary>
        /// 单条结果的圆角卡片：左两行文字 + 状态标记槽 + 右侧下载按钮。
        /// <para><b>点击 = 预览</b>（插队探测，加载简介与第一章）——旧版"点击直接打开主窗正文"
        /// 的行为移到预览顶栏的"在主窗口打开"按钮上。</para>
        /// </summary>
        private CardContext BuildResultCard(
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

            // 音视频书源：结果上标注「不支持音视频」（Fluent Warning 字形 + Caution 语义色）
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
                Text = "｜排队获取目录…",
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(latestLabel, "点击立即优先加载目录");

            latestLabel.PointerEntered += (s2, e2) => latestLabel.Foreground = palette.TextPrimary;
            latestLabel.PointerExited += (s2, e2) => latestLabel.Foreground = palette.TextSecondary;
            latestLabel.PointerPressed += (s2, e2) =>
            {
                if (!e2.GetCurrentPoint(latestLabel).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                if (hit.IsAudioVideo)
                {
                    e2.Handled = true;
                    return;
                }

                e2.Handled = true;
                // 点击目录 = 该条目立刻优先加载（插队探测；目录已就绪则直接弹出）
                latestHolder.Boost();
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

            // 状态标记槽（下载按钮左侧）：探测产出 ❌/⚠ 后由探测回调填充
            var statusHolder = new Grid { MinWidth = 18, VerticalAlignment = VerticalAlignment.Center };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(statusHolder, 1);
            Grid.SetColumn(downloadButton, 2);
            grid.Children.Add(textStack);
            grid.Children.Add(statusHolder);
            grid.Children.Add(downloadButton);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 8, 8),
                Background = palette.Surface,
                BorderBrush = palette.SurfaceBorder,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = grid,
            };

            var context = new CardContext(hit, card, latestHolder, statusHolder);

            // 点击卡片 = 预览（插队；旧"点击直接打开"移到预览顶栏"在主窗口打开"）
            card.PointerPressed += async (s, e) =>
            {
                if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                if (downloadButton.IsPointerOver)
                {
                    return;
                }

                if (hit.IsAudioVideo)
                {
                    if (_statusText is { } statusText)
                    {
                        statusText.Text = $"「{hit.Name}」来自音视频书源（{hit.SourceName}），本应用只支持文本阅读。";
                    }

                    return;
                }

                SelectCard(card, palette);
                await PreviewHitAsync(context).ConfigureAwait(true);
            };

            // 后台探测（队列限流；产出最新章节名 + ❌/⚠ 状态标记 + 预览缓存）
            if (hit.IsAudioVideo)
            {
                latestLabel.Text = "｜音视频结果，不提供目录";
                latestLabel.IsHitTestVisible = false;
                ToolTipService.SetToolTip(latestLabel, "音视频结果不支持目录/阅读");
                latestHolder.ProbeDone.TrySetResult(false);
            }
            else
            {
                EnqueueProbe(hit, latestHolder, latestLabel, statusHolder, palette, searchToken);
            }

            return context;
        }

        /// <summary>
        /// 音视频结果徽标：Fluent Warning 字形 + Caution 语义色 + 「不支持音视频」。
        /// <para><b>为什么标黄而不是标红</b>：这不是错误，而是产品边界（本应用只做文本阅读）。</para>
        /// </summary>
        private static TextBlock BuildUnsupportedMediaBadge(LegadoPalette palette)
        {
            var badge = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            badge.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "\uE7BA ",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11,
                Foreground = palette.Caution,
            });
            badge.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "不支持音视频",
                Foreground = palette.TextSecondary,
            });

            ToolTipService.SetToolTip(badge, "该结果来自音视频类书源（bookSourceType 1 音频 / 4 视频）。本应用只处理文本，音视频不在支持范围内。");
            return badge;
        }

        // ============================================================
        // 探测队列（目录 + 可访问性 + 第一章缓存）
        // ============================================================

        /// <summary>
        /// 把一条结果加入后台探测队列（优先级 1 = 后台级）。
        /// <para>探测产出三样东西：① 最新章节名（填第二行）；② 状态标记（❌ 无法访问 /
        /// ⚠ 目录空或第一章空）；③ 预览缓存（目录、简介、第一章原文）——点选条目时
        /// 探测被插队，完成后预览<b>零额外网络请求</b>直接渲染。</para>
        /// </summary>
        private void EnqueueProbe(
            BookSourceSearchService.BookSearchHit hit,
            LatestChapterHolder holder,
            TextBlock latestLabel,
            Grid statusHolder,
            LegadoPalette palette,
            CancellationToken token)
        {
            var host = _hostElement;
            var queue = _probeQueue;
            if (host is null || queue is null)
            {
                holder.ProbeDone.TrySetResult(false);
                return;
            }

            var dispatcher = host.DispatcherQueue;

            var work = queue.Enqueue(1, async probeToken =>
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(token, probeToken);
                try
                {
                    var session = _search.CreateSession(hit);
                    holder.Session = session;
                    var opened = await session.OpenAsync(linked.Token).ConfigureAwait(false);

                    var chapters = opened.Chapters;
                    holder.Chapters = chapters;
                    holder.Intro = opened.Info?.Intro;

                    var latest = chapters.Count > 0 ? chapters[^1].ChapterName ?? string.Empty : string.Empty;
                    var latestText = string.IsNullOrWhiteSpace(latest) ? "｜目录为空" : $"｜最新：{latest}";

                    string? problem = null;
                    if (chapters.Count == 0)
                    {
                        problem = "目录为空";
                    }
                    else
                    {
                        // 第一章正文抓取（警告标记的信息来源；同时成为预览缓存）
                        var first = chapters[0];
                        if (string.IsNullOrWhiteSpace(first.ChapterUrl))
                        {
                            problem = "第一章地址为空";
                        }
                        else
                        {
                            try
                            {
                                var content = await _search.Service.Engine.GetContentAsync(
                                    session.Source, first.ChapterUrl, session.Source.BookSourceUrl,
                                    linked.Token).ConfigureAwait(false);
                                var raw = content?.Content;
                                holder.FirstChapterRaw = raw;
                                if (string.IsNullOrWhiteSpace(raw)
                                    || string.IsNullOrWhiteSpace(LegadoHtmlText.ToPlainText(raw)))
                                {
                                    problem = "第一章正文为空";
                                }
                            }
                            catch (Exception ex)
                            {
                                // 目录可达但第一章抓取失败：按"无法访问"处理（章级失败也是访问失败）
                                holder.Error = $"第一章获取失败：{ex.Message}";
                                holder.ProbeDone.TrySetResult(false);
                                dispatcher.TryEnqueue(() =>
                                {
                                    latestLabel.Text = "｜无法获取第一章";
                                    ShowStatusIcon(statusHolder, error: true, palette, holder.Error);
                                });
                                return;
                            }
                        }
                    }

                    holder.ProbeDone.TrySetResult(true);
                    dispatcher.TryEnqueue(() =>
                    {
                        // 下载中显示的是实时进度，别被迟到的探测结果覆盖掉
                        if (latestLabel.Text.Contains("下载中") || latestLabel.Text.Contains("已下载"))
                        {
                            return;
                        }

                        latestLabel.Text = latestText;
                        ToolTipService.SetToolTip(latestLabel, "点击查看该书目录（" + latestText.TrimStart('｜') + "）");
                        ShowStatusIcon(statusHolder, error: false, palette, problem);
                    });
                }
                catch (Exception ex)
                {
                    holder.Error = $"无法访问：{ex.Message}";
                    holder.ProbeDone.TrySetResult(false);
                    dispatcher.TryEnqueue(() =>
                    {
                        latestLabel.Text = "｜无法访问";
                        ToolTipService.SetToolTip(latestLabel, holder.Error);
                        ShowStatusIcon(statusHolder, error: true, palette, holder.Error);
                    });
                }
                finally
                {
                    linked.Dispose();
                }
            });

            holder.BoostHandle = work;
        }

        /// <summary>在状态槽放/清状态图标（❌ 错误 / ⚠ 警告；Fluent 字形 + 语义色，下载按钮左侧）。</summary>
        private static void ShowStatusIcon(Grid statusHolder, bool error, LegadoPalette palette, string? reason)
        {
            statusHolder.Children.Clear();
            if (string.IsNullOrEmpty(reason))
            {
                return;
            }

            var icon = new FontIcon
            {
                Glyph = error ? "\uE783" : "\uE7BA",   // Error / Warning（Fluent）
                FontSize = 13,
                Foreground = error ? palette.Critical : palette.Caution,
            };
            ToolTipService.SetToolTip(icon, (error ? "无法访问：" : "有问题：") + reason);
            statusHolder.Children.Add(icon);
        }

        // ============================================================
        // 预览（简介 + 第一章）
        // ============================================================

        /// <summary>
        /// 点选条目 → 预览：<b>插队探测</b>，完成后左侧简介区展开、右侧渲染第一章。
        /// 探测已完成则零网络直接渲染；进行中则等它（已插队，通常几百毫秒内）。
        /// </summary>
        private async Task PreviewHitAsync(CardContext context)
        {
            var holder = context.Holder;
            var hit = context.Hit;
            holder.Boost();

            var introTitle = _introTitle;
            var introMeta = _introMeta;
            var introBody = _introBody;
            var introScroll = _introScroll;
            var introPanel = _introPanel;
            var previewChapterName = _previewChapterName;
            var previewText = _previewText;
            var previewScroll = _previewScroll;
            if (introTitle is null || introMeta is null || introBody is null || introScroll is null
                || introPanel is null || previewChapterName is null || previewText is null || previewScroll is null)
            {
                return;
            }

            // 左侧简介区立即展开（加载态），右侧清到加载提示
            introPanel.Visibility = Visibility.Visible;
            introTitle.Text = hit.Name + (string.IsNullOrWhiteSpace(hit.Author) ? string.Empty : $"　{hit.Author}");
            introMeta.Text = $"{hit.SourceName}｜加载中…";
            introBody.Text = string.Empty;
            previewChapterName.Text = "第一章预览";
            previewText.Text = "正在加载第一章（已插队优先加载）…";
            previewScroll.ChangeView(null, 0, null, disableAnimation: true);

            // 等探测完成（Boost 已把它提到队首）
            var ok = await holder.ProbeDone.Task.ConfigureAwait(true);
            if (_hostElement is null)
            {
                return;   // 窗口已关闭
            }

            if (!ok)
            {
                introMeta.Text = $"{hit.SourceName}｜无法访问";
                introBody.Text = holder.Error ?? "该条目无法访问（网络/书源失效）";
                previewText.Text = "第一章无法加载：该条目无法访问。";
                return;
            }

            var chapters = holder.Chapters;
            var intro = string.IsNullOrWhiteSpace(holder.Intro) ? "（书源未提供简介）" : holder.Intro!.Trim();
            var latest = chapters is { Count: > 0 } ? $"最新：{chapters[^1].ChapterName}" : "目录为空";
            introMeta.Text = $"{hit.SourceName}｜共 {chapters?.Count ?? 0} 章｜{latest}";
            introBody.Text = intro;
            introScroll.ChangeView(null, 0, null, disableAnimation: true);

            var raw = holder.FirstChapterRaw;
            if (string.IsNullOrWhiteSpace(raw))
            {
                previewChapterName.Text = chapters is { Count: > 0 } ? chapters[0].ChapterName ?? "第一章" : "目录为空";
                previewText.Text = "第一章正文为空（书源问题，见结果条目的警告标记）。";
                return;
            }

            _previewRawChapter = raw;
            _previewChapter = chapters![0];
            _previewSession = holder.Session;
            previewChapterName.Text = chapters[0].ChapterName ?? "第一章";

            previewText.Text = "正在渲染…";
            var service = _search.Service;
            var purify = _previewPurify;
            var content = raw!;
            var text = await Task.Run(() => RenderChapterText(service, content, purify)).ConfigureAwait(true);
            if (_hostElement is null)
            {
                return;
            }

            previewText.Text = string.IsNullOrWhiteSpace(text) ? "第一章正文为空（书源问题）。" : text;
            previewScroll.ChangeView(null, 0, null, disableAnimation: true);
        }

        /// <summary>预览文本渲染：替换净化（引擎管线）→ 纯文本（后台线程执行）。</summary>
        private static string RenderChapterText(BookSourceService service, string raw, bool purify)
        {
            var content = purify ? service.PurifyForDisplay(raw) : raw;
            return LegadoHtmlText.ToPlainText(content);
        }

        /// <summary>把宿主"界面字体"应用到窗口内全部文字控件（字体族，不动字号）。</summary>
        private void ApplyWindowFont()
        {
            if (_getUiFontFamily is not { } getFont || string.IsNullOrWhiteSpace(getFont()))
            {
                return;
            }

            var family = new FontFamily(getFont());
            foreach (var target in _fontTargets)
            {
                target.FontFamily = family;
            }
        }

        private static void PagePreview(ScrollViewer scroller, bool up)
        {
            var page = scroller.ViewportHeight > 0 ? scroller.ViewportHeight * 0.9 : 600;
            var target = up
                ? Math.Max(0, scroller.VerticalOffset - page)
                : Math.Min(scroller.ScrollableHeight, scroller.VerticalOffset + page);
            scroller.ChangeView(null, target, null);
        }

        private void ClearPreview()
        {
            _previewRawChapter = null;
            _previewChapter = null;
            _previewSession = null;
        }

        private void SelectCard(Border card, LegadoPalette palette)
        {
            if (_selectedCard is { } old && _hostElement is { })
            {
                old.BorderBrush = palette.SurfaceBorder;
            }

            _selectedCard = card;
            card.BorderBrush = palette.Accent;
        }

        // ============================================================
        // 下载按钮（三态机，沿用旧版全部行为）
        // ============================================================

        private Button BuildDownloadButton(
            BookSourceSearchService.BookSearchHit hit,
            BookSourceDownloadService downloads,
            TextBlock latestLabel,
            LegadoPalette palette,
            CancellationToken searchToken)
        {
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
                        SetButton(DownloadState.Cancelling, "取消中…", palette.Warning);
                        downloadCts?.Cancel();
                        return;

                    case DownloadState.Cancelling:
                        return;

                    case DownloadState.Done:
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
                    SetButton(DownloadState.Idle, "下载", palette.Accent);
                    latestLabel.Text = $"｜无法下载：{ex.Message}";
                    return;
                }

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
        /// 线性扫描的代价会随列表长度平方增长。</para>
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
        /// 弹出该书目录（探测已缓存的目录，零网络；点开时该条目已插队优先加载）。
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
                        IsHitTestVisible = false,
                    });
                }
            }
            else
            {
                list.Items.Add(new TextBlock
                {
                    Text = holder.Error ?? "目录尚未取到（已插队优先加载，可稍后再点）",
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

            var flyout = new Flyout
            {
                XamlRoot = _hostElement?.XamlRoot ?? anchor.XamlRoot,
                Placement = FlyoutPlacementMode.Bottom,
                Content = panel,
            };

            flyout.ShowAt(anchor);
        }

        /// <summary>
        /// 一条结果卡的探测状态：探测任务写入，预览/目录弹层直接复用（不重复请求）。
        /// </summary>
        private sealed class LatestChapterHolder
        {
            /// <summary>探测完成信号（true = 可访问；false = 无法访问）。预览等它。</summary>
            public TaskCompletionSource<bool> ProbeDone { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<CoreTocItem>? Chapters { get; set; }

            public string? Intro { get; set; }

            /// <summary>第一章原文（书源引擎原始返回）：预览零网络复用。</summary>
            public string? FirstChapterRaw { get; set; }

            public string? Error { get; set; }

            /// <summary>探测队列句柄：用户交互（点选/点目录）时插队。</summary>
            public PriorityWorkQueue.IPriorityWorkHandle? BoostHandle { get; set; }

            public void Boost() => BoostHandle?.Boost();

            /// <summary>探测会话（预览"在主窗口打开"用；窗口关闭时统一 Dispose）。</summary>
            public BookSourceReadingSession? Session { get; set; }
        }

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

        private enum DownloadState { Idle, Downloading, Cancelling, Done }
    }
}

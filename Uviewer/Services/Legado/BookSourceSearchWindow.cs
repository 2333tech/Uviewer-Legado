using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Windows.Graphics;
using Windows.Storage.Streams;
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

        /// <summary>预览图集：页图槽位容器 + "只显示前 N 页"的提示行。</summary>
        private StackPanel? _previewImagePanel;
        private TextBlock? _previewImageHint;

        /// <summary>预览顶栏的「部分下载」（仅图片书源可见）。</summary>
        private Button? _partialDownloadButton;

        /// <summary>预览页图下载的取消源（换条目/关窗/重渲染都作废在途请求）。</summary>
        private CancellationTokenSource? _previewImageCts;

        /// <summary>当前预览的书源是否为图片源（漫画）。</summary>
        private bool _previewIsManga;

        /// <summary>预览页图上限：只下前几页（判断"能不能显示图"够了，不必拉完整章）。</summary>
        private const int PreviewImageLimit = 6;

        /// <summary>
        /// Skia 兜底解码的目标边长（像素）。预览栏宽约 400–700，取 900 让竖版漫画页
        /// 仍有余量放大，同时把 PNG 编码量和内存压在可接受范围。
        /// </summary>
        private const int PreviewImageDecodeSize = 900;

        /// <summary>「部分下载」进行中（重复点击守卫）。</summary>
        private bool _partialDownloadRunning;

        /// <summary>「部分下载」的取消源（关窗即取消）。</summary>
        private CancellationTokenSource? _partialDownloadCts;

        /// <summary>当前预览的原文（书源引擎原始返回）与开关状态。</summary>
        private string? _previewRawChapter;

        /// <summary>当前预览第一章的图片地址（探测时由引擎抽取，图片书源/漫画用）。</summary>
        private List<string> _previewImages = new();
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

        /// <summary>上次使用的搜索范围（类别 / 分组）：窗口内反复搜索时沿用，不必每次重设。</summary>
        private BookSourceKind? _lastKind;
        private string? _lastGroup;

        /// <summary>类别下拉里某个取值对应的索引（找不到 = 0，即「全部类别」）。</summary>
        private static int KindIndexOf(BookSourceKind? kind)
        {
            for (var i = 0; i < BookSourceGroups.KindOptions.Count; i++)
            {
                if (BookSourceGroups.KindOptions[i].Kind == kind)
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>把筛选条件说成人话（状态行用）：如「漫画 · 分组「精选」」，未筛选则为「全部书源」。</summary>
        private static string DescribeScope(BookSourceFilter filter)
        {
            var parts = new List<string>(2);
            if (filter.Kind is { } kind)
            {
                parts.Add(BookSourceGroups.DescribeKind(kind));
            }

            if (!string.IsNullOrWhiteSpace(filter.Group))
            {
                parts.Add($"分组「{filter.Group}」");
            }

            return parts.Count == 0 ? "全部书源" : string.Join(" · ", parts);
        }

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

            // ── 头部：关键词输入 + 类别/分组范围 + 搜索/取消按钮 ──
            var input = new TextBox
            {
                PlaceholderText = "输入书名或作者（回车搜索）",
                Text = initialKeyword ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // 搜索范围：类别（对齐 bookSourceType）+ 自定义分组（bookSourceGroup）。
            // 3637 条启用源全量搜一遍要几十秒，而且结果里混着漫画/音频源；
            // 限定范围既是"只看我要的类型"，也是把一次搜索从几分钟压到几秒的手段。
            var kindBox = new ComboBox
            {
                MinWidth = 118,
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var (label, kind) in BookSourceGroups.KindOptions)
            {
                kindBox.Items.Add(new ComboBoxItem { Content = label, Tag = kind });
            }

            var groupBox = new ComboBox
            {
                MinWidth = 156,
                VerticalAlignment = VerticalAlignment.Center,
            };
            groupBox.Items.Add(new ComboBoxItem { Content = "全部分组", Tag = null });
            foreach (var (groupName, count) in BookSourceGroups.Collect(_search.Service.Sources.GetAll()))
            {
                groupBox.Items.Add(new ComboBoxItem { Content = $"{groupName}（{count}）", Tag = groupName });
            }

            // 恢复上次选择：同一个窗口内反复搜索时，用户不该每轮都重设范围
            kindBox.SelectedIndex = KindIndexOf(_lastKind);
            var lastGroupIndex = 0;
            if (_lastGroup is not null)
            {
                for (var i = 1; i < groupBox.Items.Count; i++)
                {
                    if (groupBox.Items[i] is ComboBoxItem { Tag: string name }
                        && string.Equals(name, _lastGroup, StringComparison.OrdinalIgnoreCase))
                    {
                        lastGroupIndex = i;
                        break;
                    }
                }
            }

            groupBox.SelectedIndex = lastGroupIndex;

            ToolTipService.SetToolTip(kindBox, "只搜索指定类别的书源（文本 / 漫画 / 音频 / 视频 / 文件）");
            ToolTipService.SetToolTip(groupBox, "只搜索属于该分组的书源（分组在「书源管理」里编辑）");

            var progressText = new TextBlock { Text = string.Empty, Opacity = 0.75, FontSize = 12 };
            _fontTargets.Add(progressText);
            var status = new TextBlock
            {
                Text = "就绪。搜索会并发查询所有已启用书源；用顶栏两个下拉可只看某类别/某分组。点选结果可预览简介与第一章。",
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

            // 「部分下载」= 只下前 5 章就打开（图片书源专属；文本书源隐藏）。
            // 与左侧卡片「下载」（整本全部章）刻意分开：漫画一章几十 MB，
            // "先看开头"与"整本离线"是两种完全不同的成本，按钮必须能区分。
            var partialDownloadButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE896", FontSize = 13 },   // Download
                        new TextBlock { Text = "部分下载", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                Padding = new Thickness(8, 4, 8, 4),
                Background = palette.Accent,
                Foreground = palette.OnAccent,
                Visibility = Visibility.Collapsed,
            };
            _partialDownloadButton = partialDownloadButton;
            ToolTipService.SetToolTip(
                partialDownloadButton,
                "只下载前 5 章就直接打开（其余章在阅读时按「前 2 后 5」自动补齐）；想在主窗口离线读整本，请用菜单「下载全本（漫画）」");

            var previewTopBar = new Grid { ColumnSpacing = 6 };
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(openInHostButton, 0);
            Grid.SetColumn(previewChapterName, 1);
            Grid.SetColumn(partialDownloadButton, 2);
            Grid.SetColumn(prevPageButton, 3);
            Grid.SetColumn(nextPageButton, 4);
            Grid.SetColumn(purifyToggle, 5);
            Grid.SetColumn(fontButton, 6);
            previewTopBar.Children.Add(openInHostButton);
            previewTopBar.Children.Add(previewChapterName);
            previewTopBar.Children.Add(partialDownloadButton);
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

            // ── 图片预览（图片书源/漫画的第一章页图） ──
            // 面板恒在，空时 Visibility=Collapsed；每张页图一个固定高度的槽位，
            // 加载完再把 Image 塞进去——这样滚动条长度不会在图片陆续到达时反复跳。
            var previewImageHint = new TextBlock
            {
                FontSize = 11,
                Foreground = palette.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            _previewImageHint = previewImageHint;
            _fontTargets.Add(previewImageHint);

            var previewImagePanel = new StackPanel
            {
                Spacing = 6,
                Visibility = Visibility.Collapsed,
            };
            _previewImagePanel = previewImagePanel;

            // 文本与图片同容器：文本书源只有文本，图片书源通常只有图片（正文里常混着导航噪音）
            var previewContent = new StackPanel { Spacing = 8 };
            previewContent.Children.Add(previewImageHint);
            previewContent.Children.Add(previewText);
            previewContent.Children.Add(previewImagePanel);

            var previewScroll = new ScrollViewer
            {
                Content = previewContent,
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
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 0);
            Grid.SetColumn(kindBox, 1);
            Grid.SetColumn(groupBox, 2);
            Grid.SetColumn(searchButton, 3);
            Grid.SetColumn(cancelButton, 4);
            header.Children.Add(input);
            header.Children.Add(kindBox);
            header.Children.Add(groupBox);
            header.Children.Add(searchButton);
            header.Children.Add(cancelButton);

            var root = new Grid
            {
                RowSpacing = 8,
                Padding = new Thickness(14, 12, 14, 14),
                Background = palette.Surface,
            };

            // ★ 本窗口是独立 Window：它<b>不继承</b>主窗口根元素的 RequestedTheme，
            // 默认按"应用级主题"解析——用户把主窗口切到深色时，这里仍是浅色。
            // 后果不只是底色：FlyoutPresenter / ListView / ScrollBar / Button 这些
            // <b>默认样式</b>全按浅色渲染，而文字色取自 palette（深色主题下是白字），
            // 于是"点最新章节弹出的目录"变成浅底白字，完全不可读。这里显式跟随宿主。
            root.RequestedTheme = _themeSource.ActualTheme;
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

            // ★ 渲染预算（2026-10-22 修"搜索一段时间后整机卡死"）：
            // 用户本地有 3637 个启用源，一次搜索的命中量可达<b>数千到数万</b>条。
            // 旧实现把每条命中都在 UI 线程上建一张卡（≈10 个控件）直接塞进 StackPanel
            // （外层 ScrollViewer + StackPanel = 无虚拟化，全部参与测量/布局），
            // 几万张卡 = 几十万个元素 → UI 线程被锁死几十分钟，主窗口同一线程也一起"未响应"。
            // 现在的口径：
            //   ① 每批只建 RenderBatchSize 张卡，批间让出消息循环（界面持续可交互）；
            //   ② 列表最多保留 MaxRenderedCards 张（按相关度，后来更相关的会顶掉最差的一张）；
            //   ③ 后台探测只对前 MaxProbedCards 张自动发起，其余"点击/预览才探"。
            const int MaxRenderedCards = 300;
            const int MaxProbedCards = 48;
            const int RenderBatchSize = 24;

            var pendingHits = new Queue<BookSourceSearchService.BookSearchHit>();
            var pendingGate = new object();
            var pumpRunning = false;
            var searchDone = true;
            var currentKeyword = string.Empty;
            int rendered = 0, probesStarted = 0, droppedHits = 0;

            int PendingCount()
            {
                lock (pendingGate)
                {
                    return pendingHits.Count;
                }
            }

            void RefreshProgress()
            {
                if (totalSources > 0)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Maximum = totalSources;
                    progressBar.Value = completed;
                }

                var suffix = droppedHits > 0 ? $"（列表只显示相关度最高的 {MaxRenderedCards} 条）" : string.Empty;
                progressText.Text = totalSources > 0
                    ? $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本{suffix}"
                    : "正在准备书源…";
            }

            /// <summary>移除列表末尾（相关度最低）的那张卡，并清掉它的上下文。</summary>
            void DropLastCard()
            {
                if (ranked.Count == 0)
                {
                    return;
                }

                var victim = ranked[^1];
                ranked.RemoveAt(ranked.Count - 1);
                resultPanel.Children.Remove(victim.Card);
                for (var i = cardContexts.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(cardContexts[i].Card, victim.Card))
                    {
                        cardContexts.RemoveAt(i);
                        break;
                    }
                }

                rendered--;
            }

            /// <summary>渲染一条命中（含预算控制）。只在 UI 线程调用。</summary>
            void AddCard(BookSourceSearchService.BookSearchHit hit, CancellationToken token)
            {
                if (rendered >= MaxRenderedCards)
                {
                    // 满额：只有比"当前最差的一张"更相关才进；否则只计数不建控件
                    var incoming = SearchRelevance.Score(hit.Name, hit.Author, currentKeyword);
                    if (ranked.Count == 0
                        || incoming <= ranked[^1].Score
                        || ReferenceEquals(_selectedCard, ranked[^1].Card))
                    {
                        droppedHits++;
                        return;
                    }

                    DropLastCard();
                    droppedHits++;   // 被顶掉的那条也算"省略"，状态行数字才对得上
                }

                var probeNow = probesStarted < MaxProbedCards;
                if (probeNow)
                {
                    probesStarted++;
                }

                var context = BuildResultCard(hit, downloads, palette, token, probeNow);
                InsertByRelevance(resultPanel, ranked, hit, currentKeyword, context.Card, sequence++);
                cardContexts.Add(context);
                rendered++;
            }

            /// <summary>确保渲染泵在跑（幂等；只在 UI 线程调用）。</summary>
            void EnsurePump()
            {
                if (pumpRunning)
                {
                    return;
                }

                pumpRunning = true;
                _ = PumpAsync();
            }

            /// <summary>
            /// 渲染泵：从待渲染队列按批取命中，在 UI 线程建卡，批间让出消息循环。
            /// <para>不自带 CancellationTokenSource：每轮读 <see cref="_cts"/>，因此用户取消后
            /// 再搜一次时，同一个泵会自然接着渲染新搜索的结果（不需要重建泵）。</para>
            /// </summary>
            async Task PumpAsync()
            {
                try
                {
                    while (true)
                    {
                        var cts = _cts;
                        if (cts is null || _hostElement is null)
                        {
                            return;   // 窗口已关闭
                        }

                        if (cts.IsCancellationRequested)
                        {
                            if (searchDone)
                            {
                                return;   // 已取消且没有新搜索接管
                            }

                            await Task.Delay(20);
                            continue;
                        }

                        var batch = new List<BookSourceSearchService.BookSearchHit>(RenderBatchSize);
                        bool drained;
                        lock (pendingGate)
                        {
                            while (batch.Count < RenderBatchSize && pendingHits.Count > 0)
                            {
                                batch.Add(pendingHits.Dequeue());
                            }

                            drained = pendingHits.Count == 0;
                        }

                        foreach (var hit in batch)
                        {
                            if (cts.IsCancellationRequested)
                            {
                                return;
                            }

                            AddCard(hit, cts.Token);
                        }

                        if (batch.Count > 0)
                        {
                            RefreshProgress();
                        }

                        if (drained)
                        {
                            if (searchDone)
                            {
                                return;   // 搜索已结束且队列已空
                            }

                            await Task.Delay(30);   // 还在搜：等下一波命中
                        }
                        else
                        {
                            await Task.Delay(1);    // 还有积压：继续建，但每批都让出消息循环
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 泵内部异常不能冒泡成"未观察的任务异常"（那会让列表无声停止更新）
                    System.Diagnostics.Debug.WriteLine($"Search render pump failed: {ex}");
                }
                finally
                {
                    pumpRunning = false;
                }
            }

            var runSearch = new Func<Task>(async () =>
            {
                var keyword = input.Text?.Trim() ?? string.Empty;
                if (keyword.Length == 0 || _searchRunning)
                {
                    return;
                }

                // 范围在按下搜索的瞬间定型：搜索途中用户再改下拉也不影响这一轮
                // （否则半途改类别，前半段与后半段的候选集不一致，命中数就说不清了）
                var filter = new BookSourceFilter
                {
                    Kind = kindBox.SelectedItem is ComboBoxItem { Tag: BookSourceKind kind } ? kind : null,
                    Group = groupBox.SelectedItem is ComboBoxItem { Tag: string group } ? group : null,
                };
                _lastKind = filter.Kind;
                _lastGroup = filter.Group;
                var scope = DescribeScope(filter);

                _cts?.Cancel();
                _cts?.Dispose();
                var cts = new CancellationTokenSource();
                _cts = cts;

                _searchRunning = true;
                searchButton.IsEnabled = false;
                input.IsEnabled = false;
                kindBox.IsEnabled = false;
                groupBox.IsEnabled = false;
                cancelButton.Visibility = Visibility.Visible;
                cancelButton.IsEnabled = true;
                cancelButton.Content = "取消";
                completed = 0;
                totalSources = 0;
                hitCount = 0;
                currentKeyword = keyword;
                resultPanel.Children.Clear();
                ranked.Clear();
                sequence = 0;

                // 旧一轮结果的探测会话要显式释放（每次搜索最多 MaxProbedCards 个；
                // 不释放的话反复搜索会把会话连同其引擎状态一起攒在内存里）
                foreach (var old in cardContexts)
                {
                    old.Holder.Session?.Dispose();
                }

                cardContexts.Clear();
                lock (pendingGate)
                {
                    pendingHits.Clear();
                }

                rendered = 0;
                probesStarted = 0;
                droppedHits = 0;
                searchDone = false;
                ClearPreview();
                _selectedCard = null;
                introPanel.Visibility = Visibility.Collapsed;
                progressBar.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;
                progressText.Text = string.Empty;
                status.Text = $"正在搜索“{keyword}”（{scope}）…";

                try
                {
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

                    // 工作线程回调：只入队 + 计数，<b>不在</b>这里建控件（建卡一律交给渲染泵）
                    void OnGroup(BookSourceSearchService.SearchGroup group)
                    {
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        lock (pendingGate)
                        {
                            foreach (var hit in group.Hits)
                            {
                                pendingHits.Enqueue(hit);
                            }
                        }

                        Interlocked.Add(ref hitCount, group.Hits.Count);
                        dispatcher.TryEnqueue(EnsurePump);
                    }

                    EnsurePump();

                    var groups = await _search.SearchAsync(keyword, null, progress, OnGroup, cts.Token, filter);

                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    searchDone = true;

                    // 等渲染泵把积压的画完（最多 3s），让状态行与列表内容一致
                    for (var i = 0; i < 100 && (pumpRunning || PendingCount() > 0); i++)
                    {
                        await Task.Delay(30);
                    }

                    if (totalSources > 0)
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = totalSources;
                    }

                    progressText.Text = $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本"
                        + (droppedHits > 0 ? $"（列表只显示相关度最高的 {MaxRenderedCards} 条）" : string.Empty);
                    status.Text = hitCount == 0
                        ? $"“{keyword}”没有结果（范围：{scope}，{totalSources} 个源全部查询完毕）。书源站点可能已失效或改版；可放宽类别/分组后再试。"
                        : droppedHits > 0
                            ? $"“{keyword}”命中 {hitCount} 本（范围：{scope}），来自 {groups.Count} 个书源；列表按相关度只显示前 {MaxRenderedCards} 条（其余 {droppedHits} 条相关度更低，已省略）。点选结果预览简介与第一章，点右侧按钮下载整本。"
                            : $"“{keyword}”命中 {hitCount} 本（范围：{scope}），来自 {groups.Count} 个书源。点选结果预览简介与第一章，点右侧按钮下载整本。";
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
                        searchDone = true;
                        _searchRunning = false;
                        searchButton.IsEnabled = true;
                        input.IsEnabled = true;
                        kindBox.IsEnabled = true;
                        groupBox.IsEnabled = true;
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

            partialDownloadButton.Click += async (s, e) =>
                await StartPartialDownloadAsync(partialDownloadButton, status).ConfigureAwait(true);

            purifyToggle.Click += async (s, e) =>
            {
                _previewPurify = purifyToggle.IsChecked == true;
                if (_previewRawChapter is null)
                {
                    return;
                }

                if (_previewText is { } target)
                {
                    target.Text = "正在重新渲染…";
                }

                // 文本与页图一起重算：图片书源净化后图集可能变（规则有时会把 <img> 挪走）
                await RenderPreviewBodyAsync().ConfigureAwait(true);
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
                // 关窗 = 停掉渲染泵与全部探测（泵每轮检查 _hostElement，置空后自动退出）
                searchDone = true;
                _cts?.Cancel();
                CancelPreviewImages();
                _partialDownloadCts?.Cancel();
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
            CancellationToken searchToken,
            bool probeNow)
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
                // ★ 必须左对齐（而不是默认的拉伸占满 Star 列）：目录弹层是 <c>ShowAt(这个元素)</c>
                // 定位的，元素若被拉满整行，弹层就会贴着整行右边缘出现——用户看到的是
                // "点第一章的文字，弹层却从很远的地方冒出来"（反馈里的"各部件错位"）。
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            ToolTipService.SetToolTip(latestLabel, "点击立即优先加载目录");

            // 状态标记槽（下载按钮左侧）：探测产出 ❌/⚠ 后由探测回调填充
            var statusHolder = new Grid { MinWidth = 18, VerticalAlignment = VerticalAlignment.Center };

            // 惰性探测：卡片数量受限（见 RenderHit 的 MaxProbedCards），预算内的卡片直接开探，
            // 预算外的等用户点它再探。用户点/预览都会调用这里，语义是"最多探一次"。
            void StartProbeOnce()
            {
                if (latestHolder.ProbeStarted)
                {
                    return;
                }

                latestHolder.ProbeStarted = true;
                latestLabel.Text = "｜排队获取目录…";
                ToolTipService.SetToolTip(latestLabel, "点击立即优先加载目录");
                EnqueueProbe(hit, latestHolder, latestLabel, statusHolder, palette, searchToken);
            }

            latestHolder.EnsureProbe = StartProbeOnce;

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
                StartProbeOnce();
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
            else if (probeNow)
            {
                StartProbeOnce();
            }
            else
            {
                // 探测预算用尽：不自动开探（几千条结果逐个探测 = 把所有站点打一遍、UI 也扛不住），
                // 改成"点/预览时才探"，用户不关心的条目零网络开销。
                latestLabel.Text = "｜点击加载目录";
            }

            return context;
        }

        /// <summary>
        /// 音视频结果徽标：Fluent Warning 字形 + Caution 语义色 + 「不支持音视频」。
        /// <para><b>为什么标黄而不是标红</b>：这不是错误，而是产品边界（本应用只做文本阅读）。</para>
        /// <para><b>为什么用 FontIcon 而不是 TextBlock + 内联 Run</b>（2026-10-22 修）：
        /// 在 <c>TextBlock.Inlines</c> 的 <c>Run</c> 上设 <c>FontFamily="Segoe Fluent Icons"</c> 时，
        /// 字形并没有从图示字体取，而是落回界面字体——<c>\uE7BA</c> 在界面字体里没有码位，
        /// 渲染成<b>方框（tofu）</b>，配上 Caution 黄就成了用户看到的"黄方框"。
        /// <see cref="FontIcon"/> 自己持有字形与字体，是本项目所有图标（顶栏/工具条）的统一做法，
        /// 不存在该问题。</para>
        /// </summary>
        private static FrameworkElement BuildUnsupportedMediaBadge(LegadoPalette palette)
        {
            var badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            badge.Children.Add(new FontIcon
            {
                Glyph = "\uE7BA",   // Warning（Fluent）
                FontSize = 11,
                Foreground = palette.Caution,
            });
            badge.Children.Add(new TextBlock
            {
                Text = "不支持音视频",
                FontSize = 12,
                Foreground = palette.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
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
                                holder.FirstChapterImages = content?.ImageUrls;
                                holder.FirstChapterUrl = first.ChapterUrl;

                                // 图片书源（漫画）的"正文"就是一组 <img>：转纯文本必然为空，
                                // 只要抽得到图片地址就算正常——否则每个漫画源都会被标 ⚠"正文为空"。
                                if (string.IsNullOrWhiteSpace(raw))
                                {
                                    problem = "第一章正文为空";
                                }
                                else if (string.IsNullOrWhiteSpace(LegadoHtmlText.ToPlainText(raw))
                                    && (content?.ImageUrls is not { Count: > 0 }))
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

            // 预算外卡片尚未开探：预览一定要探，否则下面 await ProbeDone 会永远等下去
            holder.EnsureProbe?.Invoke();
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

            // 换条目先收起「部分下载」：上一条如果是漫画，按钮不能残留到文本条目上
            _previewIsManga = false;
            _previewImages = new List<string>();
            UpdatePartialDownloadButton();
            CancelPreviewImages();
            _previewImagePanel?.Children.Clear();
            if (_previewImagePanel is { } pendingPanel)
            {
                pendingPanel.Visibility = Visibility.Collapsed;
            }

            if (_previewImageHint is { } pendingHint)
            {
                pendingHint.Visibility = Visibility.Collapsed;
            }

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

            var first = chapters is { Count: > 0 } ? chapters[0] : null;
            var session = holder.Session;
            var raw = holder.FirstChapterRaw;
            var firstImages = holder.FirstChapterImages;
            string? refetchError = null;

            // ── 预览固定「目录第一章」 ──
            // 探测缓存只当加速用。缓存章节与目录第一条不一致、或正文与图片都为空时，
            // 这里补抓一次：用户看到的必须永远是目录第一话，而不是探测那一次的残留
            // （探测可能赶上临时超时/风控，或目录在两次请求之间被站点换了顺序）。
            if (first is not null && session is not null
                && !string.IsNullOrWhiteSpace(first.ChapterUrl)
                && (!string.Equals(holder.FirstChapterUrl, first.ChapterUrl, StringComparison.Ordinal)
                    || (string.IsNullOrWhiteSpace(raw) && firstImages is not { Count: > 0 })))
            {
                previewChapterName.Text = first.ChapterName ?? "第一章";
                previewText.Text = "正在加载第一章（目录第一话）…";

                // 搜索 CTS 可能在"重新搜索"时被换掉并 Dispose：取不到就当不取消，
                // 后面用 _hostElement 判断窗口是否还在（关窗后不再碰 UI）。
                var previewToken = CancellationToken.None;
                try
                {
                    previewToken = _cts?.Token ?? CancellationToken.None;
                }
                catch (ObjectDisposedException)
                {
                    // 旧 CTS 已释放，退化成不取消
                }

                try
                {
                    var refetched = await _search.Service.Engine.GetContentAsync(
                        session.Source, first.ChapterUrl, session.Source.BookSourceUrl,
                        previewToken).ConfigureAwait(true);

                    if (_hostElement is null)
                    {
                        return;
                    }

                    raw = refetched?.Content;
                    firstImages = refetched?.ImageUrls;
                    holder.FirstChapterRaw = raw;
                    holder.FirstChapterImages = firstImages;
                    holder.FirstChapterUrl = first.ChapterUrl;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 补抓失败不吞掉已抓到的缓存：下面照常渲染（可能只是慢/临时失败）
                    System.Diagnostics.Debug.WriteLine($"Preview first chapter refetch failed: {ex.Message}");
                    refetchError = ex.Message;
                }
            }

            if (string.IsNullOrWhiteSpace(raw) && (firstImages is not { Count: > 0 }))
            {
                previewChapterName.Text = chapters is { Count: > 0 } ? chapters[0].ChapterName ?? "第一章" : "目录为空";
                previewText.Text = refetchError is null
                    ? "第一章正文为空（书源问题，见结果条目的警告标记）。"
                    : $"第一章加载失败：{refetchError}";
                return;
            }

            _previewRawChapter = raw ?? string.Empty;
            _previewImages = firstImages ?? new List<string>();
            _previewChapter = chapters![0];
            _previewSession = holder.Session;
            _previewIsManga = holder.Session?.Source.SourceKind == BookSourceKind.Image;
            previewChapterName.Text = chapters[0].ChapterName ?? "第一章";
            UpdatePartialDownloadButton();

            previewText.Text = "正在渲染…";
            await RenderPreviewBodyAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// 渲染预览主体：<b>正文纯文本</b> + <b>图片书源的第一章页图</b>（前 <see cref="PreviewImageLimit"/> 页）。
        /// <para><b>为什么同一个面板要两种能力</b>：搜索结果是混合的——文本书源给文本，
        /// 漫画/图片书源给图。旧实现只有 <c>TextBlock</c>，图片源的正文转纯文本后必然是空的，
        /// 于是"目录能解析但右侧什么都没有"。</para>
        /// <para>净化（几 MB 正则替换）在后台线程做；<b>页图必须走书源引擎的
        /// <c>GetImageAsync</c></b>——它带 Referer 与每源 Cookie，自己开 HttpClient 会被防盗链挡成 403。</para>
        /// </summary>
        private async Task RenderPreviewBodyAsync()
        {
            var raw = _previewRawChapter;
            var text = _previewText;
            var scroll = _previewScroll;
            if (raw is null || text is null || scroll is null)
            {
                return;
            }

            var service = _search.Service;
            var purify = _previewPurify;
            var chapterUrl = _previewChapter?.ChapterUrl ?? _previewSession?.BookUrl ?? string.Empty;
            var known = _previewImages;

            var (plain, images) = await Task.Run(() =>
            {
                var content = purify ? service.PurifyForDisplay(raw) : raw;
                var plainText = LegadoHtmlText.ToPlainText(content ?? string.Empty);

                // 引擎在抓正文时已经抽过图片地址（绝对化 + 去重），优先用它：
                // 净化规则只针对文本，重新从净化后的 HTML 抽一次反而可能少图。
                var urls = known.Count > 0
                    ? new List<string>(known)
                    : RuleEngine.ExtractImageUrls(content ?? string.Empty, chapterUrl);

                if (urls.Count == 0 && !ReferenceEquals(content, raw))
                {
                    urls = RuleEngine.ExtractImageUrls(raw, chapterUrl);
                }

                return (plainText, urls);
            }).ConfigureAwait(true);

            if (_hostElement is null)
            {
                return;
            }

            // 换条目/重渲染：上一轮在途的页图请求立即作废（否则旧图会填进新面板）
            CancelPreviewImages();

            var showImages = images.Count > 0;

            // 纯图章（漫画那一类）的"正文"只有几十字的导航噪音，显示出来只是干扰；
            // 而文本书源里的插图（几百上千字正文 + 一两张图）必须保留正文。
            var imageOnly = showImages && plain.Trim().Length < 60;
            text.Visibility = imageOnly ? Visibility.Collapsed : Visibility.Visible;
            if (!imageOnly)
            {
                text.Text = string.IsNullOrWhiteSpace(plain) ? "第一章正文为空（书源问题）。" : plain;
            }

            var panel = _previewImagePanel;
            var hint = _previewImageHint;
            if (panel is null || hint is null)
            {
                scroll.ChangeView(null, 0, null, disableAnimation: true);
                return;
            }

            panel.Children.Clear();
            panel.Visibility = showImages ? Visibility.Visible : Visibility.Collapsed;

            if (showImages)
            {
                var shown = Math.Min(images.Count, PreviewImageLimit);
                hint.Visibility = images.Count > shown ? Visibility.Visible : Visibility.Collapsed;
                hint.Text = $"第一章共 {images.Count} 页，这里预览前 {shown} 页；在主窗口打开可按页翻阅全部。";
            }
            else
            {
                hint.Visibility = Visibility.Collapsed;
            }

            scroll.ChangeView(null, 0, null, disableAnimation: true);

            if (!showImages)
            {
                return;
            }

            var palette = LegadoTheme.For(_hostElement);
            var slots = new List<Border>();
            var shownCount = Math.Min(images.Count, PreviewImageLimit);
            for (var i = 0; i < shownCount; i++)
            {
                var slot = CreateImageSlot(i + 1, palette);
                slots.Add(slot);
                panel.Children.Add(slot);
            }

            var cts = new CancellationTokenSource();
            _previewImageCts = cts;
            var source = _previewSession?.Source;
            var urls = images.Take(shownCount).ToList();

            // 不 await：文本/骨架先出来，页图逐张填（否则用户要盯着"正在渲染…"等网络）
            _ = LoadPreviewImagesAsync(source, urls, slots, palette, cts.Token);
        }

        /// <summary>
        /// 逐张取页图并填进槽位（<b>串行</b>：预览只要"能看见图"，不值得为它并发打站点）。
        /// <para>取消/换条目时静默退出；单张失败只降级该槽位的文案，不影响其余页。</para>
        /// </summary>
        private async Task LoadPreviewImagesAsync(
            BookSourceModel? source,
            IReadOnlyList<string> urls,
            IReadOnlyList<Border> slots,
            LegadoPalette palette,
            CancellationToken token)
        {
            if (source is null)
            {
                return;
            }

            var engine = _search.Service.Engine;
            for (var i = 0; i < slots.Count && i < urls.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var bytes = await engine.GetImageAsync(source, urls[i], token).ConfigureAwait(true);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (bytes is null || bytes.Length == 0)
                    {
                        SetSlotText(slots[i], $"第 {i + 1} 页取不到（源站拒绝或图片地址失效）", palette);
                        continue;
                    }

                    var bitmap = await DecodePreviewImageAsync(bytes, token).ConfigureAwait(true);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (bitmap is null)
                    {
                        SetSlotText(slots[i], $"第 {i + 1} 页解码失败（格式不支持）", palette);
                        continue;
                    }

                    slots[i].Child = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preview image failed: {ex.Message}");
                    SetSlotText(slots[i], $"第 {i + 1} 页加载失败", palette);
                }
            }
        }

        /// <summary>
        /// 预览页图解码：<b>先走系统 WIC，再走 SkiaSharp 兜底</b>。
        /// <para><b>为什么必须兜底</b>：漫画站点近年普遍发 <c>image/webp</c>（快看是
        /// <c>RIFF/WEBP</c>）。Windows 的 WIC 只在装了 "Webp Image Extensions" 时才认 WebP，
        /// 没装的机器上 <c>BitmapImage.SetSourceAsync</c> 直接抛异常，槽位就只剩
        /// "第 1 页加载失败"——目录能解析、图片地址也正确，却一张都看不见。</para>
        /// <para>兜底复用宿主已有的 <see cref="ArchiveThumbnailEncoder"/>（SkiaSharp 解码 +
        /// 等比缩放 + PNG 编码），后台线程做，预览宽度量级下开销可以接受。</para>
        /// </summary>
        /// <returns>解码好的位图；两条路都失败返回 <c>null</c>（由调用方降级成文案）。</returns>
        private static async Task<ImageSource?> DecodePreviewImageAsync(byte[] bytes, CancellationToken token)
        {
            // ① 系统解码器：jpg/png/gif/bmp 一步到位，不解码两次
            try
            {
                var native = new BitmapImage();
                using (var stream = new MemoryStream(bytes, writable: false))
                using (var randomAccess = stream.AsRandomAccessStream())
                {
                    await native.SetSourceAsync(randomAccess).AsTask(token).ConfigureAwait(true);
                }

                // 有的机器不抛异常但解出 0×0（缺 codec 的另一副面孔），同样视为失败
                if (native.PixelWidth > 0 && native.PixelHeight > 0)
                {
                    return native;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview native decode failed (fallback to skia): {ex.Message}");
            }

            if (token.IsCancellationRequested)
            {
                return null;
            }

            // ② SkiaSharp 转码：WebP / AVIF 之类系统缺 codec 的格式
            var png = await Task.Run(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    return ArchiveThumbnailEncoder.Encode(stream, PreviewImageDecodeSize, token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preview skia decode failed: {ex.Message}");
                    return null;
                }
            }, token).ConfigureAwait(true);

            if (png is null || png.Length == 0 || token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var converted = new BitmapImage();
                using (var stream = new MemoryStream(png, writable: false))
                using (var randomAccess = stream.AsRandomAccessStream())
                {
                    await converted.SetSourceAsync(randomAccess).AsTask(token).ConfigureAwait(true);
                }

                return converted;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview converted decode failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>页图槽位：固定最小高度占位，图片陆续到达时滚动条不会反复跳。</summary>
        private static Border CreateImageSlot(int pageNumber, LegadoPalette palette) =>
            new()
            {
                MinHeight = 180,
                CornerRadius = new CornerRadius(6),
                Background = palette.Surface,
                BorderBrush = palette.SurfaceBorder,
                BorderThickness = new Thickness(1),
                Child = BuildSlotText($"第 {pageNumber} 页 加载中…", palette),
            };

        private static void SetSlotText(Border slot, string message, LegadoPalette palette) =>
            slot.Child = BuildSlotText(message, palette);

        private static TextBlock BuildSlotText(string message, LegadoPalette palette) =>
            new()
            {
                Text = message,
                FontSize = 12,
                Foreground = palette.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
            };

        /// <summary>作废在途的预览页图请求（换条目 / 重渲染 / 关窗 / 清空预览）。</summary>
        private void CancelPreviewImages()
        {
            var cts = _previewImageCts;
            _previewImageCts = null;
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已自行结束，忽略
            }

            cts.Dispose();
        }

        /// <summary>「部分下载」按钮只对图片书源（漫画）出现。</summary>
        private void UpdatePartialDownloadButton()
        {
            if (_partialDownloadButton is { } button)
            {
                button.Visibility = _previewIsManga ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ============================================================
        // 部分下载（漫画：只下前 5 章就打开）
        // ============================================================

        /// <summary>
        /// 「部分下载」：只把<b>前 <see cref="MangaLibrary.InitialChapterImageCount"/> 章</b>的图片打成章包，
        /// 然后立刻在主窗口打开。
        ///
        /// <para><b>为什么要单独一个按钮</b>：卡片上的「下载」是"整本下完才打开"（章节多时几十分钟），
        /// 而用户点开一本漫画通常只想先看开头。把两条成本完全不同的路径做成两个按钮，
        /// 比在同一个按钮上猜用户意图更可靠。</para>
        ///
        /// <para><b>打开方式是"在线书源"而不是"离线清单"</b>（关键）：离线清单
        /// （<c>OpenMangaManifestAsync</c>）不联网，翻到未下载的章只会提示"尚未下载"。
        /// 用户要求的是"之后阅读时的下载规则遵循现有的跳转章节后下载前后"——
        /// 必须走 <c>OpenBookAsync</c> 的在线链路：已下好的章零网络直读，
        /// 未下的章由前 2 后 5 窗口按需补齐。</para>
        ///
        /// <para><b>左侧目录</b>：打开后由阅读控制器把资源管理器定位到该漫画文件夹
        /// （<c>NavigateExplorerToMangaFolder</c>），用户直接看到每章的 CBZ。</para>
        /// </summary>
        private async Task StartPartialDownloadAsync(Button button, TextBlock status)
        {
            if (_partialDownloadRunning)
            {
                return;
            }

            var session = _previewSession;
            if (session is null || _previewChapter is null)
            {
                status.Text = "请先点选一本漫画，再点「部分下载」。";
                return;
            }

            var source = session.Source;
            if (source.SourceKind != BookSourceKind.Image)
            {
                status.Text = "「部分下载」只适用于漫画（图片书源）。";
                return;
            }

            _partialDownloadRunning = true;
            var original = button.Content;
            button.IsEnabled = false;
            button.Content = "准备目录…";

            var cts = new CancellationTokenSource();
            _partialDownloadCts = cts;
            var token = cts.Token;

            try
            {
                var downloads = new MangaDownloadService(_search.Service);

                // 与主窗口打开走同一个构造函数 ⇒ 落到同一个漫画文件夹（书源名 + 书籍 URL 短哈希）
                using var manga = new MangaReadingSession(
                    _search.Service, source, session.BookUrl, downloads.MangaRoot);

                status.Text = "正在获取漫画目录…";
                var opened = await manga.OpenOnlineAsync(token).ConfigureAwait(true);
                if (opened.Chapters.Count == 0)
                {
                    status.Text = "漫画目录为空，无法部分下载（书源可能已失效）。";
                    return;
                }

                var plan = Math.Min(MangaLibrary.InitialChapterImageCount, opened.Chapters.Count);
                var progress = new Progress<MangaDownloadService.MangaDownloadProgress>(p =>
                {
                    if (_hostElement is not null && !token.IsCancellationRequested)
                    {
                        status.Text =
                            $"部分下载前 {plan} 章：{p.ChaptersDone}/{p.ChaptersTotal} 章　{p.ChapterName}";
                    }
                });

                var result = await downloads
                    .DownloadAsync(manga, MangaLibrary.InitialChapterImageCount, progress, token)
                    .ConfigureAwait(true);

                if (!result.Success)
                {
                    status.Text = result.Message;
                    return;
                }

                status.Text = "部分下载完成，正在主窗口打开…";

                // 深链打开：主窗口按"在线漫画"建会话（同一文件夹），首页零网络直读已下好的章
                var hit = new BookSourceSearchService.BookSearchHit(
                    string.IsNullOrWhiteSpace(session.BookName) ? "漫画" : session.BookName,
                    string.Empty,
                    session.BookUrl,
                    source.BookSourceName,
                    source.BookSourceUrl ?? string.Empty);

                await _onOpenBook(hit).ConfigureAwait(true);
                status.Text = $"已在主窗口打开（左侧目录已切到该漫画文件夹，前 {plan} 章已下载）";
            }
            catch (OperationCanceledException)
            {
                status.Text = "部分下载已取消。";
            }
            catch (Exception ex)
            {
                status.Text = $"部分下载失败：{ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_partialDownloadCts, cts))
                {
                    _partialDownloadCts = null;
                }

                cts.Dispose();

                _partialDownloadRunning = false;
                if (_hostElement is not null)
                {
                    button.Content = original;
                    button.IsEnabled = true;
                }
            }
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
            CancelPreviewImages();
            _previewRawChapter = null;
            _previewImages = new List<string>();
            _previewChapter = null;
            _previewSession = null;
            _previewIsManga = false;
            _previewImagePanel?.Children.Clear();
            if (_previewImagePanel is { } panel)
            {
                panel.Visibility = Visibility.Collapsed;
            }

            if (_previewImageHint is { } hint)
            {
                hint.Visibility = Visibility.Collapsed;
            }

            if (_previewText is { } text)
            {
                text.Visibility = Visibility.Visible;
            }

            UpdatePartialDownloadButton();
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

        /// <summary>
        /// 漫画卡片的「下载」：<b>整本</b>——把清单里所有章节的图片打成 CBZ，全部完成后再在主窗口打开。
        ///
        /// <para><b>与预览顶栏「部分下载」的分工</b>（用户明确要求）：</para>
        /// <list type="bullet">
        /// <item>本按钮 = 全部章节下完才打开（"我要整本离线"）；</item>
        /// <item>「部分下载」= 只前 5 章立刻打开（"我先看开头"）。</item>
        /// </list>
        /// <para>下载中按钮变「取消」（与文本书源的按钮同一套三态语义）；取消保留已完成的章包。</para>
        /// </summary>
        private Button BuildMangaDownloadButton(
            BookSourceSearchService.BookSearchHit hit,
            BookSourceModel source,
            TextBlock latestLabel,
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
            ToolTipService.SetToolTip(
                button,
                "下载整本漫画（全部章节的图片，按章打包为 CBZ）后打开；只想先看开头几章，请用右侧预览顶栏的「部分下载」");

            CancellationTokenSource? downloadCts = null;
            var state = DownloadState.Idle;

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
                        // 已下载：再点 = 直接打开（不重复下载）
                        await _onOpenBook(hit).ConfigureAwait(true);
                        return;

                    default:
                        await StartAsync();
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

            async Task StartAsync()
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(searchToken);
                downloadCts = cts;
                SetButton(DownloadState.Downloading, "取消", palette.Warning);

                try
                {
                    var downloads = new MangaDownloadService(_search.Service);

                    // 与主窗口打开共用构造函数 ⇒ 同一漫画文件夹（书源名 + 书籍 URL 短哈希）
                    using var session = new MangaReadingSession(
                        _search.Service, source, hit.BookUrl, downloads.MangaRoot);

                    latestLabel.Text = "｜正在获取漫画目录…";
                    var opened = await session.OpenOnlineAsync(cts.Token).ConfigureAwait(true);
                    if (opened.Chapters.Count == 0)
                    {
                        SetButton(DownloadState.Idle, "重试", palette.Accent);
                        latestLabel.Text = "｜目录为空，无法下载";
                        return;
                    }

                    var progress = new Progress<MangaDownloadService.MangaDownloadProgress>(p =>
                        latestLabel.Text = string.Format(
                            CultureInfo.CurrentCulture,
                            "｜下载中 {0}/{1} 章  {2}",
                            p.ChaptersDone,
                            p.ChaptersTotal,
                            p.ChapterName));

                    var result = await downloads
                        .DownloadAllAsync(session, progress, cts.Token)
                        .ConfigureAwait(true);

                    if (result.Cancelled)
                    {
                        SetButton(DownloadState.Idle, "下载", palette.Accent);
                        latestLabel.Text = "｜已取消下载（已下好的章保留）";
                        return;
                    }

                    if (!result.Success)
                    {
                        SetButton(DownloadState.Idle, "重试", palette.Accent);
                        latestLabel.Text = $"｜{result.Message}";
                        return;
                    }

                    SetButton(DownloadState.Done, "已下载", palette.Success);
                    latestLabel.Text = $"｜{result.Message}";

                    // 在线方式打开（同一文件夹）：已下好的章零网络直读，未下的章按前 2 后 5 补
                    await _onOpenBook(hit).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    SetButton(DownloadState.Idle, "下载", palette.Accent);
                    latestLabel.Text = "｜已取消下载（已下好的章保留）";
                }
                catch (Exception ex)
                {
                    SetButton(DownloadState.Idle, "重试", palette.Accent);
                    latestLabel.Text = $"｜下载失败：{ex.Message}";
                }
                finally
                {
                    cts.Dispose();
                    if (ReferenceEquals(downloadCts, cts))
                    {
                        downloadCts = null;
                    }
                }
            }

            return button;
        }

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

            // 漫画（图片书源）：文本下载链路完全不适用（正文转文本为空、图集没有落盘形态），
            // 走漫画自己的"全部章节打成 CBZ"链路，下完再打开。
            var source = _search.Service.Sources.GetByUrl(hit.SourceUrl);
            if (source?.SourceKind == BookSourceKind.Image)
            {
                return BuildMangaDownloadButton(hit, source, latestLabel, palette, searchToken);
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
        /// <para><b>底色/文字/布局为什么全部显式指定</b>（2026-10-22 修）：
        /// <see cref="Flyout"/> 的 <c>FlyoutPresenter</c> 底色来自<b>默认样式</b>的主题资源，
        /// 而弹层放在 Popup 里，主题不保证跟随本窗口；实测深色窗口里弹出的是浅色底，
        /// 配 palette 的白字 → 完全不可读。这里三件事一起做，任一层失效都不影响可读性：
        /// ① 内容外包一层 <see cref="Border"/>，底色/描边用 palette 显式给（不依赖主题资源）；
        /// ② 该 Border 的 <c>RequestedTheme</c> 跟随锚点元素，让内部的 ListView 滚动条、
        ///    指针悬停高亮也走同一套主题；③ 列表给<b>确定高度</b>——ListView 在"无限高度"
        ///    下会退化成全量实现，几千章的目录会把 UI 线程锁死。</para>
        /// </summary>
        private void ShowChapterListFlyout(
            FrameworkElement anchor,
            LatestChapterHolder holder,
            BookSourceSearchService.BookSearchHit hit,
            LegadoPalette palette)
        {
            var xamlRoot = _hostElement?.XamlRoot ?? anchor.XamlRoot;
            if (xamlRoot is null)
            {
                return;
            }

            const double rowHeight = 36;
            const double maxListHeight = 324;

            var panel = new StackPanel { Spacing = 6 };

            panel.Children.Add(new TextBlock
            {
                Text = $"目录 · {hit.Name}",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = palette.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });

            if (holder.Chapters is { Count: > 0 } chapters)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"共 {chapters.Count} 章　·　点击条目外的空白可关闭",
                    FontSize = 11,
                    Foreground = palette.TextSecondary,
                });

                var list = new ListView
                {
                    SelectionMode = ListViewSelectionMode.None,
                    IsItemClickEnabled = false,
                    Padding = new Thickness(0),
                    // 固定高度（而非 MaxHeight）：ListView 需要确定的可视高度才会虚拟化，
                    // 否则 5000 章的目录会一次性实例化 5000 个 TextBlock → 弹层卡死。
                    Height = Math.Min(Math.Max(chapters.Count, 3) * rowHeight, maxListHeight),
                };

                foreach (var chapter in chapters)
                {
                    list.Items.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(chapter.ChapterName) ? "(无标题)" : chapter.ChapterName,
                        FontSize = 13,
                        Foreground = palette.TextPrimary,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false,
                    });
                }

                panel.Children.Add(list);
            }
            else
            {
                // 目录未就绪/为空：只放一段说明，不塞 ListView（空列表还会占一个滚动区）
                panel.Children.Add(new TextBlock
                {
                    Text = holder.Error ?? "目录尚未取到（已插队优先加载，可稍后再点）",
                    FontSize = 12,
                    Foreground = palette.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 296,
                });
            }

            var content = new Border
            {
                Width = 320,
                Padding = new Thickness(12, 10, 12, 12),
                Background = palette.Surface,
                BorderBrush = palette.SurfaceBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = panel,
                // 弹层内部控件（滚动条/悬停高亮）也跟随锚点实际主题
                RequestedTheme = anchor.ActualTheme,
            };

            var flyout = new Flyout
            {
                XamlRoot = xamlRoot,
                Placement = FlyoutPlacementMode.Bottom,
                Content = content,
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

            /// <summary>第一章抽取到的图片地址（图片书源/漫画）：预览图集的来源。</summary>
            public List<string>? FirstChapterImages { get; set; }

            /// <summary>
            /// 上面两份缓存对应的<b>章节地址</b>（不是书名 URL）。
            /// <para>预览要求"固定第一章"：只有地址与目录第一条一致时缓存才算数，
            /// 否则（探测期间目录变了、缓存是别的章）预览会自己补抓。</para>
            /// </summary>
            public string? FirstChapterUrl { get; set; }

            public string? Error { get; set; }

            /// <summary>探测队列句柄：用户交互（点选/点目录）时插队。</summary>
            public PriorityWorkQueue.IPriorityWorkHandle? BoostHandle { get; set; }

            /// <summary>
            /// 惰性探测入口（卡片构造时注入，语义"最多探一次"）。
            /// <para>只有探测预算内的卡片在渲染时立即调用；预算外的等用户点"目录"或点选预览时才探。</para>
            /// </summary>
            public Action? EnsureProbe { get; set; }

            /// <summary>探测是否已发起（幂等守卫：点目录 + 点选预览可能先后触发）。</summary>
            public bool ProbeStarted { get; set; }

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

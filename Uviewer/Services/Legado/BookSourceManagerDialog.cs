using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BookSource.Core.Models;
using BookSource.Core.Services;
using BookSource.Core.Services.BookSources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BookSourceModel = BookSource.Core.Models.BookSource;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源管理器：图形化查看 / 编辑已导入的全部书源条目。
    ///
    /// <para><b>布局</b>：左侧条目列表（名称 + 分组 + 能力徽标），右侧按
    /// 「基本信息 / 搜索 / 发现 / 详情 / 目录 / 正文 / 脚本」分组展开的编辑表单。
    /// 字段用<strong>反射从规则模型生成</strong>而不是手写——模型加字段时表单自动跟上，
    /// 不必两处同步维护（这是本文件唯一的"通用机制"，各字段不重复写样板）。</para>
    ///
    /// <para><b>编辑模型</b>：进入编辑时对书源做<strong>深拷贝</strong>，取消不会污染内存中的
    /// 书源（内存是搜索/阅读的权威，被误改会直接影响阅读）。保存时才写回并落盘。</para>
    ///
    /// <para><b>能力徽标</b>：列表项右侧标出该源是否依赖阶段 4（java.*/WebView），
    /// 让用户一眼看出哪些源当前引擎读不了，而不是打开后对着空目录猜。</para>
    /// </summary>
    public sealed class BookSourceManagerDialog
    {
        private readonly FrameworkElement _rootElement;
        private readonly BookSourceService _service;
        private readonly Func<string, Task> _onImportRequested;

        /// <summary>当前编辑中的副本（null = 未选中/未在编辑）。</summary>
        private BookSourceModel? _draft;

        /// <summary>当前选中项对应的原始书源（保存时用它定位覆盖）。</summary>
        private BookSourceModel? _selected;

        public BookSourceManagerDialog(
            FrameworkElement rootElement,
            BookSourceService service,
            Func<string, Task> onImportRequested)
        {
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _onImportRequested = onImportRequested ?? throw new ArgumentNullException(nameof(onImportRequested));
        }

        /// <summary>弹出管理器。<paramref name="importPathJson"/> 预填的导入对话框参数（内部使用）。</summary>
        public async Task ShowAsync()
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Width = 280,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var detailTitle = new TextBlock
            {
                Text = "← 从左侧选择一个书源查看与编辑",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };

            var detailSubtitle = new TextBlock
            {
                Text = string.Empty,
                Opacity = 0.7,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };

            var formHost = new StackPanel { Spacing = 8 };
            var detailPanel = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0) };
            detailPanel.Children.Add(detailTitle);
            detailPanel.Children.Add(detailSubtitle);
            detailPanel.Children.Add(formHost);

            var saveButton = new Button { Content = "保存", IsEnabled = false };
            var deleteButton = new Button { Content = "删除", IsEnabled = false };
            var importButton = new Button { Content = "导入 JSON…", IsEnabled = true };
            var closeButton = new Button { Content = "关闭" };

            var summary = new TextBlock
            {
                Text = string.Empty,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
            };

            var grid = new Grid { ColumnSpacing = 0 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(list, 0);
            Grid.SetColumn(detailPanel, 1);
            grid.Children.Add(list);
            grid.Children.Add(detailPanel);

            var root = new StackPanel { Spacing = 10, Width = 900 };
            root.Children.Add(summary);
            root.Children.Add(grid);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttonRow.Children.Add(importButton);
            buttonRow.Children.Add(saveButton);
            buttonRow.Children.Add(deleteButton);

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(root);
            panel.Children.Add(buttonRow);

            var dialog = new ContentDialog
            {
                XamlRoot = _rootElement.XamlRoot,
                RequestedTheme = _rootElement.ActualTheme,
                Title = "书源管理",
                Content = new ScrollViewer
                {
                    Content = panel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto,
                    MaxHeight = 620,
                },
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            // ── 列表 ──────────────────────────────────────────────
            void RefreshList(string? selectUrl = null)
            {
                list.Items.Clear();
                foreach (var src in _service.Sources.GetAll())
                {
                    list.Items.Add(new ListViewItem
                    {
                        Content = BuildListItemContent(src),
                        Tag = src.BookSourceUrl,
                    });
                }

                var all = _service.Sources.GetAll();
                var enabled = all.Count(s => s.IsEnabled);
                summary.Text = $"共 {all.Count} 个书源，已启用 {enabled} 个";

                if (selectUrl is not null)
                {
                    foreach (var item in list.Items.OfType<ListViewItem>())
                    {
                        if (item.Tag as string == selectUrl)
                        {
                            list.SelectedItem = item;
                            break;
                        }
                    }
                }
            }

            // ── 编辑表单生成 ──────────────────────────────────────
            void ShowDetail(BookSourceModel? source)
            {
                _selected = source;
                formHost.Children.Clear();
                saveButton.IsEnabled = source is not null;
                deleteButton.IsEnabled = source is not null;

                if (source is null)
                {
                    _draft = null;
                    detailTitle.Text = "← 从左侧选择一个书源查看与编辑";
                    detailSubtitle.Text = string.Empty;
                    return;
                }

                _draft = Clone(source);
                detailTitle.Text = source.BookSourceName;
                var caps = BookSourceCapabilities.Assess(source);
                detailSubtitle.Text = caps == BookSourceCapability.None || caps == BookSourceCapability.HasJsLib
                    ? $"URL: {source.BookSourceUrl}　·　当前引擎可完整支持"
                    : $"URL: {source.BookSourceUrl}　·　⚠ 需要完整 JavaScript 支持（{Describe(caps)}）";

                // 基本信息：书源 URL 只读（它是主键，改了等于换一条源）
                var basic = new StackPanel { Spacing = 6 };
                basic.Children.Add(Header("基本信息"));
                basic.Children.Add(Field("书源名称", _draft.BookSourceName, v => _draft!.BookSourceName = v));
                basic.Children.Add(Field("书源 URL（主键，不可改）", _draft.BookSourceUrl, null));
                basic.Children.Add(Field("分组", _draft.BookSourceGroup, v => _draft!.BookSourceGroup = v));
                basic.Children.Add(Field("书籍 URL 正则", _draft.BookUrlPattern, v => _draft!.BookUrlPattern = v));
                basic.Children.Add(Field("并发速率", _draft.ConcurrentRate, v => _draft!.ConcurrentRate = v));
                basic.Children.Add(Field("请求头（JSON）", _draft.Header, v => _draft!.Header = v, multi: true));
                basic.Children.Add(Field("权重（排序用）", _draft.Weight.ToString(), v =>
                    _draft!.Weight = int.TryParse(v, out var w) ? w : 0));
                basic.Children.Add(Field("备注", _draft.BookSourceComment, v => _draft!.BookSourceComment = v, multi: true));
                basic.Children.Add(Toggle("启用", _draft.IsEnabled, async (s, val) =>
                {
                    // 启用开关是高频操作（批量禁用失效源），立即落盘而不必点"保存"
                    _draft!.Enabled = val;
                    await _service.SetSourceEnabledAsync(_draft, val);
                    RefreshList(_draft.BookSourceUrl);
                }));
                formHost.Children.Add(basic);

                // 规则分组：反射生成，模型加字段自动跟上
                formHost.Children.Add(RuleGroup("搜索规则", "searchUrl", () => _draft!.SearchUrl,
                    v => _draft!.SearchUrl = v, () => _draft!.RuleSearch ??= new SearchRule(), r => _draft!.RuleSearch = r));

                formHost.Children.Add(RuleGroup("发现/分类规则", "exploreUrl", () => _draft!.ExploreUrl,
                    v => _draft!.ExploreUrl = v, () => _draft!.RuleExplore ??= new ExploreRule(), r => _draft!.RuleExplore = r));

                formHost.Children.Add(RuleGroup("详情页规则", null, null, null,
                    () => _draft!.RuleBookInfo ??= new BookInfoRule(), r => _draft!.RuleBookInfo = r));

                formHost.Children.Add(RuleGroup("目录规则", null, null, null,
                    () => _draft!.RuleToc ??= new TocRule(), r => _draft!.RuleToc = r));

                formHost.Children.Add(RuleGroup("正文规则", null, null, null,
                    () => _draft!.RuleContent ??= new ContentRule(), r => _draft!.RuleContent = r));

                // 脚本与登录
                var scripts = new StackPanel { Spacing = 6 };
                scripts.Children.Add(Header("脚本与登录"));
                scripts.Children.Add(Field("jsLib（库代码）", _draft.JsLib, v => _draft!.JsLib = v, multi: true, tall: true));
                scripts.Children.Add(Field("登录地址", _draft.LoginUrl, v => _draft!.LoginUrl = v));
                scripts.Children.Add(Field("登录校验脚本", _draft.LoginCheckJs, v => _draft!.LoginCheckJs = v, multi: true));
                scripts.Children.Add(Toggle("启用 Cookie 保持", _draft.EnabledCookieJar ?? false,
                    (s, val) => _draft!.EnabledCookieJar = val));
                scripts.Children.Add(Field("登录界面脚本", _draft.LoginUi, v => _draft!.LoginUi = v, multi: true));
                formHost.Children.Add(scripts);
            }

            list.SelectionChanged += (s, e) =>
            {
                var item = list.SelectedItem as ListViewItem;
                var url = item?.Tag as string;
                ShowDetail(url is null ? null : _service.Sources.GetByUrl(url));
            };

            // ── 保存 ──────────────────────────────────────────────
            saveButton.Click += async (s, e) =>
            {
                if (_draft is null)
                {
                    return;
                }

                try
                {
                    await _service.SaveSourceAsync(_draft);
                    _selected = _draft;
                    ShowDetail(_draft);
                    RefreshList(_draft.BookSourceUrl);
                    summary.Text = $"已保存：{_draft.BookSourceName}";
                }
                catch (Exception ex)
                {
                    summary.Text = $"保存失败：{ex.Message}";
                }
            };

            deleteButton.Click += async (s, e) =>
            {
                if (_selected is null)
                {
                    return;
                }

                var name = _selected.BookSourceName;
                var confirm = new ContentDialog
                {
                    XamlRoot = _rootElement.XamlRoot,
                    RequestedTheme = _rootElement.ActualTheme,
                    Title = "删除书源",
                    Content = $"确定删除“{name}”吗？该源的阅读进度也会一并清除。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };

                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                await _service.RemoveSourceAsync(_selected.BookSourceUrl);
                ShowDetail(null);
                RefreshList();
            };

            importButton.Click += async (s, e) =>
            {
                dialog.Hide();
                await _onImportRequested(string.Empty);
            };

            RefreshList();
            ShowDetail(null);
            await dialog.ShowAsync();

            // 对话框关闭后刷新一次（导入可能在外面改动了书源集）
            RefreshList();
        }

        // ============================================================
        // 通用 UI 构件（各字段/各规则组共用，不重复实现）
        // ============================================================

        private static TextBlock Header(string text) => new()
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
        };

        /// <summary>
        /// 生成一个文本字段。<paramref name="commit"/> 为 null 时字段只读（用于展示主键等信息）。
        /// </summary>
        private static UIElement Field(
            string label, string? value, Action<string>? commit, bool multi = false, bool tall = false)
        {
            var box = new TextBox
            {
                Text = value ?? string.Empty,
                AcceptsReturn = multi,
                TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                IsReadOnly = commit is null,
                Height = tall ? 120 : (multi ? 64 : double.NaN),
                MinHeight = multi ? 56 : double.NaN,
                IsEnabled = commit is not null,
            };

            if (commit is not null)
            {
                box.TextChanged += (s, e) =>
                {
                    var newText = box.Text;
                    commit(newText);
                };
            }

            return new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 },
                    box,
                },
            };
        }

        private static UIElement Toggle(string label, bool value, Action<object, bool> onChange)
        {
            var toggle = new ToggleSwitch
            {
                Header = label,
                IsOn = value,
                OnContent = "开",
                OffContent = "关",
            };

            // ToggleSwitch 无 Click 事件，用 Toggled（切换即提交）
            toggle.Toggled += (s, e) => onChange(toggle, toggle.IsOn);
            return toggle;
        }

        /// <summary>
        /// 生成一个规则分组的编辑表单：可选的 URL 字段 + 反射枚举出的全部字符串规则字段。
        /// </summary>
        private static UIElement RuleGroup<T>(
            string title,
            string? urlLabel,
            Func<string?>? getUrl,
            Action<string>? setUrl,
            Func<T> ensureRule,
            Action<T> assignRule) where T : class
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(Header(title));

            if (urlLabel is not null && getUrl is not null && setUrl is not null)
            {
                panel.Children.Add(Field(urlLabel, getUrl(), v => setUrl(v), multi: true));
            }

            var rule = ensureRule();

            // 反射：枚举规则对象上的全部可读可写 string 属性，一次生成完表单。
            // 模型新增字段时表单自动出现，无需改本文件。
            foreach (var prop in rule.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite)
                {
                    continue;
                }

                var p = prop;
                // 长文本（JS/正则/HTML 相关字段）用多行框，其余单行
                var isLong = p.Name.EndsWith("Js", StringComparison.Ordinal)
                    || p.Name.Contains("Rule", StringComparison.Ordinal)
                    || p.Name.Contains("Regex", StringComparison.Ordinal)
                    || p.Name is "Init" or "Content" or "BookList" or "ChapterList";

                panel.Children.Add(Field(
                    p.Name,
                    (string?)p.GetValue(rule),
                    v => p.SetValue(rule, v),
                    multi: isLong));
            }

            // 点"保存"时把规则对象写回书源（draft 持有同一实例，此处保证非空即可）
            panel.Tag = assignRule;
            assignRule(rule);
            return panel;
        }

        private static object BuildListItemContent(BookSourceModel source)
        {
            var caps = BookSourceCapabilities.Assess(source);
            var supported = caps == BookSourceCapability.None || caps == BookSourceCapability.HasJsLib;

            var badge = new TextBlock
            {
                Text = supported ? "" : "⚠",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0x7B, 0x22)),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var group = string.IsNullOrWhiteSpace(source.BookSourceGroup)
                ? ""
                : $"　·　{source.BookSourceGroup}";

            var title = new TextBlock
            {
                Text = source.BookSourceName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = source.IsEnabled
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = source.IsEnabled ? 1.0 : 0.5,
            };

            var subtitle = new TextBlock
            {
                Text = $"{(source.IsEnabled ? "已启用" : "已停用")}{group}",
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(title);
            text.Children.Add(subtitle);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(badge, 1);
            row.Children.Add(text);
            row.Children.Add(badge);
            return row;
        }

        private static string Describe(BookSourceCapability caps)
        {
            var parts = new List<string>(4);
            if (caps.HasFlag(BookSourceCapability.RequiresJsEngine)) parts.Add("java.*");
            if (caps.HasFlag(BookSourceCapability.RequiresWebView)) parts.Add("WebView");
            if (caps.HasFlag(BookSourceCapability.RequiresLogin)) parts.Add("登录");
            if (caps.HasFlag(BookSourceCapability.RequiresRemoteJsLib)) parts.Add("远程 jsLib");
            return string.Join("/", parts);
        }

        /// <summary>
        /// 深拷贝书源（进编辑时用）。逐个规则对象重建，避免在表单里改字段时
        /// 直接污染内存中正在被搜索/阅读使用的实例。
        /// </summary>
        private static BookSourceModel Clone(BookSourceModel source) => new()
        {
            BookSourceUrl = source.BookSourceUrl,
            BookSourceName = source.BookSourceName,
            BookSourceGroup = source.BookSourceGroup,
            BookSourceType = source.BookSourceType,
            BookUrlPattern = source.BookUrlPattern,
            CustomOrder = source.CustomOrder,
            Enabled = source.Enabled,
            EnabledExplore = source.EnabledExplore,
            EnabledCookieJar = source.EnabledCookieJar,
            JsLib = source.JsLib,
            ConcurrentRate = source.ConcurrentRate,
            Header = source.Header,
            LoginUrl = source.LoginUrl,
            LoginUi = source.LoginUi,
            LoginCheckJs = source.LoginCheckJs,
            CoverDecodeJs = source.CoverDecodeJs,
            BookSourceComment = source.BookSourceComment,
            VariableComment = source.VariableComment,
            Weight = source.Weight,
            ExploreUrl = source.ExploreUrl,
            ExploreScreen = source.ExploreScreen,
            SearchUrl = source.SearchUrl,
            EventListener = source.EventListener,
            CustomButton = source.CustomButton,
            HomepageModules = source.HomepageModules,
            LoadWithBaseUrl = source.LoadWithBaseUrl,
            SingleUrl = source.SingleUrl,
            RuleSearch = source.RuleSearch is null ? null : CopySearch(source.RuleSearch),
            RuleBookInfo = source.RuleBookInfo is null ? null : CopyBookInfo(source.RuleBookInfo),
            RuleToc = source.RuleToc is null ? null : CopyToc(source.RuleToc),
            RuleContent = source.RuleContent is null ? null : CopyContent(source.RuleContent),
            RuleExplore = source.RuleExplore is null ? null : CopyExplore(source.RuleExplore),
        };

        // 各规则类的逐字段拷贝。规则字段较多，用对象初始化器逐个显式赋值——
        // 比反射拷贝更快（一次性、无分配）且新增字段时编译器会提醒（漏拷不会静默）。
        private static SearchRule CopySearch(SearchRule s) => new()
        {
            CheckKeyWord = s.CheckKeyWord,
            BookList = s.BookList,
            Name = s.Name,
            Author = s.Author,
            Intro = s.Intro,
            Kind = s.Kind,
            LastChapter = s.LastChapter,
            UpdateTime = s.UpdateTime,
            BookUrl = s.BookUrl,
            CoverUrl = s.CoverUrl,
            WordCount = s.WordCount,
        };

        private static BookInfoRule CopyBookInfo(BookInfoRule s) => new()
        {
            Init = s.Init,
            Name = s.Name,
            Author = s.Author,
            Intro = s.Intro,
            Kind = s.Kind,
            LastChapter = s.LastChapter,
            UpdateTime = s.UpdateTime,
            CoverUrl = s.CoverUrl,
            WordCount = s.WordCount,
            TocUrl = s.TocUrl,
            CanReName = s.CanReName,
            DownloadUrls = s.DownloadUrls,
        };

        private static TocRule CopyToc(TocRule s) => new()
        {
            PreUpdateJs = s.PreUpdateJs,
            Init = s.Init,
            ChapterList = s.ChapterList,
            ChapterName = s.ChapterName,
            ChapterUrl = s.ChapterUrl,
            FormatJs = s.FormatJs,
            IsVolume = s.IsVolume,
            IsVip = s.IsVip,
            IsPay = s.IsPay,
            UpdateTime = s.UpdateTime,
            NextTocUrl = s.NextTocUrl,
        };

        private static ContentRule CopyContent(ContentRule s) => new()
        {
            Content = s.Content,
            Title = s.Title,
            NextContentUrl = s.NextContentUrl,
            WebJs = s.WebJs,
            SourceRegex = s.SourceRegex,
            ReplaceRegex = s.ReplaceRegex,
            ImageStyle = s.ImageStyle,
            ImageDecode = s.ImageDecode,
        };

        /// <summary>ExploreRule 继承自 SearchRule（无额外字段），按 SearchRule 字段拷贝。</summary>
        private static ExploreRule CopyExplore(ExploreRule s) => new()
        {
            CheckKeyWord = s.CheckKeyWord,
            BookList = s.BookList,
            Name = s.Name,
            Author = s.Author,
            Intro = s.Intro,
            Kind = s.Kind,
            LastChapter = s.LastChapter,
            UpdateTime = s.UpdateTime,
            BookUrl = s.BookUrl,
            CoverUrl = s.CoverUrl,
            WordCount = s.WordCount,
        };
    }
}
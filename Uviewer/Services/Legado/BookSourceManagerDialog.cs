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
        /// <summary>
        /// 滚动区期望高度（px）。
        /// <para>书源详情表单很长（基本信息 + 5 个规则分组 + 脚本登录，通常 30+ 个字段），
        /// 给足高度可减少滚动；实际值会被 <see cref="LegadoDialogLayout"/> 按窗口高度收敛，
        /// 避免小窗口下对话框超出屏幕。</para>
        /// </summary>
        private const double PreferredContentHeight = 620;
        private readonly FrameworkElement _rootElement;
        private readonly BookSourceService _service;
        private readonly Func<string, Task> _onImportRequested;

        /// <summary>提示回调（消息 / 图标 / 颜色），用于把失败原因告诉用户。</summary>
        private readonly Action<string, string, string> _notify;

        /// <summary>当前编辑中的副本（null = 未选中/未在编辑）。</summary>
        private BookSourceModel? _draft;

        /// <summary>当前选中项对应的原始书源（保存时用它定位覆盖）。</summary>
        private BookSourceModel? _selected;

        public BookSourceManagerDialog(
            FrameworkElement rootElement,
            BookSourceService service,
            Func<string, Task> onImportRequested,
            Action<string, string, string>? notify = null)
        {
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _onImportRequested = onImportRequested ?? throw new ArgumentNullException(nameof(onImportRequested));

            // 提示回调可选：没给就用调试输出，不影响主流程
            _notify = notify ?? ((msg, _, _) => System.Diagnostics.Debug.WriteLine($"Legado manager: {msg}"));
        }

        /// <summary>弹出管理器。<paramref name="importPathJson"/> 预填的导入对话框参数（内部使用）。</summary>
        public async Task ShowAsync()
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,

                // 固定宽度：列表内容（名称/分组/徽标/启用复选框）不需要更多空间，
                // 把余量留给右侧编辑区（书源 URL、规则正则都是长文本，可读性优先）。
                // 320 是加了复选框后的宽度：名称列 + 徽标(⚠) + 复选框放得下且不挤。
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
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

            // 反射生成表单的宿主：必须 Stretch，否则里面的 TextBox 会按内容固有宽度撑开并溢出
            var formHost = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var detailPanel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(12, 0, 0, 0),

                // 关键：跟随 Star 列伸缩。原先这里没约束，编辑区会按内容撑开并溢出对话框。
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
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

            var root = new StackPanel
            {
                Spacing = 10,

                // 不再写死 Width：容器跟随对话框宽度，窗口窄时由 Star 列自然收缩。
                // 原先写死 900 而对话框没设 MaxWidth（默认 548，可用内容仅 500），
                // 右侧编辑区被裁掉 400px —— 表现就是"点开书源后编辑框超出窗口"。
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
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

            var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(root);
            panel.Children.Add(buttonRow);

            // 左侧列表固定 320（名称/分组/徽标/启用复选框），右侧编辑区吃掉剩余宽度
            const double ListWidth = 320;
            const double DesiredContentWidth = ListWidth + 12 + 560;

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

                    // 横向不滚：内容整体已按对话框宽度收敛，再横滚只会让人以为布局坏了
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                },
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            // 加大窗口以装下内容（而非缩小内容尺寸），并按实际窗口收敛
            LegadoDialogLayout.Fit(dialog, _rootElement, DesiredContentWidth);
            if (dialog.Content is ScrollViewer viewer)
            {
                viewer.MaxHeight = LegadoDialogLayout.ScrollViewportHeight(_rootElement, PreferredContentHeight);
            }

            // ── 列表 ──────────────────────────────────────────────
            void RefreshList(string? selectUrl = null)
            {
                list.Items.Clear();
                foreach (var src in _service.Sources.GetAll())
                {
                    list.Items.Add(new ListViewItem
                    {
                        Content = BuildListItemContent(src, RefreshList),
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
                IsEnabled = commit is not null,
            };

            // MinHeight 只在多行时给。
            // 刻意不写 `MinHeight = multi ? 56 : double.NaN`：WinUI 的 MinHeight setter
            // 收到 NaN 会抛 ArgumentException（Height 允许 NaN 表示"自动"，Min/Max 不允许），
            // 于是每次构建单行字段都崩 —— 点开任意书源即致命错误。
            if (multi)
            {
                box.MinHeight = 56;
            }

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

        /// <summary>
        /// 构建书源列表项内容：左侧名称/状态，中间能力徽标（⚠），右侧启用复选框。
        /// <para>启用复选框放在徽标<b>右侧</b>：徽标是"这条源当前引擎读不了"的警示，
        /// 复选框是"这条源要不要参与搜索/阅读"的开关，两者语义不同、位置分开才不会误读。
        /// 勾选变化立即落盘（与详情页的"启用"开关一致），不必再点保存。</para>
        /// </summary>
        private UIElement BuildListItemContent(BookSourceModel source, Action<string?> refreshList)
        {
            var caps = BookSourceCapabilities.Assess(source);
            var supported = caps == BookSourceCapability.None || caps == BookSourceCapability.HasJsLib;

            var badge = new TextBlock
            {
                Text = supported ? "" : "⚠",

                // ⚠ 是语义化 Emoji（表示"当前引擎读不了"），允许用彩色以示警示；
                // 文字类内容一律灰度（见 LegadoTheme）。这里取一个在亮/暗底上都够醒目的橙。
                Foreground = LegadoTheme.Rgb(0xE8, 0x8A, 0x00),
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

            // 启用复选框：勾选 = 启用该书源（即时落盘）
            var enableBox = new CheckBox
            {
                IsChecked = source.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32,
            };
            ToolTipService.SetToolTip(enableBox, source.IsEnabled ? "已启用，点击停用" : "已停用，点击启用");

            enableBox.Checked += async (s, e) => await ToggleEnabledAsync(source, true, refreshList);
            enableBox.Unchecked += async (s, e) => await ToggleEnabledAsync(source, false, refreshList);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(badge, 1);
            Grid.SetColumn(enableBox, 2);
            row.Children.Add(text);
            row.Children.Add(badge);
            row.Children.Add(enableBox);
            return row;
        }

        /// <summary>
        /// 复选框切换启用状态并立即落盘。
        /// <para>失败时刷新列表：复选框会按存储里的真实状态重建，
        /// 避免出现"勾着但其实没生效"的错位（那比报错更难排查）。</para>
        /// </summary>
        private async Task ToggleEnabledAsync(BookSourceModel source, bool enabled, Action<string?> refreshList)
        {
            try
            {
                await _service.SetSourceEnabledAsync(source, enabled);

                // 列表项的"已启用/已停用"文字与字重也要同步更新
                refreshList(source.BookSourceUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Toggle source enabled failed: {ex.Message}");
                _notify($"切换启用状态失败：{ex.Message}", "\uE783", "Red");
                refreshList(source.BookSourceUrl);
            }
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
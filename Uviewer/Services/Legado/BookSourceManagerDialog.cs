using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

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

        /// <summary>
        /// 列表每批填充条数。
        /// <para><b>为什么必须分批</b>：用户实际导入的书源是 3600+ 条（27 MB JSON），
        /// 一次性在 UI 线程为每条构建 ListViewItem（Grid + 2 TextBlock + 徽标 + 复选框，
        /// 单条约 5 个控件）＝ 近两万个控件同步实例化，窗口在此期间完全无响应，
        /// 表现就是"点开书源管理直接卡死"。分批 + 批间让出消息循环后，
        /// 列表渐进出现，窗口始终可交互（滚轮/关闭按钮都能响应）。</para>
        /// </summary>
        private const int ListBatchSize = 50;

        private readonly FrameworkElement _rootElement;
        private readonly BookSourceService _service;
        private readonly Func<string, Task> _onImportRequested;

        /// <summary>列表行内需要局部刷新的控件（按书源 URL 索引）。
        /// <para><b>为什么需要</b>：勾选启用、保存单条书源后若能只改这一行的文字/字重，
        /// 就不必重建整张表——否则 3600+ 行的重建会让每次点复选框都卡几秒。</para>
        /// </summary>
        private readonly Dictionary<string, RowVisuals> _rows = new(StringComparer.Ordinal);

        /// <summary>汇总行（"共 N 个…／当前筛选命中 M 个"），由 <see cref="ShowAsync"/> 装配。</summary>
        private TextBlock? _summary;

        /// <summary>当前列表里显示的书源（= 筛选结果），汇总行与删除后的计数都读它。</summary>
        private List<BookSourceModel> _visible = new();

        /// <summary>当前生效的筛选条件（保存后判断这一行是否还该留在列表里）。</summary>
        private BookSourceFilter _filter = new();

        /// <summary>列表控件（成员方法 <see cref="RemoveRow"/> 用；局部变量在别的成员里拿不到）。</summary>
        private ListView? _list;

        /// <summary>提示回调（消息 / 图标 / 颜色），用于把失败原因告诉用户。</summary>
        private readonly Action<string, string, string> _notify;

        /// <summary>当前编辑中的副本（null = 未选中/未在编辑）。</summary>
        private BookSourceModel? _draft;

        /// <summary>当前选中项对应的原始书源（保存时用它定位覆盖）。</summary>
        private BookSourceModel? _selected;

        /// <summary>一行列表项里会被局部刷新的控件引用（见 <see cref="_rows"/>）。</summary>
        private sealed class RowVisuals
        {
            public TextBlock Title = null!;
            public TextBlock Subtitle = null!;
            public TextBlock Badge = null!;
            public CheckBox Toggle = null!;
        }

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

            _list = list;

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

            var summary = new TextBlock
            {
                Text = string.Empty,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _summary = summary;

            // ── 筛选栏：关键词 + 类别 + 分组 ──────────────────────
            // 3637 条源没有筛选等于不可用（找一个源要滚上几分钟），所以筛选栏必须常驻，
            // 而不是藏在某个"更多"里。三者是「与」关系（见 BookSourceFilter）。
            var filterBox = new TextBox
            {
                PlaceholderText = "搜索 名称 / URL / 分组 / 备注（也认“漫画”“音频”等类别名）",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 6),
            };

            var kindBox = new ComboBox
            {
                MinWidth = 132,
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var (label, kind) in BookSourceGroups.KindOptions)
            {
                kindBox.Items.Add(new ComboBoxItem { Content = label, Tag = kind });
            }
            kindBox.SelectedIndex = 0;
            ToolTipService.SetToolTip(kindBox, "按 bookSourceType 筛选：文本(0) / 漫画(2) / 音频(1) / 视频(4) / 文件(3)");

            var groupBox = new ComboBox
            {
                MinWidth = 168,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(groupBox, "按 bookSourceGroup 筛选（一条源可属于多个分组）");

            var resetFilterButton = new Button { Content = "重置筛选", VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(resetFilterButton, "清空关键词、类别与分组筛选");

            // 批量分组：3637 条源逐条点开改分组不现实，这里把"当前筛选命中的全部书源"
            // 一次性加入/移出某个分组（落盘只写一次，见 BookSourceService.SaveSourcesAsync）。
            var batchGroupButton = new Button
            {
                Content = "批量分组…",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ToolTipService.SetToolTip(batchGroupButton, "把当前筛选命中的书源批量加入/移出某个分组（可先筛选再批量，如“类别=漫画”）");

            // ── 固定两栏高度 = 对话框可视高度 ──────────────────────
            // 关键：ListView 若拿到"无限高度"（例如被外层 ScrollViewer 包着），
            // ItemsStackPanel 会退化成"全部实现"，3600+ 行一次性参与测量/布局 —— 这是
            // 卡死的第二个来源。给它确定高度后只布局可视区，滚轮也才正常。
            // 减去汇总行（~20）+ 筛选栏（~40）+ 间距（10）+ 底部按钮行（~32）+ 余量，
            // 避免按钮被对话框裁掉。
            var viewport = LegadoDialogLayout.ScrollViewportHeight(_rootElement, PreferredContentHeight);
            var paneHeight = Math.Max(200, viewport - 132);
            list.Height = paneHeight;

            // 右侧编辑区自滚：表单通常 30+ 字段，比列表更高，不能靠外层 ScrollViewer
            var detailScroller = new ScrollViewer
            {
                Content = detailPanel,
                Height = paneHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,

                // 滚动条不压住输入框：右内边距留出滚动条宽度
                Padding = new Thickness(0, 0, 12, 0),
            };

            var grid = new Grid { ColumnSpacing = 0 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(list, 0);
            Grid.SetColumn(detailScroller, 1);
            grid.Children.Add(list);
            grid.Children.Add(detailScroller);

            var filterRow = new Grid { ColumnSpacing = 6 };
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(kindBox, 0);
            Grid.SetColumn(groupBox, 1);
            Grid.SetColumn(resetFilterButton, 2);
            Grid.SetColumn(batchGroupButton, 3);
            filterRow.Children.Add(kindBox);
            filterRow.Children.Add(groupBox);
            filterRow.Children.Add(resetFilterButton);
            filterRow.Children.Add(batchGroupButton);

            var filterStack = new StackPanel { Spacing = 0 };
            filterStack.Children.Add(filterBox);
            filterStack.Children.Add(filterRow);

            var root = new StackPanel
            {
                Spacing = 10,

                // 不再写死 Width：容器跟随对话框宽度，窗口窄时由 Star 列自然收缩。
                // 原先写死 900 而对话框没设 MaxWidth（默认 548，可用内容仅 500），
                // 右侧编辑区被裁掉 400px —— 表现就是"点开书源后编辑框超出窗口"。
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            root.Children.Add(summary);
            root.Children.Add(filterStack);
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

                // 内容不再外包 ScrollViewer：列表与编辑区各自滚动（各自高度已固定为 viewport）。
                // 外层再套一层滚动会把"无限高度"重新传给列表，虚拟化失效（见上面 list.Height 注释）。
                Content = panel,
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            // 加大窗口以装下内容（而非缩小内容尺寸），并按实际窗口收敛
            LegadoDialogLayout.Fit(dialog, _rootElement, DesiredContentWidth);

            // ── 列表 ──────────────────────────────────────────────
            // 选中目标 URL 用闭包变量而不是参数：分批填充跨多个 await，参数会被每批覆盖
            string? selectedUrl = null;

            // 关窗即取消所有在途的列表重建（否则用户关窗后仍在后台建几千个控件）
            using var windowCts = new CancellationTokenSource();

            // "我们自己在改选中项"（重建分组下拉 / 重置筛选）时不要触发一轮列表重建
            var suppressFilterEvents = false;

            BookSourceFilter ReadFilter() => new()
            {
                Keyword = filterBox.Text,
                Kind = kindBox.SelectedItem is ComboBoxItem { Tag: BookSourceKind kind } ? kind : null,
                Group = groupBox.SelectedItem is ComboBoxItem { Tag: string group } ? group : null,
            };

            // 分组下拉的选项来自数据本身（不维护独立的分组清单：组是书源上的字符串字段，
            // 用户随时可能在编辑框里手写新组名，独立清单必然与实际数据漂移）
            void RebuildGroupOptions()
            {
                var keep = groupBox.SelectedItem is ComboBoxItem { Tag: string g } ? g : null;

                suppressFilterEvents = true;
                try
                {
                    groupBox.Items.Clear();
                    groupBox.Items.Add(new ComboBoxItem { Content = "全部分组", Tag = null });

                    var selectedIndex = 0;
                    var i = 1;
                    foreach (var (name, count) in BookSourceGroups.Collect(_service.Sources.GetAll()))
                    {
                        groupBox.Items.Add(new ComboBoxItem { Content = $"{name}（{count}）", Tag = name });
                        if (keep is not null && string.Equals(name, keep, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                        }

                        i++;
                    }

                    groupBox.SelectedIndex = selectedIndex;
                }
                finally
                {
                    suppressFilterEvents = false;
                }
            }

            void AddRow(BookSourceModel src, SourceCapabilityReport report)
            {
                var (content, visuals) = BuildRowContent(src, report);
                var item = new ListViewItem { Content = content, Tag = src.BookSourceUrl };
                list.Items.Add(item);

                if (!string.IsNullOrEmpty(src.BookSourceUrl))
                {
                    _rows[src.BookSourceUrl] = visuals;
                }

                if (selectedUrl is not null && src.BookSourceUrl == selectedUrl)
                {
                    list.SelectedItem = item;
                }
            }

            // 重建整张列表：按当前筛选条件取子集 → 后台并行评估能力 → 分批（ListBatchSize）填充控件。
            // 绝不能退回"一次 foreach 全量 Add"：3600+ 条会重新把 UI 线程锁死几十秒。
            async Task RefreshListAsync(string? selectUrl, CancellationToken token)
            {
                var filter = ReadFilter();
                _filter = filter;
                selectedUrl = selectUrl;

                list.Items.Clear();
                _rows.Clear();
                summary.Text = "正在筛选书源…";

                // 筛选 + 能力评估一起放线程池：关键词匹配本身很便宜（3637 × 4 个字符串），
                // 但它紧挨着逐条能力评估（3600 条 ≈ 15 MB 正则/字符串扫描），
                // 分两次 Task.Run 只多一次线程切换，没有收益。
                var (matched, reports) = await Task.Run(() =>
                {
                    var items = filter.Apply(_service.Sources.GetAll());
                    var results = new SourceCapabilityReport[items.Count];
                    Parallel.For(0, items.Count, i =>
                    {
                        results[i] = BookSourceCapabilities.AssessCached(items[i]);
                    });

                    return (items, results);
                }, token).ConfigureAwait(true);

                _visible = matched;

                if (matched.Count == 0)
                {
                    // 空结果要明说"是筛选筛空的"，否则用户会以为书源被删光了
                    list.Items.Add(new ListViewItem
                    {
                        Content = new TextBlock
                        {
                            Text = "没有符合条件的书源。清空关键词或点「重置筛选」即可看到全部。",
                            FontSize = 12,
                            Opacity = 0.6,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        IsHitTestVisible = false,
                    });

                    UpdateSummaryText();
                    return;
                }

                for (var start = 0; start < matched.Count; start += ListBatchSize)
                {
                    token.ThrowIfCancellationRequested();

                    var end = Math.Min(start + ListBatchSize, matched.Count);
                    for (var i = start; i < end; i++)
                    {
                        AddRow(matched[i], reports[i]);
                    }

                    // 每批让出一次消息循环：列表渐进出现，渲染与输入处理都能插进来
                    await Task.Delay(1, token).ConfigureAwait(true);
                    summary.Text = $"正在加载 {end}/{matched.Count} 个书源…";
                }

                UpdateSummaryText();
            }

            // 每次重建各自一个 CTS：连续输入/连点类别时，后一轮取消前一轮（否则几轮填充交错写同一个列表）
            CancellationTokenSource? reloadCts = null;

            async Task ReloadAsync(string? selectUrl)
            {
                reloadCts?.Cancel();
                reloadCts?.Dispose();
                var cts = CancellationTokenSource.CreateLinkedTokenSource(windowCts.Token);
                reloadCts = cts;

                try
                {
                    await RefreshListAsync(selectUrl, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 被更新的一轮取代，或用户关窗：正常
                }
                catch (Exception ex)
                {
                    summary.Text = $"书源列表加载失败：{ex.Message}";
                }
            }

            // 关键词是逐字输入的：每敲一个字就重建 3637 行纯属浪费，停手 250ms 再刷
            CancellationTokenSource? debounceCts = null;

            async Task DebouncedReloadAsync()
            {
                debounceCts?.Cancel();
                debounceCts?.Dispose();
                var cts = CancellationTokenSource.CreateLinkedTokenSource(windowCts.Token);
                debounceCts = cts;

                try
                {
                    await Task.Delay(250, cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await ReloadAsync(null);
            }

            filterBox.TextChanged += (_, _) =>
            {
                if (suppressFilterEvents)
                {
                    return;
                }

                _ = DebouncedReloadAsync();
            };

            kindBox.SelectionChanged += (_, _) =>
            {
                if (!suppressFilterEvents)
                {
                    _ = ReloadAsync(null);
                }
            };

            groupBox.SelectionChanged += (_, _) =>
            {
                if (!suppressFilterEvents)
                {
                    _ = ReloadAsync(null);
                }
            };

            resetFilterButton.Click += (_, _) =>
            {
                suppressFilterEvents = true;
                try
                {
                    filterBox.Text = string.Empty;
                    kindBox.SelectedIndex = 0;
                    groupBox.SelectedIndex = 0;
                }
                finally
                {
                    suppressFilterEvents = false;
                }

                _ = ReloadAsync(null);
            };

            batchGroupButton.Click += async (_, _) => await ApplyBatchGroupAsync();

            // 批量分组：作用于"当前筛选命中的书源"（先筛选类别/分组再批量，是 3637 条源下
            // 唯一现实的整理方式）。加入/移除都以分组串文本为准，重复加入不会产生重复项。
            async Task ApplyBatchGroupAsync()
            {
                var targets = _visible.ToList();
                if (targets.Count == 0)
                {
                    summary.Text = "当前筛选没有命中任何书源，无可操作对象。";
                    return;
                }

                var nameBox = new TextBox
                {
                    PlaceholderText = "分组名（输入新名即新建分组）",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

                var pickBox = new ComboBox
                {
                    PlaceholderText = "或选择已有分组…",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                foreach (var (groupName, count) in BookSourceGroups.Collect(_service.Sources.GetAll()))
                {
                    pickBox.Items.Add(new ComboBoxItem { Content = $"{groupName}（{count}）", Tag = groupName });
                }

                pickBox.SelectionChanged += (_, _) =>
                {
                    if (pickBox.SelectedItem is ComboBoxItem { Tag: string picked })
                    {
                        nameBox.Text = picked;
                    }
                };

                var scopeNote = new TextBlock
                {
                    Text = _filter.IsEmpty
                        ? $"⚠ 当前没有筛选条件，将作用于全部 {targets.Count} 个书源。建议先按类别/关键词筛选。"
                        : $"将作用于当前筛选命中的 {targets.Count} 个书源。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                };

                var addRadio = new RadioButton
                {
                    Content = "加入该分组（已在组内的跳过）",
                    IsChecked = true,
                    GroupName = "batchGroupAction",
                };
                var removeRadio = new RadioButton
                {
                    Content = "从该分组移除",
                    GroupName = "batchGroupAction",
                };

                var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
                panel.Children.Add(scopeNote);
                panel.Children.Add(nameBox);
                panel.Children.Add(pickBox);
                panel.Children.Add(addRadio);
                panel.Children.Add(removeRadio);

                var batchDialog = new ContentDialog
                {
                    XamlRoot = _rootElement.XamlRoot,
                    RequestedTheme = _rootElement.ActualTheme,
                    Title = "批量分组",
                    Content = panel,
                    PrimaryButtonText = "应用",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };
                LegadoDialogLayout.Fit(batchDialog, _rootElement, 420);

                if (await batchDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                var newGroup = nameBox.Text?.Trim();
                if (string.IsNullOrEmpty(newGroup))
                {
                    summary.Text = "分组名为空，未做改动。";
                    return;
                }

                var adding = addRadio.IsChecked == true;

                // 先全部算完再落盘：SaveSourcesAsync 只写一次 27 MB 文件。
                // 逐条 SaveSourceAsync 会写 N 次整库（几千条 = 几十 GB 写入），必然卡死。
                var updated = new List<BookSourceModel>(targets.Count);
                foreach (var src in targets)
                {
                    var before = BookSourceGroups.Normalize(src.BookSourceGroup);
                    var after = adding
                        ? BookSourceGroups.Add(before, newGroup)
                        : BookSourceGroups.Remove(before, newGroup);

                    if (string.Equals(before, after, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // 改副本再写回，保持"内存中的书源不被就地修改"的既有约束
                    var copy = Clone(src);
                    copy.BookSourceGroup = after;
                    updated.Add(copy);
                }

                if (updated.Count == 0)
                {
                    summary.Text = $"分组无变化（{targets.Count} 个书源本就{(adding ? "都在" : "都不在")}该组）。";
                    return;
                }

                summary.Text = $"正在写入 {updated.Count} 个书源的分组…";
                try
                {
                    await _service.SaveSourcesAsync(updated);
                }
                catch (Exception ex)
                {
                    summary.Text = $"批量分组失败：{ex.Message}";
                    return;
                }

                RebuildGroupOptions();
                await ReloadAsync(null);
                summary.Text = adding
                    ? $"已把 {updated.Count} 个书源加入分组“{newGroup}”。"
                    : $"已把 {updated.Count} 个书源移出分组“{newGroup}”。";
            }

            RebuildGroupOptions();

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
                var report = BookSourceCapabilities.AssessCached(source);
                var kindName = BookSourceGroups.DescribeKind(source.SourceKind);
                detailSubtitle.Text = report.AllSupported
                    ? $"{kindName}　·　URL: {source.BookSourceUrl}　·　当前引擎可完整支持"
                    : $"{kindName}　·　URL: {source.BookSourceUrl}　·　注意：{report.DescribeMissing(maxNames: 4, readingOnly: false)}";

                // 基本信息：书源 URL 只读（它是主键，改了等于换一条源）
                var basic = new StackPanel { Spacing = 6 };
                basic.Children.Add(LegadoForm.Header("基本信息"));
                basic.Children.Add(LegadoForm.Field("书源名称", _draft.BookSourceName, v => _draft!.BookSourceName = v));
                basic.Children.Add(LegadoForm.Field("书源 URL（主键，不可改）", _draft.BookSourceUrl, null));

                // 类别：必须是下拉。这是闭集（引擎按 0/1/2/3/4 分派打开方式），
                // 自由文本一旦写出 "漫画" 或 "9" 就会静默退化成"文本"。
                basic.Children.Add(LegadoForm.Combo(
                    "类别（bookSourceType）",
                    BookSourceGroups.KindOptions,
                    _draft.SourceKind,
                    v => _draft!.BookSourceType = (int)(v ?? BookSourceKind.Text),
                    "文本→阅读模式，漫画→图片模式；音频/视频不在支持范围（保留结果并标注），文件类按文本打开。"));

                // 分组：文本框 + 已有分组下拉 + 就地新建（自定义分组的入口）
                var (groupRow, groupTextBox) = LegadoForm.FieldWithBox(
                    "分组（半角逗号分隔，可同时属于多个组）",
                    _draft.BookSourceGroup,
                    v => _draft!.BookSourceGroup = v);
                basic.Children.Add(groupRow);
                basic.Children.Add(BuildGroupPicker(groupTextBox));

                basic.Children.Add(LegadoForm.Field("书籍 URL 正则", _draft.BookUrlPattern, v => _draft!.BookUrlPattern = v));
                basic.Children.Add(LegadoForm.Field("并发速率", _draft.ConcurrentRate, v => _draft!.ConcurrentRate = v));
                basic.Children.Add(LegadoForm.Field("请求头（JSON）", _draft.Header, v => _draft!.Header = v, multi: true));
                basic.Children.Add(LegadoForm.Field("权重（排序用）", _draft.Weight.ToString(), v =>
                    _draft!.Weight = int.TryParse(v, out var w) ? w : 0));
                basic.Children.Add(LegadoForm.Field("备注", _draft.BookSourceComment, v => _draft!.BookSourceComment = v, multi: true));
                basic.Children.Add(LegadoForm.Toggle("启用", _draft.IsEnabled, async (s, val) =>
                {
                    // 启用开关是高频操作（批量禁用失效源），立即落盘而不必点"保存"
                    _draft!.Enabled = val;
                    await _service.SetSourceEnabledAsync(_draft, val);

                    // 只更新这一行（列表可能 3600+ 行，重建会卡）
                    UpdateRow(_draft);
                    UpdateSummaryText();
                }));
                formHost.Children.Add(basic);

                // 分组的"用起来"入口：把当前这条源归到某个已有组，或就地新建一个组。
                // 两者都只改 draft（与直接编辑分组文本框等价），落盘仍由底部「保存」负责——
                // 分组是书源数据的一部分，不该绕过保存语义（否则"改了没保存"的错觉更难排查）。
                UIElement BuildGroupPicker(TextBox textBox)
                {
                    var existing = new ComboBox
                    {
                        PlaceholderText = "加入已有分组…",
                        MinWidth = 190,
                    };
                    foreach (var (groupName, count) in BookSourceGroups.Collect(_service.Sources.GetAll()))
                    {
                        existing.Items.Add(new ComboBoxItem { Content = $"{groupName}（{count}）", Tag = groupName });
                    }

                    var newNameBox = new TextBox { PlaceholderText = "新建分组名…", MinWidth = 140 };
                    var addButton = new Button { Content = "新建并加入" };

                    void Apply(string groupName)
                    {
                        _draft!.BookSourceGroup = BookSourceGroups.Add(_draft.BookSourceGroup, groupName);
                        textBox.Text = _draft.BookSourceGroup;
                        summary.Text = $"已把「{_draft.BookSourceName}」加入分组“{groupName}”，点「保存」写入。";
                    }

                    existing.SelectionChanged += (_, _) =>
                    {
                        if (existing.SelectedItem is ComboBoxItem { Tag: string groupName })
                        {
                            Apply(groupName);

                            // 复位：下次还想选同一个组时，选中项变化才会再次触发
                            existing.SelectedIndex = -1;
                        }
                    };

                    addButton.Click += (_, _) =>
                    {
                        var groupName = newNameBox.Text?.Trim();
                        if (string.IsNullOrEmpty(groupName))
                        {
                            return;
                        }

                        Apply(groupName);
                        newNameBox.Text = string.Empty;
                    };

                    var row = new Grid { ColumnSpacing = 6 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(existing, 0);
                    Grid.SetColumn(newNameBox, 1);
                    Grid.SetColumn(addButton, 2);
                    row.Children.Add(existing);
                    row.Children.Add(newNameBox);
                    row.Children.Add(addButton);

                    var stack = new StackPanel { Spacing = 4 };
                    stack.Children.Add(row);
                    stack.Children.Add(new TextBlock
                    {
                        Text = "分组改动与其他编辑一致：点底部「保存」后写入书源库，筛选与搜索立刻可见。",
                        FontSize = 11,
                        Opacity = 0.55,
                        TextWrapping = TextWrapping.Wrap,
                    });
                    return stack;
                }

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
                scripts.Children.Add(LegadoForm.Header("脚本与登录"));
                scripts.Children.Add(LegadoForm.Field("jsLib（库代码）", _draft.JsLib, v => _draft!.JsLib = v, multi: true, tall: true));
                scripts.Children.Add(LegadoForm.Field("登录地址", _draft.LoginUrl, v => _draft!.LoginUrl = v));
                scripts.Children.Add(LegadoForm.Field("登录校验脚本", _draft.LoginCheckJs, v => _draft!.LoginCheckJs = v, multi: true));
                scripts.Children.Add(LegadoForm.Toggle("启用 Cookie 保持", _draft.EnabledCookieJar ?? false,
                    (s, val) => _draft!.EnabledCookieJar = val));
                scripts.Children.Add(LegadoForm.Field("登录界面脚本", _draft.LoginUi, v => _draft!.LoginUi = v, multi: true));
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

                var saved = _draft;
                try
                {
                    await _service.SaveSourceAsync(saved);
                    _selected = saved;

                    // 可能新建了分组：下拉选项跟着长出来（保持当前选中的分组不变）
                    RebuildGroupOptions();

                    if (!_filter.Matches(saved))
                    {
                        // 改成了不符合当前筛选条件的值（例如类别从"文本"改成"漫画"）：
                        // 这一行按当前筛选本就该消失，重建列表比留一条"不该出现"的行更诚实。
                        // 传 URL 进去，若它仍可见会被重新选中。
                        await ReloadAsync(saved.BookSourceUrl);
                    }
                    else
                    {
                        // 局部刷新：只重建右侧表单，列表仅更新这一行（不重建 3600+ 行）
                        ShowDetail(saved);
                        UpdateRow(saved);
                    }

                    summary.Text = $"已保存：{saved.BookSourceName}";
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

                var removedUrl = _selected.BookSourceUrl;
                await _service.RemoveSourceAsync(removedUrl);
                ShowDetail(null);

                // 删除只移除对应的一行（同样不重建全表）
                RemoveRow(removedUrl);
                _visible.RemoveAll(x => string.Equals(x.BookSourceUrl, removedUrl, StringComparison.Ordinal));

                // 分组计数变了（删掉最后一条属于某组的源，该组就该从下拉里消失）
                RebuildGroupOptions();
                UpdateSummaryText();
            };

            importButton.Click += async (s, e) =>
            {
                dialog.Hide();
                await _onImportRequested(string.Empty);
            };

            // 首次填充：先让对话框出现（加载提示可见），再分批把列表填进去。
            // 顺序很重要——先 ShowAsync 再填：用户立刻看到窗口与"正在加载 N 个书源…"，
            // 而不是先在 UI 线程闷头建两万个控件、窗口几十秒不出现。
            ShowDetail(null);

            var fillTask = ReloadAsync(null);
            await dialog.ShowAsync();

            // 关闭对话框即停止后续分批次构建（否则用户关窗后仍在后台建几千个控件）
            windowCts.Cancel();
            await fillTask;
        }

        /// <summary>从列表中移除一行（删除书源后调用）。</summary>
        private void RemoveRow(string? bookSourceUrl)
        {
            if (bookSourceUrl is null || !_rows.TryGetValue(bookSourceUrl, out var visuals))
            {
                return;
            }

            _rows.Remove(bookSourceUrl);

            // 用控件引用反查所在容器（ListViewItem），比按 Tag 遍历 3600 项快得多
            var item = FindAncestorItem(visuals.Title);
            if (item is not null)
            {
                _list?.Items.Remove(item);
            }
        }

        /// <summary>沿可视树上溯找到承载 <paramref name="element"/> 的 <see cref="ListViewItem"/>。</summary>
        private static ListViewItem? FindAncestorItem(DependencyObject element)
        {
            var current = element;
            while (current is not null)
            {
                if (current is ListViewItem item)
                {
                    return item;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        // ============================================================
        // 字段构件（Header / Field / Toggle）已抽到 LegadoForm——
        // 规则管理界面（RuleManagerDialog）与本书源管理共用同一份实现，
        // 不在此保留私有副本（避免两处漂移）。
        // ============================================================

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
            panel.Children.Add(LegadoForm.Header(title));

            if (urlLabel is not null && getUrl is not null && setUrl is not null)
            {
                panel.Children.Add(LegadoForm.Field(urlLabel, getUrl(), v => setUrl(v), multi: true));
            }

            var rule = ensureRule();

            // 反射：枚举规则对象上的全部可读可写 string 属性，一次生成完表单。
            // 模型新增字段时表单自动出现，无需改本文件。
            // Legacy* 是 legado 旧键名的导入专用别名（迁移后即被置空），不是给用户看的字段。
            foreach (var prop in rule.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite
                    || RuleEditSpec.IsLegacyAliasProperty(prop.Name))
                {
                    continue;
                }

                var p = prop;
                // 长文本（JS/正则/HTML 相关字段）用多行框，其余单行
                var isLong = p.Name.EndsWith("Js", StringComparison.Ordinal)
                    || p.Name.Contains("Rule", StringComparison.Ordinal)
                    || p.Name.Contains("Regex", StringComparison.Ordinal)
                    || p.Name is "Init" or "Content" or "BookList" or "ChapterList";

                panel.Children.Add(LegadoForm.Field(
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
        /// <para><b>能力评估由调用方传入</b>（<c>RefreshListAsync</c> 已在后台批量算好）：
        /// 这里再做一次评估会把 UI 线程重新拖回"逐条扫脚本文本"的老路。</para>
        /// </summary>
        private (UIElement Content, RowVisuals Visuals) BuildRowContent(
            BookSourceModel source, SourceCapabilityReport report)
        {
            // 语义色按宿主主题取（弹窗基座是宿主 RootGrid，见 ShowAsync 处的 palette）
            var palette = LegadoTheme.For(_rootElement);

            // 徽标按「阅读链路」判定：搜索/详情链的缺口不连坐（与搜索候选过滤同口径）
            var supported = report.ReadingSupported;

            // 徽标用 Fluent Warning 字形 + Caution 语义色（Win11 设计语言：单色字形图标 +
            // 语义色，不用彩色 Emoji）；文字类内容一律灰度（见 LegadoTheme）。
            var badge = new TextBlock
            {
                Text = supported ? string.Empty : "\uE7BA",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = palette.Caution,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!supported)
            {
                // 悬停显示具体缺什么（阶段 4-1 起不再是笼统的"需要完整 JavaScript 支持"）
                ToolTipService.SetToolTip(badge, report.DescribeMissing(maxNames: 6, readingOnly: true));
            }

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
                Text = BuildSubtitle(source),
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(title);
            text.Children.Add(subtitle);

            // 启用复选框：勾选 = 启用该书源（即时落盘）。
            // 事件里先比对模型当前值再落盘：UpdateRow 回填 IsChecked 时会再触发一次
            // Checked/Unchecked，靠这个判断打断"写回→触发→再写回"的回环。
            var enableBox = new CheckBox
            {
                IsChecked = source.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32,
            };
            ToolTipService.SetToolTip(enableBox, source.IsEnabled ? "已启用，点击停用" : "已停用，点击启用");

            enableBox.Checked += (_, _) =>
            {
                if (source.IsEnabled)
                {
                    return;
                }

                _ = ToggleEnabledAsync(source, true);
            };
            enableBox.Unchecked += (_, _) =>
            {
                if (!source.IsEnabled)
                {
                    return;
                }

                _ = ToggleEnabledAsync(source, false);
            };

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

            return (row, new RowVisuals
            {
                Title = title,
                Subtitle = subtitle,
                Badge = badge,
                Toggle = enableBox,
            });
        }

        /// <summary>列表第二行的"启用状态 + 类别 + 分组"文案。</summary>
        private static string BuildSubtitle(BookSourceModel source)
        {
            var parts = new List<string>(3)
            {
                source.IsEnabled ? "已启用" : "已停用",
                BookSourceGroups.DescribeKind(source.SourceKind),
            };

            if (!string.IsNullOrWhiteSpace(source.BookSourceGroup))
            {
                parts.Add(source.BookSourceGroup);
            }

            return string.Join("　·　", parts);
        }

        /// <summary>
        /// 刷新汇总行。
        /// <para>筛选生效时同时给出"总源数"和"当前命中数"——只报命中数会让人以为库里只剩这些；
        /// 只报总数又看不出筛选是否起作用。</para>
        /// </summary>
        private void UpdateSummaryText()
        {
            if (_summary is null)
            {
                return;
            }

            var all = _service.Sources.GetAll();
            var enabled = all.Count(s => s.IsEnabled);

            if (_filter.IsEmpty)
            {
                _summary.Text = $"共 {all.Count} 个书源，已启用 {enabled} 个";
                return;
            }

            var visibleEnabled = _visible.Count(s => s.IsEnabled);
            _summary.Text = $"共 {all.Count} 个书源（启用 {enabled}）　·　当前筛选命中 {_visible.Count} 个（启用 {visibleEnabled}）";
        }

        /// <summary>
        /// 局部刷新一行（勾选启用 / 保存单条之后调用）。
        /// <para><b>不重建整表</b>：3600+ 行重建会让每次点复选框都卡几秒，
        /// 这里只改这一行的文字、字重、徽标与复选框状态。</para>
        /// </summary>
        private void UpdateRow(BookSourceModel source)
        {
            if (source.BookSourceUrl is null || !_rows.TryGetValue(source.BookSourceUrl, out var visuals))
            {
                return;
            }

            visuals.Title.Text = source.BookSourceName;
            visuals.Title.FontWeight = source.IsEnabled
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            visuals.Title.Opacity = source.IsEnabled ? 1.0 : 0.5;
            visuals.Subtitle.Text = BuildSubtitle(source);

            // 勾选状态回填：值不同才写，避免触发 Checked/Unchecked 回环
            if (visuals.Toggle.IsChecked != source.IsEnabled)
            {
                visuals.Toggle.IsChecked = source.IsEnabled;
            }

            ToolTipService.SetToolTip(visuals.Toggle, source.IsEnabled ? "已启用，点击停用" : "已停用，点击启用");

            // 徽标按最新内容重算（保存可能改了规则）；缓存未命中时只评估这一条，代价可忽略
            var report = BookSourceCapabilities.AssessCached(source);
            visuals.Badge.Text = report.ReadingSupported ? string.Empty : "\uE7BA";
            if (report.ReadingSupported)
            {
                ToolTipService.SetToolTip(visuals.Badge, null);
            }
            else
            {
                ToolTipService.SetToolTip(visuals.Badge, report.DescribeMissing(maxNames: 6, readingOnly: true));
            }
        }

        /// <summary>
        /// 复选框切换启用状态并立即落盘。
        /// <para>只刷新受影响的那一行（<see cref="UpdateRow"/>），不重建整个列表：
        /// 3600+ 行重建会把每次勾选都变成数秒冻结。</para>
        /// <para>失败时同样刷新该行：复选框按存储里的真实状态回填，
        /// 避免"勾着但其实没生效"的错位（那比报错更难排查）。</para>
        /// </summary>
        private async Task ToggleEnabledAsync(BookSourceModel source, bool enabled)
        {
            try
            {
                await _service.SetSourceEnabledAsync(source, enabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Toggle source enabled failed: {ex.Message}");
                _notify($"切换启用状态失败：{ex.Message}", "\uE783", "Red");
            }

            UpdateRow(source);
            UpdateSummaryText();
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
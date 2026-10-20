using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 一条可开关的规则（净化规则 / 标题章节规则共用同一套 UI，故抽象出来）。
    /// </summary>
    /// <param name="Id">规则主键。</param>
    /// <param name="Title">主标题（规则名）。</param>
    /// <param name="Detail">副标题（分组 / 作用范围 / 正则摘要等）。</param>
    /// <param name="IsEnabled">当前是否启用。</param>
    public sealed record RuleListItem(long Id, string Title, string Detail, bool IsEnabled);

    /// <summary>
    /// 规则管理器的配置（净化规则与标题规则各给一份，对话框本体完全通用）。
    /// <para>用委托而不是接口：两类规则模型没有共同基类，而对话框需要的操作
    /// （列 / 取 / 克隆 / 新建 / 校验 / 存 / 删 / 开关）用委托表达最直白，
    /// 与 <see cref="BookSourceManagerDialog"/> 直接依赖服务的做法相比，
    /// 这里多一层间接只为同一份 UI 服务两个模型——逻辑仍在服务类里，不在对话框里。</para>
    /// </summary>
    public sealed class RuleManagerConfig<T> where T : class
    {
        /// <summary>对话框标题（如"替换净化规则管理"）。</summary>
        public required string DialogTitle { get; init; }

        /// <summary>条目名词（按钮与确认框用，如"净化规则"）。</summary>
        public required string ItemNoun { get; init; }

        /// <summary>列表为空时的提示。</summary>
        public required string EmptyHint { get; init; }

        /// <summary>列表数据源（名称 / 副标题 / 开关态）。</summary>
        public required Func<IReadOnlyList<RuleListItem>> ListItems { get; init; }

        /// <summary>规则名（列表为空值时显示"(未命名规则)"，由服务层统一兜底）。</summary>
        public required Func<T, string> NameOf { get; init; }

        /// <summary>副标题摘要（编辑区标题下的一行）。</summary>
        public required Func<T, string> DetailOf { get; init; }

        /// <summary>主键（新建未保存的规则为 0，存储层保存时分配）。</summary>
        public required Func<T, long> IdOf { get; init; }

        /// <summary>按 Id 取存储中的规则（选中列表项时定位）。</summary>
        public required Func<long, T?> GetById { get; init; }

        /// <summary>深拷贝（进编辑时用，避免表单改动直接污染内存中生效的实例）。</summary>
        public required Func<T, T> Clone { get; init; }

        /// <summary>新建一条带默认值的规则（Id = 0，保存时由存储层分配）。</summary>
        public required Func<T> CreateNew { get; init; }

        /// <summary>保存前校验；返回 null = 可保存，否则为给用户看的原因。</summary>
        public required Func<T, string?> Validate { get; init; }

        /// <summary>保存（新建或覆盖）：入库 + 落盘 + 刷新下游（净化器快照 / TXT 目录）。</summary>
        public required Func<T, Task> SaveAsync { get; init; }

        /// <summary>删除：移除 + 落盘 + 刷新下游。</summary>
        public required Func<long, Task> DeleteAsync { get; init; }

        /// <summary>切换单条启用状态（即时落盘，列表复选框走这里）。</summary>
        public required Func<long, bool, Task> SetEnabledAsync { get; init; }

        /// <summary>"导入 JSON…"按钮：对话框先隐藏，回调里导入并重开本对话框。</summary>
        public required Func<Task> OnImportRequested { get; init; }

        /// <summary>提示回调（消息 / 图标 / 颜色），失败原因告诉用户。</summary>
        public Action<string, string, string>? Notify { get; init; }
    }

    /// <summary>
    /// 规则管理器：图形化查看 / 逐条编辑已导入的净化规则或标题规则。
    ///
    /// <para><b>布局与 <see cref="BookSourceManagerDialog"/> 完全一致</b>（不另起炉灶）：
    /// 左侧条目列表（名称 + 副标题 + 启用复选框），右侧按字段顺序展开的编辑表单，
    /// 底部 导入 JSON… / 新建 / 保存 / 删除 按钮，关闭为主按钮。字段构件用
    /// <see cref="LegadoForm"/> 的同一份实现；表单由 <see cref="RuleEditSpec"/> 的
    /// 元数据（顺序 / 标签 / 多行 / 只读）反射生成，模型加字段时自动跟上。</para>
    ///
    /// <para><b>编辑模型</b>：进入编辑时对规则做<strong>深拷贝</strong>（<see cref="RuleManagerConfig{T}.Clone"/>），
    /// 取消不会污染内存中正被净化器 / 分章器使用的规则。保存时才写回并落盘。</para>
    ///
    /// <para><b>两种生效路径</b>（与旧清单弹窗及书源管理保持一致）：
    /// 列表复选框 = 即时生效并落盘；表单"启用"开关对已存在规则同样即时落盘
    /// （高频操作不必再点保存），其余字段统一走「保存」。</para>
    /// </summary>
    public sealed class RuleManagerDialog<T> where T : class
    {
        /// <summary>
        /// 滚动区期望高度（px）。规则表单通常 6~10 个字段，比书源表单短，
        /// 但仍按书源管理的同一档给足，减少滚动；实际值由
        /// <see cref="LegadoDialogLayout"/> 按窗口高度收敛。
        /// </summary>
        private const double PreferredContentHeight = 620;

        /// <summary>左侧列表固定宽度（与书源管理一致：名称 + 副标题 + 复选框）。</summary>
        private const double ListWidth = 320;

        /// <summary>内容期望宽度 = 列表 + 间距 + 右侧编辑区（与书源管理同公式）。</summary>
        private const double DesiredContentWidth = ListWidth + 12 + 560;

        private readonly FrameworkElement _rootElement;
        private readonly RuleManagerConfig<T> _config;
        private readonly Action<string, string, string> _notify;

        /// <summary>当前编辑中的副本（null = 未选中/未在编辑）。</summary>
        private T? _draft;

        /// <summary>当前选中项的主键（-1 = 无选中；0 = 新建未保存）。</summary>
        private long _selectedId = -1;

        /// <summary>重建列表期间屏蔽 SelectionChanged（否则 Clear + 重选会把表单洗掉）。</summary>
        private bool _suppressSelection;

        public RuleManagerDialog(FrameworkElement rootElement, RuleManagerConfig<T> config)
        {
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // 提示回调可选：没给就用调试输出，不影响主流程
            _notify = config.Notify ?? ((msg, _, _) => System.Diagnostics.Debug.WriteLine($"Rule manager: {msg}"));
        }

        /// <summary>弹出规则管理器。</summary>
        public async Task ShowAsync()
        {
            var palette = LegadoTheme.For(_rootElement);

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,

                // 固定宽度：列表内容（名称/副标题/复选框）不需要更多空间，
                // 把余量留给右侧编辑区（匹配串/替换串都是长文本，可读性优先）。
                Width = ListWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var detailTitle = new TextBlock
            {
                Text = "← 从左侧选择一条规则查看与编辑，或点「新建」",
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

                // 关键：跟随 Star 列伸缩（与书源管理同一约束）
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            detailPanel.Children.Add(detailTitle);
            detailPanel.Children.Add(detailSubtitle);
            detailPanel.Children.Add(formHost);

            var importButton = new Button { Content = "导入 JSON…" };
            var newButton = new Button { Content = $"新建{_config.ItemNoun}" };
            var saveButton = new Button { Content = "保存", IsEnabled = false };
            var deleteButton = new Button { Content = "删除", IsEnabled = false };

            var summary = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,

                // 次要文字用灰度（不用主题强调色），暗色下不会被"蓝色字"淹没
                Foreground = palette.TextSecondary,
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
            buttonRow.Children.Add(newButton);
            buttonRow.Children.Add(saveButton);
            buttonRow.Children.Add(deleteButton);

            var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(root);
            panel.Children.Add(buttonRow);

            var dialog = new ContentDialog
            {
                XamlRoot = _rootElement.XamlRoot,
                RequestedTheme = _rootElement.ActualTheme,
                Title = _config.DialogTitle,
                Content = new ScrollViewer
                {
                    Content = panel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto,

                    // 横向不滚：内容整体已按对话框宽度收敛（同书源管理）
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
            void RefreshList(long selectId = -1)
            {
                _suppressSelection = true;
                try
                {
                    list.Items.Clear();
                    foreach (var item in _config.ListItems())
                    {
                        list.Items.Add(new ListViewItem
                        {
                            Content = BuildListItemContent(item, RefreshList),
                            Tag = item.Id,
                        });
                    }

                    var all = _config.ListItems();
                    summary.Text =
                        $"共 {all.Count} 条{_config.ItemNoun}，已启用 {all.Count(x => x.IsEnabled)} 条" +
                        "　·　列表勾选即时生效，表单修改需点「保存」";

                    if (selectId >= 0)
                    {
                        foreach (var item in list.Items.OfType<ListViewItem>())
                        {
                            if (item.Tag is long id && id == selectId)
                            {
                                list.SelectedItem = item;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    _suppressSelection = false;
                }
            }

            // ── 编辑表单生成 ──────────────────────────────────────
            void ShowDetail(T? source)
            {
                _selectedId = source is null ? -1 : _config.IdOf(source);
                formHost.Children.Clear();
                saveButton.IsEnabled = source is not null;
                deleteButton.IsEnabled = source is not null && _selectedId > 0;

                if (source is null)
                {
                    _draft = null;

                    // 列表为空时给导入指引，而不是"从左侧选择"——空列表下那句话指向不存在的东西
                    detailTitle.Text = _config.ListItems().Count == 0
                        ? _config.EmptyHint
                        : "← 从左侧选择一条规则查看与编辑，或点「新建」";
                    detailSubtitle.Text = string.Empty;
                    return;
                }

                // 深拷贝进表单：副本怎么改都不影响正在净化/切分的存储实例
                _draft = _config.Clone(source);
                detailTitle.Text = _config.NameOf(_draft);
                detailSubtitle.Text = _selectedId > 0
                    ? $"ID {_selectedId}　·　{_config.DetailOf(_draft)}"
                    : $"新{_config.ItemNoun}（保存后分配 ID）　·　{_config.DetailOf(_draft)}";

                var ruleType = typeof(T);
                foreach (var propName in RuleEditSpec.FieldOrder(ruleType))
                {
                    var prop = ruleType.GetProperty(propName);
                    if (prop is null || !prop.CanRead || !prop.CanWrite)
                    {
                        continue;
                    }

                    var label = RuleEditSpec.Label(ruleType, propName);
                    var value = prop.GetValue(_draft);

                    if (RuleEditSpec.IsReadOnlyKey(propName))
                    {
                        // 主键只读：Id 由存储层分配，界面改它会造出孤儿规则
                        var idText = _selectedId > 0 ? _selectedId.ToString() : "（保存后自动分配）";
                        formHost.Children.Add(LegadoForm.Field(label, idText, null));
                    }
                    else if (prop.PropertyType == typeof(string))
                    {
                        var text = (string?)value;
                        formHost.Children.Add(LegadoForm.Field(
                            label, text, v => prop.SetValue(_draft, v),
                            multi: RuleEditSpec.IsLongText(propName)));
                    }
                    else if (prop.PropertyType == typeof(bool))
                    {
                        var flag = (bool)(value ?? false);
                        formHost.Children.Add(LegadoForm.Toggle(label, flag, async (s, val) =>
                        {
                            prop.SetValue(_draft, val);

                            // "启用"是高频操作（批量停用失效规则），对已存在规则立即落盘，
                            // 与列表复选框、书源管理的"启用"开关语义一致；新建中的规则
                            // 还没有 Id，跟随其他字段一起等「保存」。
                            if (propName == "IsEnabled" && _selectedId > 0)
                            {
                                try
                                {
                                    await _config.SetEnabledAsync(_selectedId, val);
                                    RefreshList(_selectedId);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Rule toggle failed: {ex.Message}");
                                    _notify($"切换启用状态失败：{ex.Message}", "\uE783", "Red");
                                }
                            }
                        }));
                    }
                    else if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(long))
                    {
                        var number = value switch
                        {
                            int i => i,
                            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
                            _ => 0,
                        };
                        formHost.Children.Add(LegadoForm.IntField(
                            label, number, v => prop.SetValue(_draft, v)));
                    }
                }
            }

            list.SelectionChanged += (s, e) =>
            {
                if (_suppressSelection)
                {
                    return;
                }

                var item = list.SelectedItem as ListViewItem;
                var id = item?.Tag as long? ?? -1;
                if (id < 0)
                {
                    ShowDetail(null);
                    return;
                }

                // 同一项重复选中（如启用开关导致的列表重建）：不重置表单，
                // 否则用户在表单里未保存的编辑会被一次性洗掉
                if (id == _selectedId)
                {
                    return;
                }

                ShowDetail(_config.GetById(id));
            };

            // ── 保存 ──────────────────────────────────────────────
            saveButton.Click += async (s, e) =>
            {
                if (_draft is null)
                {
                    return;
                }

                var error = _config.Validate(_draft);
                if (error is not null)
                {
                    summary.Text = error;
                    return;
                }

                try
                {
                    await _config.SaveAsync(_draft);

                    // 保存后存储里就是 _draft 这个实例；重新按 Id 取一份再克隆进表单，
                    // 让 UI 副本与存储实例立即分离（继续编辑不会再直接改到存储对象）
                    var savedId = _config.IdOf(_draft);
                    RefreshList(savedId);
                    ShowDetail(_config.GetById(savedId));
                    summary.Text = $"已保存：{_config.NameOf(_draft)}";
                }
                catch (Exception ex)
                {
                    summary.Text = $"保存失败：{ex.Message}";
                }
            };

            // ── 新建 ──────────────────────────────────────────────
            newButton.Click += (s, e) =>
            {
                list.SelectedItem = null;
                ShowDetail(_config.CreateNew());
                summary.Text = $"正在编辑新{_config.ItemNoun}，填写完成后点「保存」";
            };

            // ── 删除 ──────────────────────────────────────────────
            deleteButton.Click += async (s, e) =>
            {
                if (_draft is null || _selectedId <= 0)
                {
                    return;
                }

                var name = _config.NameOf(_draft);
                var confirm = new ContentDialog
                {
                    XamlRoot = _rootElement.XamlRoot,
                    RequestedTheme = _rootElement.ActualTheme,
                    Title = $"删除{_config.ItemNoun}",
                    Content = $"确定删除“{name}”吗？该规则将立即停止生效。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };

                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                try
                {
                    await _config.DeleteAsync(_selectedId);

                    // 先重建列表再清表单：删到最后一条时详情区要显示 EmptyHint 而非"从左侧选择"
                    RefreshList();
                    ShowDetail(null);
                    summary.Text = $"已删除：{name}";
                }
                catch (Exception ex)
                {
                    summary.Text = $"删除失败：{ex.Message}";
                }
            };

            // ── 导入 ──────────────────────────────────────────────
            importButton.Click += async (s, e) =>
            {
                // 与书源管理一致：关掉对话框 → 导入 → 由调用方重开，用户立刻看到新条目
                dialog.Hide();
                await _config.OnImportRequested();
            };

            RefreshList();
            ShowDetail(null);
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 构建规则列表项内容：左侧名称/状态，右侧启用复选框。
        /// <para>与书源列表项同构（去掉能力徽标——那是书源特有的阶段 4 警示）。
        /// 复选框勾选变化立即落盘（与表单"启用"开关一致），不必再点保存。</para>
        /// </summary>
        private UIElement BuildListItemContent(RuleListItem item, Action<long> refreshList)
        {
            var title = new TextBlock
            {
                Text = item.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = item.IsEnabled
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = item.IsEnabled ? 1.0 : 0.5,
            };

            var subtitle = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Detail)
                    ? item.IsEnabled ? "已启用" : "已停用"
                    : $"{(item.IsEnabled ? "已启用" : "已停用")}　·　{item.Detail}",
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(title);
            text.Children.Add(subtitle);

            // 启用复选框：勾选 = 启用该规则（即时落盘）
            var enableBox = new CheckBox
            {
                IsChecked = item.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32,
            };
            ToolTipService.SetToolTip(enableBox, item.IsEnabled ? "已启用，点击停用" : "已停用，点击启用");

            enableBox.Checked += async (s, e) => await ToggleEnabledAsync(item, true, refreshList);
            enableBox.Unchecked += async (s, e) => await ToggleEnabledAsync(item, false, refreshList);

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(enableBox, 1);
            row.Children.Add(text);
            row.Children.Add(enableBox);
            return row;
        }

        /// <summary>
        /// 复选框切换启用状态并立即落盘。
        /// <para>失败时刷新列表：复选框会按存储里的真实状态重建，
        /// 避免出现"勾着但其实没生效"的错位（那比报错更难排查）。
        /// 存储层语义：先改内存再落盘，落盘失败不回滚内存
        /// （见 <see cref="ReplaceRuleManager.SetEnabledAsync"/> 注释）。</para>
        /// </summary>
        private async Task ToggleEnabledAsync(RuleListItem item, bool enabled, Action<long> refreshList)
        {
            try
            {
                await _config.SetEnabledAsync(item.Id, enabled);

                // 列表项的文字/字重也要同步更新（重建即可，不手动改控件）
                refreshList(item.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Rule toggle failed: {ex.Message}");
                _notify($"切换启用状态失败：{ex.Message}", "\uE783", "Red");
                refreshList(item.Id);
            }
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    /// 规则清单弹窗：罗列全部规则，每条一个圆角矩形，右侧复选框控制该规则的开关。
    ///
    /// <para><b>刻意不做"全局总开关"</b>：用户要求逐条开关，而全局开关会让人搞不清
    /// "关掉总开关后单条勾选还有没有意义"。需要整体停用时逐条取消即可——
    /// 状态始终落在每条规则上，语义单一。</para>
    ///
    /// <para><b>改动即落盘</b>：勾选变化立刻写回存储并触发净化器快照刷新，
    /// 用户不需要再点一次"保存"——否则很容易以为没生效。</para>
    /// </summary>
    public sealed class RuleListDialog
    {
        /// <summary>
        /// 内容期望宽度（px）。规则名 + 副标题（分组/匹配串/作用范围）同处一行，
        /// 太窄会立刻触发省略号，而匹配串正是用户判断"这条规则在干什么"的关键信息。
        /// </summary>
        private const double DesiredContentWidth = 620;

        /// <summary>滚动区期望高度（px）：一次能看到 6~7 条规则，便于纵向对比。</summary>
        private const double DesiredContentHeight = 380;

        private readonly FrameworkElement _rootElement;
        private readonly string _title;
        private readonly string _emptyHint;
        private readonly Func<IReadOnlyList<RuleListItem>> _load;
        private readonly Func<long, bool, Task> _setEnabled;

        public RuleListDialog(
            FrameworkElement rootElement,
            string title,
            Func<IReadOnlyList<RuleListItem>> load,
            Func<long, bool, Task> setEnabled,
            string emptyHint = "尚未导入任何规则。请先用菜单里的「导入规则」从 JSON 文件导入。")
        {
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
            _title = title;
            _emptyHint = emptyHint;
        }

        /// <summary>弹出规则清单。返回用户是否改动了任何开关。</summary>
        public async Task<bool> ShowAsync()
        {
            // 复制成可变列表：勾选后要能就地更新 IsEnabled（重新 Load 会丢 UI 状态）
            var items = (_load() ?? Array.Empty<RuleListItem>()).ToList();
            var changed = false;

            // 配色按实际主题显式取值：文字与底色成对，保证亮/暗下都可读
            var palette = LegadoTheme.For(_rootElement);

            var listPanel = new StackPanel { Spacing = 6 };
            var scroller = new ScrollViewer
            {
                Content = listPanel,
                Height = DesiredContentHeight,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var status = new TextBlock
            {
                Text = $"共 {items.Count} 条规则　·　勾选即生效并立即保存",
                FontSize = 12,

                // 次要文字用灰度（不用主题强调色），暗色下不会被"蓝色字"淹没
                Foreground = palette.TextSecondary,
            };

            if (items.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = _emptyHint,
                    TextWrapping = TextWrapping.Wrap,

                    // 空态提示用最弱一档灰度但仍满足 4.5:1 对比度
                    Foreground = palette.TextMuted,
                });
            }
            else
            {
                foreach (var item in items)
                {
                    listPanel.Children.Add(BuildRow(item, items, palette, () => changed = true));
                }
            }

            var panel = new StackPanel
            {
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            panel.Children.Add(scroller);
            panel.Children.Add(status);

            var dialog = new ContentDialog
            {
                XamlRoot = _rootElement.XamlRoot,
                RequestedTheme = _rootElement.ActualTheme,
                Title = _title,
                Content = panel,
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            // 规则名/匹配串都是长文本，窗口给足宽度；同时按实际窗口收敛，
            // 避免硬编码宽度在窄窗口下反而被裁（原先写死 MinWidth=656 是隐患）
            LegadoDialogLayout.Fit(dialog, _rootElement, DesiredContentWidth);
            scroller.Height = LegadoDialogLayout.ScrollViewportHeight(_rootElement, DesiredContentHeight);

            await dialog.ShowAsync();
            return changed;
        }

        /// <summary>单条规则的圆角行：左侧两行文字，右侧复选框。</summary>
        private FrameworkElement BuildRow(
            RuleListItem item,
            List<RuleListItem> allItems,
            LegadoPalette palette,
            Action onChanged)
        {
            var title = new TextBlock
            {
                Text = item.Title,
                FontSize = 14,
                Foreground = palette.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };

            var detail = new TextBlock
            {
                Text = item.Detail,
                FontSize = 12,

                // 副标题（分组/匹配串/作用范围）是灰度，不带色相
                Foreground = palette.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };

            var textStack = new Grid();
            textStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            textStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(title, 0);
            Grid.SetRow(detail, 1);
            textStack.Children.Add(title);
            textStack.Children.Add(detail);

            var check = new CheckBox
            {
                IsChecked = item.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 44,
            };
            ToolTipService.SetToolTip(check, item.IsEnabled ? "已启用，点击停用" : "已停用，点击启用");

            // 回滚用：程序性改 IsChecked 会触发 Checked/Unchecked，需抑制以免误判为用户操作
            var suppress = false;

            check.Checked += async (s, e) => await ToggleAsync(true);
            check.Unchecked += async (s, e) => await ToggleAsync(false);

            async Task ToggleAsync(bool enabled)
            {
                if (suppress)
                {
                    return;
                }

                try
                {
                    await _setEnabled(item.Id, enabled);
                    onChanged();

                    // 就地更新条目（不重建整表，避免勾选动画闪烁）
                    var idx = allItems.IndexOf(item);
                    if (idx >= 0)
                    {
                        allItems[idx] = item with { IsEnabled = enabled };
                    }

                    ToolTipService.SetToolTip(check, enabled ? "已启用，点击停用" : "已停用，点击启用");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Rule toggle failed: {ex.Message}");

                    // 落盘失败：把勾选回滚，避免 UI 显示"已启用"而实际未生效
                    suppress = true;
                    check.IsChecked = !enabled;
                    suppress = false;
                }
            }

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(check, 1);
            grid.Children.Add(textStack);
            grid.Children.Add(check);

            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 8, 8),
                Background = palette.Surface,
                BorderBrush = palette.SurfaceBorder,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = grid,
            };
        }
    }
}

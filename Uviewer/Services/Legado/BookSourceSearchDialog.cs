using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services;
using Uviewer.Services.Legado;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源搜索对话框（纯代码构建，不改 XAML）。
    ///
    /// <para><b>交互</b>：输入关键词 → 回车/点搜索 → 结果按书源分组列出 → 点击某条即打开这本书
    /// （深链进入阅读）。对话框关闭即取消在途搜索（CTS 联动）。</para>
    ///
    /// <para><b>性能/资源</b>：① 搜索在后台跑（库侧 16 并发），UI 只在完成后一次性填充列表，
    /// 不会因结果多而卡顿；② 用 <c>VirtualizingListView</c> 语义（ItemsRepeater/ListView 默认虚拟化）
    /// 承载结果，几百条也不膨胀视觉树；③ 取消后立即丢弃结果，不做无用渲染。</para>
    /// </summary>
    public sealed class BookSourceSearchDialog
    {
        private readonly FrameworkElement _rootElement;
        private readonly BookSourceSearchService _search;
        private readonly Func<BookSourceSearchService.BookSearchHit, Task> _onOpenBook;
        private CancellationTokenSource? _cts;

        public BookSourceSearchDialog(
            FrameworkElement rootElement,
            BookSourceSearchService search,
            Func<BookSourceSearchService.BookSearchHit, Task> onOpenBook)
        {
            _rootElement = rootElement ?? throw new ArgumentNullException(nameof(rootElement));
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _onOpenBook = onOpenBook ?? throw new ArgumentNullException(nameof(onOpenBook));
        }

        /// <summary>弹出对话框；<paramref name="initialKeyword"/> 非空时直接带关键词搜索。</summary>
        public async Task ShowAsync(string? initialKeyword = null)
        {
            var input = new TextBox
            {
                PlaceholderText = "书名 / 作者（回车搜索）",
                Text = initialKeyword ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var status = new TextBlock
            {
                Text = string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            };

            var results = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Height = 320,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var searchButton = new Button { Content = "搜索", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var closeButton = new Button { Content = "关闭" };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 0);
            Grid.SetColumn(searchButton, 1);
            header.Children.Add(input);
            header.Children.Add(searchButton);

            var panel = new StackPanel { Spacing = 10, Width = 460 };
            panel.Children.Add(header);
            panel.Children.Add(status);
            panel.Children.Add(results);

            var dialog = new ContentDialog
            {
                XamlRoot = _rootElement.XamlRoot,
                RequestedTheme = _rootElement.ActualTheme,
                Title = "书源搜索",
                Content = panel,
                PrimaryButtonText = "搜索",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
            };

            var runSearch = new Func<Task>(async () =>
            {
                var keyword = input.Text?.Trim() ?? string.Empty;
                if (keyword.Length == 0)
                {
                    return;
                }

                // 取消上一次搜索：连续输入时只有最后一次结果会被渲染
                _cts?.Cancel();
                _cts?.Dispose();
                var cts = new CancellationTokenSource();
                _cts = cts;

                status.Text = $"正在搜索“{keyword}”…";
                results.Items.Clear();

                IReadOnlyList<BookSourceSearchService.SearchGroup> groups;
                try
                {
                    groups = await _search.SearchAsync(keyword, null, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    status.Text = $"搜索失败：{ex.Message}";
                    return;
                }

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                var total = groups.Sum(g => g.Hits.Count);
                status.Text = total == 0
                    ? $"“{keyword}”没有结果（书源 {groups.Count} 个有响应）"
                    : $"“{keyword}”：{total} 条，来自 {groups.Count} 个书源";

                foreach (var group in groups)
                {
                    // 分组标题（不可点击）
                    results.Items.Add(new ListViewItem
                    {
                        Content = new TextBlock
                        {
                            Text = $"▾ {group.SourceName}（{group.Hits.Count}）",
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Opacity = 0.85,
                        },
                        IsEnabled = false,
                    });

                    foreach (var hit in group.Hits)
                    {
                        var captured = hit;
                        var item = new ListViewItem
                        {
                            Content = new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(captured.Author)
                                    ? captured.Name
                                    : $"{captured.Name}  ·  {captured.Author}",
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                            Padding = new Thickness(12, 4, 12, 4),
                        };

                        item.PointerPressed += async (s, e) =>
                        {
                            if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
                            {
                                dialog.Hide();
                                await _onOpenBook(captured);
                            }
                        };

                        results.Items.Add(item);
                    }
                }
            });

            searchButton.Click += async (s, e) => await runSearch();
            dialog.PrimaryButtonClick += async (s, e) => await runSearch();
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

            // 有初始关键词时自动搜一次
            if (!string.IsNullOrWhiteSpace(initialKeyword))
            {
                await runSearch();
            }
        }
    }
}
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Services.BookSources;
using Uviewer.Services;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书籍搜索对话框（纯代码构建，不改 XAML）。
    ///
    /// <para><b>搜索的是什么</b>：书名 / 作者关键词 —— 由每个书源的 <c>searchUrl</c>
    /// 模板（<c>{{key}}</c> / <c>{key}</c>）插值后请求对应网站，解析其结果列表。
    /// <b>不是</b>搜索书源名称。对话框副标题与搜索框 Placeholder 都明示这一点。</para>
    ///
    /// <para><b>为什么不用 ContentDialog 的 PrimaryButton 触发搜索</b>：
    /// 该按钮的默认行为就是关闭对话框，若把搜索挂在 <c>PrimaryButtonClick</c> 上，
    /// 搜索会在对话框关闭之后才跑，用户永远看不到结果（本项目的真实 bug）。
    /// 现在改为：主按钮文案为"关闭"，搜索由面板内的独立按钮 / 回车触发，对话框不自动关。</para>
    ///
    /// <para><b>进度反馈</b>：多源搜索动辄数十秒（280 源 / 16 并发 / 15s 超时），
    /// 状态区实时显示 <c>X/X</c>（已搜索 / 总数）+ 已命中条数，让用户能区分
    /// "正在搜"与"卡死"。</para>
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

        /// <summary>弹出对话框；<paramref name="initialKeyword"/> 非空时带关键词并立即开搜。</summary>
        public async Task ShowAsync(string? initialKeyword = null)
        {
            var input = new TextBox
            {
                // 明示搜的是书籍，不是书源名
                PlaceholderText = "输入书名或作者（回车搜索）",
                Text = initialKeyword ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var progressText = new TextBlock
            {
                Text = string.Empty,
                Opacity = 0.75,
                FontSize = 12,
            };

            var status = new TextBlock
            {
                Text = "就绪。搜索会并发查询所有已启用书源。",
                TextWrapping = TextWrapping.Wrap,
            };

            var results = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Height = 300,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var searchButton = new Button
            {
                Content = "搜索书籍",
                MinWidth = 96,
            };

            var progressBar = new ProgressBar
            {
                IsIndeterminate = false,
                Visibility = Visibility.Collapsed,
                Height = 3,
            };

            // ── 进度状态（供回调与 UI 共享）
            int completed = 0, totalSources = 0, hitCount = 0;
            var running = false;

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 0);
            Grid.SetColumn(searchButton, 1);
            header.Children.Add(input);
            header.Children.Add(searchButton);

            var panel = new StackPanel { Spacing = 10, Width = 480 };
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
            panel.Children.Add(results);

            var dialog = new ContentDialog
            {
                XamlRoot = _rootElement.XamlRoot,
                RequestedTheme = _rootElement.ActualTheme,
                Title = "搜索书籍（书源）",
                Content = panel,
                // 主按钮只做"关闭"：搜索由面板内按钮触发，避免"一点搜索对话框就没了"
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            var runSearch = new Func<Task>(async () =>
            {
                var keyword = input.Text?.Trim() ?? string.Empty;
                if (keyword.Length == 0 || running)
                {
                    return;
                }

                // 取消上一次搜索：连续输入时只有最后一次结果会被渲染
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
                results.Items.Clear();
                progressBar.Visibility = Visibility.Visible;
                progressBar.IsIndeterminate = true;   // 总数未知前先转圈
                progressText.Text = string.Empty;
                status.Text = $"正在搜索“{keyword}”…";

                try
                {
                    // 进度回调可能来自完成时的工作线程 → DispatcherQueue 切回 UI 线程
                    var progress = new Progress<SearchProgress>(p =>
                    {
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        totalSources = p.Total;
                        completed = p.Completed;
                        if (totalSources > 0)
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Maximum = totalSources;
                            progressBar.Value = completed;
                        }

                        // 需求：搜索过程中以 X/X 形式展示当前已搜索条目数与总条目数
                        progressText.Text = totalSources > 0
                            ? $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本"
                            : "正在准备书源…";
                    });

                    var groups = await _search.SearchAsync(keyword, null, progress, cts.Token);

                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    // 进度条收尾（确保走到 100%）
                    if (totalSources > 0)
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = totalSources;
                    }

                    foreach (var group in groups)
                    {
                        foreach (var hit in group.Hits)
                        {
                            hitCount++;
                        }

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
                                        : $"{captured.Name}　·　{captured.Author}",
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                },
                                Padding = new Thickness(12, 4, 12, 4),
                            };

                            item.PointerPressed += async (s, e) =>
                            {
                                if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
                                {
                                    return;
                                }

                                // 先关对话框再打开：避免深链加载期间对话框挡住阅读区，
                                // 也让用户看到"正在打开"的提示（由阅读控制器给出）
                                dialog.Hide();
                                await _onOpenBook(captured);
                            };

                            results.Items.Add(item);
                        }
                    }

                    progressText.Text = $"已搜索 {completed}/{totalSources} 个书源　·　命中 {hitCount} 本";
                    status.Text = hitCount == 0
                        ? $"“{keyword}”没有结果。书源站点可能已失效或改版（{totalSources} 个源全部查询完毕）。"
                        : $"“{keyword}”命中 {hitCount} 本，来自 {groups.Count} 个书源。点击任意一条开始阅读。";
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
                    running = false;
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

            // 带初始关键词时自动开搜一次（在对话框显示之后）
            if (!string.IsNullOrWhiteSpace(initialKeyword))
            {
                await runSearch();
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;

namespace Uviewer.Services
{
    internal sealed class ImageNavigationHandlers
    {
        public Func<IReadOnlyList<ImageEntry>> GetImageEntries { get; init; } = null!;
        public Func<int> GetCurrentIndex { get; init; } = null!;
        public Action<int> SetCurrentIndex { get; init; } = null!;
        public Func<bool> IsCurrentViewSideBySide { get; init; } = null!;
        public Action<int> SetScrollDirection { get; init; } = null!;
        public FastNavigationService FastNavigationService { get; init; } = null!;
        public Func<CancellationToken, Task> ResetFastNavigationAsync { get; init; } = null!;
        public Action UpdateFastNavigationUi { get; init; } = null!;
        public Func<bool, Task> DisplayCurrentImageAsync { get; init; } = null!;
        public Func<Task> SaveCurrentPositionAsync { get; init; } = null!;
        public Func<bool> ShouldPreloadAfterNavigate { get; init; } = null!;
        public Action<bool> StartPreload { get; init; } = null!;
        public Func<bool> IsArchiveOpen { get; init; } = null!;
        public Action ShowLastPageOverlay { get; init; } = null!;
        public Action FocusViewer { get; init; } = null!;

        /// <summary>
        /// 到达当前序列边界时的续接钩子（可选）。参数 = 方向（true 前进）；
        /// 返回 true 表示"已接管"（例如漫画换到下一章的章包），调用方直接结束本次导航。
        /// <para>只有漫画模式会注册它；普通图片浏览保持原有"最后一页"提示行为。</para>
        /// </summary>
        // EN: Optional hook for crossing the edge of the current sequence (manga: switch chapter archive).
        public Func<bool, Task<bool>>? NavigateBeyondEdgeAsync { get; init; }
    }

    internal sealed class ImageNavigationCoordinator
    {
        private readonly ImageNavigationHandlers _handlers;
        private int _navigationVersion;

        public ImageNavigationCoordinator(ImageNavigationHandlers handlers)
        {
            _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        }

        public Task NavigatePreviousAsync(bool isManualClick = false) =>
            NavigateAsync(forward: false, isManualClick);

        public Task NavigateNextAsync(bool isManualClick = false) =>
            NavigateAsync(forward: true, isManualClick);

        public async Task NavigateToImageAsync(int targetIndex)
        {
            var entries = _handlers.GetImageEntries();
            if (_handlers.FastNavigationService.IsNavigationSuspended ||
                targetIndex < 0 || targetIndex >= entries.Count ||
                !FileExplorerService.IsImageEntry(entries[targetIndex])) return;

            bool hasPendingPreview = _handlers.FastNavigationService.HasPendingReset;
            _handlers.FastNavigationService.StopTimers();
            if (_handlers.GetCurrentIndex() == targetIndex && !hasPendingPreview) return;

            int version = ++_navigationVersion;
            var targetEntry = entries[targetIndex];
            bool forward = targetIndex > _handlers.GetCurrentIndex();
            bool IsCurrentNavigation() => version == _navigationVersion &&
                !_handlers.FastNavigationService.IsNavigationSuspended &&
                _handlers.GetCurrentIndex() == targetIndex &&
                targetIndex < _handlers.GetImageEntries().Count &&
                ReferenceEquals(_handlers.GetImageEntries()[targetIndex], targetEntry);

            _handlers.SetScrollDirection(forward ? 1 : -1);
            _handlers.SetCurrentIndex(targetIndex);
            await _handlers.DisplayCurrentImageAsync(false);
            if (!IsCurrentNavigation()) return;
            await _handlers.SaveCurrentPositionAsync();
            if (!IsCurrentNavigation()) return;
            if (_handlers.ShouldPreloadAfterNavigate()) _handlers.StartPreload(forward);
        }

        private async Task NavigateAsync(bool forward, bool isManualClick)
        {
            if (_handlers.FastNavigationService.IsNavigationSuspended) return;
            _handlers.SetScrollDirection(forward ? 1 : -1);

            var entries = _handlers.GetImageEntries();
            int currentIndex = _handlers.GetCurrentIndex();
            bool isSideBySide = _handlers.IsCurrentViewSideBySide();
            bool currentSpreadIncludesLastImage = false;

            if (forward && isSideBySide)
            {
                int pairedIndex = FileExplorerService.GetNextImageIndex(entries, currentIndex, 1, true);
                if (pairedIndex != currentIndex)
                {
                    int indexAfterPair = FileExplorerService.GetNextImageIndex(entries, pairedIndex, 1, true);
                    currentSpreadIncludesLastImage = indexAfterPair == pairedIndex;
                }
            }

            int step = isSideBySide ? 2 : 1;
            int nextIndex = FileExplorerService.GetNextImageIndex(entries, currentIndex, step, forward);
            bool canNavigate = nextIndex != currentIndex && !currentSpreadIncludesLastImage;

            if (canNavigate)
            {
                // Without fast navigation, accept repeated input only at the
                // fastest rate that would stay below the fast-navigation threshold.
                if (!isManualClick && !_handlers.FastNavigationService.TryBeginNormalNavigation()) return;

                int version = ++_navigationVersion;
                var targetEntry = entries[nextIndex];
                bool IsCurrentNavigation() => version == _navigationVersion &&
                    !_handlers.FastNavigationService.IsNavigationSuspended &&
                    _handlers.GetCurrentIndex() == nextIndex &&
                    nextIndex < _handlers.GetImageEntries().Count &&
                    ReferenceEquals(_handlers.GetImageEntries()[nextIndex], targetEntry);

                if (isManualClick) _handlers.FastNavigationService.StopTimers();
                bool isFast = !isManualClick &&
                    _handlers.FastNavigationService.DetectFastNavigation(_handlers.ResetFastNavigationAsync);

                _handlers.SetCurrentIndex(nextIndex);

                if (isFast)
                {
                    _handlers.UpdateFastNavigationUi();
                    return;
                }

                await _handlers.DisplayCurrentImageAsync(true);
                if (!IsCurrentNavigation()) return;
                await _handlers.SaveCurrentPositionAsync();
                if (!IsCurrentNavigation()) return;

                if (_handlers.ShouldPreloadAfterNavigate())
                {
                    _handlers.StartPreload(forward);
                }
            }
            else if (!canNavigate && _handlers.NavigateBeyondEdgeAsync is not null)
            {
                // 序列边界：优先交给续接钩子（漫画换章包 / 下一章首图）。
                // 钩子返回 false 时才继续走原有的"最后一页"提示。
                if (await _handlers.NavigateBeyondEdgeAsync(forward).ConfigureAwait(true))
                {
                    _handlers.FocusViewer();
                    return;
                }

                if (forward && entries.Count > 0 && _handlers.IsArchiveOpen())
                {
                    _handlers.ShowLastPageOverlay();
                }
            }
            else if (forward && entries.Count > 0 && _handlers.IsArchiveOpen())
            {
                _handlers.ShowLastPageOverlay();
            }

            _handlers.FocusViewer();
        }
    }
}

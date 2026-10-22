using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Controls;
using Uviewer.Models;

namespace Uviewer.Services
{
    internal interface IReaderAppStateHost
    {
        bool IsWindowClosing { get; }
        bool IsWebDavMode { get; }
        bool IsEpubMode { get; set; }
        bool IsNavigatingRecent { get; set; }
        bool IsColorPickerOpen { get; set; }
        bool ShouldInvertControls { get; }
        string WindowTitle { get; set; }

        DispatcherQueue DispatcherQueue { get; }
        AppWindow AppWindow { get; }

        /// <summary>
        /// 主窗口的原生句柄（HWND）。
        /// <para><b>为什么单独暴露而不是从 <see cref="AppWindow"/> 换算</b>：
        /// <c>WinRT.Interop.WindowNative.GetWindowHandle</c> 只有一个
        /// <c>(object target)</c> 重载，内部走 <c>IWindowNative</c> 转换。
        /// WinUI3 的 <c>Window</c> 实现了该接口，而 WinAppSDK 的
        /// <c>Microsoft.UI.Windowing.AppWindow</c> <b>没有</b>——拿它去换会得到无效句柄，
        /// 于是 FileOpenPicker 抛 <c>Invalid window handle (0x80070578)</c>。
        /// 故必须用真正的 <c>Window</c> 句柄（宿主在 MainWindow 侧取一次传进来）。</para>
        /// </summary>
        nint WindowHandle { get; }

        WindowShellController WindowShellController { get; }

        void EnsureMinWindowSizeForText();
        void ApplyLocalization();
        void ShowNotification(string message, string icon = "\uE735", string color = "Gold");
        void ToggleSidebar();
        void HandleSmartTouchNavigation(PointerRoutedEventArgs e, Action prevAction, Action nextAction);
    }

    internal interface ITextReaderViewHost
    {
        Grid RootGrid { get; }
        Grid ImageArea { get; }
        Grid TextArea { get; }
        Grid EpubArea { get; }
        ScrollViewer TextScrollViewer { get; }
        ItemsRepeater TextItemsRepeater { get; }
        CanvasControl MainCanvas { get; }
        CanvasControl AozoraTextCanvas { get; }
        CanvasControl VerticalTextCanvas { get; }
        FrameworkElement EmptyStatePanel { get; }
        Grid TextFastNavOverlay { get; }
        ContentControl RootFontControl { get; }
        ListView FileListView { get; }
        GridView FileGridView { get; }
        Pivot SidebarFavoritesPivot { get; }
        PathBreadcrumbControl CurrentPathBreadcrumb { get; }
        TextBlock NotificationText { get; }
        TextBlock FileNameText { get; }
        TextBlock ImageInfoText { get; }
        TextBlock TextProgressText { get; }
        TextBlock ImageIndexText { get; }
        MainToolbarControl MainToolbar { get; }
        ExplorerSidebarControl ExplorerSidebar { get; }

        string GetTextSettingsFilePath();
    }

    internal interface IImageNavigationHost
    {
        bool IsSideBySideMode { get; set; }
        bool AutoDoublePageForArchive { get; }
        bool NextImageOnRight { get; }
        bool SharpenEnabled { get; }
        int CurrentIndex { get; set; }
        List<ImageEntry> ImageEntries { get; set; }
        CanvasBitmap? CurrentBitmap { get; }
        PdfDocumentView? CurrentPdfDocument { get; }

        Task AddToRecentAsync(bool immediate);
        void SyncSidebarSelection(ImageEntry entry);
        void UpdateSideBySideButtonState();
        void UpdateNextImageSideButtonState();
        void UpdateStatusBar(ImageEntry entry, CanvasBitmap bitmap);
        Task DisplayCurrentImageAsync();
        Task NavigateToImageAsync(int index);

        /// <summary>
        /// 打开一个归档（ZIP/CBZ…）并把其中的图片作为当前导航序列；<paramref name="entryKey"/>
        /// 指定起始条目（null = 归档排序后的第一项）。漫画按"一章一个 CBZ"分章加载就走这里。
        /// </summary>
        // EN: Opens an archive (CBZ/ZIP) as the current image sequence, optionally starting at one entry.
        Task OpenArchiveAsync(string archivePath, string? entryKey);

        /// <summary>关闭当前归档（切到别处时调用；无归档时立即返回 true）。</summary>
        Task<bool> CloseArchiveAsync();

        ViewingContext CreateViewingContext();
        SharpenParams CreateSharpenParams();
        Task LoadImageResourceAndInvalidateAsync(
            string resourcePath,
            string cacheKey,
            CanvasDevice device,
            Action invalidate,
            Action? onMissing = null,
            Func<bool>? shouldKeepLoadedBitmap = null);
    }

    internal interface IEpubNavigationHost
    {
        int CurrentEpubChapterIndex { get; set; }
        int CurrentEpubPageIndex { get; set; }
        IReadOnlyList<string> EpubSpine { get; }
        List<EpubWin2DPage> EpubWin2DPages { get; }
        Dictionary<int, List<EpubWin2DPage>> EpubPreloadCache { get; }

        Task NavigateEpubAsync(int direction);
        Task LoadEpubChapterAsync(
            int index,
            bool fromEnd = false,
            int targetLine = -1,
            int targetBlockIndex = -1,
            int targetPage = -1,
            double? progress = null,
            CancellationToken token = default);
        void JumpToEpubTocItem(EpubTocItem item);
        void UpdateEpubStatus();
        void TriggerEpubResize();
    }

    internal interface IDocumentSearchHost
    {
        string? ActiveSearchQuery { get; }
        DocumentSearchService DocumentSearchService { get; }
        SearchHighlightService SearchHighlightService { get; }

        void ApplySearchHighlightsToTextBlock(TextBlock textBlock, string content, int lineNumber);
        DocumentSearchMatch? GetActiveSearchMatchFor(DocumentSearchKind kind);
    }

    internal interface IReaderLibraryHost
    {
        ArchiveSession ArchiveSession { get; }
        RecentService RecentService { get; }
        FavoritesService FavoritesService { get; }
        TocService TocService { get; }
        ImageResourceService ImageResourceService { get; }

        void UpdateFavoritesMenu();
        void UpdateRecentMenu();
        void UpdateWebDavServerList();
    }
}

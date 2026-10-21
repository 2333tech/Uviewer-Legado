using System.Threading.Tasks;
using Windows.System;

namespace Uviewer.Services
{
    public interface IKeyboardShortcutActions
    {
        bool IsColorPickerOpen { get; }
        bool IsFullscreen { get; }
        bool IsEpubMode { get; }
        bool IsTextMode { get; }
        bool IsAozoraMode { get; }
        /// <summary>阅读模式生效中（A 按钮开启）——↑ / ↓ 此时代表上一章 / 下一章。</summary>
        bool IsReadingMode { get; }
        bool IsVerticalMode { get; set; }
        bool ShouldInvertControls { get; }
        int CurrentEpubChapterIndex { get; set; }
        int EpubSpineCount { get; }
        int CurrentImageIndex { get; set; }
        int ImageEntriesCount { get; }
        bool HasPdfDocument { get; }
        bool IsAboutDialogActive { get; }
        bool IsDeleteDialogOpen { get; }
        bool IsTextOptionsDialogOpen { get; }
        bool IsToolbarCustomizationDialogOpen { get; }
        bool IsSearchOverlayOpen { get; }
        bool CanSearchCurrentDocument { get; }

        bool HandleDeleteDialogKey(VirtualKey key);

        void ToggleFullscreen();
        void ToggleMaximizeRestore();
        void CloseApp();
        Task ShowEpubGoToLineDialog();
        void ToggleFont();
        void ToggleVerticalMode();
        void DecreaseTextSize();
        void IncreaseTextSize();
        void ToggleSidebar();
        void ToggleSidebarWidth();
        void ToggleTheme();
        Task LoadEpubChapterAsync(int index);
        void ToggleSideBySide();
        Task NavigateDocumentPageAsync(int direction);
        Task DisplayCurrentImageAsync();
        Task NavigateToFileAsync(bool forward);
        // EN: Chapter navigation for reading mode; falls back to file navigation when not handled.
        /// <summary>阅读模式下的上一章 / 下一章；不接管时内部转回 <see cref="NavigateToFileAsync"/>。</summary>
        Task NavigateReadingChapterAsync(bool forward);
        Task DeleteSelectedFileAsync();
        Task AddToFavoritesAsync();
        void ToggleSharpening();
        Task ShowGoToLineDialog();
        Task NavigateToParentFolderAsync();
        Task OpenFileAsync();
        void ZoomIn();
        void ZoomOut();
        void FitToWindow();
        void ZoomActual();
        void ToggleAlwaysOnTop();
        void ToggleGlobalTheme();
        void TogglePin();
        void HideAboutDialog();
        void ShowSearchOverlay();
        void HideSearchOverlay();
    }
}

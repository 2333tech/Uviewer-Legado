namespace Uviewer.Services
{
    internal enum AppCommand
    {
        HandledOnly,
        ShowSearchOverlay,
        HideSearchOverlay,
        HideAboutDialog,
        ToggleFullscreen,
        ToggleMaximizeRestore,
        CloseApp,
        ShowEpubGoToLineDialog,
        ToggleFont,
        ToggleVerticalMode,
        DecreaseTextSize,
        IncreaseTextSize,
        ToggleSidebar,
        ToggleSidebarWidth,
        ToggleTheme,
        LoadEpubChapter,
        ToggleSideBySide,
        NavigateDocumentPage,
        NavigateToFirstImage,
        NavigateToLastImage,
        NavigateToFile,
        // EN: Previous/next chapter command used by arrow keys in reading mode.
        /// <summary>阅读模式下的 ↑ / ↓：上一章 / 下一章（章节由顶栏目录定义）。</summary>
        NavigateReadingChapter,
        DeleteSelectedFile,
        AddToFavorites,
        ToggleSharpening,
        ShowGoToLineDialog,
        NavigateToParentFolder,
        OpenFile,
        ZoomIn,
        ZoomOut,
        FitToWindow,
        ZoomActual,
        ToggleAlwaysOnTop,
        ToggleGlobalTheme,
        TogglePin
    }

    internal readonly record struct ShortcutRoute(AppCommand Command, int Direction = 0);
}

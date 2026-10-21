using Windows.System;

namespace Uviewer.Services
{
    internal readonly record struct ShortcutInput(VirtualKey Key, bool CtrlPressed);

    internal sealed class ShortcutContext
    {
        public bool IsColorPickerOpen { get; init; }
        public bool IsFullscreen { get; init; }
        public bool IsEpubMode { get; init; }
        public bool IsTextMode { get; init; }
        public bool IsAozoraMode { get; init; }
        // EN: Reading mode flag; arrow keys navigate chapters instead of files.
        /// <summary>阅读模式（顶栏 A 按钮）是否生效：↑ / ↓ 因此改为上一章 / 下一章。</summary>
        public bool IsReadingMode { get; init; }
        public bool IsVerticalMode { get; init; }
        public bool ShouldInvertControls { get; init; }
        public int CurrentEpubChapterIndex { get; init; }
        public int EpubSpineCount { get; init; }
        public int CurrentImageIndex { get; init; }
        public int ImageEntriesCount { get; init; }
        public bool HasPdfDocument { get; init; }
        public bool IsAboutDialogActive { get; init; }
        public bool IsDeleteDialogOpen { get; init; }
        public bool IsTextOptionsDialogOpen { get; init; }
        public bool IsToolbarCustomizationDialogOpen { get; init; }
        public bool IsSearchOverlayOpen { get; init; }
        public bool CanSearchCurrentDocument { get; init; }
    }
}

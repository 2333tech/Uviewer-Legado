using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using Uviewer.Models;
using Uviewer.Services;

namespace Uviewer.Controls
{
    public sealed partial class MainToolbarControl : UserControl
    {
        public event EventHandler? TextOptionsRequested;
        public event EventHandler? ChangeFontRequested;
        public event EventHandler<string>? EncodingSelected;
        public event EventHandler? ChangeColorsRequested;
        public event EventHandler? ChangeUiFontRequested;
        public event EventHandler? SelectExternalProgramRequested;
        public event EventHandler? ToolbarCustomizationChanged;
        /// <summary>书源搜索入口被点击（阶段 5 集成；由 MainWindow 订阅并打开搜索对话框）。</summary>
        public event EventHandler? LegadoSearchRequested;
        /// <summary>书源管理入口被点击（查看/编辑已导入的书源条目）。</summary>
        public event EventHandler? LegadoManageRequested;
        /// <summary>导入书源 JSON 入口被点击。</summary>
        public event EventHandler? LegadoImportRequested;
        /// <summary>下载设置入口被点击（设置整本下载的保存目录）。</summary>
        public event EventHandler? LegadoDownloadSettingsRequested;

        /// <summary>并行设置入口被点击（调整搜索 / 下载并发数）。</summary>
        public event EventHandler? LegadoParallelSettingsRequested;
        /// <summary>导入净化（替换）规则 JSON 入口被点击。</summary>
        public event EventHandler? LegadoImportReplaceRulesRequested;
        /// <summary>查看已导入净化规则（逐条开关）入口被点击。</summary>
        public event EventHandler? LegadoShowReplaceRulesRequested;
        /// <summary>导入标题 / 章节划分规则 JSON 入口被点击。</summary>
        public event EventHandler? LegadoImportTitleRulesRequested;
        /// <summary>查看已导入标题 / 章节规则（逐条开关）入口被点击。</summary>
        public event EventHandler? LegadoShowTitleRulesRequested;
        /// <summary>替换净化总开关被切换（参数 = 新状态）。</summary>
        public event EventHandler<bool>? LegadoPurifyEnabledChanged;
        /// <summary>章节缓存菜单项被点击（保存 / 删除当前文件的目录+章节缓存；切换后状态由 SetTextCacheState 回写）。</summary>
        public event EventHandler? TextCacheToggled;
        public event EventHandler<string>? LanguageSelected;
        public event EventHandler<bool>? MatchControlDirectionChanged;
        public event EventHandler<bool>? FastNavigationChanged;
        public event EventHandler<bool>? AllowMultipleInstancesChanged;
        public event EventHandler<bool>? KeepInTrayChanged;
        public event EventHandler<bool>? AutoDoublePageForArchiveChanged;
        public event EventHandler? AboutRequested;
        public event EventHandler? GlobalThemeToggleRequested;
        public event EventHandler? PinToggleRequested;
        public event EventHandler? AlwaysOnTopToggleRequested;
        public event EventHandler? ToggleSidebarRequested;
        public event EventHandler? AddToFavoritesRequested;
        public event EventHandler? OpenFileRequested;
        public event EventHandler? OpenFolderRequested;
        public event EventHandler? PdfTocRequested;
        public event ItemClickEventHandler? PdfTocItemClicked;
        public event EventHandler? PdfGoToPageRequested;
        public event RightTappedEventHandler? SearchRequested;
        public event EventHandler<double>? ZoomLevelChanged;
        public event EventHandler? ZoomOutRequested;
        public event EventHandler? ZoomInRequested;
        public event EventHandler? ZoomFitRequested;
        public event EventHandler? ZoomActualRequested;
        public event EventHandler? AozoraToggleRequested;
        public event EventHandler? VerticalToggleRequested;
        public event EventHandler? FontToggleRequested;
        public event EventHandler? RestorePointerCursorRequested;
        public event EventHandler? SetDefaultFont1Requested;
        public event EventHandler? SetDefaultFont2Requested;
        public event EventHandler? ResetDefaultFontsRequested;
        public event EventHandler? TocRequested;
        public event ItemClickEventHandler? TocItemClicked;
        public event EventHandler? GoToPageRequested;
        public event EventHandler? TextSizeDownRequested;
        public event EventHandler? TextSizeUpRequested;
        public event EventHandler? TextThemeToggleRequested;
        public event EventHandler? SideBySideToggleRequested;
        public event EventHandler? NextImageSideToggleRequested;
        public event EventHandler? SharpenToggleRequested;
        public event EventHandler? SharpenParamsResetRequested;
        public event EventHandler? PreviousFileRequested;
        public event EventHandler? PreviousPageRequested;
        public event EventHandler? NextPageRequested;
        public event EventHandler? NextFileRequested;
        public event EventHandler? FullscreenToggleRequested;
        public event EventHandler? CloseWindowRequested;
        public event EventHandler<BookmarkViewModel>? FavoriteItemClicked;
        public event EventHandler<BookmarkViewModel>? FavoriteRemoveClicked;
        public event EventHandler<BookmarkViewModel>? FavoritePinClicked;
        public event EventHandler<BookmarkViewModel>? RecentItemClicked;
        public event EventHandler<BookmarkViewModel>? RecentRemoveClicked;

        public static readonly DependencyProperty ImageOptionsProperty =
            DependencyProperty.Register(
                nameof(ImageOptions),
                typeof(ImageProcessingViewModel),
                typeof(MainToolbarControl),
                new PropertyMetadata(null, OnImageOptionsChanged));

        public ImageProcessingViewModel? ImageOptions
        {
            get => (ImageProcessingViewModel?)GetValue(ImageOptionsProperty);
            set => SetValue(ImageOptionsProperty, value);
        }

        public MainToolbarControl()
        {
            InitializeComponent();
            ApplyImageOptionsDataContext();
            HookEvents();
            InitializeToolbarCustomization();
        }

        private static void OnImageOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MainToolbarControl control)
            {
                control.ApplyImageOptionsDataContext();
            }
        }

        private void ApplyImageOptionsDataContext()
        {
            SharpenSettingsPanel.DataContext = ImageOptions;
        }

        private void HookEvents()
        {
            TextOptionsMenuItem.Click += (_, _) => TextOptionsRequested?.Invoke(this, EventArgs.Empty);
            ChangeFontMenuItem.Click += (_, _) => ChangeFontRequested?.Invoke(this, EventArgs.Empty);
            EncAutoItem.Click += EncodingItem_Click;
            EncUtf8Item.Click += EncodingItem_Click;
            EncEucKrItem.Click += EncodingItem_Click;
            EncSjisItem.Click += EncodingItem_Click;
            EncJohabItem.Click += EncodingItem_Click;
            EncJisItem.Click += EncodingItem_Click;
            EncGb18030Item.Click += EncodingItem_Click;
            EncGbkItem.Click += EncodingItem_Click;
            EncBig5Item.Click += EncodingItem_Click;
            ChangeColorsMenuItem.Click += (_, _) => ChangeColorsRequested?.Invoke(this, EventArgs.Empty);
            ChangeUiFontMenuItem.Click += (_, _) => ChangeUiFontRequested?.Invoke(this, EventArgs.Empty);
            SelectExternalProgramMenuItem.Click += (_, _) => SelectExternalProgramRequested?.Invoke(this, EventArgs.Empty);
            CustomizeToolbarMenuItem.Click += async (_, _) => await ShowToolbarCustomizationDialogAsync();
            LegadoSearchMenuItem.Click += (_, _) => LegadoSearchRequested?.Invoke(this, EventArgs.Empty);
            LegadoManageMenuItem.Click += (_, _) => LegadoManageRequested?.Invoke(this, EventArgs.Empty);
            LegadoImportMenuItem.Click += (_, _) => LegadoImportRequested?.Invoke(this, EventArgs.Empty);
            LegadoDownloadSettingsMenuItem.Click += (_, _) => LegadoDownloadSettingsRequested?.Invoke(this, EventArgs.Empty);
            LegadoParallelSettingsMenuItem.Click += (_, _) => LegadoParallelSettingsRequested?.Invoke(this, EventArgs.Empty);
            LegadoImportReplaceRulesMenuItem.Click += (_, _) => LegadoImportReplaceRulesRequested?.Invoke(this, EventArgs.Empty);
            LegadoShowReplaceRulesMenuItem.Click += (_, _) => LegadoShowReplaceRulesRequested?.Invoke(this, EventArgs.Empty);
            LegadoImportTitleRulesMenuItem.Click += (_, _) => LegadoImportTitleRulesRequested?.Invoke(this, EventArgs.Empty);
            LegadoShowTitleRulesMenuItem.Click += (_, _) => LegadoShowTitleRulesRequested?.Invoke(this, EventArgs.Empty);
            LegadoPurifyEnabledMenuItem.Click += (_, _) =>
                LegadoPurifyEnabledChanged?.Invoke(this, LegadoPurifyEnabledMenuItem.IsChecked);
            TextCacheMenuItem.Click += (_, _) => TextCacheToggled?.Invoke(this, EventArgs.Empty);

            // 「阅读模式（A 按钮）下才可用」的菜单项在此登记：
            // 未开启阅读模式时统一置灰，并把悬停提示换成"在阅读模式下有效"。
            // 用登记表而不是逐项写死，是因为缓存按钮后续可能复用为其它用途——
            // 新增 / 替换这类菜单项只要在这里加一行，门控与提示自动跟随。
            RegisterReadingModeMenuItem(LegadoPurifyEnabledMenuItem, () => Strings.LegadoPurifyEnabledHint);
            RegisterReadingModeMenuItem(TextCacheMenuItem, () => Strings.TextCacheHint, () => _textCacheSourceAvailable);

            LangAutoItem.Click += LanguageItem_Click;
            LangKoItem.Click += LanguageItem_Click;
            LangEnItem.Click += LanguageItem_Click;
            LangJaItem.Click += LanguageItem_Click;
            LangZhHansItem.Click += LanguageItem_Click;
            LangZhHantItem.Click += LanguageItem_Click;
            LangViItem.Click += LanguageItem_Click;
            MatchControlDirectionMenuItem.Click += (_, _) => MatchControlDirectionChanged?.Invoke(this, MatchControlDirectionMenuItem.IsChecked);
            FastNavigationMenuItem.Click += (_, _) => FastNavigationChanged?.Invoke(this, FastNavigationMenuItem.IsChecked);
            AllowMultipleInstancesMenuItem.Click += (_, _) => RaiseAllowMultipleInstancesChanged(AllowMultipleInstancesMenuItem.IsChecked);
            KeepInTrayMenuItem.Click += (_, _) => KeepInTrayChanged?.Invoke(this, KeepInTrayMenuItem.IsChecked);
            AutoDoublePageForArchiveMenuItem.Click += (_, _) => AutoDoublePageForArchiveChanged?.Invoke(this, AutoDoublePageForArchiveMenuItem.IsChecked);
            AboutMenuItem.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);

            GlobalThemeToggleButton.Click += (_, _) => GlobalThemeToggleRequested?.Invoke(this, EventArgs.Empty);
            PinButton.Click += (_, _) => PinToggleRequested?.Invoke(this, EventArgs.Empty);
            AlwaysOnTopButton.Click += (_, _) => AlwaysOnTopToggleRequested?.Invoke(this, EventArgs.Empty);
            AllowMultipleInstancesButton.Click += (_, _) => RaiseAllowMultipleInstancesChanged(AllowMultipleInstancesButton.IsChecked ?? false);
            ToggleSidebarButton.Click += (_, _) => ToggleSidebarRequested?.Invoke(this, EventArgs.Empty);
            AddToFavoritesButton.Click += (_, _) => AddToFavoritesRequested?.Invoke(this, EventArgs.Empty);
            OpenFileButton.Click += (_, _) => OpenFileRequested?.Invoke(this, EventArgs.Empty);
            OpenFolderButton.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);

            FileFavoritesList.ItemClicked += (_, item) => FavoriteItemClicked?.Invoke(this, item);
            FileFavoritesList.RemoveClicked += (_, item) => FavoriteRemoveClicked?.Invoke(this, item);
            FileFavoritesList.PinClicked += (_, item) => FavoritePinClicked?.Invoke(this, item);
            FolderFavoritesList.ItemClicked += (_, item) => FavoriteItemClicked?.Invoke(this, item);
            FolderFavoritesList.RemoveClicked += (_, item) => FavoriteRemoveClicked?.Invoke(this, item);
            FolderFavoritesList.PinClicked += (_, item) => FavoritePinClicked?.Invoke(this, item);
            RecentList.ItemClicked += (_, item) => RecentItemClicked?.Invoke(this, item);
            RecentList.RemoveClicked += (_, item) => RecentRemoveClicked?.Invoke(this, item);

            PdfTocButton.Click += (_, _) => PdfTocRequested?.Invoke(this, EventArgs.Empty);
            PdfTocListView.ItemClick += (sender, args) => PdfTocItemClicked?.Invoke(sender, args);
            PdfGoToPageButton.Click += (_, _) => PdfGoToPageRequested?.Invoke(this, EventArgs.Empty);
            PdfGoToPageButton.RightTapped += (sender, args) => SearchRequested?.Invoke(sender, args);
            ZoomOutButton.Click += (_, _) =>
            {
                ZoomLevelChanged?.Invoke(this, -1);
                ZoomOutRequested?.Invoke(this, EventArgs.Empty);
            };
            ZoomInButton.Click += (_, _) =>
            {
                ZoomLevelChanged?.Invoke(this, 1);
                ZoomInRequested?.Invoke(this, EventArgs.Empty);
            };
            ZoomFitButton.Click += (_, _) => ZoomFitRequested?.Invoke(this, EventArgs.Empty);
            ZoomActualButton.Click += (_, _) => ZoomActualRequested?.Invoke(this, EventArgs.Empty);

            AozoraToggleButton.Click += (_, _) => AozoraToggleRequested?.Invoke(this, EventArgs.Empty);
            VerticalToggleButton.Click += (_, _) => VerticalToggleRequested?.Invoke(this, EventArgs.Empty);
            FontToggleButton.Click += (_, _) => FontToggleRequested?.Invoke(this, EventArgs.Empty);
            FontToggleButton.RightTapped += (_, _) => RestorePointerCursorRequested?.Invoke(this, EventArgs.Empty);
            FontSettingsFlyout.Opening += (_, _) => RestorePointerCursorRequested?.Invoke(this, EventArgs.Empty);
            FontSettingsFlyout.Opened += (_, _) => RestorePointerCursorRequested?.Invoke(this, EventArgs.Empty);
            SetDefaultFont1MenuItem.Click += (_, _) => SetDefaultFont1Requested?.Invoke(this, EventArgs.Empty);
            SetDefaultFont2MenuItem.Click += (_, _) => SetDefaultFont2Requested?.Invoke(this, EventArgs.Empty);
            ResetDefaultFontsMenuItem.Click += (_, _) => ResetDefaultFontsRequested?.Invoke(this, EventArgs.Empty);
            TocButton.Click += (_, _) => TocRequested?.Invoke(this, EventArgs.Empty);
            TocListView.ItemClick += (sender, args) => TocItemClicked?.Invoke(sender, args);
            GoToPageButton.Click += (_, _) => GoToPageRequested?.Invoke(this, EventArgs.Empty);
            GoToPageButton.RightTapped += (sender, args) => SearchRequested?.Invoke(sender, args);
            TextSizeDownButton.Click += (_, _) => TextSizeDownRequested?.Invoke(this, EventArgs.Empty);
            TextSizeUpButton.Click += (_, _) => TextSizeUpRequested?.Invoke(this, EventArgs.Empty);
            ThemeToggleButton.Click += (_, _) => TextThemeToggleRequested?.Invoke(this, EventArgs.Empty);

            SideBySideButton.Click += (_, _) => SideBySideToggleRequested?.Invoke(this, EventArgs.Empty);
            NextImageSideButton.Click += (_, _) => NextImageSideToggleRequested?.Invoke(this, EventArgs.Empty);
            SharpenButton.Click += (_, _) => SharpenToggleRequested?.Invoke(this, EventArgs.Empty);
            SharpenParamsResetButton.Click += (_, _) => SharpenParamsResetRequested?.Invoke(this, EventArgs.Empty);
            PrevFileButton.Click += (_, _) => PreviousFileRequested?.Invoke(this, EventArgs.Empty);
            PrevPageButton.Click += (_, _) => PreviousPageRequested?.Invoke(this, EventArgs.Empty);
            NextPageButton.Click += (_, _) => NextPageRequested?.Invoke(this, EventArgs.Empty);
            NextFileButton.Click += (_, _) => NextFileRequested?.Invoke(this, EventArgs.Empty);
            FullscreenButton.Click += (_, _) => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
            CloseWindowButton.Click += (_, _) => CloseWindowRequested?.Invoke(this, EventArgs.Empty);
        }

        private void EncodingItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item && item.Tag is string tag)
            {
                EncodingSelected?.Invoke(this, tag);
            }
        }

        private void LanguageItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem item && item.Tag is string language)
            {
                LanguageSelected?.Invoke(this, language);
            }
        }

        private void RaiseAllowMultipleInstancesChanged(bool isChecked)
        {
            // 메뉴 항목과 툴바 버튼의 체크 상태를 맞춘 뒤 동일한 이벤트로 알립니다.
            AllowMultipleInstancesMenuItem.IsChecked = isChecked;
            AllowMultipleInstancesButton.IsChecked = isChecked;
            AllowMultipleInstancesChanged?.Invoke(this, isChecked);
        }

        public void ApplyLocalization()
        {
            ToolTipService.SetToolTip(ToggleSidebarButton, Strings.ToggleSidebarTooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ToggleSidebarButton, Strings.ToggleSidebarTooltip);
            ToolTipService.SetToolTip(OpenFileButton, Strings.OpenFileTooltip);
            ToolTipService.SetToolTip(OpenFolderButton, Strings.OpenFolderTooltip);
            ToolTipService.SetToolTip(ZoomOutButton, Strings.ZoomOutTooltip);
            ToolTipService.SetToolTip(ZoomInButton, Strings.ZoomInTooltip);
            ToolTipService.SetToolTip(ZoomFitButton, Strings.ZoomFitTooltip);
            ToolTipService.SetToolTip(ZoomActualButton, Strings.ZoomActualTooltip);
            ToolTipService.SetToolTip(SharpenButton, Strings.SharpenTooltip);
            ToolTipService.SetToolTip(SideBySideButton, Strings.SideBySideTooltip);
            ToolTipService.SetToolTip(NextImageSideButton, Strings.NextImageSideTooltip);
            ToolTipService.SetToolTip(AozoraToggleButton, Strings.AozoraTooltip);
            ToolTipService.SetToolTip(VerticalToggleButton, Strings.VerticalTooltip);
            ToolTipService.SetToolTip(FontToggleButton, Strings.FontTooltip);
            ToolTipService.SetToolTip(GoToPageButton, Strings.GoToPageTooltip);
            ToolTipService.SetToolTip(TextSizeDownButton, Strings.TextSizeDownTooltip);
            ToolTipService.SetToolTip(TextSizeUpButton, Strings.TextSizeUpTooltip);
            ToolTipService.SetToolTip(ThemeToggleButton, Strings.ThemeTooltip);
            ToolTipService.SetToolTip(FullscreenButton, Strings.FullscreenTooltip);
            ToolTipService.SetToolTip(CloseWindowButton, Strings.CloseWindowTooltip);
            ToolTipService.SetToolTip(RecentButton, Strings.RecentTooltip);
            ToolTipService.SetToolTip(FavoritesButton, Strings.FavoritesTooltip);
            ToolTipService.SetToolTip(TocButton, Strings.TocTooltip);
            ToolTipService.SetToolTip(PdfTocButton, Strings.TocTooltip);
            UpdateImageGoToPageTooltip();
            ToolTipService.SetToolTip(SettingsButton, Strings.SettingsTooltip);
            ToolTipService.SetToolTip(MainToolbarOverflowButton, Strings.MoreCommandsTooltip);
            ToolTipService.SetToolTip(PinButton, Strings.PinTooltip);
            ToolTipService.SetToolTip(AlwaysOnTopButton, Strings.AlwaysOnTopTooltip);
            ToolTipService.SetToolTip(AllowMultipleInstancesButton, Strings.AllowMultipleInstances);
            // 语义随阅读模式切换：普通"上/下一个文件"，阅读模式"上/下一章"
            ApplyFileNavigationTooltips();

            ToolTipService.SetToolTip(PrevPageButton, Strings.PrevPageTooltip);
            ToolTipService.SetToolTip(NextPageButton, Strings.NextPageTooltip);

            AddToFavoritesButton.Content = Strings.AddToFavorites;
            TextOptionsMenuItem.Text = Strings.TextOptions;
            ChangeFontMenuItem.Text = Strings.ChangeFont;
            ChangeUiFontMenuItem.Text = Strings.ChangeUiFont;
            EncodingMenuItem.Text = Strings.EncodingMenu;
            EncAutoItem.Text = Strings.EncAuto;
            EncUtf8Item.Text = Strings.EncUtf8;
            EncEucKrItem.Text = Strings.EncEucKr;
            EncSjisItem.Text = Strings.EncSjis;
            EncJohabItem.Text = Strings.EncJohab;
            EncJisItem.Text = Strings.EncJis;
            EncGb18030Item.Text = Strings.EncGb18030;
            EncGbkItem.Text = Strings.EncGbk;
            EncBig5Item.Text = Strings.EncBig5;
            ChangeColorsMenuItem.Text = Strings.ChangeColors;
            SelectExternalProgramMenuItem.Text = string.IsNullOrWhiteSpace(_externalProgramPath)
                ? Strings.ExternalProgramSettings
                : Strings.ExternalProgramMenuWithName(System.IO.Path.GetFileName(_externalProgramPath));
            CustomizeToolbarMenuItem.Text = Strings.ToolbarCustomization;
            LegadoSearchMenuItem.Text = Strings.LegadoSearch;
            LegadoRootMenuItem.Text = Strings.LegadoManageSources;
            LegadoImportMenuItem.Text = Strings.LegadoImportSources;
            LegadoDownloadSettingsMenuItem.Text = Strings.LegadoDownloadSettings;
            LegadoImportReplaceRulesMenuItem.Text = Strings.LegadoImportReplaceRules;
            LegadoShowReplaceRulesMenuItem.Text = Strings.LegadoShowReplaceRules;
            LegadoImportTitleRulesMenuItem.Text = Strings.LegadoImportTitleRules;
            LegadoShowTitleRulesMenuItem.Text = Strings.LegadoShowTitleRules;
            LegadoPurifyEnabledMenuItem.Text = Strings.LegadoPurifyEnabled;
            ToolTipService.SetToolTip(LegadoPurifyEnabledMenuItem, Strings.LegadoPurifyEnabledHint);
            TextCacheMenuItem.Text = Strings.TextCache;
            ToolTipService.SetToolTip(TextCacheMenuItem, Strings.TextCacheHint);
            ToolTipService.SetToolTip(LegadoSearchMenuItem, Strings.LegadoSearchHint);
            ToolTipService.SetToolTip(LegadoRootMenuItem, Strings.LegadoManageSourcesHint);
            ToolTipService.SetToolTip(LegadoDownloadSettingsMenuItem, Strings.LegadoDownloadSettingsHint);
            ToolTipService.SetToolTip(LegadoShowReplaceRulesMenuItem, Strings.LegadoShowReplaceRulesHint);
            ToolTipService.SetToolTip(LegadoShowTitleRulesMenuItem, Strings.LegadoShowTitleRulesHint);
            MatchControlDirectionMenuItem.Text = Strings.MatchControlDirection;
            FastNavigationMenuItem.Text = Strings.FastNavigation;
            ToolTipService.SetToolTip(MatchControlDirectionMenuItem, Strings.MatchControlDirectionTooltip);
            AllowMultipleInstancesMenuItem.Text = Strings.AllowMultipleInstances;
            ToolTipService.SetToolTip(AllowMultipleInstancesMenuItem, Strings.AllowMultipleInstancesTooltip);
            KeepInTrayMenuItem.Text = Strings.KeepInTray;
            ToolTipService.SetToolTip(KeepInTrayMenuItem, Strings.KeepInTrayTooltip);
            AutoDoublePageForArchiveMenuItem.Text = Strings.AutoDoublePageForArchive;
            AboutMenuItem.Text = Strings.About;

            LanguageMenuItem.Text = Strings.LanguageSelection;
            LangAutoItem.Text = Strings.LanguageAuto;
            LangKoItem.Text = Strings.LanguageKorean;
            LangEnItem.Text = Strings.LanguageEnglish;
            LangJaItem.Text = Strings.LanguageJapanese;
            LangZhHansItem.Text = Strings.LanguageChineseSimplified;
            LangZhHantItem.Text = Strings.LanguageChineseTraditional;
            LangViItem.Text = Strings.LanguageVietnamese;

            FileFavoritesHeaderText.Text = Strings.FavoritesFiles;
            FolderFavoritesHeaderText.Text = Strings.FavoritesFolders;

            SharpenSettingsTitleText.Text = Strings.SharpenSettingsTitle;
            UpscaleLabel.Text = Strings.UpscaleFactorLabel;
            SharpenAmountLabel.Text = Strings.SharpenAmountLabel;
            SharpenThresholdLabel.Text = Strings.SharpenThresholdLabel;
            UnsharpAmountLabel.Text = Strings.UnsharpAmountLabel;
            UnsharpRadiusLabel.Text = Strings.UnsharpRadiusLabel;
            SharpenParamsResetButton.Content = Strings.ResetButton;

            Bindings.Update();
            // 语言切换会重设各菜单项提示，这里再走一遍阅读模式门控，
            // 保证"置灰项显示『在阅读模式下有效』"在新语言下也成立
            ApplyReadingModeMenuState();
            QueueToolbarOverflowUpdate();
        }

        public void SetEncodingSelection(string encodingName)
        {
            EncAutoItem.IsChecked = encodingName == "Auto Detect" || encodingName == "Auto";
            EncUtf8Item.IsChecked = encodingName == "UTF-8";
            EncEucKrItem.IsChecked = encodingName == "EUC-KR";
            EncSjisItem.IsChecked = encodingName == "Shift-JIS";
            EncJohabItem.IsChecked = encodingName == "Johab";
            EncJisItem.IsChecked = encodingName == "JIS" || encodingName == "ISO-2022-JP";
            EncGb18030Item.IsChecked = encodingName == "GB18030";
            EncGbkItem.IsChecked = encodingName == "GBK";
            EncBig5Item.IsChecked = encodingName == "Big5";
        }

        public void SetLanguageSelection(string language)
        {
            var current = string.IsNullOrEmpty(language) ? "Auto" : language;
            LangAutoItem.IsChecked = current == "Auto";
            LangKoItem.IsChecked = current == "ko-KR";
            LangEnItem.IsChecked = current == "en-US";
            LangJaItem.IsChecked = current == "ja-JP";
            LangZhHansItem.IsChecked = current == "zh-Hans";
            LangZhHantItem.IsChecked = current == "zh-Hant";
            LangViItem.IsChecked = current == "vi-VN";
        }

        public void SetWindowOptionStates(
            bool matchControlDirection,
            bool fastNavigationEnabled,
            bool allowMultipleInstances,
            bool keepInTray,
            bool autoDoublePageForArchive,
            bool alwaysOnTop)
        {
            MatchControlDirectionMenuItem.IsChecked = matchControlDirection;
            FastNavigationMenuItem.IsChecked = fastNavigationEnabled;
            KeepInTrayMenuItem.IsChecked = keepInTray;
            AllowMultipleInstancesMenuItem.IsChecked = allowMultipleInstances;
            AllowMultipleInstancesButton.IsChecked = allowMultipleInstances;
            AutoDoublePageForArchiveMenuItem.IsChecked = autoDoublePageForArchive;
            AlwaysOnTopButton.IsChecked = alwaysOnTop;
        }

        public void SetMatchControlDirection(bool value) => MatchControlDirectionMenuItem.IsChecked = value;

        /// <summary>回写替换净化总开关的勾选状态（从设置读回后同步）。</summary>
        public void SetLegadoPurifyEnabled(bool value) => LegadoPurifyEnabledMenuItem.IsChecked = value;

        /// <summary>
        /// 回写章节缓存菜单项状态：勾选 = 当前文件有有效缓存；可用 = 正打开着文本源。
        /// 点击语义由控制器决定（有缓存 → 删；无 → 保存），勾选只是状态镜像。
        /// </summary>
        public void SetTextCacheState(bool isChecked, bool isEnabled)
        {
            TextCacheMenuItem.IsChecked = isChecked;
            _textCacheSourceAvailable = isEnabled;
            // 真正的 IsEnabled 由"自身可用 && 阅读模式开启"共同决定
            ApplyReadingModeMenuState();
        }
        public void SetAllowMultipleInstances(bool value)
        {
            AllowMultipleInstancesMenuItem.IsChecked = value;
            AllowMultipleInstancesButton.IsChecked = value;
        }

        // 트레이에 유지와 다중 실행은 함께 사용할 수 있습니다.
        public void SetKeepInTray(bool value) => KeepInTrayMenuItem.IsChecked = value;

        public void SetAutoDoublePageForArchive(bool value) => AutoDoublePageForArchiveMenuItem.IsChecked = value;
        public void SetAlwaysOnTopState(bool value) => AlwaysOnTopButton.IsChecked = value;

        private string _externalProgramPath = "";

        public void SetExternalProgramPath(string path)
        {
            _externalProgramPath = path ?? string.Empty;
            SelectExternalProgramMenuItem.Text = string.IsNullOrWhiteSpace(_externalProgramPath)
                ? Strings.ExternalProgramSettings
                : Strings.ExternalProgramMenuWithName(System.IO.Path.GetFileName(_externalProgramPath));
            ToolTipService.SetToolTip(
                SelectExternalProgramMenuItem,
                string.IsNullOrWhiteSpace(_externalProgramPath) ? null : _externalProgramPath);
        }

        public void SetAozoraToggleChecked(bool isChecked) => AozoraToggleButton.IsChecked = isChecked;

        // ------------------------------------------------------------------
        // 阅读模式（顶栏 A 按钮）联动的设置菜单项
        //
        // 语义：这些菜单项只在阅读模式开启时才有意义（替换净化、章节缓存），
        // 关闭时置灰 + 提示"在阅读模式下有效"。
        // 实现用登记表而不是 if 判断具体控件——缓存按钮计划复用，届时只需换登记项。
        // ------------------------------------------------------------------

        private sealed record ReadingModeMenuEntry(
            ToggleMenuFlyoutItem Item, Func<string> Hint, Func<bool>? IsAvailable);

        private readonly List<ReadingModeMenuEntry> _readingModeMenuItems = new();
        private bool _isReadingModeActive;

        /// <summary>章节缓存菜单项自身是否可用（当前是否打开着文本源），由控制器回写。</summary>
        private bool _textCacheSourceAvailable;

        /// <summary>「上一个 / 下一个」按钮当前是否代表"上一章 / 下一章"。</summary>
        private bool _isFileNavAsChapter;

        // EN: Registers a menu item that is only enabled while reading mode is active (greyed out + hint otherwise).
        /// <summary>
        /// 登记一个「仅阅读模式可用」的菜单项。
        /// </summary>
        /// <param name="item">菜单项。</param>
        /// <param name="hint">阅读模式生效时显示的原提示（延迟求值，语言切换后仍正确）。</param>
        /// <param name="isAvailable">该项自身的可用条件（阅读模式之外，如"当前有文本源"）；null = 只看阅读模式。</param>
        public void RegisterReadingModeMenuItem(
            ToggleMenuFlyoutItem item, Func<string> hint, Func<bool>? isAvailable = null)
        {
            _readingModeMenuItems.Add(new ReadingModeMenuEntry(item, hint, isAvailable));
            ApplyReadingModeMenuState();
        }

        // EN: Applies reading-mode state to every registered menu item.
        /// <summary>阅读模式开 / 关 → 联动菜单项的可用性与提示文案。</summary>
        public void SetReadingModeActive(bool isActive)
        {
            _isReadingModeActive = isActive;
            ApplyReadingModeMenuState();
        }

        // EN: Switches previous/next buttons between file navigation and chapter navigation (tooltip follows).
        /// <summary>
        /// 「上一个 / 下一个」按钮的语义切换。
        /// <para>阅读模式开启时，这两个按钮（与键盘 ↑ / ↓）改意为「上一章 / 下一章」——
        /// 章节由顶栏目录下拉栏定义；关闭时恢复"上一个 / 下一个文件"。</para>
        /// </summary>
        public void SetFileNavigationMode(bool asChapterNavigation)
        {
            if (_isFileNavAsChapter == asChapterNavigation) return;
            _isFileNavAsChapter = asChapterNavigation;
            ApplyFileNavigationTooltips();
        }

        private void ApplyFileNavigationTooltips()
        {
            ToolTipService.SetToolTip(PrevFileButton,
                _isFileNavAsChapter ? Strings.PrevChapterTooltip : Strings.PrevFileTooltip);
            ToolTipService.SetToolTip(NextFileButton,
                _isFileNavAsChapter ? Strings.NextChapterTooltip : Strings.NextFileTooltip);
        }

        private void ApplyReadingModeMenuState()
        {
            foreach (var entry in _readingModeMenuItems)
            {
                var available = _isReadingModeActive && (entry.IsAvailable?.Invoke() ?? true);
                entry.Item.IsEnabled = available;
                ToolTipService.SetToolTip(
                    entry.Item,
                    available ? entry.Hint() : Strings.ReadingModeOnlyHint);
            }
        }

        public void SetAozoraToggleState(bool? isChecked = null, bool? isEnabled = null)
        {
            if (isChecked.HasValue) AozoraToggleButton.IsChecked = isChecked.Value;
            if (isEnabled.HasValue) AozoraToggleButton.IsEnabled = isEnabled.Value;
        }

        public bool IsVerticalToggleChecked => VerticalToggleButton.IsChecked ?? false;

        public void SetVerticalToggleState(bool? isChecked = null, bool? isEnabled = null)
        {
            if (isChecked.HasValue) VerticalToggleButton.IsChecked = isChecked.Value;
            if (isEnabled.HasValue) VerticalToggleButton.IsEnabled = isEnabled.Value;
        }

        public void SetTextSizeLevel(double fontSize) => TextSizeLevelText.Text = fontSize.ToString();

        public void SetZoomLevel(double zoomLevel) => ZoomLevelText.Text = $"{(int)(zoomLevel * 100)}%";

        public void SetImageToolbarVisible(bool isVisible)
        {
            _isImageToolbarAvailable = isVisible;
            UpdateToolbarItemVisibility();
        }

        public void SetTextToolbarVisible(bool isVisible)
        {
            _isTextToolbarAvailable = isVisible;
            UpdateToolbarItemVisibility();
        }

        // EN: Shows/hides the TXT TOC button (no TOC is generated when reading mode is off).
        /// <summary>
        /// 文本目录按钮是否可用。
        /// <para>阅读模式未开启的 TXT 不生成目录，按钮随之隐藏（目录为空，点了也没意义）。</para>
        /// </summary>
        public void SetTextTocAvailable(bool isAvailable)
        {
            if (_isTextTocAvailable == isAvailable) return;
            _isTextTocAvailable = isAvailable;
            UpdateToolbarItemVisibility();
        }

        public void SetSideBySideToolbarVisible(bool isVisible)
        {
            _isSideBySideToolbarAvailable = isVisible;
            UpdateToolbarItemVisibility();
        }

        public void SetSharpenControlsVisible(bool isVisible)
        {
            _isSharpenAvailable = isVisible;
            UpdateToolbarItemVisibility();
        }

        public void SetPdfTocVisible(bool isVisible)
        {
            _isPdfTocAvailable = isVisible;
            UpdateToolbarItemVisibility();
        }

        public void SetPdfGoToPageVisible(bool isVisible)
        {
            _isPdfGoToPageAvailable = isVisible;
            UpdateImageGoToPageTooltip();
            UpdateToolbarItemVisibility();
        }

        private void UpdateImageGoToPageTooltip() =>
            ToolTipService.SetToolTip(PdfGoToPageButton, _isPdfGoToPageAvailable
                ? Strings.PdfGoToPageTooltip
                : $"{Strings.GoToPageTitle} (G)");

        public void SetSharpenState(bool isEnabled) =>
            ImageToolbarStateService.ApplySharpenState(SharpenButton, SharpenIcon, isEnabled);

        public void SetSideBySideState(bool isEnabled) =>
            ImageToolbarStateService.ApplySideBySideState(SideBySideButton, SideBySideText, isEnabled);

        public void SetNextImageSideState(bool nextImageOnRight) =>
            ImageToolbarStateService.ApplyNextImageSideState(NextImageSideText, nextImageOnRight);

        public void SetFullscreenState(bool isFullscreen) =>
            FullscreenIcon.Glyph = isFullscreen ? "\uE73F" : "\uE740";

        public void SetSidebarState(bool isVisible)
        {
            ToggleSidebarButton.IsChecked = isVisible;
            ToggleSidebarIcon.Glyph = isVisible ? "\uE8A0" : "\uE89F";
        }

        public void SetPinState(bool isPinned)
        {
            PinButton.IsChecked = isPinned;
            PinIcon.Glyph = isPinned ? "\uE890" : "\uE77A";
        }

        public void SetThemeState(ElementTheme theme)
        {
            ThemeIcon.Glyph = theme == ElementTheme.Dark ? "\uE706" : "\uE708";
            GlobalThemeToggleButton.IsChecked = theme == ElementTheme.Dark;
            UpdateThemeToggleButtonTooltip(theme);
        }

        public void UpdateThemeToggleButtonTooltip(ElementTheme theme)
        {
            ToolTipService.SetToolTip(
                GlobalThemeToggleButton,
                theme == ElementTheme.Dark ? Strings.LightModeTooltip : Strings.DarkModeTooltip);
        }

        public void SetFavoriteSources(
            ObservableCollection<BookmarkViewModel> fileFavorites,
            ObservableCollection<BookmarkViewModel> folderFavorites,
            string emptyMessage)
        {
            FileFavoritesList.ItemsSource = fileFavorites;
            FileFavoritesList.EmptyMessage = emptyMessage;
            FolderFavoritesList.ItemsSource = folderFavorites;
            FolderFavoritesList.EmptyMessage = emptyMessage;
        }

        public void SetRecentSource(ObservableCollection<BookmarkViewModel> recentItems, string emptyMessage)
        {
            RecentList.ItemsSource = recentItems;
            RecentList.EmptyMessage = emptyMessage;
        }

        public void HideFavoritesFlyout() => FavoritesFlyout.Hide();
        public void HideRecentFlyout() => RecentFlyout.Hide();

        public void SetTextTocTitle(string title) => SetFlyoutTitle(TocFlyout, title);
        public void SetPdfTocTitle(string title) => SetFlyoutTitle(PdfTocFlyout, title);
        public void SetTextTocItems(object itemsSource) => TocListView.ItemsSource = itemsSource;
        public void SetPdfTocItems(object itemsSource) => PdfTocListView.ItemsSource = itemsSource;
        public void HideTextTocFlyout() => TocFlyout.Hide();
        public void HidePdfTocFlyout() => PdfTocFlyout.Hide();

        public void ScrollTextTocIntoView(object item) => ScrollIntoView(TocListView, item);
        public void ScrollPdfTocIntoView(object item) => ScrollIntoView(PdfTocListView, item);

        public void UpdateDefaultFontMenu(string defaultFont1, string defaultFont2)
        {
            SetDefaultFont1MenuItem.Text = $"{Strings.DefaultFont1Label}: {defaultFont1}";
            SetDefaultFont2MenuItem.Text = $"{Strings.DefaultFont2Label}: {defaultFont2}";
            ResetDefaultFontsMenuItem.Text = Strings.ResetButton;
        }

        public void ApplyUiFont(FontFamily fontFamily)
        {
            ZoomLevelText.FontFamily = fontFamily;
            TextSizeLevelText.FontFamily = fontFamily;
            FavoritesPivot.FontFamily = fontFamily;
            FileFavoritesHeaderText.FontFamily = fontFamily;
            FolderFavoritesHeaderText.FontFamily = fontFamily;
            ApplyMenuFont(SettingsMenuFlyout.Items, fontFamily);
            ApplyMenuFont(FontSettingsFlyout.Items, fontFamily);

            SharpenSettingsTitleText.FontFamily = fontFamily;
            UpscaleLabel.FontFamily = fontFamily;
            UpscaleValueText.FontFamily = fontFamily;
            SharpenAmountLabel.FontFamily = fontFamily;
            SharpenValueText.FontFamily = fontFamily;
            SharpenThresholdLabel.FontFamily = fontFamily;
            SharpenThresholdValueText.FontFamily = fontFamily;
            UnsharpAmountLabel.FontFamily = fontFamily;
            UnsharpAmountValueText.FontFamily = fontFamily;
            UnsharpRadiusLabel.FontFamily = fontFamily;
            UnsharpRadiusValueText.FontFamily = fontFamily;
            UpscaleSlider.FontFamily = fontFamily;
            SharpenSlider.FontFamily = fontFamily;
            SharpenThresholdSlider.FontFamily = fontFamily;
            UnsharpAmountSlider.FontFamily = fontFamily;
            UnsharpRadiusSlider.FontFamily = fontFamily;
            SharpenParamsResetButton.FontFamily = fontFamily;
        }

        private static void ApplyMenuFont(
            System.Collections.Generic.IEnumerable<MenuFlyoutItemBase> items,
            FontFamily fontFamily)
        {
            foreach (var item in items)
            {
                item.FontFamily = fontFamily;

                if (item is MenuFlyoutSubItem subItem)
                {
                    ApplyMenuFont(subItem.Items, fontFamily);
                }
            }
        }

        public void ShowSearchOverlay(
            SearchOverlayService searchOverlayService,
            bool isPdfMode,
            FrameworkElement? requestedAnchor,
            FrameworkElement fallback)
        {
            var anchor = SearchOverlayService.ResolveAnchor(
                isPdfMode,
                requestedAnchor,
                PdfGoToPageButton,
                ZoomFitButton,
                fallback,
                GoToPageButton);

            if (anchor != null)
            {
                searchOverlayService.Show(anchor, fallback);
            }
        }

        private static void SetFlyoutTitle(Flyout flyout, string title)
        {
            if (flyout.Content is Grid grid &&
                grid.Children.Count > 0 &&
                grid.Children[0] is TextBlock textBlock)
            {
                textBlock.Text = title;
            }
        }

        private void ScrollIntoView(ListView listView, object item)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    listView.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
                }
                catch
                {
                }
            });
        }

        private static Visibility ToVisibility(bool isVisible) =>
            isVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}

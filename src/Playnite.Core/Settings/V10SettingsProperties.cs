namespace Playnite.Settings;

// Public v10 PlayniteSettings properties, legacy/source/Playnite/Settings/PlayniteSettings.cs.
// Nested graphs remain opaque compatibility values: no defaulting or lossy typed coercion.
internal static class V10SettingsProperties
{
    internal static readonly HashSet<string> Names = new("""
        Version DetailsVisibility DefaultIconSource DefaultCoverSource DefaultBackgroundSource
        IndentGameDetails CalculatedGameDetailsIndentation GameDetailsIndentation PropertiesPanelWith
        GridViewDetailsPosition FilterPanelPosition ExplorerPanelPosition DetailsListPosition
        ExplorerPanelVisible FilterPanelWitdh ExplorerPanelWitdh GrdiDetailsWitdh DetailsListWitdh
        ShowGridItemBackground GridItemHeight GridItemWidth GridItemWidthRatio GridItemHeightRatio
        CoverArtStretch GridItemSpacing GridItemMargin FullscreenItemSpacing ItemSpacingMargin
        FullscreenItemSpacingMargin FirstTimeWizardComplete DisableHwAcceleration AsyncImageLoading
        ShowNameEmptyCover ShowNamesUnderCovers ShowBackgroundImageOnWindow HighQualityBackgroundBlur
        BlurWindowBackgroundImage BackgroundImageBlurAmount DarkenWindowBackgroundImage BackgroundImageDarkAmount
        ShowBackImageOnGridView DownloadMetadataOnImport ShowIconsOnList ShowGroupCount StartInFullscreen
        DatabasePath FilterSettings ViewSettings GridViewSideBarVisible FilterPanelVisible NotificationPanelVisible
        ShowSidebar SidebarPosition MainMenuButtonSidebarMove MinimizeToTray CloseToTray EnableTray
        Language AfterLaunch AfterGameClose Theme TrayIcon InstallInstanceId DisabledPlugins StartMinimized
        StartOnBoot StartOnBootClosedToTray EnableGameControllerSupport DisabledGameControllers
        GuideControllerButtonSwitchesFullscreen ShowPanelSeparators GameDetailsCoverHeight FontFamilyName
        MonospaceFontFamilyName FontSize FontSizeSmall FontSizeLarge FontSizeLarger FontSizeLargest
        DetailsViewListIconSize TextFormattingMode TextRenderingMode MetadataSettings PreScript PostScript
        GameStartedScript AppStartupScript AppShutdownScript DownloadBackgroundsImmediately
        ShowImagePerformanceWarning BackgroundImageAnimation ClientAutoShutdown DarkenUninstalledGamesGrid
        UsedFieldsOnlyOnFilterLists DiscordPresenceEnabled ShowHiddenInQuickLaunch QuickLaunchItems
        DirectoryOpenCommand AgeRatingOrgPriority TraceLogEnabled GridViewListScrollOptions
        GridViewDetailsScrollOptions DetailsViewListScrollOptions DetailsViewDetailsScrollOptions ListViewListScrollOptions
        ShowTopPanelGeneralViewItem ShowTopPanelGroupingItem ShowTopPanelSortingItem ShowTopPanelFilterPresetsItem
        ShowTopPanelDetailsViewSwitch ShowTopPanelGridViewSwitch ShowTopPanelListViewSwitch ShowTopPanelExplorerSwitch
        ShowTopPanelSelectRandomGameButton ShowTopPanelViewSelectRandomGameButton ShowTopPanelSearchButton
        ShowTopPanelSearchBox FuzzyMatchingInNameFilter TopPanelSectionSeparatorWidth PluginTopPanelAlignment
        SelectedFilterPreset ImageScalerMode PlaytimeImportMode UseCompositionWebViewRenderer AddonsPerfNoticeShown
        GameSortingNameAutofill GameSortingNameRemovedArticles ShowNahimicServiceWarning ShowElevatedRightsWarning
        DateTimeFormatAdded DateTimeFormatModified DateTimeFormatRecentActivity DateTimeFormatReleaseDate
        DateTimeFormatLastPlayed PlaytimeUseDaysFormat InstallSizeScanUseSizeOnDisk ScanLibInstallSizeOnLibUpdate
        CheckForProgramUpdates CheckForAddonUpdates CheckForLibraryUpdates CheckForEmulatedLibraryUpdates
        LastProgramUpdateCheck LastAddonUpdateCheck LastLibraryUpdateCheck LastEmuLibraryUpdateCheck LastAutoBackup
        PrimaryGameSearchItemAction SecondaryGameSearchItemAction GlobalSearchOpenWithLegacySearch
        SaveGlobalSearchFilterSettings CustomSearchKeywrods GameSearchFilterSettings SystemSearchHotkey
        IncludeCommandsInDefaultSearch AutoBackupEnabled AutoBackupFrequency AutoBackupDir RotatingBackups
        AutoBackupIncludeLibFiles AutoBackupIncludeExtensionsData AutoBackupIncludeExtensions AutoBackupIncludeThemes
        UpdateNotificationOnPatchesOnly WebImageSarchIconTerm WebImageSarchCoverTerm WebImageSarchBackgroundTerm
        AccessibilityInterface PartialDescriptionLoading WebImageSafeSearch DefaultWebImageSource ClearFiltersOnStartup
        LnkImportMode LastSelectedGame IsPortable WindowPositions Fullscreen DevelExtenions SearchWindowVisibility
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}

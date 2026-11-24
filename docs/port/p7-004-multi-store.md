# Multi-Store Library Ecosystem: Heroic Sync, Unified Store Filtering, Store Badges, and Safe Launch (`p7-004`)

Extends the desktop library (`src/Playnite.Desktop/p7-004/`) to discover and reconcile local Heroic Games Launcher libraries (Epic/Legendary, GOG, Amazon/Nile), filter libraries by store source using `FilterSettings.Library`, surface store badges across cards, list rows, and details, contain Heroic local artwork under verified config roots, and dispatch Heroic launch URIs safely with custom `File` action priority intact.

## v10 references

- `legacy/source/PlayniteSDK/BuiltInExtensions.cs`
- `legacy/source/Playnite/Database/GameDatabase_Filters.cs`
- `legacy/source/Playnite/Queries/FilterSettings.cs`
- `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/FilterPanelView.xaml`

## Behaviour contract

- **Canonical store catalog (`StoreCatalog.cs`):**
  - Maps built-in Playnite v10 store GUIDs (`Steam`, `Epic`, `GOG`, `Amazon`, `itch.io`, `Battle.net`, `Humble`) without introducing runtime store-plugin dependencies or modifying `Playnite.Core` or `Playnite.Steam`.
  - Validates Heroic launch identifiers (`[A-Za-z0-9._-]+`, max length 128, rejecting `.`, `..`, and path separators) before formatting the `heroic` launch URI (`legendary`, `gog`, `nile`).
- **Bounded off-UI-thread Heroic library sync (`HeroicLibrarySyncService.cs`):**
  - Discovers Heroic configuration directories strictly under `XDG_CONFIG_HOME`, user `.config/heroic`, and Flatpak `com.heroicgameslauncher.hgl` config locations with segment-by-segment symlink canonicalization.
  - Reads local Epic (`legendaryConfig/legendary/installed.json`, `store_cache/legendary_library.json`), GOG (`gog_store/installed.json`, `gog_store/library.json`), and Amazon (`nile_config/nile/installed.json`, `nile_config/nile/library.json`) metadata through `LinuxBoundedFileReader` (8 MiB limit) with `MaxDepth = 32` JSON parsing, skipping DLCs and invalid entries, making zero network calls and reading zero credential/token files.
  - Reconciles discovered entries atomically in SQLite, marking games uninstalled only for stores whose installed manifest parsed cleanly and preserving custom `GameActions`, `Playtime`, `PlayCount`, `LastActivity`, `Favorite`, `Hidden`, `UserScore`, and user-owned artwork.
- **Unified store filtering, badges, Heroic artwork, and safe launch (`LibraryViewModel.cs`, `LibraryGridView.axaml*`, `LibraryTheme.axaml*`, `LibraryArtworkLoader.cs`, `HeroicPlayRequest.cs`, `GameLaunchPlanner.cs`):**
  - `SourceCombo` filters by `All stores`, `Steam`, `Epic`, `GOG`, `Amazon`, and `Custom` (`Guid.Empty`) via `FilterSettings.Library` composed with quick filters (`Installed`, `Uninstalled`, `Hidden`, `Favorite`), while preserving exact `QueryStatus` text when `SourceFilter == LibrarySourceFilter.All` and resetting via `ClearFiltersButton`.
  - Cards and list rows display subtle store pills while `LibraryArtworkLoader` allows local cached Heroic cover files strictly inside canonical Heroic config roots (rejecting `..`, remote URLs, and symlink escapes).
  - Installed Heroic games launch via `xdg-open` argv vector with a 10-second confirmation timeout, while any user-configured `File` play action takes priority over store URI launch.

## Smoke verification

- `tests/Playnite.Smoke.Tests/p7-004/MultiStoreExperienceSmokeTests.cs`:
  - `StoreCatalog_and_HeroicPlayRequest_validate_ids_build_uris_and_handle_opener_outcomes`
  - `HeroicLibrarySyncRunner_discovers_all_stores_skips_dlc_and_symlink_escapes_and_preserves_user_data`
  - `HeroicLibrarySyncRunner_handles_missing_root_and_partial_manifest_failures_without_wiping_stores`
  - `LibraryGridView_filters_by_store_displays_store_badges_syncs_heroic_and_clears_filters`
  - `Heroic_game_launches_via_uri_and_respects_custom_file_action_override_and_artwork_containment`

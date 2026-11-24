# Library grid/list, artwork, details, grouping, and on-demand Steam sync (`p7-004`)

Extends the desktop library workspace (`src/Playnite.Desktop/p7-004/`) to deliver the visual library experience while preserving all existing `p7-003`/`p7-004`/`p7-005` control names, automation names, and `ContentHostView` contracts.

## v10 references

- `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/LibraryGridView.xaml`
- `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/LibraryListView.xaml`
- `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/LibraryDetailsView.xaml`
- `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/FilterPanelView.xaml`

## Behaviour contract

- **Grid / List switching (`LibraryTheme.axaml`, `LibraryGridView.axaml`):**
  - `GridViewButton` switches `GameList` to a wrapping `220x132` card grid (`LibraryGridPanel`, `grid-card` style) showing decoded cover artwork (or deterministic initials monogram), installed/favorite indicators, and optional group pills.
  - `ListViewButton` switches `GameList` to a stacked row list (`LibraryListPanel`, `list-row` style) with `56x32` thumbnails while preserving selection and details.
- **Rich details pane (`LibraryViewModel.cs`, `LibraryGridView.axaml`):**
  - Displays hero artwork (or initials fallback), title, game ID, launch button, install status, source (`Steam` vs `Library`), formatted playtime, last played date, release year, user score, version, install directory, and description.
- **Grouping & empty-state recovery:**
  - `GroupCombo` routes `LibraryGroupBy` (`None`, `InstallationStatus`, `ReleaseYear`, `PlayTime`, `UserScore`, `Name`) through `LibraryService` (`p6-001`) and tags cards/rows with formatted group labels.
  - `EmptyState` exposes `ClearFiltersButton` (resets search, quick filter, and grouping in one query) and `EmptySyncButton`.
- **Safe artwork resolution & bounded cache (`LibraryArtworkLoader.cs`):**
  - Resolves explicit `CoverImage`/`BackgroundImage` paths and Steam `appcache/librarycache/<appId>/` (direct and 40-char SHA-1 subdirectories) strictly inside canonicalized database or discovered Steam roots—rejecting URIs, null bytes, relative `..` escapes, rooted external paths, intermediate/leaf symlink escapes, image dimensions above `16384x16384` (`64 MP`), and files `> 8 MiB`—using handle-bound `LinuxBoundedFileReader` reads.
  - Resolves paths, reads into a bounded in-memory LRU byte cache (default 64 entries / 16 MiB), and decodes via `Bitmap.DecodeToWidth` off the UI thread.
  - Cancels and disposes stale `Bitmap` instances on page reload, selection change, re-apply, and `Unloaded`.
- **On-demand Steam sync (`LibrarySteamSyncRunner.cs`):**
  - `SyncSteamButton` / `EmptySyncButton` disable while active, invoke `SteamLibrarySyncService` off the UI thread against the app-owned `library.sqlite`, reload the current page, and report added/updated/unchanged/removed/skipped/issue/missing-Steam/failure status in `SyncStatus`.

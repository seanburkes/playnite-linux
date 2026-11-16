# Target architecture

## 1. Shape

```
┌──────────────────────────────────────────────────────────────┐
│ Playnite.Desktop (Avalonia 12)                               │
│  Views, ViewModels, dialogs, theme, tray, keyboard, gamepad  │
└───────────────┬──────────────────────────────────────────────┘
                │
┌───────────────▼──────────────────────────────────────────────┐
│ Playnite.Core (net11.0, no UI, no OS)                        │
│  Models  Library  Import  Steam  Settings  Metadata  Events  │
│  Search/filter  Session lifecycle  Add-on manifests          │
└───────┬───────────────────────────────┬──────────────────────┘
        │                               │
┌───────▼──────────────┐      ┌─────────▼──────────────────────┐
│ Playnite.SDK         │      │ Playnite.Platform              │
│  Abstractions:       │      │  IPaths, IProcessLauncher, ... │
│   models, events,    │      │  Linux implementation          │
│   non-UI contracts   │      │  (XDG, proc, tray, screens)    │
│  UI: Avalonia points │      │                                │
└──────────────────────┘      └────────────────────────────────┘
        │
┌───────▼──────────────┐      ┌────────────────────────────────┐
│ Playnite.Steam       │      │ Playnite.LegacyImport          │
│  VDF/ACF discovery   │      │  LiteDB 4 reader → neutral     │
│  Launch via Steam    │      │  archive → SQLite importer      │
└──────────────────────┘      └────────────────────────────────┘
```

`Playnite.Cli` is a headless host over Core for acceptance tests, migration, diagnostics, and
CI. `Playnite.LegacyImport` is a separate executable: Desktop/Cli invoke it and consume a
neutral schema/asset directory defined by the tiny `Playnite.LegacyImport.Contracts` assembly.
LiteDB is never linked into the app or the CLI.

## 2. Projects and dependency rules

| Project | TFM | May reference | Must not reference |
| --- | --- | --- | --- |
| `Playnite.SDK.Abstractions` | `net11.0` | Microsoft.CodeAnalysis.PublicApiAnalyzers (private build only), Microsoft.DotNet.ApiCompat.Task (private build only) | Avalonia, Platform, Win32, Core |
| `Playnite.SDK` | `net11.0` | Abstractions, Avalonia (UI contracts only) | Platform, Win32, Core |
| `Playnite.Core` | `net11.0` | Abstractions, Microsoft.Data.Sqlite, Dapper, Newtonsoft.Json, YamlDotNet, Tomlyn, SharpCompress, NLog | Avalonia, `System.Windows`, `System.Drawing`, `Microsoft.Win32`, `DllImport` |
| `Playnite.Platform` | `net11.0` | Core, Abstractions | Desktop |
| `Playnite.Platform.Linux` | `net11.0` | Platform, Core, Avalonia (platform UI services only) | Desktop |
| `Playnite.Steam` | `net11.0` | Core, Platform | Desktop |
| `Playnite.LegacyImport.Contracts` | `net11.0` | nothing but BCL | LiteDB, Core, Desktop, SDK |
| `Playnite.LegacyImport` | `net11.0` exec | Contracts, LiteDB 4.1.4 | Core, Desktop, SDK |
| `Playnite.Desktop` | `net11.0` | Core, SDK, Platform, Steam, LegacyImport.Contracts, Avalonia | Microsoft.Win32, `DllImport` |
| `Playnite.Cli` | `net11.0` | Core, Platform, Steam, LegacyImport.Contracts | Desktop, LiteDB |
| Tests | `net11.0` | anything | — |

Enforced by `BannedApiAnalyzers` + architecture tests. A project reference that violates the
graph fails CI before a human sees it.

**Avalonia rule, precisely:** `Playnite.SDK.Abstractions` and `Playnite.Core` may not reference
Avalonia at all. `Playnite.Platform`/`Playnite.Platform.Linux` may reference Avalonia base and
control types where a platform service is inherently UI-shaped (tray, screens, web view);
`Playnite.Desktop` owns the views. `System.Windows.Input.ICommand` is the sole allowed
`System.Windows` type because it is cross-platform; every other `System.Windows` type is
banned.

## 3. Core modules

### 3.1 Models

Reuse the v10 model shape: `Game`, `Platform`, `Genre`, `Company`, `Category`, `Tag`,
`Feature`, `Series`, `AgeRating`, `Region`, `Source`, `CompletionStatus`, `GameAction`,
`GameRom`, `Emulator`, `FilterPreset`, `AppSoftware`, `Link`, `ReleaseDate`.

Rules:
- `DatabaseObject` identity stays `Guid`.
- JSON property names stay v10-compatible where the model crosses a data boundary.
- No UI types. `Game.CoverImage` stays a string/path/id; UI resolves it through an image service.
- Collections keep stable ordering semantics. Where v10 relies on insertion order, use an
  ordered structure, not `Dictionary`/`HashSet`.

### 3.2 Library

`IGameLibrary` owns the in-memory collection and persistence. The v10 `ItemCollection<T>`
contract is preserved (add/update/remove, buffered events, `ItemUpdated`, `ItemCollectionChanged`)
but backed by SQLite repositories.

Key invariants:
- Every persisted item has a stable `Guid` and a `Modified` timestamp.
- A write is atomic; a failed write leaves the previous state.
- Events fire after the durable write, never before.
- Batch imports buffer events and flush once, preserving v10's "one import, one refresh" feel.

### 3.3 Import and migration

Two distinct operations:

1. **v10 library migration** (one-way): read LiteDB, validate, write SQLite, verify, report.
2. **Steam sync** (repeatable): discover installed apps, upsert games, never delete user data.

Migration states (formal model: `docs/formal/LibraryImport.tla`):

```
Idle → Copying → Reading → Transforming → Writing → Verifying → Committed
                        ↘ Failed → RolledBack
```

Rules:
- The source directory is opened read-only and copied before any parse.
- A backup manifest (path, size, SHA-256) is written before the first SQLite write.
- The new database is written to a temp path, verified, then atomically renamed.
- Verification compares counts, ids, names, and asset hashes against the source manifest.
- A crash at any point leaves either the old database or a resumable temp database, never a
  half-written live database.
- Non-Steam games are reported as `skipped` with a reason; they are not silently dropped.

### 3.4 Steam (greenfield subsystem)

Steam is **not a v10 port**: the v10 Steam implementation lives in a separate built-in plugin
that is not in `legacy/`. This subsystem has its own spec and oracle (recorded real artifacts
plus a real-install acceptance test), not a v10 behavioural reference.

Discovery is pure and testable:
- Locate roots: `STEAM_ROOT`, `~/.steam/steam`, `~/.local/share/Steam`,
  `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`, plus `libraryfolders.vdf` entries.
- Parse `libraryfolders.vdf` (Valve KeyValues) and `appmanifest_<appid>.acf`.
- Enumerate `steamapps/common/<installdir>`; map `appid`, `name`, `installdir`,
  `StateFlags`, `LastUpdated`, `SizeOnDisk`, `buildid`.
- Detect Proton prefixes under `steamapps/compatdata/<appid>/pfx`.
- Read `librarycache` artwork and the public CDN for covers.
- Detect non-Steam shortcuts from `userdata/<id>/config/shortcuts.vdf` as a later increment,
  not v1.

Launch:
- `steam -applaunch <appid>` (preferred) or `xdg-open steam://rungameid/<appid>`.
- Session tracking: a `IGameSessionTracker` watches `/proc` for the game's process tree,
  Steam's child processes, and Proton/compatdata markers. It has a manual "stop tracking"
  escape hatch. The lifecycle is modeled in `docs/formal/GameSession.tla`.
- Playtime is measured by the tracker, not scraped from Steam, in v1.

Metadata:
- v1 uses Steam's public app details endpoint with a local cache and a rate limiter.
- Failures degrade to name/artwork from the ACF/library cache.
- Provider abstraction is in place for IGDB/others later.
- The v1 description view uses `Avalonia.HtmlRenderer`; JavaScript-rendered pages and the
  `IWebView` service are v1.1 (ADR-019).

### 3.5 Settings

Reuse the v10 settings object graph where possible. Keep Newtonsoft for v10-compatible
settings. Migration:
- Read `config.json` with v10 semantics.
- Add a schema version.
- Write a new settings file atomically, keeping the v10 file untouched.
- Unknown properties are preserved in a `legacy` bag, not discarded.
- Secrets (API keys, cookies) go to the platform secret store, not plain JSON, if present.
- Windows path settings (install dirs, backups, extension paths, database path) are rewritten
  to Linux equivalents where a mapping is obvious; otherwise they are preserved in the
  `legacy` bag and reported as unmapped. Silent path rewriting is forbidden.

### 3.6 Search, filter, sort

Reuse v10 semantics from `FilterSettings`, `GameFieldComparer`, `GamesCollectionViewEntry`,
and the search ViewModels. These are pure enough to port early and are covered by property
tests:
- Filtering a set and then sorting is stable.
- Text search is case-insensitive and culture-aware as v10.
- Empty/whitespace filters mean "no filter", not "match nothing".
- Grouping preserves the defined group order.

## 4. Platform abstraction

`Playnite.Platform` defines the UI/platform service boundary. The path leaf's `IPaths`,
`IEnvironment` and `IFileSystem` contracts live in Core so Core never references Platform;
`Playnite.Platform.Linux` implements them. `PlaynitePaths` in Core derives v10 filenames
from the captured roots, and `LinuxPaths` supplies the XDG roots without creating them.

```csharp
public interface IPaths {
    string Home { get; }
    string Documents { get; }
    string UserData { get; }        // XDG_DATA_HOME/playnite
    string Config { get; }          // XDG_CONFIG_HOME/playnite
    string Cache { get; }           // XDG_CACHE_HOME/playnite
    string State { get; }           // XDG_STATE_HOME/playnite
    string Logs { get; }            // State/logs
    string Program { get; }         // AppImage/Flatpak mount
    IReadOnlyList<string> SteamRootCandidates { get; } // ordered probes, not VDF libraries
}

public interface IProcessLauncher {
    Task<IGameProcess> LaunchAsync(LaunchRequest request, CancellationToken ct);
    Task OpenUrlAsync(string url, CancellationToken ct);
    Task OpenFolderAsync(string path, CancellationToken ct);
}

public interface IGameSessionTracker {
    event EventHandler<SessionStateChanged> StateChanged;
    Task TrackAsync(IGameProcess process, SessionOptions options, CancellationToken ct);
}

public interface IImageService { Task<ImageHandle> LoadAsync(string path, int decodeWidth, CancellationToken ct); }
public interface IIconService { Task<ImageHandle?> GetIconAsync(string appOrFile, CancellationToken ct); }
public interface INotificationService { Task ShowAsync(Notification notification, CancellationToken ct); }
public interface ITrayService { IDisposable Install(TrayModel model); }
public interface ISingleInstance { Task<InstanceResult> AcquireAsync(string id, CancellationToken ct); }
public interface IProtocolHandler { Task RegisterAsync(string scheme, CancellationToken ct); }
public interface IPowerService { Task InhibitSleepAsync(Reason reason, CancellationToken ct); }
public interface IScreenService { IReadOnlyList<ScreenInfo> Screens { get; } }
public interface ISecretStore { Task<string?> GetAsync(string key); Task SetAsync(string key, string value); }
public interface IClipboardService { ... }
public interface IFilePickerService { ... }
```

Linux implementations:
- XDG base dirs: nonempty absolute `XDG_DATA_HOME`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME`,
  `XDG_STATE_HOME`; otherwise HOME/.local/share, HOME/.config, HOME/.cache, HOME/.local/state.
  Each application root adds the single Core `ApplicationDirectoryName` constant. Documents
  reads a quoted absolute or `$HOME`-relative entry from `user-dirs.dirs`, otherwise HOME/Documents.
- `xdg-open` via `Process` with argument-list APIs (no shell).
- Unix domain socket + file lock for single instance.
- `.desktop` entry + `xdg-mime` for `playnite://`.
- `org.freedesktop.Notifications` over D-Bus (Tmds.DBus) or `notify-send` fallback.
- StatusNotifierItem/AppIndicator tray via Avalonia TrayIcon; verify on GNOME (extension),
  KDE, and SteamOS.
- `systemd-inhibit`/logind for sleep inhibition.
- X11/Wayland screen enumeration; no HDR in v1.
- Freedesktop icon theme lookup; Steam artwork for Steam games.
- WebKitGTK/WPE through Avalonia WebView is v1.1 (ADR-019). v1 uses `Avalonia.HtmlRenderer`
  and HTTP for descriptions/metadata.
- SDL2 for gamepad is deferred; no global hotkeys in v1 on Wayland (portal limitations
  documented).

## 5. UI architecture

- **Framework:** Avalonia 12.1.x, Fluent theme as a base, custom default theme resources.
- **MVVM:** keep the v10 `ObservableObject`/`RelayCommand` shape in the SDK for source
  familiarity. Do not introduce a second MVVM framework in v1. Prism is not used in v10 code
  and is removed.
- **Navigation:** an explicit `INavigationService` with a small route table (Library, Details,
  Settings, GameEdit, Import). No region manager.
- **Dialogs:** `IDialogService` with typed requests/results. `IDialogsFactory` from the SDK is
  adapted to Avalonia windows; the API shape stays familiar to plugin authors.
- **Threading:** UI thread is the Avalonia dispatcher. Core services are async and
  cancellation-aware. No `Dispatcher.Invoke` in Core. A `SynchronizationContext` adapter lives
  in Desktop.
- **Images:** `Avalonia.Media.Imaging.Bitmap` at the edge; Core deals in paths/streams. Decode
  off the UI thread; cache with size limits.
- **Theme:** `ThemeManager` loads `.axaml` dictionaries from the built-in theme and optional
  user theme directories. Theme API version is checked. No WPF XAML loader.
- **Localisation:** preserve the v10 key names (1,134 keys, 35 language files) and the English
  source strings. Load existing translations where keys match; report missing/changed keys.
  v1 ships English plus cleanly mapped translations; partial locales are marked as such
  (ADR-022).
- **Accessibility:** AutomationProperties, keyboard navigation, focus order, high contrast.
  Headless tests assert names/roles for key controls. v1 does not claim AT-SPI/screen-reader
  accessibility on Wayland: for Avalonia 12.1.6 it is only observable under X11/XWayland; see
  Deferred limitations D1 in [`00-DECISIONS.md`](00-DECISIONS.md).
- **Fullscreen fast-follow:** shared ViewModels; a separate shell with 10-foot layout, gamepad
  navigation, and a large-screen theme.

## 6. Plugin SDK v7

Preserve the v6 contract shape:

- `Plugin`, `GenericPlugin`, `LibraryPlugin`, `MetadataPlugin`.
- `IPlayniteAPI`, `IGameDatabaseAPI`, `IDialogsFactory`, `IMainViewAPI`,
  `INotificationsAPI`, `IPlayniteSettingsAPI`, `IWebView`, `IUriHandlerAPI`.
- Models, events, `GameMenuItem`, `MainMenuItem`, `SidebarItem`, `TopPanelItem`.
- `LibraryPlugin.GetGames`, `GetInstallActions`, `GetPlayActions`, `GetUninstallActions`.
- `MetadataPlugin.GetMetadata`, `GetMetadataFields`.

Changes:
- UI types become Avalonia (`Control`, `Window`, `IImage`, `IWebView`).
- `IWebViewFactory` is defined in `Playnite.SDK` (UI extension point, v1.1) and implemented by
  `Playnite.Platform.Linux`; it is not a `Playnite.Platform` interface.
- `PluginUserControl` hosts an Avalonia `Control`.
- Dispatcher types become Avalonia dispatcher abstractions.
- File/process APIs use the platform interfaces.
- SDK version is `7.0.0`; a plugin declares its minimum SDK version in `extension.yaml`.
- `Microsoft.DotNet.ApiCompat` produces a v6→v7 breaking-change report; the report is a
  reviewed artifact, not a surprise.

Compatibility claim, precisely:
- **Source-compatible:** model/contract code recompiles after namespace/using updates.
- **Not source-compatible:** plugin UI code that uses WPF controls, WPF bindings, or WPF
  dispatchers; it must be ported to Avalonia.
- **Not binary-compatible:** v6 assemblies will not load.

## 7. Storage schema

SQLite via `Microsoft.Data.Sqlite` + Dapper. One database file per library, WAL mode.

Core tables:
```
schema_version(version, applied_at)
game(id TEXT PK, name, sort_name, release_date, ... , metadata_json)
game_link(game_id, kind, url, name)
game_tag(game_id, tag_id)
game_genre(...), game_platform(...), game_company(...), game_feature(...), game_series(...)
platform(id, name, spec_id, icon, cover, ...)
genre/company/tag/feature/series/category/source/age_rating/region/completion_status
game_action(game_id, type, path, arguments, working_dir, ...)
game_rom(game_id, path, ...)
asset(hash TEXT PK, kind, width, height, bytes_path)
settings(key, value_json)          -- only non-secret, typed settings
migration(source_path, source_hash, started_at, completed_at, status, report_json)
```

Design rules:
- GUIDs are stored as canonical lowercase strings for portability and debuggability.
- Timestamps are UTC ISO-8601 with offset.
- JSON columns are for open-ended metadata, never for fields that need filtering/sorting.
- Foreign keys are on; migrations are versioned and forward-only.
- Asset files live under `assets/<hash>`; the database stores hashes, not absolute paths.
- No SQLite file is opened read-write across a network filesystem.

## 8. Packaging and updates

- **AppImage:** self-contained `linux-x64`/`linux-arm64`; includes .NET runtime, managed
  assemblies, native libs, default theme, localisation.
- **Flatpak:** final ID `io.github.seanburkes.PlayniteLinux` (or the captain's chosen
  namespace). v1 uses `--share=network`, `--socket=wayland`, `--socket=fallback-x11`,
  `--device=input`, `--talk-name=org.freedesktop.Notifications`, and narrow `--filesystem`
  entries for Steam library paths. **Host launch and process observation use
  `flatpak-spawn --host`; `/proc` inside the sandbox cannot see host Steam processes**
  (ADR-020). A minimal host helper is allowed only if `flatpak-spawn` cannot provide PID
  visibility. `--filesystem=home` is not a default. Gate 5 requires launch + session tracking
  inside the Flatpak build.
- **Updates:** v1 is manual download. A self-updater (AppImage zsync / Flatpak remote) is a
  later project. No silent updater in v1.
- **Crash reporting:** local logs + a diagnostics bundle; no automatic upload.

## 9. Explicit non-goals for v1

- Windows support or WPF compatibility.
- Fullscreen/Steam Deck UI.
- Emulation, non-Steam launchers, add-on store, metadata plugins.
- Binary plugin compatibility or WPF theme compatibility.
- WebView/JavaScript-rendered metadata (v1.1; ADR-019).
- HDR, global hotkeys on Wayland, streaming, cloud sync.
- Redesign of Playnite's data model or information architecture.

## 10. Import UX and library transport

v1 supports three entry paths:
1. **Existing v10 library on a mounted Windows partition / USB / SMB path**: the user picks the
   source directory; the importer copies it, migrates, verifies, and reports.
2. **Existing v10 library copied to Linux**: same flow, no Windows required at migration time.
3. **Fresh install**: no v10 library; discover Steam and start.

The wizard never writes to the source. It shows a summary of imported/skipped/unmapped rows
and keeps the source path for rollback. A user with no Windows machine is a first-class path,
not an error.

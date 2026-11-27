# Plugin Architecture — Iterations 1 & 2

This document records the OpenCode v2–inspired plugin kernel and built-in store extractions
in Playnite Linux (`Playnite.Core.Plugins` and `Playnite.Desktop`).

## Speed & Isolation Design

1. **In-process execution per surface:** Built-in plugins (`SteamPlugin`, `HeroicPlugin`,
   `AmazonLunaPlugin`, `SnesRomPlugin`) register directly into `PluginHost` with zero reflection
   or IPC overhead.
2. **Manifest-first lazy activation:** External plugins declare `plugin.json` (or legacy
   `extension.yaml`) with `surfaces`, `capabilities`, `sourceId`, and relative `entryPoints`.
   `PluginHost.DiscoverExternal` validates manifest sizes (1 MiB cap), rejects duplicate JSON keys,
   traversal, intermediate/leaf/root symlinks, and non-regular files via `IRegularFileProbe`, and
   indexes capability owners **without** loading assemblies. `LibraryViewModel` construction and
   `SourceCombo` population use non-materializing `PluginHost.GetDeclaredSources()` so external
   `librarySource` plugins stay unmaterialized until a game launch, lookup, or sync accesses them.
   Collectible `AssemblyLoadContext` loading happens only when a plugin's capability (`SourceId`,
   `LibrarySync`, `Commands`, `EventHooks`, or `Settings`) is first accessed, re-verifying
   file/dependency containment at load time.
3. **Surface splitting:** Each manifest declares which surfaces it targets (`core`, `desktop`,
   `fullscreen`, `theme`). Headless hosts skip UI-only plugins without loading Avalonia, and
   theme-only manifests are indexed without attempting `.NET` assembly loading.
4. **Indexed capability dispatch:** `Setup(PluginContext)` runs outside the host registry lock and
   registers flat lookup tables (`sourceIndex`, `launchIndex`, `syncIndex`, `commandIndex`,
   `eventIndex`, `settingsIndex`) with duplicate-owner conflict guards. Events with zero subscribers
   and no pending lazy hook plugins return `Task.CompletedTask` synchronously.
5. **DTO-only capability contracts:** `LibrarySourceInfo`, `PluginLaunchSpec`, `PluginSyncResult`,
   `PluginCommandDescriptor`, `PluginEvent`, and `PluginSettingField` contain no raw database or
   UI types, preserving compatibility with a future out-of-process JSON-RPC adapter.

## Iteration 1 & 2 Deliverables

- **`src/Playnite.Core/Plugins/`**: `PluginHost`, `PluginManifestParser`, `IPlaynitePlugin`,
  `ILibrarySourceCapability`, `ILaunchCapability`, `ILibrarySyncCapability`, `ICommandCapability`,
  `IEventHookCapability`, `IPluginSettingsCapability`, and DTO models.
- **`src/Playnite.Desktop/p7-004/Plugins/`**:
  - `SteamPlugin`: Steam source identity, `steam:` URI launch planning (`SteamPlayRequest`), and
    local VDF/ACF SQLite sync (`LibrarySteamSyncRunner`).
  - `HeroicPlugin`: multi-source built-in plugin contributing Epic, GOG, and Amazon sources,
    `heroic:` URI launch planning (`HeroicPlayRequest`), and bounded Heroic sync
    (`HeroicLibrarySyncRunner`).
  - `AmazonLunaPlugin` and `SnesRomPlugin`.
  - `DesktopPluginHostFactory`: registers built-ins and `CreateForLibrary` lazily discovers external
    plugins from `PLAYNITE_PLUGINS_DIR` or `<libraryDir>/plugins` via `LinuxRegularFileProbe`.
- **Dynamic Shell Integration**:
  - `StoreCatalog`, `GameLaunchPlanner`, `LibraryViewModel`, `LibraryGridView`, `SteamPlayRequest`,
    and `FullscreenLibraryWindow` resolve source names, launch planning, sync dispatch, and
    `GameStarting`/`GameStopped`/`LibrarySynced` events through `PluginHost`.
  - `LibraryViewModel` and `LibraryGridView` drive dynamic `SourceCombo` filter options from
    `PluginHost.GetDeclaredSources()` (preserving canonical built-in indices `0..7` and appending
    additional registered plugin sources at index `8+`); `FullscreenLibraryWindow` uses `PluginHost`
    for launch planning and session events without a desktop `SourceCombo` filter bar.
  - Note: built-in `IPluginSettingsCapability` schemas are declared for host discovery; rendering
    and persisting user-edited values from the Settings UI belongs to Iteration 3.

## Planned Next Iteration

- **Iteration 3:** Add declarative UI slots (`card.badge`, `details.badges`, `details.section`),
  host-rendered settings UI for `IPluginSettingsCapability`, declarative theme packs
  (`theme.yaml` + `.axaml`), and implement **SteamGridDB** / **ProtonDB** as the first
  community-style plugins.

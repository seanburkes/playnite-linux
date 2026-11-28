# Plugin Architecture — Iterations 1–7

This document records the OpenCode v2–inspired plugin kernel, built-in store extractions,
declarative UI extension slots, community-style compatibility plugin, host-rendered
Plugins & Add-ons manager, dynamic plugin command actions, and declarative theme packs
with live theme switching in Playnite Linux (`Playnite.Core.Plugins`, `Playnite.Core.Themes`,
and `Playnite.Desktop`).

## Speed & Isolation Design

1. **In-process execution per surface:** Built-in plugins (`SteamPlugin`, `HeroicPlugin`,
   `AmazonLunaPlugin`, `SnesRomPlugin`, `ProtonDbPlugin`) register directly into `PluginHost`
   with zero reflection or IPC overhead.
2. **Manifest-first lazy activation:** External plugins declare `plugin.json` (or legacy
   `extension.yaml`) with `surfaces`, `capabilities`, `sourceId`, and relative `entryPoints`.
   `PluginHost.DiscoverExternal` validates manifest sizes (1 MiB cap), rejects duplicate JSON keys,
   reserved host IDs, traversal, intermediate/leaf/root symlinks, and non-regular files via
   `IRegularFileProbe`, and indexes capability owners **without** loading assemblies.
   `LibraryViewModel` construction, `SourceCombo` population, `PluginManagerWindow` browsing,
   and `PluginActionsButton` / `GamePluginActionsButton` visibility checks use non-materializing
   host queries (`GetDeclaredSources()`, `HasUiSlotProviders()`, `HasCommandProviders()`,
   `GetCommands(..., activateLazy: false)`, `GetSettingsSchema(..., activateIfLazy: false)`)
   so lazy or disabled plugins stay unmaterialized until explicitly invoked or opened. Collectible
   `AssemblyLoadContext` loading happens only when a plugin's capability (`SourceId`,
   `LibrarySync`, `Commands`, `EventHooks`, `UiSlots`, `Metadata`, or Settings activation)
   is first accessed, re-verifying file/dependency containment at load time.
3. **Surface splitting:** Each manifest declares which surfaces it targets (`core`, `desktop`,
   `fullscreen`, `theme`). Headless hosts skip UI-only plugins without loading Avalonia, and
   theme-only manifests are indexed without attempting `.NET` assembly loading.
4. **Indexed capability dispatch:** `Setup(PluginContext)` runs outside the host registry lock and
   registers flat lookup tables (`sourceIndex`, `launchIndex`, `syncIndex`, `commandIndex`,
   `eventIndex`, `uiSlotIndex`, `metadataIndex`, `settingsIndex`) with duplicate-owner conflict
   guards and link/attribute/message sanitization. Events with zero subscribers and no pending lazy
   hook plugins return `Task.CompletedTask` synchronously, while `HasUiSlotProviders` and
   `HasCommandProviders` provide O(1)/non-materializing checks before slot/menu invocation.
5. **Persisted enablement & validated settings:** `PluginHost` persists per-plugin enable/disable
   overrides under reserved `HostStatePluginId` (`playnite.host.state`) in `IPluginSettingsStore`,
   sanitizes untrusted `IPluginSettingsCapability.Fields` schemas, and validates all entries
   atomically against `PluginSettingType` in `SaveSettings` before atomic `SetMany` persistence.
6. **DTO-only capability contracts:** `LibrarySourceInfo`, `PluginLaunchSpec`, `PluginSyncResult`,
   `PluginCommandDescriptor`, `PluginCommandContext`, `PluginCommandResult`, `PluginEvent`,
   `PluginSettingField`, `PluginSettingsSaveResult`, `PluginGameSnapshot`,
   `PluginUiSlotContribution`, and `PluginGameMetadata` contain no raw database or UI types,
   preserving compatibility with a future out-of-process JSON-RPC adapter.

## Iteration 1–5 Deliverables

- **`src/Playnite.Core/Plugins/`**: `PluginHost` (persisted enablement state, schema sanitization,
  validated `SaveSettings`, `HasCommandProviders` / `GetCommands(placement, activateLazy)`,
  command sanitization & fault isolation, `GetDiagnostics`), `FilePluginSettingsStore` (atomic
  single-key `Set` and multi-key `SetMany` JSON persistence with symlink/regular-file/size/key
  guards), `PluginManifestParser`, `IPlaynitePlugin`, `ILibrarySourceCapability`,
  `ILaunchCapability`, `ILibrarySyncCapability`, `ICommandCapability`, `IEventHookCapability`,
  `IUiSlotCapability`, `IMetadataCapability`, `IPluginSettingsCapability`, and DTO models.
- **`src/Playnite.Desktop/p7-004/Plugins/`**:
  - `SteamPlugin`: Steam source identity, `steam:` URI launch planning (`SteamPlayRequest`),
    local VDF/ACF SQLite sync (`LibrarySteamSyncRunner`), and `steam.sync` toolbar command.
  - `HeroicPlugin`: multi-source built-in plugin contributing Epic, GOG, and Amazon sources,
    `heroic:` URI launch planning (`HeroicPlayRequest`), bounded Heroic sync
    (`HeroicLibrarySyncRunner`), and `heroic.sync` toolbar command.
  - `AmazonLunaPlugin` and `SnesRomPlugin`.
  - `ProtonDbPlugin`: contributes ProtonDB tier `card.badge` (cache-only for grid smoothness),
    `details.badges`, `details.section`, `IMetadataCapability` metadata, and interactive commands
    (`protondb.refresh-rating` on Steam game context & `protondb.clear-cache` on library toolbar).
  - `DesktopPluginHostFactory`: registers built-ins, wires `FilePluginSettingsStore` under
    `plugin-settings`, and lazily discovers external plugins from `PLAYNITE_PLUGINS_DIR` or
    `<libraryDir>/plugins` via `LinuxRegularFileProbe`.
- **Dynamic Shell, Plugins Manager & Command Flyouts**:
  - `StoreCatalog`, `GameLaunchPlanner`, `LibraryViewModel`, `LibraryGridView`, `SteamPlayRequest`,
    `LibraryTheme`, and `FullscreenLibraryWindow` resolve source names, launch planning, sync
    dispatch, `GameStarting`/`GameStopped`/`LibrarySynced` events, and dynamic `SourceCombo` filters
    through `PluginHost`.
  - `LibraryGridView` and `LibraryTheme` render fault-isolated `card.badge`, `details.badges`, and
    `details.section` contributions through `PluginHost`; `PluginsButton` opens
    `PluginManagerWindow` for enable/disable toggles, lazy settings activation, host-rendered typed
    settings editing, reset-to-defaults, and diagnostics; `PluginActionsButton` and
    `GamePluginActionsButton` dynamically populate `LibraryToolbar` and `GameContext` flyouts on
    open (refreshing button visibility on flyout close) and trigger library/slot refreshes on
    completion.

## Iteration 6 Deliverables — Declarative Theme Packs & Live Switching

- **`src/Playnite.Core/Themes/`**:
  - `ThemePackModels`: immutable `ThemePackDescriptor`, `ThemePackDocument`, `ThemeRgbaColor`
    (strict `#RGB`/`#ARGB`/`#RRGGBB`/`#AARRGGBB` hex parser), `ThemeLinearGradient`,
    `ThemePackDefaults` (canonical Charcoal 25-color baseline matching `PlayniteDarkTheme.axaml`,
    brush mappings, semantic color aliases, and bounded metric rules).
  - `ThemePackParser`: pure, bounded validator for `theme.yaml` (strict `ThemeApiVersion` 2.x
    compatibility up to `2.10.0`, reserved/built-in ID rejection, unknown-property rejection,
    iterative YAML anchor/alias/tag/depth/duplicate-key rejection), `tokens.yaml`, and
    allow-listed declarative `.axaml` (`<ResourceDictionary>`/`<Styles>`, `<Color>`,
    `<SolidColorBrush>`, `<LinearGradientBrush>`, `<x:Double>`, `<FontFamily>`, and
    `<Style Selector="Window|:is(Window)">` `FontSize`/`FontFamily` setters via `XmlReader` with
    DTD/entities prohibited and zero runtime XAML instantiation), plus symlink/FIFO/size guards.
  - `ThemePackCatalog`: multi-root discovery (`PLAYNITE_THEMES_DIR`, `<libraryDir>/themes`,
    and `PluginSurface.Theme` directories) with built-in Default Charcoal first, duplicate ID
    diagnostics, and isolated invalid-pack reporting.
- **`PluginHost` theme state**: `GetActiveThemeId()`, `SetActiveThemeId(string?)` persisted under
  `HostStatePluginId`, and non-materializing `GetThemeDescriptors()`.
- **`src/Playnite.Desktop/p7-004/Themes/` & Desktop Shell**:
  - `LiveThemeManager` and `DesktopThemeCatalogFactory`: off-tree resource/style staging, atomic
    application swap, fresh on-disk re-validation before live switch, `LibraryCardWidth` /
    `LibraryCardHeight` / `LibraryDetailsWidth` dynamic resource synchronization, and guaranteed
    safe Charcoal fallback on any error without persisting broken theme IDs.
  - `LibraryGridView` & `PluginManagerWindow` (`Themes` tab): toolbar `Themes` action, palette
    swatch preview, live Apply / Reset to Charcoal / Reload Themes actions, and theme diagnostics.

## Iteration 7 — Local Add-on Packages and Game Metadata

- **Plugins & Add-ons → Packages** installs an unpacked folder or ZIP-based `.zip`/`.pext`
  package with a root `plugin.json` or `extension.yaml`. It checks the declared entry points
  and host surfaces without loading assemblies. Linux declarative theme plugins use
  `plugin.json` with the `theme` surface. As with loose plugins, a legacy manifest alone
  does not make a Windows-only plugin binary compatible with the Linux host.
- Select a managed package to update it from a newer package with the same ID, restore
  its previous version, or schedule removal. A new Desktop session picks up package changes
  and removes retired files, as with the v10 `ExtensionInstaller.InstallExtensionQueue`.
  The Plugins tab still applies and
  persists enablement immediately. Loose plugins discovered before this workflow are not
  treated as managed packages.
- **Edit → Search metadata** queries enabled `IMetadataCapability` providers for the current
  game. Select a result to preview its summary, community score and provider attributes.
  The summary and community score can be imported independently; attributes are preview-only.
  Import changes the edit draft and the existing Save action commits it.

# Plugin Architecture — Iterations 1–4

This document records the OpenCode v2–inspired plugin kernel, built-in store extractions,
declarative UI extension slots, community-style compatibility plugin, and host-rendered
Plugins & Add-ons manager in Playnite Linux (`Playnite.Core.Plugins` and `Playnite.Desktop`).

## Speed & Isolation Design

1. **In-process execution per surface:** Built-in plugins (`SteamPlugin`, `HeroicPlugin`,
   `AmazonLunaPlugin`, `SnesRomPlugin`, `ProtonDbPlugin`) register directly into `PluginHost`
   with zero reflection or IPC overhead.
2. **Manifest-first lazy activation:** External plugins declare `plugin.json` (or legacy
   `extension.yaml`) with `surfaces`, `capabilities`, `sourceId`, and relative `entryPoints`.
   `PluginHost.DiscoverExternal` validates manifest sizes (1 MiB cap), rejects duplicate JSON keys,
   reserved host IDs, traversal, intermediate/leaf/root symlinks, and non-regular files via
   `IRegularFileProbe`, and indexes capability owners **without** loading assemblies.
   `LibraryViewModel` construction, `SourceCombo` population, and `PluginManagerWindow` browsing
   keep lazy (`Registered`) and disabled plugins unmaterialized until activated on demand (or
   explicitly via `Load Settings`, with `GetSettingsSchema(id, activateIfLazy: false)` /
   `GetSettingsValues(id, activateIfLazy: false)` also available on `PluginHost`). Collectible
   `AssemblyLoadContext` loading happens only when a plugin's capability (`SourceId`,
   `LibrarySync`, `Commands`, `EventHooks`, `UiSlots`, `Metadata`, or Settings activation)
   is first accessed, re-verifying file/dependency containment at load time.
3. **Surface splitting:** Each manifest declares which surfaces it targets (`core`, `desktop`,
   `fullscreen`, `theme`). Headless hosts skip UI-only plugins without loading Avalonia, and
   theme-only manifests are indexed without attempting `.NET` assembly loading.
4. **Indexed capability dispatch:** `Setup(PluginContext)` runs outside the host registry lock and
   registers flat lookup tables (`sourceIndex`, `launchIndex`, `syncIndex`, `commandIndex`,
   `eventIndex`, `uiSlotIndex`, `metadataIndex`, `settingsIndex`) with duplicate-owner conflict
   guards and link/attribute sanitization. Events with zero subscribers and no pending lazy hook
   plugins return `Task.CompletedTask` synchronously, while `HasUiSlotProviders` provides an O(1)
   non-materializing check before `ResolveUiSlotAsync` runs.
5. **Persisted enablement & validated settings:** `PluginHost` persists per-plugin enable/disable
   overrides under reserved `HostStatePluginId` (`playnite.host.state`) in `IPluginSettingsStore`,
   sanitizes untrusted `IPluginSettingsCapability.Fields` schemas, and validates all entries
   atomically against `PluginSettingType` in `SaveSettings` before writing.
6. **DTO-only capability contracts:** `LibrarySourceInfo`, `PluginLaunchSpec`, `PluginSyncResult`,
   `PluginCommandDescriptor`, `PluginEvent`, `PluginSettingField`, `PluginSettingsSaveResult`,
   `PluginGameSnapshot`, `PluginUiSlotContribution`, and `PluginGameMetadata` contain no raw
   database or UI types, preserving compatibility with a future out-of-process JSON-RPC adapter.

## Iteration 1–4 Deliverables

- **`src/Playnite.Core/Plugins/`**: `PluginHost` (persisted enablement state, schema sanitization,
  validated `SaveSettings`, `GetDiagnostics`), `FilePluginSettingsStore` (per-plugin atomic JSON
  persistence with symlink/regular-file/size/key guards), `PluginManifestParser`, `IPlaynitePlugin`,
  `ILibrarySourceCapability`, `ILaunchCapability`, `ILibrarySyncCapability`, `ICommandCapability`,
  `IEventHookCapability`, `IUiSlotCapability`, `IMetadataCapability`,
  `IPluginSettingsCapability`, and DTO models.
- **`src/Playnite.Desktop/p7-004/Plugins/`**:
  - `SteamPlugin`: Steam source identity, `steam:` URI launch planning (`SteamPlayRequest`), and
    local VDF/ACF SQLite sync (`LibrarySteamSyncRunner`).
  - `HeroicPlugin`: multi-source built-in plugin contributing Epic, GOG, and Amazon sources,
    `heroic:` URI launch planning (`HeroicPlayRequest`), and bounded Heroic sync
    (`HeroicLibrarySyncRunner`).
  - `AmazonLunaPlugin` and `SnesRomPlugin`.
  - `ProtonDbPlugin`: contributes ProtonDB tier `card.badge` (cache-only for grid smoothness),
    `details.badges`, `details.section`, and `IMetadataCapability` metadata for Steam games via
    opt-in bounded HTTPS summary lookups stored in a bounded 512-entry LRU `protondb-cache.json`.
  - `DesktopPluginHostFactory`: registers built-ins, wires `FilePluginSettingsStore` under
    `plugin-settings`, and lazily discovers external plugins from `PLAYNITE_PLUGINS_DIR` or
    `<libraryDir>/plugins` via `LinuxRegularFileProbe`.
- **Dynamic Shell & Plugins Manager Integration**:
  - `StoreCatalog`, `GameLaunchPlanner`, `LibraryViewModel`, `LibraryGridView`, `SteamPlayRequest`,
    `LibraryTheme`, and `FullscreenLibraryWindow` resolve source names, launch planning, sync
    dispatch, `GameStarting`/`GameStopped`/`LibrarySynced` events, and dynamic `SourceCombo` filters
    through `PluginHost`.
  - `LibraryGridView` and `LibraryTheme` render fault-isolated `card.badge`, `details.badges`, and
    `details.section` contributions through `PluginHost`, and `PluginsButton` opens
    `PluginManagerWindow` for enable/disable toggles, lazy settings activation, host-rendered typed
    settings editing, reset-to-defaults, and discovery/plugin diagnostics with live library refresh.

## Planned Next Iteration

- **Iteration 5:** Declarative theme packs (`theme.yaml` + `.axaml`) and live theme switching,
  plus dynamic plugin-contributed commands (`ICommandCapability`) and `IMetadataCapability`
  integration in the game metadata editor (`p7-005`).

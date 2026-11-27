# Iteration 1 — In-Process Plugin Kernel & First Built-In Extractions

This document records Iteration 1 of the OpenCode v2–inspired plugin architecture in
Playnite Linux.

## Speed & Isolation Design

1. **In-process execution per surface:** Built-in plugins (`AmazonLunaPlugin`, `SnesRomPlugin`)
   register directly into `PluginHost` with zero reflection or IPC overhead.
2. **Manifest-first lazy activation:** External plugins declare `plugin.json` (or legacy
   `extension.yaml`) with `surfaces`, `capabilities`, `sourceId`, and relative `entryPoints`.
   `PluginHost.DiscoverExternal` validates manifest sizes (1 MiB cap), rejects duplicate JSON keys,
   traversal, intermediate/leaf symlinks, and non-regular files via `IRegularFileProbe`, and indexes
   capability owners **without** loading assemblies. Collectible `AssemblyLoadContext` loading
   happens only when a plugin's capability (`SourceId`, `Commands`, `EventHooks`, or `Settings`) is
   first accessed, re-verifying file/dependency containment at load time.
3. **Surface splitting:** Each manifest declares which surfaces it targets (`core`, `desktop`,
   `fullscreen`, `theme`). Headless hosts skip UI-only plugins without loading Avalonia, and
   theme-only manifests are indexed without attempting `.NET` assembly loading.
4. **Indexed capability dispatch:** `Setup(PluginContext)` runs outside the host registry lock and
   registers flat lookup tables (`sourceIndex`, `launchIndex`, `commandIndex`, `eventIndex`,
   `settingsIndex`) with duplicate-owner conflict guards. Events with zero subscribers and no
   pending lazy hook plugins return `Task.CompletedTask` synchronously.
5. **DTO-only capability contracts:** `LibrarySourceInfo`, `PluginLaunchSpec`,
   `PluginCommandDescriptor`, `PluginEvent`, and `PluginSettingField` contain no raw database or
   UI types, preserving compatibility with a future out-of-process JSON-RPC adapter.

## Extracted Features (Iteration 1)

- `src/Playnite.Core/Plugins/`: `PluginHost`, `PluginManifestParser`, `IPlaynitePlugin`,
  capability interfaces, and DTO models.
- `src/Playnite.Desktop/p7-004/Plugins/`: `AmazonLunaPlugin`, `SnesRomPlugin`, and
  `DesktopPluginHostFactory`.
- `StoreCatalog`, `GameLaunchPlanner` (with full shell/symlink safety validation for plugin
  `TrackedProcess` launches), `LibraryViewModel`, `LibraryGridView`, `SteamPlayRequest`, and
  `FullscreenLibraryWindow` resolve source metadata, tracked/URI launch planning, and
  `GameStarting` / `GameStopped` lifecycle events through `PluginHost`.
- Note: `AmazonLunaPlugin` and `SnesRomPlugin` declare their `IPluginSettingsCapability` schemas
  for host settings discovery; rendering and persisting user-edited values from the Settings UI
  belongs to Iteration 3.

## Planned Next Iterations

- **Iteration 2:** Extract Steam and Heroic (Epic/GOG/Amazon) discovery, sync, and launch behind
  the same capability contracts; replace the closed `LibrarySourceFilter` enum with dynamic source
  filters.
- **Iteration 3:** Add declarative UI slots (`details.badges`, `details.section`, `card.badge`),
  host-rendered settings UI for `IPluginSettingsCapability`, declarative theme packs
  (`theme.yaml` + `.axaml`), and implement **SteamGridDB** / **ProtonDB** as the first
  community-style plugins.

# Inventory

Reproduce with `python3 scripts/port/scan_legacy.py --format md`. Numbers below are from the
frozen v10.64 snapshot in `legacy/`, excluding `bin/` and `obj/`. The full generated detail is
in [`01-INVENTORY.generated.md`](01-INVENTORY.generated.md).

## 1. Size

| Project | C# files | C# LOC | XAML files | XAML LOC | Notes |
| --- | ---: | ---: | ---: | ---: | --- |
| `Playnite` (core) | 300 | 77,841 | 40 | 48,061 | Includes 37 localisation files, 1,134 keys |
| `PlayniteSDK` | 83 | 14,967 | 0 | 0 | 225 public types, 26 interfaces |
| `Playnite.DesktopApp` | 150 | 28,104 | 154 | 14,482 | 33 windows, 33 control views |
| `Playnite.FullscreenApp` | 71 | 10,287 | 66 | 4,892 | 21 windows, 6 control views |
| `Tools` | 25 | 3,043 | 9 | 518 | Toolbox + installer |
| `Tests` | 96 | 8,901 | 6 | 273 | 4 test projects |
| **Total** | **726** | **143,152** | **275** | **68,226** | |

UI is the majority of the *surface*, but the core is the majority of the *logic*.

## 2. Project graph

```
Playnite.SDK (net462, UseWPF)
    ↑
Playnite (net462, UseWPF + WinForms, x86, CefSharp, LiteDB 4, native)
    ↑                         ↑
Playnite.DesktopApp      Playnite.FullscreenApp
(net462, WinExe)         (net462, WinExe)
    ↑
Tests/* (net462, NUnit 3)

Playnite.BrowserProcess (net462, CefSharp renderer subprocess)
Playnite.Deps (binary-only NuGet package: DiscordRPC, HtmlRenderer, SQLNado,
               SharpCompress, Windows.winmd)
Tools/Playnite.Toolbox, Tools/PlayniteInstaller (net462)
```

Only one direction: SDK ← core ← shells. The tests reference everything. There is no
platform-abstraction layer; Win32 calls sit directly in core and UI files.

## 3. UI surface

| Kind | Desktop | Fullscreen |
| --- | ---: | ---: |
| Window XAML | 33 | 21 |
| Control XAML | 33 | 6 |
| Settings sections | 24 | 6 |
| Theme resource dictionaries | 85 | 36 |
| ViewModels | 32 (15,092 LOC) | 14 (3,280 LOC) |
| Behaviours | 8 | shared |
| Converters | 39 (shared core) | shared |

The views are not thin: `MainWindow`, library grid/list/details, game edit, settings, add-ons,
metadata comparison, emulator import, and crash/update flows all carry logic in code-behind.

**v1 reduces this to** the desktop shell's main window, library grid/list/details, search and
filters, settings, game edit, first-run/import, progress, and message dialogs. Fullscreen is a
fast-follow.

## 4. Windows coupling

| Signal | Count | Where |
| --- | ---: | --- |
| Core files referencing `System.Windows` / `System.Drawing` | 134 of 300 | converters, behaviours, controls, ViewModels, imaging |
| Files with P/Invoke or Windows-only APIs (excluding WPF-only files) | 72 | core, desktop, fullscreen, tests |
| Native interop files | 17 | `Playnite/Native/*` |
| Registry use | 4 files | settings, system integration, PowerShell detection |
| COM interop | 20 files | shell, WMI, Windows API Code Pack |
| `System.Management` (WMI) / `ServiceController` | 8 files | installed programs, process info |
| `ServiceController` | 1 file | service integration |
| `System.Drawing` | 46 files | icons, screenshots, image conversion |
| Windows Forms | core + shells | tray icon, dialogs, screen APIs |
| SDL2 P/Invoke | 6 files | controller input |
| PowerShell 5.1 / Automation | 21 files | script extensions, module loading |

The Win32 surface clusters into a small number of capabilities: **files/paths, processes,
windows/screens, icons/images, registry/WMI, services, power, shell, clipboard, single
instance, protocol handler, tray/notifications, input.** That is the platform interface list.

## 5. Data and formats

| Data | v10 format | Port treatment |
| --- | --- | --- |
| Game library | LiteDB 4, one `.db` per collection (`games.db`, `platforms.db`, …) | Copy, one-way migrate to SQLite |
| Settings | JSON via Newtonsoft, `config.json` | Deserialize with v10-compatible settings; migrate |
| Add-on manifests | YAML (`extension.yaml`, `theme.yaml`) | Reuse parser with YamlDotNet 18 |
| Localisation | 37 language files + `LocSource.xaml` (1,134 keys) | Port keys to Avalonia dictionaries/resources |
| Game controller DB | `gamecontrollerdb.txt` | Reuse; SDL2 mapping format |
| Emulator definitions | YAML (`Platforms.yaml`, `Regions.yaml`, `Emulation/*.yaml`) | Out of v1 scope; keep files for later |
| Themes | WPF `ResourceDictionary` XAML | New Avalonia theme format |
| Media | images on disk, CEF cache | Content-hash copy; new cache layout |
| Logs | NLog text files | Keep NLog, keep line format where consumed |

## 6. Tests and oracle

| Project | `[Test]` methods | `[TestFixture]` | Notes |
| --- | ---: | ---: | --- |
| `Playnite.Tests` | 192 | 70 | Core models, DB, emulation, extensions, utilities |
| `Playnite.DesktopApp.Tests` | 8 | 5 | Games editor, main view API, skins, two ViewModels |
| `Playnite.FullscreenApp.Tests` | 1 | 1 | One control layout test |
| `Playnite.Toolbox.Tests` | 3 | 2 | Paths, themes, extensions |
| **Total** | **204** | **78** | 8,895 LOC |

There are no `[TestCase]`/`[TestCaseSource]` attributes in the snapshot, so the executed case
count is close to 204. There is **no** end-to-end suite, no UI automation, no golden corpus,
no property-based testing, no fuzzing, and no coverage gate.

At ~204 test methods (82 parameterised cases) for 143k LOC, the existing suite is an **inner-loop** oracle, not a port oracle.
It cannot prove omission or cross-process behaviour. The parity ledger and the frozen app are
therefore load-bearing (see [`04-VERIFICATION.md`](04-VERIFICATION.md)).

## 7. Build and CI

- `build/build.ps1` requires PowerShell 7, Windows, .NET Framework MSBuild, VS 2026, the
  `powershell-yaml` module, and optionally a licensed-dependencies URL.
- `appveyor.yml` builds only tags on `master`, uses Visual Studio 2026, and does not run tests
  (`test: off`).
- There are no GitHub Actions workflows in the snapshot.
- `Playnite/Playnite.csproj` has a `COMReference` (`IWshRuntimeLibrary`) and a GAC
  `System.Management.Automation` reference, both of which fail on Linux MSBuild.
- `source/Playnite.slnx` is the modern solution format; `build.xml` drives MSBuild.
- `source/Directory.Build.props` treats warnings as errors only in Release.
- `source/nuget.config` adds `nuget.playnite.link` before nuget.org.
- `Playnite.Deps` is a binary NuGet package containing DiscordRPC, HtmlRenderer,
  SQLNado, SharpCompress, and `Windows.winmd`. It must be replaced by source packages in the
  new app.

**Baseline observation:** `dotnet build source/PlayniteSDK/Playnite.SDK.csproj` succeeds on
Linux with SDK 11.0.100. `dotnet build source/Playnite/Playnite.csproj` fails at
`ResolveComReference` (MSB4803) and cannot resolve the GAC PowerShell assembly. The full app
is a Windows-only build.

## 8. Reuse assessment

| Area | v10 location | Disposition | Effort/risk |
| --- | --- | --- | --- |
| Domain models | `PlayniteSDK/Models`, `Playnite/Database` | **Reuse**, retarget, remove WPF leaks | Low |
| Pure utilities | `Playnite/Common`, `Extensions/StringExtensions` | **Reuse**, replace Windows-only helpers | Low |
| Serialization | `Playnite/Common/Serialization.cs` | **Reuse** with Newtonsoft 13/YamlDotNet 18 | Low/medium |
| Collections/events | `Playnite/Database/Collections`, SDK collections | **Reuse**, replace LiteDB with SQLite repositories | Medium |
| Settings | `Playnite/Settings` | **Reuse**, migrate schema, remove WPF types | Medium |
| Metadata/add-on models | `Playnite/Metadata`, `Manifests`, `Addons` | **Reuse** models, rewrite installer/store | Medium |
| ViewModels | core + desktop VMs | **Reuse logic**, replace dispatcher/commands, split UI | Medium |
| Views/XAML | desktop/fullscreen | **Rewrite** in Avalonia | High |
| Themes | 121 resource dictionaries | **Rewrite** one functional default | High |
| Native interop | `Playnite/Native` | **Reimplement** behind interfaces | High |
| WebView | `Playnite/WebView` | **Replace** with Avalonia WebView | Medium |
| Imaging/icons | `Common/Images`, `Media/Icons` | **Replace** System.Drawing with Skia/Magick | Medium |
| Process/game launch | `Controllers`, `ProcessStarter`, `GamesEditor` | **Rewrite** Linux launch + session tracking | High |
| PowerShell | `Scripting/PowerShell` | **Replace** with PowerShell 7 SDK | Medium |
| Input | `Input`, `SDL2*` | **Reuse** SDL2 via cross-platform binding | Low/medium |
| Updater/installer | `App/Updater`, `Tools/PlayniteInstaller` | **Replace** with AppImage/Flatpak | Medium |
| Tests | `Tests` | **Reuse** portable unit tests; rewrite WPF-bound tests | Medium |

## 9. Risk hotspots found in inventory

1. **UI is half the code and has almost no tests.** The Avalonia shell is the long pole and
   the weakest oracle.
2. **Core is not cleanly separated from WPF.** 130 of 300 core files touch WPF/System.Drawing;
   extraction is a real refactor, not a copy.
3. **Win32 capability cluster is broad.** Process trees, icon extraction, screen info, power,
   tray, protocol registration, and shell integration all need Linux equivalents.
4. **Plugin binary ecosystem cannot follow.** Source recompilation is a migration project for
   third parties; v1 must stand alone.
5. **LiteDB 4 import is an unvalidated dependency.** Needs a spike before Phase 3.
6. **Steam launch/exit detection on Linux is heuristic.** Steam returns immediately; Proton
   layers processes. Needs a session-tracking design and a formal lifecycle model.
7. **WebKitGTK/WPE variance across distros.** Ubuntu lacks WPE; WebKitGTK needs XWayland for
   embedded rendering. Flatpak changes library availability.
8. **ARM64 native dependency availability** is unproven for Magick.NET, SDL2, WebKitGTK/WPE,
   and CEF (not used).
9. **The oracle is thin and the Windows build is fragile.** Golden capture must be built
   before porting, not after.
10. **v10 is x86 while the new app is x64/ARM64.** Any imported binary asset or native plugin
    dependency changes architecture; pure data is fine.

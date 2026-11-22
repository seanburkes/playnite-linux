# p7-007 — Tray, protocol activation, and in-app keyboard shortcuts

## Oracle and scope

- **Tray (`p5-007a`, `legacy/source/Playnite.DesktopApp/DesktopApplication.cs`, `ViewModels/DesktopAppViewModel.cs`, `Controls/Menus/TrayContextMenu.cs`):**
  - `AvaloniaTrayIconPresenter` implements `ITrayIconPresenter` over `Avalonia.Controls.TrayIcon` and `NativeMenu` (`Open Playnite`, `Exit`).
  - `TrayController` reads `EnableTray`, `MinimizeToTray`, `CloseToTray`, and `StartMinimized` from the native settings store (`PlayniteSettingsService`), delegates host probing and presentation to `ITrayService` (`LinuxTrayService`), and wires `MainWindow` minimize/close/start-minimized/restore/exit lifecycle.
  - When `TrayInstallation.IsHosted == false` (no StatusNotifierWatcher or headless), `MinimizeToTray` and `CloseToTray` do not hide the window so the application never becomes unreachable.
- **Protocol activation (`p5-005a`, `p6-007`, `legacy/source/Playnite/App/PlayniteApplication.cs`):**
  - `Program.cs` parses `--uridata <uri>` (or positional `playnite://...`), forwards activation URIs from secondary instances via `ISingleInstance.ActivateAsync`, registers `x-scheme-handler/playnite` via `PlayniteProtocolHandler.RegisterAsync`, and dispatches incoming `ISingleInstance.Activated` and initial `--uridata` URIs through `UriHandlerApi` and `DesktopUriCommandHost`.
  - `DesktopUriCommandHost` routes `ShowGame`, `Search`, `ShowView(Desktop)`, and `ApplyFilter` to the live `MainWindow` and `LibraryGridView`; unimplemented views (`Fullscreen`) and `StartGame` return `UriCommandResult.Unavailable` without stubs.
- **Keyboard shortcuts (`ui-017`, `legacy/source/Playnite.DesktopApp/Windows/MainWindow.xaml`, `ViewModels/DesktopAppViewModel_Commands.cs`):**
  - `DesktopShortcutHandler` binds in-app shortcuts on `MainWindow`: `F4` (Settings), `F1` (About), `Ctrl+F` (Focus library search), and `Alt+Q` (Shutdown/Exit bypassing `CloseToTray`).
  - Global hotkeys are out of v1 scope on Wayland; gamepad navigation remains deferred.

## Unit ownership

- `Cross-Unit-Move`: `p7-001:src/Playnite.Desktop/p7-001/Program.cs` → `src/Playnite.Desktop/p7-007/Program.cs`, `p7-002:src/Playnite.Desktop/p7-002/MainWindow.axaml.cs` → `src/Playnite.Desktop/p7-007/MainWindow.axaml.cs`.
- `MainWindow.axaml`, `App.cs`, `ShellView.axaml*`, `LibraryGridView.axaml*`, `ContentHostView.axaml*`, and all `p5-*` / `p6-*` files remain untouched.

## Verification

- `TrayProtocolKeyboardTests` (`tests/Playnite.Smoke.Tests/p7-007/TrayProtocolKeyboardTests.cs`) exercises gesture mapping, `--uridata` parsing, `TrayController` + `AvaloniaTrayIconPresenter` lifecycle and unhosted degradation, headless `MainWindow` shortcuts (`Ctrl+F`, `Alt+Q`), `SingleInstanceService` activation forwarding into `MainWindow`, and `PlayniteProtocolHandler` registration in an isolated XDG profile.

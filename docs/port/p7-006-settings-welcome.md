# p7-006 — Essential settings and welcome

## Oracle and scope

- `ui-010`: `legacy/source/Playnite.DesktopApp/Windows/SettingsWindow.xaml`,
  `Controls/SettingsSections/General.xaml`, `ViewModels/SettingsViewModel.cs` and
  `Controls/Views/MainWindow.cs` in the same legacy project. Keep a draft of the language
  and sidebar preferences; Save commits, Cancel or closing discards. Update sidebar
  visibility immediately after a successful save; apply language on the next launch.
- `ui-011`: `legacy/source/Playnite.DesktopApp/Windows/FirstTimeStartupWindow.xaml`,
  intro only. The welcome is a standalone acknowledgment, not an import or a claim of
  completed setup. `FirstTimeWizardComplete`, source selection, Steam setup, and all
  importer subprocess/handoff and transport behavior belong to p7-009 / ADR-021.

## State and catalog contract

The existing p6-004 service writes the native `<XDG config>/playnite/settings.json` envelope.
Only changed `Language` and `ShowSidebar` values are submitted; the service preserves the
rest of the settings and legacy graph. Missing preferences read as `english` and `true`.
A null archived language displays as the English default without rewriting the archived
value unless the user selects another language. UI changes follow successful writes.

On launch, `english` loads `Localization/en.json` if present, else embedded English.
Other installed catalogs use their filename stem (e.g. `de` for `Localization/de.json`).
A removed catalog falls back to English without replacing the saved preference. Missing keys
continue to fall back through `BootstrapLocalization`. The welcome says nothing about a
future import and is shown until OK atomically writes `welcome-acknowledged` in
the same config directory, independently of the settings envelope.

The approved p7-002 `ShellView.axaml` and `ShellView.axaml.cs` moves preserve the CLR
namespace, `x:Class`, and the established regions and smoke hooks. App and bootstrap are
unchanged. No frozen parity rows, goldens, fixtures, aggregates or test baselines changed.

## Evidence

`DesktopPreferencesTests` checks separate state, pre-cancellation, and preservation of
unrelated changes and wizard state on Save. `SettingsWelcomeSmokeTests` drives the real
headless Avalonia views in an isolated profile, including acknowledgment focus, Cancel,
Save and a restart with and without the selected catalog.

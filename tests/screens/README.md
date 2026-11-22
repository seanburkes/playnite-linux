# UI Screen Baselines (`p7-008`)

Checked-in headless Avalonia + Skia screenshot baselines (`*.png`) and semantic/automation-peer tree baselines (`*.tree.json`) for key desktop screens.

## Render contract

- Renderer: `Avalonia.Headless` + `Avalonia.Skia` (`UseHeadlessDrawing = false`)
- Theme: `FluentTheme` (`ThemeVariant.Light`)
- Scaling: `1.0` (96 DPI)
- Culture: `en-US`
- Screens:
  - `main-shell` (`1280x720`): `MainWindow` workspace with `TopPanel`, `Sidebar`, `ContentHost` (`LibraryGridView` + `GameEditView`), `Notifications`, and 2 seeded games.
  - `welcome` (`1280x720`): `MainWindow` first-launch welcome overlay (`WelcomeTitle`, `AcknowledgeButton`).
  - `about` (`480x240`): `AboutView` hosted in `Window`.
  - `messagebox` (`480x240`): `MessageBoxView` hosted in `Window`.

## Accessibility scope (D1)

Per `docs/port/00-DECISIONS.md` (D1), screen-reader / AT-SPI accessibility is deferred for v1. Semantic-tree baselines verify control names, roles, enabled state, visibility, and keyboard `Tab` focus order as quality practices, not a screen-reader claim.

## Regeneration

Baselines are only regenerated intentionally when a UI layout change is reviewed:

```sh
PLAYNITE_UPDATE_SCREENS=1 dotnet test --project tests/Playnite.Smoke.Tests -c Release --filter ScreenBaselineTests
```

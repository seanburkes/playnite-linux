# Historical UI Screen Captures (`p7-008`)

These screenshots (`*.png`) and automation-peer trees (`*.tree.json`) document an earlier desktop design. They are reference material for human review, not release targets or test inputs. UI changes do not need to update or match them.

## Capture settings

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

Per `docs/port/00-DECISIONS.md` (D1), screen-reader / AT-SPI accessibility is deferred for v1. Headless smoke tests check key controls and keyboard focus directly instead of comparing the captured tree.

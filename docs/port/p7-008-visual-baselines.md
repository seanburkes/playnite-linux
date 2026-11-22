# p7-008 — Headless screenshot and semantic-tree baselines

## Scope and D1 deferral

- **Phase 7 scope (`docs/port/03-EXECUTION-PLAN.md` §Phase 7 item 7, `docs/port/04-VERIFICATION.md` §6):**
  Deliver deterministic headless Skia screenshot baselines and semantic-tree baselines for the key desktop screens, plus keyboard Tab focus checks for main flows.
- **Accessibility deferral (`docs/port/00-DECISIONS.md` D1):**
  The unit shard `docs/port/units/p7-008.queue.tsv` previously carried the shorthand label `Accessibility baseline`. Consistent with the captain's decision and D1 in `docs/port/00-DECISIONS.md`, AT-SPI and screen-reader accessibility (for example Orca) are **deferred for v1** and are **not** claimed by this unit or by v1. On Avalonia 12.1.6, Wayland exposes no AT-SPI tree; AT-SPI is only observable under X11/XWayland and no end-to-end screen-reader pass is performed in v1.
- **What is verified:**
  - Headless Skia rendering at fixed size, `1.0` render scaling (96 DPI), `FluentTheme` Light, and `en-US` culture (`Avalonia.Headless` + `Avalonia.Skia`).
  - Pixel-level comparison against checked-in baseline PNGs under `tests/screens/` with a small per-channel antialiasing tolerance (`<= 2` per RGBA channel, `<= 0.1%` differing pixels).
  - Semantic/automation-peer tree comparison (`ControlAutomationPeer`) against checked-in JSON baselines under `tests/screens/`, verifying control `AutomationId`, `Name`, `ControlType` role, `IsEnabled`, and `IsOffscreen` with non-zero bounding rectangles.
  - Keyboard `Tab` focus traversal across key interactive controls.

## Baseline screens

| Screen | Host / View | Fixed size | Notes |
| --- | --- | --- | --- |
| `main-shell` | `MainWindow` (`ShellView` workspace) | `1280x720` | `welcome-acknowledged` present, `ShowSidebar=true`, `Language=english`, 2 seeded games in `LibraryDatabase` |
| `welcome` | `MainWindow` (`ShellView` welcome overlay) | `1280x720` | `welcome-acknowledged` absent |
| `about` | `AboutView` in `Window` | `480x240` | Deterministic localized title and author |
| `messagebox` | `MessageBoxView` in `Window` | `480x240` | Deterministic title and message |

## Evidence

`tests/Playnite.Smoke.Tests/p7-008/ScreenBaselineTests.cs` runs `HeadlessUnitTestSession` with `UseSkia()` and `AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }` against isolated temporary XDG directories, asserts non-blank repeatable renders, matches pixels and semantic trees against `tests/screens/*.png` and `tests/screens/*.tree.json`, and exercises keyboard `Tab` focus traversal.

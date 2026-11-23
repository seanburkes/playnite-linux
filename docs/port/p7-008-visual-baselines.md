# p7-008 — Headless UI usability checks

## Scope and D1 deferral

- **Phase 7 scope (`docs/port/03-EXECUTION-PLAN.md` §Phase 7 item 7, `docs/port/04-VERIFICATION.md` §6):**
  Verify key desktop controls and keyboard navigation in headless tests. Screenshots and full semantic-tree captures are for human review only.
- **Accessibility deferral (`docs/port/00-DECISIONS.md` D1):**
  The unit shard `docs/port/units/p7-008.queue.tsv` previously carried the shorthand label `Accessibility baseline`. Consistent with the captain's decision and D1 in `docs/port/00-DECISIONS.md`, AT-SPI and screen-reader accessibility (for example Orca) are **deferred for v1** and are **not** claimed by this unit or by v1. On Avalonia 12.1.6, Wayland exposes no AT-SPI tree; AT-SPI is only observable under X11/XWayland and no end-to-end screen-reader pass is performed in v1.
- **What is verified:**
  - The main shell exposes key named controls and supports keyboard `Tab` navigation.
  - The first-run acknowledgment is visible, enabled, and keyboard focusable.
  - About and MessageBox display their supplied title and body text.

## Screens exercised

| Screen | Host / View | Fixed size | Notes |
| --- | --- | --- | --- |
| `main-shell` | `MainWindow` (`ShellView` workspace) | `1280x720` | `welcome-acknowledged` present, `ShowSidebar=true`, `Language=english`, 2 seeded games in `LibraryDatabase` |
| `welcome` | `MainWindow` (`ShellView` welcome overlay) | `1280x720` | `welcome-acknowledged` absent |
| `about` | `AboutView` in `Window` | `480x240` | Deterministic localized title and author |
| `messagebox` | `MessageBoxView` in `Window` | `480x240` | Deterministic title and message |

## Evidence

`tests/Playnite.Smoke.Tests/p7-008/ScreenBaselineTests.cs` runs four `HeadlessUnitTestSession` cases. The shell and first-run cases use isolated temporary XDG directories. The tests do not read or regenerate the historical captures in `tests/screens/`.

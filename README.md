# Playnite Linux

A Linux-first, .NET 11 / Avalonia port of [Playnite](https://github.com/JosefNemec/Playnite) 10.64.

The Windows application is **frozen** at v10.64 and checked in under [`legacy/`](legacy/) as
the behavioural oracle. New work happens in this repository against that oracle; the Windows
app is not migrated to .NET 11 and is not modified.

**Status: planning.** Nothing is shipped. Start with [`docs/port/README.md`](docs/port/README.md).

## Target, in one paragraph

A desktop Linux application that discovers and launches Steam games, imports the Steam-linked
part of an existing Playnite v10 library, migrates it one-way to SQLite, and presents
Playnite's library organisation in a functional Avalonia 12 desktop shell. It runs on x64 and
ARM64, ships as an AppImage and a Flatpak, and exposes a new plugin SDK for source-level
recompilation. Fullscreen, emulation, non-Steam launchers, WebView/JavaScript metadata, and the
v10 theme/plugin binaries are explicitly out of v1 scope. Non-Steam rows in a v10 library are
reported as skipped, not silently dropped.

## Decisions already made

| Area | Decision |
| --- | --- |
| Platform | Linux-first; Windows v10 frozen as oracle |
| Runtime | `net11.0` (STS, supported to Nov 2028) |
| UI | Avalonia 12, desktop shell first |
| Web | `Avalonia.HtmlRenderer` in v1; `Avalonia.Controls.WebView` (WebKitGTK/WPE) in v1.1 |
| Storage | Copy v10 library, one-way migration to SQLite |
| Plugins | New SDK v7; source-compatible recompile, no binary compatibility |
| Themes | New Avalonia theme format; functional default theme only in v1 |
| Packages | x64 + ARM64, AppImage + Flatpak |
| Verification | Practical rigor + TLA+ models for import, session, and migration |
| Repo | Standalone; no upstream merge path |

## Repository layout

```
legacy/                 Frozen Playnite 10.64 source (read-only oracle)
src/                    New .NET 11 projects (created in Phase 1)
tests/                  New tests, fixtures, golden corpus
oracle-lock/            SHA-256 pin + fetch script for Playnite.Deps
scripts/port/           Deterministic inventory and queue generators
docs/port/              Decisions, inventory, architecture, execution, verification, operations
docs/formal/            TLA+ models for critical state machines
```

## Ground rules

1. `legacy/` is read-only. CI fails if it changes.
2. Tests, fixtures, goldens, and formal models are not editable in a porting change.
3. No behaviour redesign during the port. File improvements as follow-ups.
4. Every unit lands with a test, an oracle reference, and a ledger row.
5. "It compiles" is not evidence of behaviour.

See [`docs/port/PORTING.md`](docs/port/PORTING.md) for the coding contract.

## License

Playnite is MIT-licensed. This port keeps the upstream license and copyright notice. See
[`LICENSE.md`](LICENSE.md) (to be copied from `legacy/` when the first code lands).

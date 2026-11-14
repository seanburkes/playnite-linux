# Port status

**Phase:** 0 — decisions and inventory
**Updated:** 2026-11-13
**Next gate:** captain review, then Phase 1 (freeze the oracle)

## Done

- [x] Frozen v10.64 source imported to `legacy/` (24 MB, no history).
- [x] 16 ADRs recorded (Linux-first, .NET 11, Avalonia 12, SQLite migration, SDK v7,
      theme format, v1 scope, architectures, packaging, verification, operations).
- [x] Inventory: 143,152 C# LOC, 68,226 XAML LOC, 726 C# files, 275 XAML files,
      204 tests, 70 files with Windows-only APIs.
- [x] Target project graph and dependency rules.
- [x] Phase plan with gates and estimates.
- [x] Verification design (six layers, oracle ladder, three TLA+ models, CI gates).
- [x] Dependency replacement matrix.
- [x] Operating model for firstmate/worktrees/reviews.
- [x] Seeded surface parity ledger.
- [x] Formal model drafts (`docs/formal/`).
- [x] Deterministic inventory scanner (`scripts/port/scan_legacy.py`).
- [x] Firstmate cloned at `~/src/firstmate`.

## In flight

- [ ] Review this plan with the captain; resolve open questions.
- [ ] Create the public GitHub repo and push the first commit.
- [ ] Register `playnite-linux` in firstmate (`local-only` until `origin` exists).
- [ ] Run the Phase 1 spikes (LiteDB on net11, WebKitGTK/WPE offscreen, Steam process
      tracking, Avalonia headless screenshots).

## Not started

- Phase 1 oracle harness and Windows CI.
- Phase 2 solution/projects/analyzers.
- Any production code.

## Open decisions / questions

| # | Question | Default if unanswered |
| --- | --- | --- |
| Q1 | Confirm the public repo owner/name (`seanburkes/playnite-linux`) and visibility | Public, MIT, no upstream PRs |
| Q2 | Is a Windows runner available, or does the oracle rely on GitHub Actions? | GitHub Actions Windows |
| Q3 | Does the fixture library contain user data we may commit, or must it be synthesised? | Synthesised fixture only |
| Q4 | Should the Flatpak app ID be `org.playnite.PlayniteLinux` or a personal namespace? | `io.github.seanburkes.PlayniteLinux` |
| Q5 | Which distro/SteamOS versions are the release gate? | Ubuntu 24.04/26.04, Fedora 44/45, Arch/SteamOS, Debian 13 |
| Q6 | Is a visual designer available for the Avalonia theme, or do we hand-build? | Hand-build functional theme |
| Q7 | Do we need a trademark/name policy review before publishing? | Use "Playnite Linux (unofficial)" and link upstream |

## Blockers

- Herdr server is not running in this session (`HERDR_ENV` unset). Firstmate can use tmux; no
  execution blocker.
- Windows oracle CI needs a runner image with .NET Framework MSBuild and access to
  `nuget.playnite.link` for `Playnite.Deps`.
- LiteDB 4 compatibility on `net11.0` is unproven until the Phase 1 spike.

## Metrics

| Metric | Value | Source |
| --- | ---: | --- |
| Legacy C# LOC | 143,152 | `scan_legacy.py` |
| Legacy XAML LOC | 68,226 | `scan_legacy.py` |
| Legacy tests | 204 | `scan_legacy.py` |
| Windows-coupled files | 70 | `scan_legacy.py` |
| New tests | 0 | — |
| Ported parity rows | 0 | `surface-parity.tsv` |
| Open ADRs | 0 | `00-DECISIONS.md` |

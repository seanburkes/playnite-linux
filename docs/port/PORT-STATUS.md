# Port status

**Phase:** 0 — decisions and inventory
**Updated:** 2026-11-13
**Next gate:** captain review, then Phase 1 (freeze the oracle)

## Done

- [x] Frozen v10.64 source imported to `legacy/` (24 MB, no history).
- [x] 23 ADRs recorded (Linux-first, .NET 11, Avalonia 12, SQLite migration, SDK split,
      theme format, v1 scope, architectures, packaging, verification, operations).
- [x] Inventory: 143,152 C# LOC, 68,226 XAML LOC, 726 C# files, 275 XAML files,
      204 test methods (82 parameterised cases), 72 files with Windows-only APIs.
- [x] Target project graph and dependency rules.
- [x] Phase plan with gates and estimates.
- [x] Verification design (six layers, oracle ladder, three TLA+ models, CI gates).
- [x] Dependency replacement matrix.
- [x] Operating model for firstmate/worktrees/reviews.
- [x] Seeded surface parity ledger.
- [x] Formal models checked by TLC (LibraryImport with a concurrent reader, GameSession,
      SettingsMigration).
- [x] Deterministic inventory scanner (`scripts/port/scan_legacy.py`).
- [x] Firstmate cloned at `~/src/firstmate`.
- [x] Independent plan review; findings incorporated: Steam is greenfield, oracle harness
      constrained to v10 APIs, Playnite.Deps pinned by SHA-256, Flatpak host launch/tracking,
      LiteDB importer is a separate process, WebView deferred to v1.1, SDK split into
      Abstractions + UI, executed-case ratchet, all oracle paths guarded.

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
| Q4 | Should the Flatpak app ID be `io.github.seanburkes.PlayniteLinux`? | `io.github.seanburkes.PlayniteLinux` |
| Q5 | Which distro/SteamOS versions are the release gate? | Ubuntu 24.04/26.04, Fedora 44/45, Arch/SteamOS, Debian 13 |
| Q6 | Is a visual designer available for the Avalonia theme, or do we hand-build? | Hand-build functional theme |
| Q7 | Do we need a trademark/name policy review before publishing? | Use "Playnite Linux (unofficial)" and link upstream |
| Q8 | Is WebView v1.1 acceptable, or must JavaScript metadata scraping be in v1? | v1.1 (ADR-019) |
| Q9 | Which locales must be non-partial at v1? | English complete; others reported as partial |

## Blockers

- Herdr server is not running in this session (`HERDR_ENV` unset). Firstmate can use tmux; no
  execution blocker.
- Windows oracle CI needs a runner image with .NET Framework MSBuild. `Playnite.Deps` is
  publicly reachable, but it is a mutable third-party feed; the SHA-256 pin in `oracle-lock/`
  is the reproducibility control, and redistribution needs a license review.
- LiteDB 4 compatibility on `net11.0` is unproven until the Phase 1 spike.
- Steam is greenfield: no v10 implementation exists to port, so its oracle is a recorded
  artifact corpus + real-install acceptance, not a v10 diff.

## Metrics

| Metric | Value | Source |
| --- | ---: | --- |
| Legacy C# LOC | 143,152 | `scan_legacy.py` |
| Legacy XAML LOC | 68,226 | `scan_legacy.py` |
| Legacy test methods | 204 (82 parameterised cases) | `scan_legacy.py` |
| Windows-only files (non-WPF) | 72 | `scan_legacy.py` |
| New tests | 0 | — |
| Ported parity rows | 0 | `surface-parity.tsv` |
| Open ADRs | 0 | `00-DECISIONS.md` |

# Port status

**Phase:** 2 — scaffolding and Phase 2 kickoff (the Phase 1 READY gate was accepted)
**Updated:** 2026-11-15
**Next gate:** Phase 2 exit — Linux build/test green, architecture and Windows oracle CI,
packaging smoke, UI pilot

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
- [x] Firstmate cloned at `~/src/firstmate`; prerequisites installed; project registered `direct-PR`.
- [x] Public repo created: https://github.com/seanburkes/playnite-linux
- [x] Windows oracle baseline: 281 executed, 281 passed, 0 failed, 0 skipped with the
      documented exclusions EX-1, EX-2 and EX-3 (run 43649562478; `tests/baseline.json`,
      `tests/legacy-exclusions.md`).
- [x] LiteDB 4.1.4 read/write spike on `net11.0` passed (`spikes/litedb-net11`).
- [x] Synthetic Steam fixture corpus (`tests/fixtures/steam/`).
- [x] Oracle harness (`tools/LegacyOracle`) constrained to v10 APIs, with the 21
      settings/library goldens, `settings-library.sha256` and provenance (p1-002); manifest v0
      goldens are byte-compared with a one-byte negative.
- [x] v10 fixture library captured (p1-003, 18 payloads) with pinned
      `tests/fixtures/v10/SHA256SUMS`, strict committed-fixture comparison and typed Windows
      read-back.
- [x] Independent plan review; findings incorporated: Steam is greenfield, oracle harness
      constrained to v10 APIs, Playnite.Deps pinned by SHA-256, Flatpak host launch/tracking,
      LiteDB importer is a separate process, WebView deferred to v1.1, SDK split into
      Abstractions + UI, executed-case ratchet, all oracle paths guarded.
- [x] Curated official/public UI reference index (p1-004, reference-only; no images
      committed).
- [x] Real native Steam acceptance (p1-007, Terraria 105600) and real Flatpak
      `flatpak-spawn --host` sandbox acceptance (p1-010).
- [x] Avalonia real Wayland/X11 launch and AT-SPI evidence (p1-008); Wayland accessibility is
      an accepted v1 limitation (Deferred limitations D1).
- [x] Real Steam artifact corpus committed (p1-009, 11 paths with verifiable `SHA256SUMS`).
- [x] Oracle one-byte negatives and harness path protection for `tools/LegacyOracle` and
      `tools/V10LibraryFixture` (CODEOWNERS + plan-guards).
- [x] Port-index unit shards with parent-owned aggregate regeneration on `main` and the
      workflow hardening from #21 (push-to-main only, history check before build, no skip
      marker).
- [x] Phase 1 gate reconciled to READY in `docs/port/gates/P1-oracle.md`; p1-006 WebView is
      deferred to v1.1 and is not a v1 gate.

## In flight

Phase 2 kickoff units, none started; p2-001 is the entry point:

- p2-001 solution/projects/props; p2-002 analyzers + architecture tests; p2-003 Linux CI +
  Windows oracle CI; p2-004 empty Avalonia app + CLI smoke; p2-005 packaging smoke; p2-006
  SDK Abstractions project + PublicAPI.

## Not started

- Phase 3+ porting units and any production code beyond the Phase 2 scaffold.

## Deferred / known limitations

- **D1 — Wayland accessibility (accepted v1 deferral).** Avalonia 12.1.6 exposes no AT-SPI
  tree on native Wayland; accessibility is only observable under X11/XWayland. No
  end-to-end screen-reader test (for example Orca) was performed, so screen-reader behaviour
  is not claimed. This is a v1 deferral, not an open Phase 1 gate item.
- **p1-006 — WebView** is deferred to v1.1 by ADR-019; v1 has no browser-engine dependency.
- **p1-guard-protect** (extend the protected-path check to the guard workflow,
  `.github/CODEOWNERS` and the port-index generator) is carried into Phase 2 hardening; it is
  not an open Phase 1 gate item.
- **Bounded evidence:** the Steam acceptance is single-title/single-host; the Flatpak probe
  ran on an EOL runtime; the X11 accessibility evidence is XWayland, not bare Xorg.

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
- Steam is greenfield: no v10 implementation exists to port, so its oracle is a recorded
  artifact corpus + real-install acceptance, not a v10 diff.

## Metrics

| Metric | Value | Source |
| --- | ---: | --- |
| Legacy C# LOC | 143,152 | `scan_legacy.py` |
| Legacy XAML LOC | 68,226 | `scan_legacy.py` |
| Legacy executed cases (oracle) | 281 passed, 3 excluded | `tests/baseline.json` |
| Windows-only files (non-WPF) | 72 | `scan_legacy.py` |
| New tests at Phase 2 start | 0 | `tests/baseline.json` (`new` is null) |
| Ported parity rows | 0 | `surface-parity.tsv` (0 of 96 rows ported) |
| Open ADRs | 0 | `00-DECISIONS.md` |

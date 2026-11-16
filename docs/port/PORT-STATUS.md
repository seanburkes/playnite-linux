# Port status

**Phase:** 3 — implementation merged; exit pending owner acceptance
**Updated:** 2026-11-16
**Next gate:** Phase 4 (only after the owner accepts the Phase 3 exit)

## Done

- [x] Frozen v10.64 source imported to `legacy/` (24 MB, no history).
- [x] 24 ADRs recorded (Linux-first, .NET 11, Avalonia 12, SQLite migration, SDK split,
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
- [x] Phase 2 solution/projects, shared build/package configuration, analyzers, architecture
      rules, Linux CI, empty Avalonia/CLI smoke and SDK Abstractions/PublicAPI scaffold merged.
- [x] Gate tree `a2b66339a41130b40da0b42b94df7984227cf81c`: Linux run
      [43758217982](https://github.com/seanburkes/playnite-linux/actions/runs/43758217982)
      passed Architecture 144/144 and Smoke 4/4, zero failures/skips; Core is declared empty
      (0 cases, MTP exit 8, explicit notice), not a counted passing suite.
- [x] AppImage and Flatpak packaging run
      [43758217954](https://github.com/seanburkes/playnite-linux/actions/runs/43758217954)
      passed on the same tree: 43,731,448-byte AppImage and 34,190,272-byte Flatpak.
- [x] Fresh Windows oracle run
      [43758406267](https://github.com/seanburkes/playnite-linux/actions/runs/43758406267)
      succeeded on the gate tree: 281/281 passed, zero failures/skips, unchanged three
      authorised exclusions; downloaded TRX and counts agree.
- [x] New-suite baseline recorded from run 43740834379: Architecture 103/103, Smoke 4/4,
      Core declared empty; current 144/4 counts preserve the ratchet.
- [x] Standalone fake-data UI pilot, headless PNG and hardened semantic-tree baseline merged:
      https://github.com/seanburkes/playnite-linux/pull/38 (`9edf2bb`),
      https://github.com/seanburkes/playnite-linux/pull/51 (`858eb66`),
      https://github.com/seanburkes/playnite-linux/pull/54 (`5ce8536`). Not product integration.
- [x] First Linux performance proxies recorded in `PERFORMANCE.md` by
      https://github.com/seanburkes/playnite-linux/pull/52; empty-library-open remains
      NOT MEASURED, explicitly deferred to Phase 4 because no library/database/open API exists.
- [x] Merged Phase 2 hardening board reconciled in shards; stale UI follow-up notes removed.
      `gates/P2-scaffolding.md` records run identities, hashes, declared deferrals and residuals.
- [x] Phase 2 accepted with declared deferrals on 2026-11-15: D1 accessibility, WebView
      to v1.1 and empty-library-open to Phase 4. Exit artifact
      https://github.com/seanburkes/playnite-linux/pull/64 merged at `0e39326`;
      owner acceptance authorises Phase 3, without waiving the recorded residual risks.
- [x] Phase 3 pure-logic units merged: paths/XDG (#68), string/date/enum (#67),
      serialisation (#69, #71), models/equality (#72, #74, #75), manifests (#73),
      search/filter/sort (#76), localisation keys and diff (#78), archive/backup
      (#79, #80), v10-to-new key migration (#81), locale coverage (#82), CRC/checksums
      (#83, #85) and the bounded serialisation fuzz gate (#84). Base rows `p3-007` and
      `p3-008` are resolved as superseded-by their continuations `p3-007b`/`p3-008b`.
- [x] Phase 3 exit evidence drafted in `gates/P3-leaves.md` and checked at main
      `f6d5fab`: run
      [43803577215](https://github.com/seanburkes/playnite-linux/actions/runs/43803577215)
      reports Core 1533/1533, Architecture 144/144 and Smoke 4/4 with zero
      failures/skips; the bounded fuzz step printed `FUZZ COMPLETE`; `legacy/` is
      unchanged since Phase 1; the English key-set diff is empty and missing
      translations are reported. This is **ready for owner acceptance**, not a declared
      exit.

## In flight

None. Phase 3 units are merged; the exit is pending owner acceptance. Phase 4 has not
started and is not dispatched by the exit draft.

## Not started

- Phase 4+ porting units (SQLite migration, settings migration, real library import) and
  any production code beyond the merged Phase 3 pure-logic leaves.

## Deferred / known limitations

- **D1 — Wayland accessibility (accepted v1 deferral).** Avalonia 12.1.6 exposes no AT-SPI
  tree on native Wayland; accessibility is only observable under X11/XWayland. No
  end-to-end screen-reader test (for example Orca) was performed, so screen-reader behaviour
  is not claimed. This is a v1 deferral, not an open Phase 1 gate item.
- **p1-006 — WebView** is deferred to v1.1 by ADR-019; v1 has no browser-engine dependency.
- **Empty-library-open:** NOT MEASURED, deferred to Phase 4; startup/working-set proxies
  are not readiness, first-frame or loaded-library measurements. Two same-host warm-cache
  repeats do not establish a distribution.
- **ADR-024:** single-owner adversarial assistance is not independent approval. The owner
  exception was taken; correlated blind spots and owner-controlled protection remain risks.
- **Path guard:** merged hardening is not exhaustive; bypass and over-blocking classes may
  remain, detailed in `gates/P2-scaffolding.md`. Historical ledger exceptions stay immutable.
- **p2-006-harden:** review A's P2 on unsupplied negative full-suite proof logs remains
  open; those logs were never supplied. Acceptance records this residual, not a proof pass.
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

- No Phase 3 blocker: all Phase 3 pure-logic units are merged and the exit evidence is ready
  for owner acceptance. Library-open and the negative-proof-log residual remain explicitly
  unproven.
- Release work still needs dependency redistribution/license review. Steam remains greenfield
  with corpus/real-install acceptance rather than a v10 implementation diff; neither is an
  unreported scaffolding test pass.

## Metrics

| Metric | Value | Source |
| --- | ---: | --- |
| Legacy C# LOC | 143,152 | `scan_legacy.py` |
| Legacy XAML LOC | 68,226 | `scan_legacy.py` |
| Legacy executed cases (oracle) | 281 passed, 3 excluded | `tests/baseline.json` |
| Windows-only files (non-WPF) | 72 | `scan_legacy.py` |
| New-suite recorded baseline | 1533 Core + 103 architecture + 4 smoke | `tests/baseline.json`, additive captures through run 43800467861 |
| Current new-suite cases | 1533 Core + 144 architecture + 4 smoke; zero failures/skips | run 43803577215 on `f6d5fab` |
| Ported parity rows | 0 | `surface-parity.tsv` (0 of 96 rows ported) |
| Open ADRs | 0 | `00-DECISIONS.md` |

# Gate P2 — Linux scaffolding

- Date: 2026-11-15
- Gate commit: `a2b66339a41130b40da0b42b94df7984227cf81c` (`main`). This documentation
  change records that tree; it does not claim that its own later commit ran these workflows.
  The exit artifact merged in https://github.com/seanburkes/playnite-linux/pull/64 at
  `0e39326`; this acceptance correction follows that merge without rewriting its evidence.
- Owner: scaffolding / p2-exit
- Review model: adversarial assistance on the owner's behalf, not independent approval.
  The single-owner exception in ADR-024 was taken for the merged units; no external-human
  review is claimed. This exit artifact has two worker diff-only self-review passes;
  these are not separate reviewer agents or approval. Firstmate subsequently supplied two
  verdict reviews; their wording corrections are incorporated here.
- Result: **ACCEPTED with declared deferrals** — owner acceptance dated **2026-11-15**;
  Phase 3 is authorised. Acceptance retains D1 accessibility, WebView to v1.1,
  empty-library-open to Phase 4 and every bounded claim below. Empty-library-open is
  deferred, not a passing measurement.

## Evidence

| Item | State | Commit / run | Evidence and boundary |
| --- | --- | --- | --- |
| Linux build and tests | pass | `a2b66339a41130b40da0b42b94df7984227cf81c`; [43758217982](https://github.com/seanburkes/playnite-linux/actions/runs/43758217982) | Forge conclusion success; downloaded `linux-test-results/test-counts.json` records Architecture **144 executed / 144 passed / 0 failed / 0 skipped**, Smoke **4 / 4 / 0 / 0**. Core **0 cases**, MTP exit 8, `declared-empty`, with notice “declared zero-case scaffold; no tests yet; 0 executed cases, explicitly declared in empty_projects.” Core is not a passing nonempty suite. Native Xvfb smoke opened and closed the window. |
| Fresh Windows oracle | pass with existing exceptions | `a2b66339a41130b40da0b42b94df7984227cf81c`; [43758406267](https://github.com/seanburkes/playnite-linux/actions/runs/43758406267) | `workflow_dispatch` on main; forge `head_sha` verified equal to the gate commit, conclusion success. Downloaded `test-counts.json` and TRX counters agree: **281 executed / 281 passed / 0 failed / 0 skipped**. The same three authorised exclusions EX-1/EX-2/EX-3 remain; see `tests/legacy-exclusions.md` and the Phase 1 gate. No new exclusion. |
| AppImage and Flatpak | pass | `a2b66339a41130b40da0b42b94df7984227cf81c`; [43758217954](https://github.com/seanburkes/playnite-linux/actions/runs/43758217954) | Both jobs succeeded. Their emitted summaries report **AppImage 43,731,448 bytes**, **Flatpak 34,190,272 bytes** (package sizes, not uploaded ZIP sizes). Packaged CLI version, desktop open/close, binary/icon resolution and Flatpak narrow permissions passed. These are empty-shell packaging smokes, not a production release. |
| Recorded new-suite baseline | captured; ratchet holds | `68ee1b9976e9c5fbc7d3ae9171c8b49243c792e4`; [43740834379](https://github.com/seanburkes/playnite-linux/actions/runs/43740834379) | `tests/baseline.json` records Architecture **103/103**, Smoke **4/4**, both zero failures/skips, and Core declared empty outside the counted baseline. Downloaded run counts agree; the original capture called Core `bootstrap-empty`, before the explicit declaration was committed. Current Architecture **144 ≥ 103** and Smoke **4 ≥ 4**; no zero-case floor is invented for Core. |
| UI pilot and semantic baseline | done, standalone spike | [pilot](https://github.com/seanburkes/playnite-linux/pull/38) `9edf2bb139d7ea5fdc0633b3718997040816cb85`; [baseline](https://github.com/seanburkes/playnite-linux/pull/51) `858eb66ee7334473fc9ecf966cfa7f63d9907561`; [hardening](https://github.com/seanburkes/playnite-linux/pull/54) `5ce8536c73e90aa8dff3011c67ae10f59e57f187` | `spikes/ui-shell/README.md`, `ShellTests.cs`, `compare_processes.py` and `Baselines/library-1200.semantic.json`: real headless Skia, fake data, resource swaps, grid/search/filter/details and baseline-independent required-content checks. Outside the product solution and Linux CI. See identity evidence below. |
| Performance baseline | measured proxies; library open deferred | [performance](https://github.com/seanburkes/playnite-linux/pull/52) `c1c0cc7fc5dd262a49ea833d65540504a25c75e9`; measured product source `0a084a44178a5fae1bdeceee33265668f3a3cb6e` | Committed `docs/port/PERFORMANCE.md` records two ten-sample repeats, excluded warm-ups, commands and machine. These are historical observations, not measurements on `a2b6633` or performance thresholds. |

### Merge and board verification

Every implementation merge below was checked with
`git merge-base --is-ancestor <merge> a2b6633`; the later exit-artifact merge was
separately verified as an ancestor of acceptance-correction base `80b0e6b`.
The reconciliation changes only queue shards and appends ledger evidence; historical rows
remain unchanged and the parent post-merge workflow alone commits generated aggregates.

- `p2-ci-cost`: [advisory filters/formal pin](https://github.com/seanburkes/playnite-linux/pull/61)
  at `c448dd79a2944e4f732eac5c9e99e3b0d5e6da63`; final evidence is
  **121 CI-policy tests** (the historical 118 was pre-main-merge). Review records A/B are
  `data/p1-tooling-review-a/review-p2-061.md` and
  `data/p1-tooling-review-b/review-p2-061.md`.
- `p2-local-path-guard`: [path-guard follow-up](https://github.com/seanburkes/playnite-linux/pull/63)
  at `bc649e589e35e1ba741067bbc3ca3657a405fb4d`; final evidence is
  **94 generator / 123 CI-policy tests**. Review records A/B are the corresponding
  `review-p2-063.md` files. This is not a `p2-ci-cost` follow-up. Review packets include
  stale-candidate caveats and residual findings; citing them is not claiming every finding
  was fixed.
- `p2-guard-wording2`: [wording correction](https://github.com/seanburkes/playnite-linux/pull/55)
  at `9a4c79f7a91644063f1eb8a5a496968b54b63c20`; 116 policy / 144 architecture cases
  recorded by the unit. Protected-path authorization remains the `oracle-change` route.
- `p2-ui-semantics`, `p2-ui-pilot` and `p2-board-close` now agree: both UI changes
  above are merged; the earlier “semantics hardening remains open” notes are superseded,
  not erased from ledger history.
- Additional stale rows: [workflow/SSH hardening](https://github.com/seanburkes/playnite-linux/pull/59)
  at `d1c6dedf02656f2083fed77dda9195b6526cff8f` and
  [initial local-path guard](https://github.com/seanburkes/playnite-linux/pull/62) at
  `634d96e97f8f83d6bd51d57a69bf6dfa5d123ba7` plus its `p2-local-path-guard`
  follow-up above are merged.
  Closing those rows records delivery, not absence of the residual risks below.

### UI two-process identity evidence

The pilot's final `review-final-3` evidence records two fresh processes, six facts each,
zero skips; three captures per process, all six PNGs byte-identical, 69,092 bytes,
SHA-256 `f5bde1e9872e6a44ee7fb4d1a1dd409cdb577b7e6c0588314c5096f36a5d10e5`.
The baseline added two eight-fact process runs; hardening added two nine-fact runs,
zero skips. The committed README records direct cross-process comparison and
`comparison.json` hashes: both emitted semantic trees and the committed 46-node tree
are byte-identical, SHA-256
`886a28797c9b0dd4d12271d26fac472513f7370703830ed8ce43b30b03226b5e`.
All six hardening PNGs retain the pilot digest. Pre-final bootstrap/pilot captures
are excluded. This gate rechecks the committed semantic hash and records the merged
two-process evidence; it does not claim a fresh UI execution on the gate commit.
The structural comparator tolerates ±1 logical pixel per bounds component, not structural
drift; byte identity is separate recorded evidence. This is a visual control-tree
projection, not an Avalonia automation-peer or AT-SPI tree. It meets the plan's literal
“semantic-tree baseline” exit line. **No automation-peer baseline exists for the product
shell**; product UI parity is not claimed and accessibility remains deferred under D1.

### Performance medians and explicit gap

| Metric | Repeat 1 | Repeat 2 |
| --- | ---: | ---: |
| CLI launch → version observed (ms) | 17.906 | 17.977 |
| CLI launch → exit (ms) | 18.482 | 18.550 |
| CLI lifetime peak RSS (KiB) | 25,128 | 24,866 |
| Desktop Opened → Closed observed (ms) | 78.245 | 75.827 |
| Desktop launch → Opened observed (ms) | 273.256 | 257.724 |
| Desktop launch → exit (ms) | 359.623 | 340.406 |
| Desktop lifetime peak RSS (KiB) | 60,466 | 60,630 |
| Empty-library open | **NOT MEASURED** | **NOT MEASURED** |

These are sequential same-host warm-cache repeats about 23 seconds apart, not independent
runs; two repeats cannot establish a performance distribution. The host was Ryzen 7 5700,
CachyOS Linux 7.3.0-3-cachyos, SDK 11.0.100/runtime 11.0.0, Avalonia 12.1.6.
Opened is not first frame or readiness; lifetime peak RSS is not a loaded-library working
set. The 5.7% launch-to-Opened difference is host/measurement noise, not a trend.
**Empty-library open is deferred to Phase 4 because no library/database/open API exists**;
an empty shell is not an empty-library fixture. The performance deliverable remains
partially measured, with that explicit deferral rather than a fictional number.

## Deferrals and accepted residual risk

- **D1:** native Wayland accessibility is deferred for v1. Avalonia 12.1.6 has no observed
  Wayland AT-SPI tree; X11/XWayland evidence is bounded and no end-to-end screen-reader
  test was performed.
- **WebView:** deferred to v1.1 by ADR-019; no v1 browser-engine dependency.
- **Empty-library-open performance:** NOT MEASURED, deferred to Phase 4 as above.
- **p2-006-harden negative-proof evidence:** review A's P2 on unsupplied negative
  full-suite proof logs remains open; those logs were noted but never supplied. The
  merged hardening unit does not claim that missing evidence. Owner acceptance retains
  this residual rather than treating the positive 144-case suite as negative-proof logs.
- **ADR-024 single-owner exception:** review assistance is correlated and can miss shared
  blind spots; the owner selects evidence and controls merge/protection decisions. Frozen
  sources, hashes, protected-path diffs, executed-case ratchets and required checks provide
  structural checks, not proof of semantic correctness. No independent human approval is
  asserted. The ADR's historical unpinned-formal-tool concern is superseded by the pinned
  CI tool in the advisory-filter merge, not by a claim that all local JAR consumers verify it.

## What is NOT covered

- Actual empty-library-open timing, first rendered frame, application readiness,
  steady-state loaded-library memory, real-display or large-library performance.
- Product integration of the standalone UI spike, v10 UI parity, OS accessibility,
  keyboard/pointer end-to-end acceptance or a finished user-theme format.
- Production packaging/release acceptance, all distro/architecture combinations, or
  broad Steam/Proton/Flatpak acceptance beyond the previously bounded probes.
- Exhaustive path-guard correctness. Residual classes may remain: Windows profile
  aliases beyond USERPROFILE, relative task-path prefixes and URL-boundary cases;
  generic Steam/config subpaths and numeric tilde prose may be over-blocked. Binary
  document contents are outside the text guard. Fixed historical exceptions are not
  permission to add new personal paths. These are declared limitations, not fixed claims.
- Social independence of review or protection against deliberate owner-controlled gate
  weakening; SSH key cleanup does not sandbox the generator during the writer step.

## Exit checklist and Phase 3 entry

- [x] Linux x64 build/test and architecture checks green at the gate commit; skips zero.
- [x] Fresh Windows oracle green at the same commit, with executed counts verified.
- [x] AppImage and Flatpak smoke artifacts produced at the same commit.
- [x] Standalone UI pilot screenshot and semantic-tree baseline merged, with bounded evidence.
- [x] New-suite baseline committed and current executed counts non-decreasing.
- [x] First performance proxies recorded; empty-library-open explicitly deferred to Phase 4.
- [x] Board rows reconciled in shards without editing aggregate/history bytes.

**Verdict: ACCEPTED with declared deferrals — owner acceptance 2026-11-15.** Linux,
Windows, packaging and plan-guards evidence was verified at `a2b6633`; the recorded baseline
comes from run 43740834379 at `68ee1b9`; UI and performance evidence is historical from
their merged PRs. The performance gap, D1 and
WebView are explicit deferrals, not passes, and the negative-proof-log residual remains open.
Phase 3 is authorised; its next gate is Phase 3 exit. This acceptance does not retroactively
make the documentation PR's head the subject of the recorded runtime evidence.

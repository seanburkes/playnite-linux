# Gate P1 — Windows oracle baseline

- Date: 2026-11-14
- Reconciled: 2026-11-15 against `main` at `8c5a811`, which contains the p1-009 corpus merge
  `2045697`; this is the READY verdict proposed for captain approval
- Owner: oracle
- Reviewer: independent plan review (session `ses_e27629994ffec0IK0wD5lgcnOT`); #25 gate
  review findings are addressed below; captain approval of the READY verdict is still pending
- Runs (all verified against the forge; each id below is the workflow named beside it):
  - legacy-oracle v10 suite: https://github.com/seanburkes/playnite-linux/actions/runs/43649562478
  - settings/library generation: https://github.com/seanburkes/playnite-linux/actions/runs/43653446375
    and validation https://github.com/seanburkes/playnite-linux/actions/runs/43653802671
  - hardened-corpus re-runs: https://github.com/seanburkes/playnite-linux/actions/runs/43656640766
    and https://github.com/seanburkes/playnite-linux/actions/runs/43657110640
  - strict fixture verification: https://github.com/seanburkes/playnite-linux/actions/runs/43650322895
    and https://github.com/seanburkes/playnite-linux/actions/runs/43650673457
  - port-index / plan-guards on `main` for #21:
    https://github.com/seanburkes/playnite-linux/actions/runs/43656817650 /
    https://github.com/seanburkes/playnite-linux/actions/runs/43656817607
  - #22: https://github.com/seanburkes/playnite-linux/actions/runs/43660863563 /
    https://github.com/seanburkes/playnite-linux/actions/runs/43660863485
  - #24: https://github.com/seanburkes/playnite-linux/actions/runs/43660917613 /
    https://github.com/seanburkes/playnite-linux/actions/runs/43660917606
  - #26 (p1-009 corpus): https://github.com/seanburkes/playnite-linux/actions/runs/43662983092 /
    https://github.com/seanburkes/playnite-linux/actions/runs/43662983105
- Result: **READY** — every Phase 1 deliverable is merged and verified on the committed tree.
  p1-009's 11 protected `tests/fixtures/steam/real/` paths are committed in #26 with a
  verifiable `SHA256SUMS`, so the last open item is closed. p1-006 (WebView feasibility) is
  deferred to v1.1 by ADR-019 and is not a v1 gate. The Wayland accessibility gap is an
  accepted v1 limitation (Deferred limitations D1), not a blocker; no screen-reader test was
  performed. The `p1-guard-protect` follow-up is hardening, not an oracle gate item.
  **Phase 2 must not start until the captain approves this READY verdict.**

## Evidence

| Item | State | Merged | Evidence | Still missing |
| --- | --- | --- | --- | --- |
| Windows oracle build + v10 NUnit (p1-001) | pass-with-exceptions, unchanged | `main` | `legacy/source/Playnite.slnx` builds on `windows-latest`; VSTest ran **281 executed, 281 passed, 0 failed, 0 skipped** with 3 documented exclusions; TRX/binlog and counts in the `legacy-oracle` artifact; `tests/baseline.json` records the executed count | Only EX-1, EX-2 and EX-3 remain; EX-3's quarantine and the 281/3 baseline are captain-authorised and recorded in `tests/legacy-exclusions.md` |
| Oracle harness settings/library (p1-002) | done | PR #5 (`082d25b`), PR #19 (`f958bcc`) and PR #22 (`9cbdc8d`) | Harness, dedicated Windows workflow and 21 protected golden payloads plus `settings-library.sha256` and `settings-library.md` are on `main`; generation run 43653446375 and validate run 43653802671 reproduced the emitted 21 payloads byte-for-byte (21/21); PR #22 adds the real-corpus one-byte negative and protects the harness paths; settings-library-oracle runs 43656640766 and 43657110640 are green (63-check Windows self-test and 21-payload validate) | Nothing for the unit; the committed-corpus one-byte criterion is satisfied and recorded in the checklist below |
| Fixture v10 library (p1-003) | done | PR #13 (`1a47c7f`) | 18 approved fixture payloads and pinned `tests/fixtures/v10/SHA256SUMS` committed byte-for-byte from Windows run 43650013648 (artifact 14135728206); strict runs 43650322895 and 43650673457 passed two fresh generations, logical compare, one-byte and extra-file rejection, and typed copy-only v10 read-back with source bytes unchanged | Nothing for the capture |
| UI reference index (p1-004) | done, reference-only, non-blocking | PR #17 (`1c17d7f`) | `docs/port/ui-reference/SOURCES.md` indexes official/public Playnite 10.x UI links for all six tracked screens with slide numbers, section anchors, image filenames and YouTube timestamps; no copyrighted frames downloaded or committed; human comparison only, never a pixel gate | None. A local 10.64 capture remains optional and is not a gate |
| Avalonia headless spike (p1-008) | done, with an accepted backend-specific accessibility limitation | PR #4 (`336f0e9`) and PR #24 (`ff1da20`) | Headless spike: 3/3 xunit facts pass with zero skips, three byte-identical Skia renders, managed automation-peer tree, Tab traversal plus Space activation. Real runtime: native Wayland (`Avalonia.Wayland.WindowImpl`, `xwayland=false`) and X11/XWayland (`Avalonia.X11.X11Window`, `xwayland=true`) windows both open at 640x480 with compositor screenshots and matching Tab/Space traversal. AT-SPI: X11 exposes the full tree (names, roles, states, `AccessibleId`s, toggle/click/select actions, editable text read-back); Wayland exposes nothing. The Wayland negative was retested with app-logged `AT_SPI_BUS_ADDRESS`, `ACCESSIBILITY_ENABLED=1`, `GTK_A11Y=atspi`, `NO_AT_BRIDGE=0` and a live `org.a11y.Bus.GetAddress`, and is consistent with `Avalonia.X11.dll` carrying the AT-SPI bridge while `Avalonia.Wayland.dll` carries none; a zenity/GTK 4 native-Wayland control probe was visible through the same bus | Nothing for this gate item. **No end-to-end screen-reader test (for example Orca) was performed**, so screen-reader behaviour is not claimed. For Avalonia 12.1.6, accessible-to-screen-reader behaviour is only observable under X11/XWayland; that is recorded as accepted v1 limitation D1 in `docs/port/00-DECISIONS.md`, not a Phase 1 blocker. A bare Xorg session and generic uinput injection also remain untested |
| Steam process tracking (p1-007) | real native acceptance done, bounded | PR #15 (`c67a770`) | 21 synthetic/owned-process tests pass with zero skips; real native Terraria (105600, no Proton) was launched through Steam and the unmodified tracker observed the real process tree running to stopped; compositor close confirmed exit in 0.283 s with no leftover process; see `spikes/steam-process-tracking/real-acceptance.md` | Nothing for this gate item, but the evidence is single-title and single-host. Proton, Flatpak-hosted, anti-cheat and multi-library cases remain Phase 2 or later coverage |
| Flatpak host probe (p1-010) | real sandbox acceptance done | PR #16 (`6e4635b`) | 26 local/synthetic tests pass with zero skips; a real user-scoped Flatpak ran `flatpak-spawn --host`, returned host PID 435688 and start ticks 199472051, showed the host `pid:[4026531836]` outside the sandbox namespace `pid:[4026533530]`, forwarded host child exit 23, and failed the no-talk-name negative case with `ServiceUnknown`; see `spikes/flatpak-host/REAL-ACCEPTANCE.md` | Nothing for this item. The runtime was EOL `org.freedesktop.Platform/Sdk` 24.08, and real Steam/native/Proton tracking and a supported-runtime run remain outside this bounded probe |
| Steam real-artifact corpus + written spec (p1-009) | done | PR #14 (`ea4d558`), PR #18 (`663e02c`) and PR #26 (`2045697`) | The written spec is merged in `docs/port/08-STEAM-SPEC.md`; the corrected proposal lists 11 exact protected paths with hashes, redactions and a narrow validator record. All 11 paths are committed under `tests/fixtures/steam/real/` (10 payloads plus `SHA256SUMS`); `sha256sum -c SHA256SUMS` verifies 10/10 payloads, and the `SHA256SUMS` digest `a959d618d04597deda9ca126fb15821c04113dd967ab24b4e4f794d9a338cf8e` matches the proposal. The captain approved the real variant, keeping public app ids and game names with personal data redacted; 105-check corpus validation and a clean residual scan pass on the committed bytes | Nothing; the corpus is committed and verified |
| LiteDB 4.1.4 read/write spike (p1-005) | done | `main` | `spikes/litedb-net11`; read/write passed on `net11.0` | Nothing for this spike; v10 mapper/collection verification remains part of p1-003 |
| WebView feasibility (p1-006) | deferred to v1.1, not a v1 gate | — | ADR-003 and ADR-019 defer WebView and the WebKitGTK/WPE engine to v1.1 and make this a bounded feasibility study; v1 has no browser-engine dependency and no v1 code path consumes `IWebView` | Nothing for v1. The study remains a v1.1 exercise; the p1-006 shard records `deferred` so no Phase 1 row is silently open |
| Protected oracle paths guarded | done for the oracle roots and the harness paths | PR #22 (`9cbdc8d`); `main` | `.github/CODEOWNERS` plus the oracle, fixture, parity and formal workflows; protected-path changes require an `oracle-change` PR. `PROTECTED_PATHS` covers `tests/golden`, `tests/fixtures`, `tests/baseline.json`, `docs/formal`, `docs/port/surface-parity.tsv`, and now `tools/LegacyOracle` and `tools/V10LibraryFixture`; `legacy/` and `oracle-lock/` are unconditionally immutable | CODEOWNERS only enforces when branch protection requires code-owner review. The guard workflow, `.github/CODEOWNERS` itself and `scripts/port/build_port_index.py` are still outside `PROTECTED_PATHS`; that is the separate `p1-guard-protect` hardening follow-up, not an oracle gate item |
| Port index shards + generator (p1-011) | done, hardened | PR #9 (`f886749`), PR #20 (`6259c1a`) and PR #21 (`e9e0429`) | `docs/port/units/` plus `scripts/port/build_port_index.py`; shard-only PR validation with `--forbid-aggregate-changes`, parent-owned aggregate regeneration, and `queue.tsv`/`ledger.tsv` generated only by that step. #21 makes the writer push-only on `refs/heads/main` (no manual dispatch), validates shard tree and history against the previous `main` commit before `build`, and retries once without force on a non-fast-forward rejection; the `[skip port-index]` escape is gone and covered by a generator test | Nothing for the unit. The generator script itself is still outside `PROTECTED_PATHS`, tracked by `p1-guard-protect` |

Supporting checks on the committed tree:

- `tests/baseline.json` records 281 executed, 281 passed, 0 failed, 0 skipped and the three
  exclusions; `tests/legacy-exclusions.md` explains all three. EX-3's quarantine and the
  resulting 281 executed / 3 excluded baseline are a recorded captain authorisation, not a
  silently weakened rule. EX-3 run evidence is run 43649562478.
- `tests/golden/` now contains the two v10 manifest goldens plus the 21 settings/library
  payloads, `settings-library.sha256` and `settings-library.md`. Validation run 43653802671
  reported `PASS: exact 21 golden payloads and path/hash manifest; settings roundtrip/repeat;
  raw library bytes; source preserved.` PR #22 added the real-corpus one-byte negative:
  `Test-Validation.ps1` assembles a disposable emitted set from the committed 21 payloads,
  requires it to pass the same `Assert-Goldens` comparator, flips exactly one byte in one
  copied payload, requires rejection, then proves `tests/golden` is byte-identical. No protected
  payload byte changed.
- `tests/fixtures/v10/` contains the 18 captured library payloads and `SHA256SUMS`. The strict
  committed-fixture comparison and typed v10 read-back ran against the committed bytes in runs
  43650322895 and 43650673457; no source byte changed.
- `tests/fixtures/steam/real/` exists with 11 tracked files. `sha256sum -c SHA256SUMS`
  verifies all 10 listed payloads, and `SHA256SUMS` hashes to
  `a959d618d04597deda9ca126fb15821c04113dd967ab24b4e4f794d9a338cf8e`, the digest the merged
  proposal pinned. Merge runs 43662983092 (port-index) and 43662983105 (plan-guards) are green.
  This was the last open Phase 1 item.
- `spikes/avalonia-headless/REAL-RUNTIME.md` records the real Wayland and X11/XWayland launches,
  the X11 AT-SPI tree and actions, the Wayland AT-SPI negative with its accessibility
  environment, and the zenity control probe. `Avalonia.X11.dll` contains the AT-SPI bridge;
  `Avalonia.Wayland.dll` contains no AT-SPI symbols. It also records that a real screen reader
  (Orca), a bare Xorg session and generic uinput injection were not tested; the limitation
  record D1 repeats that.
- `spikes/steam-process-tracking/real-acceptance.md` records one native title on one host, not a
  general Proton or Flatpak claim.
- `spikes/flatpak-host/REAL-ACCEPTANCE.md` records one EOL runtime and a probe permission, not
  production packaging or Steam tracking inside Flatpak.
- PR #21 (`e9e0429`) is merged. `.github/workflows/port-index.yml` runs only from a push to
  `refs/heads/main`, refuses an empty/zero base, runs
  `check --shards-only --against "$BASE_SHA"` before `build`, and on a rejected push retries once
  against the new `main` without force; it fails visibly and leaves `main` unchanged. There is no
  manual-dispatch trigger and no `[skip port-index]` marker. Main runs 43656817650 (port-index)
  and 43656817607 (plan-guards) are green.
- PR #22 (`9cbdc8d`) is merged. `.github/CODEOWNERS` names `tools/LegacyOracle/` and
  `tools/V10LibraryFixture/`, and `plan-guards.yml` includes both in `PROTECTED_PATHS`; main
  runs 43660863563 (port-index) and 43660863485 (plan-guards) are green. Caveat: CODEOWNERS
  enforcement depends on branch protection requiring code-owner review, and
  `p1-guard-protect` will extend the guard to the workflow, CODEOWNERS and generator files that
  protect other paths.
- PR #24 (`ff1da20`) is merged. Main runs 43660917613 (port-index) and 43660917606
  (plan-guards) are green.

## Exceptions

| id | reason | owner | expiry |
| --- | --- | --- | --- |
| EX-1 | `FileSystemTests.CanWriteToFolderTest` assumes a non-admin Windows user; the runner is admin. | oracle | Revisit when the oracle runs as a standard user |
| EX-2 | `SafeFileEnumeratorTests.OverUnsafeWorksTest` assumes a non-admin Windows user; the runner is admin. | oracle | Revisit when the oracle runs as a standard user |
| EX-3 | `ProcessStarterTests.ShellExecuteTest` is a Windows `notepad`/`taskkill` shell test whose `taskkill` returned 1 instead of 0. It passed in the 282/282 baseline, so this is environment flakiness, not a missing check or oracle-setup gap. The captain authorised quarantining it with an accepted baseline of **281 executed / 3 excluded**. | oracle | Revisit when the Windows runner's `notepad`/`taskkill` shell behaviour is stable |

EX-1 and EX-2 are Windows ACL tests, not portable app logic. EX-3 is a flaky Windows shell
test whose quarantine is a recorded captain decision. All three are excluded by a narrow VSTest
filter naming that single test, not by editing `legacy/`; the 281/3 baseline is therefore an
authorised count, not a weakened test rule. No new exception is claimed for any pending item:
pending evidence is missing, not waived.

## Still required for Phase 1 exit

The checklist below and the Phase 2 entry together are the exhaustive Phase 1 gate. Do not
close the listed items while leaving a checklist item unchecked.

- [x] Old app builds and tests run on Windows CI (281/281 passed; 3 documented exclusions,
      including the captain-authorised EX-3 quarantine).
- [x] Executed cases recorded from a TRX file (`tests/baseline.json`).
- [x] Oracle harness constrained to v10 APIs; the two manifest v0 goldens are committed and
      byte-compared with freshly regenerated output by the Windows workflow.
- [x] Settings/library harness produces its protected golden output: 21 payloads,
      `settings-library.sha256` and `settings-library.md` committed in #19; validate run
      43653802671 reproduced all 21 payloads byte-for-byte.
- [x] Fixture v10 library captured and verified: 18 approved protected payloads with pinned
      `SHA256SUMS`, strict committed-fixture comparison and typed Windows read-back.
- [x] UI references satisfied by curated official/public links (`docs/port/ui-reference/SOURCES.md`);
      reference-only, no copyrighted frames committed; optional local capture is not a Phase 1 gate.
- [x] Written Steam spec merged (#14), the corpus proposal corrected after review (#18), and the
      11 approved protected paths committed in #26 with a verifiable `SHA256SUMS`.
- [x] LiteDB spike result recorded (passed).
- [x] The listed protected roots (`legacy/`, `tests/golden/`, `tests/fixtures/`,
      `tests/baseline.json`, `docs/formal/`, `oracle-lock/`, `docs/port/surface-parity.tsv`)
      are guarded by CODEOWNERS and CI.
- [x] Oracle harness path protection extension merged in #22: `tools/LegacyOracle` and
      `tools/V10LibraryFixture` are covered by CODEOWNERS and the protected-path check. (The
      guard workflow, CODEOWNERS itself and the port-index generator remain for
      `p1-guard-protect`; that is follow-up hardening, not an oracle gate item.)
- [x] Port queue/ledger generated from unit shards; shard edit is the only edit path.
- [x] Port-index workflow hardening merged in #21: writes to `main` only from a push event on
      `refs/heads/main`, validates shard tree and history before `build`, handles a
      non-fast-forward without force, and has no user-controlled skip marker.
- [x] Steam process tracking accepted on a real native game (Terraria, single title and host).
- [x] Flatpak host launch/tracking accepted with real `flatpak-spawn --host` (one user-scoped
      probe on an EOL runtime).
- [x] Avalonia real Wayland/X11 launch and native AT-SPI evidence merged in #24: a real window
      opens on both stacks; X11/XWayland exposes the full AT-SPI tree; Wayland exposes no
      accessibility tree for Avalonia 12.1.6, confirmed with the accessibility environment, a
      live-bus check and matching binary symbols. No end-to-end screen-reader test (for example
      Orca) was performed, so screen-reader behaviour is not claimed. Accessible-to-screen-reader
      behaviour is only observable under X11/XWayland and is recorded as accepted v1 limitation
      D1, not a Phase 1 blocker.
- [x] WebView feasibility (p1-006) recorded as deferred to v1.1 by ADR-019 and explicitly not a
      v1 gate, so no Phase 1 row is left silently open.
- [x] Manifest golden corpus reproducibility: the Windows workflow emits both v0 manifests into a
      temporary directory and byte-compares them with the committed `tests/golden/manifests/`
      files; a one-byte change in a disposable copy of a committed manifest is rejected by the
      same comparator.
- [x] Settings/library golden corpus one-byte criterion merged in #22: the committed 21-payload
      corpus passes the `Assert-Goldens` comparator, a one-byte mutation of a disposable copy is
      rejected, and `tests/golden` is proven unchanged.

## Phase 2 entry

**Phase 2 may start once the captain approves this READY verdict.** No Phase 1 gate item
remains open.

Deferred or bounded evidence, not gate blockers:

- **Wayland accessibility** is an accepted v1 limitation (D1 in `00-DECISIONS.md`). For
  Avalonia 12.1.6, AT-SPI is only observable under X11/XWayland, and no end-to-end screen-reader
  test (for example Orca) was performed. Accessibility is not a v1 requirement.
- **WebView (p1-006)** is deferred to v1.1 by ADR-019; v1 ships with no browser-engine
  dependency.
- **`p1-guard-protect`** is hardening: extend the protected-path check to the guard workflow,
  `.github/CODEOWNERS` itself and the port-index generator. It is not an oracle gate item.
- **Bounded evidence**: the Steam acceptance is single-title/single-host; the Flatpak probe used
  an EOL runtime; the X11 accessibility evidence is XWayland, not bare Xorg; keyboard injection
  on Wayland used a compositor-specific path. These are stated limits, not hidden claims.

UI references (p1-004) are **advisory only**, not a remaining item:
`docs/port/ui-reference/SOURCES.md` is human comparison material, never a pixel gate and
never an equivalence claim. A local 10.64 capture remains an optional later exercise.

**Verdict: READY** — every named Phase 1 deliverable is merged and verified on the committed
tree, including the 11-file `tests/fixtures/steam/real/` corpus with a verifiable `SHA256SUMS`;
p1-006 is explicitly deferred; accessibility is an accepted v1 limitation. The checklist above
and this verdict together are the Phase 1 gate. Phase 2 must not start before captain approval.

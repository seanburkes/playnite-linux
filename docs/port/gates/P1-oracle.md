# Gate P1 — Windows oracle baseline

- Date: 2026-11-14
- Owner: oracle
- Reviewer: independent plan review (session `ses_e27629994ffec0IK0wD5lgcnOT`); gate evidence pending captain review
- Runs: v10 suite https://github.com/seanburkes/playnite-linux/actions/runs/43649562478;
  settings/library generation https://github.com/seanburkes/playnite-linux/actions/runs/43653446375
  and validation https://github.com/seanburkes/playnite-linux/actions/runs/43653802671;
  strict fixture verification https://github.com/seanburkes/playnite-linux/actions/runs/43650322895
  and https://github.com/seanburkes/playnite-linux/actions/runs/43650673457
- Result: **NOT READY** — the Windows oracle, settings/library goldens, committed v10 fixture,
  reference-only UI links, bounded real Steam acceptance and bounded real Flatpak acceptance are
  merged. Four items remain open: the real Steam corpus payloads need captain approval, real
  X11/Wayland and native AT-SPI behaviour is not proven, the committed settings/library corpus
  still lacks a real one-byte negative, and the harness-path and port-index hardening changes are
  in flight. Phase 2 must not start.

## Evidence

| Item | State | Merged | Evidence | Still missing |
| --- | --- | --- | --- | --- |
| Windows oracle build + v10 NUnit (p1-001) | pass-with-exceptions, unchanged | `main` | `legacy/source/Playnite.slnx` builds on `windows-latest`; VSTest ran **281 executed, 281 passed, 0 failed, 0 skipped** with 3 documented exclusions; TRX/binlog and counts in the `legacy-oracle` artifact; `tests/baseline.json` records the executed count | Only EX-1, EX-2 and EX-3 remain; no other gap |
| Oracle harness settings/library (p1-002) | done | PR #5 (`082d25b`) and PR #19 (`f958bcc`) | Harness, dedicated Windows workflow and 21 protected golden payloads plus `settings-library.sha256` and `settings-library.md` are on `main`; generation run 43653446375 and `validate` run 43653802671 passed, reproducing the emitted 21 payloads byte-for-byte (21/21) and preserving fixture/golden sources | Nothing for the unit. The comparator still lacks a negative over the committed settings/library bytes; that gate item is tracked separately below |
| Fixture v10 library (p1-003) | done | PR #13 (`1a47c7f`) | 18 approved fixture payloads and pinned `tests/fixtures/v10/SHA256SUMS` committed byte-for-byte from Windows run 43650013648 (artifact 14135728206); strict runs 43650322895 and 43650673457 passed two fresh generations, logical compare, one-byte and extra-file rejection, and typed copy-only v10 read-back with source bytes unchanged | Nothing for the capture |
| UI reference index (p1-004) | done, reference-only, non-blocking | PR #17 (`1c17d7f`) | `docs/port/ui-reference/SOURCES.md` indexes official/public Playnite 10.x UI links for all six tracked screens with slide numbers, section anchors, image filenames and YouTube timestamps; no copyrighted frames downloaded or committed; human comparison only, never a pixel gate | None. A local 10.64 capture remains optional and is not a gate |
| Avalonia headless spike (p1-008) | in-progress (headless only) | PR #4 (`336f0e9`) | 3/3 xunit facts pass with zero skips: three byte-identical headless Skia renders, a managed automation-peer tree, and Tab traversal plus Space activation | Real X11/Wayland launch and native AT-SPI/screen-reader behaviour are not proven; a task is running with the host environment (Wayland session, `DISPLAY=:0`, `at-spi2-core`, `xdotool`). The automation tree is Avalonia's managed model, not an accessibility bridge |
| Steam process tracking (p1-007) | real native acceptance done, bounded | PR #15 (`c67a770`) | 21 synthetic/owned-process tests pass with zero skips; real native Terraria (105600, no Proton) was launched through Steam and the unmodified tracker observed the real process tree running to stopped; compositor close confirmed exit in 0.283 s with no leftover process; see `spikes/steam-process-tracking/real-acceptance.md` | Nothing for this gate item, but the evidence is single-title and single-host. Proton, Flatpak-hosted, anti-cheat and multi-library cases remain Phase 2 or later coverage |
| Flatpak host probe (p1-010) | real sandbox acceptance done | PR #16 (`6e4635b`) | 26 local/synthetic tests pass with zero skips; a real user-scoped Flatpak ran `flatpak-spawn --host`, returned host PID 435688 and start ticks 199472051, showed the host `pid:[4026531836]` outside the sandbox namespace `pid:[4026533530]`, forwarded host child exit 23, and failed the no-talk-name negative case with `ServiceUnknown`; see `spikes/flatpak-host/REAL-ACCEPTANCE.md` | Nothing for this item. The runtime was EOL `org.freedesktop.Platform/Sdk` 24.08, and real Steam/native/Proton tracking and a supported-runtime run remain outside this bounded probe |
| Steam real-artifact corpus + written spec (p1-009) | proposal merged, corpus not committed | PR #14 (`ea4d558`) and PR #18 (`663e02c`) | The written spec is merged in `docs/port/08-STEAM-SPEC.md`; the corrected proposal lists 11 exact protected paths with hashes, redactions and a narrow validator record, and is staged outside the checkout | The 11 proposed `tests/fixtures/steam/real/` payloads are not committed and await captain approval. The captain must also decide whether the corpus keeps the real installed-library identities or replaces game ids/names |
| LiteDB 4.1.4 read/write spike (p1-005) | done | `main` | `spikes/litedb-net11`; read/write passed on `net11.0` | Nothing for this spike; v10 mapper/collection verification remains part of p1-003 |
| Protected oracle paths guarded | done for the listed roots | `main` | `.github/CODEOWNERS` plus the oracle, fixture, parity and formal workflows; protected-path changes require an `oracle-change` PR | Harness-path protection is being extended in a follow-up PR; see the remaining item below |
| Port index shards + generator (p1-011) | done, workflow hardening pending | PR #20 (`6259c1a`) | `docs/port/units/` plus `scripts/port/build_port_index.py`; shard-only PR validation and parent-owned aggregate regeneration on `main`; `queue.tsv`/`ledger.tsv` are generated and cannot be hand-edited | Post-merge review findings remain: a manual dispatch could still push `main`, the bot ran `build` without first validating shard history, and a `[skip port-index]` marker existed. These are being fixed in open PR #21 (`https://github.com/seanburkes/playnite-linux/pull/21`); `main` is not considered hardened until it lands |

Supporting checks on the committed tree:

- `tests/baseline.json` records 281 executed, 281 passed, 0 failed, 0 skipped and the three
  exclusions; `tests/legacy-exclusions.md` explains all three. EX-3 run evidence is run
  43649562478.
- `tests/golden/` now contains the two v10 manifest goldens plus the 21 settings/library
  payloads, `settings-library.sha256` and `settings-library.md`. Validation run 43653802671
  reported `PASS: exact 21 golden payloads and path/hash manifest; settings roundtrip/repeat;
  raw library bytes; source preserved.`
- `tests/fixtures/v10/` contains the 18 captured library payloads and `SHA256SUMS`. The strict
  committed-fixture comparison and typed v10 read-back ran against the committed bytes in runs
  43650322895 and 43650673457; no source byte changed.
- `tests/fixtures/steam/real/` does **not** exist. The corrected p1-009 proposal is the only
  repository change; the 11 proposed payload files remain staged outside the checkout.
- `spikes/avalonia-headless/` proves the headless Skia render and managed automation-peer model
  only. No real X11/Wayland launch or native AT-SPI observation is committed.
- `spikes/steam-process-tracking/real-acceptance.md` records one native title on one host, not a
  general Proton or Flatpak claim.
- `spikes/flatpak-host/REAL-ACCEPTANCE.md` records one EOL runtime and a probe permission, not
  production packaging or Steam tracking inside Flatpak.
- Port-index PR #20 merged at its pre-fix head (`3872ae3`); the review-fix diff is open PR #21.
  Until #21 merges, main still has the dispatch-capable workflow, the build-without-history-check
  step, and the skip marker.
- The manifest corpus has a working one-byte negative. The settings/library comparator's
  one-byte, missing, extra, renamed, duplicate and malformed negatives run on synthetic unit
  bytes, not on the committed settings/library payloads; a real-corpus negative is in flight.

## Exceptions

| id | reason | owner | expiry |
| --- | --- | --- | --- |
| EX-1 | `FileSystemTests.CanWriteToFolderTest` assumes a non-admin Windows user; the runner is admin. | oracle | Revisit when the oracle runs as a standard user |
| EX-2 | `SafeFileEnumeratorTests.OverUnsafeWorksTest` assumes a non-admin Windows user; the runner is admin. | oracle | Revisit when the oracle runs as a standard user |
| EX-3 | `ProcessStarterTests.ShellExecuteTest` is a Windows `notepad`/`taskkill` shell test whose `taskkill` returned 1 instead of 0. It passed in the 282/282 baseline, so this is environment flakiness, not a missing check or oracle-setup gap. | oracle | Revisit when the Windows runner's `notepad`/`taskkill` shell behaviour is stable |

EX-1 and EX-2 are Windows ACL tests, not portable app logic. EX-3 is a flaky Windows shell
test. All three are excluded by a narrow VSTest filter naming that single test, not by
editing `legacy/`; see `tests/legacy-exclusions.md`. No new exception is claimed for any
pending item: pending evidence is missing, not waived.

## Still required for Phase 1 exit

The checklist below and the numbered remaining-items list in the Phase 2 entry together are the
exhaustive Phase 1 gate. Do not close the listed items while leaving a checklist item unchecked.

- [x] Old app builds and tests run on Windows CI (281/281 passed; 3 documented exclusions).
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
- [x] Written Steam spec merged (#14) and the corpus proposal corrected after review (#18).
- [ ] Steam real-artifact corpus committed: the 11 proposed protected paths need captain
      approval, and the captain must decide whether real installed-library identities stay or
      game ids/names are replaced.
- [x] LiteDB spike result recorded (passed).
- [x] The listed protected roots (`legacy/`, `tests/golden/`, `tests/fixtures/`,
      `tests/baseline.json`, `docs/formal/`, `oracle-lock/`, `docs/port/surface-parity.tsv`)
      are guarded by CODEOWNERS and CI.
- [ ] Oracle harness path protection extension is in flight; harness paths are not yet covered by
      the protected-path gate.
- [x] Port queue/ledger generated from unit shards; shard edit is the only edit path.
- [ ] Port-index workflow hardening is in flight (#21): no dispatch can write `main`, the bot
      validates shard history before building, and the skip marker is gone. `main` is not
      hardened until it lands.
- [x] Steam process tracking accepted on a real native game (Terraria, single title and host).
- [x] Flatpak host launch/tracking accepted with real `flatpak-spawn --host` (one user-scoped
      probe on an EOL runtime).
- [ ] Avalonia real X11/Wayland launch and native AT-SPI/screen-reader evidence.
- [x] Manifest golden corpus reproducibility: the Windows workflow emits both v0 manifests into a
      temporary directory and byte-compares them with the committed `tests/golden/manifests/`
      files; a one-byte change in a disposable copy of a committed manifest is rejected by the
      same comparator.
- [ ] Settings/library golden corpus one-byte criterion: the committed 21-payload corpus has no
      real one-byte negative yet. The comparator's existing negatives run on synthetic unit
      bytes, not on the committed settings/library payloads. A follow-up PR is adding the
      real-corpus negative; until it lands this criterion is not satisfied.

## Phase 2 entry

**Phase 2 must not start until the remaining Phase 1 items are closed.** The Phase 1 gate is
**NOT READY**; no Phase 2 queue unit is released. Remaining items:

1. **p1-009** — the corrected corpus proposal is merged, but the 11 proposed protected payload
   paths under `tests/fixtures/steam/real/` are not committed and await captain approval. The
   captain must also decide whether the corpus keeps the real installed-library identities or
   replaces game ids/names. Approval and the resulting protected commit are not done.
2. **p1-008** — the merged spike has headless evidence only. Real Wayland/X11 launch and native
   AT-SPI exposure are not yet proven; a task is running with the host environment (Wayland
   session, `DISPLAY=:0`, `at-spi2-core`, `xdotool`), and its evidence is not merged.
3. **Oracle one-byte guarantee on the committed corpus** — the manifest corpus has a real
   one-byte negative, but the committed settings/library corpus does not. Existing comparator
   negatives run against synthetic bytes; a follow-up PR is adding a real-corpus negative.
   Until it lands, do not mark the one-byte criterion satisfied.
4. **Oracle harness path protection and port-index workflow hardening** — both are in flight.
   The harness-path protection change is not merged. The port-index fixes are carried by open
   PR #21 (`https://github.com/seanburkes/playnite-linux/pull/21`): the merged #20 head could
   still push `main` from a manual dispatch, the bot lacked history validation before `build`,
   and a skip marker existed. `main` is not considered hardened until #21 lands.

UI references (p1-004) are **advisory only**, not a remaining item:
`docs/port/ui-reference/SOURCES.md` is human comparison material, never a pixel gate and
never an equivalence claim. A local 10.64 capture remains an optional later exercise.

The checklist under “Still required for Phase 1 exit” and this numbered list together are the
exhaustive Phase 1 gate. Do not close these items while leaving a checklist item unchecked,
and do not treat either list alone as the complete gate.

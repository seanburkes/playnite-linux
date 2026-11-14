# Gate P1 — Windows oracle baseline

- Date: 2026-11-14
- Owner: oracle
- Reviewer: independent plan review (session `ses_e27629994ffec0IK0wD5lgcnOT`); gate evidence pending captain review
- Runs: legacy oracle https://github.com/seanburkes/playnite-linux/actions/runs/43604176690;
  manifest comparison https://github.com/seanburkes/playnite-linux/actions/runs/43645705588 (green)
  and https://github.com/seanburkes/playnite-linux/actions/runs/43647994673 (attempt 3 green;
  attempts 1-2 flaked on unrelated legacy tests);
  settings/library https://github.com/seanburkes/playnite-linux/actions/runs/43627447663;
  fixture https://github.com/seanburkes/playnite-linux/actions/runs/43629325359
- Result: **NOT READY** — the oracle build and v10 suite are green, but the settings/library
  goldens, the fixture capture, the UI references and the Steam corpus/spec are not produced,
  and the spikes have no real-environment acceptance.

## Evidence

| Item | State | Merged | Evidence | Still missing |
| --- | --- | --- | --- | --- |
| Windows oracle build + v10 NUnit (p1-001) | pass-with-exceptions, unchanged | `main` | `legacy/source/Playnite.slnx` builds on `windows-latest`; VSTest ran **281 executed, 281 passed, 0 failed, 0 skipped** with 3 documented exclusions; TRX/binlog and counts in the `legacy-oracle` artifact; `tests/baseline.json` records the executed count | Only EX-1, EX-2 and EX-3 remain; no other gap |
| Oracle harness settings/library (p1-002) | merged-with-goldens-pending | PR #5 (`082d25b`) | `tools/LegacyOracle` settings-bytes and library-export commands plus the dedicated Windows workflow; run 43627447663 green (generation plus 59-check synthetic self-test); `Validate` records acceptance pending when protected data is absent | No protected settings/library goldens exist. The golden generation run requires the approved p1-003 fixture, so its golden output and protected-data validation are not produced (21 payloads plus `settings-library.sha256`/`settings-library.md`). |
| Fixture v10 library (p1-003) | pending | PR #6 (`edcbe71`) | `tools/V10LibraryFixture` generator, validator, pre-write isolation guard, disposable-tree tests and narrow Windows workflow; run 43629325359 green (two generations, logical compare, mutation/extra-file rejection, copy-only v10 read-back); two independent oracle-change reviews approved the exact 19 new protected fixture paths | The capture itself does not exist: no `tests/fixtures/v10/`, no pinned `SHA256SUMS`, no typed Windows read-back of committed bytes, and no strict committed-fixture comparison. Approval of the paths is not a capture. |
| UI reference images (p1-004) | pending | PR #1 (`b53093e`) | Capture instructions and provenance checklist in `docs/port/ui-reference/README.md`, including the capture audit and frozen-build requirements | All three genuine Windows captures (`main-library.png`, `details.png`, `settings.png`) and `provenance.md` are missing. Authorised interactive Windows capture access and a runnable frozen 10.64 build are not available. Human only; not a pixel gate. |
| Avalonia headless spike (p1-008) | merged-with-real-runtime-pending | PR #4 (`336f0e9`) | 3/3 xunit facts pass with zero skips: three byte-identical headless Skia renders, managed automation-peer tree, and Tab traversal plus Space activation | Real X11/Wayland window behaviour and native AT-SPI/screen-reader behaviour are not proven; the automation tree is Avalonia's managed model, not an accessibility bridge. |
| Steam process tracking (p1-007) | merged-with-real-acceptance-pending | PR #3 (`9396d45`) | 21 synthetic/owned-process tests pass with zero skips; deadline ordering P2 reproduced and corrected; identity, reparenting, PID reuse and cancellation semantics covered | Real native/Proton game acceptance has not run; p1-003's fixture remains incomplete. |
| Flatpak host probe (p1-010) | merged-with-real-acceptance-pending | PR #2 (`96e98df`) | 26 local/synthetic tests pass with zero skips; controlled local child visibility and exit 23 observed; fragmented records bounded by total bytes and one deadline | No real `flatpak-spawn --host` acceptance: this worker has neither `flatpak` nor `flatpak-spawn` and is not in a sandbox; real Steam/native/Proton tracking is unrun. |
| Steam real-artifact corpus + written spec (p1-009) | not-started | — | None. `tests/fixtures/steam/` contains only synthetic VDF/ACF fixtures | Redacted real Steam artifact corpus and the reviewed written Steam spec (ADR-017) |
| LiteDB 4.1.4 read/write spike (p1-005) | done | `main` | `spikes/litedb-net11`; read/write passed on `net11.0` | Nothing for this spike; v10 mapper/collection verification remains part of p1-003 |
| Protected oracle paths guarded | done | `main` | `.github/CODEOWNERS` plus the oracle, fixture, parity and formal workflows; protected-path changes require an `oracle-change` PR | Nothing |
| Port index shards + generator (p1-011) | done | PR #9 (`f886749`) | `docs/port/units/` plus `scripts/port/build_port_index.py`; two fix rounds and 47 generator tests; queue/ledger are now generated and cannot be hand-edited; migration moved eight append-order rows into wave-then-id order with row content unchanged, and the ledger migration is byte-identical | No Phase 1 acceptance gap. Residual documentation follow-ups from the final review: qualify the README history wording so a RECORDED retirement can still remove shards/rows; scope the absolute prefix statements to units not retired in the change; describe `approved-by` as a human-review label, not machine authentication. |

Supporting checks on the committed tree:

- `tests/baseline.json` records 281 executed, 281 passed, 0 failed, 0 skipped and the three
  exclusions; `tests/legacy-exclusions.md` explains all three.
- Manifest reproducibility is proven by the workflow, not just emitted: run 43646522483
  (attempt 2) ran the byte comparison and logged `Manifest goldens match committed bytes:
  2 file(s)` and rejection of the one-byte copy, then failed only in the unrelated legacy
  `ShellExecuteTest` (281/282); attempt 1 of that rerun flaked on the network tests
  `WebTest`, `AddFileTest` and `GetResponseCodeTest`. The same commit was fully green,
  including the comparison and one-byte negative, in run 43645705588. After the harness-trigger
  change, head 70973d5 run 43647994673 was green on attempt 3 (attempts 1-2 flaked: first the
  three network tests above, then `ShellExecuteTest`). The recurrent `ShellExecuteTest` flake
  is now recorded as EX-3.
- `tests/golden/` contains only the two v10 manifest goldens; no
  `settings-library.sha256`, `settings-library.md`, `settings/` or `library/` corpus exists.
- `tests/fixtures/v10/` does not exist. Only the synthetic Steam fixtures under
  `tests/fixtures/steam/` are present.
- `tools/LegacyOracle` and `tools/V10LibraryFixture` are merged with their Windows workflows.
  Both workflows explicitly record acceptance as pending when the approved fixture and
  protected goldens are absent; neither claims success.
- `docs/port/ui-reference/` contains the procedure only; all three images and `provenance.md`
  are absent.

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
- [ ] Settings/library harness produces its protected golden output (merged tooling only; the
      approved p1-003 fixture is required first).
- [ ] Fixture v10 library captured and verified: the 19 approved protected paths, pinned
      `SHA256SUMS`, strict committed-fixture comparison and typed Windows read-back.
- [ ] UI reference images captured (human only; 0 of 3 images plus provenance).
- [ ] Steam real-artifact corpus captured/redacted and the written Steam spec reviewed.
- [x] LiteDB spike result recorded (passed).
- [x] All protected paths guarded (CODEOWNERS and CI in place).
- [x] Port queue/ledger generated from unit shards; shard edit is the only edit path.
- [ ] Steam process tracking accepted on a real native/Proton game.
- [ ] Flatpak host launch/tracking accepted with real `flatpak-spawn --host`.
- [ ] Avalonia real X11/Wayland launch and native AT-SPI/screen-reader evidence.
- [x] Manifest golden corpus reproducibility: the Windows workflow emits both v0 manifests into a
      temporary directory and byte-compares them with the committed `tests/golden/manifests/`
      files; a one-byte change in a disposable copy of a committed manifest is rejected by the
      same comparator.
- [ ] Settings/library golden corpus one-byte criterion: that corpus does not exist yet, so it
      has no comparator to bite. Once committed, a deliberate one-byte change in its protected
      bytes must fail its comparison. The manifest corpus above is a separate, existing corpus.

## Phase 2 entry

**Phase 2 must not start until the remaining Phase 1 items are closed.** The Phase 1 gate is
**NOT READY**; no Phase 2 queue unit is released. Remaining items:

1. **p1-002** — generate and commit the protected settings/library goldens from the approved
   fixture, with the exact 21 payloads, `settings-library.sha256` and `settings-library.md`
   reviewed.
2. **p1-003** — produce the actual v10 fixture capture through the merged tooling, commit the
   19 approved protected paths with pinned `SHA256SUMS`, then pass the strict
   committed-fixture comparison and typed Windows read-back. Path approval is not the capture:
   the capture PR must post-write review the actual committed protected bytes.
3. **p1-004** — capture the three genuine Windows UI reference images and provenance
   (human only).
4. **p1-009** — capture/redact the real Steam artifact corpus and review the written Steam
   spec.
5. **p1-007** — run real native/Proton game acceptance.
6. **p1-010** — run real `flatpak-spawn --host` launch/tracking acceptance.
7. **p1-008** — prove real X11/Wayland launch and native AT-SPI/screen-reader behaviour.
8. Keep EX-1, EX-2 and EX-3 recorded until the oracle can run as a standard Windows user and the
   shell test is stable.
9. **Settings/library one-byte criterion** — once the p1-002 protected corpus exists, prove that
   a deliberate one-byte change in its protected bytes fails its comparator. The manifest
   corpus is already byte-compared with its own one-byte negative; that does not cover the
   settings/library corpus, which does not exist yet.

The checklist under “Still required for Phase 1 exit” and this numbered list together are the
exhaustive Phase 1 gate. Do not close these items while leaving a checklist item unchecked,
and do not treat either list alone as the complete gate.

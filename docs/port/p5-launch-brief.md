# Phase 5 launch brief — Steam and Linux platform

- Date: 2026-11-18 (second reconciliation; first board was #117)
- Board unit: `p5-shards2`
- Base: `71d6b412b37e2c3b367802b340bfb375e2a5125a` (`main`)
- Scope: records the merged Phase 5 desktop wave and the honest remaining work. This is a
  shard-board record, not a gate acceptance.
- Addendum (follow-up to merged #123): all six delivered rows cite main-push linux-ci runs on
  their merged heads, not pull-request head runs. The criteria statuses did not change.
- Constraint: a pull request owns exactly one unit queue/evidence namespace, so this change
  edits only the `p5-shards2` shards and documentation. The canonical per-unit queue rows
  (`p5-001`..`p5-008`) are **not** rewritten here; the tables below are the board's reconciled
  view of them. Their own namespace close-outs remain the canonical queue transitions, and
  none of those close-outs has landed yet — every merged desktop row still reads
  `in-progress`.

## Delivered

All six rows below are **done** at the named merge commit. The linux-ci citation for each row
is a **main-push run on the merged head**, not a pull-request head run.

| Unit | State | Merged evidence | Merged-head linux-ci | Review / evidence integrity |
| --- | --- | --- | --- | --- |
| p5-001 VDF/ACF parsers | done | [#109](https://github.com/seanburkes/playnite-linux/pull/109) merged at `777578cc81efb0130b41924c282542f0b346836c` | [44190373018](https://github.com/seanburkes/playnite-linux/actions/runs/44190373018) on `777578c`, push success | Bounded pure parsers; no discovery, mount, launch, UI or network behaviour. Recorded by #117. |
| p5-002 Steam root/library discovery | done | [#110](https://github.com/seanburkes/playnite-linux/pull/110) merged at `c7b25dc7fa39238ab8f6fc47b9b21a4237e3ddb0`, plus follow-ups [#112](https://github.com/seanburkes/playnite-linux/pull/112) at `f8aed2fe6b29918fbe6bc29dd5f3b54a7af6fcee`, [#115](https://github.com/seanburkes/playnite-linux/pull/115) at `c057be0f3e548ac31ac515b6da7ad4c606d17916` and [#116](https://github.com/seanburkes/playnite-linux/pull/116) at `a37890f33110c70b8411bac4457d16e0aa41822f` | [44323525396](https://github.com/seanburkes/playnite-linux/actions/runs/44323525396) on `a37890f` (final follow-up merged head), push success; earlier merged-head runs [44200516358](https://github.com/seanburkes/playnite-linux/actions/runs/44200516358), [44203797675](https://github.com/seanburkes/playnite-linux/actions/runs/44203797675), [44244649688](https://github.com/seanburkes/playnite-linux/actions/runs/44244649688) | Not a clean #110 delivery: #110 merged before its four blocking P2 findings were closed; the follow-ups closed them. Recorded by #117. |
| p5-003 artwork/metadata cache | done | [#118](https://github.com/seanburkes/playnite-linux/pull/118) merged at `22f051a92b6b5a82566fd382a04f9848c30ed756` | [44344905055](https://github.com/seanburkes/playnite-linux/actions/runs/44344905055) on `22f051a`, push success | Review A findings, the evidence-only citation correction and the final empty/whitespace expected-hash fix were all folded into the PR **before** the merge; the merged tree contains them. Explicit sources only; Steam CDN mapping and real artwork acquisition remain UNKNOWN/deferred. |
| p5-005a XDG/single-instance/protocol | done | [#119](https://github.com/seanburkes/playnite-linux/pull/119) merged at `785b89200666ab63f07ca2fd1af4e757c3ef1e36` | [44344964418](https://github.com/seanburkes/playnite-linux/actions/runs/44344964418) on `785b892`, push success | Delivers the p5-005 base scope (the canonical `p5-005` row is still `todo`). Review fixes landed pre-merge. The ledger initially carried a **nonexistent** 40-hex head SHA; an evidence-only delta (`18d22b9`) corrected it, and #122 hardened the guard that had skipped unresolvable head candidates. |
| p5-004 launcher/session tracker | done with an open post-merge finding | [#120](https://github.com/seanburkes/playnite-linux/pull/120) merged at `064e261b5bffa6c434515b9851160a21fd0e317d` | [44345002684](https://github.com/seanburkes/playnite-linux/actions/runs/44345002684) on `064e261`, push success | Process-group containment and the C/D review set landed pre-merge. One finding — a child reparented **before** the first snapshot — was committed as `eac06aafeb5d9676fd30715352eb2f601b50f169` at 16:15:55Z, one minute **after** the 16:14:56Z merge, and exists only on the unmerged `fm/p5-004` branch. `main` lacks `Real_child_reparented_before_the_first_snapshot_is_still_contained`. |
| p5-007a tray/notifications/sleep | done | [#121](https://github.com/seanburkes/playnite-linux/pull/121) merged at `60e71ca950a21b72a08165e60c0e8066905ffed3` | [44345049906](https://github.com/seanburkes/playnite-linux/actions/runs/44345049906) on `60e71ca`, push success | Delivers the p5-007 base scope (the canonical `p5-007` row is still `todo`). The cancelled-session lease release and the concurrent-notification/gdbus fixes landed pre-merge (`f0e0344`); the final evidence-only citation (`554a0da`) is not on `main`, but the merged fragment already cites the fixed implementation head and its green run. |

## Remaining

| Unit | State | Depends | Remaining scope |
| --- | --- | --- | --- |
| p5-008 launch native/Proton | todo — the only remaining Phase 5 implementation unit | p5-002, p5-004 | Real Steam launch and session tracking for a native game **and** a Proton game; Flatpak `flatpak-spawn --host` launch and tracking; a real no-orphan proof for that path; and GameSession terminal-state alignment for the real session lifecycle. Requires a real Steam install; no unit of this wave proves it. |

## Deferred

| Unit | State | Decision |
| --- | --- | --- |
| p5-006 WebView service | deferred | ADR-019 defers the WebView service to v1.1; v1 uses `Avalonia.HtmlRenderer` for non-interactive descriptions and plain HTTP for metadata, and the Phase 5 order in `docs/port/03-EXECUTION-PLAN.md` does not include WebView. WebView is not counted as remaining Phase 5 implementation work. |

## Review and evidence integrity — not a uniformly clean wave

- #118, #119, #120 and #121 each required review remediation. For #118, #119 and #121 the
  remediation commits and evidence-only deltas were folded into the PR before the merge, so
  the merged tree contains them.
- #120 is the exception: its last finding was fixed **after** the merge and only on the
  unmerged branch. The merged commit does not prove the reparent-before-first-snapshot case.
- #119's ledger contained a nonexistent head SHA (`9af1b5e…`, an invented suffix) that the
  unit-evidence guard skipped because it ignored unresolvable candidates. The fragment row was
  corrected by an evidence-only delta inside the PR; #122 then changed the guard so every
  cited head must resolve and every cited run's `head_sha` must equal a cited head.
- #122 is a process/tooling row (`p2-evidence2`), not a Phase 5 desktop unit.
- The canonical rows for the merged units remain `in-progress`: merging does not flip a queue
  row, and the one-namespace rule prevents this board PR from doing it. This is a record
  defect, not a claim that the units are unfinished.

## Merge ancestry

Verified with `git merge-base --is-ancestor <merge-commit> 71d6b41` on
`main` at `71d6b412b37e2c3b367802b340bfb375e2a5125a`; every command returned true.
The cited evidence is the merged commit on `main`, not the pull-request head.

| PR | Merge commit | Unit | Ancestor of `71d6b41` |
| --- | --- | --- | --- |
| [#118](https://github.com/seanburkes/playnite-linux/pull/118) | `22f051a92b6b5a82566fd382a04f9848c30ed756` | p5-003 | yes |
| [#119](https://github.com/seanburkes/playnite-linux/pull/119) | `785b89200666ab63f07ca2fd1af4e757c3ef1e36` | p5-005a | yes |
| [#120](https://github.com/seanburkes/playnite-linux/pull/120) | `064e261b5bffa6c434515b9851160a21fd0e317d` | p5-004 | yes |
| [#121](https://github.com/seanburkes/playnite-linux/pull/121) | `60e71ca950a21b72a08165e60c0e8066905ffed3` | p5-007a | yes |
| [#122](https://github.com/seanburkes/playnite-linux/pull/122) | `7ea3a3752dca413fae3aac8e2e869678d2375c37` | p2-evidence2 (tooling) | yes |

## Exit assessment

The six Phase 5 exit criteria are assessed in
[`docs/port/gates/P5-pending.md`](gates/P5-pending.md): **0 proven, 3 partial, 3 not proven**.
Phase 5 is not accepted. No real-machine launch, Flatpak host launch, or packaged host-library
read is claimed.

No product code, test, protected oracle or aggregate is changed by this brief.

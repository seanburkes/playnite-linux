# review-p6-203-second — Cross-Unit-Move guard, second independent adversarial review

- PR: https://github.com/seanburkes/playnite-linux/pull/203
- Repository: `seanburkes/playnite-linux` only
- Head reviewed: **b0a81e55aa824a0c6920d1699c008ff0cc03a525** (`b0a81e55`)
- Delta under review: `65afe55..b0a81e55`
  - delta sha256: `ac77491d56c32db3c56b5b62580ff629a063237db4110a94a33812e74a45d3bc`
- Full candidate (regression sweep): sha256
  `a30a50d5739d76a7678b446ad35212c516a9c36bbbc9c156f55c0a4ae3afa657`
- Forge state at review time: open, **not draft**, 8/8 checks pass, merge state `CLEAN`,
  `oracle-change` label present, head still `b0a81e55`.
- Mode: review-only. No repository files were changed. Scratch fixtures and mutation trees
  lived under `/tmp/opencode`.

## Verdict

**NOT CLEAN — one blocking P2 finding.**

The ownership guard itself is sound: all eight required behaviours are enforced by executable
tests, the new tests are load-bearing against the previous head, and the full `scripts/port`
suite passes with zero skips. The blocking defect is at the integration boundary: the feature
the PR advertises — "a captain-approved unit move can touch the owning unit's paths" — still
fails the other required job of the same workflow, `unit-evidence`, for **both** documented
allowed move shapes. The PR's own eight checks pass only because this PR does not use the
trailer.

## Blocking finding

### P2 — a documented cross-unit move fails the required `unit-evidence` check

`check_unit_ownership.py` now admits manifest-covered foreign paths, but
`scripts/port/check_unit_evidence.py` was not changed and still rejects every foreign path:

- `scripts/port/check_unit_evidence.py:333` rejects any changed evidence path outside the
  declared unit ("touches foreign unit evidence").
- `scripts/port/check_unit_evidence.py:343` rejects any changed map-owned path outside the
  declared unit ("touches unit-owned paths owned only by another unit").

Both checks run before the queue/fragment checks, so a complete, otherwise-valid move PR cannot
reach them. `.github/workflows/port-unit-guard.yml` runs both scripts as separate required jobs
(`unit-ownership` at line 18, `unit-evidence` at line 59; commands at lines 54 and 104) on every
`pull_request` event. The workflow is deliberately label-free, and
`test_guard_workflow_runs_both_checks_without_a_label_escape` (`test_unit_guard.py:1645`) asserts
that both scripts run and that no `labels.` filter exists — so `oracle-change` cannot bypass
`unit-evidence`.

Reproduced at head `b0a81e55` with disposable fixtures (outside the worktree), using the existing
`Fixture` harness:

1. Evidence move (`tests/golden/p4-002-legacy/PROVENANCE.md` →
   `tests/golden/p4-003-legacy/PROVENANCE.md`, `Port-Unit: p4-003`,
   `Cross-Unit-Move: p4-002:<old>`), plus a queue transition and a ledger fragment:

   ```
   check_unit_ownership.py --base <base> --head <head> --branch fm/p4-003
     -> exit 0
   check_unit_evidence.py --base <base> --head <head> --pr 207 --branch fm/p4-003
     -> exit 1
   error: unit PR p4-003 touches foreign unit evidence:
     tests/golden/p4-002-legacy/PROVENANCE.md (unit p4-002)
   ```

2. The map-owned product move the PR tests
   (`src/Playnite.Core/Import/AssetCopy.cs` → `src/Playnite.Core/Import/Moved/AssetCopy.cs`,
   owner `p4-005` from `ownership.paths`):

   ```
   check_unit_ownership.py ... -> exit 0
   check_unit_evidence.py  ... -> exit 1
   error: unit PR p4-003 touches unit-owned paths owned only by another unit:
     src/Playnite.Core/Import/AssetCopy.cs (unit p4-005; pull/96 at commit ...)
   ```

The new test `test_foreign_unit_evidence_also_fails_the_evidence_check` (`test_unit_guard.py:1441`)
still encodes the opposite invariant, so the two guards now contradict each other for exactly the
change the trailer authorises. `docs/port/units/README.md:117-160` presents the move as a usable
mechanism ("A pull request may move a path out of another unit's ownership only with an explicit
manifest", "CI enforces the shape of the manifest") without saying that the required
`unit-evidence` job will reject it.

Why this blocks: the feature cannot be used end-to-end. Every real cross-unit move PR will go red
on a required check whose documented escape (`oracle-change`) is explicitly not honoured, and the
failure appears only after the move is attempted — the PR under review passes only because it is
a guard change. This is a correctness/integration defect, not a security weakening, so I rate it
P2 rather than P1.

Concrete remedy (either is acceptable, but the current head is neither):

- Extend `check_unit_evidence.py` to consume the same head-commit manifest (reuse
  `cross_unit_move_manifest` and `validate_cross_unit_move`) and exempt exactly the listed foreign
  paths in both the `foreign` and `foreign_owned` checks, with executable tests proving a complete
  move PR passes **both** scripts and that an unlisted/wrong-owner path still fails both; update
  the README and the existing evidence-guard test accordingly; or
- If the evidence-guard integration is deliberately a separate follow-up, say so explicitly in
  the PR/README and do not present the judge as usable; the current wording overstates what CI
  accepts.

## Executable verification of the eight required behaviours

Run at head `b0a81e55` (detached checkout):

```sh
python3 -m unittest discover -s scripts/port -p 'test_unit_guard.py'          # 85 tests, OK
python3 -m unittest discover -s scripts/port -p 'test_*.py'                   # 248 tests, OK, 0 skips
python3 -m unittest discover -s scripts/port -p test_unit_guard.py \
    -k 'CrossUnitMoveTests' -v                                                # 14 tests, OK
python3 scripts/port/check_guard_tests.py \
    --base 15833157a2843d011e01ea0b5fb9736cd25711f0 \
    --head b0a81e55aa824a0c6920d1699c008ff0cc03a525                          # preserved
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
                                                                              # shards valid
```

I also re-derived each required rule with independent probes (not just re-running the PR tests).
All eight hold:

| # | Rule | Test actually exercised | Outcome |
|---|------|-------------------------|---------|
| 1 | Two foreign `M`s sharing one basename `D` fails | `test_two_modifications_sharing_one_delete_fails`; independent probe | exit 1, "no unclaimed deleted counterpart"; names the unpaired second `M` |
| 2 | An `M` with no `D` fails | `test_modified_listing_with_no_delete_fails`; independent probe | exit 1, same message |
| 3 | A `D` paired with an add outside the declaring unit fails | `test_delete_paired_with_an_add_outside_the_declared_unit_fails`; independent probe | exit 1, "no same-basename added counterpart owned by p4-003" |
| 4 | A `D` reused across `M`s fails | independent probe (one `D`, one in-unit add, two `M`s) | exit 1, second `M` named |
| 5 | A valid move passes | `test_allowed_move_of_foreign_evidence_passes`, `..._map_owned_product_path_passes`, `test_modification_paired_with_its_own_delete_passes`; independent probe | exit 0, "offending paths: (none)" |
| 6 | Declaring-unit ownership comes from `ownership.paths`, not trailer text | `test_allowed_move_of_a_map_owned_product_path_passes`, `test_wrong_owner_fails`; independent wrong-owner probe | wrong trailer owner for a map-owned path → exit 1 with the map's actual owner |
| 7 | An unlisted foreign path fails | `test_unlisted_foreign_path_fails`; independent probe | exit 1, path and unit named |
| 8 | No-add fails | `test_move_with_no_added_counterpart_fails`; independent probe | exit 1, "no same-basename added counterpart" |

Grammar/validation paths without a named test were probed and also fail closed: repeated path,
unknown unit, invalid unit id, whitespace, path traversal, empty entry, and a listed unchanged
path each exit 1 with the expected message.

Mutation check: the head test module was copied over an exported `65afe55` tree and
`CrossUnitMoveTests` was run against the old guard. Four tests fail there — the three real
regressions (`test_two_deletes_sharing_one_add_fails`,
`test_two_modifications_sharing_one_delete_fails`,
`test_delete_paired_with_an_add_outside_the_declared_unit_fails`; each returns 0 on the old guard)
and one message-only assertion (`test_modified_listing_with_no_delete_fails`). The delta is
load-bearing.

## Regression sweep

- With no `Cross-Unit-Move` trailer, `move is None`, `allowed_foreign` is empty, and every
  ownership branch reduces to the pre-delta behaviour; the full 248-test suite confirms no
  regression.
- The `validate_cross_unit_move` foreign set covers both evidence namespaces (`touched`) and base
  map claims, rejects any unlisted changed foreign path, rejects listed `A` paths, and enforces
  one-to-one pairing in both directions.
- `_head_message` preserves `derive_marker_unit`'s decode/error behaviour.
- No banned APIs, no new dependencies, no workflow or label escape in the guard change itself.
- Ledger/queue rows match the tests actually run; the fragment's cited validated head
  (`ee8a009`) precedes the one-to-one fix, as the PR body states.

## Non-blocking observations

- The trailer block does not have named tests for the grammar failures (repeated path, unknown
  unit, invalid unit id, invalid path, whitespace, empty entry, listed-but-unchanged path).
  They behave correctly in my probes; tests would lock them in.
- Basename-only pairing remains by design (captain hand review of file identity).
- The success output lists `cross-unit move:` lines, which is useful for the captain's hand
  review.

**Conclusion:** the ownership guard delta is correct and well tested, but the feature is not
usable in the required workflow until `unit-evidence` honours the same manifest. Do not merge
https://github.com/seanburkes/playnite-linux/pull/203 at head `b0a81e55` as a working feature
until that integration (or an explicit, documented scope reduction) lands.

# Adversarial review — PR #202 (p6-006 notification, dialog and main-view host surfaces)

**Verdict: one blocking P2. The code move is sound, but the shipped `IGameView` documentation and
the delivered unit record both state an integration design the code does not have. Fix the two
sentences before merge; nothing else blocks.**
Review-only: this review authored no tracked change to the reviewed branch, did not merge, and did
not push to it.

| | |
|---|---|
| Repository | https://github.com/seanburkes/playnite-linux |
| Pull request | https://github.com/seanburkes/playnite-linux/pull/202 |
| Reviewed head | `bc34b832aa12e5e43dab6d61b946df0696d7cbfa` (`bc34b832`) |
| Base / merge base | `0187b09fc7e85c25dcba64f9458f88769b2b1622` (`origin/main` at review time) |
| Candidate diff | `data/p6-202/candidate.diff`, sha256 `2c2941303eac81978347b47da13618e6e72246f197db26a4872aefb6d1e661c6` |
| Diff check | `git diff origin/main...refs/review/202` has exactly that sha256; 27 files, +4098 / −633 |
| PR state | open, not draft, 8/8 checks pass |
| Ledger-validated head | `7c5db4eac7b472279e1943e1feb5f036b69d08e8` (ancestor of the reviewed head), linux-ci run `44812902320` |

## Method

Fetched `refs/pull/202/head`, confirmed the SHA above, generated
`git diff origin/main...refs/review/202`, confirmed the SHA-256 matches the supplied candidate
diff, checked out the head detached in this isolated worktree, ran the narrow builds, the full
Core and Architecture suites and both unit guards, and read the complete diff plus the moved files
and adapters at the head.

## Blocking finding (P2)

### P2 — the `IGameView` XML doc and the p6-006 queue note claim `Game` implements the interface

`src/Playnite.SDK.Abstractions/Models/IGameView.cs:4-6` ships this public doc comment:

> The Core `Playnite.Core.Models.Game` implements it, so a host can hand Core games to the
> abstraction without copying or re-projecting them.

It is false in the reviewed tree:

- `src/Playnite.Core/Models/Game.cs:190` declares `public class Game : DatabaseObject`; it does not
  implement `Playnite.SDK.Models.IGameView`.
- `src/Playnite.Core/Hosting/MainView.cs:13` introduces `public sealed class GameViewAdapter :
  IGameView` instead, and its doc says the Core model "stays free of the abstraction's types".
- `tests/Playnite.Core.Tests/SdkContracts/SdkHostContractTests.cs` proves the opposite of the
  comment: `Core_game_is_adapted_to_IGameView_without_changing_the_model` asserts
  `Assert.DoesNotContain(typeof(IGameView), typeof(Game).GetInterfaces())`.
- `MainViewState` and `MainViewGamesResponse` copy their inputs with `ToArray()`, so "without
  copying" is false too.

The same false claim is in the permanent unit row: `docs/port/units/p6-006.queue.tsv:2` says
"…the abstraction-level `IGameView`, which Game implements, so `MainViewState` and
`MainViewGamesResponse` expose `IReadOnlyList<IGameView>` with no copy…". That contradicts the PR
body itself ("`Game` is untouched and does not implement the SDK interface") and the delivery doc,
which correctly describes `GameViewAdapter`.

This is not a runtime defect, but it is a false statement in the public SDK surface and in the
unit record that later units will read as the delivered design. The review brief requires the
queue/docs rows to be honest, so this blocks merge until corrected.

**Fix:** reword both places to say that `GameViewAdapter` projects a Core `Game` onto `IGameView`
and that the responses copy their lists. (Making `Game` implement the interface instead would
contradict the ownership-safe design the PR describes and its own test; do not take that route
without reopening the design.)

## Other checks (all pass)

1. **Foreign product edits are genuinely gone.** The diff touches only p6-006-owned Core hosting
   files, `Playnite.Platform/DesktopIntegration/NotificationServiceHost.cs`, new Abstractions
   files, p6-006 tests/docs and the shared `PublicAPI.Unshipped.txt` allowance. No p3-004,
   p3-006, p6-001, p6-011 or p6-012 source/test file appears.
   `check_unit_ownership.py --base origin/main --head HEAD --branch fm/p6-006b` exits 0 with
   `offending paths: (none)`; `check_unit_evidence.py --pr 202` also exits 0.
2. **SDK↔Core enum mapping preserves values.** The SDK `SortOrder` (18 members) and
   `LibraryGroupBy` (13 members) are member-for-member and value-for-value copies of
   `Playnite.Core.Queries.SortOrder` and `Playnite.Core.Library.LibraryGroupBy`;
   `MainViewEnumAdapter` casts the numeric value and rejects undefined values in both directions.
   `Core_and_SDK_query_enums_round_trip` enumerates every Core value and both rejection paths.
3. **Ordering and copying are preserved.** `MainViewApi` keeps the same query path and
   `ToArray()` snapshot; `MainViewApiTests.ResponsesCopyTheHostList` and
   `MainViewStateValidatesAndCopiesItsInputs` still pass, now over `IGameView`. This unit has no
   paging surface, and no library-query code is edited.
4. **Contract coverage and plugin-facing compile check.** `SdkHostContractTests` adds one
   compile-time fake and behavioural test for `IGameView`, `INotificationHost`,
   `INotificationsAPI`, `IDialogHost`, `IDialogsFactory`, `IMainViewHost` and `IMainViewAPI`,
   plus adapter and both-direction enum tests. `SdkHostSurfaceTests` asserts the moved types are
   exported by `Playnite.SDK.Abstractions`, that no `Playnite.Core.Hosting` copy remains, and
   compiles a sample plugin against the Abstractions assembly alone (no Core, no Avalonia),
   using all three host surfaces and `IGameView`.
5. **`NotificationServiceHost` still works.** `NotificationServiceHostTests` pass unchanged
   against the moved abstraction types, as part of the Core suite.
6. **No Core duplicates remain.** `grep` for `INotificationsAPI`, `IDialogsFactory`,
   `IMainViewAPI`, `NotificationMessage` and `DesktopView` in `src/Playnite.Core` finds no
   duplicate declarations.
7. **PublicAPI matches the surface.** The Abstractions build (with `ApiCompatValidateAssemblies`
   and `-warnaserror`) reports 0 warnings / 0 errors; the Architecture `PublicApiTests` pass. The
   `api-baseline` is unchanged, as the PR states.
8. **ApiCompat artefact is internally consistent.** `p6-006-apicompat-v6-v7.txt` has 308
   difference lines: 210 `CP0001`, 82 `CP0002`, 15 `CP0006`, 1 `CP0009`.
   `p6-006-apicompat-classification.tsv` has 308 classified rows (114 adapted, 180 deferred,
   14 excluded), and the suppression XML is well-formed with 308 suppressions.
9. **Docs are otherwise accurate.** The delivery doc, PR body and ledger fragment correctly
   describe the adapter, the copied snapshots and the v6→v7 counts. The queue row's XML-doc
   claim above is the only false statement found.

## Build and test evidence

All commands ran in the reviewed worktree at `bc34b832`; full transcripts are in
`data/p1-review-by/logs/202-validated-bc34b83.log` and `data/p1-review-by/logs/202-guards.log`.

```text
$ dotnet build src/Playnite.Core -c Release -warnaserror --no-incremental
Build succeeded.  0 Warning(s)  0 Error(s)                 # build_exit=0
$ dotnet test tests/Playnite.Core.Tests -c Release
  total: 3049  failed: 0  succeeded: 3049  skipped: 0      # test_exit=0
$ dotnet test tests/Playnite.Architecture.Tests -c Release
  total: 201  failed: 0  succeeded: 201  skipped: 0        # arch_exit=0
$ python3 scripts/port/check_unit_ownership.py --base origin/main --head HEAD --branch fm/p6-006b
  offending paths: (none)                                 # ownership_exit=0
$ python3 scripts/port/check_unit_evidence.py --base origin/main --head HEAD --pr 202 --branch fm/p6-006b
  unit: p6-006; queue status done; fragment head 7c5db4e, run 44812902320   # evidence_exit=0
```

`gh-axi pr checks 202` reports 8/8 passing including `unit-ownership` and `unit-evidence`.
`gh-axi run view 44812902320` reports `completed / success` on branch `fm/p6-006b`, matching the
ledger fragment's cited run and head (the final `bc34b832` commit is the docs-only ledger-citation
delta after the validated head, as the fragment states).

## Non-blocking observations (P3)

1. **Stale PR-body gate paragraph.** The body's "gate state" still says
   `inventory-and-parity` fails on `PublicAPI.Unshipped.txt`; all 8 checks, including
   `inventory-and-parity`, are green at the reviewed head. This is prose on the forge, not a
   tracked artifact.
2. **`HostOperations.cs` rename is namespace-only.** The 93 % rename hunk changes only
   `namespace Playnite.Core.Hosting;` to `namespace Playnite.SDK;`; no member or behaviour change.
3. **ApiCompat replay not independently reproduced.** The 6.18.0 `PlayniteSDK` assembly and the
   ApiCompat tool are not available in this worktree's cache, so I verified the report's counts,
   its classification table and its XML shape rather than replaying the suppressions. The merge
   decision remains the captain's; this review did not merge, approve, or push anything.

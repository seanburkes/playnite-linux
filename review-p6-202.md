# Adversarial review — https://github.com/seanburkes/playnite-linux/pull/202 (p6-006 move)

**Verdict: NOT CLEAN — one blocking P2 (the queue row and the public `IGameView` XML doc state
that `Game` implements `IGameView` and that the surface copies nothing; the delivered code uses
`GameViewAdapter` and the unit's own test asserts the opposite). The restructure itself is
behaviour-preserving. Hold for the captain.**
Review-only: no tracked product change was authored by this review.

| | |
|---|---|
| Repository | https://github.com/seanburkes/playnite-linux |
| Pull request | https://github.com/seanburkes/playnite-linux/pull/202 |
| Reviewed head | `bc34b832aa12e5e43dab6d61b946df0696d7cbfa` (`bc34b83`, docs-only delta after the source head) |
| Base / merge base | `0187b09fc7e85c25dcba64f9458f88769b2b1622` (`0187b09`, current `origin/main`) |
| Candidate diff | task material `data/p6-202/candidate.diff`, sha256 `2c2941303eac81978347b47da13618e6e72246f197db26a4872aefb6d1e661c6` |
| Diff shape | 27 files, +4098 / −633 (recomputed with `git diff --shortstat`; digest byte-identical to the artifact) |
| Ledger-validated head | `7c5db4eac7b472279e1943e1feb5f036b69d08e8` (ancestor of the reviewed head; linux-ci run `44812902320`) |
| PR state | open, not draft, merge state clean, `oracle-change` label present, 8/8 checks pass |

## Commands run on the reviewed head

All commands were run after `git checkout --detach bc34b832…`.

```console
$ sha256sum data/p6-202/candidate.diff
2c2941303eac81978347b47da13618e6e72246f197db26a4872aefb6d1e661c6  …/candidate.diff

$ git diff 0187b09fc7e85c25dcba64f9458f88769b2b1622 bc34b832aa12e5e43dab6d61b946df0696d7cbfa | sha256sum
2c2941303eac81978347b47da13618e6e72246f197db26a4872aefb6d1e661c6  -

$ git diff --shortstat 0187b09fc7e85c25dcba64f9458f88769b2b1622 bc34b832aa12e5e43dab6d61b946df0696d7cbfa
 27 files changed, 4098 insertions(+), 633 deletions(-)

$ python3 scripts/port/check_unit_ownership.py \
    --base 0187b09fc7e85c25dcba64f9458f88769b2b1622 \
    --head bc34b832aa12e5e43dab6d61b946df0696d7cbfa --branch fm/p6-006b
unit: p6-006
allowed paths touched: … (19 listed paths; every listed product/test path is owned by p6-006,
                        PublicAPI.Unshipped is the shared SDK baseline path; the new
                        Abstractions files are additions under the p6-006 declaration)
offending paths:
  (none)
EXIT=0

$ GH_TOKEN=… python3 scripts/port/check_unit_evidence.py \
    --base 0187b09fc7e85c25dcba64f9458f88769b2b1622 \
    --head bc34b832aa12e5e43dab6d61b946df0696d7cbfa --pr 202 --branch fm/p6-006b \
    --repo-slug seanburkes/playnite-linux --verify-ci
unit: p6-006
queue: docs/port/units/p6-006.queue.tsv status done
fragment: docs/port/units/p6-006.ledger.d/202-host-surface-move.tsv
          (head 7c5db4eac7b472279e1943e1feb5f036b69d08e8, run 44812902320)
EXIT=0

$ dotnet build … -c Release -t:Rebuild   # Abstractions, Core, Platform, Core.Tests, Architecture.Tests
Build succeeded. 0 Warning(s) 0 Error(s)  (all five projects)
$ dotnet build src/Playnite.Cli and src/Playnite.Desktop -c Release
0 Warning(s) 0 Error(s)

$ dotnet test tests/Playnite.Core.Tests -c Release --no-build
total: 3049  failed: 0  succeeded: 3049  skipped: 0

$ dotnet test tests/Playnite.Architecture.Tests -c Release --no-build
total: 201  failed: 0  succeeded: 201  skipped: 0

$ dotnet test tests/Playnite.Core.Tests -c Release --no-build --filter "FullyQualifiedName~NotificationServiceHost"
total: 9  failed: 0  skipped: 0
$ dotnet test tests/Playnite.Core.Tests -c Release --no-build --filter "FullyQualifiedName~SdkContracts"
total: 9  failed: 0  skipped: 0

$ python3 scripts/port/build_port_index.py check --shards-only --against origin/main
port index shards are valid; aggregates are generated on main   (exit 0)
```

`gh-axi pr checks 202`: `8 passed, 0 failed`, including `unit-ownership` and `unit-evidence`.
The `oracle-change` label is present, so the `inventory-and-parity` protected-path gate is green
as well.

## What was reviewed

- **The foreign edits really are gone.** `check_unit_ownership` exit 0 shows every product/test
  path in the diff is owned by p6-006 (map rows from pull/190); the only shared path is
  `src/Playnite.SDK.Abstractions/PublicAPI.Unshipped.txt`, allowed by #205. The diff touches no
  p3-004/p3-006/p6-001/p6-011/p6-012 file, matching the PR body.
- **The move is a move, not a redefinition.** Normalising away `using`/`namespace` lines and
  diffing the old bodies against the new Abstractions files gives:
  - `Notifications.cs`: zero differences apart from the moved `NotificationsApi` class (which
    stays in Core unchanged) and the two `RS0026` pragma lines;
  - `Dialogs.cs`: zero differences apart from the moved `DialogsFactory` class (unchanged);
  - `HostOperations.cs`: byte-identical body under the renamed namespace;
  - `MainView.cs`: only `Game` → `IGameView` substitutions; every `Enum.IsDefined` validation,
    default and snapshot copy is intact.
  The concrete services (`NotificationsApi`, `DialogsFactory`, `MainViewApi`) are byte-identical
  apart from the element type in `MainViewApi`'s delegates.
- **Sort/group mapping is faithful.** Core `Playnite.Core.Queries.SortOrder` and
  `Playnite.Core.Library.LibraryGroupBy` have exactly the same member names and numeric values as
  the new `Playnite.SDK.Models` enums, and those values match v10
  (`legacy/source/PlayniteSDK/Models/FilterPreset.cs:11-99`: `Name=0, LastActivity=4,
  ReleaseDate=6, … InstallSize=25, RecentActivity=26`). `MainViewEnumAdapter` maps by numeric
  value with `Enum.IsDefined` rejection of unknown values, and
  `Core_and_SDK_query_enums_round_trip` covers every member both ways plus the four rejections.
  Ordering semantics, paging and group-by live in the untouched `LibraryQuery`/comparer code.
- **`IGameView` adapter.** `Game` still does not implement it; `GameViewAdapter` wraps the model
  and projects `Id`/`Name` (null-safe). The model file is untouched. The contract copies the list
  into the response (`MainViewState`/`MainViewGamesResponse` still snapshot input).
- **`NotificationServiceHost`** still implements the moved `INotificationHost` and maps
  Info/Normal and Error/Critical; its 9 tests pass, including cancellation and the unavailable
  dismissal path.
- **Plugin-facing compile check present.** `SdkHostSurfaceTests` asserts the moved types live in
  `Playnite.SDK.Abstractions` and compiles a sample plugin against that assembly alone (no Core,
  no Avalonia), using all three host surfaces and `IGameView`.
- **PublicAPI**: `PublicAPI.Unshipped.txt` gains 343 lines; `PublicAPI.Shipped.txt` and the binary
  baseline are unchanged. Abstractions builds with `TreatWarningsAsErrors=true` and the
  architecture suite's RS0016/RS0017 + ApiCompat baseline tests pass (201/201), so the declared
  surface matches the compiled one.
- **ApiCompat evidence**: the classification has 308 rows (210 `CP0001`, 82 `CP0002`, 15 `CP0006`,
  1 `CP0009`) with 98 member-level rows all `adapted`; the suppression file has 308 suppression
  elements; the v6 package and `lib/net462/Playnite.SDK.dll` hashes match the report. I did not
  re-run ApiCompat itself (the v6 package is not in the offline cache), but the report,
  classification and suppressions are internally consistent and the repo's baseline test passes.
  (The v6 package and DLL hashes cited above were verified directly from NuGet; only the 308-row
  comparison itself was not re-executed.)

## P2 — the queue row and the public `IGameView` doc describe a design that was not delivered

`src/Playnite.SDK.Abstractions/Models/IGameView.cs:3-7` says:

> The game fields an SDK-facing surface exposes without the Core database model. The Core
> `Playnite.Core.Models.Game` **implements it**, so a host can hand Core games to the abstraction
> **without copying or re-projecting them**.

`docs/port/units/p6-006.queue.tsv` (notes column) repeats it:

> `IMainViewAPI` is reshaped off the Core `Game` type through the abstraction-level `IGameView`,
> **which `Game` implements**, so `MainViewState` and `MainViewGamesResponse` expose
> `IReadOnlyList<IGameView>` **with no copy** …

Both statements are false at the reviewed head:

- `src/Playnite.Core/Hosting/MainView.cs:13` is `public sealed class GameViewAdapter : IGameView`,
  a re-projection; `src/Playnite.Core/Models/Game.cs:190` is `public class Game : DatabaseObject`
  with no `IGameView`.
- The unit's own test pins the opposite:
  `tests/Playnite.Core.Tests/SdkContracts/SdkHostContractTests.cs:38` —
  `Assert.DoesNotContain(typeof(IGameView), typeof(Game).GetInterfaces());`
- The PR body and the ledger fragment both describe the delivered adapter correctly
  ("`Game` is untouched and does not implement the SDK interface"; "`GameViewAdapter` projects a
  `Game` onto it"). The queue row and the public XML doc are the leftovers of an earlier design.

Why this is P2 rather than a typo: the queue row is the authoritative unit record the captain
reads, and the XML doc ships with the public SDK contract. A plugin author reading it would
allocate a `Game` and pass it where `IGameView` is required and get a compile error, and a future
unit would believe the model already implements the interface. This is the same class of
doc/implementation mismatch flagged as blocking in the p6-168 review (P2-3).

Fix: reword both to match the delivered shape (`GameViewAdapter` projects a Core `Game`; the
model is not modified), and optionally add a positive note that the host wraps before handing a
game to `IMainViewAPI`.

## Non-blocking observations

- `docs/port/p6-006-notifications-dialogs-mainview.md` (Verification) says
  "Full `Playnite.Core.Tests`: 3,047 passed"; the reviewed head runs 3,049 (the PR body and ledger
  both say 3,049). Stale intermediate count.
- The moved `INotificationsAPI` keeps the deliberate `RS0026` overload pair under a documented
  pragma; no member was silently dropped.

## Verdict

The restructure is otherwise sound: a real move with identical bodies, a correct numeric
sort/group mapping, a null-safe `IGameView` adapter, green ownership/evidence, green build and
tests (Core 3,049 / Architecture 201, zero skips), an intact `NotificationServiceHost`, a
matching PublicAPI and a plugin-only compile check. The one blocker is the false
"`Game` implements `IGameView` / no copy" claim in the unit queue row and the public `IGameView`
XML doc, which contradicts the delivered adapter and the unit's own test.

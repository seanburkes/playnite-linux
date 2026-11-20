# p6-006 review — notification, dialog and main-view host API

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/190
- Reviewed head: `5bf8ba8426548341254a626e40f1a4e32bb2ab78` (`docs(p6-006): record the linux-ci evidence`; implementation commit `dc88ae4c93cf092711d0685b67f64ee4596f3fdb`)
- Base: `main` at `19a4637f78852fa87b5a009e7b61f047585093a9`
- Candidate diff: `/home/sean/src/firstmate/data/p6-190/candidate.diff`
- SHA-256: `2eeaa7f63f83c4c1f19f8ecd31883c8c54701bd217833baf342bc2ae1cd6d176`
- Size: 15 files, +2259/−0 (five `src/Playnite.Core/Hosting` files, the Platform notification adapter, six test files (five suites plus shared fakes), one contract doc, the p6-006 shard and its ledger fragment); matches the forge's changed-file and line counts for the head.
- Method: static review of the candidate diff only (extracted to a scratch tree for stable line numbers). No build or test execution, no other reviewer's output, no other repository slug.

## Verdict

**No blocking P1/P2 findings.** Every file in the diff is new, so no existing abstraction can break;
missing hosts degrade rather than throw; the notification dedupe/cancellation state machine has no lost
reservation or double-presentation path; snapshots are copied; and the head has forge-verifiable evidence.
Merge can proceed.

## Blocking findings

None.

## Checks performed

### 1. API contract shape and source compatibility — acceptable for this unit's stated scope

All 15 files are additions (`new file mode`), including the unit shard; no existing `Playnite.SDK*`,
abstraction, PublicAPI or api-baseline file is modified, so the PR cannot be source-breaking. The three
surfaces are new Core types: `INotificationsAPI` (`Notifications.cs:122`), `IDialogsFactory`
(`Dialogs.cs:255`) and `IMainViewAPI` (`MainView.cs:144`). The doc
(`docs/port/p6-006-notifications-dialogs-mainview.md:16-23`) explains why they live in Core rather than
Abstractions and hands the reviewed public-name freeze and ApiCompat report to `p6-008`; the queue row
keeps `area=sdk` while the implementation sits in Core, which the doc justifies by the Core-only `Game`
dependency. Enum values are explicit (`NotificationType.Info=0/Error=1`,
`DesktopView.Details=0/Grid=1/List=2`, `FullscreenView.List=0/Details=1`), so a later one-for-one mapping
does not depend on declaration order. Request records validate enum values and reject unknown ones
(`Dialogs.cs:61-79`, `:114-131`; `Notifications.cs:18-27`; `MainView.cs:34-66`).

### 2. Missing-host degraded startup — sound

`HostApis.Create` accepts three nullable seams, never throws, and adds exactly one diagnostic per missing
host (`HostApis.cs:38-64`); the tests assert three, two and zero diagnostics for the no-host, partial and
full cases (`HostApiTests.cs:9-60`). Each surface returns `Unavailable` plus its documented diagnostic when
its host is null (`Dialogs.cs:301-303`, `:340-342`, `:394-396`; `MainView.cs:171-175`;
`QueryGamesAsync` `:218-221`; `Notifications.cs:229-235`), and no surface throws on construction. That
covers the CLI and pre-shell paths the doc describes.

### 3. Notification dedupe, in-flight sharing, ordering and cancellation — sound

- Dedupe is ordinal and checked against the tracked list first (`Notifications.cs:204-210`), so a repeat
  neither presents nor raises `Changed` and the first text survives (`NotificationsApiTests.cs:18-32`).
- The in-flight map (`Notifications.cs:159-160`) is only mutated under `gate`; exactly one caller becomes
  the owner and presents (`:212-222`), while a concurrent duplicate awaits the owner's completion source
  (`:224-227`) and gets the same terminal result (`NotificationsApiTests.cs:60-78`).
- On success the owner removes the reservation and appends under the lock, then completes the source and
  raises `Changed` (`:239-252`); on unavailable/failed presentation `Complete` removes the reservation
  before completing (`:384-396`), which is what makes a retry possible
  (`NotificationsApiTests.cs:100-134`).
- Caller cancellation before the host call throws with nothing reserved (`:198-199`); cancellation during
  presentation removes the reservation, cancels waiters, and rethrows (`:263-271`), pinned by
  `NotificationsApiTests.cs:145-165` including a successful retry afterwards. A shared waiter's own token
  is honoured by `WaitAsync` (`:226`).
- `Messages`/`Count` return snapshots under the lock (`:166-188`); `DismissAll` captures ids and clears
  under the lock before any host call (`:344-355`), and host failures are reported per id without
  re-adding anything (`:357-379`).

### 4. Dialog mapping, degradation and cancellation — sound

Each entry point validates the request, checks cancellation before touching the host, maps a missing host
to `Unavailable`, delegates with `ConfigureAwait(false)`, and maps any host exception to `Failed` with a
diagnostic while rethrowing `OperationCanceledException` (`Dialogs.cs:294-414`). The convenience builders
pass the right slots: `SelectFolderAsync` sets `initialDirectory` and leaves `filter` null
(`Dialogs.cs:362-368`), `SaveFileAsync` sets filter/directory/overwrite (`:371-379`), and
`ShowErrorAsync` builds the error request (`:324-330`). Responses copy selected paths (`:201`), and the
folder/multi-select contradiction is rejected (`:127-130`).

### 5. Main-view snapshots and failures — sound

State and game lists are copied on construction (`MainView.cs:68-69`, `:108`), so a host cannot mutate an
answer after return (`MainViewApiTests.cs:152-163`). A null state is `Failed` (`MainView.cs:180-184`) and a
host exception is `Failed` with a diagnostic (`:186-194`, `:229-238`), never an escaped exception except
cancellation.

### 6. Threading and no UI dependency — sound

The Core layer never captures a synchronization context, never touches a dispatcher, and never blocks a
caller thread; UI marshalling remains the host's job, as documented. I grepped every added line for
`Avalonia`, `System.Windows`, `System.Drawing`, `Microsoft.Win32`, `DllImport`, `Process.Start`,
`Thread.Sleep`, `async void`, `Task.Run`, `.Result` and `.Wait()`; none appear in product or test code. The
only UI-adjacent piece is `Playnite.Platform.DesktopIntegration.NotificationServiceHost`, which adapts the
already-merged p5-007a service and adds no UI reference to Core.

### 7. p5-007a adapter — sound

`NotificationServiceHost` maps Info→`Normal`, Error→`Critical`, Delivered and transport-level duplicate to
`Presented`, and reports the service's own unavailable detail (`NotificationServiceHost.cs:21-46`),
including a null-service guard (`:18`) and cancellation before and during the service call
(`NotificationServiceHostTests.cs:81-118`). Dismissal is honestly reported as unsupported locally
(`:48-56`), which the API treats as "removed locally; host did not dismiss" (`Notifications.cs:314-321`).

### 8. Evidence and ownership — acceptable

- Head `5bf8ba8` has all eight checks green, including `unit-ownership` and `unit-evidence`; the
  `linux-build-and-test` run on this head is
  [44697795295](https://github.com/seanburkes/playnite-linux/actions/runs/44697795295) and `unit-evidence`
  is [44697795281](https://github.com/seanburkes/playnite-linux/actions/runs/44697795281).
- The diff adds exactly 51 `[Fact]` cases (18 notifications, 11 dialogs, 11 main-view, 3 host wiring, 8
  adapter), zero `Skip`/`Ignore`, matching the PR body, the doc and the ledger. The claimed Core total
  (2,842) is exactly the current main baseline (2,791, which p6-005's 2,809 total also implies after its 18
  metadata cases) plus these 51.
- The ledger fragment records the validated head, the base commit and the CI run; the queue row is new and
  moves the unit to `done`. No protected oracle, baseline, protected PublicAPI or generated aggregate is
  touched, and no merge was performed.

## Non-blocking notes (P3, not findings)

- A shared in-flight waiter receives the owner's terminal state, including the owner's cancellation, even
  when the waiter's own token is live (`Notifications.cs:224-227`, `:263-271`). The doc says an in-flight
  duplicate "shares the first result", so this is the documented behaviour; a caller that must distinguish
  its own cancellation from the owner's would want a per-caller result.
- `DialogsFactory` does not null-check the host's returned response (e.g. `Dialogs.cs:306-318`), unlike
  `MainViewApi.GetStateAsync` (`MainView.cs:180-184`); a host that violates its contract by returning null
  yields a `NullReferenceException` instead of `Failed`.
- `NotificationServiceHost` maps every delivery state other than `Delivered`/`SuppressedDuplicate` to
  `Unavailable` (`NotificationServiceHost.cs:41-43`). If the p5-007a `NotificationDeliveryState` has a
  distinct failure member, that failure is reported as `Unavailable`; the tracked-list behaviour is the same
  but the status and diagnostic say "unavailable" rather than "failed". Worth reconciling when p5-007a's
  enum is frozen.

## Scope

Read only PR 190's candidate diff and forge metadata for its head. PR 189 was reviewed first from its own
candidate file; no other review output was read.

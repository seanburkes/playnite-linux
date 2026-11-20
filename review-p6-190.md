# p6-006 review — notifications, dialogs and main-view host API

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/190
- Reviewed head: `5bf8ba8426548341254a626e40f1a4e32bb2ab78`
- Base: `19a4637f78852fa87b5a009e7b61f047585093a9`
- Candidate diff: `/home/sean/src/firstmate/data/p6-190/candidate.diff`, SHA-256 `2eeaa7f63f83c4c1f19f8ecd31883c8c54701bd217833baf342bc2ae1cd6d176`
- Size: 15 files, +2259/−0
- Method: static review of the candidate diff and the APIs it names. No build and no test execution, per the brief; the cited CI run is not re-run.
- Verdict: **one blocking P2 finding**

## Blocking findings

### P2-1 — `DismissAllAsync` drops a non-throwing host failure

**Locations:** `src/Playnite.Core/Hosting/Notifications.cs:367` (the host result is awaited and
discarded), `src/Playnite.Core/Hosting/Notifications.cs:379-381` (a diagnostic is produced only
from caught exceptions); the promise is at
`docs/port/p6-006-notifications-dialogs-mainview.md:27` ("`DismissAllAsync()`: removes everything
and reports any host failures"); the production adapter returns a non-throwing failure at
`src/Playnite.Platform/DesktopIntegration/NotificationServiceHost.cs:48-54`.

`INotificationHost.DismissAsync` can report failure through its return value
(`NotificationHostStatus.Failed`/`Unavailable` with a diagnostic), not only by throwing.
`NotificationsApi.DismissAsync` handles that path (`Notifications.cs:321-324`), but
`DismissAllAsync` ignores the returned `NotificationHostResult`. It adds to `failures` only in the
`catch` block, so a host that returns `Failed`/`Unavailable` yields
`NotificationDismissAllResult(Dismissed: ids.Length, Diagnostic: null)`.

**Concrete failure:** publish one message, then use a fake host whose `DismissHandler` returns
`new NotificationHostResult(NotificationHostStatus.Failed, "cannot dismiss")`. Calling
`DismissAllAsync()` returns `Dismissed == 1` and `Diagnostic == null`, while calling
`DismissAsync("id")` on the same host returns the diagnostic `"cannot dismiss"`. The real
`NotificationServiceHost` always returns `Unavailable` with
`"The desktop notification service does not expose dismissal..."`, so every `DismissAllAsync` in
the only production adapter silently loses that information, contrary to the unit's own table.
The existing test at `tests/Playnite.Core.Tests/Hosting/NotificationsApiTests.cs:270-285` covers
only a host that throws, so the contract is not pinned.

The fix is local: capture each `NotificationHostResult`, and add a failure entry when
`!result.WasPresented` (using `result.Diagnostic` or a stable fallback), in the same way
`DismissAsync` does, then add a test for a non-throwing `Failed` result.

## Checks performed

### 1. API shape and source compatibility

The unit adds a new `Playnite.Core.Hosting` namespace and does not edit an existing abstraction.
`INotificationsAPI` keeps the v10 `Messages`, `Count` and `Changed` names and maps add/remove to
the asynchronous, typed `PublishAsync`, `DismissAsync` and `DismissAllAsync`
(`src/Playnite.Core/Hosting/Notifications.cs:118-144`). `IDialogsFactory` keeps the v10 entry
points with typed requests (`src/Playnite.Core/Hosting/Dialogs.cs:255-279`), and
`IMainViewAPI` exposes the UI-free state/selection/filtered-game data
(`src/Playnite.Core/Hosting/MainView.cs:144-154`). Requests validate enums and required strings
(`Dialogs.cs:61-80`, `:114-130`, `:163-169`; `MainView.cs:34-70`). No existing public file is
changed.

### 2. Threading, deduplication and ordering

`NotificationsApi` guards the tracked list and the in-flight map with one lock
(`Notifications.cs:156-160`, `:202-222`); a duplicate id is rejected before the host is called,
and an in-flight duplicate shares the first `TaskCompletionSource` (`:202-227`). The successful
publish adds the message and removes the reservation in the same critical section
(`:242-246`), then completes the shared result and raises `Changed` outside the lock
(`:248-250`). `DismissAsync` removes locally before the best-effort host call
(`Notifications.cs:300-324`). `ConfigureAwait(false)` is used at each await, and the result
records are immutable. The fake-host tests cover publish/track, ordinal dedupe, a concurrent
duplicate sharing one host request, failure retry and cancellation
(`tests/Playnite.Core.Tests/Hosting/NotificationsApiTests.cs:11-285`).

### 3. Cancellation and disposal

Pre-cancelled calls throw before the host is entered, and an in-flight reservation is removed
before the cancellation is rethrown (`Notifications.cs:197-198`, `:263-271`;
`Dialogs.cs:298-299`, `:310-312`; `MainView.cs:218-220`, `:229-231`). A cancelled publish is not
tracked and can be retried (`NotificationsApiTests.cs:130-166`). None of the three surfaces owns
an OS resource, so the absence of `IDisposable` is appropriate.

### 4. Dependency direction and UI isolation

The new product files are in `Playnite.Core` and use only Core models/queries plus the BCL
(`src/Playnite.Core/Hosting/MainView.cs:1-4`). The p5-007a adapter is in
`Playnite.Platform.DesktopIntegration` and maps only message/delivery types
(`NotificationServiceHost.cs:1-55`); it adds no Avalonia reference. `HostApis.Create` accepts
nullable seams and records one diagnostic per missing host (`HostApis.cs:38-63`). The degraded
startup, partial-registration and full-wiring paths are tested in
`tests/Playnite.Core.Tests/Hosting/HostApiTests.cs:8-59`.

### 5. Evidence and ownership

The change is additive and confined to the new hosting layer, its tests and the p6-006 unit
shard/ledger. The PR body and ledger cite linux-ci `44697258233` on validated head
`dc88ae4c93cf092711d0685b67f64ee4596f3fdb` with Core 2842/2842, Architecture 199/199 and Smoke
4/4, zero failures and zero skips; the PR currently reports 8/8 checks passing. No protected
oracle, baseline, PublicAPI or UI file is edited in the candidate.

## Non-blocking observations

- `MainViewApi.QueryGamesAsync` reports a completed empty list when a host returns a null game
  list (`src/Playnite.Core/Hosting/MainView.cs:226-227`), while `GetStateAsync` correctly reports
  a null state as `Failed` (`MainView.cs:180-184`). A host returning null violates its contract,
  but the inconsistency means a broken host looks like an empty view.
- `DialogsFactory` returns a host's null `MessageDialogResponse` unchanged
  (`Dialogs.cs:308`), so the caller sees a null instead of a `Failed` result. The other surfaces
  catch thrown host errors.
- `NotificationsApi` keeps `messages` and `inFlight` as unbounded collections
  (`Notifications.cs:158-160`); a caller publishing an unlimited number of distinct ids can grow
  memory without backpressure. This matches the v10 in-memory list, so it is not a port blocker.

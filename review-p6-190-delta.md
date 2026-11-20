# PR #190 focused delta review — post-review host hardening

- **Reviewed head:** `f339b37f8da74e547934afdb98857ab3ebcd3da3`
- **Delta range:** `5bf8ba8426548341254a626e40f1a4e32bb2ab78..f339b37f8da74e547934afdb98857ab3ebcd3da3`
- **delta.diff SHA-256:** `71dd89344b417d331becacbd49e0712e7b36f17fa9032f950387fe9e13708546`
  (the supplied file is byte-for-byte `git diff 5bf8ba84..f339b37`: 25 files, 2059 insertions,
  78 deletions)
- **CI-validated code head:** `0d56d3ed8ffc6eefd613c34e1030d1459baf29f1`; `f339b37` adds one
  ledger line on top (`git diff --name-status 0d56d3e..f339b37` touches only
  `docs/port/units/p6-006.ledger.d/190-notifications-dialogs-mainview.tsv`).
- **Verdict: two blocking P2 findings.** The requested dismiss-all aggregation and the
  publish-path cancellation hardening are otherwise implemented and tested.

## Blocking findings

### P2-1 — `DismissAllAsync` dereferences the host result outside the guarded region; a null host response escapes as an unhandled `NullReferenceException` and destroys the aggregate

- **Where:** `src/Playnite.Core/Hosting/Notifications.cs:410,430`
- **Failure:** `result` is assigned at `:413` inside the `try`, but `result.WasPresented` is
  evaluated at `:430`, after both `catch` blocks. A host that returns a null
  `NotificationHostResult` (for example `Task.FromResult<NotificationHostResult>(null!)`) makes
  `:430` throw `NullReferenceException` out of `DismissAllAsync`. By then `messages.Clear()` has
  already run at `:391`, and the `failures`/`diagnostics` accumulated for earlier ids are never
  returned. Concrete sequence: two tracked messages, `DismissAllAsync()` with a default token; the
  host returns `Presented` for `"one"` and `null` for `"two"`; the caller gets an NRE instead of a
  `NotificationDismissAllResult`, the first id's outcome is dropped, `"two"` is never reported as
  a failure, and both messages are gone from the tracked list (only `"one"` was actually
  dismissed by the host).
- **Why this is a delta defect:** the delta adds exactly this guard in three sibling surfaces —
  `DialogsFactory.ShowMessageAsync` (`Dialogs.cs:309-312`), `SelectFileAsync` (`:351-354`),
  `SelectStringAsync` (`:408-411`), `MainViewApi.QueryGamesAsync` (`MainView.cs:227-231`) — and
  the publish path evaluates `result.WasPresented` inside its `try` (`Notifications.cs:277`).
  `DismissAsync` also survives a null result, but only incidentally: its `:361` check is inside
  the `try`, so the NRE becomes a `"the notification host failed to dismiss it: Object reference
  not set..."` result (`:374-379`). The two dismiss paths therefore disagree on the same host
  behaviour, and the one the delta rewrote is the one that throws.
- **Fix:** treat null as an aggregated failure, e.g. inside the try or immediately after it:
  `if (result is null) { failures.Add(new NotificationDismissFailure(id, NotificationHostStatus.Failed, "The notification host returned no result.")); diagnostics.Add($"{id}: the notification host returned no result"); continue; }`.
  Add a test using `Task.FromResult<NotificationHostResult>(null!)`, mirroring
  `ANullHostResponseIsReportedAsFailed` in `DialogsFactoryTests`.

### P2-2 — A host-originated `OperationCanceledException` is rethrown as caller cancellation, aborting the aggregate and skipping the remaining messages

- **Where:** `src/Playnite.Core/Hosting/Notifications.cs:415-418`
- **Failure:** the catch rethrows every `OperationCanceledException` without checking whether
  `cancellationToken` was cancelled. A host that applies its own per-call timeout
  (`CreateLinkedTokenSource`, `CancelAfter`) or surfaces a transport `TaskCanceledException`
  therefore makes `DismissAllAsync` throw while the caller passed `default`. The
  `failures`/`diagnostics` built for earlier ids are discarded, every later id is never sent to
  the host, and all of them were already removed from the tracked list at `:391`. That directly
  contradicts the new contract on `INotificationsAPI.DismissAllAsync` ("Every host outcome is
  aggregated", `Notifications.cs:168-173`) and the unit doc
  (`docs/port/p6-006-notifications-dialogs-mainview.md`, "aggregates every host failure").
- **Why this is a delta defect:** the delta rewrote this hunk to aggregate host outcomes, and the
  same repository already distinguishes caller cancellation from a provider-internal cancellation
  (`docs/port/p6-005-metadata-service.md`: a provider cancellation the caller did not request is
  mapped to `provider-timeout`, only caller cancellation propagates). The current test
  (`NotificationsApiTests.cs:432`) only covers the caller-cancelled case, so the gap is invisible.
- **Fix:** rethrow only caller cancellation:
  `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { failures.Add(...Status: Failed, exception.Message, Threw: true); diagnostics.Add(...); continue; }`
  followed by `catch (OperationCanceledException) { throw; }`. Add a test where the host throws
  `new TaskCanceledException("host timeout")` under an uncancelled token and assert
  `Failures` carries the id and the remaining ids are still attempted. Align `DismissAsync`
  (`:370-373`), which currently reports the same situation to a non-cancelling caller as
  cancellation.

## Verification of the requested points

### 1. Dismiss-all aggregates every host outcome without dropping it — pass for returned results and non-cancellation exceptions

- The loop (`Notifications.cs:407-440`) now inspects the returned `NotificationHostResult`:
  `Presented` is skipped (`:430-433`); `Unavailable`/`Failed` become a
  `NotificationDismissFailure` carrying the id, status and diagnostic (`:435-439`); thrown
  exceptions become `Threw: true` entries and the loop continues (`:419-428`).
- `Failures`, `FailedCount` and `HasFailures` are exposed on the result (`:130-141`), and the
  aggregate diagnostic names each failure (`:444`). `Dismissed` still counts messages removed
  from the tracked list, as documented (`docs/port/p6-006-...md`, "Dismissed still counts the
  messages removed").
- Mixed, all-success and returned-failure cases are covered by
  `DismissAllReportsOnlyTheMessagesTheHostDidNotDismiss` (`NotificationsApiTests.cs:387`),
  `DismissAllWithEveryHostSuccessReportsNoFailures` (`:413`) and
  `DismissAllSurfacesAHostFailureThatDoesNotThrow` (`:363`, both `Failed` and `Unavailable`).
- Null results and host-originated cancellation are the two gaps above (P2-1, P2-2).

### 2. Cancellation is honoured and does not mask results — pass for caller-requested cancellation

- Pre-cancelled calls throw before the list is touched (`:385`), mid-loop cancellation is checked
  before each host call (`:409`) and during the host call (`:415-418`), and caller cancellation is
  not converted into a `Failed` entry — it propagates as `OperationCanceledException`, matching
  the publish contract.
- Publish cancellation is hardened in this delta: `WaitAsync(cancellationToken)` (`:274-276`)
  stops a caller wait even when the host ignores the token, the `inFlight` reservation is removed
  in the catch (`:298-306`), and a duplicate joiner can cancel its own wait without cancelling
  the owner (`:258-260`). Covered by `AHostThatIgnoresCancellationDoesNotPoisonTheId` (`:204`)
  and `ADuplicateJoinerCanCancelItsOwnWaitWithoutCancellingTheOwner` (`:79`).
- Caller cancellation still discards an aggregate already built for earlier ids and leaves the
  whole list cleared (`DismissAllCancellationThrowsAfterClearingTheLocalList`, `:432`). That
  clear-first ordering predates `5bf8ba84` and the delta does not change it; it is not counted
  against this delta, but the P2-2 fix narrows the same window for host-originated cancellation.

### 3. The host API contract stays stable — pass

- `NotificationDismissAllResult` keeps its positional `(int Dismissed, string? Diagnostic = null)`
  shape and only adds an init-only `Failures` plus computed `FailedCount`/`HasFailures`;
  `INotificationsAPI` member signatures are unchanged.
- `HostOperationResult` is deleted from `HostOperations.cs`. It was introduced inside this same
  PR: it does not exist at the merge base `141cf1c` (`src/Playnite.Core/Hosting/` does not exist
  there), and `git grep HostOperationResult` returns no references at either `5bf8ba84` or
  `f339b37`. Removing dead, unshipped surface is not a breaking change.
- `HostApis.Create` now reports the three startup diagnostics from `HostApis.*` constants
  (`HostApis.cs:67-77`) instead of reusing the per-call service constants; the text changed but
  the `HostApiSet` shape did not. `NotificationServiceHost.DismissAsync` likewise drops the
  "removed locally" suffix, and `NotificationServiceHostTests` was updated with it.
- No Abstractions `PublicAPI` or `api-baseline` path is touched; the file list contains only
  `Playnite.Core.Hosting`, the desktop adapter and their tests.

### 4. Tests fail on reversion — pass for the implemented fixes

- Reverting the returned-result aggregation makes
  `DismissAllSurfacesAHostFailureThatDoesNotThrow` and
  `DismissAllReportsOnlyTheMessagesTheHostDidNotDismiss` fail (empty `Failures`, null aggregate
  diagnostic); reverting the exception capture makes
  `DismissAllReportsHostFailuresButStillRemovesEveryMessage` fail.
- Reverting `WaitAsync` hangs `AHostThatIgnoresCancellationDoesNotPoisonTheId`; reverting the
  joiner `WaitAsync` fails `ADuplicateJoinerCanCancelItsOwnWaitWithoutCancellingTheOwner`;
  moving `RaiseChanged` back inside the try fails
  `AThrowingChangedSubscriberIsNotMisreportedAsAHostFailure`.
- Null dialog and main-view responses are pinned by `ANullHostResponseIsReportedAsFailed` and
  `ANullHostGameListIsReportedAsFailed`.
- **Gap:** no test exercises a null `DismissAllAsync` host result or an uncancelled
  host-originated `OperationCanceledException`; those two untested paths are why P2-1/P2-2 pass
  the suite.

### 5. No unrelated change from the PR — pass

- Of the 25 delta files, nine are `p6-005`/metadata files and are byte-identical to main's
  `b46bef6` (`#189`), which this branch merged: `git diff b46bef6..f339b37 -- src/Playnite.Core/Metadata tests/Playnite.Core.Tests/Metadata docs/port/p6-005-metadata-service.md docs/port/units/p6-005.queue.tsv docs/port/units/p6-005.ledger.d` is empty.
  They are the merge import, not PR intent.
- The four aggregate files (`docs/port/PORT-STATUS.md`, `ledger.tsv`, `queue.tsv`,
  `ownership.paths`) are the post-merge `port-index` regeneration for that same `#189` merge; the
  only `queue.tsv` row they touch is p6-005, and the two ledger rows are its rows. None of them
  records p6-006 product work.
- The remaining 12 files are exactly the p6-006 unit: five hosting files, the `Playnite.Platform`
  adapter, four hosting test files, the p6-006 contract doc and the p6-006 queue/ledger shard
  entries. No protected oracle, baseline or schema path.
- `f339b37` is docs-only over the CI-validated code head `0d56d3e`, so the cited
  `ci_run=44702814169` still covers every changed code and test file.

## Non-blocking notes

1. `p6-006` reports a host dismissal that returns `Unavailable` both as a successful local
   dismissal (`DismissAsync` returns `NotificationDismissStatus.Dismissed`, `:361-368`) and as a
   failure (`DismissAllAsync` `Failures`). This is documented ("Dismissed still counts the
   messages removed from the tracked list"), but a caller reading `IsDismissed` cannot tell that
   the host kept showing the notification, and `NotificationServiceHost.DismissAsync` always
   returns `Unavailable` (`NotificationServiceHost.cs:50-56`), so every real dismiss-all now
   reports `HasFailures == true`. Worth an explicit CLI/shutdown policy before Phase 7 consumes
   it.
2. `HostApiTests` only asserts that diagnostics contain the words "notification host", "dialog
   host" and "main-view host" (`HostApiTests.cs:13-16`), so the new diagnostic wording is not
   pinned. Not a delta blocker.
3. `DismissAllAsync` raises `Changed` before any host call (`:399`); a throwing subscriber
   therefore aborts the operation after `messages.Clear()` and before a single dismissal. The
   publish path documents that subscriber exceptions propagate (correct); the dismiss path has no
   equivalent note or test.

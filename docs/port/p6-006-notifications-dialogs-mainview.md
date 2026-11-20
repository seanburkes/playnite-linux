# p6-006 — notifications, dialogs and main-view API

`Playnite.Core.Hosting` is the headless, host-facing API layer for the surfaces the shell and
the CLI need: notifications, modal dialogs and main-view queries. Every surface is an interface
over an injectable host, so a fake host drives the contract tests and the future Avalonia shell
supplies the real one. The layer references no UI toolkit: the dependency graph is
`Playnite.Core → Playnite.SDK.Abstractions` plus the BCL. `Playnite.Platform` adapts the
p5-007a desktop notification service without adding Avalonia to the graph.

## Why Core, not Abstractions

The v10 interface names and shapes are retained where they map cleanly, but this is the headless
v11 surface, not a byte-for-byte v10 clone: sort direction is `bool SortDescending`, grouping is
Core `LibraryGroupBy`, and `SortOrder` is the Core stored-field subset. The full v10 key sets and
the UI extension points are p6-008/Phase 7 work. The public Abstractions files
(`PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `api-baseline/`) are protected by the oracle
gate, so `p6-008` must move this surface into Abstractions and freeze it with the reviewed
ApiCompat report. Putting the service surface in Core now keeps this unit additive. `IMainViewAPI`
also needs the Core `Game` model, which Abstractions may not reference. The
`sdk-002`/`sdk-003`/`sdk-004` parity rows stay `planned` until that move.

## Notification API

| v10 | v11 |
| --- | --- |
| `INotificationsAPI.Messages`, `Count` | Same, as an immutable snapshot; `Changed` signals updates. |
| `Add(message)` | `PublishAsync(message)`: the host presents first, then the message is tracked. |
| Duplicate id ignored | `Duplicate`; the host is not called again. An in-flight duplicate shares the first result. |
| `Remove(id)` | `DismissAsync(id)`: local removal, then a best-effort host dismiss. |
| `RemoveAll()` | `DismissAllAsync()`: removes everything and aggregates every host failure. |

Deduplication is by ordinal `Id`, as in v10. A duplicate never calls the host, never raises
`Changed` and leaves the first message's text intact. A failed or unavailable presentation does
not enter the list, so a retry is not suppressed. Cancellation before or during presentation
throws `OperationCanceledException` and releases the in-flight reservation. With no host,
publishing returns `Unavailable` with `NotificationsApi.MissingHostDiagnostic`; nothing is
tracked and startup continues. Dismissal only sees tracked messages: a dismiss that arrives while
the first publish of that id is still in flight returns `NotFound`, and that publish completes and
is tracked when the host answers.

A `Changed` handler is caller code and is raised after the publish result is fixed. A throwing
handler is not converted into a host failure; it propagates to the publisher after the message is
already tracked.

`DismissAllAsync` never drops a host outcome. Every message the host did not dismiss — whether
the host returned `Unavailable`/`Failed` or threw — is recorded as a
`NotificationDismissFailure` (id, status, diagnostic, `Threw`) in
`NotificationDismissAllResult.Failures`, and the aggregate diagnostic names each one.
`Dismissed` still counts the messages removed from the tracked list; `HasFailures` and
`FailedCount` summarize the host outcome.

## Dialog API

`IDialogsFactory` keeps the v10 entry points (`ShowMessageAsync`, `ShowErrorAsync`, `SelectFileAsync`,
`SelectFolderAsync`, `SaveFileAsync`, `SelectStringAsync`) but is asynchronous and typed. `IDialogHost`
is the only seam; the request records validate their inputs, `DialogsFactory` rejects a null
request, delegates, and maps a missing host or a null host response to `Unavailable`/`Failed` with
a diagnostic. The service never calls a blocking UI method and never waits on a dispatcher.
Cancellation before or during the host call rethrows. `FileDialogRequest` carries the v10 filter,
initial directory, multi-select and overwrite flags; `MessageDialogRequest` carries the v10
button/image sets.

## Main-view API

`IMainViewAPI` exposes the query data from v10 `IMainViewAPI` that does not need an Avalonia
type: active desktop and fullscreen views, a Core `SortOrder` plus `bool SortDescending`, a Core
`LibraryGroupBy`, the current selection and the games visible in the library view. `IMainViewHost`
supplies the snapshot; the API copies the lists so a host cannot mutate an answer after it
returns. Missing host, a null state and a null game list are `Unavailable`/`Failed` results, never
startup exceptions.

## Degraded startup

`HostApis.Create(notificationHost, dialogHost, mainViewHost)` accepts null for every seam and
returns a `HostApiSet` with the three working surfaces plus one clear diagnostic per missing
host. It never throws. This is the intended CLI and pre-shell startup path (this unit adds the
seam, not the product wiring): a caller logs `HostApiSet.Diagnostics` and still gets usable,
explicitly degraded surfaces.

## Tests

`tests/Playnite.Core.Tests/Hosting`:

| File | Covers |
| --- | --- |
| `NotificationsApiTests` | Publish/track, id dedupe, concurrent duplicate sharing, missing host, unavailable/failed host retry, cancellation, dismiss, dismiss-all failure aggregation (thrown and returned results), `Changed`. |
| `DialogsFactoryTests` | Message/error/file/folder/save/string request and response mapping, missing host, host failure, cancellation, validation. |
| `MainViewApiTests` | State and game queries, missing host, host failure, cancellation, snapshot copying, enum validation. |
| `HostApiTests` | Degraded startup, partial registration, full wiring, diagnostics. |
| `NotificationServiceHostTests` | p5-007a mapping: Info/Normal, Error/Critical, delivered and transport-duplicate presentations, unavailable detail, dismissal, cancellation. |

The fake hosts are in `Fakes.cs`; no test needs a desktop session, D-Bus, a window or a dialog.

## Verification

- `dotnet build tests/Playnite.Core.Tests -c Release -warnaserror`: 0 warnings, 0 errors.
- `dotnet test tests/Playnite.Core.Tests --filter-namespace Playnite.Core.Tests.Hosting`:
  63 passed, 0 failed, 0 skipped.
- Full `Playnite.Core.Tests`: 2,872 passed, 0 failed, 0 skipped.
- `Playnite.Architecture.Tests`: 199 passed, 0 failed, 0 skipped (banned constructs, SDK
  boundary and dependency graph unchanged).

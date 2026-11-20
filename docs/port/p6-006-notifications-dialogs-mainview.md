# p6-006 — notifications, dialogs and main-view API

`Playnite.Core.Hosting` holds the concrete headless services for the surfaces the shell and the
CLI need: notifications, modal dialogs and main-view queries. The contracts, host seams and DTOs
live in `Playnite.SDK.Abstractions` (`Playnite.SDK` and `Playnite.SDK.Models`), so a plugin can
consume them with a reference to Abstractions alone. Every surface is an interface over an
injectable host, so a fake host drives the contract tests and the future Avalonia shell supplies
the real one. The layer references no UI toolkit: the dependency graph is
`Playnite.Core → Playnite.SDK.Abstractions` plus the BCL. `Playnite.Platform` adapts the
p5-007a desktop notification service without adding Avalonia to the graph.

## Where the contracts live

`INotificationsAPI`, `IDialogsFactory`, `IMainViewAPI`, their `I*Host` seams and every
request/response DTO are public members of `Playnite.SDK.Abstractions`. They were moved out of
`Playnite.Core` in this unit, not redefined: the v10 names and shapes are retained where they map
cleanly, but this is the headless v11 surface, not a byte-for-byte v10 clone. `SortOrder` and
`LibraryGroupBy` moved to `Playnite.SDK.Models` so `MainViewState` keeps every shell field, and
`IMainViewAPI` no longer names the Core `Game` type: it returns `Playnite.SDK.Models.IGameView`,
which `Playnite.Core.Models.Game` implements. The concrete services (`NotificationsApi`,
`DialogsFactory`, `MainViewApi`, `HostApis`) stay in Core because they are host implementations,
not contracts.

`PublicAPI.Unshipped.txt` declares the moved surface; `PublicAPI.Shipped.txt` and the api-baseline
are unchanged because the move is a compatible addition to the assembly. The reviewed
`p6-006-apicompat-*` files record the v6→v7 comparison for the moved members: 98 intended
`CP0002`/`CP0006`/`CP0009` breaks, each classified `adapted`, with the suppression file replaying
to a clean ApiCompat exit 0. The complete 62-type freeze and the remaining v6 surface stay with
p6-008.


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
the host returned `Unavailable`/`Failed`, returned no result, threw, or canceled its own
dismissal — is recorded as a `NotificationDismissFailure` (id, status, diagnostic, `Threw`) in
`NotificationDismissAllResult.Failures`, and the aggregate diagnostic names each one. A
host-originated `OperationCanceledException` is a host failure and does not stop the aggregate;
only cancellation of the caller's token aborts the remaining messages. `Dismissed` still counts
the messages removed from the tracked list; `HasFailures` and `FailedCount` summarize the host
outcome.

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
type: active desktop and fullscreen views, an abstraction-level `SortOrder` plus
`bool SortDescending`, an abstraction-level `LibraryGroupBy`, the current selection and the games
visible in the library view. Games are exposed as `IGameView` (`Id`, `Name`), so the contract has
no dependency on the Core database model; `IMainViewHost` supplies the snapshot and the API copies
the lists so a host cannot mutate an answer after it returns. Missing host, a null state and a
null game list are `Unavailable`/`Failed` results, never startup exceptions.


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
| `NotificationsApiTests` | Publish/track, id dedupe, concurrent duplicate sharing, missing host, unavailable/failed host retry, cancellation, dismiss, dismiss-all failure aggregation (thrown, null, returned and host-canceled results), caller cancellation, `Changed`. |
| `DialogsFactoryTests` | Message/error/file/folder/save/string request and response mapping, missing host, host failure, cancellation, validation. |
| `MainViewApiTests` | State and game queries, missing host, host failure, cancellation, snapshot copying, enum validation. |
| `HostApiTests` | Degraded startup, partial registration, full wiring, diagnostics. |
| `NotificationServiceHostTests` | p5-007a mapping: Info/Normal, Error/Critical, delivered and transport-duplicate presentations, unavailable detail, dismissal, cancellation. |

The fake hosts are in `Fakes.cs`; no test needs a desktop session, D-Bus, a window or a dialog.
`tests/Playnite.Core.Tests/SdkContracts/SdkHostContractTests` adds one compile-time fake for
`IGameView`, `INotificationHost`, `INotificationsAPI`, `IDialogHost`, `IDialogsFactory`,
`IMainViewHost` and `IMainViewAPI`, exercising every member through the real Core service.
`tests/Playnite.Architecture.Tests/SdkHostSurfaceTests` asserts the moved types live in the
Abstractions assembly and compiles a sample plugin against that assembly alone (no Core, no
Avalonia), including the reshaped `IGameView` main-view surface.

## Verification

- `dotnet build src/Playnite.SDK.Abstractions -c Release -warnaserror`: 0 warnings, 0 errors.
- `dotnet build` on Core, Platform, Cli, Core.Tests and Architecture.Tests: 0 warnings, 0 errors.
- Full `Playnite.Core.Tests`: 3,047 passed, 0 failed, 0 skipped.
- `Playnite.Architecture.Tests`: 201 passed, 0 failed, 0 skipped (banned constructs, SDK
  boundary and dependency graph unchanged).
- ApiCompat v6→v7: 308 differences (210 `CP0001`, 82 `CP0002`, 15 `CP0006`, 1 `CP0009`),
  classified in `p6-006-apicompat-classification.tsv`; the 98 moved-member suppressions replay
  to exit 0. The final linux-ci run is cited in the unit ledger fragment and the PR.


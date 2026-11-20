# p6-006 — notifications, dialogs and main-view API

`Playnite.Core.Hosting` is the headless, host-facing API layer for the surfaces the shell and
the CLI need: notifications, modal dialogs and main-view queries. Every surface is an interface
over an injectable host, so a fake host drives the contract tests and the future Avalonia shell
supplies the real one. The layer references no UI toolkit: the dependency graph is
`Playnite.Core → Playnite.SDK.Abstractions` plus the BCL. `Playnite.Platform` adapts the
p5-007a desktop notification service without adding Avalonia to the graph.

## Why Core, not Abstractions

The v10 names are preserved so a later port can map them one-for-one, but the public Abstractions
files (`PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `api-baseline/`) are protected by the
oracle gate. `p6-008` owns the Abstractions freeze and the reviewed ApiCompat report. Putting the
moving service surface in Core now keeps this unit additive and lets `p6-008` freeze a surface
that no longer moves. `IMainViewAPI` also needs the Core `Game` model, which Abstractions may not
reference. The `sdk-002`/`sdk-003`/`sdk-004` parity rows stay `planned` until that freeze.

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
tracked and startup continues.

`DismissAllAsync` never drops a host outcome. Every message the host did not dismiss — whether
the host returned `Unavailable`/`Failed` or threw — is recorded as a
`NotificationDismissFailure` (id, status, diagnostic, `Threw`) in
`NotificationDismissAllResult.Failures`, and the aggregate diagnostic names each one.
`Dismissed` still counts the messages removed from the tracked list; `HasFailures` and
`FailedCount` summarize the host outcome.

## Dialog API

`IDialogsFactory` keeps the v10 entry points (`ShowMessage`, `ShowError`, `SelectFile`,
`SelectFolder`, `SaveFile`, `SelectString`) but is asynchronous and typed. `IDialogHost` is the
only seam; `DialogsFactory` validates the request, delegates, and maps a missing host to
`Unavailable` and a host exception to `Failed` with a diagnostic. The service never calls a
blocking UI method and never waits on a dispatcher. Cancellation before or during the host call
rethrows. `FileDialogRequest` carries the v10 filter, initial directory, multi-select and
overwrite flags; `MessageDialogRequest` carries the v10 button/image sets.

## Main-view API

`IMainViewAPI` exposes the query data from v10 `IMainViewAPI` that does not need an Avalonia
type: active desktop and fullscreen views, sort key and direction, grouping, the current
selection and the games visible in the library view. `IMainViewHost` supplies the snapshot;
the API copies the lists so a host cannot mutate an answer after it returns. Missing host and
host failure are `Unavailable` and `Failed` results, never startup exceptions.

## Degraded startup

`HostApis.Create(notificationHost, dialogHost, mainViewHost)` accepts null for every seam and
returns a `HostApiSet` with the three working surfaces plus one clear diagnostic per missing
host. It never throws. This is the CLI path and the pre-shell startup path: a caller logs
`HostApiSet.Diagnostics` and still gets usable, explicitly degraded surfaces.

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
  55 passed, 0 failed, 0 skipped.
- Full `Playnite.Core.Tests`: 2,846 passed, 0 failed, 0 skipped.
- `Playnite.Architecture.Tests`: 199 passed, 0 failed, 0 skipped (banned constructs, SDK
  boundary and dependency graph unchanged).

# p5-007a — tray, notifications and sleep inhibition

Scope: the platform half of W5 `p5-007`. One new namespace, `Playnite.Platform.DesktopIntegration`
(contracts) and `Playnite.Platform.Linux.DesktopIntegration` (implementation), so the parallel
p5-003/p5-004/p5-005 units cannot collide with these files.

## Behaviour contract

| Facility | Contract |
| --- | --- |
| Tray | `ITrayService.InstallAsync` probes for a StatusNotifierItem host, then presents through the UI-supplied `ITrayIconPresenter`. No host, no presenter, or a presenter failure returns a `TrayInstallation` with `IsHosted == false` and a `DesktopCapability` detail. It never throws for an environment failure. |
| Notifications | `INotificationService.ShowAsync` delivers through the first available backend, coalesces identical concurrent requests onto one delivery, suppresses an identical request inside the duplicate window (default 5 s), and returns `Unavailable` when every backend fails. An absent backend, a non-zero exit, or an exit of `-1` (timeout) is a recorded failure that falls through to the next backend; it is never success and never throws. The method is asynchronous; no caller waits synchronously on `notify-send` or `gdbus`. |
| Sleep inhibition | `ISleepInhibitionService.InhibitAsync` returns a lease even when no inhibitor is available. `SessionSleepInhibition` acquires before `BeginSessionAsync` returns (before the game starts), shares one lease across concurrent sessions, and releases before `EndSessionAsync` returns (after the game stopped). |

All three return a `DesktopCapability`. An unavailable facility is reported to the caller and
startup continues; nothing in this unit fails startup because a desktop service is missing.

## Session detection is explicit

`LinuxDesktopSessionProbe` applies this precedence, and records the deciding evidence:

1. `XDG_SESSION_TYPE=wayland` → Wayland.
2. `XDG_SESSION_TYPE=x11` → X11.
3. non-empty `WAYLAND_DISPLAY` → Wayland.
4. non-empty `DISPLAY` → X11.
5. otherwise → headless (`None`).

A Wayland session normally exports `DISPLAY` through XWayland, so the probe never infers X11
from `DISPLAY` when an explicit Wayland type or a Wayland display exists.

## Fallback matrix

| Facility | Primary | Fallback order | Unavailable behaviour | Wayland | X11 |
| --- | --- | --- | --- | --- | --- |
| Tray | `org.kde.StatusNotifierWatcher` host, probed via `gdbus` → `busctl` → `dbus-send` | Presenter supplied by the UI (Avalonia `TrayIcon` is the intended presenter) | `TrayInstallation.IsHosted == false`; capability detail names the missing host/presenter | StatusNotifierItem only; there is no XEmbed tray | StatusNotifierItem preferred; XEmbed-only window managers degrade, because XEmbed is not implemented in v1 |
| Notifications | `notify-send` (libnotify client of `org.freedesktop.Notifications`) | direct `org.freedesktop.Notifications.Notify` through `gdbus` | `NotificationDeliveryState.Unavailable` with per-backend detail; no exception | same session-bus mechanism | same session-bus mechanism |
| Sleep inhibition | `systemd-inhibit --what=sleep --mode=block` with a `cat` stdin lifeline | none | lease `IsHeld == false`, capability `Unavailable`; the game still starts | session-agnostic (logind) | session-agnostic (logind) |

`LinuxDesktopIntegrationDiagnostics` performs the read-only startup probe. Availability there
means the tool is installed; an installed tool can still fail at call time, which the delivery
result reports. The tray probe is skipped entirely for a headless session.

## Notification deduplication

The key is `(id, title, body, urgency, expiry)` joined with U+001F. Identical requests that
overlap in time are coalesced onto one in-flight delivery and both callers receive that
delivery's result: both `Delivered`, or both the same `Unavailable` reason. A request that
repeats after a successful delivery inside the window returns `SuppressedDuplicate` without
calling a backend. Distinct ids or changed text are not duplicates. A failed delivery removes
the key so a retry is not suppressed. The clock is injected as `TimeProvider`; the window is
configurable and zero disables post-completion deduplication (concurrent overlap still shares
one delivery). The deduplication dictionary is pruned on each call and never grows without
bound.

## Inhibitor lifetime and the no-leak argument

`systemd-inhibit` is started with stdin redirected and the hold command `cat`. The lock lives as
long as the pipe write end is open:

- **Normal stop:** `EndSessionAsync` disposes the lease, which closes stdin; `cat` reads EOF and
  exits; `systemd-inhibit` releases the block and exits. Release is awaited before the stop call
  returns. A `ProcessExit` hook closes the same pipe, so an orderly shutdown that forgets the
  coordinator still releases. End-of-session teardown is deliberately non-cancellable: an
  already-cancelled stop still removes the session and releases the last lease.
- **Cancel:** a cancelled acquire kills the started process tree and throws before a lease is
  returned. A cancel after acquisition closes the pipe, removes the session, and rethrows.
- **Cancelled stop:** `EndSessionAsync` is teardown and is not cancellable. An already-cancelled
  token still removes the session and releases the last lease; if another session survives, the
  lease stays held for it.
- **Crash:** process death closes the pipe in the kernel. `cat` sees EOF with no managed code
  running, so the lock is released even for SIGKILL. This is why the hold command is `cat`
  rather than an inhibitor process whose only owner is the app.
- **No leak:** `SleepInhibitionLease` releases its handle at most once; the coordinator owns one
  lease, releases it when the last session ends, and `ShutdownAsync` releases it unconditionally.

`StdinHoldTests.ClosingTheLifelineReleasesTheChildWithoutAnyOtherCleanup` exercises the crash
path directly with `/bin/cat`: it closes only the pipe and observes the child exit before any
dispose runs.

## Integration points owned by later units

- Desktop supplies the Avalonia `TrayIcon` presenter through `ITrayIconPresenter`.
- The game session lifecycle calls `BeginSessionAsync` before launch and `EndSessionAsync` after
  the tracked process stops (p5-004 owns the tracker).
- Desktop logs `IDesktopIntegrationDiagnostics.DiagnoseAsync()` at startup; a report row is not a
  startup error.

## Evidence

- `dotnet build tests/Playnite.Core.Tests -c Release -warnaserror`: zero warnings.
- `dotnet test --filter-namespace Playnite.Core.Tests.DesktopIntegration`: 65 cases pass, zero
  skips.
- Full `Playnite.Core.Tests`: 2076 cases pass, zero skips.
- Fakes cover tray host/presenter, notification backends, inhibitor backends, command execution,
  executable lookup, session evidence and the clock. Real-process tests use `/bin/cat`, `/bin/echo`,
  `/bin/sleep` and `/bin/false` for the stdin lifeline and command timeout/cancel paths; no test
  requires a desktop session, D-Bus, logind or a tray host.

## Process dependency note

`System.Diagnostics.Process` is the approved argument-list process API. This unit deliberately
does not reference `System.ComponentModel.Win32Exception` (which lives in
`Microsoft.Win32.Primitives`): process-start and best-effort-kill failures are caught with a
filtered `Exception` instead. The product dependency closure therefore stays free of the Win32
shim without changing any architecture guard.



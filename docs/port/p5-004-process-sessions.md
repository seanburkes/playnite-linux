# p5-004 — process launcher and game session tracker

Status: implemented in `src/Playnite.Platform/Sessions/` and
`src/Playnite.Platform.Linux/Sessions/`, with tests in
`tests/Playnite.Core.Tests/Sessions/`. Oracle: `docs/formal/GameSession.tla` plus the
process-tracking evidence in `spikes/steam-process-tracking/`.

## Scope

This unit owns the platform contracts and the Linux implementation for:

- starting one game process from an argv vector;
- observing that process tree until it exits;
- manual stop;
- cancellation and application-shutdown teardown with no orphaned child.

Single instance, XDG, protocol registration, tray and notifications are p5-005/p5-007 and are
not part of this unit. Steam attribution (`SteamAppId`, Proton wrapper selection, Flatpak host
visibility) is p5-008; this launcher takes an already-chosen executable and arguments.

## Contracts

`Playnite.Platform.Sessions`:

- `LaunchRequest` — absolute executable, argv vector, optional absolute working directory.
  There is no command-line string member. Shell interpreters (`sh`, `bash`, `dash`, `zsh`,
  `ksh`, `mksh`, `pdksh`, `csh`, `tcsh`, `fish`, `yash`, `rbash`, `busybox`) are rejected, as
  are relative executables and NUL bytes. Arguments are copied verbatim into
  `ProcessStartInfo.ArgumentList`; `UseShellExecute` is explicitly `false`.
- `IProcessLauncher` — `StartAsync`, `IsRunningAsync`, `StopAsync`.
- `IGameProcess` — the immutable `ProcessIdentity` and `StartedAt`.
- `IProcessObserver` / `ProcessSnapshot` — an unattributed process-table sample and its
  completeness flag. Command lines and environments are never read.
- `IGameSessionTracker` — `StateChanged`, `State`, `IsDegraded`, `Playtime`, `TrackAsync`,
  `StopAsync`, `IAsyncDisposable`.
- `SessionOptions` — poll interval, stop timeout, and
  `ExitObservationSupported = false` for the manual fallback.
- `ISessionDelay` — the poll-wait seam used by cancellation tests.

`Playnite.Platform.Linux.Sessions` implements the process system, the `/proc` reader, the
observer, and the launcher. `IProcessSystem`/`IProcessHandle` and `IProcStatReader` are the
seams used by the fake-process and cancellation tests; their production implementations use
`System.Diagnostics.Process` and `/proc` respectively.

## Process identity rule

`ProcessIdentity` is `(pid, starttime)` where `starttime` is raw `/proc/<pid>/stat` field 22
(clock ticks since boot), never a wall-clock time and never a formatted `Process.StartTime`.
The identity is valid only within one boot and PID namespace.

Every liveness decision reads the current stat row and requires both the PID and the exact
start ticks to match. Therefore:

- a recycled PID with a different start time is a different process and ends the tracked
  identity rather than prolonging it;
- a zombie or dead state (`Z`, `X`, `x`) is not running;
- an incomplete or unreadable sample is `unknown` and never `stopped`;
- descendants are found by a fixed-point closure over parent PIDs independent of enumeration
  order and retained across reparenting.

## Session state machine

The state names are exactly the `GameSession` model's set: `Idle`, `Starting`, `Running`,
`Stopping`, `Stopped`, `Cancelled`, `Failed`. The terminal states are exactly `Stopped`,
`Cancelled`, `Failed`.

| Formal action | Implementation |
| --- | --- |
| `Start` / `LaunchOk` | `TrackAsync` emits `Starting` then `Running` for the successfully launched handle. |
| `Stop` / `StopOk` | Manual `StopAsync`, or an observed complete empty tree, emits `Stopping` then `Stopped`. |
| `CancelLaunch` / cancellation | Caller cancellation or tracker disposal emits `Cancelled`; the owned process is signalled and reaped before the terminal event returns. |
| `Crash` | An observer or stop failure emits `Failed`; the owned process is still signalled and reaped. |
| `NoOrphanProcess` | Every terminal path calls `IProcessLauncher.StopAsync`; `LinuxProcessLauncher.DisposeAsync` terminates every process it launched. |
| `OneSessionPerGame` | One tracker instance owns at most one active `TrackAsync`; a second concurrent or completed session call is rejected. |
| `PlaytimeNonDecreasing` | Playtime is measured from the `Running` timestamp with `TimeProvider` and frozen at the terminal transition. |

Natural exit is modeled as `Running → Stopping → Stopped`, so the observable sequence still
matches the formal stop actions. The tracker never reports `Stopped` for an unavailable
observation.

## Manual fallback and degradation

The platform reports degradation explicitly instead of pretending a session ended:

- `SessionOptions.ExitObservationSupported = false` makes the tracker enter `Running`, emit a
  `SessionStateChanged` notice with `IsDegraded = true` and a reason, and wait for
  `StopAsync`. It does not call the observer and never emits `Stopped` on its own.
- An incomplete `ProcessSnapshot` similarly emits a degraded running notice, retains the known
  identities, and keeps polling; a later complete sample can clear the notice. Only a complete
  sample with no live known identity emits `Stopped`.
- An observer or stop failure is terminal `Failed`, not a fabricated normal stop.

Manual stop is always available, including while degraded, and uses the same kill-and-reap
path as cancellation.

## Cancellation matrix

Every await in the production path has a cancellation test. Tests assert the resulting terminal
state and, where a process exists, that it is gone.

| Await point | Production path | Test |
| --- | --- | --- |
| `identity read after spawn` | `LinuxProcessLauncher.StartAsync` | `Start_cancelled_during_identity_read_kills_the_spawned_process` |
| `stat read in IsRunning` | `LinuxGameProcess.IsRunningAsync` | `IsRunning_cancelled_during_stat_read_propagates_without_killing` |
| `exit wait in Stop` | `LinuxGameProcess.TerminateAsync` | `Stop_cancelled_during_exit_wait_still_kills_and_reaps_the_process` |
| `/proc` row read in a snapshot | `LinuxProcessObserver.SnapshotAsync` | `Observer_snapshot_cancelled_during_proc_read_propagates` |
| poll delay | `GameSessionTracker.TrackAsync` | `Cancelling_the_tracker_during_the_poll_delay_reports_cancelled_and_stops_the_process` |
| observer snapshot | `GameSessionTracker.TrackAsync` | `Cancelling_the_tracker_during_an_observer_snapshot_reports_cancelled_and_stops_the_process` |
| launcher stop inside manual stop | `GameSessionTracker.StopAsync` | `Cancelling_manual_stop_still_signals_and_reports_stopped_without_an_orphan` |
| track completion wait in disposal | `GameSessionTracker.DisposeAsync` | `Disposing_while_running_cancels_the_session_and_stops_the_process` |
| pre-cancelled launch observation | `GameSessionTracker.TrackAsync` | `Cancelling_before_the_first_observation_terminates_the_launched_process_as_cancelled` |
| no-exit-observation wait | `GameSessionTracker.TrackAsync` | `Manual_stop_is_required_and_reported_when_exit_observation_is_unavailable` |

The stop path deliberately does not take the caller's token for the identity check or the
signal: once a stop is requested, cancellation can only bound the wait, never skip the kill.
If the wait is cancelled, the process is reaped without the token before the cancellation
propagates.

## No-orphan proof

The logic tests use a fake launcher/process and observer. The no-orphan proof additionally uses
a real, short-lived child (`/usr/bin/sleep 30`, falling back to `/bin/sleep`) launched through
`LinuxProcessLauncher`, and an actual `/proc` check by `(pid, start ticks)`:

- `Real_stop_leaves_no_child` — manual stop reaches `Stopped`, `IsRunningAsync` is false, and
  `/proc/<pid>/stat` is absent or has different start ticks;
- `Real_cancel_leaves_no_child` — cancelling `TrackAsync` reaches `Cancelled` and the child is
  gone;
- `Real_tracker_disposal_leaves_no_child` — disposal reaches `Cancelled` and the child is gone;
- `Real_launcher_shutdown_leaves_no_child` — launcher disposal (application shutdown) leaves no
  child;
- `Arguments_containing_shell_metacharacters_are_passed_literally` — a real `/usr/bin/touch`
  receives a single argument containing `;` and a space; the literal filename is created and
  no shell-split command runs.

`StartAsync` rejects a handle whose identity cannot be captured because the child already
exited; a killed start is reaped by the launcher before the exception propagates.

## Known limitations

- `Process.Kill(entireProcessTree: true)` is SIGKILL on Linux; this unit guarantees no orphan,
  not graceful game shutdown.
- `/proc` coverage is the caller's PID namespace. Flatpak host PID visibility remains
  `UNKNOWN` (p1-010) and is p5-008's concern.
- Attribution of a Steam launch (which process tree is the game) is not decided here; the
  caller passes the attributed root identity.
- The tracker owns one session per instance; multi-game session management belongs to the
  service that creates trackers.

## Verification

Local Release, warnings as errors:

- `Playnite.Core.Tests`: 2048 passed, 0 failed, 0 skipped (37 new session tests).
- `Playnite.Architecture.Tests`: 188 passed, 0 failed, 0 skipped.
- `build_port_index.py check --shards-only --against origin/main` is run before delivery.

Real linux-ci on the final pushed head is the delivery gate; no protected path, baseline,
aggregate, or formal-model file is changed by this unit.

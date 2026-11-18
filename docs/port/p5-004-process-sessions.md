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
  `ksh`, `mksh`, `pdksh`, `csh`, `tcsh`, `fish`, `yash`, `rbash`, `busybox`) are rejected as
  defense in depth, as are relative executables and NUL bytes. The security property does not
  depend on that denylist: `LinuxProcessStartInfoFactory` copies arguments into
  `ProcessStartInfo.ArgumentList` and never sets `ProcessStartInfo.Arguments`; a test asserts
  that shape directly.
- `IProcessLauncher` — `StartAsync`, `IsRunningAsync`, `StopAsync` (root only), the tracked-tree
  `StopAsync` overload, and `IAsyncDisposable`. Disposal is the teardown reachable through the
  interface so application shutdown can always reap launched processes. The Linux
  implementation launches through `setsid` in its own process group/session and kills that group
  on every stop, cancellation, disposal and shutdown path.
- `IGameProcess` — the immutable `ProcessIdentity` (PID, raw start ticks, PGID) and `StartedAt`.
- `IProcessObserver` / `ProcessSnapshot` — an unattributed process-table sample, its table
  completeness, and the PIDs of individually unreadable rows. Command lines and environments
  are never read.
- `IGameSessionTracker` — `StateChanged`, `State`, `IsDegraded`, `Playtime`, `TrackAsync`,
  `StopAsync`, `IAsyncDisposable`.
- `SessionOptions` — poll interval, stop timeout, exit-confirmation sample count, and
  `ExitObservationSupported = false` for the manual fallback.
- `ProcessOrphanedException` — a failed start whose kill/wait could not prove the spawned
  process was reaped.
- `ProcessSurvivorException` — a stop that found a tracked identity outside the reaped group
  still running, or could not prove the group empty.
- `ISessionDelay` — the poll-wait seam used by cancellation tests.

`Playnite.Platform.Linux.Sessions` implements the process system, the `/proc` reader, the
observer, and the launcher. `IProcessSystem`/`IProcessHandle`, `IProcessStartInfoFactory` and
`IProcStatReader` are the seams used by fake-process, start-shape and cancellation tests; their
production implementations use `System.Diagnostics.Process` and `/proc` respectively.

## Process identity rule

`ProcessIdentity` is `(pid, starttime, pgid)` where `starttime` is raw `/proc/<pid>/stat`
field 22 (clock ticks since boot) and `pgid` is field 5. Neither is a wall-clock time or a
formatted `Process.StartTime`. The identity is valid only within one boot and PID namespace.

The launcher waits until the spawned root has entered its own process group (`PGID == PID`
from `setsid`) before accepting the identity. If that cannot be established, the start fails
and is reaped rather than killing an inherited group.

Every liveness decision reads the current stat row and requires both the PID and the exact
start ticks to match. Therefore:

- a recycled PID with a different start time is a different process and cannot be mistaken for
  the game;
- a zombie or dead state (`Z`, `X`, `x`) is not running;
- descendants are found by process-group membership, a fixed-point closure over parent PIDs,
  and a root-PID anchor for a child row that still names the root after the root row vanished;
  group membership survives reparenting and covers a child that was never observed while its
  parent was alive;
- an unreadable row degrades that entry only. It makes the decision unknown only when its PID is
  the root or an already-tracked identity; an unrelated unreadable row is ignored;
- table-level enumeration failure is unknown and never `stopped`.

## Session state machine

The state names are exactly the `GameSession` model's set: `Idle`, `Starting`, `Running`,
`Stopping`, `Stopped`, `Cancelled`, `Failed`. The terminal states are exactly `Stopped`,
`Cancelled`, `Failed`.

| Formal action | Implementation |
| --- | --- |
| `Start` / `LaunchOk` | `TrackAsync` emits `Starting` then `Running` for the successfully launched handle, unless a stop or terminal state raced the launch. |
| `Stop` / `StopOk` | Manual `StopAsync` (including during `Starting`), or a confirmed observed tree end, emits `Stopping`, then reaps, then emits `Stopped`. |
| `CancelLaunch` / cancellation | Caller cancellation or tracker disposal reaps and then emits `Cancelled`. |
| `Crash` | An observer or stop failure reaps and then emits `Failed`, or stays in a degraded `Running` state when reap cannot be proven. A child that left the group is reported through `ProcessSurvivorException` rather than silently folded into a clean stop. |
| `NoOrphanProcess` | Every terminal transition is preceded by a successful tracked-tree stop: the launcher signals the whole process group, signals tracked identities, waits for exit, scans the group for survivors, and verifies tracked identities are gone. A failed reap or survivor never emits a terminal state. |
| `OneSessionPerGame` | One tracker instance owns at most one active `TrackAsync`; a second concurrent or completed session call is rejected. |
| `PlaytimeNonDecreasing` | Playtime is measured from the `Running` timestamp with `TimeProvider` and frozen at the terminal transition. |

Natural exit is modeled as `Running → Stopping → Stopped`, after the reap proves the tracked
tree is gone. Stop attempts are serialized by one gate, so concurrent `StopAsync`, disposal and
cancellation cannot race duplicate transitions or skip a kill.

A throwing `StateChanged` subscriber is contained per handler: it cannot kill the process,
abort a transition, or skip stop cleanup.

## Observation and degradation

The platform reports degradation explicitly instead of pretending a session ended:

- A root that has never been observed is `unknown`, not `stopped`. A complete empty sample
  before the first root or descendant observation emits a degraded `Running` notice and keeps
  polling; it never infers a terminal state from an unobserved exit.
- `SessionOptions.ExitObservationSupported = false` makes the tracker enter `Running`, emit a
  degraded notice with a reason, and wait for `StopAsync`. It never calls the observer and never
  emits `Stopped` on its own.
- An incomplete sample or a relevant unreadable row emits a degraded notice, retains the known
  identities, and keeps polling; a later complete sample clears the notice.
- `ExitConfirmationSamples` (default 2) requires consecutive complete samples with no live
  known identity before a natural stop is considered. A single torn sample cannot end a session.
- A stop failure that cannot be proven reaped emits a degraded `Running` notice and leaves the
  session non-terminal. In the manual fallback, `TrackAsync` returns instead of waiting forever;
  `StopAsync` throws the failure and the caller may retry.
- A natural-exit stop failure is reported the same way: no `Stopped` event is emitted after a
  failed reap.

Manual stop is always available, including while degraded, and uses the same tracked-tree
kill-and-reap path as cancellation. A successful `StopAsync` proves the root and every known
descendant are no longer running.

## Cancellation matrix

Every await in the production path has a cancellation test. Tests assert the resulting terminal
state and, where a process exists, that it is gone.

| Await point | Production path | Test |
| --- | --- | --- |
| `identity read after spawn` | `LinuxProcessLauncher.StartAsync` | `Start_cancelled_during_identity_read_kills_the_spawned_process` |
| `stat read in IsRunning` | `LinuxGameProcess.IsRunningAsync` | `IsRunning_cancelled_during_stat_read_propagates_without_killing` |
| `exit wait in Stop` | `LinuxProcessLauncher.StopAsync` | `Stop_cancelled_during_exit_wait_still_kills_and_reaps_the_process` |
| `/proc` row read in a snapshot | `LinuxProcessObserver.SnapshotAsync` | `Observer_snapshot_cancelled_during_proc_read_propagates` |
| poll delay | `GameSessionTracker.TrackAsync` | `Cancelling_the_tracker_during_the_poll_delay_reports_cancelled_and_stops_the_process` |
| observer snapshot | `GameSessionTracker.TrackAsync` | `Cancelling_the_tracker_during_an_observer_snapshot_reports_cancelled_and_stops_the_process` |
| launcher stop inside manual stop | `GameSessionTracker.StopAsync` | `Cancelling_manual_stop_still_signals_and_reports_stopped_without_an_orphan` |
| track completion wait in disposal | `GameSessionTracker.DisposeAsync` | `Disposing_while_running_cancels_the_session_and_stops_the_process` |
| pre-cancelled launch observation | `GameSessionTracker.TrackAsync` | `Cancelling_before_the_first_observation_terminates_the_launched_process_as_cancelled` |
| stop requested while still `Starting` | `GameSessionTracker.StopAsync` | `Stop_during_starting_reaches_stopped_and_stops_the_process`; `Real_stop_during_starting_leaves_no_child` |
| cancelled-stop retry failure | `GameSessionTracker.StopAsync` | `Cancelled_stop_reaps_before_reporting_failed` |
| no-exit-observation wait | `GameSessionTracker.TrackAsync` | `Manual_stop_is_required_and_reported_when_exit_observation_is_unavailable` |
| manual-fallback stop failure | `GameSessionTracker.StopAsync` | `Manual_fallback_stop_failure_terminates_with_a_degraded_running_outcome` |
| concurrent stop calls | `GameSessionTracker.StopAsync` | `Concurrent_stop_calls_are_serialized` |
| natural-exit stop failure | `GameSessionTracker.TrackAsync` | `Natural_exit_stop_failure_is_reported_without_a_false_stopped` |

The stop path deliberately does not take the caller's token for the identity checks or the
signals: once a stop is requested, cancellation can only bound the wait, never skip the kill.
If the wait is cancelled, the tracked identities are reaped without the token before the
cancellation propagates.

## No-orphan proof

The logic tests use a fake launcher/process and observer. The no-orphan proof additionally uses
real children launched through `LinuxProcessLauncher` and actual `/proc` checks by
`(pid, start ticks)`:

- `Real_stop_during_starting_leaves_no_child` — a manual stop raced while the tracker is still
  `Starting` reaches `Stopped` and the child is gone;
- `Real_stop_leaves_no_child` — manual stop reaches `Stopped`, `IsRunningAsync` is false, and
  `/proc/<pid>/stat` is absent or has different start ticks;
- `Real_cancel_leaves_no_child` — cancelling `TrackAsync` reaches `Cancelled` and the child is
  gone;
- `Real_tracker_disposal_leaves_no_child` — disposal reaches `Cancelled` and the child is gone;
- `Real_launcher_shutdown_leaves_no_child` — disposal through `IProcessLauncher` (application
  shutdown) leaves no child;
- `Real_root_exit_keeps_a_tracked_child_and_stop_reaps_it` — a real `python3` root forks a
  child in the same process group and exits; the tracker stays `Running` on the reparented
  child, and the final stop reaps the whole group;
- `Real_child_reparented_before_the_first_snapshot_is_still_contained` — a real child is
  reparented away from the root before the tracker's first sample but remains in the root's
  group; the test proves the reparenting and the unchanged group membership, the tracker stays
  `Running` while the child lives, and the final stop reaps the whole group before reporting
  `Stopped`; a PID-only tracker would report a clean `Stopped` while the child survives;
- `Real_escaped_child_is_reported_as_a_survivor_not_silently_ignored` — a real child calls
  `setsid()`, leaving the contained group; the stop raises `ProcessSurvivorException`, keeps the
  session degraded and non-terminal, and the test reaps the survivor manually before retrying;
- `Arguments_containing_shell_metacharacters_are_passed_literally` — a real `/usr/bin/touch`
  receives a single argument containing `;` and a space; the literal filename is created and no
  shell-split command runs;
- `Failed_start_reports_the_orphan_when_kill_and_wait_fail` — a real spawned process with an
  injected kill/wait failure surfaces `ProcessOrphanedException` with its PID instead of
  swallowing the orphaning, then the test reaps the child for cleanup.

A failed start always attempts kill and wait; if the process is still running afterwards, the
launcher throws `ProcessOrphanedException` instead of only rethrowing the original start error.

## Known limitations

- `Process.Kill(entireProcessTree: true)` is SIGKILL on Linux; this unit guarantees no orphan,
  not graceful game shutdown.
- `/proc` coverage is the caller's PID namespace. Flatpak host PID visibility remains
  `UNKNOWN` (p1-010) and is p5-008's concern.
- Attribution of a Steam launch (which process tree is the game) is not decided here; the
  caller passes the attributed root identity.
- Process-group containment is the no-orphan boundary. A child that calls `setsid` or is
  reparented into a different group **before observation** is out of scope for the guarantee;
  a tracked identity that escapes is reported as a survivor, not silently ignored. A child
  spawned and reparented between samples without ever naming the root or sharing its group is
  likewise not attributable from `/proc` alone. Reparenting to init (or another subreaper) while
  the child stays in the launch's process group does **not** leave the boundary: the group
  membership keeps it contained even when the tracker never observed it, as covered by
  `Real_child_reparented_before_the_first_snapshot_is_still_contained`.
- The tracker owns one session per instance; multi-game session management belongs to the
  service that creates trackers.

## Verification

Local Release, warnings as errors:

- `Playnite.Core.Tests`: 2283 passed, 0 failed, 0 skipped (66 session tests).
- `Playnite.Architecture.Tests`: 188 passed, 0 failed, 0 skipped.
- `build_port_index.py check --shards-only --against origin/main` is run before delivery.

Real linux-ci on the final pushed head is the delivery gate; no protected path, baseline,
aggregate, or formal-model file is changed by this unit.

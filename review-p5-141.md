# Review — p5-008c2 follow-up (PR #141)

- Reviewed candidate: `/home/sean/src/firstmate/data/p5-141/candidate.diff`
- **sha256: `093524fc6193a182c0d5e4fde7049895d71bebd34a9000a8fbb6537bdc40581b`**
- PR: https://github.com/seanburkes/playnite-linux/pull/141
- Reviewed head: `fc3ebd8` (code head `83f4c14`; later commits docs-only)
- Scope: this diff only. No builds, no other units, no other reviewers' findings.

Verdict: **one blocking P2; no P1.** The clean-stop hole is closed and the code never
signals the escaped process, but the fix makes the *whole* process sample incomplete for an
unrelated process's group change, which can abort a launch or a stop.

## P2 — an unrelated process's group change aborts launch/stop

`src/Playnite.Platform.Linux/p5-008/LinuxExternalProcessHost.cs:69-83` sets
`complete = false` for **every** row whose re-read PGID changed. The host has no tracked set:
`LinuxProcessObserver` enumerates every numeric `/proc` entry (`LinuxProcessObserver.cs:42-66`)
and the host re-reads every one of them. A PGID change by a process that has nothing to do with
the Steam group therefore degrades the entire sample, although the tracked game row is intact.

Concrete interleaving (the window is the remainder of the `/proc` pass, hundreds of rows):

```
observer pass reads Q:  pid=Q start_ticks=1000 pgid=100     (Q is unrelated to the game)
Q calls setsid()/setpgid()
host identity re-read:  pid=Q start_ticks=1000 pgid=200
=> SnapshotAsync: complete = false, Q's row retained
```

Consequences (all fail-closed, but user-visible and immediate):

- `src/Playnite.Steam/p5-008/SteamProcessLauncher.cs:95-99` — the baseline snapshot is checked
  once; the launch aborts with `UNKNOWN: baseline process coverage is incomplete; Steam was
  not launched.` while the game's group is untouched.
- `src/Playnite.Steam/p5-008/SteamProcessLauncher.cs:275-279` — `StopAsync` throws
  `SteamAttributionUnknownException` on the *first* incomplete sample, before any signal and
  without the `StopTimeout` retry used for unreadable game identities
  (`SteamProcessLauncher.cs:305-318`). A single unrelated `setsid` during the stop scan leaves
  the game running with zero signals.
- `src/Playnite.Steam/p5-008/SteamProcessLauncher.cs:193-197` — `IsRunningAsync` throws instead
  of reporting the running game; `GameSessionTracker.cs:422-425` marks the session degraded on
  every poll that catches the race.
- The old code returned `IsComplete = true` for the same input (the unrelated row was dropped),
  so this is a regression introduced by the change, not pre-existing behavior.

This also contradicts the sample contract in `src/Playnite.Platform/Sessions/ProcessSnapshot.cs:7-9`:
"an unrelated unreadable row cannot turn a good sample into unknown."

Suggested direction: make the instability conditional on identities the caller tracks (or keep
the retained row without the global `complete = false` and let the launcher's existing
escaped/continuity checks decide). If `IsRunningAsync` is changed to rely on the escaped check,
it must still distinguish a retained new-group identity from an exited game (line 199 currently
uses full identity equality). Alternatively, retry an incomplete stop sample until `StopTimeout`
instead of throwing on the first one.

## What the fix does get right

- The reported hole is closed for the tracked game: the re-read identity is retained
  (`LinuxExternalProcessHost.cs:77-82`), so a row cannot silently disappear while the process
  lives; the launcher's escaped check (`SteamProcessLauncher.cs:210-217`) names the escape and
  `StopAsync` cannot produce a terminal report from that sample.
- A changed start tick is still treated as PID recycle, and a vanished row as exit evidence.
- The new tests exercise the intended seam and fail on the old behavior: the host test would see
  a complete empty snapshot, and the launcher test would get a clean `group empty` report with
  zero signals.
- The `GroupRejection` comment now matches `ClassifySurvivors` (`SteamLaunchEvidence.cs:169-179`,
  `SteamProcessLauncher.cs:418-464`): an unreadable member needs observed parent evidence.
- No `p5-004`, protected-path, aggregate, baseline, ownership-map, or `legacy/` changes. The
  cited linux-ci run `44400284691` is completed/success on `83f4c14`, matching the fragment.

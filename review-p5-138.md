# Review — p5-008c (PR #138)

- PR: https://github.com/seanburkes/playnite-linux/pull/138
- PR head: `7fc57ff`; code head: `17d923a2d0904eb1de211a750524da95169ab388`
- Reviewed candidate diff: `/home/sean/src/firstmate/data/p5-138/candidate.diff`
- **Reviewed sha256 digest: `b4b6357ce9cde3f3f7705c32a817f4047f0e35bc5684a1a4cd49f52b95c4c6f1`**
- Scope: candidate diff only. No other unit, no builds, no repository edits, no merge.
- Line numbers below are candidate-file line numbers (the diff applied to the PR base).

**Verdict: not ready to merge.** Two blocking P2 findings. The core torn-evidence fix
(completeness before the empty-group shortcut, the unreadable-known-identity guard, the
stop-loop retry, survivor classification, signal reordering and the DEGRADED harness path) is
otherwise sound and is covered by tests that fail on the old behaviour.

## P2-1 — a same-snapshot PGID change is silently truncated, so `StopAsync` can still return a clean "group empty" report with zero signals

**Files:** `src/Playnite.Platform.Linux/p5-008/LinuxExternalProcessHost.cs:56-60` (row dropped,
`complete` left true) consumed by `src/Playnite.Steam/p5-008/SteamProcessLauncher.cs:283-290`
(escaped check) and `:320-325` (clean stop).

**Concrete failure.** `LinuxExternalProcessHost.SnapshotAsync` re-reads each enumerated row's
`/proc/PID/stat` and only keeps the entry when both start ticks *and* PGID still match
(lines 56-60). When start ticks match but the PGID changed, the row is dropped and `complete`
stays `true` — only the exception path at lines 51-55 sets `complete = false`. The launcher's
escaped check (`SameProcess(known, row.Identity)` plus PGID comparison) can never see that row,
`GameMembers`/`GroupMembers` are empty, and `SteamProcessLauncher.StopAsync` takes the
`games.Length == 0 && members.Length == 0` branch (lines 320-325) and returns
`SteamStopReport(false, "Attributed game identities gone; observed group empty", [], [])`.
`GameSessionTracker` then reaches `Stopped`, zero signals were sent, and the game is still
running in its new process group.

**Failing interleaving (concrete).** A game is attributed as pid 22, start ticks 2, pgid 20.
It calls `setsid()`/`setpgid(0, 0)` after `LinuxProcessObserver.SnapshotAsync` has recorded
pid 22/pgid 20 but before the host's per-PID `stats.ReadAsync(22)` in the same
`host.SnapshotAsync` call. The observer→host window covers the exe and stat reads for every
entry enumerated before pid 22, so it is millisecond-scale, not theoretical. The host re-read
returns start ticks 2 (match) and pgid 22 (mismatch), line 56-57 fails, the row is dropped at
line 60, and `complete` remains true. `StopAsync` samples once, so there is no debounce: the
clean report above is produced on that first sample. (The tracker's natural-exit path needs
`ExitConfirmationSamples = 2` and the next snapshot sees the moved row as escaped, so the
natural path is not the exposed one — the one-shot stop is.)

**Why this blocks this PR.** The change's stated invariant is "A sample that cannot prove the
group state is never a terminal fact" and "Unknown coverage, identity discontinuity or
survivors cannot produce a successful stop report"
(`docs/port/p5-008-identity-stop.md:48`, `:65`). This sample contains a proven identity
discontinuity (same pid/start ticks, different PGID) yet is treated as a complete, empty group,
and the existing table row "an observed child escaped/reparented into another group →
UNKNOWN/survivor" is bypassed. The host gap is pre-existing, but the PR edits this same file
(`SignalCoreAsync`) and its new contract claims the stronger invariant, so the PR cannot ship
claiming it.

**Fix.** In the host, when `row.StartTimeTicks == entry.Identity.StartTimeTicks` but
`row.ProcessGroupId != entry.Identity.ProcessGroupId`, either keep the entry with the re-read
identity so the launcher's `SameProcess` escaped check fires, or set `complete = false`. Add a
seam test at the host/launcher level for a PGID change between the observer read and the host
re-read (the `FakeHost` in `SteamLaunchTests` returns rows directly and cannot express this).

## P2-2 — the rewritten `GroupRejection` contract comment contradicts the survivor classifier added by the same PR

**Files:** `src/Playnite.Steam/p5-008/SteamLaunchEvidence.cs:172-176` versus
`src/Playnite.Steam/p5-008/SteamProcessLauncher.cs:418-464` (`ClassifySurvivors`),
`tests/Playnite.Core.Tests/p5-008/SteamLaunchTests.cs:375-392`, and
`tools/p5-008/SteamAcceptance/Program.cs:158`.

**Concrete failure.** The comment says a live member whose executable is unreadable is "an
unidentified survivor requiring manual inspection, never an asserted Steam helper." The same PR
classifies an unreadable member as an identified helper when its parent is an owned identity
(`ClassifySurvivors` fixpoint), the new test
`Unreadable_survivor_with_observed_steam_parent_is_an_identified_helper` asserts
`HelperSurvivors == [20, 24]` for a null-exe member 24, and the harness prints
`IDENTIFIED HELPER SURVIVOR ... runtime/parent evidence`. The authoritative table row
(`docs/port/p5-008-identity-stop.md:30`) is correct — "an unreadable member stays unidentified
until parent evidence identifies it" — so only this public-policy comment is wrong. A
maintainer implementing or consuming `GroupRejection` per this comment would never expect
`HelperSurvivors` to contain an identity whose executable the host could not read.

**Fix.** Reword lines 173-176 to match the table row: an unreadable member stays unidentified
until runtime-path or observed Steam-owned parent evidence identifies it; it is never signalled
either way. Remove the "never an asserted Steam helper" sentence.

## Checked and found sound

- `SnapshotAsync` now checks `IsComplete` before the empty-group shortcut and before the
  unreadable-known-identity guard; both torn shapes are non-terminal. The unreadable guard is
  not bypassed by the tracker's use of `known`.
- The stop-loop retry cannot produce a terminal report: it either retries or throws
  `ProcessSurvivorException` at the deadline, never a clean report; a real exit still resolves
  on the next complete sample. Zombies are `IsRunning == false`, so a dead game row cannot wedge
  the retry.
- Survivor classification partitions every member; a null-exe member with no infrastructure
  path and no owned parent lands in `UnidentifiedSurvivors` with `IsDegraded == true` and
  "manual inspection required"; a clean report requires `members.Length == 0`, so a known
  survivor cannot accompany `IsDegraded == false` (subject to P2-1's truncation).
- Signal order pin → stat identity → exe re-read → SIGTERM is correct for the stated threat:
  the new seam test fails on the old ordering, `execve` preserves pid/start/PGID, and pidfd
  prevents redirecting to a recycled PID. No PID fallback.
- The harness prints `DEGRADED` and returns non-zero instead of `PASS` for a degraded report;
  `105600` is honestly labelled `Unknown` selection with a Linux executable, and the
  Proton/Windows-binary stop shape remains NOT PROVEN.
- No p5-004 edits, no protected paths, no skips/Ignores, no banned APIs, no merge.

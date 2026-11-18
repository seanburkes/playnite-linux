# p5-008b — Steam attribution window and game-only stop

Task/branch `fm/p5-008b`, declared `Port-Unit: p5-008` by firstmate ruling
2026-11-18 (the existing paths belong to p5-008). This supersedes the attribution-window
and Steam-group cleanup sections of `p5-008-steam-launch.md`, not p5-004.
Oracle: `legacy/source/Playnite/Common/ProcessStarter.cs:70–81` (URL launch only);
Linux policy: `08-STEAM-SPEC.md` §§5–7 and firstmate's game-only stop ruling.

## Window contract

- Default **240 seconds**, covering request completion and attribution, for native, Proton,
  and unknown selection alike. `SteamLaunchOptions.AttributionWindow` is configurable;
  shorter values are available to injected tests, not used by real acceptance.
- Poll with an awaited 250ms delay. Both cancellation deadline and delays use the injected
  `TimeProvider`; no wall-clock deadline or busy-loop. Progress is rate-limited to 5 seconds,
  with initial and terminal decisions always reported via `Progress`.
- Progress includes elapsed/window time, waiting for Steam request completion versus fresh
  installdir/prefix identities, and, for every snapshot that is not attributed, the candidate
  `pid`, `exe`, `pgid` and the exact rejecting predicate. An unattributed wait is therefore
  explained rather than merely reported: `candidates=1 [pid=… exe=… pgid=…]; rejected: …`.
- Expiry emits terminal UNKNOWN, the last snapshot decision, and asks for Steam manual Stop.
- Deadline/cancellation before attribution never authorizes cleanup of an unidentified game
  or client. No success is inferred from a request exit or runtime wrapper.

## Individual-stop map

| Identity evidence | Result |
| --- | --- |
| One or more fresh game executables under the canonical installdir/app prefix sharing one fresh group (Proton commonly runs both `Game.exe` in the prefix and `Game.bin` in the install dir) | Attribute the group's game identities; game-only SIGTERM for each |
| Fresh group containing `steam-launch-wrapper`, `pv-adverb`, `srt-bwrap`, `reaper`, `wineserver`, a launch shell, or a live member whose `/proc/PID/exe` the host cannot read (Steam runtime helper in another mount namespace) | Helpers tolerated as group evidence, excluded from the allowlist and never signalled; reported as helper survivors |
| Pre-existing group member, Steam client identity, candidates spanning two fresh groups, readable unrelated member, or incomplete coverage | UNKNOWN/degraded with the rejecting predicate named; zero signals on that sample |
| Known game identity changed executable, or an observed child escaped/reparented into another group | UNKNOWN/survivor; no speculative cross-group signal |
| Game remains after SIGTERM deadline | `ProcessSurvivorException`; no clean stop claim and no SIGKILL escalation |
| All attributed games gone, Steam helpers remain | Stopped/Cancelled, with `SteamStopReport.IsDegraded`, explicit reason and helper identities; no group-empty claim |
| All attributed games and group members gone | Clean stop report; empty-group proof |

The adapter allowlist retains only policy-verified game identities, not arbitrary tracker input.
Immediately before SIGTERM the Linux host opens a **pidfd**, checks the canonical executable
against the sampled expected game path, then rechecks PID/start ticks/PGID/running state.
Signals use the existing reviewed pidfd allowlist; no new interop, PID fallback, negative PID,
group signal, Steam-client signal or helper signal. Exec/group changes after the final check
remain a non-atomic `/proc` residual; pidfd prevents redirecting the signal to a recycled PID.

Each stop/cancel/dispose/shutdown re-scans for game identity exit and observed escaped descendants.
`StopReport` is adapter-owned degraded metadata, surfaced by the acceptance harness alongside
the tracker's Stopped/Cancelled event; the unchanged p5-004 tracker does not own Steam helpers.
Unknown coverage, identity discontinuity or survivors cannot produce a successful stop report.
Observed nongame descendants outside the group are reported, not killed. Unobserved children
that setsid/reparent before observation remain outside the proof: this is not cgroup containment.

**p5-004 is unchanged:** launcher-created setsid groups still use its exclusive containment
contract, identity-scoped SIGTERM to every proven member and complete group-empty proof.

## Verification

`SteamLaunchTests` covers helper-sharing versus pre-existing/client-sharing groups, tolerated
fresh unreadable members, Proton multi-identity groups, candidates spanning groups, ambiguity,
identity reuse/escape, helper survivors, failed stop, stop/cancel/launcher and tracker
disposal, per-snapshot rejection evidence, and a deterministic 140-second Proton appearance
plus 240-second expiry seam.
`LinuxPidFdSignalTests` retains PID-recycle proofs, adds exec/reuse scoped-signal rejection,
and stops a real copied short-lived child through `SteamProcessLauncher` using only pidfd SIGTERM.
The child inherits its group: the launcher never owns/signals that group.

Local full Core validation: **2370 passed, zero failures/skips**. Steam namespace: **309 passed,
zero failures/skips**. CI and owner-machine evidence belong to this PR's ledger fragment and
final-head verification comment. Until the real native + selected Proton run passes on that
head, criterion 2 remains **NOT PROVEN**. No Phase 5 exit or Flatpak proof follows from this task.

The consent-gated `scripts/p5-008/steam-acceptance.sh` retains its read-only default. With both
explicit consent flags, it reports window progress, runs native/Proton stop and cancellation,
disposes tracker/launcher, then asserts attributed games and observed descendants gone and
original client identities retained. It prints helper survivors instead of asserting an empty
Steam-owned group. No launch options, Steam config or prefixes are changed by the harness.

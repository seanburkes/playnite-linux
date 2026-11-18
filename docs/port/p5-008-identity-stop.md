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
- Progress includes elapsed/window time, waiting for Steam request completion versus unique
  fresh installdir/prefix identity, candidate count, incomplete coverage, and UNKNOWN pending
  safe attribution. Expiry emits terminal UNKNOWN and asks for Steam manual Stop.
- Deadline/cancellation before attribution never authorizes cleanup of an unidentified game
  or client. No success is inferred from a request exit or runtime wrapper.

## Individual-stop map

| Identity evidence | Result |
| --- | --- |
| Unique fresh game executable under canonical installdir or selected app prefix; fresh group containing only games/known helpers | Attribute game; game-only SIGTERM |
| Fresh group shared with `steam-launch-wrapper`, `pv-adverb`, `srt-bwrap`, `wineserver`, etc. | Helpers excluded from tracking/signals; group shape is not ownership |
| Pre-existing group/member, Steam client identity, multiple candidates, unreadable/unrelated member or incomplete coverage | UNKNOWN/degraded; zero signals on that sample |
| Known game identity changed executable or observed child escaped/reparented into another group | UNKNOWN/survivor; no speculative cross-group signal |
| Game remains after SIGTERM deadline | `ProcessSurvivorException`; no clean stop claim and no SIGKILL escalation |
| All attributed games gone, Steam helpers remain | Stopped/Cancelled, with `SteamStopReport.IsDegraded`, explicit reason and helper identities; no group-empty claim |

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

`SteamLaunchTests` covers fresh helper-sharing versus pre-existing/client-sharing groups,
ambiguity, identity reuse/escape, helper survivors, failed stop, stop/cancel/launcher and tracker
disposal, and a deterministic 140-second Proton appearance plus 240-second expiry seam.
`LinuxPidFdSignalTests` retains PID-recycle proofs, adds exec/reuse scoped-signal rejection,
and stops a real copied short-lived child through `SteamProcessLauncher` using only pidfd SIGTERM.
The child inherits its group: the launcher never owns/signals that group.

Local full Core validation: **2363 passed, zero failures/skips**. Steam namespace: **302 passed,
zero failures/skips**. CI and owner-machine evidence belong to this PR's ledger fragment and
final-head verification comment. Until the real native + selected Proton run passes on that
head, criterion 2 remains **NOT PROVEN**. No Phase 5 exit or Flatpak proof follows from this task.

The consent-gated `scripts/p5-008/steam-acceptance.sh` retains its read-only default. With both
explicit consent flags, it reports window progress, runs native/Proton stop and cancellation,
disposes tracker/launcher, then asserts attributed games and observed descendants gone and
original client identities retained. It prints helper survivors instead of asserting an empty
Steam-owned group. No launch options, Steam config or prefixes are changed by the harness.

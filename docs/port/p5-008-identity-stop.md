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
| Fresh group containing `steam-launch-wrapper`, `pv-adverb`, `srt-bwrap`, `reaper`, `wineserver`, a launch shell, or a live member whose `/proc/PID/exe` the host cannot read | Tolerated as group evidence, excluded from the allowlist and never signalled. A readable recognised runtime helper is an identified survivor; an unreadable member stays unidentified until parent evidence identifies it |
| Pre-existing group member, Steam client identity, candidates spanning two fresh groups, readable unrelated member, or incomplete coverage | UNKNOWN/degraded with the rejecting predicate named; zero signals on that sample |
| Known game identity changed executable, or an observed child escaped/reparented into another group | UNKNOWN/survivor; no speculative cross-group signal |
| Known game identity is still running but its executable is unreadable (torn `/proc` read) | Unverifiable, never an exit, a changed executable or a survivor: incomplete snapshot while tracking, retry until the stop deadline proves the stop unproven |
| Game remains after SIGTERM deadline | `ProcessSurvivorException`; no clean stop claim and no SIGKILL escalation |
| All attributed games gone, identified Steam helpers remain | Stopped/Cancelled, with `SteamStopReport.IsDegraded`, explicit reason and helper identities; no group-empty claim |
| All attributed games gone, a member without Steam/Proton evidence remains | Stopped/Cancelled, with `SteamStopReport.IsDegraded`, `UnidentifiedSurvivors`, and manual inspection required; no group-empty claim and no helper claim |
| All attributed games and group members gone | Clean stop report; empty-group proof |

The adapter allowlist retains only policy-verified game identities, not arbitrary tracker input.
Immediately before SIGTERM the Linux host opens a **pidfd**, rechecks PID/start ticks/PGID/running
state, then re-reads the canonical executable against the sampled expected game path. The
executable read is last because `execve` preserves pid/start/PGID, so an exec during the stat
read is still caught; a PID recycle after that final read cannot redirect the pinned signal.
Signals use the existing reviewed pidfd allowlist; no new interop, PID fallback, negative PID,
group signal, Steam-client signal or helper signal. Exec/group changes after the final check
remain a non-atomic `/proc` residual; pidfd prevents redirecting the signal to a recycled PID.

A sample that cannot prove the group state is never a terminal fact. `SnapshotAsync` returns an
incomplete snapshot with a reason when host coverage is incomplete, and also when a tracked game
identity is still running with an unreadable executable, before any empty-group shortcut. The
stop loop treats that unreadable executable as unverifiable: it retries instead of raising
*changed executable*, and fails with `ProcessSurvivorException` only when the stop deadline
proves the stop unproven. An exit therefore needs complete coverage and a readable game identity
for every live tracked row.

The host also binds every sampled row to a re-read stat identity. Different start ticks mean the
PID was recycled, so the sampled identity is gone. Equal start ticks in a changed process group
mean the same live process moved group (`setsid`) between the two reads: the host retains the
re-read identity, so the launcher's escaped-child check fires, and marks the sample incomplete.
Dropping that row left a complete, empty snapshot, which read as a natural exit and allowed a
clean "group empty" stop with zero signals while the game ran in its new group. A launcher that
sees this shape now returns an incomplete snapshot naming the escape and `StopAsync` refuses a
terminal report; it never signals the escaped process in its unrelated group.

A survivor is an identified Steam/Proton helper only with evidence: a recognised Steam/Wine/Proton
runtime executable path, or a parent identity observed as Steam-owned (baseline Steam client,
attributed game identity, or an already identified helper). `ExternalProcessSnapshot` carries no
namespace field, so namespace evidence is never assumed. Members without evidence are
`SteamStopReport.UnidentifiedSurvivors`, keep the report degraded, and require manual inspection.
`GroupRejection` applies the same contract while the game is still tracked: an unreadable live
member is tolerated group evidence and never signalled, but it is never labelled a helper there
either. Only the terminal survivor classifier may identify such a member, and only when the
observed parent is Steam-owned.

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
plus 240-second expiry seam. It also covers both torn-evidence triggers (an incomplete sample
with an empty group, and live members with no readable game identity) and survivor identity:
null-exe-only survivors stay unidentified, and parent evidence is required to call one a helper.
`LinuxPidFdSignalTests` adds the host-level `Same_start_ticks_in_a_changed_group_is_retained_and_incomplete`
and the launcher-level `Launcher_seam_a_group_change_between_reads_cannot_produce_a_clean_stop`
seam tests: equal start ticks in a new group must retain the re-read identity, make the sample
incomplete, and refuse both a terminal stop report and any signal.
`LinuxPidFdSignalTests` retains PID-recycle proofs, adds exec/reuse scoped-signal rejection,
including an exec that lands after the stat identity check and before the final executable read,
and stops a real copied short-lived child through `SteamProcessLauncher` using only pidfd SIGTERM.
The child inherits its group: the launcher never owns/signals that group.

Local full Core validation: **2376 passed, zero failures/skips**. Steam namespace: **318 passed,
zero failures/skips**. CI and owner-machine evidence belong to this PR's ledger fragment and
final-head verification comment. The real 105600 flow ran a Linux binary with `Unknown` selection
evidence, so a Proton/Windows-binary stop shape remains **NOT PROVEN**, and no Phase 5 exit or
Flatpak proof follows from this task.

The consent-gated `scripts/p5-008/steam-acceptance.sh` retains its read-only default. With both
explicit consent flags, it reports window progress, runs native/Proton stop and cancellation,
disposes tracker/launcher, then asserts attributed games and observed descendants gone and
original client identities retained. It prints identified and unidentified survivors separately,
and a degraded stop report prints `DEGRADED` and exits non-zero instead of `PASS`. No launch
options, Steam config or prefixes are changed by the harness.

## Owner-machine acceptance

Consented run on final code head `ae52afd` (later PR commits are docs-only): native `770100`
attributed in 4.4s (stop) and 2.5s (cancel); Proton `105600` attributed in 3.8s (stop) and
3.0s (cancel). Every flow printed a single game identity plus a tolerated unreadable
`pv-adverb`, sent pidfd SIGTERM only to the game executable, retained the original Steam
client identities, reported the group empty, and passed the harness's no-orphan assertion.
No SIGKILL, group signal, client signal or helper signal occurred. Native `975370` and
`427520` were not used: both survive SIGTERM past the 5s stop deadline, so they remain
manual-Stop cases and are not offered as clean-stop evidence. Real linux-ci run
`44381409850` succeeded on that exact head.

Rerun after the p5-008c corrections, on code head `17d923a2d0904eb1de211a750524da95169ab388`
(later PR commits are docs-only), the owner's default Steam library as `--` library argument,
apps `770100` and `105600`, both consent flags: four flows, all `PASS`, every
`STOP REPORT degraded=False` with reason `Attributed game identities gone; observed group empty`,
exit 0. Attribution took 3.6s/2.7s native and 3.3s/3.3s for the second slot; each flow signalled
only the attributed game executable through pidfd. Selection evidence is `Unknown` for both apps,
so Steam owned selection: `105600` is the harness's second slot and ran `Terraria.bin.x86_64`,
which is not a Proton/Windows-binary shape and is not claimed as Proton proof. Local Core
validation on that head: **2376 passed, zero failures/skips**.

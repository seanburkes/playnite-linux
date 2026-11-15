# p1-007: bounded native/Proton tracking experiment

**Candidate evidence, not production platform code.**
This dependency-free Python/Linux experiment answers what a polling process-tree
tracker can and cannot establish. One bounded real native Steam acceptance run is
recorded in `real-acceptance.md`; the rest of this note is synthetic/owned-process
evidence. The experiment does not implement Flatpak host transport, production
root attribution, or environment inspection.

## Reproduce

From the repository root, with Python 3.10+ and Linux `/proc`:

```sh
TMPDIR=/tmp/opencode timeout 45s /usr/bin/python3 -B spikes/steam-process-tracking/test_tracking.py
```

The command runs all tests, with no exclusions or skips. Non-Linux is an error.
No packages, restore, .NET solution, Steam setup, or elevated privileges are needed.
The live tests start only this directory's `controlled_child.py`, using argument
lists and `shell=False` (the subprocess default). They read only the PIDs returned
by those owned children. Each child has a four-second input wait bound; ancestors
send stop down the pipe chain and reap their immediate children. Every communicate
and handshake has a timeout. Emergency kill targets only an owned `Popen` handle,
never a process discovered by the tracker. Final assertions require every recorded
identity to disappear, including zombies. The outer timeout is a final guard,
not the normal cleanup mechanism; children also exit on pipe EOF.

## Experiment contract / p1-010 handoff

`tracker.py` deliberately separates snapshot acquisition from graph observation:

- `Identity(pid, start_ticks)`: `/proc/<pid>/stat` field 22, **raw clock ticks**, not
  UTC or elapsed seconds. Valid only within the same boot and PID namespace.
  Any future transport must bind its snapshot stream to a stable boot/namespace
  context and reset observation if that context changes. Do not compare a sandbox
  PID with a host PID, or ticks with rounded wall-clock timestamps.
- `Process(identity, parent_pid, state)`: parent is stat field 4; state is field 3.
  `parse_stat` handles spaces and parentheses in comm. No executable name or
  `SteamAppId` is treated as sufficient identity or launch attribution.
- `Snapshot(processes, complete)`: a tuple of process rows from **one context**.
  `complete=False` means unreadable/malformed coverage; return `unknown` and retain
  prior identities, never infer stopped. Namespace loss/transport failure must
  likewise be unknown, not an empty successful response.
- `Session(root)`: the root is **already attributed by the caller**, not the PID
  of a `steam -applaunch` command assumed to be the game. Root attribution, app ID,
  launch request ID, and disambiguating concurrent launches remain unimplemented.
  The observer follows the fixed-point descendant closure independent of row
  ordering, retains observed descendants across reparenting, and rejects reused
  PIDs by start ticks. Zombies and dead processes are not running.
- `update`: `unobserved` until the attributed root is seen; `running` while a known
  live identity remains; `stopped` only after observed identities disappear from
  complete samples. Here `stopped` means **known tree ended**, not authoritative
  proof a Steam game ended. A stopped observer cannot reattach.
- `wait(read, cancel, timeout)`: bounded polling with a caller-provided **bounded,
  synchronous** reader. Cancellation before or after a read returns `cancelled`,
  not stopped, and takes precedence over expiry. The monotonic deadline is checked
  before each read and after it, before applying the observation. At or after the
  deadline, return `timeout` without changing prior identities or observation state.
  It cannot interrupt a blocked reader; a remote host transport must enforce its
  own I/O timeout/cancellation.
  `close()` is idempotent and prevents further use; it never signals a game.

The local `snapshot(pids)` adapter takes a **predeclared controlled PID set**. It
does not scan `/proc` or discover arbitrary game children. Its completeness flag
describes that set only. A production discovery adapter needs a separate coverage
contract; a sequence of stat reads is not an atomic snapshot, and a permission or
enumeration gap cannot be called complete merely because some rows were readable.
The graph observer assumes supplied rows correctly describe the parent relationship;
PID/start-time keys alone cannot prove a parent link across a racy multi-read scan.
No Flatpak host helper or host launch/probe is implemented here (p1-010 owns that).

Firstmate's p1-010 handoff uses `child_identity={pid,start_ticks,namespace}` and
`caller_proc_visibility=match|mismatch|denied|inconclusive`. The first two fields map
directly to this experiment's `Identity`; retain/validate `namespace` at the stream
boundary, not by dropping it and comparing arbitrary local PIDs. A non-`match`
visibility result cannot authorize using caller `/proc` as host evidence. Use a
validated host snapshot source or report unknown instead. A namespace value alone
does not establish boot continuity or session attribution. This is a field/semantics
handoff only: no cross-spike integration or real sandbox result was executed here.

## Evidence (2026-11-14)

Executed locally with `/usr/bin/python3` 3.14.10 on Linux 7.3.0-3-cachyos:
**21 tests passed, zero skipped** with the command above. Full revised command output
is retained under `/home/sean/src/firstmate/data/p1-007-fix/tests.txt`; the original
16-test run is under `/home/sean/src/firstmate/data/p1-007/tests.txt`.
The test source is the reproducible, repository-contained evidence. Real native
Steam launch/track/exit evidence for Terraria (105600) is in
`real-acceptance.md`; it is separate from the synthetic table below.

| Scenario | Evidence kind | Observation |
| --- | --- | --- |
| Native-shaped launcher → game | Live owned Python tree | Two distinct PID/start-tick identities; running then stopped after controlled exit; all reaped |
| Proton-shaped launcher → wrapper → wrapper → game | Live owned Python tree | Four identities tracked; cancellation/disposal leave tree alive; controlled shutdown removes all identities |
| Reversed enumeration order | Synthetic graph | Fixed point finds all four levels, unrelated root excluded |
| Parent exit and child reparented to PID 1 | Synthetic snapshots only | Previously observed child keeps the session running until it exits |
| PID reuse (root or child), zombie | Synthetic snapshots | Does not prolong known-tree lifetime |
| Unreadable/malformed coverage | Synthetic/local temporary stat data | Unknown does not erase prior identity; missing PID file is absent |
| Root appears after initial empty sample | Synthetic snapshots | Unobserved does not discard the attributed root before it appears |
| Launch handed to an existing Steam broker | Synthetic snapshots | An unrelated game tree is unobserved; launch completion cannot establish a session |
| Short-lived wrapper missed between polls | Synthetic snapshots | Demonstrates a false stop: reparented, never-seen game cannot be reconstructed |
| Cancellation, timeout, repeated disposal | Synthetic and owned live trees | No fake stopped result or game termination; disposed observation fails explicitly |
| Final sleep exhausts budget | Deterministic clock/reader | No read after expiry; prior identities retained; cancellation on wake takes precedence |
| Bounded read reaches/crosses deadline | Deterministic clock/reader | Expired stopped/running observations are discarded without mutating known identities or first-observation state; cancellation after read takes precedence |

Deadline regression reproduction: with a one-second budget, a bounded reader
returning an empty complete sample at 1.0 or 1.25 seconds previously returned
`stopped`; a final sleep consuming the budget also allowed an extra empty read.
Expiry is the trigger; the empty sample after an observed live tree makes the
wrong result visible. Expired live samples instead exposed identity mutation even
when the old result was already `timeout`. Changing only read duration to 0.5
seconds makes the same empty observation timely: `stopped` and cleared identities
are then correct. These comparisons are preserved as regressions. Before the fix,
the expanded 21-test suite reported five failing cases; after the fix all pass.
No test asserts that synchronous reads can be interrupted.

### Observations versus assumptions

**Observed:** the local kernel exposes stat identities/parent links for owned Python
processes, fixed-point tree tracking works for those supplied rows, and controlled
cleanup/cancellation works. Synthetic reparenting and broker cases expose where
tree-only attribution fails. A real native Steam launch of Terraria (105600) on
this host produced the expected reaper/runtime/launcher/game chain and a correct
`running` → `stopped` session; see `real-acceptance.md`.

**Unverified:** real Proton/Wine wrapper lifetimes and multi-process Wine trees,
actual live reparenting, Steam client handoff/early return against a real client,
concurrent same-app launches, shared Wine servers, permission-restricted `/proc`,
Flatpak host PID visibility, anti-cheat/launcher/login titles, and the secondary
library. The real run did not change Steam/game configuration. Python wrapper
shape is not Proton emulation or evidence of Proton compatibility.

**Conclusion:** a known-tree observer is feasible, but process trees alone are
insufficient to identify and authoritatively end Steam sessions. Do not promote
this spike to production tracking or mark ADR-017/020 acceptance passed. p5-004/
p5-008 need a reviewed launch/session attribution strategy plus real Proton
acceptance and broader title coverage; one real native title is now observed
(`real-acceptance.md`). p1-010 must validate host namespace/transport separately.
No privileged helper is justified by these results.

## References / remaining gates

- ADR-017 declares Steam greenfield: there is no v10 Steam plugin oracle here.
  `legacy/source/Playnite/Common/ProcessMonitor.cs` was read only as a reference
  for retaining related process IDs; no v10 parity is claimed. This experiment
  adds Linux identity keys and explicit unknown/cancelled/disposed outcomes.
- Phase 1 process-tree spike: `docs/port/03-EXECUTION-PLAN.md`, lines 73–82;
  host transport boundary: ADR-020 in `docs/port/00-DECISIONS.md`.
- No protected parity/oracle rows changed. Synthetic Steam manifest fixtures are
  not used as process/session evidence. p1-003 remains unsatisfied: the v10 library
  and fixture-dependent integration are not completed by this independent spike.
  Read-only parity references: `steam-010` (session tracking) and `steam-012`
  (manual fallback) remain greenfield, not implemented by this experiment.
- Two adversarial blind diff-only agent reviews and the owner's merge decision remain
  firstmate-owned; reviewers must not edit. No merge is authorized by this report.

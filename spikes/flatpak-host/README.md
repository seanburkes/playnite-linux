# p1-010 — Flatpak host launch/tracking spike

## Bounded conclusion (2026-11-14)

**Inconclusive for ADR-020's no-host-helper question.** This worker has neither
`flatpak` nor `flatpak-spawn` on PATH and is not inside a Flatpak sandbox.
Controlled local-child observations and synthetic identity tests pass; they do
not establish sandbox-to-host access or native/Proton Steam session tracking.
No helper or permission change is proposed or implemented.

References: ADR-017 and ADR-020 in `docs/port/00-DECISIONS.md`; Phase 1
spikes in `docs/port/03-EXECUTION-PLAN.md`. Steam is greenfield: there is no
v10 Steam implementation here to port (ADR-017). The protected Steam parity
rows remain unchanged. p1-003's v10 library dependency is **not complete**;
this isolated probe does not consume or validate fixture data.

## Reproduce

Requires Linux `/proc` and Python 3 standard library only. From repository root:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s spikes/flatpak-host -v
PYTHONDONTWRITEBYTECODE=1 python3 spikes/flatpak-host/probe.py --host-child
```

The evidence run used `/usr/bin/python3` (3.14.10), not the untrusted worktree's
mise shims. Both commands above resolve their script paths from repository root.
All 13 tests executed, zero failures/errors/skips. JSON is an observation report,
not an acceptance verdict: command exit zero does not mean host tracking passed.

Default mode only starts a controlled local Python child. `--host-child` also
attempts a controlled host child **only from an already available Flatpak
sandbox** with `flatpak-spawn` on PATH. It never creates a sandbox or changes
permissions. The exact host argument vector is:

```text
flatpak-spawn --host --watch-bus /usr/bin/python3 -u -c <fixed CHILD source in probe.py>
```

Host Python at `/usr/bin/python3` is an additional requirement of this optional
experiment, not a proposed production dependency. A missing host Python or D-Bus
denial is launch-failure evidence, not proof that Steam itself cannot launch.
No runtime/helper is copied to the host. The fixed inline child only reads its
own proc identity, emits one small JSON record, waits at most 3 seconds for
release, and exits 23. Parent waits are bounded at 5 seconds; cleanup kills/reaps
only its own launcher if needed. No Steam command is executed; no user process
is enumerated, signalled, launched, or killed. PATH presence of `steam` does not
verify an installation, running client, or available native/Proton games.

## Evidence matrix

| Experiment | Evidence class | Observed result | What it does NOT establish |
| --- | --- | --- | --- |
| Capability check | Actual local, read-only | `in_flatpak=false`, `flatpak_on_path=false`, `flatpak_spawn_on_path=false`, `steam_on_path=true` | Steam installation health or any game availability |
| Own local child identity while live | Actual local controlled process | PID 2362442, start ticks 197314503, PID namespace `pid:[4026531836]`; caller `/proc` identity matched | Host PID visibility from a sandbox |
| Own local child release/exit | Actual local controlled process | Exit 23 observed, empty stderr; launcher reaped | Steam launcher PID equals game PID; game exit propagation |
| Host child via `flatpak-spawn --host` | **Not run** | Caller is not sandboxed; both Flatpak tools absent | Host transport, D-Bus authority, host namespace access, host exit forwarding |
| Real host Steam launch from sandbox | **Not run** | No existing sandbox/host transport; user-game launches not authorised | ADR-020 launch acceptance |
| Real native Steam session | **Not run** | No authorised native game experiment; availability not inspected | Start/stop, reparenting, already-running Steam behaviour |
| Real Proton Steam session | **Not run** | No authorised Proton game experiment; availability not inspected | Wine/Proton descendant lifetime, wineserver reuse, app attribution |
| PID reuse, namespace collision, missing/denied `/proc`, stat names with spaces/parentheses | Synthetic unit tests | Distinct identity or explicit inconclusive/denied outcome, never success | Real sandbox policy or Steam identity/attribution |
| Failed launcher and timeout cleanup | Controlled test children | Failure exit 7 reported; unresponsive owned child reaped | Any host-side teardown guarantee beyond the self-expiring child design |
| Host gate and fixed argv | Synthetic unit tests | Existing-sandbox/tool gates enforced; fixed host argument list | Actual `flatpak-spawn` execution or permissions |

Raw run output is retained as task proof outside the repository at
`/home/sean/src/firstmate/data/p1-010/{probe.json,tests.txt}`. The matrix above
records the relevant observations without publishing user paths or process lists.

## Interface to p1-007 (via firstmate)

The native tracking owner remains p1-007. Its handoff requests PID plus process
start-time identity and warns about reparenting. This transport probe emits
`child_identity = {pid, start_ticks, namespace}`: start ticks are Linux stat field
22, not wall time. `caller_proc_visibility` is one of `same-identity`,
`different-identity`, `permission-denied`, or `not-visible-or-exited`.
The latter cannot distinguish exit from isolation. Namespace comparison is
conservative: a numeric PID alone must not establish cross-namespace visibility.
This is a spike JSON contract, not a production service/interface.

A host child reporting its own proc identity only establishes that host-side
code can read **itself**. A sandbox matching that identity would establish direct
visibility for that one process at that instant. Failure to match would not rule
out repeated host-side probes through `flatpak-spawn`. Neither observation
attributes a Steam game to its launcher; Steam may reuse a running client, exit
the launcher early, or reparent a game. Those remain p1-007/real-install questions.

## Remaining evidence / review handoff

1. On an already authorised sandbox/test host, run the optional host-child probe
   unchanged and retain its output, sandbox identity/version and existing
   permissions. Do not broaden permissions just to make this pass.
2. Under separately authorised real-game acceptance, coordinate native and Proton
   launch/start/stop observations with p1-007, including an already-running Steam
   client and reparenting. The present probe deliberately has no game-launch mode.
3. If direct `/proc` is isolated, test whether existing host transport can perform
   bounded host-side identity observations without installing a persistent helper.
   This experiment has not run; isolation alone is not evidence a helper is needed.
4. Any D-Bus/permission or host-helper architecture change belongs to the captain.
   ADR-020 and Phase 5/8 real acceptance remain open, not waived by local tests.

Candidate diff and evidence are for firstmate to obtain two independent blind
diff-only reviews. No reviewer edits or completed-review claim is included here.
The queue remains `in-progress` because real sandbox/Steam evidence is absent.

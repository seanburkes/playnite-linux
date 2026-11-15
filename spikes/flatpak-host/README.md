# p1-010 — Flatpak host launch/tracking spike

## Bounded conclusion (2026-11-14)

**Sandbox-to-host transport is now proven; Steam tracking is not.** A minimal
user-scoped test Flatpak was built with `flatpak-builder`, run for real, and its
`flatpak-spawn --host` child returned the host PID, start ticks and host PID
namespace. From inside the sandbox that host PID was not visible; the host child
exited 23 and `flatpak-spawn` forwarded that code. A second run without the
`org.freedesktop.Flatpak` D-Bus permission failed with `launch-failed` and
`org.freedesktop.DBus.Error.ServiceUnknown`, as expected.

The run used only the EOL `org.freedesktop.Platform`/`Sdk` 24.08 present on the
host, so this is probe evidence, not a supported-runtime or production claim.
No Steam client, native game or Proton game was launched or tracked, and no
helper or production permission change is proposed or implemented. Full
commands, output, versions and limitations are in
[`REAL-ACCEPTANCE.md`](REAL-ACCEPTANCE.md).

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

The local suite run used `/usr/bin/python3` (3.14.10), not the untrusted
worktree's mise shims. Both commands above resolve their script paths from
repository root. All 26 tests executed, zero failures/errors/skips. JSON is an
observation report, not an acceptance verdict: command exit zero does not mean
host tracking passed.

Real sandbox acceptance uses the committed manifest and builds a throwaway app
(requires `flatpak-builder` and an installed `org.freedesktop.Platform` runtime):

```sh
flatpak-builder --user --install --force-clean /tmp/p1-010-build \
  spikes/flatpak-host/org.playnite.FlatpakHostProbe.yaml
flatpak run org.playnite.FlatpakHostProbe
flatpak run --no-talk-name=org.freedesktop.Flatpak org.playnite.FlatpakHostProbe
flatpak uninstall --user -y org.playnite.FlatpakHostProbe
```

The exact versions, output, exit paths and cleanup confirmation are recorded in
[`REAL-ACCEPTANCE.md`](REAL-ACCEPTANCE.md).

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

The report reader accumulates a newline-framed JSON record across short reads,
with a total 4096-byte cap (including newline) and one monotonic 5-second deadline
that fragments do not renew. Empty EOF retains the `launch-failed` report. Partial
EOF raises `EOFError`; expiry raises `TimeoutError` with the received-byte count;
oversized output raises `ValueError`; malformed JSON/UTF-8 raises
`JSONDecodeError`/`UnicodeDecodeError`. These are failures, not visibility results;
the owned launcher is still killed/reaped in `finally`. No blocking `readline`
is used.

## Evidence matrix

| Experiment | Evidence class | Observed result | What it does NOT establish |
| --- | --- | --- | --- |
| Capability check | Actual local and actual sandbox, read-only | Local: `in_flatpak=false`, `flatpak_on_path=false`, `flatpak_spawn_on_path=false`, `steam_on_path=true`. Sandbox: `in_flatpak=true`, `flatpak_spawn_on_path=true`, `steam_on_path=false` | Steam installation health or any game availability |
| Sandbox identity | Actual Flatpak sandbox | App/entrypoint PID 2, PID namespace `pid:[4026533530]`; host shell namespace `pid:[4026531836]` | Cross-namespace PID visibility by itself |
| Own local child identity while live | Actual local controlled process | PID 2362442, start ticks 197314503, PID namespace `pid:[4026531836]`; caller `/proc` identity matched (local run). Inside the sandbox: PID 4, namespace `pid:[4026533530]`, matched | Host PID visibility from a sandbox |
| Own local child release/exit | Actual local controlled process | Exit 23 observed, empty stderr; launcher reaped | Steam launcher PID equals game PID; game exit propagation |
| Host child via `flatpak-spawn --host` | **Actual Flatpak sandbox run** | Transport worked: host PID 435688, start ticks 199472051, host namespace `pid:[4026531836]`; sandbox visibility `not-visible-or-exited`; exit 23 forwarded, empty stderr | Steam launch, D-Bus policy for other runtimes, signal delivery |
| Host child without `org.freedesktop.Flatpak` permission | **Actual Flatpak sandbox run** | `launch-failed`, exit 1, `org.freedesktop.DBus.Error.ServiceUnknown` | Whether another transport would succeed |
| Real host Steam launch from sandbox | **Not run** | No Steam in the test sandbox; user-game launches not authorised | ADR-020 launch acceptance |
| Real native Steam session | **Not run** | No authorised native game experiment; availability not inspected | Start/stop, reparenting, already-running Steam behaviour |
| Real Proton Steam session | **Not run** | No authorised Proton game experiment; availability not inspected | Wine/Proton descendant lifetime, wineserver reuse, app attribution |
| PID reuse, namespace collision, missing/denied `/proc`, stat names with spaces/parentheses | Synthetic unit tests | Distinct identity or explicit inconclusive/denied outcome, never success | Real sandbox policy or Steam identity/attribution |
| Failed launcher and timeout cleanup | Controlled test children | Failure exit 7 reported; unresponsive owned child reaped | Any host-side teardown guarantee beyond the self-expiring child design |
| Host gate and fixed argv | Synthetic unit tests | Existing-sandbox/tool gates enforced; fixed host argument list | Gate logic alone; real execution is covered by the two sandbox rows above |
| Fragmented record and bounded failure paths | Controlled local child plus synthetic short-read/clock tests | Seven-byte reads retain same identity and exit 23; partial EOF/timeout, malformed records and byte-limit failures reap owned launchers; fragments share one deadline | Real Flatpak forwarding, sandbox visibility or game acceptance |

Raw run output is retained as task proof outside the repository at
`/home/sean/src/firstmate/data/p1-010/{probe.json,tests.txt}`; the real
acceptance logs are at `/home/sean/src/firstmate/data/p1-010-live/`. The matrix
above records the relevant observations without publishing user paths or process
lists.

Correction evidence is at `/home/sean/src/firstmate/data/p1-010-fix/`:
`reproduction-before.txt` compares full delivery, seven-byte fragmentation, and
changing only the read limit back to 4096. Only fragmentation triggered the old
`JSONDecodeError`; full delivery reported `same-identity`, so sandbox visibility
is not a trigger. `reproduction-after.txt` reports the same identity/exit with all
three delivery modes. `tests.txt` and `syntax.txt` record the narrow checks.

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

1. ~~On an already authorised sandbox/test host, run the optional host-child
   probe unchanged.~~ Done for one runtime: see `REAL-ACCEPTANCE.md`. Repeating
   on a supported runtime is still open.
2. Under separately authorised real-game acceptance, coordinate native and Proton
   launch/start/stop observations with p1-007, including an already-running Steam
   client and reparenting. The present probe deliberately has no game-launch mode.
3. Direct `/proc` is isolated in the tested sandbox, and existing host transport
   (`flatpak-spawn --host`) did perform bounded host-side identity observations
   without a persistent helper. That is one runtime/host result; it does not by
   itself settle the ADR-020 helper decision or production permissions.
4. Any D-Bus/permission or host-helper architecture change belongs to the captain.
   ADR-020 and Phase 5/8 real acceptance remain open, not waived by this probe.

Candidate diff and evidence are for firstmate to obtain two adversarial diff-only
reviews. No reviewer edits or completed-review claim is included here.
The queue remains `in-progress` because real Steam/native/Proton evidence is
absent; sandbox transport acceptance is no longer pending.

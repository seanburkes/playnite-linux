# GameSessionTracker bounded refinement

## Design decision

Use route (b). Generate the finite, one-session transition graph with pinned TLC
from the frozen GameSession module, then replay its reachable paths against the
real tracker in the required Core test suite. Record every StateChanged event
and both public endpoints. The oracle includes handle ownership and playtime.
Distinguish a matching path from a concrete counterexample. Changing the spec
or silently discarding an intermediate state would invalidate this comparison.

| Route | Estimated effort | Coverage and principal risk |
| --- | --- | --- |
| (a) TLAPS | Several weeks to specify the C# async control flow and prove its simulation obligations | Induction could cover arbitrary lengths under explicit environment assumptions. A proof of a hand-translated implementation would still need a trustworthy link to the compiled tracker. Several observable edges already contradict the proposed simulation. |
| (b) TLC trace replay | About two engineering days for graph generation, deterministic drivers and a source mutation | Exercises compiled production entry points against reachable transitions computed by the existing model checker. Reproducible finite traces expose the first bad step. Schedules, driver inputs, clocks and session count bound the result. |
| (c) Executable next-state oracle | About three engineering days for a separate action interpreter, differential checks and bounded input exploration | Can test rejected inputs as well as legal model actions. A separately maintained translation can reproduce the tracker's mistakes or drift from the TLA+ guards. TLC already evaluates those guards. |

Route (b) keeps the guard and update semantics in one place. Use a generated
artifact so ordinary test runs work offline. Bind it to the input model and
provide the deterministic regeneration command for independent verification.

## How the replay works

TLC 1.7.4 explores the checked-in module with its own configuration (two games,
playtime at most two). The run generated 1,401 states, with 400 distinct states
and depth 27. The extractor keeps edges for g1 with g2 at its initial tuple,
walks from Init, and ends each trace before Reset. There are six maximal
action-labelled paths of two to four actions. LaunchFail and Crash from Starting
have identical state effects but distinct action labels in the dump.

For a graph independent of the tracker, reproduce the committed trace table
from the repository root with the same jar digest as the formal CI job.

```sh
python3 tools/p5-005/generate_session_traces.py "$TLA2TOOLS_JAR" > /tmp/session-traces.tsv
cmp tests/Playnite.Core.Tests/p5-005/GameSession.traces.tsv /tmp/session-traces.tsv
```

The existing required `linux-build-and-test` job runs GameSessionRefinementTests
in the unfiltered Core suite on each pull request. Its offline input is the
TLC-generated table. The suite checks the model, configuration and table hashes.
Regeneration uses one TLC worker, a fixed seed and fingerprint polynomial,
512 MiB heap and a 60-second timeout.

### Observation mapping

Each row starts at the public initial state. The driver samples every synchronous
StateChanged callback and checks the final public state. It checks event identity,
previous state, and agreement of State and Playtime with the public getters.
The g1 tuple has the following components.

| Variable | Sample |
| --- | --- |
| sstate | Public GameSessionState, retaining every intermediate state including Stopping. |
| handle | False before handoff. After handoff the fake launcher's independently observed process remains live until successful reap. This measures unreaped ownership at this boundary. It does not inspect an OS handle. |
| playtime | Actual elapsed seconds from the tracker with an injected integral clock. The bounded StopOk runs advance it by one second during reap. Failure runs hold time fixed. A separate zero-duration stop exercises the bound's sensitivity. |

Identical consecutive **whole tuples** are permitted stutters under
`[Next]_vars`. A degradation event with a changed state, handle or time survives
the comparison. The second game stays idle throughout.

| Model action | Concrete input and observation |
| --- | --- |
| Start | TrackAsync accepts a live process and emits Starting. |
| LaunchOk | TrackAsync emits Running before polling or entering manual fallback. |
| Stop | Confirmed natural exit, manual StopAsync, or teardown after an observer exception emits Stopping from Running. This maps the state effect. The model has no input label for the observer fault. |
| StopOk | A successful natural or manual reap emits Stopped, releases ownership, and records one second in the positive fixture. |
| Crash | A scripted observer exception followed by verified reap executes StopCoreAsync(Failure) and emits Failed. The observed suffix matches Stop then Crash from Stopping. Direct Crash from Running or Starting is outside this replay. |
| CancelLaunch | A pre-cancelled TrackAsync reaps and ends Cancelled. Its intermediate Stopping violates the model. Cancellation after Running has no corresponding CancelLaunch guard. |
| LaunchFail | TrackAsync takes an already launched process. A failed launcher call before handoff has no tracker event. |
| Reset | A tracker is single-use. The comparison ends at its terminal state or at the nonterminal failed-reap observation. |

## Results and limits

The positive replay matches two complete TLC paths. For natural exit and
observer failure it exhausts all seven prefixes of length zero through two over
{live root, incomplete snapshot}. Natural exit then supplies one live and two
empty samples. Failure throws on the next snapshot. One manual fallback run
never polls. These are 15 positive executions with at most five snapshots
each. The fake observer and launcher use scripted inputs, with no OS process,
randomness or wall-clock sleeps.

Four additional executions supply counterexamples. The tests identify the
specific tuple edges that are absent from the TLC graph. They are evidence of
divergence, separate from the positive replay.

| Input | Transition absent from the graph |
| --- | --- |
| Cancel before first observation | Starting → Stopping and Stopping → Cancelled. CancelLaunch in the model goes directly from Starting to Cancelled. |
| Cancel during a running snapshot | Stopping → Cancelled. |
| All manual reap attempts fail | Stopping → Running with a still-live process. The tracker reports degradation and permits retry. |
| Natural exit at zero elapsed time | Stopping/TRUE/0 → Stopped/FALSE/0. StopOk requires playtime to increase by one. |

**Criterion 5 remains partial.** The exact event-level mapping has bounded
counterexamples. Failed is covered for a scripted observer fault and successful
reap at zero elapsed seconds. The packaged native and Proton runs have no
Failed witness. General elapsed times, retry schedules, concurrent callers,
two active games, repeated sessions, native handle disposal, arbitrary-length
behaviour and liveness are outside the positive claim.

For the negative control, change only the StopKind.Failure arm in the production
tracker from GameSessionState.Failed to GameSessionState.Stopped, rebuild Core
tests and run GameSessionRefinementTests. The observer-failure replay fails at
tuple 4 (zero-based), expecting Failed/FALSE/0 and observing Stopped/FALSE/0.
The mutated binary returned test exit code 2. The original tracker is used for
the passing run. Neither test injects a synthetic event.

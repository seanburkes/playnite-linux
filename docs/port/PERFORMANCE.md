# First Linux shell performance baseline

Recorded 2026-11-15. These are observations for later comparison, **not targets,
thresholds, or passing/failing gates**. The Phase 2 performance exit item is only
**partially satisfied**: shell startup/initialisation proxies and peak working set
are recorded; actual application readiness, first frame, and library open are not.

**Empty-library open time: NOT MEASURED — deferred to Phase 4.** Main has no
library, database or open API yet, so any number would be fictional. An empty
shell window is not an empty-library fixture. No endpoint or fixture was invented.

## Machine and measured build

- AMD Ryzen 7 5700, 8 cores / 16 threads, x86-64, boost enabled; CPU range reported
  by `lscpu`: 420.569–4654.2881 MHz. No CPU pinning or governor changes.
- 32,742,392 KiB RAM (31.23 GiB usable as reported by `/proc/meminfo`).
- CachyOS rolling, Linux `7.3.0-3-cachyos`, glibc 2.44.
- .NET SDK 11.0.100, MSBuild 18.12.2, Microsoft.NETCore.App runtime 11.0.0,
  framework-dependent `net11.0`, Release, no publish/AOT/single-file packaging.
- Avalonia / Avalonia.Desktop / Avalonia.Headless / Fluent 12.1.6.
- Product source commit: `0a084a44178a5fae1bdeceee33265668f3a3cb6e`.
  Measurement ran with only the new measurement script untracked; product sources
  were unchanged. Both runs produced identical SHA-256 manifests for every file in
  both host output directories. Main assemblies:
  - CLI: `9b3add420257ddaaeb091d3fe3e53b4251a377c73a803aeaa6697300ad7553cf`
  - Desktop: `7965c8c8bc31107ddc02379befb2341e242065b93e0d51c7a17c0366a11a7b47`

## Reproduce

Prerequisites: Linux, Python 3 with `hashlib.file_digest` (3.11+), `lscpu`, and the
SDK selected by `global.json` on PATH. From the repository root:

```sh
python3 scripts/perf/baseline.py --output /tmp/opencode/playnite-perf-run1.json
python3 scripts/perf/baseline.py --output /tmp/opencode/playnite-perf-run2.json
```

Both recorded runs used the same script and arguments below (shown in portable,
repository-relative form; `perf-evidence` denotes the external evidence directory):

```sh
python3 scripts/perf/baseline.py --output perf-evidence/run1.json
python3 scripts/perf/baseline.py --output perf-evidence/run2.json
```

Each invocation performs `dotnet restore <project> --locked-mode`, then
`dotnet build <project> -c Release --no-restore` once for each host project
(`src/Playnite.Cli/Playnite.Cli.csproj`, `src/Playnite.Desktop/Playnite.Desktop.csproj`).
There is no solution-wide build. It then executes these commands directly, never
`dotnet run`:

```sh
dotnet src/Playnite.Cli/bin/Release/net11.0/Playnite.Cli.dll --version
dotnet src/Playnite.Desktop/bin/Release/net11.0/Playnite.Desktop.dll --headless --smoke
```

The script resolves `dotnet` to its absolute PATH location, sets telemetry opt-out,
no-logo and skip-first-time-experience, removes DISPLAY and WAYLAND_DISPLAY, and
otherwise inherits the invoking environment. All subprocesses use repository-root
cwd. On this host the resolved executable used the SDK and runtime listed above.

One full process launch per host is recorded separately as an **excluded warm-up**,
then **10 fresh-process repetitions** per host are measured sequentially. Build,
restore, metadata capture, binary hashing and SDK first-use are outside every timed
interval. Each new process still pays runtime initialisation/JIT costs. OS file
caches are **warm** (including from builds and hashing): these are cold-process,
not cold-boot/cold-disk numbers. No cache dropping or host lifecycle changes.

JSON output includes every raw sample, excluded warm-ups, median/min/max,
commands, product commit/worktree status, per-output-file SHA-256, `dotnet --info`,
central package versions, `lscpu`, OS/kernel, RAM and load averages. Preserve it
when comparing runs; `perf-evidence` is a directory name for external capture
files, not a committed repository directory. Create the output directory before
running. This document is the committed result; raw JSON is generated on demand.

## Results

Each cell is **median [min, max]**, 10 measured repetitions. Times are milliseconds;
RSS is KiB (1024 bytes). Decimal rounding below does not affect the raw samples.
Runs 1 and 2 are sequential same-host, warm-cache repeats captured about 23 seconds
apart, **not independent runs**. Two repeats cannot establish a performance
distribution; these summaries describe only the observed samples.

| Metric and scope | Run 1 | Run 2 |
| --- | ---: | ---: |
| CLI launch → version line observed: initialisation proxy, **not app readiness** | 17.906 [17.379, 19.360] | 17.977 [17.180, 19.409] |
| CLI launch → exit: version-output **plus exit**, no build/warm-up | 18.482 [17.950, 19.914] | 18.550 [17.704, 20.007] |
| CLI lifetime peak RSS: version-only process, **not a loaded library** | 25,128 [24,848, 25,356] | 24,866 [24,388, 25,212] |
| Desktop Opened → Closed observed: headless window lifecycle proxy, **not first frame** | 78.245 [74.702, 97.333] | 75.827 [73.710, 76.986] |
| Desktop launch → Opened observed: shell initialisation proxy, **not first frame/library open** | 273.256 [259.987, 294.554] | 257.724 [249.173, 275.101] |
| Desktop launch → exit: headless smoke launch and teardown, **not interactive app startup** | 359.623 [344.515, 399.948] | 340.406 [331.115, 357.790] |
| Desktop lifetime peak RSS: headless smoke process, **not steady-state/real display** | 60,466 [59,768, 60,976] | 60,630 [59,928, 61,012] |
| Empty-library open | **NOT MEASURED; Phase 4** | **NOT MEASURED; Phase 4** |

Excluded first launches (same metric order as above, omitting library open):

| Host / run | Version or Opened (ms) | Opened → Closed (ms) | Launch → exit (ms) | Peak RSS (KiB) |
| --- | ---: | ---: | ---: | ---: |
| CLI / 1 | 19.317 | n/a | 19.945 | 25,000 |
| CLI / 2 | 17.742 | n/a | 18.353 | 24,616 |
| Desktop / 1 | 255.759 | 80.203 | 342.938 | 60,696 |
| Desktop / 2 | 258.097 | 75.115 | 339.504 | 60,192 |

## Boundaries and repeatability

Python's monotonic `perf_counter_ns` starts immediately before process creation.
Output timestamps are when the parent observes complete stdout lines, not an
instrumented timestamp inside the application. Pipe buffering and scheduling are
included. Exit detection polls with a 1 ms selector timeout; this adds observer
uncertainty and process-launch bookkeeping. Two lines can arrive in the same read;
their difference would then be zero. Unexpected output, nonzero exit, or a 30-second
hang fails capture rather than silently yielding a successful sample. This timeout
is a liveness guard, not a performance threshold.

CLI only prints assembly version `1.0.0.0` and exits. Desktop smoke prints
`Window opened: Playnite` from its Opened event, schedules Close at
ApplicationIdle, then prints `Window closed`. Opened is not a rendered-frame signal.
The Opened → Closed measurement includes idle-dispatch delay and closure handling;
it excludes process/runtime/theme/window initialisation before Opened and final
process teardown after Closed. Avalonia's default headless drawing substitutes for
a real display backend: these numbers do not measure X11/Wayland, GPU/Skia frame
presentation, interaction, plugins, database work or real-library content.

RSS comes from Linux `wait4(...).ru_maxrss`, the exact child's lifetime high-water
resident set, including runtime/native allocations and shutdown. It is not a
sampled `/proc` estimate, managed heap size, proportional set size, steady-state
working set, or process-tree total. The Python observer and build processes are
not added. Binary hashing is streamed to avoid a large parent allocation inflating
the inherited pre-exec high-water mark. **REJECTED PILOT — not baseline data:** a
preliminary capture using whole-file hashing exposed that artefact (both hosts
reported 126,720 KiB). The pilot is excluded from all accepted results above;
hashing was corrected, and **both reported runs use the corrected script**.

CLI medians agree within 0.4% for timing and 1.1% for RSS. Desktop lifecycle medians
differ by 3.1%, launch → Opened by 5.7%, launch → exit by 5.3%, and RSS by 0.3%
(relative to run 1). **Desktop timing is noisy**, especially run 1's 97.333 ms
lifecycle maximum versus 78.245 ms median. The runs share an ordinary active host,
with no scheduler/frequency/thermal isolation; stdout observation and idle-dispatch
timing also contribute. Load averages (1/5/15 minutes) were 3.12/2.37/2.02 before
and 3.67/2.50/2.06 after run 1; 3.54/2.54/2.08 before and after run 2. These explain
possible variation, not a proven attribution to a particular competing process.
Use multiple runs and comparable host conditions for later comparisons, not these
individual maxima as a regression threshold.
Treat the 5.7% desktop launch → Opened difference as host/measurement noise, not a
performance trend; these two closely spaced repeats cannot demonstrate one.

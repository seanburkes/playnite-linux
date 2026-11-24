# Benchmarks and soak harness (`p8-003`)

Machine-readable performance benchmarks and resource-steady-state soak verification for **Playnite Linux** (`tools/p8-003-benchmarks/Playnite.Benchmarks.csproj`), paired with cold-process host startup proxies in `scripts/perf/baseline.py` (`docs/port/PERFORMANCE.md`) and headless UI/bitmap smoke soak tests (`tests/Playnite.Smoke.Tests/p8-003/DesktopPerfSoakSmokeTests.cs`).

## Scope and boundaries

- **Covered by `Playnite.Benchmarks`:**
  - `library_open_1000_ms` (`median` gate): opens a seeded 1,000-game SQLite library via `LibraryDatabase.OpenAsync` and queries page 1 (`Limit: 50`) through `LibraryService.ListAsync` (1 excluded warm-up + 25 repetitions).
  - `filter_search_p95_ms` (`p95` gate): runs `LibraryService.SearchAsync` with an `IsInstalled` filter, `SortOrder.Playtime` descending sort, and `LibraryGroupBy.ReleaseYear` grouping over the 1,000-game fixture (1 excluded warm-up + 25 repetitions).
  - `artwork_cache_probe_ms` (`median` gate): writes and reads deterministic `256x120` PNG payloads through `ArtworkCache` (`StoreAsync`/`FindAsync`) and streams + verifies dimensions via `AssetCopy.RunAsync` (1 excluded warm-up + 100 repetitions).
  - `memory_post_bench_rss_kib` & `memory_post_soak_rss_kib`: `/proc/self/status` `VmRSS` snapshots after benchmark and soak phases.
  - `soak_import_browse`: 10 repeated cycles seeding a 250-game SQLite library, paging through all slices, searching, and cleaning up per-cycle storage, asserting `/proc/self/fd` and `VmRSS` flatten after cycle 0 warm-up.
  - `soak_session_lifecycle`: 25 repeated `GameSessionTracker` cycles exercising natural exit, manual `StopAsync`, and `CancellationToken` teardown, asserting flat FD and RSS curves.
  - `soak_real_process_lifecycle`: 6 repeated real Linux `LinuxProcessLauncher` + `LinuxProcessObserver` + `GameSessionTracker` cycles launching and terminating a real child process with zero orphans and flat FD/RSS curves after cycle 0 process-runtime warm-up.
- **Covered by `DesktopPerfSoakSmokeTests`:**
  - 15-cycle headless `LibraryViewModel` open, page, search, filter, sort, and dispose soak over a 200-game SQLite database (`/proc/self/fd` delta `<= 6` and late `VmRSS` growth `<= 25%` after warm-up).
  - 100-iteration headless Avalonia `Bitmap` decode soak over synthetic `320x180` PNG streams (`/proc/self/fd` delta `<= 6` and late `VmRSS` growth `<= 25%` after warm-up).
- **Deferred / separate units:**
  - Cold-process CLI/Desktop startup and peak RSS remain in `scripts/perf/baseline.py` (`docs/port/PERFORMANCE.md`).
  - WebView load/dispose soak is deferred to `v1.1` per `ADR-019` (`p1-006`/`p8-008`).
  - 7-day pre-release soak belongs to `p9-001`.
  - No `GC.Collect` calls are used anywhere in the harness, so `docs/port/BenchmarkHarnessAllowlist.props` remains empty.

## Tolerance gate and flattening contract

- **Tolerance gate (`ToleranceGate.Compare`):**
  - Default thresholds: `+10%` (`warn`), `+20%` (`fail`), `0.25 ms` absolute sub-millisecond jitter floor (metrics in `ms` where both baseline and current are in `[0, 0.25 ms]` pass while preserving actual `deltaRatio`).
  - Missing metrics fail (`missing-metric`), unit/statistic mismatches fail (`unit-mismatch`/`statistic-mismatch`), and negative, non-finite, empty, or duplicate metric inputs are rejected.
  - Exit codes: `0` on clean pass (`BENCHMARK COMPLETE` / `SOAK COMPLETE`), `1` on soak leak (`SOAK LEAK DETECTED`), tolerance failure (`BENCHMARK REGRESSION`), cancellation, or runtime error, and `2` on invalid CLI arguments.
- **Soak flattening (`ProcessResourceProbe.EvaluateSoakFlattening`):**
  - Excludes cycle `0` as warm-up and splits steady-state samples into first and second halves.
  - Requires steady FD delta `<= 4` (second-half max minus first-half min, and final sample minus first-half median) and late `VmRSS` median growth `<= 25%`.

## Recorded local baseline (2026-11-24)

Captured on Linux x86-64 (`net11.0`, `Release`, warm OS page cache) via:

```sh
dotnet restore tools/p8-003-benchmarks/Playnite.Benchmarks.csproj --locked-mode
dotnet build tools/p8-003-benchmarks/Playnite.Benchmarks.csproj -c Release --no-restore
dotnet tools/p8-003-benchmarks/bin/Release/net11.0/Playnite.Benchmarks.dll --mode all --output perf-evidence/p8-003-baseline.json
```

| Metric / Soak Curve | Gate / Check | Observed Result |
| --- | --- | --- |
| `library_open_1000_ms` | `median` | `7.258 ms` (`p95=7.961`, `min=7.054`, `max=7.978`, `n=25`) |
| `filter_search_p95_ms` | `p95` | `22.456 ms` (`median=19.350`, `min=15.954`, `max=37.209`, `n=25`) |
| `artwork_cache_probe_ms` | `median` | `0.675 ms` (`p95=0.932`, `min=0.595`, `max=2.266`, `n=100`) |
| `memory_post_bench_rss_kib` | `median` | `97,060 KiB` |
| `memory_post_soak_rss_kib` | `median` | `112,000 KiB` |
| `soak_import_browse` | `flat` | `fd_delta=0`, `rss_growth=1.86%` (`n=10`) |
| `soak_session_lifecycle` | `flat` | `fd_delta=0`, `rss_growth=0.01%` (`n=25`) |
| `soak_real_process_lifecycle` | `flat` | `fd_delta=0`, `rss_growth=3.11%` (`n=6`) |

`perf-evidence/p8-003-baseline.json` denotes a caller-supplied output directory outside committed repository paths. To gate a subsequent run against a saved baseline:

```sh
dotnet tools/p8-003-benchmarks/bin/Release/net11.0/Playnite.Benchmarks.dll --mode all --baseline perf-evidence/p8-003-baseline.json
```

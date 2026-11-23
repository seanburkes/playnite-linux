# Verification design

## 1. What "verified" means here

This project uses **practical rigor plus three formal models**. It does not claim a
machine-checked proof of the C#/Avalonia implementation. A unit is verified when:

1. Its intended behaviour is written down as an observable contract.
2. It has an executable test or golden that would fail if the behaviour changed.
3. The oracle for that behaviour is independent of the implementation (recorded output,
   fixture, formal model, or the frozen app).
4. The test is run in CI with no skips.
5. The parity ledger row is updated with the test id.

Anything less is "it compiles". Every regression in a well-run port is code that compiled.

## 2. Oracle ladder for this port

Ranked by strength, adapted from the Language Port Doctrine:

| # | Oracle | Here | Lives through the port? |
| --- | --- | --- | --- |
| 1 | Wire/process-level goldens | Exported library JSON, settings bytes, manifest bytes, archive bytes, HTTP request/response bytes, log lines, exit codes | Yes |
| 2 | Differential corpus | Run v10 oracle and new CLI on the same fixture; compare outputs | Yes |
| 3 | Property tests | VDF/ACF parsers, path handling, filters/sorts, settings round-trip, DB invariants | Yes |
| 4 | Surface parity inventory | One row per public SDK type/member, setting, UI flow, Steam feature | Yes |
| 5 | Unit tests in the source language | v10 NUnit suite; useful for the old code, not an oracle for the new | No |
| 6 | Human side-by-side review | Review technique, not evidence | No |

Rows 5–6 are used, but never counted as port verification. The port relies on rows 1–4.

## 3. Six verification layers

### Layer 1 — Unit-by-unit equivalence

For each ported unit:
- Ported function against recorded cases from v10 (where the function is pure).
- Table-driven tests for edge cases: null vs empty, zero vs missing, case/culture, overflow,
  path separators, Unicode.
- Assertions on the *observable* result, not on internal structure.

Tools: xUnit v3 or NUnit 4 (decision in Phase 2; default xUnit v3 for new tests, keep NUnit
for reused legacy tests if cheaper). `FluentAssertions` optional; prefer plain asserts.

### Layer 2 — Boundary differential (the layer people skip)

Run the old and new implementations on the same input and compare **bytes**, with no
normalization:

- Library export: JSON/JSONL, sorted by stable ids, exact property names and value formatting.
  Compare both canonical bytes and a parsed semantic diff (so a formatting change is visible
  but not fatal).
- Settings: write, read, write; compare bytes for stable fields.
- Manifests: parse → serialize → bytes.
- Archives: entry names, ordering, timestamps (fixed clock), CRC, bytes.
- HTTP: a local stub records method, path, query, headers of interest, body; responses are
  replayed from files. Compare both request bytes and the resulting library state.
- Logs: fixed level, volatile tokens redacted by a documented rule; compare remaining lines.
- Exit codes and stderr for CLI operations.

Why both canonical bytes and semantic diff: `42` vs `42.0`, `null` vs missing, and key order
are exactly the bugs that break consumers; a semantic-only comparison hides them.

### Layer 3 — Invariant pairs

After each operation, assert the set of things that must agree:

- DB row ↔ in-memory item ↔ emitted event ↔ asset file.
- Import report count ↔ DB count ↔ source count − skipped.
- Game session state ↔ tracked process ↔ playtime record.
- Settings write ↔ file on disk ↔ reloaded value.
- Migration status ↔ backup manifest ↔ source hash.

A half-ported pair is invisible to per-function tests. Invariant-pair tests are mandatory for
import, settings, and session lifecycle.

### Layer 4 — Surface parity inventory

`surface-parity.tsv` is the anti-omission mechanism. One row per:

- SDK public type/member/interface method.
- v10 setting and default.
- v10 library operation.
- v10 UI window/flow in v1 scope.
- Steam discovery/launch feature.
- CLI command/flag.

Columns:
```
id  area  surface  v10_ref  v1_status  test_id  oracle  owner  notes
```
`v1_status`: `planned`, `ported`, `adapted`, `greenfield`, `deferred`, `out-of-scope-v1`, `removed` (with reason). `greenfield` means there is no v10 reference (Steam); it still needs a test id and oracle.
CI checks that every `ported`/`adapted`/`greenfield` row has a non-empty `test_id`, that every
non-placeholder `v10_ref` resolves under `legacy/source/`, and that no row is silently deleted.
A new public member without a row fails `PublicApiAnalyzers`. Full test-file existence
checking activates once the test projects exist (Phase 2).

### Layer 5 — Resource steady state

Repeat an operation N times in one process and assert the curve flattens:

- Import a 1,000-game library 10 times; RSS and open handles flat.
- Open/close the library view 200 times; image cache bounded.
- Launch/cancel/stop a game 100 times; no orphan processes, handles, or threads.
- WebView load/dispose 100 times; no WebKit process leak.

Tools: `dotnet-counters`, `dotnet-gcdump`, `/proc/<pid>/fd`, `ps` process-tree assertions.
Thresholds are baselines with tolerance, not absolute numbers.

### Layer 6 — Performance with tolerance

Benchmarks that fail on regression:
- cold startup to first frame;
- open a 1,000-game library and first render;
- filter/search latency (p95);
- image decode/cover grid scroll;
- memory after open and after 10 minutes idle.

Tool: BenchmarkDotNet for micro; a custom harness for app-level. CI compares against a stored
baseline with a tolerance (e.g. +20% fails, +10% warns). A benchmark nobody reads is not a
gate.

## 4. Meta-checks

### 4.1 Executed test cases never fall

`tests/baseline.json` records, per project, the **executed case count from a JUnit result
file** (not a regex over source), plus skip count (must be zero) and coverage percentage
(informational at first). Assertion count is not a gate: neither xUnit nor NUnit exposes it
reliably, and a loop can game it.

The new-suite baseline is recorded at **Phase 2 exit**, not from the legacy suite. A PR that
lowers the executed-case count fails unless it carries the `oracle-change` label and passes the
protected-path gate. Replaced tests must be replaced one-for-one or better.

### 4.2 Verify the harness itself

- A known-failing test must fail in CI; a skipped test must be reported.
- Golden comparison must fail when one byte changes (mutation test on the comparator).
- The Windows oracle job must actually run the old tests; count them.
- The Linux job must not silently exclude a test project.
- The test runner version and OS image are pinned.

### 4.3 Oracle independence

Independence is structural, not cultural. The mechanisms that carry it are:
- `legacy/` is a frozen snapshot and `oracle-lock/` pins its dependencies. Both are
  **unconditionally** immutable: CI fails on any change and no label overrides them.
- `tests/fixtures/`, `tests/golden/`, `tests/baseline.json`, `docs/formal/`, and
  `docs/port/surface-parity.tsv` are protected paths. The `plan-guards` workflow diffs every
  one of them against the merge base on each PR, and fails on a change the `oracle-change`
  route did not declare. The `merge_group` variant of that step is retained but dormant:
  GitHub merge queue is unavailable for this user-owned repository, so a PR label change is
  the live path.
- The bytes are pinned: `tests/fixtures/v10/SHA256SUMS`, the golden manifests, `oracle-lock/`,
  and the `api-baseline/` SHA files change when a payload changes, even when the diff looks
  small.
- The checks are executed, not asserted: the `formal` job runs the TLA+ models in
  `docs/formal/`, and the Linux job runs the count and skip policy. The five required contexts
  are `inventory-and-parity`, `formal`, `linux-build-and-test`, `AppImage packaging smoke` and
  `Flatpak packaging smoke`; these are status checks, not reviewer opinions. The
  `legacy-oracle`, `settings-library-oracle` and `windows-fixture-validation` jobs run the
  frozen Windows code and validate regenerated fixtures as path-filtered additional checks,
  so they only run when their paths change.
- Strict/up-to-date is intentionally off because a GitHub merge queue is unavailable for this
  user-owned repository. Residual risk: a PR whose head was tested against an older `main` can
  merge; the post-merge `linux-ci` run on `main` is the backstop, so the regression is caught
  after merge rather than before it. The post-merge `port-index` aggregate commit is not a
  mergeability gate.
- There is no auto-accept/update-snapshot command in the repo. Regeneration is a separate
  script run by a human, and the resulting diff goes through the protected-path route.
- Escape hatches (`Skip`, `Explicit`, `[Ignore]`, category filters) are counted; a non-zero
  skip count fails.
- `.github/CODEOWNERS` records which paths belong to the owner for visibility and routing,
  including the complete `.github/workflows/` directory, the CODEOWNERS file itself,
  `scripts/port/build_port_index.py`, and the oracle harness tools. GitHub enforces code-owner
  review only when repository branch protection requires it. CODEOWNERS alone is not an
  approval gate: protecting `main` against direct pushes and requiring review remain repository
  settings; this repository change does not configure them.

An oracle change is declared, not approved by a second person: the task says `oracle-change`,
the PR carries the label, and the protected-path gate checks the diff. This repository has one
human owner, so review is adversarial agent assistance rather than independent human approval,
and the owner makes the merge decision (ADR-024). An external reviewer is still preferred where
one can exist - an external human, or a different machine or owner - and when it is not used
the owner's merge decision is the accepted exception, recorded in the gate artifact
(`docs/port/gates/P<phase>-*.md`). The residual risk is real: one human plus models can share
blind spots, correlated reviewers miss the same defects, and the structural checks catch
tampering and regression rather than a wrong port that passes its tests. The mitigations are
the mechanical protections above, not a second signature, and they reduce the risk without
eliminating it.

## 5. Process-level differential harness

Because `net462` and `net11.0` cannot share a process, the harness is a pair of CLIs:

```
Windows CI:  legacy-oracle.exe export --library fixtures/v10 --out artifacts/old
Linux CI:    playnite-cli export --library fixtures/v10 --out artifacts/new
compare:     python3 scripts/verify/compare.py artifacts/old artifacts/new --mode bytes
```

`legacy-oracle` is built from `legacy/` on Windows. It must not change the fixture. It is
constrained to **existing v10 public APIs and raw bytes**:
- call existing legacy assemblies for pure functions and emit raw output;
- drive the legacy app with a fixed data dir and CLI flags;
- run the v10 NUnit suite and emit a JUnit result file.

It may **not** introduce a new export schema, re-serialise objects, normalise output, or
"helpfully" sort/format. Otherwise the comparator tests two new implementations, not the old
one.

The new CLI exposes the same operations headlessly. Both write to a fixed clock, fixed temp
paths, and redacted logs. The comparator is the only code allowed to interpret differences;
it has its own tests.

## 6. UI verification

Avalonia makes UI testable in ways WPF v10 was not:

- **Headless rendering:** `Avalonia.Headless.XUnit` + Skia; render a view to a bitmap at fixed
  size/DPI/theme/culture. This is the screenshot gate; it uses neither X11 nor Wayland.
- **Baseline screenshots:** checked in under `tests/golden/ui/`; pixel diff with a small
  tolerance for antialiasing; fail on layout/size changes beyond tolerance.
- **Semantic tree assertions:** query the visual/logical tree for names, roles, text, enabled
  state, and focus order. More stable than pixels and better for accessibility.
- **Interaction tests:** simulate keyboard/pointer input, assert ViewModel state and navigation.
- **Theme matrix:** default dark/light, 100%/150% scaling, LTR/RTL for a few screens.
- **Old-app UI references:** curated official/public screenshot and video links in
  [`ui-reference/SOURCES.md`](ui-reference/SOURCES.md) (p1-004, reference-only), for human
  comparison. A local 10.64 capture is optional. These references are not reproducible across
  DPI/theme/font/ClearType, are never a pixel gate, and no old-app screenshot comparison is
  ever a test gate.
- **X11/Wayland:** an app-launch and input smoke test, not a screenshot gate.

Accessibility is **not a v1 requirement** (captain decision, 2026-11-14; Deferred limitations
D1 in [`00-DECISIONS.md`](00-DECISIONS.md)). Every interactive control should still have a
usable name/role, tab order is tested for the main flows, and high contrast is smoke-tested as
quality practice, but v1 makes no screen-reader claim: no end-to-end screen-reader test (for
example Orca) was performed, and for Avalonia 12.1.6 AT-SPI is only observable under
X11/XWayland.

## 7. Property-based and fuzz testing

Property tests (`CsCheck` 5.x):
- VDF/ACF parse→serialize round-trip where the format permits.
- Path join/normalize invariants: no `..` escape, no duplicate separators, XDG roots honored.
- Settings round-trip: deserialize(serialize(x)) == x for generated valid settings.
- Filter/sort: filtering then sorting is stable; a filter never adds items.
- Database collection: add/update/remove sequences preserve ids and counts; buffered events
  fire once per batch.
- Release date / playtime formatting: locale-invariant parse, culture-specific display.

Fuzzing (`SharpFuzz` + libFuzzer or AFL):
- VDF, ACF, INI, JSON settings, YAML manifests, ZIP/7z/tar entry names, M3U/CUE.
- Corpus from real fixtures. `linux-ci` keys the budget on the event: pull-request runs target
  **45 s** and pushes to `main` target **120 s** (a dormant `merge_group` run would use the
  same 45 s target), both with a
  **50,000,000-iteration** cap and an outer timeout of the target budget plus 30 s. The
  target rejects a wall-clock budget under **30 s** or an iteration budget under
  **100,000**, so a short run cannot pass vacuously; `FUZZ COMPLETE` is printed only after
  a budget at or above those floors is exhausted cleanly.
- Every crash becomes a regression test and a fixed corpus entry.

## 8. Mutation testing

`dotnet-stryker` on `Playnite.Core` (not UI) nightly:
- Target mutation score ≥ 60% initially, ratchet upward.
- A surviving mutant in a ported unit is a missing test; triage into the queue.
- Do not gate PRs on mutation score; gate the trend.

## 9. Formal models

Three TLA+ models, checked by TLC in CI (nightly and on changes to `docs/formal/`). They are
**safety models**: invariants over reachable states. Liveness is documented but not
machine-checked in v1 (the per-action fairness assumptions need refinement). Do not claim
liveness as verified.

### 9.1 `LibraryImport.tla`
The model writes to a temp database and atomically swaps; a concurrent reader must never see a
partial live database. Invariants:
- `SourceUnchanged`: the source path is never written.
- `NoCommitWithoutVerify`: `state = Committed` implies verification passed.
- `ReaderNeverPartial`: a reader sees `Old` or `New`, never a temp/partial database.
- `BackupBeforeWrite`: a backup manifest exists before the first live write.
- `OneImport`: at most one import runs at a time.
Crash recovery: from any state, a restart reaches `Committed` or `RolledBack`, never a partial
live DB. `AtomicLive` is the falsifiable one: a crash during the swap leaves `Partial` until
`Rollback` restores `Old`, so removing rollback fails TLC. `ReaderNeverPartial` is a design
invariant over the published database, not a claim that every interleaving was tested.

### 9.2 `GameSession.tla` (design spec)
This model is a **design specification**, not a falsifiable concurrency model. Its safety
invariants are useful for the implementation and tests, but several are re-established by every
action; do not overclaim. Invariants:
- `OneSessionPerGame`: no two active sessions for the same game id.
- `StopOnlyFromRunning`: a stop request is valid only in `Running`.
- `NoOrphanProcess`: every terminal state has released the tracked process handle.
- `CancelAlwaysTerminates`: a cancel from any pre-`Running` state reaches `Cancelled`.
- `PlaytimeMonotonic`: recorded playtime never decreases.
Liveness: a started session eventually reaches `Stopped` or `Failed` unless the process is
externally killed; the external-kill path is explicit.

### 9.3 `SettingsMigration.tla`
Invariants:
- `OldFileUntouched`.
- `NoUnknownLoss`: every input property is either mapped or preserved in `legacy`.
- `AtomicWrite`: readers see old or new, never a partial file.
- `RollbackAvailable`: on failure, the old file remains usable.

Each invariant has a C# test that cites it, e.g.
`[Trait("Invariant", "LibraryImport.ReaderNeverPartial")]`. A model change requires an ADR
and a test update in the same PR (oracle-change path). The TLA+ result is evidence about the
design, not a proof of the C# implementation.

## 10. CI gates (mechanical, cheap, layered)

| Gate | Fails when | Runs |
| --- | --- | --- |
| Build | any project fails | every PR |
| Unit tests | any test fails or is skipped | every PR |
| Architecture | Core references Avalonia/Win32/System.Drawing; Platform.Linux leaks into Core | every PR |
| Banned APIs | `System.Windows` (except `ICommand`), `Microsoft.Win32`, `DllImport` outside platform, `BinaryFormatter`, `Thread.Abort`, `AppDomain` | every PR |
| Oracle guard | protected artifacts changed without label; `legacy/`/`oracle-lock/` always | every PR |
| Golden diff | bytes differ | every PR |
| Test counts | executed cases fall or skips > 0 | every PR |
| Public API | SDK surface removed/changed without an API report update | every PR |
| Dependency audit | vulnerable/denied package, license change | every PR + nightly |
| SBOM | missing/unsigned artifact | release |
| Fuzz smoke | crash in bounded run | every PR (45 s), main push (120 s) |
| Formal | TLC invariant violation | nightly + `docs/formal/**` |
| Mutation | score drops below ratchet | nightly |
| Benchmarks | > tolerance regression | nightly |
| Soak | RSS/handles/processes grow | nightly |
| Packaging | AppImage/Flatpak fails or cannot start | every PR (smoke), release (full) |
| Platform smoke | missing `CREATE_NO_WINDOW` equivalent, tray/notification/webview failure | nightly on distro matrix |

Layering matters: a golden diff, an architecture test, and a fuzz crash have different failure
modes. One waiver cannot hide all three.

## 11. Definition of Done for a ported unit

- [ ] Parity row exists with `v10_ref` and `v1_status`.
- [ ] Behaviour contract written (one paragraph or a table).
- [ ] Unit tests added; edge cases from the information-loss checklist addressed.
- [ ] Golden/differential test added where the unit crosses a boundary.
- [ ] Property/fuzz test added where the input is parser-like.
- [ ] No banned API; architecture tests pass.
- [ ] No new warning; analyzers clean.
- [ ] Resource lifetime: dispose/cancel paths tested.
- [ ] Logging and error messages match v10 where consumed.
- [ ] `legacy/` untouched; test/golden paths untouched.
- [ ] Two adversarial reviews (diff only, "assume this is wrong"); fixes applied.
- [ ] Ledger row updated; commit message names the row and oracle.
- [ ] CI green, including oracle and counts.

## 12. Evidence to keep

```
tests/baseline.json                 counts, skips, coverage
tests/golden/**                     expected bytes + manifest
tests/fixtures/**                   synthetic v10 library, Steam roots, archives
tests/screens/**                    UI baselines + semantic trees
docs/port/gates/**                  gate evidence
docs/port/ledger.tsv                unit status, owner, attempt count
docs/port/surface-parity.tsv        omission ledger
docs/formal/**                      TLA+ models + TLC configs + results
artifacts/benchmarks/**             baseline numbers
artifacts/fuzz/**                   corpora and crash repros
```

The ledger is the resumable artifact: a different person, or the same person after a context
reset, can see exactly what is done, what is in flight, and what evidence exists.

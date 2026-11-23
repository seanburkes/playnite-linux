# PORTING.md — v10 (.NET Framework/WPF/Win32) → v11 (.NET 11/Avalonia/Linux)

This is the shared contract every implementer reads before touching a unit. If a unit needs to
deviate, write the reason in the parity row and get it reviewed; do not improvise silently.

## Non-negotiables

1. **Behaviour first.** Preserve v10 observable behaviour for in-scope surfaces. Do not
   redesign, optimise, or "fix" during the port.
2. **Port the wrongness.** If v10 is wrong, port it and file a follow-up with a reproduction.
3. **No stubs.** No `TODO`, `NotImplementedException` on a reachable path, placeholder
   constants, or fake success. If it cannot be implemented yet, mark the parity row
   `deferred` and keep it out of the build.
4. **No paragraph-long justification comments.** If a workaround needs a paragraph, the code
   is wrong. Fix the code.
5. **Oracle independence.** Do not edit tests, fixtures, goldens, `docs/formal/`, or
   `tests/baseline.json` in a porting change.
6. **One unit, one commit.** Keep the diff reviewable; split rather than bundle.
7. **Ask, don't guess.** Ambiguity is a decision request, not a licence to invent.

## Source-reuse protocol

- Read the v10 file in `legacy/` first. Copy it into the new project, then adapt.
- Keep member and file names 1:1 where practical so `grep` still works across both trees.
- Remove WPF/Win32 as a deliberate, reviewed change, not as a side effect.
- If a v10 file contains both UI and logic, split it: logic to Core, UI to Desktop. Record the
  split in the parity row.
- Do not copy generated `bin/`/`obj/` files, `.Designer.cs`, or XAML code-behind wholesale.
- After the unit is done, `rg` the new project for `System.Windows`, `Microsoft.Win32`,
  `DllImport`, and `System.Drawing`; zero hits outside the allow-list.

## Type mapping

| v10 | v11 | Notes |
| --- | --- | --- |
| `int` IDs / `Guid` | `Guid` for entities; `long` for SQLite rowid only | Do not expose rowids |
| `DateTime` local | `DateTimeOffset` UTC at boundaries; `DateTime` only where v10 format requires | Persist UTC ISO-8601 |
| `string` paths | `string` at API edges, `Path`/`FileInfo` internally | Never concatenate separators; use `Path.Combine` |
| `System.Drawing.Bitmap` | `Avalonia.Media.Imaging.Bitmap` at UI; `MagickImage`/Skia in services | No `System.Drawing` |
| `System.Windows.Media.Color` | `Avalonia.Media.Color` | SDK UI contract |
| `System.Windows.Controls.Control` | `Avalonia.Controls.Control` | SDK v7 |
| `System.Windows.Threading.Dispatcher` | `Avalonia.Threading.Dispatcher` / injected `IUiDispatcher` | No dispatcher in Core |
| `System.Windows.Input.ICommand` | `System.Windows.Input.ICommand` (in-box) | Keep `RelayCommand` shape |
| `INotifyPropertyChanged` | same | Keep `ObservableObject` shape |
| `Window` | `Avalonia.Controls.Window` | |
| `MessageBox` | `IDialogService` | Testable |
| `System.Management` (WMI) | `/proc`, `lsblk`, D-Bus, or a platform service | No WMI |
| `Microsoft.Win32.Registry` | XDG config/secret store | No registry |
| `ServiceController` | no equivalent; remove or platform service | Not v1 |
| `System.Web` | `HttpClient`/`System.Net` | |
| `Thread`/`ThreadPool` | `Task` + `CancellationToken` | No `Thread.Abort` |
| `AppDomain` | `AssemblyLoadContext` | For plugin isolation later |
| `BinaryFormatter` | none | Banned |
| `PowerShell 5.1` | `pwsh` 7 via `Microsoft.PowerShell.SDK` | Not v1 |

## Information-loss checklist

Answer these explicitly in the unit's parity row when the unit crosses a boundary. The
language/framework change makes implicit behaviour explicit; an unanswered row is a guess.

| # | Category | v10 behaviour to preserve | v11 decision |
| --- | --- | --- | --- |
| 1 | Numeric domain | `int`/`long`/`float` mixed; unchecked by default | Explicit widths; checked arithmetic in parsers; no silent casts |
| 2 | Null/absence | `null`, empty string, missing JSON key, `default` | Define per field; use `string?` semantics; preserve v10 mapping |
| 3 | Truthiness | `string.IsNullOrEmpty`, `0`, `false`, empty list | Explicit predicates; do not treat empty as absent unless v10 did |
| 4 | Text encoding | UTF-16 in memory, UTF-8 files, code pages in old data | UTF-8 at boundaries; `Encoding.UTF8`; preserve BOM expectations |
| 5 | Formatting/parsing | `ToString()` culture-sensitive in places | Culture-invariant for wire/files; current culture for display |
| 6 | Ordering | Insertion order in collections; unstable sorts in places | Ordered collections; stable sort; document event order |
| 7 | Error model | Exceptions cross layers; some swallowed with logs | Exceptions at boundaries; `Result` only where it clarifies; log and rethrow policy |
| 8 | Resource lifetime | `IDisposable`, finalizers, process handles | `using`/`await using`; no finalizer side effects; explicit close |
| 9 | Concurrency | UI thread + thread pool + `async void` events | `Task` + `CancellationToken`; UI dispatcher at the edge; no `async void` except event handlers |
| 10 | Evaluation order | Argument evaluation, short-circuit, logging side effects | Explicit sequencing; don't put side effects in assertions/log arguments |
| 11 | Reflection/metaprogramming | Attributes, `Type.GetType`, plugin reflection | Same where possible; trimming/AOT not assumed in v1 |
| 12 | Ambient state | cwd, env, TZ, locale, `%AppData%` | Capture once at startup in `IPaths`/`IEnvironment`; pass explicitly |
| 13 | Platform contracts | Win32 flags, console windows, registry, shell | Platform interface; smoke tests assert absence of side effects |
| 14 | Dependencies | NuGet + `Playnite.Deps` binaries | Source package per dependency; contract test for each replacement |

## Async and threading

- Core APIs are `async` with `CancellationToken` for anything that can block or take >50 ms.
- No `.Result`/`.Wait()` on the UI thread. No `Task.Run` to hide sync I/O in a request path;
  make the I/O async.
- Event handlers may be `async void` only at the UI boundary; they catch and log.
- Never fire an event before the durable state is written.
- Cancellation must be honored at every await; lifecycle tests cancel at each point.
- UI thread affinity is explicit: `IUiDispatcher.InvokeAsync`. Core never assumes a UI thread.
- Do not use `ConfigureAwait(false)` blindly in library code without a policy; default to
  `false` in Core, `true` at UI boundaries.

## Errors and logging

- Expected failures (missing file, invalid manifest, no Steam library) are results/logged
  warnings, not crashes.
- Unexpected failures are logged with context and surfaced to the user; never swallowed.
- Log messages that users/scripts consume keep v10 wording where practical. Golden tests
  compare stable lines.
- Redact tokens, cookies, API keys, and absolute home paths in diagnostics bundles.
- Do not log inside a tight loop; use debug level and rate limits.

## Paths and ambient state

- All paths come from `IPaths`; no `Environment.GetFolderPath` or `%AppData%` in Core.
- XDG:
  - data: `${XDG_DATA_HOME:-~/.local/share}/playnite`
  - config: `${XDG_CONFIG_HOME:-~/.config}/playnite`
  - cache: `${XDG_CACHE_HOME:-~/.cache}/playnite`
- Case-sensitive filesystems: never assume `A` and `a` are the same file. Compare paths
  ordinally on Linux.
- Normalize separators only at display time; store POSIX paths.
- Steam paths may contain spaces and Unicode; always pass argument lists, never a shell string.
- Capture `TZ`, `LANG`, `LC_ALL`, cwd, and `HOME` once at startup; pass them to services that
  need them. Tests run under several values.

## Serialization

- v10-compatible JSON: Newtonsoft 13 with the same `JsonResolver`, property names, and
  `DontSerialize` semantics.
- New internal JSON: `System.Text.Json`, camelCase, explicit options; do not mix.
- YAML: YamlDotNet 18; preserve manifest behaviour; golden-test parse→serialize.
- Dates: ISO-8601 UTC at boundaries; display uses current culture.
- Enums: preserve numeric/string representation used by v10 files; do not reorder enum values
  without a migration.
- Unknown properties: preserve in a `legacy` bag; never drop silently.

## Database

- SQLite via `Microsoft.Data.Sqlite`; Dapper for queries; explicit SQL, no ORM magic.
- Every write is in a transaction; every migration is versioned and forward-only.
- Use parameterized SQL only; no string concatenation.
- Foreign keys on; `PRAGMA journal_mode=WAL`; `PRAGMA busy_timeout`.
- Store GUIDs as canonical lowercase strings; timestamps as UTC ISO-8601.
- Batch imports in chunks (e.g. 500) with one transaction; report progress.
- Never open the v10 LiteDB file read-write.

## UI rules

- Views are `axaml`; code-behind is for view-only concerns, not business logic.
- ViewModels are testable without a window; inject services.
- Bindings are compiled where possible; no reflection binding in new code unless required.
- Every interactive control has an accessible name and a keyboard path.
- No hard-coded colors/sizes in views; use theme resources.
- Dialogs go through `IDialogService`; no static `MessageBox` in ViewModels.
- Images decode off the UI thread; use a bounded cache.
- No `DispatcherTimer` for business logic; use `PeriodicTimer`/scheduled tasks in Core.

## Tests

- One behaviour per test; name it after the behaviour, not the method.
- Use fixtures/goldens from `tests/`; never inline a copy of a golden.
- For parsers: table tests + at least one property test + a fuzz target.
- For lifecycle: cancel at every await; assert no live handles.
- For UI: headless render + semantic assertions; screenshot only for layout.
- A test that would pass with the implementation deleted is not a test.
- No `[Skip]`, `[Ignore]`, `--filter` exclusions, or conditional test runs in CI.

## Queue and ledger shards

`docs/port/queue.tsv` and `docs/port/ledger.tsv` are generated aggregates owned by the parent
post-merge `port-index` workflow. Never hand-edit them, and never commit them in a pull
request. Add or update `docs/port/units/<unit-id>.queue.tsv` and, when there is status
evidence, `docs/port/units/<unit-id>.ledger.tsv`, then validate the shard tree:

```sh
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
```

A pull request that changes either aggregate fails CI, even if it contains freshly generated
output; the aggregate files stop being a per-PR deliverable. The `port-index` workflow is the
only writer: it runs on a push to `main`, validates the shard tree and history against the
previous `main` commit before writing, then commits the regenerated aggregates with a bot
commit. It has no manual-dispatch path and never force-pushes.

The contract is in [`units/README.md`](units/README.md). Unit ids are lowercase letters,
digits, and single hyphens; the units directory accepts only `README.md`, `removed.tsv`, and
correctly named shards, so a misnamed file fails instead of silently vanishing. Ledger history
is append-only in content: never edit, delete, or duplicate an existing row. Add new rows in
any position, including with an earlier instant than rows already recorded; the generated
aggregates are ordered by the parent job, never by hand. `check --against <ref>` fails when a
base shard, queue row, or ledger row is missing or altered at head, and requires `<ref>` to be
a strict ancestor of HEAD. Retiring a unit is explicit: delete its
shards and add a new `removed.tsv` row with a reason and a non-empty `approved-by` in the same
change; retirement records are immutable and captain-authorized, enforced by human review.
`check --shards-only` is the CI pull request gate; a shard with a missing or reordered header,
a ragged row, invalid UTF-8, or a filename/id mismatch fails before any aggregate is written.

## Banned constructs

| Banned | Why | Allowed |
| --- | --- | --- |
| `System.Windows.*` | WPF, Windows-only | `System.Windows.Input.ICommand` only (cross-platform) |
| `System.Drawing.*` | Windows-only in .NET 7+ | None |
| `Microsoft.Win32.*` | Registry, Windows-only | None |
| `DllImport`/`LibraryImport` | OS boundary | Only `Playnite.Platform.Linux` with a reviewed allow-list |
| `System.Management` | WMI, Windows-only | None |
| `BinaryFormatter` | Insecure, removed | None |
| `Thread.Abort` | Unsupported | None |
| `AppDomain.CreateDomain` | Not supported | `AssemblyLoadContext` if needed |
| `Process.Start(string)` with a shell string | Injection/quoting | Argument-list `ProcessStartInfo` |
| `Environment.Exit` in library code | Untestable | Host decides |
| `GC.Collect` | Masking leaks | Only in a benchmark/soak harness with a comment |
| `catch { }` | Hides failures | Catch specific, log, rethrow or return a result |
| `async void` | Unobservable failures | UI event handlers only |
| `.Result`/`.Wait()` | Deadlocks | Top-level CLI only, with a comment |
| `DateTime.Now` in Core | Non-deterministic | `IClock`/`TimeProvider` |
| `Guid.NewGuid()` in pure logic | Non-deterministic | Inject `IGuidProvider` or pass an id |
| `Random` without a seed in logic | Non-deterministic | Inject `Random`/seed |

## Commit checklist

```
[ ] Read the v10 file(s) and the parity row.
[ ] Copied/adapted, not redesigned.
[ ] No banned API outside allow-list.
[ ] Unit tests + edge cases.
[ ] Golden/differential test if boundary.
[ ] Property/fuzz test if parser.
[ ] Cancellation/disposal paths tested.
[ ] Logs/messages preserve v10 where consumed.
[ ] legacy/ and oracle paths untouched.
[ ] Parity row updated with test id.
[ ] Unit shard updated; `check --shards-only` clean; no aggregate files in the PR diff.
[ ] Two adversarial diff-only reviews on the owner's behalf; findings fixed or escalated.
[ ] CI green; counts non-decreasing.
```

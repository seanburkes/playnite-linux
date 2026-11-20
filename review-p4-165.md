# Review — PR #165 (p4-002 CLI command dispatch)

- PR: https://github.com/seanburkes/playnite-linux/pull/165
- Reviewed head: `06f4f42edfc37da3c4160c32f537fd83a4d985d2`
- Reviewed diff: `data/p4-165/candidate.diff`,
  sha256 `cc355a210fb91fc5d8b3b2dc00f4955d785b76e2b692069fcf2c355531b09c4b`
  (10 files, +273/−11). Recomputed `git diff origin/main 06f4f42` is byte-identical.
- Scope: p4-002 wiring only. Static review; no build or test execution.

## Verdict

**PASS — no blocking P1/P2 findings.**

The change does what it claims: `Program` dispatches to one command found in the
`Playnite.Cli` assembly, unknown verbs keep exit code 1 and the existing usage
line, `import-v10` and `--version` are behaviourally unchanged, and the change
adds no UI, network, filesystem, or plugin surface.

## Verification of the requested properties

### 1. Discovery cannot execute arbitrary code or load plugins from the working directory

- `src/Playnite.Cli/CliCommandRegistry.cs:20` scans
  `typeof(CliCommandRegistry).Assembly` — the `Playnite.Cli` assembly — and
  nothing else. There is no argument-controlled assembly name, no
  `Assembly.Load*`/`AssemblyLoadContext`, no directory enumeration, no `*.dll`
  globbing, and no `Type.GetType(string)` anywhere in the file.
- `src/Playnite.Cli/CliCommandRegistry.cs:32` is the only construction site. It
  runs the parameterless constructor of a type that is already in that assembly.
  The only discovered type today is `ImportCommand`
  (`src/Playnite.Cli/ImportCommand.cs:7`), which declares no constructor, so the
  implicit public one has no side effects.
- `--version` and the empty invocation return at `src/Playnite.Cli/Program.cs:12-16`
  before `CliCommandRegistry`'s static initialiser
  (`src/Playnite.Cli/CliCommandRegistry.cs:9`) is touched, so no command is
  constructed on that path.
- The synthetic test command lives in another assembly
  (`tests/Playnite.Core.Tests/Cli/SyntheticCommand.cs`) and is reachable only
  through `Register` (`CliCommandRegistry.cs:15`,
  `tests/Playnite.Core.Tests/Cli/CliCommandRegistryTests.cs:35`); it can never be
  discovered from the product assembly.

### 2. Deterministic and bounded; stable order; no double dispatch

- Discovery is one static pass (`CliCommandRegistry.cs:9,17-36`). `Find`
  (`CliCommandRegistry.cs:13`) is the only lookup and
  `Program.cs:18-20` dispatches exactly once.
- Unknown verbs fail on the documented path: `Program.cs:24-30` prints
  `Unknown command '<name>'.` followed by the existing usage line and returns 1.
  Option-shaped input (`--unknown`) still prints only the usage line and returns
  1, which `tests/Playnite.Smoke.Tests/HostSmokeTests.cs:32-38` already asserts.
- Command lookup is `StringComparer.Ordinal` (`CliCommandRegistry.cs:19`), matching
  the old exact-match dispatch. Duplicate names and missing/empty names fail the
  host instead of silently picking a winner (`CliCommandRegistry.cs:27-30,40-48`).
- Order caveat, non-blocking: `Names` (`CliCommandRegistry.cs:11`) exposes
  `Dictionary.Keys`, whose order follows `Assembly.GetTypes()` — an order the
  runtime does not contractually guarantee. Nothing consumes `Names` in this
  change, so no observable CLI behaviour depends on it.

### 3. `--version` and `import-v10` are unchanged

- `Program.cs:12-16` reproduces the previous empty-args/`--version` branch
  exactly (version string to stdout, exit 0). Covered by
  `tests/Playnite.Smoke.Tests/HostSmokeTests.cs:22-29` for both forms.
- `ImportCommand.cs:7-13` keeps the existing implementation body and receives the
  same post-verb arguments via `Program.cs:20` (`args[1..]`), matching the old
  direct call. The new
  `tests/Playnite.Core.Tests/Cli/CliCommandRegistryTests.cs:43-51` pins its usage
  text and exit code 1, and
  `tests/Playnite.Core.Tests/Library/LibraryServiceImportTests.cs:93` still runs
  the real `import-v10` path.

### 4. Scanning is confined to the intended assembly

Confirmed by `CliCommandRegistry.cs:20`. Nothing derives the scan target from
arguments, configuration, the working directory, or an environment variable.

### 5. No UI or Avalonia dependency is added

`src/Playnite.Cli/Playnite.Cli.csproj` is untouched; the new source files use only
BCL types. `[assembly: InternalsVisibleTo("Playnite.Core.Tests")]`
(`src/Playnite.Cli/AssemblyInfo.cs:3`) grants tests access to internals and adds
no runtime dependency.

## Non-blocking observations (P3)

1. **Eager construction** — `CliCommandRegistry.cs:32` constructs every command on
   the first `Find`, which happens for every invocation except `--version`/no
   args, including a typo'd verb (`Program.cs:18`). Today's only command is
   side-effect-free, but a future command constructor doing I/O would run on an
   unrelated invocation. Consider lazy construction or an explicit
   constructor-purity rule.
2. **`Names` order** — see the order caveat above; sort if it ever feeds help
   output.
3. **Test name overclaims discovery** —
   `tests/Playnite.Core.Tests/Cli/CliCommandRegistryTests.cs:33-41` is named
   "registered from a new file" but registers via `Register`, so it proves
   dispatch works without a `Program` edit, not that assembly scanning finds a
   new file. Discovery itself is covered by `:27-30` against `ImportCommand`
   (converted in place). Renaming or adding a CLI-assembly fixture would make the
   claim exact.
4. **Fragile CLI path in tests** —
   `tests/Playnite.Core.Tests/Cli/CliCommandRegistryTests.cs:23-24` hard-codes the
   `net11.0` TFM and infers the build configuration from a `"/Release/"` segment
   in `AppContext.BaseDirectory`, defaulting to Debug otherwise. The test output
   directory already contains a copy of `Playnite.Cli.dll`, so
   `Path.Combine(AppContext.BaseDirectory, "Playnite.Cli.dll")` would remove both
   assumptions.
5. **Evidence head** — `docs/port/units/p4-002.ledger.d/165-cli-dispatch.tsv:2`
   records linux-ci 44652411756 on `b5ed93c`; `06f4f42` adds only that fragment
   (`git diff b5ed93c 06f4f42` = 1 file, +2 lines). The captured
   implementation/test bytes are therefore the reviewed ones, but the merge
   decision still needs the required checks green on `06f4f42` itself.

## Method and limitations

- Reviewed the diff, the touched sources on the PR head, and the surrounding
  dispatch/smoke tests. No edits, builds, or test runs.
- CI counts in the ledger (Core 2550 / Architecture 199 / Smoke 4) were not
  independently re-executed and are not part of this verdict.

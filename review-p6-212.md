# p6-009 / PR #212 review — sample plugins and headless preview surface

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/212
- Reviewed head: `1f34164b2b12ba7ce0ad64b12349c72c61e7bcaf`
- Base: `6ef2ca5de741e3edd13018d56d579b064de6e485` (current `origin/main`, and the PR
  merge base)
- Candidate diff: `/home/sean/src/firstmate/data/p6-212/candidate.diff`
- Candidate diff SHA-256: `7889fd19807182657a18df9023188e76abeb2728c9c673912a7e06984cb5a40b`
- Size: 27 files, +1004/−0
- Method: fetched `refs/pull/212/head`, recomputed `git diff 6ef2ca5 1f34164b` and confirmed it
  is byte-identical to the candidate diff (same SHA-256), built and tested the reviewed head,
  downloaded the cited CI artifact and re-read its counts, and ran negative probes against the
  compile and load proofs. Proof logs are under
  `/home/sean/src/firstmate/data/p1-review-cd/proof/`, outside the worktree. Worktree restored
  clean afterwards.
- Verdict: **no blocking P1/P2 findings; recommend merge.**

## Blocking findings

None.

## Checks performed

### 1. Scope, base and diff integrity

```sh
git merge-base origin/main 1f34164b                  # 6ef2ca5de741e3edd13018d56d579b064de6e485
git diff 6ef2ca5 1f34164b | sha256sum                # 7889fd19…, identical to candidate.diff
git diff --numstat 6ef2ca5 1f34164b                  # 27 files, 1004 added, 0 deleted
git diff --name-status 6ef2ca5 1f34164b
```

Observed: 27 files — 25 added and 2 modified (`Playnite.slnx`, `docs/port/00-DECISIONS.md`).
Every `src/` and `samples/` entry is status `A` (new file); there is no modification to an
existing `src/Playnite.SDK` file. `git ls-tree 6ef2ca5 src/Playnite.SDK` shows only
`AssemblyMarker.cs` (internal), the csproj and its lock file, and `git ls-tree 6ef2ca5 samples/`
is empty, so the premise correction in the unit document is accurate.

A name-by-name check for `src/Playnite.SDK.Abstractions`, `PublicAPI.*`, `api-baseline`,
`tests/golden`, `tests/fixtures`, `tests/baseline.json`, `docs/formal`,
`docs/port/surface-parity.tsv`, `legacy/` and `oracle-lock/` returned no path. The PR
therefore needs no `oracle-change` label: it changes no protected baseline and does not
redefine correctness.

### 2. Compile proof is real

```sh
dotnet build tests/Playnite.Plugin.Tests/Playnite.Plugin.Tests.csproj \
  -c Release --no-restore -warnaserror -t:Rebuild
```

Observed:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`dotnet restore … --locked-mode -warnaserror` also exited 0, so the committed lock files are
consistent with the project graph. The rebuild compiles the preview SDK, all three samples and
the test project from source.

Negative probe: I changed
`samples/Playnite.Sample.LibraryProvider/Playnite.Sample.LibraryProvider.csproj` to reference
`src/Playnite.SDK.Abstractions` instead of `src/Playnite.SDK`. The sample then fails to
compile:

```text
error CS0246: The type or namespace name 'LibraryPlugin' could not be found
error CS0246: The type or namespace name 'GameMetadata' could not be found
Build FAILED.
```

This proves the samples get `Plugin`/`LibraryPlugin`/`GameMetadata` from the preview
`Playnite.SDK` assembly and do not declare those types themselves. The csproj was restored and
the suite returned to 15/15.

### 3. Load proof is real and load-bearing

The built preview assembly exports exactly the seven documented types, with no `IPlayniteAPI`,
no host service and no UI type:

```text
Playnite.SDK.Models.GameMetadata
Playnite.SDK.Plugins.GenericPlugin
Playnite.SDK.Plugins.GetMetadataArgs
Playnite.SDK.Plugins.IPlayniteExtension
Playnite.SDK.Plugins.LibraryPlugin
Playnite.SDK.Plugins.MetadataPlugin
Playnite.SDK.Plugins.Plugin
```

Its references are `Playnite.SDK.Abstractions`, `System.Collections` and `System.Runtime` only.
Each sample exports one type, and its references are `Playnite.SDK`, `Playnite.SDK.Abstractions`
and framework assemblies — no Core, Desktop, Platform, Avalonia, store, network or database
assembly.

`FakePluginHost` loads sample DLLs from disk with
`AssemblyLoadContext.Default.LoadFromAssemblyPath`, enumerates concrete types assignable to
`IPlayniteExtension`, constructs them and invokes the start/library/metadata paths.
`SamplePluginLoadTests` asserts that `typeof(IPlayniteExtension).Assembly.GetName().Name` is
`Playnite.SDK`, so discovery cannot silently bind to a sample-declared interface.

Negative probe A: I removed `Playnite.Sample.MetadataProvider.dll` from the test output and ran
the suite without rebuilding. It failed rather than skipping:

```text
FileNotFoundException : Sample assembly was not built.
total: 15  failed: 8  succeeded: 7  skipped: 0   (exit 2)
```

Negative probe B: I rebuilt the HelloWorld sample with `Name => "Hello World Probe"` and ran the
suite. Two tests failed against the changed behavior:

```text
Assert.Equal() Failure: Collections differ at index 0
Actual:   "Hello World Probe"
total: 15  failed: 2  succeeded: 13
```

Both probes show the tests observe the actually-built DLLs and invoke the extension points; a
missing or changed sample is a failure, never a skip. The sample sources were restored and the
suite returned to 15/15.

### 4. Preview surface is minimal and honestly labelled

`docs/port/p6-009-sample-plugins.md` §2 matches the built assembly: `IPlayniteExtension`
(`Guid Id`, `string Name`), `Plugin` (abstract, `IDisposable`, `OnApplicationStarted`),
`GenericPlugin`, `LibraryPlugin.GetGames(LibraryGetGamesArgs)`,
`MetadataPlugin.SupportedFields` + `GetMetadata(GetMetadataArgs)`, `GetMetadataArgs`, and
`GameMetadata`. All seven types carry the `PREVIEW, NOT FROZEN` remark. The surface reuses the
frozen `LibraryGetGamesArgs`, `MetadataField`, `IGameView` and `OnApplicationStartedEventArgs`
from Abstractions rather than duplicating them.

ADR-023's addendum states the same: frozen Abstractions are unchanged, the preview is unfrozen,
it is outside the Abstractions PublicAPI/ApiCompat baseline, and **UI extension points remain
deferred to p8-007**. The addendum does not claim v6 source compatibility.

### 5. The premise correction is factually correct

```sh
awk -F'\t' '$2 ~ /^Playnite\.SDK\.Plugins\.[A-Za-z0-9]+$/ {c++} END {print c}' \
  docs/port/p6-008-apicompat-classification.tsv      # 59, all deferred
grep -oP "Playnite\.SDK\.Plugins\.[A-Za-z0-9]+" docs/port/p6-008-frozen-api.txt | sort -u
```

The classification has exactly 59 `Playnite.SDK.Plugins.*` type rows, all `deferred`, and the
frozen text contains only `LibraryGetGamesArgs` and `MetadataField` from that namespace. The
unit's statement that the whole plugin extension surface was deferred at the base is true.

### 6. Test results and CI evidence

Local Release runs at the reviewed head:

```text
Playnite.Plugin.Tests        total 15   failed 0  succeeded 15  skipped 0   exit 0
Playnite.Architecture.Tests  total 210  failed 0  succeeded 210 skipped 0   exit 0
```

The cited linux-ci run `44836110479` is `completed/success` with
`head_sha=2cb6ce6b39a278a42ddf93d42c574322e3d9dfd5`, which is a merge of `origin/main`
`6ef2ca5` into `fm/p6-009`. The PR head `1f34164b` is a docs-only delta from that run head
(`M docs/port/p6-009-sample-plugins.md`, `A docs/port/units/p6-009.ledger.d/212-sample-plugins.tsv`,
`M docs/port/units/p6-009.queue.tsv`), and all eight checks pass on `1f34164b` itself:
`linux-build-and-test`, `AppImage packaging smoke`, `guard-inventory`, `unit-ownership`,
`Flatpak packaging smoke`, `formal`, `unit-evidence`, `inventory-and-parity`.

The downloaded `linux-test-results` artifact reproduces the unit's numbers exactly:

```text
Playnite.Core.Tests          3066/3066 passed, 0 failed, 0 skipped, exit 0
Playnite.Architecture.Tests   210/210  passed, 0 failed, 0 skipped, exit 0
Playnite.Smoke.Tests            4/4    passed, 0 failed, 0 skipped, exit 0
Playnite.Plugin.Tests          15/15   passed, 0 failed, 0 skipped, exit 0
baseline_recorded: true, previous_baseline_recorded: true, failures: []
```

CI discovers the new test project generically (`scripts/ci/list_test_projects.py` scans
`tests/**/*.csproj` for `IsTestProject`/test SDK), so the PR did not need a workflow edit, and
the `unit-evidence` check passing at the PR head shows the queue/ledger shards are well formed.

### 7. Solution wiring, unit shards and packaging

`dotnet sln Playnite.slnx list` contains the three sample projects and
`tests/Playnite.Plugin.Tests` alongside the existing projects. The port-index shard check exits
0:

```text
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
port index history matches origin/main
port index shards are valid; aggregates are generated on main
```

`packaging/assemble.sh` publishes only `Playnite.Cli`, `Playnite.Desktop` and the existing p5
tools, and both packaging smoke checks pass, so the samples are not part of a shipped package.
The names (`Playnite.Sample.*`), the `/samples/` solution folder and the fact that no product
project references a sample keep them distinct from product assemblies.

## Non-blocking observations (P3)

1. `docs/port/p6-009-sample-plugins.md` §1 says the classification covered "all 59
   `Playnite.SDK.Plugins.*` types (including `Plugin`, `LibraryPlugin`, `MetadataPlugin`,
   `GameMetadata` and `Game`)". The 59 count is correct for `Playnite.SDK.Plugins.*`, but
   `GameMetadata` and `Game` are `Playnite.SDK.Models.*` rows (70 model rows, also `deferred`).
   The conclusion stands; the parenthetical should say "plus the deferred `Game` and
   `GameMetadata` models".
2. §3 says the library sample "honours cancellation". The implementation checks
   `args.CancelToken` once before the first `yield`, so it honours a token cancelled before the
   first result, not a token cancelled mid-enumeration. The test name is precise
   (`…before_the_first_result`); §3 could use the same qualifier.
3. `SamplePluginBuildTests.Sample_references_the_preview_sdk_and_no_product_or_ui_assembly`
   denies `Playnite.Core`, `Playnite.Desktop`, `Playnite.Cli`, `Playnite.Platform` and
   `Playnite.Platform.Linux`, but not `Playnite.Steam` or the `Playnite.LegacyImport*` projects,
   while the doc claims no product assembly. The separate csproj assertion that the SDK is the
   only project reference is the stronger guarantee; the deny list should either be completed
   or the doc wording narrowed to what is asserted.
4. The preview's `MetadataPlugin.GetMetadata(GetMetadataArgs)` is a new shape: v10 has
   `GetMetadataProvider(MetadataRequestOptions)` and `GetMetadataFieldArgs`, and no
   `GetMetadataArgs` at all. The document's "keeps the v10 names where they exist" is literally
   true, but the §2 table can be read as v10 shapes; one sentence naming `GetMetadata` as a
   preview-only seam would remove the ambiguity.

## Recommendation

**Merge.** The scope is exactly the new preview types and the three samples plus their tests and
unit docs; the frozen Abstractions and every protected path are untouched; compile, load,
negative-probe, cancellation, solution-wiring and CI evidence all reproduce. The P3 items are
documentation/test-precision wording, not correctness defects, and none of them is blocking.
This review PR contains only this document; merge of
https://github.com/seanburkes/playnite-linux/pull/212 remains with the configured merge
authority.

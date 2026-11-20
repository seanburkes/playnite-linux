# p6-009 / PR #212 review — sample plugins and headless preview plugin surface

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/212
- Reviewed head: `1f34164b2b12ba7ce0ad64b12349c72c61e7bcaf`
- Base: `6ef2ca5de741e3edd13018d56d579b064de6e485` (`origin/main`)
- Candidate diff: `/home/sean/src/firstmate/data/p6-212/candidate.diff`
- Candidate diff SHA-256: `7889fd19807182657a18df9023188e76abeb2728c9c673912a7e06984cb5a40b`
- Size: 27 files, +1004/−0
- Method: checked out the reviewed head in a disposable worktree, restored and built the sample
  and test projects with warnings as errors, ran the plugin and architecture suites, replayed the
  CI evidence from the forge, and ran negative load/discovery probes. Proof output is under
  `/tmp/opencode/p1-review-cc/`, outside the worktree.
- Verdict: **no blocking P1/P2 findings; recommend merge.**

## Blocking findings

None.

## Checks performed

### 1. Diff integrity and scope

```sh
git diff --name-status 6ef2ca5 1f34164b
git diff --stat 6ef2ca5 1f34164b
git diff --name-only 6ef2ca5 1f34164b | grep -E 'Abstractions|PublicAPI|api-baseline|p6-008'
```

Observed: 27 files, +1004/−0, no deletions. Every code file is an addition:

- 7 new files under `src/Playnite.SDK/` (`Models/GameMetadata.cs`; `Plugins/IPlayniteExtension.cs`,
  `Plugin.cs`, `GenericPlugin.cs`, `LibraryPlugin.cs`, `MetadataPlugin.cs`, `GetMetadataArgs.cs`);
- 3 sample projects with source, csproj and `packages.lock.json` under `samples/`;
- 6 new files under `tests/Playnite.Plugin.Tests/`;
- docs: `docs/port/00-DECISIONS.md` (addendum only), `docs/port/p6-009-sample-plugins.md`, and the
  `p6-009` unit shards;
- `Playnite.slnx` (adds the `/samples/` folder and the plugin test project only).

`src/Playnite.SDK.Abstractions`, `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`,
`api-baseline/`, and every `p6-008` artifact are untouched. No `legacy/`, `tests/golden/`,
`tests/fixtures/`, `tests/baseline.json`, `docs/formal/`, `docs/port/surface-parity.tsv`, or
`oracle-lock/` path is changed. The diff is entirely new code plus the explicitly recorded shared
solution edit, so no existing product file was rewritten.

### 2. Compile proof

```sh
dotnet restore tests/Playnite.Plugin.Tests/Playnite.Plugin.Tests.csproj
dotnet build tests/Playnite.Plugin.Tests/Playnite.Plugin.Tests.csproj -c Release --no-restore -warnaserror
for p in HelloWorld LibraryProvider MetadataProvider; do
  dotnet build "samples/Playnite.Sample.$p/Playnite.Sample.$p.csproj" -c Release --no-restore -warnaserror -v q
done
```

Observed: restore clean; test-project build `0 Warning(s), 0 Error(s)`; each sample independently
`Build succeeded. 0 Warning(s), 0 Error(s)`. The test project references the samples, so a sample
that stops compiling fails the test-project build before any load test can run.

Reference inspection of the built sample assemblies shows only `Playnite.SDK` and
`Playnite.SDK.Abstractions` as `Playnite.*` references:

```text
Playnite.Sample.HelloWorld.dll        Playnite.SDK, Playnite.SDK.Abstractions
Playnite.Sample.LibraryProvider.dll   Playnite.SDK, Playnite.SDK.Abstractions
Playnite.Sample.MetadataProvider.dll  Playnite.SDK, Playnite.SDK.Abstractions
```

No Core, Desktop, Cli, Platform, Avalonia, store, network client or database reference appears.

### 3. Test evidence at the reviewed head

```sh
dotnet test --project tests/Playnite.Plugin.Tests/Playnite.Plugin.Tests.csproj -c Release --no-build --no-restore
dotnet test --project tests/Playnite.Architecture.Tests/Playnite.Architecture.Tests.csproj -c Release --no-restore
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
```

Observed:

```text
Playnite.Plugin.Tests       total 15, failed 0, succeeded 15, skipped 0
Playnite.Architecture.Tests total 210, failed 0, succeeded 210, skipped 0
port index shards are valid; aggregates are generated on main
```

### 4. The load proof is real and load-bearing

The fake host loads each sample DLL from the test output with
`AssemblyLoadContext.Default.LoadFromAssemblyPath`, enumerates concrete types assignable to
`typeof(IPlayniteExtension)`, constructs them, and invokes `OnApplicationStarted`, `GetGames` and
`GetMetadata`. The discovery contract is asserted to belong to `Playnite.SDK`
(`typeof(IPlayniteExtension).Assembly.GetName().Name`), and the Hello World test asserts the
observable counter increments to 1 after the start call.

Negative probes, each reverted afterwards (worktree verified clean):

| Probe | Result |
| --- | --- |
| Remove `Playnite.Sample.MetadataProvider` from the host's load list | 4 failures, no skips (`total 12, failed 4`) |
| Delete `Playnite.Sample.MetadataProvider.dll` from the test output | 8 failures, no skips (`total 15, failed 8`), including the explicit `FileNotFoundException` and the missing-assembly build assertion |
| Rename the Hello World implementation class, keeping it a `GenericPlugin` | **15/15 still pass** — discovery is interface-based, not name-based |
| Make the Hello World class a plain class (no `GenericPlugin` base) | 3 failures (`total 15, failed 3`), including discovery count 2 ≠ 3 |

The rename result is a strength, not a gap: discovery does not depend on class names. The
exclusion and missing-assembly probes prove the suite fails closed rather than skipping.

### 5. Preview surface is minimal and honestly labelled

All seven new types carry an XML-doc remark beginning `PREVIEW, NOT FROZEN`:

```text
src/Playnite.SDK/Models/GameMetadata.cs
src/Playnite.SDK/Plugins/GenericPlugin.cs
src/Playnite.SDK/Plugins/GetMetadataArgs.cs
src/Playnite.SDK/Plugins/IPlayniteExtension.cs
src/Playnite.SDK/Plugins/LibraryPlugin.cs
src/Playnite.SDK/Plugins/MetadataPlugin.cs
src/Playnite.SDK/Plugins/Plugin.cs
```

The added source names no `IPlayniteAPI` field, host service, Avalonia type, `UserControl`, or
other UI type; the only `IPlayniteAPI` occurrence is the comment explaining its deliberate
absence. The surface is 7 types: `IPlayniteExtension` (`Guid Id`, `string Name`), abstract
`Plugin` (`OnApplicationStarted`, `Dispose`), `GenericPlugin`, `LibraryPlugin.GetGames`,
`MetadataPlugin.SupportedFields`/`GetMetadata`, `GetMetadataArgs`, and `GameMetadata` with only
the fields the samples use. This matches the p6-009 doc table and the ADR addendum.

v10 comparison (oracle `legacy/source/PlayniteSDK/Plugins/*.cs`): the preview keeps the v10
namespaces and existing type names, but `IPlayniteExtension` and `GetMetadataArgs` are not v10
types, v10 `Plugin` is `IDisposable, IIdentifiable` with an `IPlayniteAPI` field, and v10
`MetadataPlugin.SupportedFields` is `List<MetadataField>` with `GetMetadataProvider` instead of
`GetMetadata`. The doc says "keeps the v10 namespace and type names where they exist" and "does
not claim v6 source compatibility", so this is a documented preview adaptation, not a false
compatibility claim.

### 6. ADR-023 addendum

`docs/port/00-DECISIONS.md` adds an addendum under ADR-023 stating that the frozen Abstractions
are unchanged, the headless preview is explicitly unfrozen, it carries no `IPlayniteAPI`/host
service/UI types, it is outside the Abstractions PublicAPI/ApiCompat baseline, and UI extension
points stay deferred to p8-007. That matches the code and the p6-009 doc/queue row.

### 7. Forge and CI evidence

- The cited linux-ci run is real: run `44836110479`, workflow `linux-ci`, conclusion `success`,
  head `2cb6ce6b39a278a42ddf93d42c574322e3d9dfd5`, base `6ef2ca5`.
  The downloaded `linux-test-results` artifact reports Core 3066/3066, Architecture 210/210,
  Smoke 4/4, Plugin 15/15, all `failed 0`, `skipped 0`, exit 0 — exactly the ledger claim.
- `2cb6ce6..1f34164` is a docs-only delta (3 files, +9/−1), so citing the implementation-head run
  is truthful.
- The final head `1f34164` has its own green linux-ci run `44836700086`, and its artifact reports
  the same 3066/210/4/15 counts with zero failures/skips. All 8 PR checks are green; the PR is
  open, non-draft, and `merge_state: clean`.

### 8. Unit-record claims

- `docs/port/p6-009-sample-plugins.md` matches the implementation: the cited p6-008 classification
  really does carry 59 `Playnite.SDK.Plugins.*` types, all `deferred`; the doc's 15/15 and 210/210
  local counts are reproducible; the solution edit is recorded.
- `docs/port/units/p6-009.queue.tsv` and the ledger fragment name the head/run and the shared
  `Playnite.slnx` edit; the shard passes the port-index check. No aggregate `queue.tsv`/`ledger.tsv`
  is modified.

## Non-blocking observations (P3)

1. **Samples are packable.** `dotnet msbuild samples/Playnite.Sample.HelloWorld/...csproj
   -getProperty:IsPackable` reports `true` and `PackageId=Playnite.Sample.HelloWorld`. No current
   workflow runs `dotnet pack` (`packaging/assemble.sh` publishes only Cli, Desktop, Steam Import,
   Launch and SteamAcceptance), so nothing ships, but adding `<IsPackable>false</IsPackable>` would make the
   "not product assemblies" boundary explicit and future-proof.
2. **The build test checks ProjectReference, not PackageReference.** It asserts the sample has
   exactly `../../src/Playnite.SDK/Playnite.SDK.csproj` as its only project reference, but a future
   `PackageReference` (for example an Avalonia package that is not used and therefore emits no
   assembly reference) would not be caught. The samples currently have no package references, so
   the claim is true today.
3. **One unrequested metadata field is not asserted.** The "returns only the requested fields" test
   requests only `Description` and asserts `Name`, `Developers` and `ReleaseDate` are null; it does
   not assert `Genres` is null. The implementation leaves it null, and the doc's "leaves
   unrequested fields null" holds, but the test could pin all four.
4. **New versus v10 names could be stated more explicitly.** `IPlayniteExtension` and
   `GetMetadataArgs` do not exist in the v10 oracle, and `GetMetadata`/`IReadOnlyList` differ from
   v10's `GetMetadataProvider`/`List<MetadataField>`. The doc's "where they exist" wording is
   accurate, but a one-line "these three names are new/adapted, not v10 shapes" in §2 would stop a
   future reader from treating the preview as a v10 source-compatibility surface.
5. **The p6-008 interface-coverage map remains Abstractions-only.** `SdkFreezeTests` enumerates
   `Playnite.SDK.Abstractions` interfaces (13 rows) and does not list the new SDK-owned
   `IPlayniteExtension`. That is consistent with the addendum (the preview is outside the freeze),
   but the test name "Every_SDK_interface..." now reads slightly broader than what it covers.

## Bottom line

The PR does what it says: it adds a small, explicitly unfrozen headless plugin surface and three
real sample plugins that compile with warnings as errors against `Playnite.SDK` alone, and a fake
host proves reflection-based discovery, construction and invocation of the SDK-owned contract. The
load proof fails closed when a sample is excluded, a built assembly is missing, or the
implementation stops being a plugin; the discovery test is contract-based rather than name-based.
Abstractions, PublicAPI and api-baseline are untouched, UI extension points stay deferred, and the
cited CI/artifact counts are reproducible on both the implementation head and the final head. The
remaining notes are P3 hardening/documentation items, not merge blockers.

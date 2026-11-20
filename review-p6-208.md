# p6-008 / PR #208 review — SDK Abstractions freeze and v6→v7 ApiCompat report

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/208
- Reviewed head: `7241628fe70f3970096cebb3da816101b75efb5b`
- Base: `226739b37eef68cc50541a34a8120169864d24ae` (current `origin/main`)
- Candidate diff: `/home/sean/src/firstmate/data/p6-208/candidate.diff`
- Candidate diff SHA-256: `acdd776e77b33edad844ebc59ccbbe64de6011441f82457f424f4d609cd8511d`
- Size: 13 files, +4258/−8
- Method: applied the candidate diff to a clean worktree at the stated base, rebuilt the
  Abstractions project and both affected test projects, re-ran the pinned PlayniteSDK 6.18.0
  ApiCompat comparison, replayed the reviewed suppression file, and ran a negative freeze probe.
  Proof logs are under `/home/sean/src/firstmate/data/p1-review-cb/proof/`, outside the worktree.
- Verdict: **no blocking P1/P2 findings; recommend merge.**

## Blocking findings

None.

## Checks performed

### 1. Scope and base

```sh
git diff --name-status 226739b 7241628f
git merge-base --is-ancestor 226739b 7241628f
git rev-parse 226739b origin/main
git diff --stat 226739b 7241628f
```

Observed: the base is exactly `origin/main` (`226739b37eef68cc50541a34a8120169864d24ae`),
and the diff is 13 files / +4258 / −8: the eight `p6-008` docs/shards, the three
`src/Playnite.SDK.Abstractions/api-baseline/*` files, and the two new test files
(`SdkFreezeTests.cs`, `SdkContracts/SdkInterfaceContractTests.cs`). A name-by-name check for
files outside the `p6-008` namespace and the shared `api-baseline/` directory returned `NONE`.
There is no product-source move and no `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`
change; the PR carries the `oracle-change` label for the protected `api-baseline/` payload.

### 2. PublicAPI freeze is real

```sh
dotnet build src/Playnite.SDK.Abstractions/Playnite.SDK.Abstractions.csproj \
  -c Release -t:Rebuild -warnaserror
```

Observed:

```text
APICompat ran successfully without finding any breaking changes.
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

The project wires `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0 with
`TreatWarningsAsErrors=true`, so `RS0016` (undeclared public symbol) and `RS0017` (stale
declaration) are build errors. A clean rebuild therefore proves the committed text surface is
complete and has no stale declaration. There is no separate regeneration executable in the
repo; the analyzer-clean rebuild is the regeneration check.

Committed counts match the freeze document:

| File | Types | Declarations |
| --- | ---: | ---: |
| `PublicAPI.Shipped.txt` | 7 | 61 |
| `PublicAPI.Unshipped.txt` | 55 | 542 |
| `p6-008-frozen-api.txt` (non-comment) | 62 | 603 |

`git diff 226739b 7241628f -- src/Playnite.SDK.Abstractions/PublicAPI.Shipped.txt
src/Playnite.SDK.Abstractions/PublicAPI.Shipped.sha256` is empty; the shipped text and its
review checksum are unchanged. The Unshipped text is honestly described as the moved surface
(55 types / 542 declarations), not as shipped.

### 3. The api-baseline binary corresponds to the merged sources

```sh
sha256sum src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.dll
stat -c '%s' src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.dll
sha256sum src/Playnite.SDK.Abstractions/obj/Release/net11.0/ref/Playnite.SDK.Abstractions.dll
cat src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.sha256
```

Observed, after the rebuild above from the merged sources:

```text
e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8  api-baseline/Playnite.SDK.Abstractions.dll
29696 api-baseline/Playnite.SDK.Abstractions.dll
e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8  obj/Release/net11.0/ref/Playnite.SDK.Abstractions.dll
e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8
```

The regenerated reference assembly is byte-for-byte the committed pin (same size, same
SHA-256), and the shipped build's ApiCompat pass used that baseline with no suppression.

### 4. ApiCompat v6→v7 is reproducible

The v6 left side was extracted from a locally cached copy of the published package; both
hashes were re-computed:

```text
playnitesdk.6.18.0.nupkg       63ae943bf226a782738d3d6b9977dcb77861e5d2ea1aadcf620332618c93e236
lib/net462/Playnite.SDK.dll    ce7511941aba4d4b17dd7b226fadfd0ecefdf51ab73775b25631089fd7542fc7
```

Both match `docs/port/p6-008-sdk-freeze.md` and the ledger.

The right side was a scratch build of exactly `src/Playnite.SDK.Abstractions/**/*.cs` with
`AssemblyName=Playnite.SDK`, `Version=7.0.0.0`, `net11.0`, and no other source. The comparison
used `Microsoft.DotNet.ApiCompat.Task` 11.0.100 with `LeftAssemblies`/`RightAssemblies` and
`RoslynAssembliesPath` and no explicit reference lists. That is the task-level equivalent of
the documented CLI invocation and reproduces its output:

```sh
dotnet msbuild /home/sean/src/firstmate/data/p1-review-cb/apicompat-norefs.proj -t:Validate
```

Observed: 306 differences, with the exact per-code counts:

```text
208 CP0001
 82 CP0002
 15 CP0006
  1 CP0009
```

After normalizing the two assembly paths to `v6-baseline` / `v7-candidate`, the 306 generated
lines are identical to `docs/port/p6-008-apicompat-v6-v7.txt` as a set (`diff` of the sorted
files exits 0). The committed report is sorted by code/target; a raw run emits the same lines
in a different order.

The classification file has 306 unique rows with exactly 114 `adapted`, 178 `deferred`, 14
`excluded`, and no `breaking` row. The 98 `CP0002`/`CP0006`/`CP0009` rows are the intentional
v7 host-surface adaptations; ADR-018 (Avalonia-free abstractions, v6 shape preserved for
non-UI contracts, no binary-compat promise) and ADR-023 (UI/abstraction compatibility scoped)
justify accepting them, and the document says so instead of hiding them behind a suppression
trick. The shipped Abstractions build validates against its own v7 baseline and suppresses no
diagnostic.

### 5. Suppression replay exits 0

The reviewed XML contains 306 `<Suppression>` entries, one per difference, each with a
specific `T:`/`M:`/`F:`/`P:`/`E:` target and no wildcard. Replaying it requires substituting the
two label placeholders with the actual assembly paths, as the file header says:

```sh
# <Left>v6-baseline</Left>  -> <Left>…/lib/net462/Playnite.SDK.dll</Left>
# <Right>v7-candidate</Right> -> <Right>…/scratch/bin/Release/net11.0/Playnite.SDK.dll</Right>
dotnet msbuild /home/sean/src/firstmate/data/p1-review-cb/apicompat-replay-norefs.proj -t:Validate
```

Observed: exit 0 and an empty log. Every one of the 306 generated diagnostics matched exactly
one reviewed suppression, so the suppression file covers only the classified differences and
does not suppress a category wholesale.

### 6. Interface coverage and freeze enforcement

- `p6-008-interface-coverage.tsv` has one row for each of the 13 public interfaces in the
  built assembly; `SdkFreezeTests.Every_SDK_interface_has_a_fake_host_contract_test` passed.
- `SdkInterfaceContractTests` has 13 `[Fact]` methods and 13 fakes; the fakes implement the
  interfaces at compile time, so a removed or renamed member fails the test build. The tests
  exercise every member of the small host interfaces and all 27 `IDataSerializer` methods.
- Negative probe for an added declaration: I appended `Playnite.SDK.P6Probe` to
  `PublicAPI.Unshipped.txt` and added a public `P6Probe` class. The first build failed with
  `error RS0016: Symbol 'Playnite.SDK.P6Probe.P6Probe() -> void' is not part of the declared
  public API`. After also declaring the constructor, the build passed and `SdkFreezeTests`
  failed 2 of 8:

```text
failed SdkFreezeTests.Frozen_assembly_exports_exactly_the_declared_types
failed SdkFreezeTests.Frozen_manifest_is_the_committed_PublicAPI_surface
Test run summary: Failed! failed: 2
```

The probe and its PublicAPI lines were then removed; the worktree was clean again before this
review was written. This proves the freeze fails on an added declaration even when the
analyzer is satisfied. Removed/renamed declarations are caught by `RS0017` and by the
compile-time fake implementations.

### 7. Sample plugin compiles against the Abstractions assembly alone

`SdkFreezeTests.Sample_plugin_compiles_against_the_abstractions_assembly_alone` compiles a
consumer of `INotificationsAPI`, `IDialogsFactory`, `IMainViewAPI`, `IUriHandlerAPI` and
`IGameView` against `Abstractions.Location` plus framework/System references only, filtering
out every `Playnite.*` assembly. It passed as part of the 8-test `SdkFreezeTests` run.

### 8. Test suites and docs honesty

```sh
dotnet test tests/Playnite.Architecture.Tests/... -c Release
dotnet test tests/Playnite.Core.Tests/... -c Release
```

Observed:

```text
Architecture: total 210, failed 0, succeeded 210, skipped 0
Core:         total 3066, failed 0, succeeded 3066, skipped 0
```

These match the ledger's claimed CI counts. The ledger's CI run `44830148388` is
`completed/success` on validated head `850e50f8fdf53b00129b5b7b437eb1059260d92a`; the PR head
`7241628f` is the docs-only delta after that head, the PR base is `226739b`, and the PR reports
8/8 checks passing. The queue row, freeze document and ledger all cite the merged p6-006
(#202, `d1d57be`) and p6-007 (#201, `b80847b`) moves, and the counts/hashes in them match the
artifacts re-derived here.

## Non-blocking observations (P3)

1. The ledger says the report and suppression file reproduce "byte-for-byte". The committed
   report is a sorted rendition of the 306 tool lines; a raw run emits the same lines in a
   different order. If "byte-for-byte" is meant literally, say "the same 306 lines, sorted" or
   include the sort in the regeneration command.
2. `p6-008-sdk-freeze.md` §5 says each contract test calls its members "through the real Core
   service". That is true for the host interfaces, but `IDataSerializer`, `IIdentifiable` and
   `IGameView` have no Core service; those three tests use the fake directly.
3. `p6-008-sdk-freeze.md` §4 describes `NotificationMessage` as "8 v6 ICommand/event/activation
   members removed"; the 8 rows also include the v6 four-argument constructor.
4. The suppression file's `<Left>`/`<Right>` values are review labels, not assembly paths; the
   header documents the substitution, but a copy-pasteable replay command in the freeze doc
   would make the exit-0 evidence easier to re-run.

## Recommendation

**Merge.** The unit is a genuine freeze-only change over the already-merged surface. The
PublicAPI text, binary baseline, ApiCompat report, classification, suppression replay,
interface coverage, sample-plugin compile proof, dependency proof and CI evidence are all
reproducible. The non-blocking observations are wording/documentation precision issues, not
correctness defects.

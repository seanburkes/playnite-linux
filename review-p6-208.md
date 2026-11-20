# Adversarial review — https://github.com/seanburkes/playnite-linux/pull/208 (p6-008 SDK Abstractions freeze)

**Verdict: CLEAN — no blocking P1 or P2. The freeze is real, the binary baseline reproduces
from the merged sources, the v6→v7 ApiCompat report reproduces 306/306 and the suppression
file suppresses exactly the classified differences. Hold for the captain's merge decision.**
Review-only: no product change was authored by this review.

| | |
|---|---|
| Repository | https://github.com/seanburkes/playnite-linux |
| Pull request | https://github.com/seanburkes/playnite-linux/pull/208 |
| Reviewed head | `7241628fe70f3970096cebb3da816101b75efb5b` (`7241628`, `docs(p6-008): record the PR and linux-ci freeze evidence`) |
| Base / merge base | `226739b37eef68cc50541a34a8120169864d24ae` (`226739b`, current `origin/main`; `git merge-base` equals it) |
| Candidate diff | task material `data/p6-208/candidate.diff`, sha256 `acdd776e77b33edad844ebc59ccbbe64de6011441f82457f424f4d609cd8511d` |
| Diff shape | 13 files, +4258 / −8 (`git diff --shortstat`); plain `git diff` digest equals the candidate digest byte-for-byte |
| PR state | open, **not draft**, `mergeStateStatus: CLEAN`, `oracle-change` label present, 8/8 checks pass |
| CI | run [44830738130](https://github.com/seanburkes/playnite-linux/actions/runs/44830738130) on `7241628` success; run [44830148388](https://github.com/seanburkes/playnite-linux/actions/runs/44830148388) on `850e50f` success |

The reviewed tree is the PR head. Commands below were run from the repository root;
`<proof>` and `<sdk>` stand for the local proof directory and the .NET SDK 11.0.100 directory.
The diff digest reproduces exactly:

```console
$ sha256sum data/p6-208/candidate.diff
acdd776e77b33edad844ebc59ccbbe64de6011441f82457f424f4d609cd8511d  .../candidate.diff

$ git diff 226739b37eef68cc50541a34a8120169864d24ae 7241628fe70f3970096cebb3da816101b75efb5b | sha256sum
acdd776e77b33edad844ebc59ccbbe64de6011441f82457f424f4d609cd8511d  -

$ git diff --shortstat 226739b..7241628
 13 files changed, 4258 insertions(+), 8 deletions(-)
```

## 1. Scope — freeze only, no move work, no foreign product edit

`git diff --name-status origin/main...origin/pr208` returns exactly 13 paths:

```
A docs/port/p6-008-apicompat-classification.tsv
A docs/port/p6-008-apicompat-suppressions.xml
A docs/port/p6-008-apicompat-v6-v7.txt
A docs/port/p6-008-frozen-api.txt
A docs/port/p6-008-interface-coverage.tsv
A docs/port/p6-008-sdk-freeze.md
A docs/port/units/p6-008.ledger.d/208-sdk-freeze.tsv
A docs/port/units/p6-008.queue.tsv
M src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.dll
M src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.sha256
M src/Playnite.SDK.Abstractions/api-baseline/README.md
A tests/Playnite.Architecture.Tests/SdkFreezeTests.cs
A tests/Playnite.Core.Tests/SdkContracts/SdkInterfaceContractTests.cs
```

No `src/` product source other than the shared SDK binary baseline changed. There is no
move work and no p6-006/p6-007 product edit in this diff. The branch starts at the current
`origin/main` (`merge-base == 226739b`), and the queue/ledger cite the merged moves
(`d1d57be` for https://github.com/seanburkes/playnite-linux/pull/202, `b80847b` for
https://github.com/seanburkes/playnite-linux/pull/201).

The repository's own guards agree:

```console
$ python3 scripts/port/check_unit_ownership.py --base 226739b --head 7241628 --branch fm/p6-008c
unit: p6-008
offending paths:
  (none)
EXIT=0

$ GH_TOKEN=… python3 scripts/port/check_unit_evidence.py --base 226739b --head 7241628 \
    --pr 208 --branch fm/p6-008c --repo-slug seanburkes/playnite-linux --verify-ci
unit: p6-008
queue: docs/port/units/p6-008.queue.tsv status done
fragment: docs/port/units/p6-008.ledger.d/208-sdk-freeze.tsv (head 850e50f…, run 44830148388)
EXIT=0

$ python3 scripts/port/build_port_index.py check --shards-only --against origin/main
port index shards are valid; aggregates are generated on main   (exit 0)
```

`api-baseline/` is a protected path; the `oracle-change` label is present
(`gh-axi pr view 208 --fields labels` → `oracle-change`), and the protected-path checks
(`guard-inventory`, `inventory-and-parity`) pass.

## 2. PublicAPI freeze is real

Rebuilt the Abstractions project in Release with warnings as errors; both PublicAPI analyzers
are active and `RS0016`/`RS0017` are errors:

```console
$ dotnet build src/Playnite.SDK.Abstractions/Playnite.SDK.Abstractions.csproj -c Release -warnaserror
Build succeeded.
    0 Warning(s)
    0 Error(s)
       ApiCompatValidateAssembliesCore:
         APICompat ran successfully without finding any breaking changes.
```

The analyzer is genuinely enforcing both directions, proved on a throwaway copy outside the
worktree (same sources and PublicAPI text):

```console
# remove the IGameView type declaration from PublicAPI.Unshipped.txt
$ dotnet build src/Playnite.SDK.Abstractions/... -c Release
error RS0016: Symbol 'Playnite.SDK.Models.IGameView' is not part of the declared public API
Build FAILED.

# append a stale declaration
$ echo 'Playnite.SDK.Models.IGameView.Bogus.get -> int' >> PublicAPI.Unshipped.txt
error RS0017: Symbol 'Playnite.SDK.Models.IGameView.Bogus.get -> int' is part of the declared API,
              but is either not public or could not be found
Build FAILED.
```

Regeneration produces no diff:

```console
$ dotnet format analyzers src/Playnite.SDK.Abstractions/Playnite.SDK.Abstractions.csproj \
    --diagnostics RS0016 RS0017 --severity error
Formatted 0 of 24 files.
$ sha256sum -c <scratch>/publicapi-before.sha
src/.../PublicAPI.Shipped.txt: OK
src/.../PublicAPI.Unshipped.txt: OK
```

Surface counts are independently recomputed and match the docs exactly:

```console
shipped 61 declarations / 7 types
unshipped 542 declarations / 55 types
total 603 declarations / 62 types
p6-008-frozen-api.txt == PublicAPI.Shipped.txt + PublicAPI.Unshipped.txt   (True)
```

`PublicAPI.Shipped.txt` is byte-identical to `origin/main` and its checksum pin is unchanged
(`sha256 8d54786f51b13af2d188898ebd4db3c06b5d3e4dee309dcb1ef31d335f9a61ca`, matching
`PublicAPI.Shipped.sha256`). `PublicAPI.Unshipped.txt` is also unchanged vs `origin/main`.
The "unshipped" placement is honest: a v7 assembly that has not shipped keeps its declarations
in `Unshipped`.

## 3. Binary baseline corresponds to the merged sources

The committed reference assembly is 29,696 bytes and matches its own pin:

```console
$ sha256sum src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.dll
e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8
$ cat src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.sha256
e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8
```

An independent rebuild of the same sources reproduces those exact bytes when the build embeds
the same source revision (`226739b`, the base commit whose sources are identical to the PR
head):

```console
$ dotnet build src/Playnite.SDK.Abstractions/... -c Release \
    -p:SourceRevisionId=226739b37eef68cc50541a34a8120169864d24ae
$ sha256sum src/Playnite.SDK.Abstractions/obj/Release/net11.0/ref/Playnite.SDK.Abstractions.dll
e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8
```

At the PR head without that pin, size is still 29,696 and only the embedded
`AssemblyInformationalVersion` differs (`7.0.0+7241628f…` vs `7.0.0+226739b3…`), which is why
the hash differs (`11cfbf6a…`); the API surface is unchanged. This is a documentation nuance,
not a correctness defect — see non-blocking observations.

The shipped build validates against this baseline with no suppression file
(`ApiCompatValidationSuppressions` is not set anywhere in the project).

## 4. ApiCompat v6→v7 reproduces on the merged surface

The v6 baseline was re-downloaded from NuGet and its hashes independently verified:

```console
$ curl -sSL -o playnitesdk.6.18.0.nupkg \
    https://api.nuget.org/v3-flatcontainer/playnitesdk/6.18.0/playnitesdk.6.18.0.nupkg
$ sha256sum playnitesdk.6.18.0.nupkg
63ae943bf226a782738d3d6b9977dcb77861e5d2ea1aadcf620332618c93e236
$ unzip -p … lib/net462/Playnite.SDK.dll | sha256sum
ce7511941aba4d4b17dd7b226fadfd0ecefdf51ab73775b25631089fd7542fc7
```

Right side is a scratch compilation of the PR head's
`src/Playnite.SDK.Abstractions/**/*.cs` with `AssemblyName=Playnite.SDK`,
`AssemblyVersion=7.0.0.0`, `net11.0`, and no other source. Tool:
`Microsoft.DotNet.ApiCompat.Tool 11.0.100` (`11.0.100+297acf93…`).

```console
$ apicompat --left <proof>/v6-extract/lib/net462/Playnite.SDK.dll \
    --right <proof>/compat/bin/Release/net11.0/Playnite.SDK.dll \
    --roslyn-assemblies-path <sdk>/Roslyn/bincore
EXIT=1
$ grep -c '^CP' run.log
306
$ grep -o '^CP[0-9]*' run.log | sort | uniq -c
    208 CP0001
     82 CP0002
     15 CP0006
      1 CP0009
```

Normalizing the two absolute paths to `v6-baseline` / `v7-candidate` and sorting gives a
byte-identical set to the committed `docs/port/p6-008-apicompat-v6-v7.txt`:

```console
$ diff mine.txt committed.txt
IDENTICAL: all 306 normalized lines byte-for-byte
```

The report is a sorted normalization (the raw tool emits a different order); that is within
the file's "normalized" claim.

Classification counts recomputed from the TSV match the docs:

```
306 rows: 114 adapted / 178 deferred / 14 excluded
by code: 208 CP0001 / 82 CP0002 / 15 CP0006 / 1 CP0009
(CP0002 82 + CP0006 15 + CP0009 1 = 98 member breaks; CP0001 16 adapted + 178 deferred + 14 excluded)
```

The 98 member breaks are real ApiCompat breaks, but they are the intended p6-006/p6-007
adaptations: `docs/port/00-DECISIONS.md` ADR-018 explicitly declines binary compatibility and
scopes "source-compatible", and ADR-023 scopes the compatibility claim to the Abstractions
surface with UI extension points adapted. Each row's `decision` text cites ADR-018/ADR-023.
"No breaking row" is therefore a classification decision, not a claim that v6 binaries load.

## 5. Suppression replay is precise, not wholesale

The committed suppression file is documentation evidence for the v6 report and carries the
review labels `v6-baseline` / `v7-candidate` in `Left`/`Right`; its header says to substitute
the two assembly paths before replaying. As-is it does not replay (exit 1), which is expected
by that header. After substitution:

```console
$ apicompat --left <proof>/v6-extract/lib/net462/Playnite.SDK.dll --right <proof>/compat/bin/Release/net11.0/Playnite.SDK.dll \
    --roslyn-assemblies-path <sdk>/Roslyn/bincore \
    --suppression-file substituted-suppressions.xml
APICompat ran successfully without finding any breaking changes.
EXIT=0
```

It is not a category-wide blanket: 306 `<Suppression>` blocks, 306 distinct `<Target>`s, no
wildcards, and the DiagnosticId counts are 208/82/15/1. A negative control that removes only
the `CP0009` block surfaces exactly that one difference and exits 1:

```console
$ apicompat … --suppression-file negative-suppressions.xml
CP0009: Type 'Playnite.SDK.NotificationMessage' has the sealed modifier on v7-candidate but not on v6-baseline
EXIT=1
```

The tool's own generated suppression file has an identical 306-target set to the committed
file:

```console
$ apicompat … --generate-suppression-file --suppression-output-file generated-suppressions.xml
$ python3 …   # compare <Target> sets
generated 306 committed 306
target sets equal: True
```

The shipped build's own ApiCompat run uses no suppression at all.

## 6. Interface coverage and sample-plugin compile

`SdkInterfaceContractTests` is a real contract test, not a smoke stub: the fakes implement the
interfaces at compile time, and each test drives every member through the real Core service
(`NotificationsApi`, `DialogsFactory`, `MainViewApi`, `UriHandlerApi`, `HtmlDescriptionService`).
`IDataSerializer` asserts 27 distinct member calls.

```console
$ dotnet test tests/Playnite.Core.Tests -c Release --filter "FullyQualifiedName~SdkInterfaceContractTests"
total: 13, failed: 0, succeeded: 13, skipped: 0

$ dotnet test tests/Playnite.Architecture.Tests -c Release --filter "FullyQualifiedName~SdkFreezeTests"
total: 8, failed: 0, succeeded: 8, skipped: 0
```

`Every_SDK_interface_has_a_fake_host_contract_test` derives the interface set by reflection
over the built assembly and asserts the 13-row coverage map matches it; it fails if a new
interface has no row. The sample-plugin test compiles a source using the moved host surfaces
against the Abstractions assembly plus framework references only, excluding every other
`Playnite.*` assembly, so a Core type leak fails the compile.

## 7. Docs and evidence honesty

- The queue row is `p6-008 … status done owner fm/p6-008c`, and the PR head ref is
  `fm/p6-008c`.
- The ledger fragment cites head `850e50f8fdf53b00129b5b7b437eb1059260d92a` and run
  `44830148388`; the GitHub API confirms that run is a completed successful `linux-ci` run on
  that head. The fragment explicitly says it is a docs-only delta committed after the cited
  head.
- Downloaded `test-counts.json` for both cited runs report:
  `Core 3066/3066`, `Architecture 210/210`, `Smoke 4/4`, `failed: 0`, `skipped: 0`, exit 0
  for each project.
- `python3 -m unittest discover -s scripts/port -p 'test_*.py'` → `Ran 261 tests … OK`,
  matching the ledger claim.
- Full local suites on the reviewed head agree with CI:
  Architecture `210 passed, 0 failed, 0 skipped`; Core `3066 passed, 0 failed, 0 skipped`.
- No `TODO`, `NotImplementedException`, placeholder, `[Skip]`, or test deletion in the new
  files. Nothing in the diff weakens a test.
- Core references only `Playnite.SDK.Abstractions` among `Playnite.*`; Abstractions has no
  `Playnite.*` or Avalonia reference. `Core_and_abstractions_have_no_UI_dependency` passes.

## Non-blocking observations (no action required for merge)

1. **Suppression replay needs path substitution.** The committed file stores review labels in
   `Left`/`Right`, so running it verbatim exits 1. The file header and the PR body both say to
   substitute the paths, and after substitution it exits 0. Worth knowing for anyone who
   replays it directly.
2. **The baseline hash is commit-stamped.** The committed reference assembly embeds
   `AssemblyInformationalVersion 7.0.0+226739b…`. A rebuild at the PR head produces the same
   size and API surface but a different hash (`11cfbf6a…`) because the embedded commit differs;
   rebuild with `-p:SourceRevisionId=226739b37eef68cc50541a34a8120169864d24ae` to reproduce
   `e79568cb…` exactly. The README does not mention the commit stamp.
3. **Minor wording.** `p6-008-sdk-freeze.md` §1 says the 62 types are "the seven shipped seed
   contracts plus 55 declarations"; the 55 is types (the declarations count is 542). The
   counts elsewhere are correct.
4. `Frozen_manifest_is_the_committed_PublicAPI_surface` is a document-to-document equality
   check; the source-to-text link is enforced by the build's RS0016/RS0017, which this review
   proved independently with positive and negative builds.

## What this review did not run

- The full solution Release build and packaging smokes (parent-owned); only the Abstractions
  project, Architecture.Tests and Core.Tests were built/tested locally. CI's linux-ci run on
  the reviewed head covers the solution build and the packaging smokes.
- The bounded serialisation fuzz and the native Xvfb smoke (environment prerequisites); CI
  covers these where configured.
- No test was skipped or filtered to reach the reported counts.

**Conclusion:** the freeze deliverable is accurate and reproducible: PublicAPI text equals the
merged source surface, the binary baseline reproduces from those sources with its pin, the
v6→v7 report reproduces 306/306 with correct 208/82/15/1 and 114/178/14/0 classification, and
the suppression file is a precise one-per-difference document. CLEAN for blocking P1/P2.

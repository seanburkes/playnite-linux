# Gate P6 — Phase 6 exit-gate evidence assessment (EVIDENCE ONLY)

- Date: 2026-11-20
- Base: merged `main` `481c96575b055b15807b433f4c30d1991c184e8a` (post-merge aggregates), whose
  parent `8f8f89e` is the Phase 6 merge `#215` (`p6-010`). All command output below was produced on
  this exact checkout (detached `main` at `481c965`, branch `fm/p6-shards5` cut from it).
- Owner: `p6-shards` (resumed board unit; no new unit id).
- Result: **3 PROVEN / 1 PARTIAL / 0 DEFERRED.**
- **This is an evidence assessment, not an acceptance.** It deliberately does not claim the gate.
  Acceptance is the captain's decision after review; this file is named `P6-pending.md`
  deliberately and **`P6-accepted.md` is not written by this worker**.
- Review basis: real commands run on merged `main`; exact stdout/stderr and exit codes are quoted.
  Raw logs are kept in worker scratch outside the worktree (not committed, not an oracle).
  No fixture, run, output or exit code is fabricated. Where a
  positive result could not be produced from a committed artifact, the gap is named and the item is
  marked `PARTIAL`.
- Environment: `dotnet --version` → `11.0.100`; `python3 --version` → `3.14.10`.

## Exit criteria summary

| # | Criterion | Status | Concrete artifact or exact gap |
| --- | --- | --- | --- |
| 1 | `Playnite.Cli` import/list/search/launch --dry-run on a fixture library, no UI assembly loaded | **PARTIAL** | On the committed v10 fixture, `import`, `list` and `search` succeed (exit 0) and `launch --dry-run` runs and returns the documented `NoPlayAction` exit 5 with a reason — but that fixture has **only library-plugin-action games**, so a positive `started:false` launch plan cannot be produced from a committed fixture. Missing evidence: a committed fixture library containing a game with a stored play action. No-UI condition is proven: the CLI project graph and deps closures contain no Avalonia/Desktop assembly. |
| 2 | Abstractions build with ApiCompat; reviewed v6→v7 report; samples compile and load; PublicAPI regeneration has no diff | **PROVEN** | `dotnet build src/Playnite.SDK.Abstractions/... -c Release --no-incremental -warnaserror`: exit 0, `APICompat ran successfully without finding any breaking changes`. Committed p6-008 artifacts carry exactly 306 differences (208 `CP0001` / 82 `CP0002` / 15 `CP0006` / 1 `CP0009`) classified 114 adapted / 178 deferred / 14 excluded / 0 breaking. `Playnite.Plugin.Tests`: 15 passed, 0 failed, 0 skipped, exit 0. PublicAPI regeneration in a scratch copy: `NO DIFF` on both declaration files and `--verify-no-changes` exit 0 under RS0016/RS0017-as-errors. |
| 3 | No Core project references Avalonia directly or transitively | **PROVEN** | `Playnite.Architecture.Tests` 210 passed / 0 failed / 0 skipped (exit 0); the four direct graph/no-Avalonia tests pass (exit 0); evaluated graph shows `Playnite.Core` → only `Playnite.SDK.Abstractions`, with zero Avalonia packages in its transitive package closure. |
| 4 | Contract tests for every SDK interface with a fake host | **PROVEN** | 13 interfaces (3 seed + 6 added by p6-006 + 4 added by p6-007) all appear in `p6-008-interface-coverage.tsv` with a fake class, a test method and a test file. Reflection test `Every_SDK_interface_has_a_fake_host_contract_test` passes (exit 0); the Core fake-host contract classes pass 26/0/0 (exit 0). No interface lacks a fake-host test. |

Summary: **3 PROVEN / 1 PARTIAL / 0 DEFERRED.** Criterion 1 is `PARTIAL` solely because the committed
fixture cannot drive the positive `launch --dry-run` plan; the command itself runs and returns a
documented exit code. Nothing is silently dropped. This file does not accept the gate.

---

## Criterion 1 — CLI without the UI

All commands were run as real processes against merged `main` at `481c965`. Fixture used: the
committed v10 library `tests/fixtures/v10/library`; reader: the Release-built
`src/Playnite.LegacyImport/bin/Release/net11.0/Playnite.LegacyImport.dll`. Destination SQLite was
written under worker scratch, never into the worktree. `REPO` below denotes the repository
checkout root (the disposable task worktree), and `SCRATCH` denotes a scratch directory outside it.

### 1a. Build the two executables used

```
dotnet build src/Playnite.Cli/Playnite.Cli.csproj -c Release --nologo -v minimal
  -> Build succeeded. 0 Warning(s), 0 Error(s). exit 0
dotnet build src/Playnite.LegacyImport/Playnite.LegacyImport.csproj -c Release --nologo -v minimal
  -> Build succeeded. 0 Warning(s), 0 Error(s). exit 0
```

### 1b. Import on the committed fixture — PROVEN

Command (exit **0**):

```
dotnet $REPO/src/Playnite.Cli/bin/Release/net11.0/Playnite.Cli.dll import \
  $REPO/src/Playnite.LegacyImport/bin/Release/net11.0/Playnite.LegacyImport.dll \
  $REPO/tests/fixtures/v10/library \
  $SCRATCH/fixture/library.sqlite
```

Output (exit 0):

```
{"Outcome":"Published","Report":{"Version":1,"SourceHash":"218fb2b2297c5538aa2ebabe8c43427ee80cb9f5e72bfbe31d380d03a1f9b8f1","SourceManifestHash":"63e79f54301a438685765ff8e8a9ba227c390b58016233247ea5fdfed3bba9d9","ExpectedSteam":3,"ImportedSteam":3,"DryRun":false, ... ,"MissingAssets":0,"UnreadableAssets":0,"EmptyAssets":0}}
```

`Outcome=Published`, `ImportedSteam=3`; the destination database was created.

### 1c. List on that library — PROVEN

Command: `dotnet ...Playnite.Cli.dll list --library <dest.sqlite> --limit 5` — exit **0**:

```
{"kind":"page","totalCount":3,"offset":0,"limit":5,"returned":3}
{"kind":"game","id":"10000000-0000-0000-0000-000000000001","gameId":"220","name":"Synthetic Steam Installed", ...}
{"kind":"game","id":"10000000-0000-0000-0000-000000000003","gameId":"400","name":"Synthetic Steam Unicode — 雪", ...}
{"kind":"game","id":"10000000-0000-0000-0000-000000000002","gameId":"570","name":"Synthetic Steam Uninstalled", ...}
```

### 1d. Search on that library — PROVEN

Command: `dotnet ...Playnite.Cli.dll search Steam --library <dest.sqlite> --limit 5` — exit **0**:

```
{"kind":"page","totalCount":3,"offset":0,"limit":5,"returned":3}
{"kind":"game","id":"...0001","gameId":"220","name":"Synthetic Steam Installed", ...}
{"kind":"game","id":"...0003","gameId":"400","name":"Synthetic Steam Unicode — 雪", ...}
{"kind":"game","id":"...0002","gameId":"570","name":"Synthetic Steam Uninstalled", ...}
```

(An initial `search --library ... -- Steam` mis-ordered the term and correctly returned the usage
line with exit 2; the term-first form above is the documented invocation and is the one cited.)

### 1e. Launch --dry-run on that library — PARTIAL

Command: `dotnet ...Playnite.Cli.dll launch --dry-run --library <dest.sqlite> --id 10000000-0000-0000-0000-000000000001`
— exit **5**:

```
'Synthetic Steam Installed' is started by a library plugin action, not a stored play action.
```

The same exit 5 is returned for all three fixture games (`--id ...0001`, `...0002`, `...0003`) and
for `--name "Synthetic Steam Installed"`. Exit 5 is the documented `QueryExitCodes.NoPlayAction`
(`src/Playnite.Cli/Commands/QueryCommandSupport.cs:14`); the command resolves the game and refuses
to start a process, but it cannot print a `started:false` launch plan because the committed fixture
games carry `IncludeLibraryPluginAction` rather than a stored `GameAction`.

Positive launch-plan output is proven only by the merged in-process unit test
`LaunchDryRunCommandTests` (`tests/Playnite.Core.Tests/Cli/LaunchDryRunCommandTests.cs`), which
asserts `kind=launch`, `dryRun=true`, `started=false` for a game with a File action. That is not a
real-process run on a committed fixture, so it does not close the gap. **Missing evidence: a
committed fixture library containing at least one game with a stored (File/URL/Script) play action,
run through the CLI process to a `started:false` plan.** Per the brief, a fixture was not invented;
this item stays `PARTIAL`.

### 1f. No UI assembly loaded — PROVEN

Exact checks run:

```
dotnet list src/Playnite.Cli/Playnite.Cli.csproj reference --include-transitive
  -> Project reference(s)
     --------------------
     ../Playnite.Core/Playnite.Core.csproj
     ../Playnite.Platform/Playnite.Platform.csproj
     ../Playnite.Steam/Playnite.Steam.csproj
     ../Playnite.LegacyImport.Contracts/Playnite.LegacyImport.Contracts.csproj
  exit 0   (no Avalonia, no Playnite.Desktop, no Playnite.SDK)

grep -i avalonia src/Playnite.Cli/bin/Release/net11.0/Playnite.Cli.deps.json
  -> (no lines) ; grep exit 1  (no Avalonia assembly in the deps closure)

ls src/Playnite.Cli/bin/Release/net11.0/*.dll | grep -iE 'avalonia|desktop'
  -> (none) ; pipeline exit 0 with empty match
```

The CLI project graph contains no UI assembly, its resolved `deps.json` names no Avalonia assembly,
and its output directory ships no Avalonia/Desktop DLL. The four CLI processes above therefore ran
with no UI assembly in their load path.

---

## Criterion 2 — Abstractions build, ApiCompat report, samples, PublicAPI regeneration

### 2a. Abstractions builds with ApiCompat enabled — PROVEN

Command and result (exit **0**):

```
dotnet build src/Playnite.SDK.Abstractions/Playnite.SDK.Abstractions.csproj \
  -c Release --no-incremental -warnaserror -v normal
  ...
  ApiCompatValidateAssembliesCore:
    APICompat ran successfully without finding any breaking changes.
  ...
  Build succeeded. 0 Warning(s), 0 Error(s). exit 0
```

`Playnite.SDK.Abstractions.csproj` sets `ApiCompatValidateAssemblies=true`,
`ApiCompatContractAssembly=<repo>/src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.dll`,
and its `ValidateSdkPublicApiPolicy` target hard-errors if `TreatWarningsAsErrors` is not `true` or
if `RS0016`/`RS0017` appear in `NoWarn`/`WarningsNotAsErrors`. The build above ran with
`-warnaserror`, so RS0016/RS0017 were errors, and the shipped baseline was not rebaselined.

### 2b. v6→v7 ApiCompat report — PROVEN (cite + verify committed artifact)

The reviewed report is `docs/port/p6-008-apicompat-v6-v7.txt`, the frozen surface is
`docs/port/p6-008-frozen-api.txt`, and the classification is
`docs/port/p6-008-apicompat-classification.tsv`, all merged by p6-008 (`98f7391`, PR #208). Verified
directly from the committed files:

```
for code in CP0001 CP0002 CP0006 CP0009; do grep -c "^$code:" docs/port/p6-008-apicompat-v6-v7.txt; done
  CP0001: 208   CP0002: 82   CP0006: 15   CP0009: 1
grep -cE '^CP[0-9]+:' docs/port/p6-008-apicompat-v6-v7.txt
  306
awk -F'\t' 'NR>1{c[$4]++} END{for(k in c) print k, c[k]}' docs/port/p6-008-apicompat-classification.tsv | sort
  adapted 114
  deferred 178
  excluded 14
classification rows (excl header): 306
```

Result: **306 differences = 208 CP0001 / 82 CP0002 / 15 CP0006 / 1 CP0009**, classified
**114 adapted / 178 deferred / 14 excluded / 0 breaking** — exactly the figures in the task and in
`docs/port/p6-008-sdk-freeze.md` §4. `docs/port/p6-008-frozen-api.txt` begins with `#nullable enable`
and lists the frozen declarations; the frozen surface is 62 exported types / 603 declarations
(7 types / 61 declarations in `PublicAPI.Shipped.txt`, 55 types / 542 in `PublicAPI.Unshipped.txt`).
This is a citation and byte-level count of the committed, reviewed artifact; the v6 tool run that
produced it was performed by p6-008 and is recorded in that unit's fragment, not re-run here.

### 2c. Sample plugins compile and load — PROVEN

```
dotnet test tests/Playnite.Plugin.Tests -c Release --nologo
  Passed! total: 15, failed: 0, succeeded: 15, skipped: 0, duration 3s 581ms. exit 0
```

`Playnite.Plugin.Tests` contains `SamplePluginBuildTests` (Release compile of the three samples)
and `SamplePluginLoadTests` (fake-host load with manifest and entry point resolved). 15/0/0, exit 0.

### 2d. PublicAPI regeneration produces no diff under RS0016/RS0017-as-errors — PROVEN

The Abstractions project plus its parent build props were copied to worker scratch
(`$SCRATCH/apiregen`) so the protected declaration files in the
worktree were never touched. Regeneration was run there with the committed `PublicAPI.*.txt` files
copied aside as the comparison baseline:

```
dotnet format analyzers <scratch Abstractions.csproj> --diagnostics RS0016 RS0017 --verbosity diagnostic
  Running 2 analyzers on Playnite.SDK.Abstractions.
  Fixing diagnostics... Complete in 1ms.
  Formatted 0 of 24 files. exit 0
diff <committed> <regenerated> PublicAPI.Shipped.txt    -> NO DIFF
diff <committed> <regenerated> PublicAPI.Unshipped.txt  -> NO DIFF
dotnet format analyzers <scratch Abstractions.csproj> --verify-no-changes --diagnostics RS0016 RS0017
  -> (no output) exit 0
```

Zero fixes from the RS0016/RS0017 analyzers and byte-identical declaration files: regeneration
produces no diff while both diagnostics are treated as errors.

---

## Criterion 3 — No Core project references Avalonia

### 3a. Architecture suite at phase level — PROVEN

```
dotnet test tests/Playnite.Architecture.Tests -c Release --nologo
  Passed! total: 210, failed: 0, succeeded: 210, skipped: 0, duration 11s 930ms. exit 0
```

### 3b. Direct graph/no-Avalonia tests (not merely "CI green") — PROVEN

```
dotnet <Playnite.Architecture.Tests.dll> \
  -method '*Core_and_abstractions_have_no_direct_or_transitive_Avalonia_dependency' \
  -method '*Core_and_abstractions_have_no_UI_dependency' \
  -method '*Core_UI_SDK_reference_is_rejected_even_when_transitive' -noLogo
  Total: 4, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0. exit 0
```

These assert, on the evaluated project graph (`ProductGraph`/`SdkBoundaryPolicy`), that
`Playnite.Core` and `Playnite.SDK.Abstractions` have no direct or transitive dependency whose family
is `Avalonia`, that Core's only `Playnite.*` dependency is `Playnite.SDK.Abstractions`, and that a
Core→UI-SDK edge is rejected even when transitive.

### 3c. Direct project-graph dump — PROVEN

```
dotnet list src/Playnite.Core/Playnite.Core.csproj reference --include-transitive
  -> ../Playnite.SDK.Abstractions/Playnite.SDK.Abstractions.csproj   (only one) ; exit 0
dotnet list src/Playnite.Core/Playnite.Core.csproj package --include-transitive | grep -i avalonia
  -> (no lines) ; grep exit 1  (no Avalonia package anywhere in Core's resolved closure)
```

The phase-level result is evaluated graph data, not a "CI green" assertion: Core's project closure
is exactly SDK.Abstractions and its package closure contains no Avalonia.

---

## Criterion 4 — Fake-host contract test for every SDK interface

### 4a. Interface inventory

Interfaces added by p6-006 (`d1d57be`, PR #202, `docs/port/p6-006-notifications-dialogs-mainview.md`):
`INotificationsAPI`, `INotificationHost`, `IDialogsFactory`, `IDialogHost`, `IMainViewAPI`,
`IMainViewHost`, plus the reshaped `IGameView`.

Interfaces added by p6-007 (`b80847b`, PR #201, `docs/port/p6-007-uri-html-description.md`):
`IUriHandlerAPI`, `IUriCommandHost`, `IHtmlDescriptionProvider`, `IHtmlSanitizer`.

Seed interfaces already frozen: `IDataSerializer`, `IIdentifiable` (and `IGameView` counted above).
The `p6-008` coverage list `docs/port/p6-008-interface-coverage.tsv` froze 13 rows:

```
Playnite.SDK.Data.IDataSerializer                    ContractDataSerializer        IDataSerializer_every_member_is_exercised
Playnite.SDK.Models.IIdentifiable                    ContractIdentifiable          IIdentifiable_id_is_exercised
Playnite.SDK.Models.IGameView                        ContractGameView              IGameView_id_and_name_are_exercised
Playnite.SDK.INotificationHost                       ContractNotificationHost      INotificationHost_is_exercised_by_NotificationsApi
Playnite.SDK.INotificationsAPI                       ContractNotificationHost      INotificationsAPI_every_member_is_exercised
Playnite.SDK.IDialogHost                             ContractDialogHost            IDialogHost_is_exercised_by_DialogsFactory
Playnite.SDK.IDialogsFactory                         ContractDialogHost            IDialogsFactory_every_member_is_exercised
Playnite.SDK.IMainViewHost                           ContractMainViewHost          IMainViewHost_is_exercised_by_MainViewApi
Playnite.SDK.IMainViewAPI                            ContractMainViewHost          IMainViewAPI_every_member_is_exercised
Playnite.SDK.IUriCommandHost                         ContractUriCommandHost        IUriCommandHost_is_exercised_by_UriHandlerApi
Playnite.SDK.IUriHandlerAPI                          ContractUriCommandHost        IUriHandlerAPI_every_member_is_exercised
Playnite.SDK.Descriptions.IHtmlDescriptionProvider  ContractHtmlDescriptionProvider IHtmlDescriptionProvider_is_exercised_by_HtmlDescriptionService
Playnite.SDK.Descriptions.IHtmlSanitizer             ContractHtmlSanitizer         IHtmlSanitizer_is_exercised_by_HtmlDescriptionService
```

All 13 rows point at `tests/Playnite.Core.Tests/SdkContracts/SdkInterfaceContractTests.cs`.

### 4b. Reflection test proves the list is complete — PROVEN

```
dotnet <Playnite.Architecture.Tests.dll> -method '*Every_SDK_interface_has_a_fake_host_contract_test' -noLogo
  Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0. exit 0
```

The test enumerates `Abstractions.GetExportedTypes().Where(t => t.IsInterface)`, asserts the set
equals exactly the 13 coverage rows, asserts each row names a fake class, a test method and an
existing test file, and asserts that file contains the fake class, the test method and the
interface name. It passes, so no exported interface lacks a fake-host contract test.

### 4c. The fake-host tests execute — PROVEN

```
dotnet <Playnite.Core.Tests.dll> \
  -class '*SdkInterfaceContractTests' -class '*SdkHostContractTests' -class '*UriHtmlContractTests' -noLogo
  Total: 26, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0. exit 0
```

The fakes implement the interfaces at compile time (a removed member or changed signature fails the
build) and each test calls every member through the real Core service. No interface is named as
missing: **0 PARTIAL** within this criterion.

---

## Validation of this assessment's own change set

The change set is docs-only and confined to `p6-shards` evidence: this gate doc, the `p6-shards`
queue shard notes (status stays `done`), one new `p6-shards.ledger.d/219-p6-gate-evidence.tsv`
fragment, and the hand-written narrative window of `docs/port/PORT-STATUS.md`.

- PR: https://github.com/seanburkes/playnite-linux/pull/219 (base `main`, head `fm/p6-shards5`).
- Validated pushed head: `cff31a169e15d4980dbb0c88cd4753663a7afaf0`.
- linux-ci run: https://github.com/seanburkes/playnite-linux/actions/runs/44851252321
  (`ci_run=44851252321`), workflow `linux-ci`, event `pull_request`, on that exact head;
  `gh-axi run view 44851252321` reported `status: completed`, `conclusion: success`,
  job `linux-build-and-test` (ID 136682871551) `completed/success`.
- Local guard checks on the validated head: `python3 scripts/port/build_port_index.py check
  --shards-only --against origin/main --forbid-aggregate-changes` → exit 0;
  `python3 scripts/port/check_unit_ownership.py --base origin/main --head HEAD --unit p6-shards
  --branch fm/p6-shards5` → exit 0 with `offending paths: (none)`.
- The ledger fragment is a docs-only delta added after that run, following the established
  `p6-shards` fragment pattern; the head-commit `Port-Unit: p6-shards` trailer is present.

No acceptance is written here. `P6-accepted.md` is deliberately not created; the captain decides
after review.

# Architecture and banned-API gates (p2-002, p2-006)

The policy comes from `02-ARCHITECTURE.md` §2 and `PORTING.md`'s banned table.
The frozen reference is `legacy/source/Playnite/Playnite.csproj`: its WPF/Win32,
WMI, Windows-service and LiteDB dependencies must not carry into the new hosts.
No application behavior is ported by this unit; its queue oracle is these policy documents.

## Running the gates

```sh
dotnet build Playnite.slnx -c Release
dotnet test --project tests/Playnite.Architecture.Tests -c Release
```

The existing xUnit v3 MTP v2 packages require the Microsoft.Testing.Platform
runner selected in `global.json`. On .NET 11 its project argument is `--project`,
so this is the supported equivalent of the task's positional `dotnet test` command.
The original scaffold's positional command stopped before executing tests with
“Testing with VSTest target is no longer supported by Microsoft.Testing.Platform”.
See [Microsoft's runner migration instructions](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test#mtp-mode-of-dotnet-test).
No workflows are added here; p2-003 owns wiring these commands into CI.

## Scope and dependency checks

`Directory.Build.props` attaches BannedApiAnalyzers (private build dependency)
and the symbol files only to projects under `src/`. Tests, tooling, spikes and
the frozen Windows oracle are outside this scope. In particular, xUnit brings
`Microsoft.Win32.Registry` and `System.Security.AccessControl` transitively into
the architecture runner; scanning the runner's dependency graph as if it were a
product would reject the test harness itself. A test asserts that those packages
are present in the runner, that its analyzer/symbol inputs are absent, and that
all product projects are scanned instead. This does not exempt any product API.

NetArchTest.Rules 1.3.2 (the dependency document's preferred library) checks
compiled product types. Complementary graph tests inspect evaluated build inputs
and NuGet assets, because an unused reference can disappear from emitted IL.
`Directory.Build.targets` records direct project edges before the SDK adds
transitive edges, resolved compiler references, output assemblies, source files,
defines, analyzers and additional files under each project's ignored `obj/` tree.
The tests use the active build configuration and fail on missing inputs.

The graph gate checks every direct product-project edge against the architecture
table, including the importer/Contracts separation. It also checks the listed
package restrictions for Core, SDK, Abstractions, Contracts and LegacyImport
(including the importer's LiteDB 4.1.4 version), and restricts direct Avalonia
packages to the UI/platform projects. The textual architecture clarification
allows Avalonia in Platform as well as Platform.Linux.

The allowed-reference table is not duplicated in the test code. `ArchitectureTable`
parses section 2 of `docs/port/02-ARCHITECTURE.md` at test time, expands the
document's project shorthands, and drives the allowed and "must not reference"
project and package columns. A test fails when the document and `src/` disagree
about the project set, when a table row names an unknown project, when a row has
no reference allowance, or when a project token no longer resolves. Table tokens
are checked against a closed vocabulary of projects, packages and API names, so a
misspelled project, package or must-not token throws instead of turning a
prohibition into a silent allowance. The must-not columns are asserted against
direct project edges, direct packages and resolved package closures; the Core ->
Platform (project), Core -> Avalonia (package) and Contracts -> Core/LiteDB
examples are covered by dedicated tests. Each must-not API token also drives a
source-gate probe (`ForbiddenApis`), so the API half of the column cannot become
documentation-only. Package allowances come from the same row: Avalonia's table
qualifiers are enforced (SDK only the contracts package, Platform/Platform.Linux
base packages, Desktop the unqualified family it owns), and the importer's LiteDB
row is version-pinned to 4.1.4.

Scope guards fail closed rather than passing on an empty target set. The product
enumeration throws on zero or duplicate projects; every product must have source
files, every executable must have a resolved entry point, and every product
assembly must contain at least one type, so the NetArchTest rules cannot succeed
vacuously over an empty assembly. The scaffold's namespace-only `AssemblyMarker`
files emitted no type, so each now declares the marker type its filename names;
the non-empty assertion would otherwise fail for eight products. `SourcePolicy`
refuses an unclassified project
name, so renaming or adding a product cannot silently narrow the host, logic or
UI scopes. The banned-symbol attachment matrix is asserted per project (common,
library, Core, logic, interop, blocking and GC files), so a scope that silently
drops a file fails a test. A separate expected-product floor asserts the full
ten-project set against the document, `src/` and every policy scope, so deleting
a product from two places at once cannot reduce coverage.

Core and Abstractions exclude Avalonia from the entire resolved closure.
All products exclude Windows-specific packages/explicit assembly dependencies;
Desktop therefore cannot reference Microsoft.Win32. Desktop/Cli exclude LiteDB
and the importer executable transitively, and LegacyImport/Contracts exclude
Core/Desktop/both SDK assemblies transitively. Framework reference-pack
catalogs are not treated as application dependencies: net11 exposes Win32 and
Drawing reference assemblies even to an empty project. Actual uses of those
framework APIs are checked in source and emitted IL.

## SDK Abstractions boundary and API guards (p2-006)

The scaffold's `Playnite.SDK.Abstractions` is now a versioned `net11.0` contracts
assembly, matching Core's framework. Its public namespaces remain `Playnite.SDK`,
`.Models`, `.Data`, `.Events` and `.Plugins` to preserve the oracle's names; the
assembly split does not force a namespace rename. Core already directly referenced
Abstractions in the scaffold, and the UI SDK already referenced it too. Those edges
are retained, not replaced with artificial product logic to manufacture a consumer.
The target diagram in `02-ARCHITECTURE.md` groups the SDK conceptually; the actual
Core edge is to **Abstractions, never the Avalonia UI SDK** (ADR-018).

The initial public surface is deliberately seven complete, small contracts, consumed
by the architecture/contract tests; it is not an application feature or full v6 port:

| Contract | Frozen oracle under `legacy/source/PlayniteSDK/` | Adaptation |
| --- | --- | --- |
| `Models.IIdentifiable` | `Models/IIdentifiable.cs` | Guid identity unchanged |
| `Models.InstallationStatus` | `Models/InstallationStatus.cs` | Numeric values and localization attributes unchanged |
| `Data.IDataSerializer` | `Data/DataSerialization.cs` | All 27 interface methods; explicit nullable results/outputs and nullable serialization inputs |
| `Plugins.LibraryGetGamesArgs` | `Plugins/LibraryPlugin.cs` | Retains parameterless constructor and getter/internal setter; adds token constructor so a host in another assembly can supply live cancellation |
| `Plugins.MetadataField` | `Plugins/MetadataPlugin.cs` | All 19 names and numeric values unchanged |
| `Events.OnApplicationStartedEventArgs` | `Events/ApplicationEvents.cs` | Complete payload-free event contract, not a fake event implementation |
| `SdkVersions` | `SdkVersions.cs` | Reads its own assembly version, now 7.0.0.0 |

`IDataSerializer` contains no serializer implementation, globals, or fake-success
methods. Its nullable outputs preserve the v10 implementation's ability to deserialize
JSON `null` successfully (`legacy/source/Playnite/Common/Serialization.cs`); a true
Try result does not promise a non-null object. The full Game/DatabaseObject model,
collections and database API, game events, plugin base classes, library/metadata
providers, serializer implementations and static Serialization facade, host facade,
UI contracts, plugin loading and the full v6→v7 breaking-change report are **not yet
ported**. Existing surface-parity rows remain planned; this unit does not redefine
those protected oracle rows as implemented.

### Enforcement and proving tests

`SdkBoundaryPolicy.GraphViolations` consumes the same evaluated direct edges and
resolved dependency closure as the existing gates. The source rule is part of
`SourcePolicy`, so `Product_compiler_inputs_obey_the_banned_construct_table` applies
it to actual compiler inputs, not a separate test-only scan.

| Rule | Proving test in `SdkBoundaryTests` |
| --- | --- |
| Core must directly reference Abstractions | `Missing_Core_abstractions_reference_is_rejected` |
| Core must not reference UI SDK, directly or transitively | `Core_UI_SDK_reference_is_rejected_even_when_transitive` |
| Core/Abstractions exclude Avalonia; Abstractions also excludes all other product assemblies and Windows platform dependency families, directly or transitively | `Abstractions_forbidden_dependency_is_rejected_directly_and_transitively`; existing Core/Avalonia policy proofs remain |
| Abstractions cannot use Avalonia, Platform or SDK.Controls namespace types, including aliases/nested generic arrays | `Abstractions_rejects_UI_or_platform_types_even_behind_an_alias` |
| No type from the UI SDK assembly can be used, regardless of its namespace | `Any_type_from_the_UI_SDK_assembly_is_rejected_regardless_of_namespace` |
| Valid BCL contracts remain allowed, including the documented ICommand exception | `BCL_contracts_remain_allowed` |

`Evaluated_Core_and_abstractions_obey_the_SDK_boundary` runs the graph rule on the
real projects. The existing Windows/source/interop bans still apply. Build-only
PublicApiAnalyzers and ApiCompat.Task are private package references on Abstractions;
they are explicitly classified as tooling, not new runtime allowances in the table.

PublicApiAnalyzers **5.6.0** automatically consumes `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt` from the Abstractions directory. Warnings are build errors.
`PublicApiTests` verifies the evaluated attachment and warning policy, accepts the
committed surface, and proves that an untracked addition yields **RS0016** and a
removed shipped member yields **RS0017**, using the actual analyzer assembly and
committed API files.

Microsoft.DotNet.ApiCompat.Task **11.0.100** runs after `CoreCompile` in ordinary
builds, with `ApiCompatValidateAssemblies=true` and the committed reference baseline
`src/Playnite.SDK.Abstractions/api-baseline/Playnite.SDK.Abstractions.dll` on the left.
It is not pack-only, is not regenerated by a build, and has no suppressions.
`ApiCompatTests.ApiCompat_accepts_the_baseline_and_rejects_a_changed_identity_type`
invokes Microsoft's MSBuild task with resolved framework references: the real output
passes, while an otherwise identical compilation with `IIdentifiable.Id` changed from
Guid to string fails with **CP0002**. No public-API analyzer is involved in that
negative test, so updating the declaration files cannot mask the binary break.
The baseline README records its provenance and checksum.

Local Release validation on 2026-11-15: narrow architecture-project build succeeded
with **0 warnings / 0 errors**; the full architecture suite passed **103 cases, 0
failed, 0 skipped** (the original 76 plus 27 new cases). Two actual narrow negative
builds were also run and reverted: an untracked public class failed with RS0016;
changing Id to string **and updating the text API declaration to match** failed with
CP0002/CP0006. Proof logs are under `/home/sean/src/firstmate/data/p2-006/`.
Independent blind reviews and CI acceptance remain the merge authority's gates.

## Banned construct coverage

BannedApiAnalyzers consumes documentation-comment IDs: it can ban namespaces, types and
members, but it cannot describe syntax, semantic purity or a comment-based exception. See its
[official symbol-list format](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md).
`BannedConstructInventory.cs` therefore keeps one row for every row of the PORTING.md banned
table, and `PolicyCoverageTests` fails when the table gains, loses or renames a row without an
owner. The comparison is a multiset: a duplicated PORTING construct or a duplicated inventory
row fails instead of collapsing to one set member. A symbol row must exist in its named
`BannedSymbols*.txt` file and resolve against the products' reference assemblies (the two
package-only symbols that the net11 reference set cannot resolve are recorded explicitly); a
syntax row must produce its violation from a failing probe. Removing a rule, weakening a
probe, or adding an unowned or duplicated PORTING row fails.

| PORTING row | Narrowed condition | Owner | Residual approximation / risk |
| --- | --- | --- | --- |
| `System.Windows.*` | `System.Windows.Input.ICommand` only | `SourcePolicy` semantic resolver | BannedApi cannot exclude one type from a namespace ban, so the source resolver owns it. Exact for source; dynamic, reflection and generated code are outside source analysis. |
| `System.Drawing.*` | none | `BannedSymbols.txt`, source, IL and package graph | Exact. |
| `Microsoft.Win32.*` | none | `BannedSymbols.txt`, source, IL and package graph | Exact. |
| `DllImport`/`LibraryImport` | Platform.Linux with a reviewed allow-list | `BannedSymbols.Interop.txt` outside Linux, source everywhere | No import has been reviewed, so Platform.Linux is rejected too; its porting unit must add an allow-list entry. |
| `System.Management` | none | `BannedSymbols.txt`, source and graph | The namespace is absent from the net11 reference set; the symbol row is ready if a package adds it. |
| `BinaryFormatter` | none | `BannedSymbols.txt` and source | Exact. |
| `Thread.Abort` | none | `BannedSymbols.txt` and source | Exact. |
| `AppDomain.CreateDomain` | `AssemblyLoadContext` if needed | `BannedSymbols.txt` and source | Exact; no replacement wrapper is approved yet. |
| `Process.Start(string)` with a shell string | `ProcessStartInfo` with `ArgumentList` | `BannedSymbols.txt` (shell overloads, constructor, `Arguments`), source | Exact for the listed shapes; `UseShellExecute` is additionally restricted to constant false. Dynamic values cannot be proven safe. |
| `Environment.Exit` in library code | Desktop, Cli and LegacyImport are hosts | `BannedSymbols.Library.txt` and source | Exact. |
| `GC.Collect` | A policy-allowlisted benchmark/soak harness with a comment on the call | `BannedSymbols.GcCollect.txt` (scoped by the protected `docs/port/BenchmarkHarnessAllowlist.props`), source | No harness exists yet (p8-003). The allowlist is empty; the protected props file is imported by the root build and read by `SourcePolicy.BenchmarkHarnessProjects`, so there is one source of truth and a product cannot self-declare. Until a harness is reviewed in, every product call is rejected. |
| `catch { }` | Catch specific, log, rethrow or return a result | `SourcePolicy` syntax check | Only an empty body is rejected. A typed catch that silently swallows still passes and remains a review obligation. |
| `async void` | UI event handlers only | `SourcePolicy` semantic check | Registration cannot be proven from source. The approximation is a Desktop/SDK `(object sender, EventArgs-derived)` method or lambda; async void local functions are rejected everywhere. Logging handler failures remains a review obligation. |
| `.Result`/`.Wait()` | Commented top-level Cli entry point | `BannedSymbols.Blocking.txt` (except Cli) and source | The Cli entry-point method is treated as the whole top-level boundary: a commented call anywhere inside it is accepted; helper methods and nested functions are not. |
| `DateTime.Now` in Core | Other projects are outside the rule | `BannedSymbols.Core.txt` and source | Exact. |
| `Guid.NewGuid()` in pure logic | Inject an ID or pass one in | `BannedSymbols.Logic.txt` and source | Purity is approximated by the Core, SDK.Abstractions and Steam project scope, so impure members there are also blocked. |
| `Random` without a seed in logic | Inject a seeded `Random` | `BannedSymbols.Logic.txt` and source | Same project-scope approximation as `Guid.NewGuid`. |

The semantic gate rebuilds a Roslyn compilation from each product's evaluated
compiler inputs and fails closed on compiler errors before inspecting symbols.
This avoids regular-expression false positives from comments/string literals,
and catches aliases, method groups and target-typed construction. Policy tests
exercise violations and permitted exceptions without adding product logic.

The `System.Windows` resolver, empty-catch syntax rule and `async void` signature
rule are the only rows BannedApi cannot own at all. Every other row has a symbol
file, including `.Result`/`.Wait()` (`BannedSymbols.Blocking.txt`) and
`GC.Collect` (`BannedSymbols.GcCollect.txt`); those two files are deliberately
scoped to the documented exception and the source policy checks the contextual
condition. The common file records the exceptions in comments. No source
suppression or new native-import exemption is introduced by this unit.

## Local negative proofs

Earlier negative proofs (2026-11-15 UTC) added a temporary, unused ProjectReference
from Core to SDK and a temporary Core source file. The full suite failed the
allowed-reference check, and a narrow Core build exited 1 with zero warnings and
these expected errors:

```text
error RS0030: The symbol 'GC.Collect(int, GCCollectionMode)' is banned in this project: Do not hide resource leaks.
error RS0030: The symbol 'DateTime.Now' is banned in this project: Inject IClock or TimeProvider.
```

The 2026-11-15 hardening re-ran both kinds of proof:

- A temporary Core source file referenced `Task<TResult>.Result`, `GC.Collect()`
  and `System.Drawing.Point`. A narrow Core build exited 1 with zero warnings and
  three RS0030 errors, proving the new `BannedSymbols.Blocking.txt` row, the moved
  `BannedSymbols.GcCollect.txt` row, and the namespace ban all fire. Log:
  `/home/sean/src/firstmate/data/p2-002-harden/negative-api-2026-11-14.log`.
- With `<PlayniteBenchmarkHarness>true</PlayniteBenchmarkHarness>` set temporarily
  on Core and the same `GC.Collect()` probe, the narrow build exited 0 with zero
  warnings. That self-declarable property was removed in the review round; the
  exception is now a protected project allowlist
  (`docs/port/BenchmarkHarnessAllowlist.props`, read by `SourcePolicy` and imported
  by the root build), with per-project tests proving a second product cannot
  borrow an allowlisted name. The old log is retained as the negative proof of the
  removed mechanism:
  `/home/sean/src/firstmate/data/p2-002-harden/harness-exemption-2026-11-14.log`.

Both probes were reverted; the worktree contains no probe file. The review round
also made `docs/port/02-ARCHITECTURE.md`, `docs/port/PORTING.md`,
`docs/port/BenchmarkHarnessAllowlist.props` and the root `BannedSymbols*.txt`
files protected paths in `plan-guards.yml` and `CODEOWNERS`, so a later change
to the enforced table, exception allowlist or symbol vocabulary needs the
`oracle-change` label and a second reviewer. This PR itself changes those files,
so it needs that label to pass the oracle gate.
The architecture suite count is recorded in the p2-002 ledger shard alongside
the clean build/test results. Two independent blind diff reviews remain required
before this unit is accepted.

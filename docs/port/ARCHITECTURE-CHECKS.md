# Architecture and banned-API gates (p2-002)

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

Core and Abstractions exclude Avalonia from the entire resolved closure.
All products exclude Windows-specific packages/explicit assembly dependencies;
Desktop therefore cannot reference Microsoft.Win32. Desktop/Cli exclude LiteDB
and the importer executable transitively, and LegacyImport/Contracts exclude
Core/Desktop/both SDK assemblies transitively. Framework reference-pack
catalogs are not treated as application dependencies: net11 exposes Win32 and
Drawing reference assemblies even to an empty project. Actual uses of those
framework APIs are checked in source and emitted IL.

## Banned construct coverage

BannedApiAnalyzers consumes documentation-comment IDs; it cannot describe syntax,
semantic purity or exceptions within a namespace. See its
[official symbol-list format](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md).
Each context-dependent row is named in `BannedSymbols.txt` and enforced below.

| Rule | Automatic enforcement |
| --- | --- |
| System.Windows except System.Windows.Input.ICommand | Semantic type/member resolution, including aliases and descendants; ICommand has an explicit positive test |
| System.Drawing, Microsoft.Win32, System.Management | Namespace symbol bans plus semantic descendant checks, compiled dependencies and package graph |
| ServiceController, BinaryFormatter | Type bans and semantic checks |
| Thread.Abort, AppDomain.CreateDomain, GC.Collect | Every framework overload in the common symbol list and semantic checks; a catalog test detects new uncovered overloads |
| DllImport/LibraryImport | Attribute symbol bans outside Platform.Linux, plus semantic checks; Linux's reviewed native-import allow-list is currently empty, so the source gate rejects imports there too |
| Shell-string Process.Start | Single-filename and argument-string Start overloads, the two-string ProcessStartInfo constructor and Arguments property are banned; use a ProcessStartInfo with ArgumentList or an IEnumerable<string> argument-list overload. Semantic checks reject UseShellExecute assignments unless the value is constant false |
| Environment.Exit in library code | Library symbol list and semantic check; only Desktop, Cli and LegacyImport are hosts |
| Empty catch | Syntax check for every empty catch body, including typed and filtered catches |
| async void | Semantic checks for methods, local functions and lambdas; UI signature exception described below |
| Task/ValueTask Result and Task Wait/WaitAll/WaitAny | Semantic checks; only the actual Cli entry point with a comment on the statement is exempt, never helper methods or nested functions |
| DateTime.Now in Core | Core symbol list and semantic checks |
| Guid.NewGuid and unseeded Random in logic | Logic symbol list and semantic checks, including Random.Shared; seeded constructors remain permitted |

The semantic gate rebuilds a Roslyn compilation from each product's evaluated
compiler inputs and fails closed on compiler errors before inspecting symbols.
This avoids regular-expression false positives from comments/string literals,
and catches aliases, method groups and target-typed construction. Policy tests
exercise violations and permitted exceptions without adding product logic.

Purity and actual event registration cannot be inferred from a symbol list.
For this scaffold, the logic scope is all of Core, SDK.Abstractions and Steam;
IDs/randomness are injected throughout these projects, rather than trying to
guess which methods are pure. Other projects retain their host/platform duties.
The UI exception is limited to Desktop/SDK async void methods or lambdas with
`(object sender, EventArgs-derived args)` signatures; async void local functions
are rejected everywhere. Whether such a method is really registered as an event
handler, and whether handler failures are logged, remains a review obligation.
Likewise these gates cannot infer malicious shell execution hidden behind dynamic
values, reflection or another process; they enforce the documented API shapes.
No source suppression or new native-import exemption is introduced by this unit.

## Local negative proofs

On 2026-11-15 UTC, added a temporary, unused ProjectReference from Core to SDK
and ran the complete architecture suite. It exited 2 with 38 passed, 1 failed,
0 skipped (39 cases at that point). The observed failure was:

```text
Playnite.Core -> Playnite.SDK violates docs/port/02-ARCHITECTURE.md allowed-reference table
```

The reference was restored in a `finally` block. No violating reference or probe
file is included in the change. A separate temporary Core source file caused a
narrow Core build to exit 1, with zero warnings and these two expected errors:

```text
error RS0030: The symbol 'GC.Collect(int, GCCollectionMode)' is banned in this project: Do not hide resource leaks.
error RS0030: The symbol 'DateTime.Now' is banned in this project: Inject IClock or TimeProvider.
```

Full logs are task evidence outside the repository under
`/home/sean/src/firstmate/data/p2-002/negative-reference.log` and `negative-api.log`.
The final clean build/test results are recorded in the p2-002 ledger shard.
Two independent blind diff reviews remain required before this unit is accepted.

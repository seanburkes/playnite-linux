# p6-008 — SDK Abstractions freeze and v6→v7 ApiCompat report

**Status:** delivered for review. **Unit:** `p6-008`. **Freeze target:**
`src/Playnite.SDK.Abstractions` (assembly `Playnite.SDK.Abstractions`, version `7.0.0.0`,
`net11.0`).

This document is the reviewed freeze record. The machine-readable surface is
[`p6-008-frozen-api.txt`](p6-008-frozen-api.txt); the complete, normalized ApiCompat output
is [`p6-008-apicompat-v6-v7.txt`](p6-008-apicompat-v6-v7.txt); every difference and its
classification is in
[`p6-008-apicompat-classification.tsv`](p6-008-apicompat-classification.tsv); the interface
coverage map is [`p6-008-interface-coverage.tsv`](p6-008-interface-coverage.tsv).

## 1. Frozen surface

The freeze is the committed PublicAPI text surface, copied verbatim into
[`p6-008-frozen-api.txt`](p6-008-frozen-api.txt). `PublicAPI.Shipped.txt` remains pinned by
`api-baseline/PublicAPI.Shipped.sha256`; the binary baseline remains pinned by
`api-baseline/Playnite.SDK.Abstractions.sha256`. Neither baseline, neither declaration file,
nor any source file in the assembly is changed by this unit.

The frozen assembly exports **nine types** (seven seed contracts plus two serialization
attributes added by p3-003c):

| Type | Kind | Frozen members |
| --- | --- | --- |
| `Playnite.SDK.Data.IDataSerializer` | interface | 27 methods (JSON, YAML, TOML, streams/files, clone, equality) |
| `Playnite.SDK.Data.DontSerializeAttribute` | class | parameterless constructor |
| `Playnite.SDK.Data.SerializationPropertyNameAttribute` | class | constructor + `PropertyName` get/set |
| `Playnite.SDK.Events.OnApplicationStartedEventArgs` | class | parameterless constructor |
| `Playnite.SDK.Models.IIdentifiable` | interface | `Guid Id { get; }` |
| `Playnite.SDK.Models.InstallationStatus` | enum | `Installed = 0`, `Uninstalled = 1` |
| `Playnite.SDK.Plugins.LibraryGetGamesArgs` | class | parameterless and `CancellationToken` constructors; `CancelToken` get |
| `Playnite.SDK.Plugins.MetadataField` | enum | 19 values, `Name = 0` through `InstallSize = 18` |
| `Playnite.SDK.SdkVersions` | static class | `static Version SDKVersion { get; }` |

### Intentional changes

**None.** This unit does not add, remove, rename or re-sign any public member of
`Playnite.SDK.Abstractions`. The existing PublicAPI files are the freeze, and the existing
ApiCompat baseline continues to validate every ordinary build.

The p6-006 (notifications, dialogs, main view) and p6-007 (URI handler, HTML description)
host surfaces are **not moved into this assembly by p6-008**. Moving them would require
editing the p6-006/p6-007-owned Core files and the protected PublicAPI/`api-baseline`
files, and `IMainViewAPI` still carries a Core `Game` dependency that Abstractions may not
take. ADR-018 and ADR-023 already scope UI extension points as adapted (not
source-compatible) and defer their freeze until after the Phase 7 shell pilot. Those
surfaces are therefore recorded here as **adapted** v6 differences, with fake-host contract
coverage in the Core test suite, rather than silently rebaselined or claimed as frozen.
If the reviewer requires the move in this unit, that is a separate protected-file decision
(the `oracle-change` route) and a Core `Game` reshape; this PR does not make it silently.

## 2. v6 baseline

The v6 baseline is the published `PlayniteSDK` **6.18.0** package, the same version declared
by the frozen oracle project `legacy/source/PlayniteSDK/Playnite.SDK.csproj`
(`<Version>6.18.0.0</Version>`).

| Artifact | Value |
| --- | --- |
| NuGet package | `PlayniteSDK` 6.18.0, MIT |
| Package URL | `https://api.nuget.org/v3-flatcontainer/playnitesdk/6.18.0/playnitesdk.6.18.0.nupkg` |
| Package SHA-256 | `63ae943bf226a782738d3d6b9977dcb77861e5d2ea1aadcf620332618c93e236` |
| Assembly | `lib/net462/Playnite.SDK.dll` |
| Assembly SHA-256 | `ce7511941aba4d4b17dd7b226fadfd0ecefdf51ab73775b25631089fd7542fc7` |
| Nuspec repository commit | `f64386f94f29dd8f4faeb9d7f0e61cf6df57d302` |

The package is used as the ApiCompat **left** side only. It is not vendored into the
repository and it is not a new oracle path.

## 3. ApiCompat method and result

Tool: **Microsoft.DotNet.ApiCompat 11.0.100** (the same version as the private
`Microsoft.DotNet.ApiCompat.Task` build dependency).

ApiCompat matches assemblies by simple name. The shipped v7 assembly is named
`Playnite.SDK.Abstractions`, while the v6 assembly is named `Playnite.SDK`. To make the
comparison valid, the **right** side is a scratch compilation of the exact same
`src/Playnite.SDK.Abstractions/**/*.cs` sources with `AssemblyName=Playnite.SDK`,
`Version=7.0.0.0`, `net11.0`; only the assembly name is changed. The scratch build contains
no other source and is not committed. The committed shipped assembly is validated separately
against its own `api-baseline` on every build.

```sh
apicompat \
  --left  <PlayniteSDK 6.18.0>/lib/net462/Playnite.SDK.dll \
  --right <scratch>/Playnite.SDK.dll \
  --roslyn-assemblies-path <sdk>/Roslyn/bincore
```

Result: **218 differences, all `CP0001` (“type exists on v6 but not on v7”) and zero
member-level differences** (`CP0002`–`CP0009`). No v6 member is removed or changed from a
v7 type that the freeze claims, because the freeze claims only the nine declared types. The
full normalized output is committed; the tool exits non-zero on any difference, as expected
for a subset freeze.

### Classification

| Classification | Count | Meaning |
| --- | ---: | --- |
| `adapted` | 26 | v6 host/UI surface deliberately re-shaped in Core by p6-006/p6-007; documented breaking adaptation, not a frozen v7 contract |
| `deferred` | 178 | v6 type outside the current nine-type freeze; no v7 compatibility claim; later porting work |
| `excluded` | 14 | global-namespace helpers and WPF/SQLite/markup implementation types outside the abstraction boundary |
| **Total** | **218** | |

Group counts: plugins 59, models 46, host-api-adapted 26, infrastructure 22, events 20,
excluded-boundary 14, webview 13, database 8, logging 4, settings 4, emulation 1, data 1.

**Breaking-change assessment.** p6-008 introduces no breaking change to the frozen
`Playnite.SDK.Abstractions` surface. The 26 `adapted` rows are pre-existing Core host
surfaces, not members of this assembly; their v6 incompatibility is documented in
ADR-018/ADR-023 and in the p6-006/p6-007 unit records, and they are not claimed as
source-compatible. Nothing in the report required a new breaking decision, so no
`needs-decision` stop was necessary. If the v6-compatible host shapes are wanted later, they
need a new protected-surface decision and, for `IMainViewAPI`, a `Game`-free reshape.

## 4. Contract tests for every SDK interface

Every public interface in the frozen assembly and every SDK-facing host contract in
`Playnite.Core.Hosting`, `Playnite.Core.UriHandling` and `Playnite.Core.Descriptions` has a
fake implementation and an executable test in
`tests/Playnite.Core.Tests/SdkContracts/SdkInterfaceContractTests.cs`. The fakes implement
the interfaces at compile time, so a removed member or changed signature fails the build;
each test then calls every member through the real Core service.

| Interface | Fake | Test |
| --- | --- | --- |
| `Playnite.SDK.Data.IDataSerializer` | `ContractDataSerializer` | `IDataSerializer_every_member_is_exercised` |
| `Playnite.SDK.Models.IIdentifiable` | `ContractIdentifiable` | `IIdentifiable_id_is_exercised` |
| `Playnite.Core.Hosting.INotificationHost` | `ContractNotificationHost` | `INotificationHost_is_exercised_by_NotificationsApi` |
| `Playnite.Core.Hosting.INotificationsAPI` | `ContractNotificationHost` | `INotificationsAPI_every_member_is_exercised` |
| `Playnite.Core.Hosting.IDialogHost` | `ContractDialogHost` | `IDialogHost_is_exercised_by_DialogsFactory` |
| `Playnite.Core.Hosting.IDialogsFactory` | `ContractDialogHost` | `IDialogsFactory_every_member_is_exercised` |
| `Playnite.Core.Hosting.IMainViewHost` | `ContractMainViewHost` | `IMainViewHost_is_exercised_by_MainViewApi` |
| `Playnite.Core.Hosting.IMainViewAPI` | `ContractMainViewHost` | `IMainViewAPI_every_member_is_exercised` |
| `Playnite.Core.UriHandling.IUriCommandHost` | `ContractUriCommandHost` | `IUriCommandHost_is_exercised_by_UriHandlerApi` |
| `Playnite.Core.UriHandling.IUriHandlerAPI` | `ContractUriCommandHost` | `IUriHandlerAPI_every_member_is_exercised` |
| `Playnite.Core.Descriptions.IHtmlDescriptionProvider` | `ContractHtmlDescriptionProvider` | `IHtmlDescriptionProvider_is_exercised_by_HtmlDescriptionService` |
| `Playnite.Core.Descriptions.IHtmlSanitizer` | `ContractHtmlSanitizer` | `IHtmlSanitizer_is_exercised_by_HtmlDescriptionService` |

Twelve interfaces, twelve tests, no skipped interface. The architecture test
`Every_SDK_interface_has_a_fake_host_contract_test` reflects over the product assemblies and
fails if an interface is added without a row here.

## 5. Dependency verification

`SdkFreezeTests.Core_and_abstractions_have_no_UI_dependency` consumes the evaluated project
edges, compiler references and resolved package closure recorded by the normal Release
build:

- `Playnite.Core` directly references `Playnite.SDK.Abstractions` and nothing else from
  `Playnite.*`; it has no direct or transitive `Avalonia` package/assembly.
- `Playnite.SDK.Abstractions` has no `Playnite.*` dependency and no `Avalonia`, WPF,
  WindowsDesktop, Platform or UI SDK dependency.
- The existing `SdkBoundaryTests` graph/source gates remain green.

## 6. Evidence

- Build: `dotnet build Playnite.slnx --configuration Release --no-restore -warnaserror`
  (CI, warnings are errors).
- Tests: the new contract class (12 cases) and the new freeze class (7 cases) pass locally;
  the full linux-ci run is cited in the ledger fragment and PR.
- No skip, no suppression, no baseline or protected-path edit, no aggregate edit, no merge.

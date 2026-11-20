# p6-008 — SDK Abstractions freeze and the v6→v7 ApiCompat report

**Status:** delivered for review. **Unit:** `p6-008`. **Freeze target:**
`src/Playnite.SDK.Abstractions` (assembly `Playnite.SDK.Abstractions`, version `7.0.0.0`,
`net11.0`).

This unit is the **freeze**, not a move. The host surfaces it freezes landed in separate
units that are already merged on `main`:

| PR | Merge commit | Unit | Surface |
| --- | --- | --- | --- |
| [#202](https://github.com/seanburkes/playnite-linux/pull/202) | `d1d57be` | p6-006 | notifications, dialogs and main-view contracts |
| [#201](https://github.com/seanburkes/playnite-linux/pull/201) | `b80847b` | p6-007 | URI handler and HTML-description contracts |

p6-008 therefore changes no product source, moves no type and reshapes no member. It
re-verified the committed `PublicAPI` text against the merged surface, regenerated the v7
binary baseline from those merged sources, and recorded the complete reviewed
`Microsoft.DotNet.ApiCompat` v6→v7 report over the surface as merged.

The machine-readable surface is [`p6-008-frozen-api.txt`](p6-008-frozen-api.txt); the complete,
normalized ApiCompat output is [`p6-008-apicompat-v6-v7.txt`](p6-008-apicompat-v6-v7.txt);
every difference and its classification is in
[`p6-008-apicompat-classification.tsv`](p6-008-apicompat-classification.tsv); the reviewed
suppression file is [`p6-008-apicompat-suppressions.xml`](p6-008-apicompat-suppressions.xml);
the interface coverage map is [`p6-008-interface-coverage.tsv`](p6-008-interface-coverage.tsv).

## 1. The surface under freeze

The 62 exported types are the seven shipped seed contracts plus 55 declarations added by the
merged p6-006/p6-007 moves. The moves kept the v10 names and namespaces, so the port is a move
toward the v10 names rather than a new spelling:

| Merged unit | Namespace | Types |
| --- | --- | --- |
| p6-006 (`d1d57be`) | `Playnite.SDK` | `INotificationsAPI`, `INotificationHost`, `NotificationMessage`, `NotificationType`, `NotificationHostStatus`, `NotificationHostResult`, `NotificationPublishStatus`, `NotificationPublishResult`, `NotificationDismissStatus`, `NotificationDismissResult`, `NotificationDismissFailure`, `NotificationDismissAllResult` |
| p6-006 (`d1d57be`) | `Playnite.SDK` | `IDialogsFactory`, `IDialogHost`, `MessageDialogButton`, `MessageDialogImage`, `MessageDialogResult`, `MessageDialogRequest`, `FileDialogKind`, `FileDialogRequest`, `TextInputDialogRequest`, `MessageDialogResponse`, `FileDialogResponse`, `TextInputDialogResponse` |
| p6-006 (`d1d57be`) | `Playnite.SDK` | `IMainViewAPI`, `IMainViewHost`, `MainViewState`, `MainViewResponse`, `MainViewGamesResponse`, `DesktopView`, `FullscreenView` |
| p6-006 (`d1d57be`) | `Playnite.SDK` | `HostOperationStatus` |
| p6-007 (`b80847b`) | `Playnite.SDK` | `IUriHandlerAPI`, `IUriCommandHost`, `UriCommandStatus`, `UriCommandResult`, `UriDispatchStatus`, `UriDispatchResult`, `PlayniteUriTarget`, `PlayniteViewTarget`, `PlayniteFilterField`, `PlayniteFilterTarget`, `PlayniteUriRoute` |
| p6-007 (`b80847b`) | `Playnite.SDK.Events` | `PlayniteUriEventArgs` |
| p6-007 (`b80847b`) | `Playnite.SDK.Descriptions` | `IHtmlDescriptionProvider`, `IHtmlSanitizer`, `HtmlDescriptionStatus`, `HtmlDescriptionSourceResult`, `HtmlDescriptionResult`, `HtmlSanitizationResult` |
| p6-006 (`d1d57be`) | `Playnite.SDK.Models` | `IGameView`, `SortOrder`, `LibraryGroupBy` |

Core keeps its own `Playnite.Core.Queries.SortOrder` and `Playnite.Core.Library.LibraryGroupBy`
query enums alongside the SDK-facing ones in `Playnite.SDK.Models`;
`Playnite.Core.Hosting.MainViewEnumAdapter` maps between the two. `IMainViewAPI` does not name
the Core `Game` type: it exposes `Playnite.SDK.Models.IGameView` and
`Playnite.Core.Hosting.GameViewAdapter` projects a Core game onto that view. The concrete
services (`NotificationsApi`, `DialogsFactory`, `MainViewApi`, `UriHandlerApi`,
`HtmlDescriptionService`, `HtmlDescriptionSanitizer`, `HostApis`) stay in Core because they are
host implementations, not contracts.

Core references only `Playnite.SDK.Abstractions`; Abstractions references no other
`Playnite.*` assembly and no Avalonia package.

## 2. Frozen surface

The freeze is the committed PublicAPI text surface, copied verbatim into
[`p6-008-frozen-api.txt`](p6-008-frozen-api.txt): **62 exported types, 603 declarations**
(7 types / 61 declarations in `PublicAPI.Shipped.txt`, 55 types / 542 declarations in
`PublicAPI.Unshipped.txt`).

The merged `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` already describe exactly this
surface. Regenerating them against the merged sources produces no diff, and a clean Release
build (warnings as errors, so `RS0016`/`RS0017` are errors) succeeds with them unchanged:
there is no undeclared public symbol and no stale declaration. `PublicAPI.Shipped.txt` and its
checksum pin are unchanged.

The assembly binary baseline `api-baseline/Playnite.SDK.Abstractions.dll` was **regenerated**
from the merged sources so it pins the complete v7 surface (62 types, 29,696 bytes, SHA-256
`e79568cb2f30f404244c3a4db13fe7e731dc617f448a8789fcff591eead8beb8`). Its `.sha256` pin and the
baseline README were updated in step. Ordinary builds then validated the compiled implementation
against the new baseline: `APICompat ran successfully without finding any breaking changes`.

## 3. v6 baseline

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

## 4. ApiCompat method and result

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

Result over the **merged** surface: **306 differences**.

| Code | Count | Meaning |
| --- | ---: | --- |
| `CP0001` | 208 | v6 type outside the v7 abstraction surface |
| `CP0002` | 82 | v6 member deliberately replaced by the headless adaptation |
| `CP0006` | 15 | asynchronous member added to a moved v6-named host interface |
| `CP0009` | 1 | `NotificationMessage` is a sealed record where v6's class was inheritable |

These are post-move numbers: the run was made against the merged `main` sources (`d1d57be`,
`b80847b`), not against a pre-move surface. The 98 `CP0002`/`CP0006`/`CP0009` rows are the
direct evidence of the merged p6-006/p6-007 moves:

- `IDialogsFactory`: 30 v6 synchronous WPF/Dispatcher members removed, 6 typed asynchronous
  members added.
- `IMainViewAPI`: 26 v6 mutable properties, setters, filter-preset and Dispatcher members
  removed, 3 asynchronous queries added over `IGameView`.
- `INotificationsAPI`: 5 `ObservableCollection` mutations removed, 6 id-deduplicated async
  members added.
- `NotificationMessage`: 8 v6 `ICommand`/event/activation members removed, and the headless
  message is a sealed immutable record.
- `SortOrder`: 13 v6 collection sort keys are not implemented by the stored-field subset; they
  are deferred, not reinterpreted as ID sorts.
- `IUriHandlerAPI`: no member difference remains; `RegisterSource`/`RemoveSource` match v6 and
  `PlayniteUriEventArgs` sits in its v10 namespace, so the p6-007 surface is source-compatible.

### Classification

| Classification | Count | Meaning |
| --- | ---: | --- |
| `adapted` | 114 | 98 moved/reshaped host members plus 16 remaining v6 host types intentionally re-shaped by the merged moves |
| `deferred` | 178 | v6 type outside the current freeze; no v7 compatibility claim |
| `excluded` | 14 | global-namespace helpers and WPF/SQLite/markup implementation types outside the abstraction boundary |
| **Total** | **306** | |

Every difference has a row in
[`p6-008-apicompat-classification.tsv`](p6-008-apicompat-classification.tsv) with its group and
decision. No row is classified `breaking`: each break is an intended v7 adaptation recorded in
ADR-018/ADR-023 and in the merged p6-006/p6-007 records.

### Justified suppressions

[`p6-008-apicompat-suppressions.xml`](p6-008-apicompat-suppressions.xml) contains one reviewed
suppression per difference (208 `CP0001`, 82 `CP0002`, 15 `CP0006`, 1 `CP0009`), grouped with the
reason for each diagnostic class. Substituting the two assembly paths and replaying the
comparison with `--suppression-file` prints
`APICompat ran successfully without finding any breaking changes` and exits 0, which proves
the remaining v6 surface is exactly the reviewed set. The file is evidence for the v6 report
only; the shipped Abstractions build validates against its own v7 baseline and suppresses no
diagnostic.

## 5. Contract tests

Every public interface in the frozen assembly has a fake implementation and an executable test
in `tests/Playnite.Core.Tests/SdkContracts/SdkInterfaceContractTests.cs`. The fakes implement
the interfaces at compile time, so a removed member or changed signature fails the build;
each test then calls every member through the real Core service.

| Interface | Fake | Test |
| --- | --- | --- |
| `Playnite.SDK.Data.IDataSerializer` | `ContractDataSerializer` | `IDataSerializer_every_member_is_exercised` |
| `Playnite.SDK.Models.IIdentifiable` | `ContractIdentifiable` | `IIdentifiable_id_is_exercised` |
| `Playnite.SDK.Models.IGameView` | `ContractGameView` | `IGameView_id_and_name_are_exercised` |
| `Playnite.SDK.INotificationHost` | `ContractNotificationHost` | `INotificationHost_is_exercised_by_NotificationsApi` |
| `Playnite.SDK.INotificationsAPI` | `ContractNotificationHost` | `INotificationsAPI_every_member_is_exercised` |
| `Playnite.SDK.IDialogHost` | `ContractDialogHost` | `IDialogHost_is_exercised_by_DialogsFactory` |
| `Playnite.SDK.IDialogsFactory` | `ContractDialogHost` | `IDialogsFactory_every_member_is_exercised` |
| `Playnite.SDK.IMainViewHost` | `ContractMainViewHost` | `IMainViewHost_is_exercised_by_MainViewApi` |
| `Playnite.SDK.IMainViewAPI` | `ContractMainViewHost` | `IMainViewAPI_every_member_is_exercised` |
| `Playnite.SDK.IUriCommandHost` | `ContractUriCommandHost` | `IUriCommandHost_is_exercised_by_UriHandlerApi` |
| `Playnite.SDK.IUriHandlerAPI` | `ContractUriCommandHost` | `IUriHandlerAPI_every_member_is_exercised` |
| `Playnite.SDK.Descriptions.IHtmlDescriptionProvider` | `ContractHtmlDescriptionProvider` | `IHtmlDescriptionProvider_is_exercised_by_HtmlDescriptionService` |
| `Playnite.SDK.Descriptions.IHtmlSanitizer` | `ContractHtmlSanitizer` | `IHtmlSanitizer_is_exercised_by_HtmlDescriptionService` |

Thirteen interfaces, thirteen fakes, thirteen tests, no skipped interface.
`SdkFreezeTests.Every_SDK_interface_has_a_fake_host_contract_test` reflects over the built
assembly and fails if a new interface has no row.

`SdkFreezeTests.Sample_plugin_compiles_against_the_abstractions_assembly_alone` is the
sample-plugin-facing check: it compiles a source file that uses every moved host surface and
`IGameView` against the Abstractions output plus framework references only. Core, the Avalonia
SDK assembly and every other `Playnite.*` assembly are excluded from the reference set, so a
contract that leaks a Core type fails that test.

## 6. Dependency verification

`SdkFreezeTests.Core_and_abstractions_have_no_UI_dependency` consumes the evaluated project
edges, compiler references and resolved package closure recorded by the normal Release build:

- `Playnite.Core` directly references `Playnite.SDK.Abstractions` and nothing else from
  `Playnite.*`; it has no direct or transitive `Avalonia` package/assembly.
- `Playnite.SDK.Abstractions` has no `Playnite.*` dependency and no `Avalonia`, WPF,
  WindowsDesktop, Platform or UI SDK dependency.
- The existing `SdkBoundaryTests` graph/source gates remain green.

## 7. Evidence

- Build: `dotnet build Playnite.slnx --configuration Release --no-restore -warnaserror`
  (CI, warnings are errors).
- Tests: the contract class, the freeze class and the sample-plugin compile check run in
  `tests/Playnite.Architecture.Tests` and `tests/Playnite.Core.Tests`; the full linux-ci run
  is cited in the ledger fragment and the PR.
- No skip, no test suppression, no aggregate edit by this lane.
- The regenerated ApiCompat report and suppression file reproduce the reviewed artifacts
  byte-for-byte from the merged surface, and the suppression replay exits 0.

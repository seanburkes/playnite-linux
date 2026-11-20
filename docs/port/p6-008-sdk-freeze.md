# p6-008 — SDK Abstractions freeze and the v6→v7 ApiCompat report

**Status:** delivered for review. **Unit:** `p6-008`. **Freeze target:**
`src/Playnite.SDK.Abstractions` (assembly `Playnite.SDK.Abstractions`, version `7.0.0.0`,
`net11.0`).

This revision completes the freeze by **moving** the p6-006 and p6-007 host surfaces into the
abstraction assembly and by reshaping `IMainViewAPI` off the Core `Game` type. The previous
revision only documented those surfaces as adapted in Core; that deferral is withdrawn.

The machine-readable surface is [`p6-008-frozen-api.txt`](p6-008-frozen-api.txt); the complete,
normalized ApiCompat output is [`p6-008-apicompat-v6-v7.txt`](p6-008-apicompat-v6-v7.txt);
every difference and its classification is in
[`p6-008-apicompat-classification.tsv`](p6-008-apicompat-classification.tsv); the reviewed
suppression file is [`p6-008-apicompat-suppressions.xml`](p6-008-apicompat-suppressions.xml);
the interface coverage map is [`p6-008-interface-coverage.tsv`](p6-008-interface-coverage.tsv).

## 1. What moved

The host contracts, their injectable host seams and their request/response DTOs moved out of
`Playnite.Core` into `Playnite.SDK.Abstractions`. The concrete services stay in Core
(`NotificationsApi`, `DialogsFactory`, `MainViewApi`, `UriHandlerApi`,
`HtmlDescriptionService`, `HtmlDescriptionSanitizer`, `HostApis`) because they are host
implementations, not contracts; a plugin can consume every moved contract with a reference to
Abstractions alone.

The v6-named interfaces keep the v10 namespace `Playnite.SDK` (or `Playnite.SDK.Models` for
model types), so the port is a move toward the v10 names rather than a new spelling:

| Old namespace | New namespace | Types |
| --- | --- | --- |
| `Playnite.Core.Hosting` | `Playnite.SDK` | `INotificationsAPI`, `INotificationHost`, `NotificationMessage`, `NotificationType`, `NotificationHostStatus`, `NotificationHostResult`, `NotificationPublishStatus`, `NotificationPublishResult`, `NotificationDismissStatus`, `NotificationDismissResult`, `NotificationDismissFailure`, `NotificationDismissAllResult` |
| `Playnite.Core.Hosting` | `Playnite.SDK` | `IDialogsFactory`, `IDialogHost`, `MessageDialogButton`, `MessageDialogImage`, `MessageDialogResult`, `MessageDialogRequest`, `FileDialogKind`, `FileDialogRequest`, `TextInputDialogRequest`, `MessageDialogResponse`, `FileDialogResponse`, `TextInputDialogResponse` |
| `Playnite.Core.Hosting` | `Playnite.SDK` | `IMainViewAPI`, `IMainViewHost`, `MainViewState`, `MainViewResponse`, `MainViewGamesResponse`, `DesktopView`, `FullscreenView` |
| `Playnite.Core.Hosting` | `Playnite.SDK` | `HostOperationStatus` |
| `Playnite.Core.UriHandling` | `Playnite.SDK` | `IUriHandlerAPI`, `IUriCommandHost`, `UriCommandStatus`, `UriCommandResult`, `UriDispatchStatus`, `UriDispatchResult`, `PlayniteUriTarget`, `PlayniteViewTarget`, `PlayniteFilterField`, `PlayniteFilterTarget`, `PlayniteUriRoute` |
| `Playnite.Core.UriHandling` | `Playnite.SDK.Events` | `PlayniteUriEventArgs` (the v10 namespace for the type) |
| `Playnite.Core.Descriptions` | `Playnite.SDK.Descriptions` | `IHtmlDescriptionProvider`, `IHtmlSanitizer`, `HtmlDescriptionStatus`, `HtmlDescriptionSourceResult`, `HtmlDescriptionResult`, `HtmlSanitizationResult` |
| `Playnite.Core.Queries` | `Playnite.SDK.Models` | `SortOrder` (the v10 model namespace) |
| `Playnite.Core.Library` | `Playnite.SDK.Models` | `LibraryGroupBy` |
| — (new) | `Playnite.SDK.Models` | `IGameView`, the abstraction-level game view |

Core still references only `Playnite.SDK.Abstractions`, and Abstractions still references no
other `Playnite.*` assembly and no Avalonia package.

## 2. `IMainViewAPI` reshape

`IMainViewAPI` no longer names the Core `Game` type. The abstraction exposes
`Playnite.SDK.Models.IGameView`:

```csharp
public interface IGameView
{
    Guid Id { get; }
    string Name { get; }
}
```

`Playnite.Core.Models.Game` implements `IGameView`, so the host hands over its Core games with
no copy and no mapping, while a plugin sees only identity and display name. The reshape is
mechanical and behaviour-preserving:

| Member | Before | After |
| --- | --- | --- |
| `IMainViewHost.GetSelectedGamesAsync` | `Task<IReadOnlyList<Core.Models.Game>>` | `Task<IReadOnlyList<IGameView>>` |
| `IMainViewHost.GetFilteredGamesAsync` | `Task<IReadOnlyList<Core.Models.Game>>` | `Task<IReadOnlyList<IGameView>>` |
| `MainViewState.SelectedGames` / `FilteredGames` | `IReadOnlyList<Core.Models.Game>` | `IReadOnlyList<IGameView>` |
| `MainViewGamesResponse.Games` | `IReadOnlyList<Core.Models.Game>` | `IReadOnlyList<IGameView>` |

`SortOrder` and `LibraryGroupBy` moved to `Playnite.SDK.Models`, so `MainViewState` keeps every
shell field, every `Enum.IsDefined` validation and its copied snapshots. `MainViewApi` keeps
the same delegation, null-state/null-list failure results and cancellation propagation; only
the compile-time element type changed.

## 3. Frozen surface

The freeze is the committed PublicAPI text surface, copied verbatim into
[`p6-008-frozen-api.txt`](p6-008-frozen-api.txt): **62 exported types, 603 declarations**.
`PublicAPI.Shipped.txt` is unchanged (61 declarations, the eight original seed contracts) and
its checksum pin is unchanged. The moved contracts are new members of this assembly, so they
are declared in `PublicAPI.Unshipped.txt` (542 declarations). The assembly binary baseline was
regenerated from the same sources so ordinary builds keep validating the complete v7 surface.

## 4. v6 baseline

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

## 5. ApiCompat method and result

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

Result: **306 differences**.

| Code | Count | Meaning |
| --- | ---: | --- |
| `CP0001` | 208 | v6 type outside the v7 abstraction surface |
| `CP0002` | 82 | v6 member deliberately replaced by the headless adaptation |
| `CP0006` | 15 | asynchronous member added to a moved v6-named host interface |
| `CP0009` | 1 | `NotificationMessage` is a sealed record where v6's class was inheritable |

The 98 `CP0002`/`CP0006`/`CP0009` rows are the direct evidence of the move and reshape:

- `IDialogsFactory`: 30 v6 synchronous WPF/Dispatcher members removed, 6 typed asynchronous
  members added.
- `IMainViewAPI`: 26 v6 mutable properties, setters, filter-preset and Dispatcher members
  removed, 3 asynchronous queries added over `IGameView`.
- `NotificationMessage`: 8 v6 `ICommand`/event/activation members removed; the message is an
  immutable record.
- `INotificationsAPI`: 5 `ObservableCollection` mutations removed, 6 id-deduplicated async
  members added.
- `SortOrder`: 13 v6 collection sort keys not implemented by the stored-field subset; they are
  deferred, not reinterpreted as ID sorts.
- `NotificationMessage`: the headless message is a sealed immutable record; v6's inheritable
  class is not reproduced.
- `IUriHandlerAPI`: no member difference remains; `RegisterSource`/`RemoveSource` match v6 and
  `PlayniteUriEventArgs` moved to its v10 namespace, so the p6-007 surface is source-compatible.

### Classification

| Classification | Count | Meaning |
| --- | ---: | --- |
| `adapted` | 114 | 98 moved/reshaped host members plus 16 remaining v6 host types intentionally re-shaped |
| `deferred` | 178 | v6 type outside the current freeze; no v7 compatibility claim |
| `excluded` | 14 | global-namespace helpers and WPF/SQLite/markup implementation types outside the abstraction boundary |
| **Total** | **306** | |

Every difference has a row in
[`p6-008-apicompat-classification.tsv`](p6-008-apicompat-classification.tsv) with its group and
decision. No row is classified `breaking`: each break is an intended v7 adaptation recorded in
ADR-018/ADR-023 and in this move.

### Justified suppressions

[`p6-008-apicompat-suppressions.xml`](p6-008-apicompat-suppressions.xml) contains one reviewed
suppression per difference (208 `CP0001`, 82 `CP0002`, 15 `CP0006`, 1 `CP0009`), grouped with the reason
for each diagnostic class. Substituting the two assembly paths and replaying the comparison
with `--suppression-file` prints
`APICompat ran successfully without finding any breaking changes` and exits 0, which proves
the remaining v6 surface is exactly the reviewed set. The file is evidence for the v6 report
only; the shipped Abstractions build validates against its own v7 baseline and suppresses no
diagnostic.

## 6. Contract tests and the plugin compile check

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

`SdkFreezeTests.Sample_plugin_compiles_against_the_abstractions_assembly_alone` is the
sample-plugin-facing check: it compiles a source file that uses all four moved host surfaces
and `IGameView` against the Abstractions output plus framework references only. Core, the
Avalonia SDK assembly and every other `Playnite.*` assembly are excluded from the reference
set, so a contract that leaks a Core type fails that test.

## 7. Dependency verification

`SdkFreezeTests.Core_and_abstractions_have_no_UI_dependency` consumes the evaluated project
edges, compiler references and resolved package closure recorded by the normal Release build:

- `Playnite.Core` directly references `Playnite.SDK.Abstractions` and nothing else from
  `Playnite.*`; it has no direct or transitive `Avalonia` package/assembly.
- `Playnite.SDK.Abstractions` has no `Playnite.*` dependency and no `Avalonia`, WPF,
  WindowsDesktop, Platform or UI SDK dependency.
- The existing `SdkBoundaryTests` graph/source gates remain green.

## 8. Evidence

- Build: `dotnet build Playnite.slnx --configuration Release --no-restore -warnaserror`
  (CI, warnings are errors).
- Tests: the contract class, the freeze class and the sample-plugin compile check run in
  `tests/Playnite.Architecture.Tests` and `tests/Playnite.Core.Tests`; the full linux-ci run
  is cited in the ledger fragment and the PR.
- No skip, no test suppression, no protected-source edit, no aggregate edit by this lane.

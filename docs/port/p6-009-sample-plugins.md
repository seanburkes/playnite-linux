# p6-009 — Sample plugins (Hello World, library provider, metadata provider)

**Status:** delivered for review. **Unit:** `p6-009`. **Branch:** `fm/p6-009`.

This unit builds three sample plugins, compiles them against the frozen
`Playnite.SDK.Abstractions` plus the `Playnite.SDK` preview assembly, and loads each one in a
fake host that discovers the SDK-owned extension contract, instantiates the plugin and invokes
its extension points. It is compile-and-load evidence only: no store, no packaging, no network,
and no plugin UI freeze.

## 1. Premise correction

The dispatch brief said the frozen Abstractions and the SDK UI preview were already on main.
The first half was true; the second was not. `docs/port/p6-008-frozen-api.txt` contained only
`LibraryGetGamesArgs` and `MetadataField` under `Playnite.SDK.Plugins`, and
`docs/port/p6-008-apicompat-classification.tsv` classified all 59 `Playnite.SDK.Plugins.*`
types (including `Plugin`, `LibraryPlugin`, `MetadataPlugin`, `GameMetadata` and `Game`) as
`deferred`. `src/Playnite.SDK` was an empty preview assembly with only an internal
`AssemblyMarker`.

Declaring the extension points inside `samples/` would have made the fake-host test circular,
and editing Abstractions would have unfrozen p6-008 and changed protected baseline files. The
p6-009 ruling therefore kept Abstractions frozen and approved a minimal **headless preview**
plugin surface in `src/Playnite.SDK`, new files only. The decision is recorded as an addendum to
ADR-023 in [`00-DECISIONS.md`](00-DECISIONS.md#adr-023-addendum-2026-11-20--headless-preview-plugin-surface).

## 2. Preview surface (`src/Playnite.SDK`, unfrozen)

| Type | Namespace | Shape |
| --- | --- | --- |
| `IPlayniteExtension` | `Playnite.SDK.Plugins` | `Guid Id`, `string Name` |
| `Plugin` | `Playnite.SDK.Plugins` | abstract `IPlayniteExtension`, `IDisposable`; `OnApplicationStarted(OnApplicationStartedEventArgs)` |
| `GenericPlugin` | `Playnite.SDK.Plugins` | `Plugin` with no extra surface |
| `LibraryPlugin` | `Playnite.SDK.Plugins` | `IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs)` |
| `MetadataPlugin` | `Playnite.SDK.Plugins` | `IReadOnlyList<MetadataField> SupportedFields`, `GameMetadata GetMetadata(GetMetadataArgs)` |
| `GetMetadataArgs` | `Playnite.SDK.Plugins` | frozen `IGameView Game`, requested `MetadataField` list |
| `GameMetadata` | `Playnite.SDK.Models` | name, provider game id, description, release date, developers, genres |

Every type carries an XML-doc remark saying **PREVIEW, NOT FROZEN**. The surface deliberately:

- keeps the v10 namespace and type names where they exist, but does not claim v6 source
  compatibility;
- uses the frozen `LibraryGetGamesArgs` and `IGameView` from Abstractions rather than
  duplicating moved types;
- omits `IPlayniteAPI`, host services, import/play/install hooks, on-demand metadata providers,
  and all UI types;
- is not covered by the Abstractions PublicAPI text or the `api-baseline` ApiCompat check, and
  can change before v1.

UI extension points remain deferred to p8-007, as ADR-023 requires.

## 3. Samples (`samples/`)

| Project | Extension point | Behaviour |
| --- | --- | --- |
| `Playnite.Sample.HelloWorld` | `GenericPlugin.OnApplicationStarted` | counts the host's start call |
| `Playnite.Sample.LibraryProvider` | `LibraryPlugin.GetGames` | returns two deterministic games, honours cancellation |
| `Playnite.Sample.MetadataProvider` | `MetadataPlugin.GetMetadata` | returns only the requested fields, deterministically |

Each project targets `net11.0`, sets `TreatWarningsAsErrors=true`, and references only
`src/Playnite.SDK/Playnite.SDK.csproj` (Abstractions comes through the project graph). No
sample references Core, Desktop, Platform, Avalonia, a store, a network client or a database.

## 4. Fake host and tests (`tests/Playnite.Plugin.Tests`)

`FakePluginHost` loads each sample assembly by reflection
(`AssemblyLoadContext.LoadFromAssemblyPath`), discovers concrete `IPlayniteExtension`
implementations, constructs them, and invokes the start, library and metadata paths. Discovery
is against the SDK-owned interface, never a sample-declared one; a test asserts that
`typeof(IPlayniteExtension).Assembly` is `Playnite.SDK`.

`SamplePluginBuildTests` proves each sample assembly exists, targets `net11.0`, references the
preview SDK and Abstractions, references no product or Avalonia assembly, and takes the SDK as
its only project reference with warnings as errors. Because the test project references the
samples, a sample that fails to compile fails the test project build before any load test runs.

`SamplePluginLoadTests` proves:

- three extensions are discovered, with distinct ids and the expected names;
- the Hello World start extension point is invoked and its counter increments once;
- the library provider returns both games with their ids, developers and release dates, and
  throws `OperationCanceledException` for an already-cancelled request;
- the metadata provider returns the requested name, description, developers, genres and release
  date, and leaves unrequested fields null.

Local Release evidence:

```text
dotnet build tests/Playnite.Plugin.Tests/Playnite.Plugin.Tests.csproj -c Release --no-restore -warnaserror
  0 Warning(s), 0 Error(s)

dotnet test --project tests/Playnite.Plugin.Tests/Playnite.Plugin.Tests.csproj -c Release --no-build --no-restore
  total 15, failed 0, succeeded 15, skipped 0

dotnet test --project tests/Playnite.Architecture.Tests/Playnite.Architecture.Tests.csproj -c Release
  total 210, failed 0, succeeded 210, skipped 0
```

## 5. Shared solution edit

`Playnite.slnx` is shared build metadata outside the ownership map. This unit adds one
`/samples/` folder with the three sample projects and the `Playnite.Plugin.Tests` project under
`/tests/`. Nothing else in the solution file is reformatted or reordered.

## 6. Out of scope

No add-on store, installation model, packaging, download/update path, network call, plugin UI,
UI extension-point freeze or Abstractions change. p6-010 owns the add-on manifest and
installation model; p8-007 owns the UI extension-point freeze.

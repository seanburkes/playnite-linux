# Phase 6 launch brief — core services and SDK v7

- Date: 2026-11-19
- Board unit: `p6-shards`
- Base: `14406866743705c478785a827dc442768bf8f460` (`main`)
- Plan: [`03-EXECUTION-PLAN.md`](03-EXECUTION-PLAN.md) Phase 6
- Architecture: [`02-ARCHITECTURE.md`](02-ARCHITECTURE.md) §3–§6; [`04-VERIFICATION.md`](04-VERIFICATION.md)
- Scope: the Phase 6 board and dependency plan only. This is not a gate acceptance and changes no
  product code, test, protected oracle, baseline or generated aggregate.
- Revision 2026-11-20: `p6-001` is the library service only, as delivered by
  https://github.com/seanburkes/playnite-linux/pull/161. The CLI query surface moves to new unit
  `p6-011`, and new unit `p6-012` carries repository-level ordered paging. See
  [Board correction, 2026-11-20](#board-correction-2026-11-20).
- Revision 2026-11-20 (second): the four remaining unstarted rows `p6-007`–`p6-010` now carry
  final dependencies, owner, oracle/test surface and foreign-owned-path guidance, and the Phase 6
  exit gate records the current evidence state without claiming acceptance. See
  [Remaining units, 2026-11-20](#remaining-units-2026-11-20).

## Constraint: one unit namespace per pull request

The unit-ownership and unit-evidence guards allow a pull request to touch exactly one unit's queue
shard, evidence namespace and mapped paths. This board PR therefore adds only `p6-shards`. The
twelve rows below are the board's planned canonical rows: each unit's own PR creates its
`docs/port/units/<id>.queue.tsv` (or replaces its existing seeded row) with that row and records
the transition in its own ledger fragment. Adding twelve queue shards here would fail both guards,
and rewriting a seeded row for another unit is forbidden by the same rule.

## Planned queue rows, in plan order

Exact queue rows, copied verbatim so a dispatch brief can drop one into its unit shard:

```tsv
id	wave	area	unit	depends	status	owner	oracle	test	notes
p6-001	W6	core	Library service	p3-006,p4-001	todo	core	golden:library	test:library	First Phase 6 unit. Headless read facade over the SQLite repositories and the v10 query helpers. It does not own the CLI or edit src/Playnite.Cli: p6-011 owns list, search and launch --dry-run against this service. Delivered by https://github.com/seanburkes/playnite-linux/pull/161.
p6-002	W6	core	Import service	p6-001,p4-003	todo	core	golden:import	test:import	Wraps the verified LibraryMigration path as a service; keeps the existing import-v10 evidence and adds the CLI import surface. Exit-gate item 1.
p6-003	W6	core	Steam sync	p6-002,p5-002	todo	core	golden:steam-sync	test:steam-sync	Syncs discovered Steam apps and ACF metadata into the library; no launch. Depends on the import service and merged Steam discovery (p5-002).
p6-004	W6	core	Settings service	p6-001,p4-004	todo	core	golden:settings	test:settings	Load, validate and write settings over the p4-004 migration output, preserving unknown properties and the legacy bag.
p6-005	W6	core	Metadata service	p6-001,p5-003	todo	core	golden:metadata	test:metadata	Steam app-details fetch with cache and rate limit, degrading to name/artwork; provider seam for other sources later. No WebView (ADR-019).
p6-006	W6	sdk	Notifications, dialogs and main-view API	p6-001,p5-007a	todo	sdk	contract:fake-host	test:sdk-host	INotificationsAPI, IDialogsFactory and IMainViewAPI over the p5-007a notification service and the platform seam. Every interface gets a fake-host contract test. Exit-gate item 4.
p6-007	W6	sdk	URI handler and HTML description service	p5-005a,p6-005	todo	sdk	contract:fake-host; legacy/source/Playnite/PlayniteUriHandler.cs; legacy/source/PlayniteSDK/IUriHandlerAPI.cs; legacy/source/Tests/Playnite.Tests/PlayniteUriHandlerTests.cs	test:uri-html; UriHandlerApiTests; HtmlDescriptionServiceTests	Adds the host-facing IUriHandlerAPI and playnite:// source registration/dispatch over the merged p5-005a protocol handler, plus the non-interactive HTML description path. p5-005a owns src/Playnite.Platform/Integration/ProtocolHandler.cs, src/Playnite.Platform.Linux/Integration/PlayniteProtocolHandler.cs and PlayniteUri.cs, tests/Playnite.Core.Tests/Integration/ProtocolHandlerTests.cs and PlayniteUriTests.cs, and docs/port/p5-005a-integration.md (foreign-owned), so this unit adds new files instead of editing them. The renderer must not enter Core: Core may not reference Avalonia, so an Avalonia.HtmlRenderer 12.0.0 wrapper lives in Playnite.SDK behind a Core interface. No WebView (ADR-019). Exit-gate item 4.
p6-008	W6	sdk	SDK Abstractions freeze and ApiCompat report	p6-001,p6-002,p6-003,p6-004,p6-005,p6-006,p6-007	todo	sdk	api-compat; src/Playnite.SDK.Abstractions/api-baseline; docs/port/ARCHITECTURE-CHECKS.md	test:apicompat; ApiCompatReportTests; SdkFreezeCoverageTests	Freezes the v7 Abstractions surface: moves the p6-006/p6-007 host surfaces into Playnite.SDK.Abstractions, updates PublicAPI.Shipped.txt and PublicAPI.Unshipped.txt, and produces the reviewed v6-to-v7 Microsoft.DotNet.ApiCompat breaking-change report without rebaselining away a break. Those declaration files and the whole api-baseline directory are PROTECTED_PATHS owned by p2-006, p2-006-harden and p3-003c, and ApiCompatTests.cs/PublicApiTests.cs are foreign-owned, so dispatch must authorise the protected change through the oracle-change label and add new test files instead of editing the existing ones. UI extension points stay unfrozen until the Phase 7 pilot (ADR-023). Exit-gate items 2 and 4.
p6-009	W6	sdk	Sample plugins (Hello World, library provider, metadata provider)	p6-008	todo	sdk	compile; docs/port/p6-009-sample-plugins.md	test:plugins; SamplePluginBuildTests; SamplePluginLoadTests	Builds the Hello World, library-provider and metadata-provider samples against the frozen Abstractions plus the SDK UI preview and loads each one in a fake host. This unit adds a new samples/ tree and new test project/files and touches no foreign-owned path; Playnite.slnx is shared build metadata outside the ownership map, so the unit records that solution edit explicitly. Compile and load evidence only: no store, no packaging, no plugin UI freeze. Exit-gate item 2.
p6-010	W6	sdk	Add-on manifest and installation model	p6-009,p3-005	todo	sdk	golden:manifests; legacy/source/Playnite/Manifests; legacy/source/Playnite/API/AddonsAPI.cs; legacy/source/Tests/Playnite.Tests/Manifests/AddonManifestTests.cs	test:addon-model; AddonInstallationTests	Manifest validation plus the install/uninstall directory model only; the add-on store stays v1.1 (Phase 10). p3-005 owns src/Playnite.Core/Manifests/*.cs, tests/fixtures/manifests-v10/*, docs/port/p3-005-manifests.md and the manifest tests, so this unit adds new files (for example src/Playnite.Core/Addons/) instead of editing them; tests/golden/manifests is shared (*). No store, network, download or update path.
p6-011	W6	core	CLI query surface	p6-001	todo	core	contract:cli	test:cli	Owns src/Playnite.Cli/Program.cs plus the CLI usage and docs. Delivers list, search and launch --dry-run (resolve a game and print the planned launch without starting a process) over the p6-001 library service, closing Phase 6 exit-gate item 1 without the UI. File ownership is attributed by the generated ownership map after its commits merge; firstmate confirms attribution rather than seeding ownership.paths by hand.
p6-012	W6	core	Repository-level ordered paging for list/search	p6-001,p6-011	todo	core	oracle:GamesCollection/Models; docs/port/p4-001-sqlite.md	test:RepositoryPaging	Owns the repository path src/Playnite.Core/Database/GameRepository.cs. Pushes ordering and paging for list/search into SQL so the CLI and the Phase 7 UI read one page without materialising the whole library; the public query shape and comparers stay in p6-001/p6-011. REQUIRED before Phase 7 UI. File ownership is attributed by the generated ownership map after its commits merge; firstmate confirms attribution rather than seeding ownership.paths by hand.
```

## Boundaries: twelve units, not the five seeded rows

The five `p6-001`–`p6-005` rows were generated at scaffolding time (`f886749`, PR #9) from a
coarse plan summary. Phase 6 now names twelve discrete deliverables, and bundling them would exceed
the 200–800 changed-line / one-public-surface unit rule and mix service, SDK-contract, CLI and
packaging evidence. No unit is merged; the splits are forced by the plan's own order.

| Seeded row | Plan units | Why it changed |
| --- | --- | --- |
| `p6-001` Library service | `p6-001` Library service | Scope narrowed to the library service: dependencies are made explicit (`p3-006`, `p4-001`), and the CLI list/search/launch dry-run surface moves to `p6-011` because PR #161 delivered only the headless read facade. |
| `p6-002` Import/sync service | `p6-002` Import service, `p6-003` Steam sync | Import is the verified v10 migration path; Steam sync is a different input (discovery) and depends on import, so it cannot share one reviewable unit. |
| `p6-003` Settings/metadata services | `p6-004` Settings service, `p6-005` Metadata service | Two independent service surfaces with different oracles and failure modes; the plan names them separately. |
| `p6-004` SDK v7 contracts | `p6-006` Notifications/dialogs/main-view API, `p6-007` URI handler/HTML description, `p6-008` Abstractions freeze and ApiCompat report, `p6-010` Add-on manifest/installation model | The plan separates host-facing SDK interfaces (which need implementations and fake-host contract tests), the Abstractions freeze (which must follow every service unit), and the add-on model (which depends on the sample plugins). The freeze is now `p6-008`, not `p6-004`. |
| `p6-005` Sample plugins | `p6-009` Sample plugins | Scope stands; it now follows the Abstractions freeze (`p6-008`). |

`p6-006`–`p6-012` are genuinely new ids; they are not renames. The existing five rows keep their
ids and are redefined in place by their own unit PRs, so no id is retired and no unit is renamed.
`p6-011` and `p6-012` are the 2026-11-20 correction described below.

## Units, dependencies and starting point

| Unit | Depends | Already in the tree | Exit-gate |
| --- | --- | --- | --- |
| `p6-001` Library service | `p3-006`, `p4-001` | SQLite repositories and schema (`src/Playnite.Core/Database`), filter/search/comparer leaves (`src/Playnite.Core/Queries`) | Library service golden and contract tests; supplies the read API that `p6-011` calls |
| `p6-002` Import service | `p6-001`, `p4-003` | `LibraryMigration`, `AssetCopy`, CLI `import-v10` (p4-002) | CLI `import` (item 1) |
| `p6-003` Steam sync | `p6-002`, `p5-002` | Steam discovery, root resolver, library/manifest parsers (`src/Playnite.Steam`) | Sync evidence only |
| `p6-004` Settings service | `p6-001`, `p4-004` | `SettingsMigration`, `V10SettingsProperties` | Settings golden |
| `p6-005` Metadata service | `p6-001`, `p5-003` | Steam artwork cache/fetcher; no app-details service | Metadata golden |
| `p6-006` Notifications, dialogs and main-view API | `p6-001`, `p5-007a` | Platform notification service (`p5-007a`); SDK surface rows `sdk-002`, `sdk-003`, `sdk-004` are still `planned` | Fake-host contract tests (item 4) |
| `p6-007` URI handler and HTML description service | `p5-005a`, `p6-005` | `PlayniteProtocolHandler`, `PlayniteUri` (`p5-005a`) | Fake-host contract tests (item 4) |
| `p6-008` SDK Abstractions freeze and ApiCompat report | `p6-001`–`p6-007` | Seven-type preview baseline, `PublicAPI.Shipped.txt`, ApiCompat task already wired | Abstractions build + reviewed ApiCompat report (item 2) |
| `p6-009` Sample plugins | `p6-008` | None | Samples compile and load (item 2) |
| `p6-010` Add-on manifest and installation model | `p6-009`, `p3-005` | Manifest parsing/model (`AddonManifestBase`, `ExtensionManifest`, `ManifestSerialization`) | Manifest golden + install/uninstall model; no store |
| `p6-011` CLI query surface | `p6-001` | `src/Playnite.Cli/Program.cs` and the existing `import-v10` CLI (p4-002); `LibraryService` from `p6-001` | CLI `list`/`search`/`launch --dry-run` exit codes and output on a fixture library with no UI assembly loaded (item 1) |
| `p6-012` Repository-level ordered paging | `p6-001`, `p6-011` | `GameRepository.GetAllAsync` currently reads every row (`src/Playnite.Core/Database/GameRepository.cs`) | Repository paging tests: the same page and order as the in-memory comparers, without reading the whole library |

Dependency notes:

- `p6-001` is first and has no Phase 6 prerequisite. It is the library service only; the CLI
  query surface is `p6-011`.
- `p6-011` depends on `p6-001` and can start as soon as the library service lands. `p6-012`
  depends on both `p6-001` and `p6-011` and is **required before Phase 7 UI** so the grid does not
  page a fully materialised library.
- `p6-002` and `p6-004` depend on `p6-001`. `p6-005` also depends on `p6-001`; it has no import
  or Steam-sync prerequisite and can run alongside `p6-002`/`p6-004`.
- `p6-003` depends on the import service (`p6-002`) and on merged Steam discovery (`p5-002`).
- `p6-006` and `p6-007` depend on the services they expose, and `p6-007` also on the merged
  protocol handler (`p5-005a`).
- `p6-008` follows every service unit (`p6-001`–`p6-007`); the freeze cannot be reviewed against a
  moving service surface.
- `p6-009` follows the Abstractions freeze (`p6-008`); `p6-010` follows `p6-009`.

## Dispatch order

| Wave | Units | Note |
| --- | --- | --- |
| 6a | `p6-001` | Single first unit; nothing else starts before it lands. |
| 6b | `p6-011` | After `p6-001`; owns the CLI query surface. |
| 6c | `p6-012` | After `p6-011`; required before the Phase 7 UI pages the library. |
| 6d | `p6-002`, `p6-004`, `p6-005` | Independent after `p6-001`; can run in parallel. |
| 6e | `p6-003` | After `p6-002`; reuses `p5-002` discovery. |
| 6f | `p6-006`, `p6-007` | After their service dependencies; both close the fake-host contract surface. |
| 6g | `p6-008` | After `p6-001`–`p6-007`; freezes Abstractions only. |
| 6h | `p6-009`, then `p6-010` | Samples compile and load, then the add-on model. |

## Remaining units, 2026-11-20

The rows above are planned canonical rows, not shards. The unit-ownership and unit-evidence
guards allow one unit namespace per pull request, so this board PR cannot create
`docs/port/units/p6-007.queue.tsv`–`p6-010.queue.tsv` or their ledger fragments. Each unit's own
PR creates its queue shard from the exact row above and carries its own fragment. This section
records what that dispatch needs: dependency state, owner, oracle and test surface, and the paths
that are foreign-owned or protected so the unit adds new files where it can.

| Unit | Depends | Dependencies at 2026-11-20 | Owner | Oracle / test labels | Exit gate |
| --- | --- | --- | --- | --- | --- |
| `p6-007` URI handler and HTML description service | `p5-005a`, `p6-005` | Both satisfied: `p5-005a` done at `785b892` (PR #119), `p6-005` done at `b46bef6` (PR #189). Startable now. | `sdk`; branch `fm/p6-007`, declared `Port-Unit: p6-007` | `contract:fake-host`, `test:uri-html` | Item 4 |
| `p6-008` SDK Abstractions freeze and ApiCompat report | `p6-001`–`p6-007` | Not satisfied: `p6-007` is `todo`; `p6-003` code merged at `3ee1379` (PR #180) but its queue row is still `in-progress` with no close-out row. | `sdk`; branch `fm/p6-008` | `api-compat`, `test:apicompat` | Items 2 and 4 |
| `p6-009` Sample plugins | `p6-008` | Not satisfied: `p6-008` is `todo`. | `sdk`; branch `fm/p6-009` | `compile`, `test:plugins` | Item 2 |
| `p6-010` Add-on manifest and installation model | `p6-009`, `p3-005` | `p3-005` done at `8e3c9c5` (PR #73); `p6-009` is `todo`, so not satisfied. | `sdk`; branch `fm/p6-010` | `golden:manifests`, `test:addon-model` | None; Phase 6 scope, store is v1.1 |

### `p6-007` — URI handler and HTML description service

- **Scope.** Add the host-facing `IUriHandlerAPI` (register/remove a source, reserved `playnite`
  source rejected) and the `playnite://` dispatch path over the merged p5-005a protocol handler,
  plus the non-interactive HTML description service. The legacy oracle is
  `legacy/source/Playnite/PlayniteUriHandler.cs`, `legacy/source/PlayniteSDK/IUriHandlerAPI.cs` and
  `legacy/source/Tests/Playnite.Tests/PlayniteUriHandlerTests.cs`. The description path is
  non-interactive only: no WebView (ADR-019), no script, no network fetch.
- **Foreign-owned paths.** `p5-005a` owns
  `src/Playnite.Platform/Integration/ProtocolHandler.cs`,
  `src/Playnite.Platform.Linux/Integration/PlayniteProtocolHandler.cs`,
  `PlayniteUri.cs`, `SingleInstanceService.cs`, `XdgDirectories.cs`,
  `tests/Playnite.Core.Tests/Integration/ProtocolHandlerTests.cs` and `PlayniteUriTests.cs`, and
  `docs/port/p5-005a-integration.md`. Those commits are merged, so the files are foreign-owned to
  this unit: add new files instead of editing them (for example
  `src/Playnite.Core/Uri/`, new tests under `tests/Playnite.Core.Tests/Uri/`, and
  `docs/port/p6-007-uri-html.md`).
- **Architecture constraint.** `Playnite.Core` may not reference Avalonia. If the description
  view uses `Avalonia.HtmlRenderer` 12.0.0, the control wrapper belongs in `Playnite.SDK`
  (Avalonia UI contracts) behind a Core interface; `Directory.Packages.props` is shared build
  metadata outside the ownership map, so the unit records that package addition.
- **Test surface.** Fake-host contract tests for every interface it adds, URI parse/register/
  dispatch parity with the legacy tests, a plain-text/escaped description fallback, and a degraded
  no-host path. Zero skips.

### `p6-008` — SDK Abstractions freeze and ApiCompat report

- **Scope.** Move the p6-006 and p6-007 host surfaces into `Playnite.SDK.Abstractions`, freeze the
  v7 surface in `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`, and produce the reviewed
  v6-to-v7 `Microsoft.DotNet.ApiCompat` breaking-change report. A break may not be silenced by
  rebaselining the shipped API or the reference assembly. UI extension points stay unfrozen until
  the Phase 7 shell pilot (ADR-023). `IMainViewAPI`'s Core `Game` dependency must be reshaped
  before it can move, because Abstractions may not reference Core.
- **Protected and foreign-owned paths.** `src/Playnite.SDK.Abstractions/PublicAPI.Shipped.txt`,
  `PublicAPI.Unshipped.txt` and the whole `api-baseline/` directory are in `PROTECTED_PATHS` and
  owned by `p2-006`, `p2-006-harden` and `p3-003c`. `ApiCompatTests.cs`, `PublicApiTests.cs`,
  `SdkBoundaryTests.cs` and `SdkBoundaryPolicy.cs` are foreign-owned. The freeze cannot add a new
  file in place of the protected declarations, so its dispatch must explicitly authorise the
  protected change through the `oracle-change` label; new coverage goes in new test files, and
  existing tests are not edited.
- **Test surface.** The new report artifact is reviewed, the protected checksums still match, the
  existing ApiCompat/PublicAPI/architecture suites stay green, and a new report test proves that a
  removed or changed member fails rather than being reaccepted. The interface-coverage list must
  show every SDK interface has a fake-host test before the freeze.

### `p6-009` — Sample plugins

- **Scope.** Build the Hello World, library-provider and metadata-provider samples against the
  frozen Abstractions plus the SDK UI preview and load each one in a fake host. Compile and load
  evidence only: no store, no packaging, no plugin UI freeze.
- **Foreign-owned paths.** None. The unit adds a new `samples/` tree and new test files. The
  solution file `Playnite.slnx` is shared build metadata outside the ownership map, so the unit
  records the solution edit instead of treating it as an owned product path.
- **Test surface.** Each sample builds in Release against the frozen surface and loads through the
  fake host with its manifest and entry point resolved; a sample that fails to compile or load
  fails the test. Zero skips.

### `p6-010` — Add-on manifest and installation model

- **Scope.** Manifest validation plus the install/uninstall directory model only. The add-on
  store, download, update and online catalog stay v1.1 (Phase 10). The legacy oracle is
  `legacy/source/Playnite/Manifests`, `legacy/source/Playnite/API/AddonsAPI.cs`,
  `legacy/source/Playnite/Addons/Addons.cs` and
  `legacy/source/Tests/Playnite.Tests/Manifests/AddonManifestTests.cs`.
- **Foreign-owned paths.** `p3-005` owns `src/Playnite.Core/Manifests/*.cs`,
  `tests/Playnite.Core.Tests/ManifestTests.cs`, `tests/fixtures/manifests-v10/*` and
  `docs/port/p3-005-manifests.md`. This unit adds new files (for example
  `src/Playnite.Core/Addons/`, new tests and `docs/port/p6-010-addon-model.md`) and does not edit
  the p3-005 manifest parser or its fixtures. `tests/golden/manifests` is shared (`*`).
- **Test surface.** Manifest validation reuses the p3-005 parser and golden fixtures read-only; a
  new installation-model test covers install into a temporary directory, uninstall, a refused
  path traversal, a duplicate install and a manifest/version mismatch. No network and no store
  path.

## Phase 6 exit gate

The gate is a phase-level result; no single unit may claim it. The captain's accepted record is
[`gates/P6.md`](gates/P6.md), supported by the command-level evidence assessment in
[`gates/P6-pending.md`](gates/P6-pending.md). Acceptance is not owned by an individual unit.

1. **`Playnite.Cli` can import, list, search and launch (dry-run) without the UI.**
   `p6-011` owns `list`, `search` and `launch --dry-run` (resolve a game and print the planned
   launch without starting a process) over the `p6-001` library service; `p6-002` owns `import`.
   Evidence is the CLI exit codes and output on a fixture library with no UI assembly loaded. The
   existing `import-v10` path is the starting evidence for `p6-002`. `p6-001` itself delivers the
   library service only and does not edit `src/Playnite.Cli`.
2. **Abstractions build; API compatibility report reviewed; sample plugins compile and load.**
   `p6-008` builds `Playnite.SDK.Abstractions` with the ApiCompat task enabled and produces the
   complete v6→v7 report for review; it must not overwrite the shipped baseline to silence a
   break. `p6-009` compiles the Hello World, library-provider and metadata-provider samples
   against Abstractions plus the UI preview and loads them in a fake host. UI extension points
   stay unfrozen until the Phase 7 shell pilot (ADR-023).
3. **No Core project references Avalonia, directly or transitively.**
   Enforced on every unit by `tests/Playnite.Architecture.Tests`
   (`dotnet test --project tests/Playnite.Architecture.Tests -c Release`), which also owns the
   banned-API and project-graph gates. `p6-008` cannot pass if this is red.
4. **Contract tests for every SDK interface with a fake host.**
   `p6-006` and `p6-007` add fake-host contract tests for the host-facing interfaces they
   introduce; `p6-008` must show the interface coverage list is complete before the freeze;
   `p6-009` uses the fake host to load the samples. This is also the anti-stub check: an
   interface with no fake-host test is not delivered.

### Gate evidence and acceptance state, 2026-11-21

This table summarizes the accepted phase-level result. The detailed evidence and residuals remain
in the gate artifacts; this board is not a replacement for them.

| Gate item | Owner units | Evidence today | State |
| --- | --- | --- | --- |
| 1. CLI import/list/search/launch dry-run without the UI | `p6-002`, `p6-011`, `p6-012` | Fixture CLI commands and no-UI graph evidence; #220 adds a child-process positive `started:false` plan with a sentinel non-execution assertion | **Accepted: PROVEN** |
| 2. Abstractions build, reviewed ApiCompat report, samples compile and load | `p6-008`, `p6-009` | v7 freeze and classified ApiCompat report; PublicAPI no-diff under RS0016/RS0017; sample compile/load tests | **Accepted: PROVEN** |
| 3. No Core project references Avalonia, directly or transitively | every unit; `p6-008` cannot pass while red | Phase-level Architecture suite and evaluated project/package graph | **Accepted: PROVEN** |
| 4. Contract tests for every SDK interface with a fake host | `p6-006`, `p6-007`, `p6-008`, `p6-009` | 13-interface coverage inventory, reflection completeness test, and executing fake-host contracts | **Accepted: PROVEN** |

The captain accepted Phase 6 on 2026-11-21 with **4 PROVEN / 0 PARTIAL / 0 DEFERRED**. See
[`gates/P6.md`](gates/P6.md) for the decision and residuals, and [`gates/P6-pending.md`](gates/P6-pending.md)
for the detailed evidence. `p6-010` is Phase 6 scope but not an exit-gate item.

## Board correction, 2026-11-20

Two corrections from the delivered `p6-001` work and the Phase 7 requirement:

- **`p6-001` is the library service only.** PR
  https://github.com/seanburkes/playnite-linux/pull/161 delivers
  `Playnite.Core.Library.LibraryService` (a headless read facade) and does not edit
  `src/Playnite.Cli`. The board's earlier claim that `p6-001` owns `list`, `search` and
  `launch --dry-run` was wrong and is removed above.
- **`p6-011` CLI query surface** now owns `src/Playnite.Cli/Program.cs` plus the CLI usage and
  docs, depends on `p6-001`, and delivers `list`, `search` and `launch --dry-run` for exit-gate
  item 1.
- **`p6-012` repository-level ordered paging for list/search** owns
  `src/Playnite.Core/Database/GameRepository.cs`, depends on `p6-001` and `p6-011`, and is
  required before the Phase 7 UI: `GameRepository.GetAllAsync` currently reads every row, so the
  grid must not page a fully materialised library.

Neither new row creates its queue shard here. The one-unit guard forbids a `p6-shards` pull
request from touching another unit's evidence namespace, so each new unit's own PR creates
`docs/port/units/<id>.queue.tsv` from the exact row above and carries its own fragment.

File ownership for both new units is attributed by the generated map, not by this PR.
`docs/port/units/ownership.paths` is bot-owned: `docs/port/units/README.md` forbids a pull request
from adding or editing it, and both `check_unit_ownership.py` and `check_unit_evidence.py` reject
any such change through their `bot_owned_errors` gate. The guard also reads the map from the pull
request's base commit, never from its head, so a seed row could not take effect even if one were
added. Rows appear only after a unit's commits merge and the post-merge `port-index` workflow
regenerates the map on `main`; firstmate confirms that attribution afterwards.

The base map currently attributes `src/Playnite.Cli/Program.cs` to `p2-001`, `p2-004` and
`p4-002`, and `src/Playnite.Core/Database/GameRepository.cs` to `p4-001`. A `p6-011` or `p6-012`
PR that changes those paths is checked against that base map before its own rows exist, so
pre-merge attribution would need a deliberate, captain-approved change to the ownership guard or
generation contract, not a hand-seeded map row. This board does not attempt one.

### Remaining rows, 2026-11-20

The same one-unit rule applies to the four unstarted rows. This revision finalises their planned
rows in place but does not create `docs/port/units/p6-007.queue.tsv`–`p6-010.queue.tsv` or their
fragments; each unit's own PR does that. It also does not rewrite any delivered unit's row. The
foreign-owned path lists are read from the base `ownership.paths` map, which the guard uses, so a
unit that changes one of those paths fails before its own map row could exist. Where a unit only
needs new behaviour, it adds new files; `p6-008` is the deliberate exception because the
Abstractions freeze cannot be delivered without editing the protected declaration files, so its
dispatch must carry the `oracle-change` label.

## Stale records this board supersedes

- `docs/port/PORT-STATUS.md` says the v7 freeze is "scheduled as `p6-004`". It is `p6-008` under
  this board. `PORT-STATUS.md` is generated on `main`, so this PR does not edit it; the correction
  belongs to a process follow-up or the post-merge regeneration, not to a unit PR.
- The seeded `p6-003` ("Settings/metadata services") and `p6-004` ("SDK v7 contracts") scopes were
  replaced by their own unit PRs: `p6-003` is now Steam sync and `p6-004` is the settings service.
  `p6-003` code merged at `3ee1379` (PR https://github.com/seanburkes/playnite-linux/pull/180) but
  its queue row is still `in-progress` and its fragment carries only the open row. The board
  therefore shows the delivered sync service as open; that close-out belongs to the unit's own
  PR, not to this board, and this PR does not rewrite another unit's row.

## Validation

```sh
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
python3 scripts/port/build_port_index.py check --shards-only --against origin/main --forbid-aggregate-changes
python3 scripts/port/check_unit_ownership.py --base origin/main --head HEAD --unit p6-shards --branch fm/p6-shards4
```

No product code, test, protected oracle, `tests/baseline.json`, generated aggregate or other
unit's evidence namespace is changed by this board.

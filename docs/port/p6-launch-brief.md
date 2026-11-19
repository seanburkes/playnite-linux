# Phase 6 launch brief — core services and SDK v7

- Date: 2026-11-19
- Board unit: `p6-shards`
- Base: `14406866743705c478785a827dc442768bf8f460` (`main`)
- Plan: [`03-EXECUTION-PLAN.md`](03-EXECUTION-PLAN.md) Phase 6
- Architecture: [`02-ARCHITECTURE.md`](02-ARCHITECTURE.md) §3–§6; [`04-VERIFICATION.md`](04-VERIFICATION.md)
- Scope: the Phase 6 board and dependency plan only. This is not a gate acceptance and changes no
  product code, test, protected oracle, baseline or generated aggregate.

## Constraint: one unit namespace per pull request

The unit-ownership and unit-evidence guards allow a pull request to touch exactly one unit's queue
shard, evidence namespace and mapped paths. This board PR therefore adds only `p6-shards`. The
ten rows below are the board's planned canonical rows: each unit's own PR creates its
`docs/port/units/<id>.queue.tsv` (or replaces its existing seeded row) with that row and records
the transition in its own ledger fragment. Adding ten queue shards here would fail both guards,
and rewriting a seeded row for another unit is forbidden by the same rule.

## Planned queue rows, in plan order

Exact queue rows, copied verbatim so a dispatch brief can drop one into its unit shard:

```tsv
id	wave	area	unit	depends	status	owner	oracle	test	notes
p6-001	W6	core	Library service	p3-006,p4-001	todo	core	golden:library	test:library	First Phase 6 unit. Read/write facade over the SQLite repositories and query comparers; owns the CLI list, search and launch --dry-run commands (no UI). Exit-gate item 1.
p6-002	W6	core	Import service	p6-001,p4-003	todo	core	golden:import	test:import	Wraps the verified LibraryMigration path as a service; keeps the existing import-v10 evidence and adds the CLI import surface. Exit-gate item 1.
p6-003	W6	core	Steam sync	p6-002,p5-002	todo	core	golden:steam-sync	test:steam-sync	Syncs discovered Steam apps and ACF metadata into the library; no launch. Depends on the import service and merged Steam discovery (p5-002).
p6-004	W6	core	Settings service	p6-001,p4-004	todo	core	golden:settings	test:settings	Load, validate and write settings over the p4-004 migration output, preserving unknown properties and the legacy bag.
p6-005	W6	core	Metadata service	p6-001,p5-003	todo	core	golden:metadata	test:metadata	Steam app-details fetch with cache and rate limit, degrading to name/artwork; provider seam for other sources later. No WebView (ADR-019).
p6-006	W6	sdk	Notifications, dialogs and main-view API	p6-001,p5-007a	todo	sdk	contract:fake-host	test:sdk-host	INotificationsAPI, IDialogsFactory and IMainViewAPI over the p5-007a notification service and the platform seam. Every interface gets a fake-host contract test. Exit-gate item 4.
p6-007	W6	sdk	URI handler and HTML description service	p5-005a,p6-005	todo	sdk	contract:fake-host	test:uri-html	IUriHandlerAPI and playnite:// dispatch over p5-005a, plus the Avalonia.HtmlRenderer description path. No WebView (ADR-019). Exit-gate item 4.
p6-008	W6	sdk	SDK Abstractions freeze and ApiCompat report	p6-001,p6-002,p6-003,p6-004,p6-005,p6-006,p6-007	todo	sdk	api-compat	test:apicompat	Full v6-to-v7 Abstractions surface and a reviewed Microsoft.DotNet.ApiCompat breaking-change report; UI extension points stay unfrozen until the Phase 7 pilot (ADR-023). Exit-gate items 2 and 4.
p6-009	W6	sdk	Sample plugins (Hello World, library, metadata)	p6-008	todo	sdk	compile	test:plugins	Three sample plugins compiled against Abstractions and the UI preview and loaded by a fake host. Compile and load evidence only; no store. Exit-gate item 2.
p6-010	W6	sdk	Add-on manifest and installation model	p6-009,p3-005	todo	sdk	golden:manifests	test:addon-model	Manifest validation plus the install/uninstall directory model only; the add-on store is v1.1 (Phase 10).
```

## Boundaries: ten units, not the five seeded rows

The five `p6-001`–`p6-005` rows were generated at scaffolding time (`f886749`, PR #9) from a
coarse plan summary. Phase 6 now names ten discrete deliverables, and bundling them would exceed
the 200–800 changed-line / one-public-surface unit rule and mix service, SDK-contract and
packaging evidence. No unit is merged; the splits are forced by the plan's own order.

| Seeded row | Plan units | Why it changed |
| --- | --- | --- |
| `p6-001` Library service | `p6-001` Library service | Scope stands; dependencies are made explicit (`p3-006`, `p4-001`) and the CLI list/search/launch dry-run surface is attached. |
| `p6-002` Import/sync service | `p6-002` Import service, `p6-003` Steam sync | Import is the verified v10 migration path; Steam sync is a different input (discovery) and depends on import, so it cannot share one reviewable unit. |
| `p6-003` Settings/metadata services | `p6-004` Settings service, `p6-005` Metadata service | Two independent service surfaces with different oracles and failure modes; the plan names them separately. |
| `p6-004` SDK v7 contracts | `p6-006` Notifications/dialogs/main-view API, `p6-007` URI handler/HTML description, `p6-008` Abstractions freeze and ApiCompat report, `p6-010` Add-on manifest/installation model | The plan separates host-facing SDK interfaces (which need implementations and fake-host contract tests), the Abstractions freeze (which must follow every service unit), and the add-on model (which depends on the sample plugins). The freeze is now `p6-008`, not `p6-004`. |
| `p6-005` Sample plugins | `p6-009` Sample plugins | Scope stands; it now follows the Abstractions freeze (`p6-008`). |

`p6-006`–`p6-010` are genuinely new ids; they are not renames. The existing five rows keep their
ids and are redefined in place by their own unit PRs, so no id is retired and no unit is renamed.

## Units, dependencies and starting point

| Unit | Depends | Already in the tree | Exit-gate |
| --- | --- | --- | --- |
| `p6-001` Library service | `p3-006`, `p4-001` | SQLite repositories and schema (`src/Playnite.Core/Database`), filter/search/comparer leaves (`src/Playnite.Core/Queries`) | CLI `list`/`search`/`launch --dry-run` (item 1) |
| `p6-002` Import service | `p6-001`, `p4-003` | `LibraryMigration`, `AssetCopy`, CLI `import-v10` (p4-002) | CLI `import` (item 1) |
| `p6-003` Steam sync | `p6-002`, `p5-002` | Steam discovery, root resolver, library/manifest parsers (`src/Playnite.Steam`) | Sync evidence only |
| `p6-004` Settings service | `p6-001`, `p4-004` | `SettingsMigration`, `V10SettingsProperties` | Settings golden |
| `p6-005` Metadata service | `p6-001`, `p5-003` | Steam artwork cache/fetcher; no app-details service | Metadata golden |
| `p6-006` Notifications, dialogs and main-view API | `p6-001`, `p5-007a` | Platform notification service (`p5-007a`); SDK surface rows `sdk-002`, `sdk-003`, `sdk-004` are still `planned` | Fake-host contract tests (item 4) |
| `p6-007` URI handler and HTML description service | `p5-005a`, `p6-005` | `PlayniteProtocolHandler`, `PlayniteUri` (`p5-005a`) | Fake-host contract tests (item 4) |
| `p6-008` SDK Abstractions freeze and ApiCompat report | `p6-001`–`p6-007` | Seven-type preview baseline, `PublicAPI.Shipped.txt`, ApiCompat task already wired | Abstractions build + reviewed ApiCompat report (item 2) |
| `p6-009` Sample plugins | `p6-008` | None | Samples compile and load (item 2) |
| `p6-010` Add-on manifest and installation model | `p6-009`, `p3-005` | Manifest parsing/model (`AddonManifestBase`, `ExtensionManifest`, `ManifestSerialization`) | Manifest golden + install/uninstall model; no store |

Dependency notes:

- `p6-001` is first and has no Phase 6 prerequisite.
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
| 6b | `p6-002`, `p6-004`, `p6-005` | Independent after `p6-001`; can run in parallel. |
| 6c | `p6-003` | After `p6-002`; reuses `p5-002` discovery. |
| 6d | `p6-006`, `p6-007` | After their service dependencies; both close the fake-host contract surface. |
| 6e | `p6-008` | After `p6-001`–`p6-007`; freezes Abstractions only. |
| 6f | `p6-009`, then `p6-010` | Samples compile and load, then the add-on model. |

## Phase 6 exit gate

The gate is a phase-level result; no single unit may claim it. A `docs/port/gates/P6.md`
artifact is required at exit (plan §5) and does not exist yet.

1. **`Playnite.Cli` can import, list, search and launch (dry-run) without the UI.**
   `p6-001` owns `list`, `search` and `launch --dry-run` (resolve a game and print the planned
   launch without starting a process); `p6-002` owns `import`. Evidence is the CLI exit codes and
   output on a fixture library with no UI assembly loaded. The existing `import-v10` path is the
   starting evidence for `p6-002`.
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

## Stale records this board supersedes

- `docs/port/PORT-STATUS.md` says the v7 freeze is "scheduled as `p6-004`". It is `p6-008` under
  this board. `PORT-STATUS.md` is generated on `main`, so this PR does not edit it; the correction
  belongs to a process follow-up or the post-merge regeneration, not to a unit PR.
- The seeded `p6-003` ("Settings/metadata services") and `p6-004` ("SDK v7 contracts") rows are
  redefined by their own unit PRs as described above. Until those PRs land, the aggregate queue
  still shows the old scopes.

## Validation

```sh
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
python3 scripts/port/build_port_index.py check --shards-only --against origin/main --forbid-aggregate-changes
```

No product code, test, protected oracle, `tests/baseline.json` or generated aggregate is changed
by this board.

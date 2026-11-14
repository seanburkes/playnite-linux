# Execution plan

This plan applies the [Language Port Doctrine](../../../brain/software-engineering/Language%20Port%20Doctrine.md)
to a **framework/platform port**, not a language port. The invariant still holds: the cost and
risk are set by the quality of an oracle that outlives the old runtime. The old runtime is
`net462` + WPF + Win32; the new one is `net11.0` + Avalonia + Linux. They cannot share a
process, so the oracle is **process/data-level**, and the strategy is a **staged strangler**:
build the new app beside the frozen one, port leaves inward, and cut over per surface.

## 0. Strategy selector

| Variable | This project | Consequence |
| --- | --- | --- |
| Coupling | High in UI/core, low at data/Steam boundaries | Port leaves first; rewrite the UI shell |
| Liveness | Upstream v10 is frozen for us; no incoming changes | No merge-race guard needed; `legacy/` is immutable |
| Oracle | Thin unit suite; strong data/process boundaries | Build goldens before porting |
| Coexistence | Old/new cannot share a process | Differential tests are process-level |
| Shape | **In-place by project, staged cutover** | New repo, new projects, old app untouched |

Why not big-bang: the UI and platform surface is too large to verify in one merge, and the
oracle is too thin. Why not per-file in-place: `net462`/`net11` cannot coexist. Why not
parallel A/B in one process: impossible; process-level dual-run is the substitute.

## 1. Phase gates

Each phase ends in a finite, tool-generated work queue. Do not start the next phase until the
gate is green and the evidence is checked in.

### Phase 0 — Decisions and inventory (current)

**Goal:** lock global decisions and make the inventory reproducible.
**Deliverables:** ADRs, inventory, architecture, verification design, dependency matrix,
operating model, formal model drafts.
**Exit gate:**
- [x] ADRs 001–016 recorded.
- [x] Inventory reproducible by `scripts/port/scan_legacy.py`.
- [x] Target project graph and dependency rules.
- [x] Oracle and CI gate design.
- [x] v1 scope explicit.
- [ ] Captain review and Phase 1 authorisation.
- [ ] GitHub repository created and first commit pushed (after review).

### Phase 1 — Freeze the oracle

**Goal:** make "is this behaviour identical?" answerable before touching code.
**Deliverables:**
- Windows CI workflow that builds `legacy/` with .NET Framework MSBuild and runs the NUnit
  suite.
- `Playnite.LegacyOracle` harness (runs on Windows, references legacy assemblies or drives the
  app) that emits deterministic JSON/bytes for:
  - settings round-trip;
  - manifest/extension/theme parsing;
  - library export for a fixture library;
  - archive/backup behaviour;
  - HTTP requests to a local stub (headers, method, body) and response handling;
  - log lines at a fixed level with volatile tokens redacted.
- `tests/fixtures/` synthetic v10 library and Steam roots.
- `tests/golden/` checked-in outputs with a manifest and regeneration procedure.
- A short list of UI screenshots (main library, details, settings) captured on Windows.
- Test/assertion count baseline (`tests/baseline.json`).
**Exit gate:**
- Old app builds and tests run on Windows CI.
- Goldens regenerate reproducibly on a clean runner.
- A deliberate one-line change in the oracle harness fails the comparison.
- Oracle paths are protected by CODEOWNERS/labels.

**Spikes (time-boxed, in parallel):**
1. LiteDB 4.1.4 read on `net11.0` (isolated console). If it fails, build the exporter on
   Windows instead.
2. WebKitGTK/WPE offscreen page load and text extraction on Ubuntu, Fedora, Arch/SteamOS.
3. Steam process-tree detection for native and Proton games.
4. Avalonia 12 headless screenshot + accessibility tree on Linux.

### Phase 2 — Scaffolding

**Goal:** the new stack builds, tests, and ships a trivial app on Linux.
**Deliverables:**
- Solution and projects from [`02-ARCHITECTURE.md`](02-ARCHITECTURE.md).
- `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`.
- `BannedApiAnalyzers` + `BannedSymbols.txt`; `NetArchTest`/`ArchUnitNET` rules.
- xUnit v3 (or NUnit 4) test projects; `Microsoft.NET.Test.Sdk`; coverage.
- CI: Linux build/test, Windows oracle, architecture, dependency audit, packaging smoke.
- A trivial Avalonia window that opens headlessly and in Xvfb; a CLI that prints version.
- First performance baseline: startup, working set, library open time (empty fixture).
**Exit gate:**
- `dotnet build` and `dotnet test` green on Linux x64.
- Architecture test fails if Core references Avalonia or a banned API.
- Windows oracle job green.
- AppImage and Flatpak smoke artifacts produced (can be empty shells).

### Phase 3 — Leaves (pure logic)

**Goal:** port pure, well-tested code before anything stateful.
**Order:**
1. Paths, string/date/enum extensions, INI/JSON/YAML/TOML helpers, CRC, archive helpers.
2. Models and equality/hash semantics.
3. Serialization compatibility (Newtonsoft settings, property names, null/empty behaviour).
4. Search/filter/sort comparers.
5. Localisation key extraction and culture handling.
6. Manifest parsing (extension/theme/add-on).
**Pilots:** take 3 units through the entire path — code, unit test, golden, review, CI,
ledger. One of them must be a serialization round-trip; one a parser with a fuzz target.
**Exit gate:**
- Every ported unit has a v10-derived test or golden.
- Test count and assertion count ≥ baseline.
- No `legacy/` changes.
- Fuzz targets run for a bounded time in CI.

### Phase 4 — Data, settings, and migration

**Goal:** read a real v10 library and produce a verified SQLite library.
**Order:**
1. SQLite schema + repositories + migrations.
2. `Playnite.LegacyImport`: read LiteDB 4, transform, write temp DB, verify, atomic swap.
3. Settings migration and unknown-property preservation.
4. Asset copy by hash; image dimensions; missing-file handling.
5. Migration report and dry-run mode.
6. Formal model `LibraryImport.tla` checked with TLC; implementation tests cite invariants.
**Exit gate:**
- A fixture v10 library migrates with 100% of expected Steam rows, 0 silent drops.
- Crash-injection tests at every migration state leave a resumable or clean state.
- The source directory is byte-identical after migration.
- A second run is idempotent or refuses with a clear message.
- Settings round-trip golden is byte-identical for stable fields.

### Phase 5 — Steam and Linux platform

**Goal:** discover and launch a real Steam library on Linux.
**Order:**
1. VDF/ACF parsers + fixtures (pure; fuzz target).
2. Steam root/library discovery, including Flatpak paths.
3. Artwork and metadata fetch with a local HTTP stub; cache and rate limit.
4. `IProcessLauncher`, `IGameSessionTracker`, single instance, XDG paths, protocol handler.
5. Launch native and Proton games; track start/stop; manual stop fallback.
6. Formal model `GameSession.tla`; cancellation-at-every-await tests.
**Exit gate:**
- Synthetic Steam fixture yields the expected app set, paths, and artwork ids.
- A real Steam install on a test machine launches and stops a native and a Proton game.
- No orphaned processes after cancel/teardown.
- Session state matches the formal model's terminal states.
- Flatpak and AppImage can read a host Steam library (documented permission).

### Phase 6 — Core services and SDK v7

**Goal:** the headless app can do everything the UI needs, and a sample plugin compiles.
**Order:**
1. Library service, import service, Steam sync, settings service, metadata service.
2. Notifications, dialogs, main-view API, URI handler, web view service.
3. SDK v7 surface: port contracts, Avalonia UI points, `PublicAPI.Shipped.txt`.
4. API compatibility report v6→v7; sample plugin (`Hello World`, library provider, metadata
   provider) compiled against v7.
5. Add-on manifest/installation model only (store is v1.1).
**Exit gate:**
- `Playnite.Cli` can import, list, search, and launch (dry-run) without the UI.
- SDK builds; API compatibility report reviewed; sample plugins compile and load.
- No Core project references Avalonia.
- Contract tests for every SDK interface with a fake host.

### Phase 7 — Avalonia desktop shell

**Goal:** a usable desktop app over the headless core.
**Order (one direction, bottom-up):**
1. App bootstrap, DI, logging, single instance, theme loading, localisation.
2. Shell: main window, sidebar, top panel, content host, notifications.
3. Library grid/list, details view, search, filters, sort, grouping.
4. Game edit and metadata display; image picker.
5. Settings (essential sections first), first-run/import wizard, progress/crash dialogs.
6. Tray, protocol activation, keyboard shortcuts, gamepad navigation (desktop).
7. Headless screenshot and accessibility baselines for each key screen.
**Pilots:** `About`/`MessageBox` and `Library grid` through theme, headless test, packaging.
**Exit gate:**
- All v1 flows work on Linux x64 and ARM64.
- UI test count and assertion count non-decreasing; no skipped UI tests.
- Screenshot baselines pass on X11 and Wayland (where applicable).
- Accessibility smoke: every interactive control has a name/role.
- No WPF types in the dependency graph.

### Phase 8 — Packaging, performance, hardening

**Goal:** installable, observable, and boring.
**Deliverables:**
- AppImage and Flatpak manifests; x64 + ARM64 builds; desktop entry; icons; MIME handler.
- Startup, library-open, image-decode, and memory benchmarks with tolerance gates.
- Soak test: import + browse + launch loop N times; RSS/handles flatten.
- Fuzz corpus for VDF/ACF, JSON settings, YAML manifests, archive entries.
- Diagnostics bundle; log redaction; crash handler.
- Security review: no shell interpolation, path traversal, zip-slip, unsafe deserialization.
**Exit gate:**
- Fresh install on Ubuntu 24.04/26.04, Fedora 44/45, Arch/SteamOS, Debian 13.
- ARM64 smoke on real or emulated hardware.
- No P0/P1 security findings.
- Benchmarks within tolerance; no leak in the soak test.

### Phase 9 — v1 release and cutover

**Goal:** ship.
**Deliverables:**
- Release notes, known limitations, migration guide, plugin SDK migration guide.
- Signed artifacts, checksums, SBOM, provenance.
- A rollback story: keep the v10 library copy; the app never writes to it.
**Exit gate:**
- Release checklist complete.
- 7-day pre-release soak with telemetry-free crash reports from testers.
- No open P0/P1 bugs.

### Phase 10 — Fast-follows (not v1)

- Fullscreen/Steam Deck shell.
- Heroic/Lutris/itch providers; emulation.
- Add-on store and metadata plugins.
- Self-updater, Flatpak remote.
- Visual parity and theme ecosystem.
- Redesign now that the port is stable (per doctrine: translation first, redesign second).

## 2. Work breakdown and parallelisation

After Phase 2, four tracks can run in parallel **only after the core interfaces exist**:

| Track | Owns | Depends on | Can start |
| --- | --- | --- | --- |
| A. Core/data | models, serialization, SQLite, import | scaffolding | Phase 3 |
| B. Steam | VDF/ACF, discovery, metadata, launch | models/interfaces | Phase 3 (parsers), Phase 5 (launch) |
| C. Platform | XDG, processes, webview, tray, notifications | interfaces | Phase 3 |
| D. UI | shell, views, theme, accessibility | core interfaces + platform fakes | Phase 2 spike, Phase 7 integration |
| E. Verification | oracle, goldens, fuzz, formal, CI | Phase 1 | Phase 1 |
| F. Packaging | AppImage/Flatpak, CI matrix, signing | shell | Phase 8 |

**One direction of partition:** bottom-up. UI spikes are allowed to validate Avalonia, but no
UI-driven change may reshape Core after Phase 4 without an ADR. Do not port top-down and
bottom-up at the same time; the two fronts meet in `MainWindow`/`PlayniteApplication`.

**Unit size:** one public surface or 200–800 changed lines, whichever is smaller. A unit that
cannot be reviewed in one sitting is two units.

**Wave structure:**
```
W0 scaffolding + oracle
W1 leaves (models, utils, serialization, parsers)
W2 data + migration + settings
W3 steam discovery + platform services
W4 core services + SDK
W5 UI shell + library + settings
W6 packaging + hardening
W7 release
```

## 3. Estimates

These are **priors**, not measurements. Re-estimate after Phase 1 pilots using observed
per-unit pace and the real oracle strength.

| Phase | Engineer-weeks (agent-assisted) | Confidence |
| --- | ---: | --- |
| 0 Decisions/inventory | 1–2 | High (mostly done) |
| 1 Oracle | 2–4 | Medium |
| 2 Scaffolding | 1–2 | High |
| 3 Leaves | 3–5 | Medium |
| 4 Data/migration | 4–6 | Medium |
| 5 Steam/platform | 5–8 | Low |
| 6 Core/SDK | 4–6 | Medium |
| 7 UI shell | 10–16 | Low |
| 8 Packaging/hardening | 4–6 | Medium |
| 9 Release | 2–3 | Medium |
| **Total** | **36–58 engineer-weeks** | Low |

With 3–4 parallel crewmates and agents, the calendar range is roughly **16–28 weeks** for a
narrow v1, with the UI as the critical path. A single engineer without a fleet should plan for
**9–12 months**. Token cost is even less predictable; use **$30k–$90k** as a planning band and
replace it with measured spend after Phase 1.

What would compress the estimate:
- Reusing the v10 ViewModels and a functional (not pixel-perfect) shell.
- Cutting v1 further (e.g., no settings UI, CLI-only import) — not recommended.
- Buying/porting a mature Avalonia theme instead of hand-building one.
- A strong golden corpus that lets agents work without re-deriving behaviour.

What would expand it:
- Plugin compatibility or add-on store pulled into v1.
- Fullscreen/Steam Deck in v1.
- WebKitGTK/WPE instability across distros.
- Steam process tracking turning into a reverse-engineering project.
- A thin oracle forcing manual comparison for every unit.

## 4. First ten working days

| Day | Work | Evidence |
| --- | --- | --- |
| 1 | Captain review of this plan; create GitHub repo; first commit; firstmate project registration | repo URL, first commit |
| 2 | Scaffolding: solution, projects, props, analyzers, `global.json` | `dotnet build` green |
| 3 | CI: Linux build/test + architecture; Windows oracle skeleton | workflows green |
| 4 | Oracle harness v0: settings + manifest golden | `tests/golden/` diffable |
| 5 | Fixture v10 library + synthetic Steam root | fixtures committed |
| 6 | Pilot A: serialization unit through full path | ledger row + golden |
| 7 | Pilot B: ACF parser through full path + fuzz target | ledger row + fuzz run |
| 8 | Pilot C: Avalonia `About` window headless screenshot | baseline PNG + a11y tree |
| 9 | LiteDB import spike; WebKitGTK/WPE spike; Steam process spike | spike reports |
| 10 | Re-estimate; review pilots; decide Phase 3 bulk order | updated plan + queue |

## 5. Gate evidence format

Every gate produces a checked-in artifact:

```
docs/port/gates/P<phase>-<gate>.md
  - what was checked
  - command(s) run
  - result (counts, pass/fail)
  - artifact paths (goldens, screenshots, reports)
  - reviewer(s)
  - open exceptions with owner and expiry
```

A gate without an artifact is not a gate. A green CI run is evidence only if the workflow is
pinned and the artifact is retained.

## 6. Change control

- ADR required for: target framework, UI framework, storage, plugin compatibility, supported
  architectures, packaging, serializer, any new global dependency.
- Plan update required for: phase scope change, gate waiver, estimate change > 30%.
- Oracle change requires a separate PR, a written reason, and a second reviewer; the porting
  agent cannot approve its own oracle change.
- A repeated failure mode (twice) becomes a standing instruction, analyzer, template, or
  eval — never a manual fix on unit #N.

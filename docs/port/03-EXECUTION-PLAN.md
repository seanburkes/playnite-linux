# Execution plan

This plan applies the Language Port Doctrine at
`~/src/brain/software-engineering/Language Port Doctrine.md` to a **framework/platform port**, not a language port. The invariant still holds: the cost and
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
- [x] ADRs 001–023 recorded.
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
- `Playnite.LegacyOracle` harness (runs on Windows) constrained to **existing v10 public APIs**
  and raw bytes. It may call the old assemblies and drive the old app; it may not re-serialise
  into a new format or normalise output. It emits:
  - settings bytes;
  - manifest/extension/theme bytes;
  - library bytes/exports for a fixture library;
  - archive/backup bytes;
  - HTTP request/response bytes against a local stub;
  - log lines at a fixed level with volatile tokens redacted.
- `tests/fixtures/`: synthetic v10 library, synthetic Steam roots, and a **redacted real Steam
  artifact corpus** (`libraryfolders.vdf`, `appmanifest_*.acf`, `shortcuts.vdf`,
  `librarycache` images, captured app-details JSON).
- `tests/golden/` checked-in outputs with a manifest and a human-run regeneration procedure.
- UI reference index of official/public screenshots and videos (main library grid/list/details,
  settings, first-run/import, fullscreen) in [`ui-reference/SOURCES.md`](ui-reference/SOURCES.md),
  as **human comparison only**, not pixel gates. A local Windows capture is optional and not a
  Phase 1 gate.
- Executed-test-case baseline from a JUnit result file (`tests/baseline.json`), not a regex.
**Exit gate:**
- Old app builds and tests run on Windows CI; executed cases are recorded.
- Goldens regenerate reproducibly on a clean runner with a checksum-pinned `Playnite.Deps`.
- A deliberate one-byte change in the oracle bytes fails the comparison.
- All protected oracle paths are covered by CODEOWNERS and CI: `legacy/`, `tests/golden/`,
  `tests/fixtures/`, `tests/baseline.json`, `docs/formal/`, `oracle-lock/`.
- The Steam fixture corpus exists and a written Steam spec is reviewed.
- UI references are curated official/public links (p1-004, reference-only); a local capture
  is optional, and no screenshot comparison is ever a test gate.

**Spikes (time-boxed, in parallel):**
1. LiteDB 4.1.4 read on `net11.0` (isolated console). If it fails, the importer is a Windows
   executable and the Linux app consumes its neutral output.
2. Steam process-tree detection for native and Proton games, including inside Flatpak
   (`flatpak-spawn --host` and PID visibility).
3. Avalonia 12 headless render + accessibility tree on Linux (headless Skia; X11/Wayland only
   for app launch smoke). Outcome: a real window opens on both stacks, X11/XWayland exposes
   AT-SPI and Wayland does not; accessibility is an accepted v1 limitation (Deferred
   limitations D1 in [`00-DECISIONS.md`](00-DECISIONS.md)).
4. WebView feasibility (WebKitGTK/WPE) — a bounded study for v1.1, not a v1 gate.
5. Flatpak host launch/tracking: can `flatpak-spawn --host` launch Steam and observe the game
   process without a host helper?

### Phase 2 — Scaffolding

**Goal:** the new stack builds, tests, and ships a trivial app on Linux.
**Deliverables:**
- Solution and projects from [`02-ARCHITECTURE.md`](02-ARCHITECTURE.md).
- `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`.
- `BannedApiAnalyzers` + `BannedSymbols.txt`; `NetArchTest`/`ArchUnitNET` rules.
- xUnit v3 (or NUnit 4) test projects; `Microsoft.NET.Test.Sdk`; coverage.
- CI: Linux build/test, Windows oracle, architecture, dependency audit, packaging smoke.
- A trivial Avalonia window that opens headlessly and in Xvfb; a CLI that prints version.
- **UI shell pilot begins here, in parallel:** library grid over fake data, using the real
  theme/resource pipeline and a headless screenshot. This is a spike, not integration.
- New-suite test baseline recorded (`tests/baseline.json`) at Phase 2 exit.
- First performance baseline: startup, working set, library open time (empty fixture).
**Exit gate:**
- `dotnet build` and `dotnet test` green on Linux x64.
- Architecture test fails if Core/SDK.Abstractions references Avalonia, directly or
  transitively, or a banned API.
- Windows oracle job green.
- AppImage and Flatpak smoke artifacts produced (can be empty shells).
- UI pilot has a headless screenshot and a semantic-tree baseline; screen-reader accessibility
  is deferred for v1 (Deferred limitations D1 in [`00-DECISIONS.md`](00-DECISIONS.md)).

### Phase 3 — Leaves (pure logic)

**Goal:** port pure, well-tested code before anything stateful.
**Order:**
1. Paths, string/date/enum extensions, INI/JSON/YAML/TOML helpers, CRC, archive helpers.
2. Models and equality/hash semantics.
3. Serialization compatibility (Newtonsoft settings, property names, null/empty behaviour).
4. Search/filter/sort comparers.
5. Localisation: key extraction, key-set diff, placeholder validation, culture handling
   (1,134 keys, 37 language files). This is a workstream, not a bullet.
6. Manifest parsing (extension/theme/add-on).
7. `Playnite.SDK.Abstractions`: models/non-UI contracts, `PublicAPI.Shipped.txt`, ApiCompat
   baseline. This freezes early because it is not UI-shaped.
**Pilots:** take 3 units through the entire path — code, unit test, golden, review, CI,
ledger. One of them must be a serialization round-trip; one a parser with a fuzz target.
**Exit gate:**
- Every ported unit has a v10-derived test or golden.
- New-suite executed-case count is non-decreasing from the Phase 2 baseline; skips are zero.
- No `legacy/` changes.
- Localisation key-set diff is empty for English; missing translations are reported.
- Fuzz targets run for a bounded time in CI.

### Phase 4 — Data, settings, and migration

**Goal:** read a real v10 library and produce a verified SQLite library.
**Order:**
1. SQLite schema + repositories + migrations.
2. `Playnite.LegacyImport` as a **separate executable**: read LiteDB 4, transform, write temp
   DB, verify, atomic swap. Desktop invokes it and never references LiteDB.
3. Settings migration: unknown-property preservation and Windows-path rewriting/`legacy` bag.
4. Asset copy by hash; image dimensions; missing-file handling.
5. Migration report and dry-run mode; transport paths (mounted Windows/USB, fresh install).
6. Formal model `LibraryImport.tla` checked with TLC; implementation tests cite invariants.
**Exit gate:**
- A fixture v10 library migrates with 100% of expected Steam rows, 0 silent drops.
- Crash-injection tests at every migration state leave a resumable or clean state.
- The source directory is byte-identical after migration.
- A second run is idempotent or refuses with a clear message.
- Settings round-trip golden is byte-identical for stable fields; unmapped Windows paths are
  reported, not silently dropped.
- Desktop has no reference to LiteDB or the importer assembly.

### Phase 5 — Steam and Linux platform

**Goal:** discover and launch a real Steam library on Linux. This is a greenfield subsystem,
not a port (ADR-017); the oracle is the recorded Steam corpus plus real-install acceptance.
**Order:**
1. Written Steam spec: roots, VDF/ACF grammar, Proton markers, launch, exit detection,
   failure modes.
2. VDF/ACF parsers + fixtures (pure; fuzz target).
3. Steam root/library discovery, including Flatpak paths.
4. Artwork and metadata fetch with a local HTTP stub; cache and rate limit.
5. `IProcessLauncher`, `IGameSessionTracker`, single instance, XDG paths, protocol handler.
6. Launch native and Proton games; track start/stop; manual stop fallback.
7. Formal model `GameSession.tla`; cancellation-at-every-await tests.
**Exit gate:**
- Synthetic + redacted real fixture yields the expected app set, paths, and artwork ids.
- A real Steam install on a test machine launches and stops a native and a Proton game.
- **Inside Flatpak**, `flatpak-spawn --host` launches Steam and session tracking observes the
  game process (with a host helper if PID visibility requires it).
- No orphaned processes after cancel/teardown.
- Session state matches the formal model's terminal states.
- AppImage and Flatpak can read a host Steam library with narrow permissions.

### Phase 6 — Core services and SDK v7

**Goal:** the headless app can do everything the UI needs, and a sample plugin compiles.
**Order:**
1. Library service, import service, Steam sync, settings service, metadata service.
2. Notifications, dialogs, main-view API, URI handler, HTML description service.
3. `Playnite.SDK.Abstractions` freeze and ApiCompat report (v6→v7).
4. UI extension points are drafted but **not frozen** until the shell pilot lands (ADR-023).
5. API compatibility report; sample plugin (`Hello World`, library provider, metadata
   provider) compiled against Abstractions/UI preview.
6. Add-on manifest/installation model only (store is v1.1).
**Exit gate:**
- `Playnite.Cli` can import, list, search, and launch (dry-run) without the UI.
- Abstractions build; API compatibility report reviewed; sample plugins compile and load.
- No Core project references Avalonia, directly or transitively.
- Contract tests for every SDK interface with a fake host.

### Phase 7 — Avalonia desktop shell

**Goal:** a usable desktop app over the headless core.
**Order (one direction, bottom-up):**
1. App bootstrap, DI, logging, single instance, theme loading, localisation.
2. Shell: main window, sidebar, top panel, content host, notifications.
3. Library grid/list, details view, search, filters, sort, grouping.
4. Game edit and metadata display; image picker.
5. Settings (essential sections first), first-run/import wizard, progress/crash dialogs.
6. Tray, protocol activation, keyboard shortcuts. Gamepad navigation is deferred.
7. Headless screenshot and semantic-tree baselines for each key screen; screen-reader
   accessibility is deferred for v1 (Deferred limitations D1 in
   [`00-DECISIONS.md`](00-DECISIONS.md)).
8. Freeze the SDK UI extension points after the shell pilot (ADR-023).
**Pilots:** `About`/`MessageBox` and `Library grid` through theme, headless test, packaging.
**Exit gate:**
- All v1 flows work on Linux x64 and ARM64.
- UI executed-case count non-decreasing; no skipped UI tests.
- Headless Skia screenshot baselines pass; X11/Wayland is an app-launch smoke, not a pixel gate.
- Semantic-tree smoke: key interactive controls have a name/role. This is not a v1
  screen-reader gate; accessibility is deferred (Deferred limitations D1 in
  [`00-DECISIONS.md`](00-DECISIONS.md)).
- No WPF types in the dependency graph.
- SDK UI surface frozen and documented.

### Phase 8 — Packaging, performance, hardening

**Goal:** installable, observable, and boring.
**Deliverables:**
- AppImage and Flatpak manifests; x64 + ARM64 builds; desktop entry; icons; MIME handler.
- Flatpak host-launch/tracking path (`flatpak-spawn --host`), narrow filesystem permissions,
  and a Flathub submission decision.
- Startup, library-open, image-decode, and memory benchmarks with tolerance gates.
- Soak test: import + browse + launch loop N times; RSS/handles flatten.
- Fuzz corpus for VDF/ACF, JSON settings, YAML manifests, archive entries.
- Diagnostics bundle; log redaction; crash handler.
- Security review: no shell interpolation, path traversal, zip-slip, unsafe deserialization.
**Exit gate:**
- Fresh install on Ubuntu 24.04/26.04, Fedora 44/45, Arch/SteamOS, Debian 13.
- Flatpak launches and tracks a native and a Proton game on a host Steam install.
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
- WebView (WebKitGTK/WPE), metadata plugins, JavaScript-rendered pages.
- Self-updater, Flatpak remote.
- Visual parity and theme ecosystem.
- Redesign now that the port is stable (per doctrine: translation first, redesign second).

## 2. Work breakdown and parallelisation

After Phase 2, four tracks can run in parallel **only after the core interfaces exist**:

| Track | Owns | Depends on | Can start |
| --- | --- | --- | --- |
| A. Core/data | models, serialization, SQLite, import | scaffolding | Phase 3 |
| B. Steam | VDF/ACF, discovery, metadata, launch | models/interfaces | Phase 3 (parsers), Phase 5 (launch) |
| C. Platform | XDG, processes, tray, notifications (WebView v1.1) | interfaces | Phase 3 |
| D. UI | shell, views, theme, accessibility | core interfaces + platform fakes | **Phase 2 pilot**, parallel track, Phase 7 integration |
| E. Verification | oracle, goldens, fuzz, formal, CI | Phase 1 | Phase 1 |
| F. Packaging | AppImage/Flatpak, CI matrix, signing | shell | Phase 8 |

**One direction of partition:** bottom-up. The UI runs as a **parallel spike track from Phase
2**, not as a serial phase after Core. It may consume fake Core interfaces; no UI-driven change
may reshape Core after Phase 4 without an ADR. Do not port top-down and bottom-up at the same
time; the two fronts meet in `MainWindow`/`PlayniteApplication`.

**Unit size:** one public surface or 200–800 changed lines, whichever is smaller. A unit that
cannot be reviewed in one sitting is two units.

**Wave structure:**
```
W0 scaffolding + oracle
W1 leaves (models, utils, serialization, parsers) + UI pilot
W2 data + migration + settings
W3 steam discovery + platform services
W4 core services + SDK abstractions
W5 UI shell + library + settings (parallel from W1)
W6 packaging + hardening + SDK UI freeze
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

With 3–4 parallel crewmates and agents, the calendar range is roughly **14–22 weeks** for a
narrow v1 **only if the UI pilot starts in Phase 2 and runs as a parallel track**. If the UI is
kept serial after Core, add **8–12 weeks** and treat the 16–28 week figure as the floor. A
single engineer without a fleet should plan for **9–12 months**. Token cost is even less
predictable; use **$30k–$90k** as a planning band and replace it with measured spend after
Phase 1.

These estimates do not include CI/Actions cost. Price the nightly matrix before authorising:
roughly 30 nightly runs/month × (6 distro smoke × 20 min + Windows oracle × 30 min + ARM64
smoke × 30 min + mutation/fuzz/soak × 60 min) ≈ **5,000–8,000 Actions minutes/month**, plus
per-PR runs. A public repo is free on GitHub-hosted runners today, but the wall-clock and
queueing cost is real; use a self-hosted Linux runner for the heavy nightly work if needed.

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
| 1 | Captain review; create GitHub repo; first commit; firstmate registration | repo URL, first commit |
| 2 | Windows oracle CI: build legacy + run NUnit, record executed cases | workflow green + JUnit |
| 3 | Oracle harness v0 constrained to v10 APIs: settings + manifest bytes | `tests/golden/` diffable |
| 4 | Fixture v10 library + synthetic/redacted Steam corpus | fixtures committed |
| 5 | Oracle gate check; one-byte mutation fails the comparator | `gates/P1-oracle.md` |
| 6 | Scaffolding: solution, projects, props, analyzers, `global.json` | `dotnet build` green |
| 7 | Linux CI + architecture tests; packaging smoke | workflows green |
| 8 | UI pilot: library grid over fake data + headless screenshot/a11y | baseline PNG + tree |
| 9 | Spikes: LiteDB, Steam process/Flatpak, Avalonia headless | spike reports |
| 10 | Re-estimate with pilot pace; decide Phase 3 bulk order | updated plan + queue |

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
- Oracle change requires a separate PR, a written reason, and the `oracle-change` label
  through the protected-path gate; the porting agent cannot approve its own oracle change.
- A repeated failure mode (twice) becomes a standing instruction, analyzer, template, or
  eval — never a manual fix on unit #N.

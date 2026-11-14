# Decisions

Format: context → decision → consequences → alternatives rejected. These are the global
decisions that per-unit work must not re-litigate. Changing one requires a new ADR and a
captain decision.

---

## ADR-001 — Linux-first; the Windows app is frozen as the oracle

**Context.** v10.64 targets `net462`, x86, WPF, WinForms, CefSharp, and Win32. WPF and
WinForms remain Windows-only in .NET 11. `.NET Framework` and `.NET 11` assemblies cannot be
loaded into the same process. The upstream project is rewriting v11 privately, so there is no
merge path to depend on.

**Decision.** Do not migrate the Windows app. Keep v10.64 in `legacy/` as a read-only,
buildable oracle. Build a new Linux application in this repository. Windows users stay on
upstream v10.64.

**Consequences.**
- The old and new implementations coexist as **processes**, not assemblies. Differential
  testing is process/data-level.
- `legacy/` is immutable; CI fails on any change.
- The port cannot use "delete the old file in the same commit" to force merge conflicts.
  Instead, the parity ledger and the frozen snapshot are the anti-omission mechanism.
- Windows-only features that cannot be exercised on Linux are either reimplemented behind a
  platform interface or explicitly out of v1 scope.

**Rejected.**
- *Move Windows to `net11.0-windows` WPF first, then Linux.* Isolates runtime risk but delays
  Linux and doubles UI work; upstream v10 already serves Windows.
- *Keep WPF via Wine.* Not supportable, not testable, and not a Linux application.

---

## ADR-002 — Target `net11.0` (STS), pin SDK 11.0.100

**Context.** .NET 11 shipped 2026-11-10 as a Standard Term Support release (supported to
2028-11-14). .NET 10 is LTS. The request is explicitly ".NET 11".

**Decision.** All new projects target `net11.0` and pin `11.0.100` in `global.json` with
`rollForward: latestPatch`. Use C# 15.

**Consequences.**
- We accept a shorter support window and a planned upgrade to the next LTS.
- Package versions must support `net11.0`; Avalonia 12 supports .NET 8+ and is validated on
  .NET 10/11.
- We do not multi-target unless a concrete consumer needs it. A second TFM doubles CI and
  conditional code for no v1 benefit.
- The upgrade to the next LTS is a scheduled maintenance task, not a port.

**Rejected.**
- *`net10.0` LTS.* Longer support but ignores the explicit request.
- *Multi-target `net10.0;net11.0`.* Premature; revisit only if a packaging target requires it.

---

## ADR-003 — Avalonia 12 with the official WebView

**Context.** 68k lines of XAML and 275 XAML files are WPF. Avalonia is the closest XAML model
with mature Linux support. Avalonia 12.1.6 is current; `Avalonia.Controls.WebView` 12.1.2 is
official and uses WebView2 (Windows), WKWebView (macOS), WebKitGTK/WPE (Linux).
`Avalonia.HtmlRenderer` 12.0.0 covers simple HTML descriptions without a browser process.

**Decision.** Build the desktop shell on Avalonia 12. Use `Avalonia.Controls.WebView` behind
an `IWebView` abstraction; WebKitGTK is the baseline, WPE is optional. Use
`Avalonia.HtmlRenderer` for non-interactive descriptions where it is sufficient.

**Consequences.**
- WPF controls, behaviours, converters, resource dictionaries, and themes need Avalonia
  equivalents. This is the largest single workstream.
- CefSharp is removed. Chromium-specific rendering quirks in metadata scraping must be
  re-tested against WebKit.
- `NativeWebView` on Linux prefers WPE when installed and falls back to WebKitGTK. Ubuntu does
  not package WPE, so WebKitGTK is the must-pass path; WPE is an optimisation.
- UI tests run headless with Avalonia.Headless + Skia.

**Rejected.**
- *Uno Platform.* Good Linux story, but WinUI XAML maps less directly to the existing WPF
  views and the team would still rewrite them.
- *Keep WPF on Windows, Avalonia on Linux.* Rejected by ADR-001; one UI, one behaviour.
- *Web/Blazor hybrid.* Too large a rewrite of desktop interactions, theming, and gamepad UI.

---

## ADR-004 — Standalone repository, `legacy/` snapshot included

**Context.** Upstream is private-rewriting v11. A fork would carry confusing history and no
merge path. The oracle must be available to CI.

**Decision.** New repository `playnite-linux`, MIT, public, owned by `seanburkes`. Import
v10.64 source as a single snapshot commit under `legacy/` (no upstream history). Keep the
upstream URL documented for provenance. No upstream PRs.

**Consequences.**
- CI can build the oracle and the new app from one checkout.
- `legacy/` is ~24 MB; acceptable.
- Attribution: keep `legacy/LICENSE.md` and the upstream copyright; new code is MIT.
- If the project ever needs upstream changes, cherry-pick manually and record the source
  commit in `legacy/UPSTREAM.md`.

**Rejected.**
- *Fork with full history.* 77 MB of history for no merge path.
- *Separate oracle repo.* Extra CI checkout and version skew.

---

## ADR-005 — Copy the v10 library; one-way migration to SQLite

**Context.** v10 stores collections in LiteDB 4.1.4 files, one `.db` per collection, with a
known critical vulnerability and no maintained cross-platform story. Users must not lose data,
and the new app must never corrupt a v10 library.

**Decision.** Never open a v10 library read-write. Copy it, migrate to a new SQLite schema,
verify, and keep the source copy as rollback. The importer is a separate, isolated component.
LiteDB 4 is referenced only by the importer (or, if it fails on `net11.0`, by a Windows-side
exporter), never by the application.

**Consequences.**
- Migration is explicit and versioned; the user sees a backup path and a summary.
- SQLite gives cross-platform, well-understood durability and queryability.
- Images/assets are copied by content hash, not by path, to survive case and separator
  differences.
- Settings JSON is imported separately with v10-compatible deserialisation.
- Rollback is "keep using the v10 copy"; no downgrade of the new database is promised.

**Rejected.**
- *Keep LiteDB 4.* Unmaintained, vulnerable, and a poor fit for ARM64/Flatpak.
- *LiteDB 5/6.* Still a niche dependency; migration between LiteDB formats is its own risk.
- *In-place upgrade.* Violates the "never write the source" rule.

---

## ADR-006 — New SDK v7; source-compatible recompile, no binary compatibility

**Context.** The v6 SDK is `net462` + WPF. Existing plugin binaries cannot load on Linux, and
WPF types do not exist. The plugin ecosystem is a major reason Playnite exists.

**Decision.** Ship `Playnite.SDK` v7 targeting `net11.0` and Avalonia. Preserve the v6 API
shape for models, events, library/metadata contracts, and host services. Replace UI extension
points with Avalonia types. Provide a migration guide, sample plugins, and an API-compatibility
report. Do not promise binary compatibility or a WPF shim.

**Consequences.**
- "Source-compatible" means: model/contract code recompiles with namespace/using changes;
  plugins that render UI need their views ported to Avalonia.
- `Microsoft.DotNet.ApiCompat` and `PublicApiAnalyzers` guard the surface.
- The add-on store and installer are v1.1, not v1; v1 ships the SDK and sample plugin only.
- Plugin sandboxing is out of scope; plugins are trusted code, as in v10.

**Rejected.**
- *Binary compatibility on Windows.* Windows is frozen; a second host is unjustifiable.
- *WPF compatibility shims over Avalonia.* Leaky, large, and a permanent behavioural trap.

---

## ADR-007 — New theme format; functional default theme in v1

**Context.** v10 themes are WPF `ResourceDictionary` XAML, loaded at runtime and layered over
the default theme. Avalonia's XAML dialect, control templates, and resource system differ.
Automated conversion is imperfect and would become a permanent compatibility surface.

**Decision.** Define an Avalonia theme format (`.axaml` resource dictionaries + `theme.yaml`).
Ship one functional default theme in v1. Support user themes by loading dictionaries from a
theme directory, with API versioning. Do not convert v10 themes.

**Consequences.**
- Existing theme authors must port; document the mapping and publish the default theme as the
  reference.
- Functional first: correct layout, readable typography, basic dark/light resources; visual
  polish follows.
- The theme API is versioned so future changes do not silently break themes.

**Rejected.**
- *WPF-to-Avalonia converter.* High effort, low fidelity, and a maintenance burden.
- *No user themes.* Removes a core Playnite feature for little saving.

---

## ADR-008 — v1 scope: desktop, Steam only

**Context.** Attempting desktop + fullscreen + all library plugins + emulation + add-ons at
once would make the first release unbounded.

**Decision.** v1 ships:
- Avalonia **desktop** shell.
- **Steam** library discovery and launch (native + Proton via Steam).
- v10 library import (Steam games and user metadata preserved).
- Settings, search, filters, grid/list/details, game launch, notifications, tray.
- New SDK v7 preview + one sample plugin.
- x64 + ARM64 AppImage + Flatpak.

Out of v1: fullscreen shell, emulation, non-Steam launchers (Heroic/Lutris/itch), add-on
store, metadata provider plugins, controller-first UI, HDR, Windows compatibility.

**Consequences.**
- The architecture must still support these as plugins/frontends; scope is deferred, not
  designed away.
- v1 is a real Linux Playnite for Steam users, not a generic launcher.
- Fullscreen is the first fast-follow; its ViewModels and controls are designed for reuse.

**Rejected.**
- *Parity v1.* Unbounded and delays the first usable release.
- *CLI only.* Proves the core but not the product.

---

## ADR-009 — x64 + ARM64; AppImage + Flatpak; 64-bit only

**Context.** .NET 11 supports Linux x64, ARM64, and ARM32; Linux x86 is not in the supported
matrix. v10 is x86 with `LargeAddressAware`, CEF x86, and x86 native dependencies. Steam Deck
is x64; ARM desktops and some handhelds are ARM64.

**Decision.** Support `linux-x64` and `linux-arm64`. Ship self-contained AppImage and Flatpak.
Drop 32-bit entirely. Publish framework-dependent builds only as developer artifacts.

**Consequences.**
- Native dependencies must have ARM64 builds: SQLite, Skia, Magick.NET, SDL2, WebKitGTK/WPE.
- Flatpak must use a runtime with WebKitGTK/WPE or bundle the needed libraries.
- Self-contained builds are larger but avoid distro runtime skew.
- No `LargeAddressAware`; 64-bit address space removes that class of problem.

**Rejected.**
- *x64 only.* Leaves ARM desktop users and some handhelds out.
- *Distro packages in v1.* Adds repository/signing maintenance before the app is stable.

---

## ADR-010 — Verification: practical rigor + three formal models

**Context.** "Formally verified" can mean machine-checked proofs of the whole application,
which is not feasible for a UI app spanning Avalonia, WebKit, Steam, and the filesystem.
It can also mean rigorous, mechanised evidence with explicit invariants.

**Decision.** Use the practical rigor ladder in [`04-VERIFICATION.md`](04-VERIFICATION.md):
golden/approval tests, process-level differential tests, property-based tests, fuzzing,
mutation testing, architecture analyzers, API compatibility checks, and headless UI tests.
Add TLA+ models for exactly three state machines where a bug is expensive and concurrency is
real:
1. library import/migration atomicity and crash recovery;
2. game session lifecycle (start/stop/cancel/crash);
3. settings migration and rollback.

"Formally verified" in this repo means: the invariant is written down, the model is checked,
and the implementation has a test that pins the same invariant. It does **not** mean a proof
of the C# implementation.

**Consequences.**
- Formal models are reviewed artifacts, not decoration. CI runs TLC.
- Tests that encode an invariant must cite the model/invariant id.
- The plan does not promise mathematical proof of UI behaviour.

**Rejected.**
- *Full formal verification.* Cost is out of proportion; tooling for Avalonia/WebKit does not
  exist.
- *Tests only.* Leaves concurrency and crash-recovery invariants implicit.

---

## ADR-011 — One-way dependency rules, mechanically enforced

**Context.** A port drifts when UI types leak into core, platform code leaks into models, or
WPF/Win32 reappears in "temporary" helpers.

**Decision.** Enforce this dependency direction:

```
Playnite.SDK  ←  Playnite.Core  ←  Playnite.Desktop
                       ↑
                Playnite.Platform  ←  Playnite.Platform.Linux
                       ↑
                Playnite.Steam / Playnite.LegacyImport
```

Rules:
- `Playnite.Core` may not reference Avalonia, `System.Windows`, `System.Drawing`,
  `Microsoft.Win32`, or `DllImport`.
- `Playnite.Platform.Linux` is the only project allowed OS-specific P/Invoke.
- `Playnite.Desktop` may reference Avalonia and Core, never `Microsoft.Win32`.
- `Playnite.SDK` may reference Avalonia types at UI extension points, never platform APIs.
- Tests may reference anything.

Enforcement: `BannedApiAnalyzers`, `NetArchTest`/`ArchUnitNET`, and a CI grep gate.

**Consequences.**
- Some v10 code must be split rather than copied wholesale.
- Platform behaviour is injectable and testable with fakes.
- Temporary exceptions need an allow-list entry with an owner and a removal issue.

**Rejected.**
- *Convention only.* Agents optimise for green; a written rule is not a gate.

---

## ADR-012 — Keep v10-compatible serialization and logging; modernise internals selectively

**Context.** v10 settings and manifests are JSON/YAML with Newtonsoft-specific behaviour,
including custom property names and field serialization. Replacing the serializer during the
port changes bytes and breaks round-trips.

**Decision.** Keep Newtonsoft.Json 13.0.4 for v10-compatible data and the plugin-facing
`IDataSerializer`. Keep NLog 6.x for file logging and log-line compatibility. Use
`System.Text.Json` only for new internal/CLI payloads that are not part of the v10 contract.

**Consequences.**
- Two JSON stacks exist, with a clear boundary: compatibility vs new internal.
- Settings goldens are byte-exact where the format is stable, semantic where ordering is
  incidental.
- A later "one serializer" project can be scheduled after the port.

**Rejected.**
- *System.Text.Json everywhere.* Cheaper long-term, but changes polymorphism, field handling,
  and property-name behaviour during the riskiest phase.
- *Keep Newtonsoft 10.* Known high-severity vulnerability.

---

## ADR-013 — Functional UI first, visual parity later

**Context.** Porting 68k lines of WPF XAML pixel-for-pixel is a project of its own. The first
Linux release needs to be usable and trustworthy, not identical.

**Decision.** Build a clean Avalonia default theme with Playnite's information architecture
and interaction model. Prioritise library grid/list/details, search/filter, settings, and game
launch. Defer bespoke animations, custom chrome, and pixel parity.

**Consequences.**
- Faster path to a usable release; visual polish is a separate, parallel workstream.
- Theme authors get a simpler reference theme.
- Screenshot tests use layout/semantic assertions first, pixel tolerance later.

**Rejected.**
- *Pixel parity v1.* Delays the port and couples it to WPF rendering quirks.
- *Generic Fluent UI with no Playnite identity.* Loses the product's feel; the information
  architecture and default theme still matter.

---

## ADR-014 — Steam is the only v1 library source

**Context.** The v10 plugin ecosystem supplies Steam, Epic, GOG, Battle.net, itch.io, Xbox,
and others. Those plugins are Windows binaries. Porting all of them, or building Linux
equivalents, is a multi-release effort.

**Decision.** Implement Steam discovery and launch in the app (not as a plugin) for v1.
Design the library-provider interface so other sources can be added as plugins later.
Manual/local/emulation libraries are out of v1 scope.

**Consequences.**
- v1 is aimed at the large Steam-on-Linux audience, including Steam Deck.
- The importer preserves non-Steam games from a v10 library as read-only metadata? **No** —
  v1 imports only Steam-linked games plus user metadata attached to them. Non-Steam rows are
  reported as skipped, not silently dropped.
- Heroic/Lutris/itch are post-v1 plugins or built-ins.
- Metadata enrichment is limited to Steam's public app details in v1; IGDB comes later.

**Rejected.**
- *Port all built-in library plugins.* Unbounded and depends on Windows APIs.
- *Manual-only v1.* Too little value for a Playnite port.

---

## ADR-015 — Windows oracle runs in GitHub Actions; local Windows is optional

**Context.** We develop on Linux. The oracle is a Windows GUI app. We need reproducible
goldens and the ability to run the old test suite.

**Decision.** Add a Windows CI workflow that builds `legacy/` with .NET Framework MSBuild,
runs the NUnit suite, and executes a headless oracle harness that exports/serialises fixture
data. Store goldens in `tests/golden/`. Local Windows is a convenience, not a requirement.

**Consequences.**
- CI must handle COM references (`IWshRuntimeLibrary`), the GAC PowerShell reference, and the
  `Playnite.Deps` NuGet feed.
- GUI screenshots are captured only for a small, stable set of screens; most goldens are
  data/HTTP/log level.
- The oracle harness is versioned with the frozen snapshot.

**Rejected.**
- *Local Windows VM only.* Not reproducible for future contributors.
- *No live old app.* Would turn the port into a rewrite.

---

## ADR-016 — Firstmate for execution; Herdr when available

**Context.** The work is large, parallelisable, and needs independent review. Firstmate
provides worktree isolation, supervision, and a durable fleet state. Herdr provides terminal
and agent visibility.

**Decision.** Use firstmate as the execution supervisor, with one crewmate per worktree and
the review protocol in [`06-OPERATIONS.md`](06-OPERATIONS.md). Use Herdr as the runtime
backend when its server is running; otherwise use tmux. OpenCode remains a supported worker
harness.

**Consequences.**
- Builds and packaging are serialised through a single lease.
- The captain reviews gate evidence, not every line.
- The plan/queue files are the durable contract; a context reset is recoverable.
- Herdr's absence in the current session is an environment fact, not a plan blocker.

**Rejected.**
- *One agent in one worktree.* Too slow for the UI and platform tracks.
- *Unsupervised fan-out.* Build contention and merge conflicts; the doctrine is explicit.

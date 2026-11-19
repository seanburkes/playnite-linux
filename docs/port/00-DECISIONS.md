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
2028-11-14). .NET 10 is LTS and currently has the same end-of-support date. The request is
explicitly ".NET 11".

**Decision.** All new projects target `net11.0` and pin `11.0.100` in `global.json` with
`rollForward: latestPatch`. Use C# 15.

**Consequences.**
- The support end date is the same as .NET 10 LTS today; the real trade-off is STS release
  cadence, newer-tooling risk, and a planned upgrade to the next LTS. It is not a shorter
  support window.
- Package versions must support `net11.0`; Avalonia 12 supports .NET 8+ and is validated on
  .NET 10/11.
- We do not multi-target unless a concrete consumer needs it. A second TFM doubles CI and
  conditional code for no v1 benefit.
- The upgrade to the next LTS is a scheduled maintenance task, not a port.

**Rejected.**
- *`net10.0` LTS.* Longer support but ignores the explicit request.
- *Multi-target `net10.0;net11.0`.* Premature; revisit only if a packaging target requires it.

---

## ADR-003 — Avalonia 12; WebView deferred to v1.1

**Context.** 68k lines of XAML and 275 XAML files are WPF. Avalonia is the closest XAML model
with mature Linux support. Avalonia 12.1.6 is current; `Avalonia.Controls.WebView` 12.1.2 is
official and uses WebView2 (Windows), WKWebView (macOS), WebKitGTK/WPE (Linux).
`Avalonia.HtmlRenderer` 12.0.0 covers simple HTML descriptions without a browser process.

**Decision.** Build the desktop shell on Avalonia 12. Use `Avalonia.HtmlRenderer` for the
v1 description surface. Keep `IWebView` in the SDK, with `Avalonia.Controls.WebView`
(WebKitGTK baseline, WPE optional) as the v1.1 engine (ADR-019). v1 does not ship a browser
engine dependency.

**Consequences.**
- WPF controls, behaviours, converters, resource dictionaries, and themes need Avalonia
  equivalents. This is the largest single workstream.
- CefSharp is removed. v1 has no JavaScript-capable scraper; Steam metadata uses HTTP/JSON.
- v1.1's `NativeWebView` on Linux prefers WPE when installed and falls back to WebKitGTK.
  Ubuntu does not package WPE, so WebKitGTK is the must-pass path; WPE is an optimisation.
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

**Decision.** Ship `Playnite.SDK` v7 targeting `net11.0`, split into Avalonia-free
`Playnite.SDK.Abstractions` and an Avalonia UI layer (ADR-018). Preserve the v6 API shape for
models, events, library/metadata contracts, and host services. Replace UI extension points with
Avalonia types. Provide a migration guide, sample plugins, and an API-compatibility
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
- **Steam** library discovery and launch (native + Proton via Steam) in AppImage.
- v10 library import (Steam games and user metadata preserved).
- Settings, search, filters, grid/list/details, game edit, game launch, notifications, tray.
- v10 library import with a clear transport/fresh-install path (ADR-021).
- Localisation key reuse and a partial-locale report (ADR-022).
- New SDK Abstractions + UI preview + one sample plugin.
- x64 + ARM64 AppImage + Flatpak (Flatpak ships the app shell only in v1; ADR-020).

Out of v1: fullscreen shell, emulation, non-Steam launchers (Heroic/Lutris/itch), add-on
store, metadata provider plugins, controller-first/gamepad UI, WebView/JavaScript metadata
(v1.1), HDR, Windows compatibility.

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
Playnite.SDK.Abstractions  ←  Playnite.Core  ←  Playnite.Desktop
        ↑                           ↑
Playnite.SDK (Avalonia UI)     Playnite.Platform  ←  Playnite.Platform.Linux
                                    ↑
                          Playnite.Steam / Playnite.LegacyImport (separate exec)
```

Rules:
- `Playnite.Core` references `Playnite.SDK.Abstractions` only. It may not reference Avalonia,
  `System.Windows` (except `ICommand`), `System.Drawing`, `Microsoft.Win32`, or `DllImport`.
- `Playnite.SDK.Abstractions` may not reference Avalonia, Platform, Win32, or Core.
- `Playnite.SDK` may reference Avalonia types at UI extension points, never platform APIs.
- `Playnite.Platform.Linux` is the only project allowed OS-specific P/Invoke. It may reference
  Avalonia base/control types for inherently UI-shaped platform services (tray, screens,
  web view); it may not reference Desktop.
- `Playnite.Desktop` may reference Avalonia, Core, SDK, and Platform, never
  `Microsoft.Win32`.
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
- Headless Skia screenshot tests use layout/semantic assertions first, pixel tolerance later.
  Old-app screenshots are reference images only.

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
Manual/local/emulation libraries are out of v1 scope. Steam is a **greenfield subsystem**
(ADR-017), not a v10 port: the v10 Steam plugin is not in this repository.

**Consequences.**
- v1 is aimed at the large Steam-on-Linux audience, including Steam Deck.
- v1 imports only Steam-linked games plus user metadata attached to them. Non-Steam rows are
  reported as `skipped` with a reason, not silently dropped. A user whose library is mostly
  GOG/Epic will see those rows missing from v1; the migration report says so before commit.
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
runs the NUnit suite, and executes an oracle harness that calls **existing v10 public APIs**
and emits raw bytes — no new export format, no re-serialisation, no normalisation. Store
goldens in `tests/golden/`. Pin `Playnite.Deps` by SHA-256 (see `oracle-lock/`). Local Windows
is a convenience, not a requirement.

**Consequences.**
- CI must handle COM references (`IWshRuntimeLibrary`), the GAC PowerShell reference, and the
  `Playnite.Deps` feed. A feed outage is an oracle outage; the checksum-pinned fallback is
  documented in `oracle-lock/README.md`.
- GUI screenshots are **reference images only**, not pixel gates. They are not reproducible on
  a hosted runner across DPI/theme/font/ClearType.
- The oracle harness is versioned with the frozen snapshot and constrained to existing APIs.

**Rejected.**
- *Local Windows VM only.* Not reproducible for future contributors.
- *No live old app.* Would turn the port into a rewrite.

---

## ADR-016 — Firstmate for execution; Herdr when available

**Context.** The work is large, parallelisable, and needs adversarial review. Firstmate
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

---

## ADR-017 — Steam is a new subsystem, not a port

**Context.** Playnite v10's Steam library is a separate built-in plugin, not part of the
`legacy/` source. There is no v10 Steam discovery, ACF/VDF parser, Proton detection, launch,
or session tracker to compare against. The plan originally labelled Steam work as a port.

**Decision.** Treat Steam as a **greenfield subsystem** with its own specification and oracle:

- recorded real-world artifacts: `libraryfolders.vdf`, `appmanifest_*.acf`, `shortcuts.vdf`,
  `librarycache` images, and a captured HTTP corpus for app details;
- a synthetic fixture corpus checked in, plus a redacted fixture captured from a real install;
- acceptance on a real Steam install (native + Proton) as a release gate;
- no claim of v10 behavioural parity for Steam rows; the parity ledger marks them
  `greenfield` with a spec/test reference, not a `v10_ref`.

**Consequences.**
- Steam gets an explicit spec and estimate, not a borrowed "port" estimate.
- The fixture corpus is the oracle; it must be captured before the implementation.
- Steam bugs are new-product bugs, not port regressions, but they still block release.

**Rejected.**
- *Find and port the v10 Steam plugin source.* It is not in this repository and depends on
  Windows APIs; vendoring it would not help Linux.
- *Treat Steam as out of scope.* Then v1 has no library source.

---

## ADR-018 — Split the SDK into Avalonia-free abstractions and an Avalonia UI layer

**Context.** `Playnite.Core` must not depend on Avalonia, but it needs the SDK's models and
non-UI contracts. If the SDK references Avalonia, Core transitively does too, and the
"Core has no Avalonia" rule is false. `BannedApiAnalyzers` checks API use, not transitive
references.

**Decision.** Split:

- `Playnite.SDK.Abstractions` — models, events, non-UI interfaces, `IDataSerializer`,
  library/metadata contracts, versioning. **No Avalonia, no platform APIs.**
- `Playnite.SDK` — the UI extension points (`PluginUserControl`, dialogs, main view, web view,
  menu items) and Avalonia types. References Abstractions.

`Playnite.Core` references **Abstractions only**. `Playnite.Desktop` references both. Plugin
authors reference `Playnite.SDK` (which brings Abstractions transitively).

**Consequences.**
- The dependency graph is enforceable: Core cannot see Avalonia even transitively.
- The v6→v7 API report is split into "abstractions" (strong compatibility claim) and "UI
  extension points" (adapted, documented breaking changes).
- More projects and a slightly more complex package graph; worth it for a real boundary.

**Rejected.**
- *Core references the SDK and we rely on analyzers.* Transitive Avalonia leaks.
- *Move all models into Core and have the SDK reference Core.* Inverts the plugin contract;
  plugins would depend on app internals.

---

## ADR-019 — WebView is v1.1; v1 uses HTML rendering and HTTP

**Context.** v1 has no metadata plugins, no add-on store, and Steam metadata is fetched over
HTTP. The only v1 HTML need is game descriptions. WebKitGTK/WPE is the most fragile host
dependency (Ubuntu lacks WPE, Flatpak runtime coupling, offscreen rendering variance), and it
was the largest packaging risk in the plan.

**Decision.** Keep WebKitGTK/WPE as the chosen engine for `IWebView`, but **defer the WebView
service and offscreen scraping to v1.1**. v1 uses `Avalonia.HtmlRenderer` for non-interactive
descriptions and plain HTTP for metadata. The `IWebView` abstraction remains in the SDK so
plugins can target it later.

**Consequences.**
- v1 packaging does not require `webkit2gtk-4.1`/`libsoup-3` or a WPE runtime.
- The Phase 1 WebView spike becomes a bounded feasibility study, not a gate.
- Metadata scraping that requires JavaScript is out of v1; Steam's JSON API is the v1 path.
- v1.1 adds WebView with a real consumer (metadata plugins / auth flows).

**Rejected.**
- *Ship WebView in v1 with no consumer.* Adds the largest dependency and test matrix for a
  feature nothing in v1 calls.

---

## ADR-020 — Flatpak Steam integration deferred to v1.1

**Context.** A Flatpak sandbox cannot see the host's `steam` binary on `PATH` or observe host
processes as children. The plan originally granted filesystem access and assumed `/proc`
tracking would work. That is false, and `--filesystem=home` is also a Flathub review problem.

**Decision.** The owner **DEFERS Flatpak Steam integration to v1.1**. Flatpak ships the app
shell only in v1. No general host-command authority is granted and no host helper service
is built for v1. Steam launch and session tracking from Flatpak, and reads of host Steam
libraries from Flatpak, are v1.1 work. AppImage is the only Steam-integrated packaging target
for v1.

**Consequences.**
- Phase 5 criteria 3 (Flatpak launch and tracking) and 6 (packaged host-library access) stay
  visible as deferred by owner decision. The Phase 5 gate cannot be claimed as fully satisfied
  while both criteria are deferred. AppImage host-library access still needs a packaged read
  to establish evidence.
- The v1.1 design must establish launch, host PID visibility and narrow library access. Any
  host helper considered then adds a security surface and must be minimal and audited.

**Rejected.**
- *Assume `/proc` works inside the sandbox.* It does not for host processes.
- *Require `--filesystem=home`.* Broad permission, Flathub review risk.

**Amendment (2026-11-19) — criterion 6 un-deferred via the portal route.** This supersedes the
read half of the decision above and the corresponding consequence. Phase 5 criterion 6 (AppImage
and Flatpak read of a host Steam library with narrow permissions) is **un-deferred and PROVEN**
by merged [#156](https://github.com/seanburkes/playnite-linux/pull/156) at
`c72614dda10fc9fa89995c2b58b41c37cecf2441`. The **portal scope** is discovery only:

- The **Flatpak** reads one `steamapps` directory at a time that the user grants through
  `xdg-desktop-portal` `FileChooser`/`Documents`. It has no filesystem access of its own.
- The **narrow-permission guarantee** is that the manifest allowlist stays exactly `wayland`,
  `fallback-x11`, `ipc` and `dri`, enforced by the unchanged `packaging/verify-flatpak.py`.
  There is no `filesystem` grant and no `org.freedesktop.Flatpak` talk-name, so the app can run
  no host command. Effective permissions are identical before and after a grant.
- The **AppImage** is unsandboxed and reads the visible host library directly.
- **No launch, no session tracking and no host helper.** Those remain v1.1 work, and Phase 5
  criterion 3 (Flatpak host launch through `flatpak-spawn`) stays **deferred to v1.1**. The
  Phase 5 gate is therefore not blocked on criterion 6; it remains pending only on that
  owner-decided deferral.

**Amendment (2026-11-19) — Flatpak launch is launch-only through OpenURI.** This supersedes the
launch half of the decision above while leaving process tracking deferred. Inside the sandbox
Playnite opens the §6.3 Steam rungameid URI (`steam:&#47;&#47;rungameid/<appid>`) through the desktop `org.freedesktop.portal.OpenURI`
route (the sandbox `xdg-open`). The v1 product surface is the shipped CLI command
`playnite launch <appid>`; the desktop GUI play action is **deferred** and is not claimed by this
amendment. In Flatpak the session is reported as **UNKNOWN**: Steam owns the game,
Playnite cannot observe host PIDs, reports no playtime, and exposes no stop or signal surface.
The user stops the game in Steam. The app id is validated as a strict decimal `uint`, the URI is
one argv element, and no shell parses it. No `flatpak-spawn`, host command, filesystem grant,
`talk-name` or manifest change is introduced; the allowlist stays exactly `wayland`,
`fallback-x11`, `ipc` and `dri`. Native and AppImage keep the existing launch-plus-tracking path
unchanged. The v1 Flatpak launch surface is the shipped CLI command `playnite launch <appid>`;
the desktop GUI play action is deferred. This closes Phase 5 criterion 3 as the **adjusted
launch-only scope** (owner/captain call), explicitly **not** as the original `flatpak-spawn`
host-tracking scope. Process tracking from Flatpak is deferred to v1.1 and would require a
minimal audited companion service only if demanded; no such service is built for v1.

Any v1.1 host helper for launch and tracking still adds a security surface and must be minimal
and audited; the portal read alone grants no process or command authority.

---

## ADR-021 — The importer is a separate process; v1 has an explicit transport story

**Context.** ADR-005 promised LiteDB 4 is never in the application, but the first-run wizard is
a Desktop feature. A project reference is not isolation, and a Windows-library-to-Linux path
was unspecified.

**Decision.**
- `Playnite.LegacyImport` is a **separate executable** with a schema-only handoff (neutral
  JSONL/zip + assets). Desktop invokes it as a subprocess and never references LiteDB.
- v1 supports:
  - copy from a mounted Windows partition / USB / SMB path chosen by the user;
  - a one-way migration with backup and report;
  - a fresh install with no v10 library (Steam-only setup).
- Windows path settings are rewritten to Linux equivalents where a mapping is obvious;
  otherwise preserved in a `legacy` bag and reported as unmapped.
- The importer is sandbox-friendly: it reads a path, writes a temp output dir, and exits.

**Consequences.**
- Desktop never ships LiteDB; the importer can be audited and fuzzed separately.
- The wizard has a clear "source library" step and a "no library" path.
- A user with no Windows machine can start fresh from Steam.

**Rejected.**
- *Reference the importer from Desktop.* Ships the CVE-bearing reader in the main app.
- *Require a Windows export step.* Blocks Steam Deck and fresh Linux users.

---

## ADR-022 — Localisation reuses the v10 key set, with a migration report

**Context.** v10 has 1,134 localisation keys and 35 language files. The plan treated this as a
small Phase 3 bullet. It is a real workstream and a product decision.

**Decision.**
- Preserve the v10 key names and English source strings.
- Load existing translations where the key exists; report missing/changed keys.
- v1 ships English plus whatever existing translations map cleanly; no machine translation is
  shipped without review.
- A locale can be marked `partial` in the UI; do not silently fall back without a report.

**Consequences.**
- Translators can reuse Crowdin work.
- The key migration is mechanical and testable (key-set diff, placeholder validation).
- Some locales will be partial at v1; that is visible, not hidden.

**Rejected.**
- *New key scheme.* Breaks translation reuse for no user benefit.
- *Drop non-English locales in v1.* Unnecessary loss; the keys are reusable.

---

## ADR-023 — SDK UI surface freezes after the shell pilot; compatibility is scoped

**Context.** The SDK's UI extension points describe Avalonia shapes the shell has not designed
yet. Freezing them in Phase 6, before Phase 7, guarantees churn. "Source-compatible" against
zero third-party plugins is a claim without a corpus.

**Decision.**
- Freeze `Playnite.SDK.Abstractions` early (Phase 3/4), because models and non-UI contracts
  are stable.
- Freeze `Playnite.SDK` UI extension points at the **end of Phase 7**, after the shell pilot
  and the first real views exist.
- The compatibility claim is scoped: "abstractions are API-compatible (ApiCompat report);
  UI extension points are adapted with a documented migration guide." Do not claim proven
  source compatibility for arbitrary third-party plugins until a named corpus of real plugins
  compiles.
- Build a corpus of at least three representative v10 plugin sources (one library, one
  metadata, one UI-heavy generic) as part of v1.1, not v1.

**Consequences.**
- Less SDK churn; the UI informs the contract.
- v1 ships a preview SDK with honest claims.
- The add-on ecosystem migration is a post-v1 project.

---

## ADR-024 — Single-owner review model

**Context.** This repository has one human owner. Every pull request is merged by that person,
and every review is made on their behalf: when an agent or model produces the review text, the
model is working for the owner, not acting as a second person. Several documents described
a "second reviewer" or "independent review" as a merge gate, and some described CODEOWNERS as
an approval requirement. Neither is true. Branch protection on the default branch requires a
pull request and the five required status checks (`inventory-and-parity`, `formal`,
`linux-build-and-test`, `AppImage packaging smoke`, `Flatpak packaging smoke`), with
strict/up-to-date intentionally off because a GitHub merge queue is unavailable for this
user-owned repository, and it blocks force pushes and branch deletion; it has no approval
requirement. The path-filtered `legacy-oracle`, `settings-library-oracle` and
`windows-fixture-validation` jobs run in addition when their paths change; they are not
required contexts. A rule that cannot be satisfied is not a gate, and a checklist that names a
reviewer who does not exist teaches readers to accept a false claim.

**Decision.**
- Review is adversarial agent review assistance: a reviewer agent, preferably a different model
  from the implementer, receives only the diff, the parity row and the oracle artifacts, and is
  asked to assume the change is wrong. It is never described as independent review, a second
  reviewer, or an approval gate.
- The owner makes the merge decision. No document may present an agent review or a CODEOWNERS
  entry as human approval.
- CODEOWNERS is audit metadata only: it records which surface each path belongs to for
  visibility and routing. It is not an approval requirement, and branch protection does not
  require code-owner review.
- Oracle independence is structural, not social: frozen `legacy/` data, committed hashes in
  `tests/fixtures/*/SHA256SUMS`, golden manifests and `oracle-lock/` pins, protected paths
  enforced by the `plan-guards` CI diff against the merge base, the five required status checks
  and the path-filtered oracle jobs that run on their paths, and the TLA+ models run by the
  `formal` job. Cite those mechanisms rather than asserting independence in prose.
- A protected oracle change is declared, not seconded: the task says `oracle-change`, the PR
  carries the label, and the protected-path gate checks the diff, re-reading the label through
  the API on pull-request label events. The `merge_group` variant is retained but dormant
  because no merge queue exists here. `legacy/` and `oracle-lock/` remain unconditionally
  immutable; no label overrides them.
- An external review is preferred where one can exist: for an oracle change, prefer a reviewer
  outside this repository (an external human, or a different machine or owner). When that is
  not used, the owner's merge decision is the accepted exception, and the gate artifact
  (`docs/port/gates/P<phase>-*.md`) records that the exception was taken.

**Consequences.**
- A reader can tell a real gate (branch protection, the five required checks, the
  protected-path gate) from assistance (agent review).
- Agent review stays unit evidence: two adversarial diff-only reviews per unit, with findings
  applied by a fixer. Its authority is "evidence the owner reads", not "approval".
- The `oracle-change` route remains the only way a protected surface moves, and CI enforces it
  rather than a person's signature.
- The repository still contains stale "second reviewer" wording in a small set of workflow,
  test and protected-path files. That is tracked as a required follow-up unit in the pull
  request that added this ADR; it is not claimed to be fixed here.

**Residual risk.** One human plus models can share blind spots. Concretely:
- diff-only review cannot execute builds or tests, so a reviewer reasons about behaviour it
  never runs;
- two reviewers drawn from the same model or provider are correlated and tend to miss the same
  class of defect; a different model is a preference, not a guarantee;
- the owner chooses the reviewer packet and can ignore a finding, and no mechanism overrides
  that decision;
- the owner also administers branch protection and the protected-path gate, so a deliberate
  owner action can weaken the gate itself;
- the structural checks catch tampering and regression (changed bytes, lowered counts, broken
  hashes), not a semantically wrong port that happens to pass its tests;
- strict/up-to-date is off, so a PR whose head was tested against an older `main` can merge;
  the post-merge `linux-ci` run on `main` is the backstop, and the post-merge `port-index`
  aggregate commit is not a mergeability gate;
- the `formal` job fetches `tla2tools.jar` from the latest release without a hash pin, and the
  oracle jobs are path-filtered, so they only run when their paths change.

The mitigations are structural rather than social - a frozen `legacy/` snapshot the port
cannot edit, committed hashes and locks, CI diffs of protected paths, mutation and negative
probes, the formal models, and the external-review preference above. They reduce the risk but
do not eliminate it, so review claims are stated as assistance, never as independent approval.

**Rejected.**
- *Pretend agent review is independent approval.* It is not a second person, and saying so
  would overstate the evidence behind a merge.
- *Require an approval that cannot exist.* An approval rule with one owner is either
  self-approval, which is meaningless, or a permanent block on every change.
- *Drop the external-review preference because the repository has one owner.* An external
  reviewer or a different machine can still exist for a high-risk oracle change; the option
  stays, with the exception recorded.

---

## Deferred limitations for v1 (not ADRs)

These are accepted v1 limitations recorded after the original ADR list was closed at ADR-023;
ADR-024 is a later governance decision. They are not new architectural decisions and do not
renumber the ADR list.

### D1 — Wayland accessibility (AT-SPI) is unavailable for Avalonia 12.1.6

**Status.** Accepted v1 limitation and deferred item, by captain decision on 2026-11-14.
Accessibility is **not a v1 requirement**, and this limitation is not a Phase 1 gate blocker.

**Evidence (merged).** `spikes/avalonia-headless/REAL-RUNTIME.md`, merged in PR #24
(`ff1da20`):

- A real window opens on both native Wayland (`Avalonia.Wayland.WindowImpl`,
  `xwayland=false`) and X11/XWayland (`Avalonia.X11.X11Window`, `xwayland=true`) at 640x480
  with compositor screenshots and matching Tab/Space traversal.
- X11/XWayland exposes the full AT-SPI tree (names, roles, states, `AccessibleId`s) and drives
  toggle/click/select/text actions.
- Wayland exposes no AT-SPI tree at all, even with app-logged `AT_SPI_BUS_ADDRESS`,
  `ACCESSIBILITY_ENABLED=1`, `GTK_A11Y=atspi`, `NO_AT_BRIDGE=0` and a live
  `org.a11y.Bus.GetAddress`; `Avalonia.Wayland.dll` carries no AT-SPI symbols while
  `Avalonia.X11.dll` does. A zenity/GTK 4 native-Wayland control probe was visible through the
  same bus, so the probe was not the problem.

**What the evidence does not show.** No end-to-end screen-reader test (for example Orca) was
performed; speech output, caret tracking and browse mode are untested and are not claimed. A
bare Xorg session and generic (uinput/ydotool) input injection were also not tested.

**Scope.** For Avalonia 12.1.6, accessible-to-screen-reader behaviour is only observable under
X11/XWayland. v1 does not claim screen-reader accessibility on Wayland, and no v1 gate requires
it. Keyboard navigation, AutomationProperties and semantic-tree tests remain in the product and
test plan as quality practices; they are not a screen-reader claim.

**Possible future directions.** (a) Document an X11/XWayland fallback for accessibility users
as a workaround, not a default; (b) track upstream Avalonia Wayland AT-SPI work and re-run the
probe on each major upgrade; (c) plan a later accessibility milestone with an Orca end-to-end
pass, a bare Xorg session, generic input injection, focus order and high-contrast checks.
Revisit on an Avalonia major upgrade or at v1.1.

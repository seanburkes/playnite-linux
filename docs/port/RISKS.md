# Risks

Scored: likelihood × impact, 1–5 each. Owner is the role that must act, not the person to blame.

| # | Risk | L | I | Score | Mitigation | Trigger / early warning |
| --- | --- | ---: | ---: | ---: | --- | --- |
| R1 | **Thin oracle**: 204 tests for 143k LOC; UI untested | 5 | 5 | 25 | Phase 1 goldens + process-level differential + parity ledger; do not start bulk porting before they exist | A unit cannot be compared to v10 without a manual side-by-side |
| R2 | **UI port is the long pole**: 68k XAML, 275 files, WPF-specific behaviours | 5 | 4 | 20 | Functional default theme; desktop only; reuse ViewModels; pilot 3 views; buy/borrow a mature Avalonia theme if needed | A single view takes >1 week or needs custom control rewrites |
| R3 | **Steam session detection** is heuristic on Linux (Steam returns immediately; Proton layers) | 4 | 4 | 16 | Formal `GameSession.tla`; process-tree watcher + manual stop; spike in Phase 1; fallback to time-based tracking | Orphan processes or games never marked stopped in the spike |
| R4 | **WebKitGTK/WPE variance** across Ubuntu/Fedora/Arch/SteamOS/Flatpak | 4 | 4 | 16 | WebKitGTK baseline; WPE optional; `NativeWebDialog` fallback; distro matrix in CI; abstraction at `IWebView` | A page loads in one distro and not another, or offscreen extraction fails |
| R5 | **Plugin ecosystem gap**: no v10 binaries; source migration is third-party work | 4 | 4 | 16 | v1 stands alone (Steam only); SDK v7 + samples + migration guide; no binary-compat promise | v1 launch reviews say "no plugins, no Playnite" |
| R6 | **LiteDB 4 import** fails or corrupts data on `net11.0` | 3 | 5 | 15 | Spike first; isolated importer; Windows exporter fallback; copy-only + verify + atomic swap; never write source | The spike cannot read a fixture library on Linux |
| R7 | **Data loss during migration** (assets, non-Steam rows, unknown settings) | 3 | 5 | 15 | Backup manifest, dry-run, count/hash verification, `legacy` bag for unknown settings, explicit skipped report | Count mismatch or a missing asset in the fixture |
| R8 | **UI behaviour drift**: WPF layout/binding semantics differ in Avalonia | 5 | 3 | 15 | Semantic + screenshot tests; layout primitives; avoid pixel parity; pilot views | Repeated "it looks wrong" without a failing test |
| R9 | **Build/CI fragility** (COMReference, GAC PowerShell, Playnite.Deps feed, VS version) | 4 | 3 | 12 | Windows oracle CI with VS/MSBuild; pin image; vendor source packages; keep AppVeyor fallback | Windows job red for infra reasons >1 day |
| R10 | **ARM64 native dependency gaps** (Magick, SDL2, WebKit, SQLite) | 3 | 4 | 12 | Verify each in Phase 2/5; prefer managed/bundled; CI on ARM runner or qemu smoke | A dependency has no `linux-arm64` runtime |
| R11 | **Flatpak sandbox vs game libraries** (host paths, Steam, processes) | 4 | 3 | 12 | Document permissions; test Flatpak against a host Steam install; portals where possible | Flatpak cannot see `~/.steam` or launch a game |
| R12 | **.NET 11 STS support window** ends Nov 2028 | 3 | 3 | 9 | Pin SDK; schedule upgrade to next LTS; avoid preview-only APIs | Next LTS announcement |
| R13 | **Agent drift / review theatre**: agents optimise for green, weaken tests, add stubs | 4 | 4 | 16 | Oracle guard, counts gate, banned APIs, diff-only blind reviews, no self-review, 2-attempt escalation | A PR changes a golden/test or adds a `TODO`/stub |
| R14 | **Scope creep**: fullscreen, plugins, emulation, add-on store pulled into v1 | 4 | 4 | 16 | ADR-008 scope; parity ledger `out-of-scope-v1`; captain gate | A "while we're here" PR touches out-of-scope UI |
| R15 | **Serialization drift** (Newtonsoft → STJ, property names, null vs missing) | 3 | 4 | 12 | Keep Newtonsoft for v10 data; byte goldens; ADR-012 | A settings round-trip diff |
| R16 | **Path/case/locale bugs** on Linux | 4 | 3 | 12 | `IPaths`, ordinal comparisons, invariant formatting, TZ/LANG test matrix | Tests pass on developer machine, fail in CI/Flatpak |
| R17 | **Security**: zip-slip, path traversal, shell injection, unsafe deserialization, malicious add-ons | 3 | 5 | 15 | Parameterized processes, path canonicalization, archive entry validation, no BinaryFormatter, security review gate | A fuzz crash or a path escaping the data root |
| R18 | **Licensing/supply chain**: bundled `Playnite.Deps`, replaced libraries | 3 | 4 | 12 | Source packages, license scan, SBOM, NuGet audit, no copied DLLs | Unknown license or a vulnerable transitive package |
| R19 | **Solo bandwidth / context loss** | 4 | 4 | 16 | Ledger + queue + gate artifacts; firstmate supervision; small units; resumable state | The plan cannot be resumed from docs alone |
| R20 | **Upstream v11 divergence**: upstream releases a Linux-capable v11 | 2 | 3 | 6 | Standalone fork; monitor upstream; if v11 ships Linux, reassess (do not merge blindly) | Upstream announces Linux/v11 source |

## Top three to watch

1. **R1/R13 — the oracle and the reviewers.** If these fail, everything else is unverifiable.
   Spend the first two weeks on goldens, CI, and review prompts, not on UI.
2. **R2 — the UI.** Start the Avalonia pilots early, but do not let UI work reshape Core
   without an ADR. The functional theme is the scope control.
3. **R3/R4 — the Linux platform edges.** Steam session tracking and WebKit are the two places
   where "it works on my machine" is most likely. Spike both before committing to v1 dates.

## Risk review cadence

- Review this table at every phase gate.
- Add a row when a new failure mode appears twice.
- Close a row only with evidence (a test, a spike report, or a shipped mitigation).
- Do not delete rows; mark them `retired` with the evidence.

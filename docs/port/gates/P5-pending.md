# Gate P5 — Steam and Linux platform (pending assessment)

- Date: 2026-11-18
- Base: `ce8e2c22a753ffe3279dc7f744455405464aa92a` (`main`)
- Owner: `p5-shards` (2026-11-18 update; initial assessment by `p5-shards2`)
- Result: **PENDING — NOT AN ACCEPTANCE.** Zero of the six exit criteria are proven. A
  real-machine run was recorded on 2026-11-18; it did not prove criteria 2 or 4.
- Review basis: worker diff-only self-assessment of merged artifacts and unit shards. No
  independent approval and **no owner acceptance** is claimed.
- Addendum (follow-up to merged #123): the unit evidence table cites main-push linux-ci runs on
  the merged heads, not pull-request head runs. No criterion status changed.
- Addendum (p5-008 merge and real-machine run): p5-008 is **MERGED** at
  `1e842c075aeb3412323fcd467ca93aaaef03b8cb` (#126), so criterion 2 no longer has an
  implementation gap, but it still requires a real launch-and-stop and stays **NOT PROVEN**. The
  owner-authorized run of 2026-11-18 (`data/p5-008-real-acceptance-2026-11-18.md`; logs
  `data/p5-008-acceptance-2026*`) is evidence, not acceptance; it is summarised in criteria 2,
  4 and 5 below. No criterion verdict moved.

This file records the honest state of the Phase 5 exit gate. It is deliberately not an
acceptance record: a criterion moves to PROVEN only when the named artifact exists and is
reachable from merged `main`.

## Exit criteria

| # | Criterion | Status | Concrete artifact or exact gap |
| --- | --- | --- | --- |
| 1 | Synthetic + redacted real fixture yields the expected app set, paths and artwork ids | **PARTIAL** | Synthetic trees (`tests/fixtures/steam/basic`, `second-library`, `broken`; `tools/Playnite.Fuzz/Corpus/p5-002`) are exercised by `SteamDiscoveryTests` (app/library paths, unavailable library, broken VDF), and `ArtworkTests.Identity_uses_only_observed_names` pins the five observed `appcache/librarycache/<appid>/<name>` identities plus the achievement JSON identity. The redacted real corpus `tests/fixtures/steam/real` is committed and hash-pinned. **Gap:** no test runs discovery over the redacted real corpus to assert its expected app set and paths — `SteamDiscoveryTests` declares itself synthetic-only. Only one artwork test reads the real corpus, and only its achievement JSON (`Recorded_metadata_corpus_is_read_without_modification`); the real corpus explicitly contains no artwork. Artwork ids are therefore proven only synthetically, and Steam CDN/remote artwork mapping remains UNKNOWN. |
| 2 | A real Steam install launches and stops a native and a Proton game | **NOT PROVEN** | p5-008 is merged at `1e842c075aeb3412323fcd467ca93aaaef03b8cb` (#126) and supplies the launch path (`src/Playnite.Steam/p5-008/`, `src/Playnite.Platform.Linux/p5-008/`): argv-only `steam -applaunch <appid>`, protocol-handler fallback, conservative native/Proton/UNKNOWN selection, and stop/cancel/disposal through an injected process host with no live Steam in unit tests. The owner-authorized real-machine run of 2026-11-18 (`data/p5-008-real-acceptance-2026-11-18.md`; logs `data/p5-008-acceptance-2026*`) launched native Dwarf Fortress 975370 and attributed a unique fresh pid 405679 with pinned start ticks in pgid 405646, but that group also held Steam's own launcher shim, so the tracker correctly degraded (`Steam group is shared or unverifiable`) and never reached a non-degraded Running state; the identities were SIGTERM-ed individually, the group ended empty and the client survived. Proton Terraria 105600 produced no unique identity inside the 60s window (startup exceeded it, UNKNOWN), and Hotline Miami 219150 crashed in its SDL platform plugin before attribution. **Gap:** no native or Proton title has completed a non-degraded launch **and** stop; group-scoped stop is permanently degraded while Steam shares the group, and the Proton attribution window is shorter than a real startup. |
| 3 | Inside Flatpak, `flatpak-spawn --host` launches Steam and tracking observes the game process | **NOT PROVEN** | ADR-020 selects the host-launch design, but no `flatpak-spawn` implementation exists in `src/`. Steam spec §6.4 states neither host command was executed, and the p1-010 Flatpak spike concluded **inconclusive** for host launch and host PID visibility. No Flatpak acceptance run exists. |
| 4 | No orphaned processes after cancel/teardown | **PARTIAL** | p5-004 provides real-process proofs through `LinuxProcessLauncher`: `Real_stop_leaves_no_child`, `Real_cancel_leaves_no_child`, `Real_tracker_disposal_leaves_no_child`, `Real_launcher_shutdown_leaves_no_child`, `Real_stop_during_starting_leaves_no_child`, plus process-group containment (`setsid` launch, group kill and verification) and explicit `ProcessSurvivorException` for tracked escapees. The reparent-before-first-snapshot test is now on `main` (`5c4d11a`, #125). p5-008 merged at `1e842c0` adds group-empty proof and per-identity pidfd signalling for its attributed group (`SteamLaunchTests`: stop/cancel/disposal reaping, escaped/unrelated groups never signalled, group reuse rejected). The 2026-11-18 real run left no orphan in any observed path: the native game's shared Steam group was deliberately left alive and its identities SIGTERM-ed individually to an empty group, and the Proton tree was cleaned by graceful SIGTERM to the game binary to an empty tree; there was no SIGKILL, no group kill and no client kill. **Gap:** the guarantee is still not proven end-to-end through the Steam/Flatpak paths: the native title was never stopped through a non-degraded group, the Proton title was never attributed, and no Flatpak host path was exercised. |
| 5 | Session state matches the formal model's terminal states | **PARTIAL** | `docs/formal/GameSession.tla` defines the state machine; the P4 gate records the TLC run (GameSession 1401 generated / 400 distinct / depth 27, log-backed in run 43998974777). `docs/port/p5-004-process-sessions.md` maps every formal action to the implementation and states that the terminal set is exactly `Stopped`, `Cancelled`, `Failed`; `GameSessionTrackerTests` assert terminal transitions and that no terminal state is emitted without a proven reap. **Gap:** this is a documented mapping plus unit tests, not a machine-checked refinement. The 2026-11-18 real run observed only a degraded `Cancelled` on disposal (native Dwarf Fortress) and no attributed Proton session, not a non-degraded `Running` to `Stopped` sequence. |
| 6 | AppImage and Flatpak can read a host Steam library with narrow permissions | **NOT PROVEN** | The Flatpak manifest grants only `wayland`, `fallback-x11`, `ipc` and `dri`, and `packaging/README.md` states it has no home or host-filesystem access; `packaging/verify-flatpak.py` enforces that narrow allowlist. That deliberately leaves no path to a host Steam library. The packaging smoke jobs run the version CLI and a headless desktop shell, not a Steam-library read. AppImage is unsandboxed but has no recorded packaged-build Steam-library read either. No narrow Steam-library `--filesystem` entry or portal path is implemented or tested. |

Summary: **0 PROVEN, 3 PARTIAL, 3 NOT PROVEN.** Phase 5 is not accepted. The 2026-11-18
real-machine run is recorded and did not move any criterion.

## Unit evidence relied on

| Unit | Merged commit | Merged-head linux-ci (main push) |
| --- | --- | --- |
| p5-001 | `777578cc81efb0130b41924c282542f0b346836c` (#109) | [44190373018](https://github.com/seanburkes/playnite-linux/actions/runs/44190373018) on `777578c`, success. |
| p5-002 | `c7b25dc7fa39238ab8f6fc47b9b21a4237e3ddb0` (#110), with follow-ups `f8aed2fe6b29918fbe6bc29dd5f3b54a7af6fcee` (#112), `c057be0f3e548ac31ac515b6da7ad4c606d17916` (#115) and `a37890f33110c70b8411bac4457d16e0aa41822f` (#116) | [44323525396](https://github.com/seanburkes/playnite-linux/actions/runs/44323525396) on the final follow-up merged head `a37890f`, success; earlier merged-head runs 44200516358, 44203797675 and 44244649688 also succeeded. |
| p5-003 | `22f051a92b6b5a82566fd382a04f9848c30ed756` (#118) | [44344905055](https://github.com/seanburkes/playnite-linux/actions/runs/44344905055) on `22f051a`, success. |
| p5-005a | `785b89200666ab63f07ca2fd1af4e757c3ef1e36` (#119) | [44344964418](https://github.com/seanburkes/playnite-linux/actions/runs/44344964418) on `785b892`, success. |
| p5-004 | `064e261b5bffa6c434515b9851160a21fd0e317d` (#120) | [44345002684](https://github.com/seanburkes/playnite-linux/actions/runs/44345002684) on `064e261`, success. |
| p5-007a | `60e71ca950a21b72a08165e60c0e8066905ffed3` (#121) | [44345049906](https://github.com/seanburkes/playnite-linux/actions/runs/44345049906) on `60e71ca`, success. |
| p5-008 | `1e842c075aeb3412323fcd467ca93aaaef03b8cb` (#126) | [44366637238](https://github.com/seanburkes/playnite-linux/actions/runs/44366637238) on `1e842c0`, success. The pull-request run [44365780060](https://github.com/seanburkes/playnite-linux/actions/runs/44365780060) also succeeded on the verified pre-merge head `23e0797b`. |

The tooling row #122 (`p2-evidence2`, merge `7ea3a3752dca413fae3aac8e2e869678d2375c37`) had a
successful main-push linux-ci run [44345093914](https://github.com/seanburkes/playnite-linux/actions/runs/44345093914);
it is not a Phase 5 desktop unit and contributes no exit evidence.

These are merged-head main-push runs, not pull-request head runs. They do not measure this
documentation change and are not Phase 5 exit evidence by themselves.

## What would close a row

1. A discovery test over `tests/fixtures/steam/real/` asserting the expected app set and paths,
   plus an artwork-identity assertion against evidence (or a recorded ruling that the redacted
   corpus excludes artwork and a separate real artwork capture).
2. p5-008's real Steam acceptance: native and Proton titles launched and stopped, with the
   observed process-to-session attribution recorded.
3. A Flatpak acceptance run where `flatpak-spawn --host` launches Steam and the tracker observes
   the game process in the host PID namespace.
4. The same no-orphan proof through the Steam/Flatpak paths. Reparent-before-first-snapshot
   coverage is already merged (`5c4d11a`, #125).
5. A machine-checked refinement of the implementation against `GameSession.tla`, or a recorded
   real session whose terminal sequence matches the model.
6. A narrow Flatpak Steam-library path (explicit `--filesystem` or portal) plus a packaged
   AppImage/Flatpak read acceptance, with the effective permissions shown.

**Real-machine run required to move criteria 2 and 4:** one recorded transcript plus logs from
a real Steam install showing (a) one native and one Proton title each attributed as a unique
fresh pid/start-tick identity, reaching a non-degraded `Running` and then `Stopped` terminal
(the Proton window must outlast the real startup), and (b) a cancel/teardown in the same run
whose post-stop process table shows no remaining game, wrapper, wineserver or prefix process
while the Steam client survives. The 2026-11-18 run produced neither condition (native stop
degraded by the shared Steam group; Proton never attributed), so criterion 2 stays **NOT
PROVEN** and criterion 4 stays **PARTIAL**.

## Non-claims

- Phase 5 is not accepted.
- No real Steam install, native game, Proton game, Flatpak host launch/tracking, or packaged
  host-library read is claimed as accepted. The 2026-11-18 real-machine run is recorded as
  evidence only and did not prove criteria 2 or 4.
- No protected oracle, baseline, aggregate or product file is changed by this assessment.

# Gate P5 — Steam and Linux platform (pending assessment)

- Date: 2026-11-18
- Base: `8e5984efbdc2e9d725a422bbdc95af1364156800` (`main`)
- Owner: `p5-shards` (2026-11-18 update; initial assessment by `p5-shards2`)
- Result: **PENDING — NOT AN ACCEPTANCE.** The 2026-11-18 real Steam rerun proves criterion 2.
- Review basis: worker diff-only self-assessment of merged artifacts and unit shards. No
  independent approval and **no owner acceptance** is claimed.
- Addendum (follow-up to merged #123): the unit evidence table cites main-push linux-ci runs on
  the merged heads, not pull-request head runs. No criterion status changed.
- Earlier p5-008 assessment: the launch implementation merged at
  `1e842c075aeb3412323fcd467ca93aaaef03b8cb`. The first owner-authorized run of 2026-11-18
  (`data/p5-008-real-acceptance-2026-11-18.md`; logs `data/p5-008-acceptance-2026*`) left
  criterion 2 unproven and changed no verdict. The observations are retained below.
  [Initial p5-008 merge](https://github.com/seanburkes/playnite-linux/pull/126)

This file records the honest state of the Phase 5 exit gate. It is deliberately not an
acceptance record: a criterion moves to PROVEN only when the named artifact exists and is
reachable from merged `main`.

## Exit criteria

| # | Criterion | Status | Concrete artifact or exact gap |
| --- | --- | --- | --- |
| 1 | Synthetic + redacted real fixture yields the expected app set, paths and artwork ids | **PARTIAL** | Synthetic trees (`tests/fixtures/steam/basic`, `second-library`, `broken`; `tools/Playnite.Fuzz/Corpus/p5-002`) are exercised by `SteamDiscoveryTests` (app/library paths, unavailable library, broken VDF), and `ArtworkTests.Identity_uses_only_observed_names` pins the five observed `appcache/librarycache/<appid>/<name>` identities plus the achievement JSON identity. The redacted real corpus `tests/fixtures/steam/real` is committed and hash-pinned. **Gap:** no test runs discovery over the redacted real corpus to assert its expected app set and paths — `SteamDiscoveryTests` declares itself synthetic-only. Only one artwork test reads the real corpus, and only its achievement JSON (`Recorded_metadata_corpus_is_read_without_modification`); the real corpus explicitly contains no artwork. Artwork ids are therefore proven only synthetically, and Steam CDN/remote artwork mapping remains UNKNOWN. |
| 2 | A real Steam install launches and stops a native and a Proton game | **PROVEN** | The owner-authorized real Steam run recorded below launched and stopped native One Deck Dungeon (`770100`) and Proton Hades (`1145360`), and repeated both launches with cancellation. All four flows passed with a non-degraded stop report. The run's accepted head `649d25f` has the same tree as merged `d54e897`. |
| 3 | Inside Flatpak, `flatpak-spawn --host` launches Steam and tracking observes the game process | **NOT PROVEN** | ADR-020 selects the host-launch design, but no `flatpak-spawn` implementation exists in `src/`. Steam spec §6.4 states neither host command was executed, and the p1-010 Flatpak spike concluded **inconclusive** for host launch and host PID visibility. No Flatpak acceptance run exists. |
| 4 | No orphaned processes after cancel/teardown | **PARTIAL** | p5-004 provides real-process proofs through `LinuxProcessLauncher`: `Real_stop_leaves_no_child`, `Real_cancel_leaves_no_child`, `Real_tracker_disposal_leaves_no_child`, `Real_launcher_shutdown_leaves_no_child`, `Real_stop_during_starting_leaves_no_child`, plus process-group containment (`setsid` launch, group kill and verification) and explicit `ProcessSurvivorException` for tracked escapees. The reparent-before-first-snapshot test is now on `main` (`5c4d11a`, #125). p5-008 merged at `1e842c0` adds group-empty proof and per-identity pidfd signalling for its attributed group (`SteamLaunchTests`: stop/cancel/disposal reaping, escaped/unrelated groups never signalled, group reuse rejected). The real Steam rerun passed the no-orphan check after every observed stop and cancel. **Gap:** no end-to-end cancel/teardown has been exercised through the Flatpak host path. |
| 5 | Session state matches the formal model's terminal states | **PARTIAL** | `docs/formal/GameSession.tla` defines the state machine. The P4 gate records the TLC run (GameSession 1401 generated / 400 distinct / depth 27, log-backed in run 43998974777). `docs/port/p5-004-process-sessions.md` maps every formal action to the implementation and states that the terminal set is exactly `Stopped`, `Cancelled`, `Failed`. `GameSessionTrackerTests` assert terminal transitions and that no terminal state is emitted without a proven reap. The real Steam rerun observed non-degraded `Running` through `Stopping` to `Stopped` or `Cancelled`. **Gap:** the two observed paths and unit tests leave formal refinement unchecked, and the real run did not exercise `Failed`. |
| 6 | AppImage and Flatpak can read a host Steam library with narrow permissions | **NOT PROVEN** | The Flatpak manifest grants only `wayland`, `fallback-x11`, `ipc` and `dri`, and `packaging/README.md` states it has no home or host-filesystem access; `packaging/verify-flatpak.py` enforces that narrow allowlist. That deliberately leaves no path to a host Steam library. The packaging smoke jobs run the version CLI and a headless desktop shell, not a Steam-library read. AppImage is unsandboxed but has no recorded packaged-build Steam-library read either. No narrow Steam-library `--filesystem` entry or portal path is implemented or tested. |

Summary: **1 PROVEN / 3 PARTIAL / 2 NOT PROVEN.** Phase 5 exit remains pending. The evidence
is limited to the real Steam install and does not establish Flatpak or host-library permissions.

## Criterion 2 real-machine evidence

The 2026-11-18 transcript (`data/p5-008f/acceptance.log`) records all four flows passing
and exit code 0. The accepted head is `649d25f61d6d58b66860575149735a3cf7d550d2`. A full
`git diff 649d25f d54e897` is empty. At assessment base `8e5984e`,
`git diff 649d25f origin/main` shows only the regenerated `docs/port/PORT-STATUS.md`,
`docs/port/ledger.tsv`, and `docs/port/queue.tsv`. The tested Steam implementation is on
`main`. [Merged p5-008 follow-up](https://github.com/seanburkes/playnite-linux/pull/141)

Executables below are relative to the tested Steam library. For native `770100`, the Linux
binary established attribution while the harness reported selection `Unknown`. For Proton
`1145360`, the harness reported selection `Proton`. Hades ran under Wine's preloader and
its `argv[0]` (`S:\steamapps\common\Hades\x64\Hades.exe`) resolved to
`steamapps/common/Hades/x64/Hades.exe` through its own
`steamapps/compatdata/1145360/pfx/dosdevices/s:` mapping. Steam owns the runtime selection.

| Flow | Outcome | PID / start ticks / PGID | Observed executable | Time to attribution | Stop report |
| --- | --- | --- | --- | --- | --- |
| Native `770100` stop | `PASS native Stopped` | `2408985 / 233722889 / 2408880` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 5.3 s | `STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty` |
| Native `770100` cancel | `PASS native Cancelled` | `2422511 / 233725363 / 2422478` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 3.6 s | `STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty` |
| Proton Hades `1145360` stop | `PASS Proton Stopped` | `2441767 / 233728214 / 2441767` | `steamapps/common/Proton - Experimental/files/lib/wine/i386-unix/wine64-preloader` | 7.8 s | `STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty` |
| Proton Hades `1145360` cancel | `PASS Proton Cancelled` | `2467654 / 233731050 / 2467654` | `steamapps/common/Proton - Experimental/files/lib/wine/i386-unix/wine64-preloader` | 7.3 s | `STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty` |

Each flow sent one pidfd `SIGTERM` to its attributed game identity, retained the original
Steam client identities, and passed the no-orphan assertion. No `SIGKILL`, group signal,
client signal, or helper signal occurred. The unreadable member in each native game's
group was tolerated as evidence and received no signal. Games that outlast the graceful
stop deadline still require manual Steam Stop. The run did not verify launch options or offline mode.

### Earlier run limitations

The initial p5-008 implementation supplied argv-only `steam -applaunch <appid>`,
protocol-handler fallback, conservative native/Proton/UNKNOWN selection, and
stop/cancel/disposal through an injected process host with no live Steam in unit tests.
The earlier owner-authorized run attributed native Dwarf Fortress `975370` to PID
`405679` in PGID `405646`. The group held Steam's launcher shim and the tracker
reported `Steam group is shared or unverifiable`, followed by degraded `Cancelled`
on disposal. Terraria `105600` exceeded the 60 s attribution window and stayed
UNKNOWN. Hotline Miami `219150` crashed in its SDL platform plugin before attribution.
Individual graceful SIGTERM cleanup emptied the observed game group and tree, leaving
the Steam client alive with no orphan, group kill, client kill, or SIGKILL.

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
2. Proven by the native and Proton launches, stops, cancellations, and process-to-session
   attribution recorded above.
3. A Flatpak acceptance run where `flatpak-spawn --host` launches Steam and the tracker observes
   the game process in the host PID namespace.
4. The same no-orphan proof through the Flatpak host path, including a post-teardown process
   table showing no remaining game, wrapper, wineserver, or prefix process and the Steam client
   still running. Reparent-before-first-snapshot coverage is already merged (`5c4d11a`, #125).
5. A machine-checked refinement of the implementation against `GameSession.tla`, or recorded
   real-session traces demonstrating the model's terminal states, including `Failed`.
6. A narrow Flatpak Steam-library path (explicit `--filesystem` or portal) plus a packaged
   AppImage/Flatpak read acceptance, with the effective permissions shown.

## Non-claims

- Phase 5 is not accepted.
- The 2026-11-18 real-machine run is evidence for criterion 2. This assessment records no
  owner acceptance of the install or either game, Flatpak host launch/tracking, or packaged
  host-library access.
- No protected oracle, baseline, aggregate or product file is changed by this assessment.

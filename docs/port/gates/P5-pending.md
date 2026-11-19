# Gate P5 — Steam and Linux platform (pending assessment)

- Date: 2026-11-19
- Base: `c72614dda10fc9fa89995c2b58b41c37cecf2441` (`main`); earlier revisions of this
  assessment were based on `7f28065`, `49302a4`, `1b9af6e`, `8e5984e` and `7c4498c`.
- Owner: `p5-shards` (2026-11-19 update; initial assessment by `p5-shards2`)
- Result: **PENDING — NOT AN ACCEPTANCE.** Criteria 1, 2, 4 and 5 are proven by the merged
  evidence recorded below, and criterion 6 is proven by the merged portal host-library evidence
  ([#156](https://github.com/seanburkes/playnite-linux/pull/156) at `c72614d`). Criterion 3 is
  **DEFERRED BY OWNER DECISION** to v1.1 per ADR-020. v1 scope is satisfied **on the
  owner-decided scope**; **Phase 5 exit is the captain's call**, and this file neither announces
  exit nor writes acceptance. AppImage is the only Steam-integrated packaging target for v1; the
  Flatpak does not launch or track Steam and reads only a user-granted host library through the
  desktop portal.
- Review basis: worker diff-only self-assessment of merged artifacts and unit shards. No
  independent approval and **no owner acceptance** is claimed.
- Addendum (follow-up to merged #123): the unit evidence table cites main-push linux-ci runs on
  the merged heads, not pull-request head runs. No criterion status changed.
- Addendum (PR #151): criterion 4 moves to **PROVEN** on the packaged AppImage evidence merged
  as [#148](https://github.com/seanburkes/playnite-linux/pull/148). Criteria 1 and 5 stay
  **PARTIAL**; criteria 3 and 6 stay **DEFERRED BY OWNER DECISION**. The summary is now
  **2 PROVEN / 2 PARTIAL / 2 DEFERRED**. **Phase 5 is still not exited** while criteria 1 and
  5 are partial and criteria 3 and 6 are deferred.
- Addendum (follow-up to merged #152): criterion 1 moves to **PROVEN** on the real-cache
  artwork identity evidence merged as
  [#152](https://github.com/seanburkes/playnite-linux/pull/152) at
  `74475aef9353c4a7459ca838d9c3736b1be5a189`. Criterion 5 remains the only **PARTIAL**, and
  criteria 3 and 6 remain **DEFERRED BY OWNER DECISION**. The summary is now **3 PROVEN /
  1 PARTIAL / 2 DEFERRED**. **Phase 5 is still not exited** while criterion 5 is partial and
  criteria 3 and 6 are deferred. Image bytes and the Steam CDN/remote artwork mapping remain
  **UNKNOWN** and out of v1 scope; only local artwork identity is proven.
- Addendum (follow-up to merged #154): criterion 5 moves to **PROVEN** on the written
  terminal-state claim and the captain ruling. [Bounded TLC
  replay](../p5-004-session-refinement.md) over the frozen `GameSession` model produced two
  complete matching paths and 15 positive executions, including the observer-failure `Failed`
  path, plus a mutation control that changes only the failure arm's terminal and is rejected at
  tuple 4. The real Steam run observed non-degraded `Running` through `Stopping` to `Stopped` or
  `Cancelled` terminals; the packaged AppImage flows ended `Stopped` and `Cancelled`. The four
  intermediate-edge divergences are **ACCEPTED, DOCUMENTED IMPLEMENTATION EXTENSIONS**: cancel
  emits `Stopping` before `Cancelled`; cancel during running is `Stopping -> Cancelled`; a
  failed reap retries `Stopping -> Running`; and a zero-duration natural exit emits `Stopped` at
  playtime 0. The model was **not weakened** to accommodate them; these are implementation
  extensions at intermediate edges, not edits to `docs/formal/GameSession.tla`. Residual:
  bounded replay does not cover unbounded behaviour, and there is no packaged `Failed` witness.
  The summary is now **4 PROVEN / 0 PARTIAL / 2 DEFERRED**. v1 scope is satisfied **on the
  owner-decided scope**, with criteria 3 and 6 deferred to v1.1; **Phase 5 exit is the
  captain's call**, and no acceptance is written here.
- Addendum (follow-up to merged #156): criterion 6 moves to **PROVEN** on the packaged
  host-library evidence merged as [#156](https://github.com/seanburkes/playnite-linux/pull/156)
  at `c72614dda10fc9fa89995c2b58b41c37cecf2441` (final head
  `97b1fec46eae1df865019e289fb076384a6af45f`, 8/8 checks). The packaged AppImage read the real
  host Steam library directly and returned 83 apps. The installed Flatpak read the same library
  only through a user-granted `xdg-desktop-portal` document, with the manifest allowlist
  unchanged at `wayland`, `fallback-x11`, `ipc` and `dri`, no filesystem grant and no
  `talk-name` grant. The pre-grant negative control failed as required, the grant persisted by
  document id, and revocation is handled. Criterion 3 remains **DEFERRED BY OWNER DECISION** to
  v1.1. The summary is now **5 PROVEN / 0 PARTIAL / 1 DEFERRED**. v1 scope is satisfied **on the
  owner-decided scope**; **Phase 5 exit is the captain's call**, and no acceptance is written
  here.
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
| 1 | Synthetic + redacted real fixture yields the expected app set, paths and artwork ids | **PROVEN** | Merged [#152](https://github.com/seanburkes/playnite-linux/pull/152) at `74475aef9353c4a7459ca838d9c3736b1be5a189` (final head `8e4de0b916d071c11e38811edeed771b8d0b173d`, 9/9 checks, unfiltered Core suite 2460 passed with zero skipped, two reviews clean after the P2 classifier fix) adds a real-cache artwork identity manifest. `tests/fixtures/steam/real-artwork/manifest.tsv` records 31 files across app ids 219150, 770100, 975370, 1091500 and 1145360: 15 canonical identities (app id plus one of the five documented names) and 16 hashed-name entries. The manifest self-check is `d561743a972469c6dcd10e5a7a70ffce35297cffc6ae4723d5e6ee3eff16ac25`; no image bytes or thumbnails are committed. `RealArtworkIdentityTests` verifies the manifest and self-check, materialises an empty-file tree, asserts exactly the recorded 15 canonical identities and accepts the 16 hashed entries as untyped. The classifier requires the `.jpg` suffix on the content-addressed file form and exactly two segments on the content-addressed directory form. The synthetic and redacted-real evidence previously recorded under this criterion (`SteamDiscoveryTests`, `SteamRealCorpusDiscoveryTests`, `ArtworkTests`) remains in place. **Scope:** local identity only. Image bytes, artwork availability, byte equality and the Steam CDN/remote artwork mapping remain **UNKNOWN** and out of v1 scope. |
| 2 | A real Steam install launches and stops a native and a Proton game | **PROVEN** | The owner-authorized real Steam run recorded below launched and stopped native One Deck Dungeon (`770100`) and Proton Hades (`1145360`), and repeated both launches with cancellation. All four flows passed with a non-degraded stop report. The run's accepted head `649d25f` has the same tree as merged `d54e897`. |
| 3 | Inside Flatpak, `flatpak-spawn --host` launches Steam and tracking observes the game process | **DEFERRED BY OWNER DECISION** | ADR-020 defers Flatpak launch and tracking to v1.1. The criterion is neither proven nor failed. No `flatpak-spawn` implementation exists in `src/`, neither historical probe in Steam spec §6.4 was executed, and the p1-010 Flatpak spike was **inconclusive** for host launch and host PID visibility. No Flatpak acceptance run exists. |
| 4 | No orphaned processes after cancel/teardown on the v1 AppImage native and Proton paths | **PROVEN** | p5-004 (`064e261b`) provides real-process proofs through `LinuxProcessLauncher`: `Real_stop_leaves_no_child`, `Real_cancel_leaves_no_child`, `Real_tracker_disposal_leaves_no_child`, `Real_launcher_shutdown_leaves_no_child`, `Real_stop_during_starting_leaves_no_child`, plus process-group containment (`setsid` launch, group kill and verification) and explicit `ProcessSurvivorException` for tracked escapees. The reparent-before-first-snapshot test is on `main` (`5c4d11a`). p5-008 merged at `1e842c0` adds group-empty proof and per-identity pidfd signalling for its attributed group (`SteamLaunchTests`: stop/cancel/disposal reaping, escaped/unrelated groups never signalled, group reuse rejected). **Packaged proof:** merged #148 at `71f4578b2d574d345e08a6ac0045c47feeb40e2f` (final head `03ca0e930d2ec8adf2c08f263f05eae1b4bd7e8a`, 8/8 checks) mounted the v1 AppImage and ran four live flows: native One Deck Dungeon (`770100`) and Proton Hades (`1145360`), each stopped and each cancelled. Every flow ended `POST-TEARDOWN complete=True survivors=0 tracked=1 original_clients_retained=11/11`, `STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty`, and an independent same-user `/proc` observer printed zero app/group and fresh-runtime rows with the original Steam client still running. The harness issued one scoped game-identity pidfd `SIGTERM` per flow. No group, Steam client or helper signal was issued, and no orphan remained. The evidence is in `docs/port/p5-008-appimage-acceptance.md`; the mounted AppImage was built from clean `fa443d9730419758173a1e1033820b650b40f813` (46,471,672 bytes, SHA-256 `c216c50b8b809f9c810f879bcfb9f67840feedb9378f87135594dc52ad80317e`), with the CI AppImage at packaging run 44441401765, packaged-code linux-ci 44441401426, final-head linux-ci 44446797838 and merged-head main-push linux-ci 44449314928. The exact survivor-scan scope and its documented residual are stated under "Criterion 4 packaged AppImage evidence". |
| 5 | Session state on the v1 AppImage native and Proton paths matches the formal model's terminal states | **PROVEN** | The written terminal-state claim, accepted by the captain ruling, is that the terminal set is exactly `Stopped`, `Cancelled` and `Failed`, that no terminal state is emitted without a proven reap, and that the observed terminals match the formal model. `docs/formal/GameSession.tla` defines the state machine; the P4 gate records the TLC run (GameSession 1401 generated / 400 distinct / depth 27, log-backed in run 43998974777). Evidence merged as [#154](https://github.com/seanburkes/playnite-linux/pull/154) at `49302a43a1a366b56b6e576a0c3bd5e8a3c9b3dc`: bounded TLC replay over the frozen model produced **two complete matching paths** and **15 positive executions** across at most five snapshots each, including the observer-failure `Failed` path after a verified reap. A mutation control changes only the `StopKind.Failure` arm from `Failed` to `Stopped`; the replay rejects it at tuple 4 (exit 2), so a wrong terminal cannot pass. `GameSessionRefinementTests` runs in the required unfiltered Core suite. The real Steam rerun on the host observed non-degraded `Running` through `Stopping` to `Stopped` or `Cancelled`; the packaged AppImage native and Proton flows also ended `Stopped` or `Cancelled`. Four intermediate-edge divergences are **ACCEPTED, DOCUMENTED IMPLEMENTATION EXTENSIONS**: cancel emits `Stopping` before `Cancelled`; cancel during running is `Stopping -> Cancelled`; a failed reap retries `Stopping -> Running`; and a zero-duration natural exit emits `Stopped` at playtime 0. The model was **not weakened** to accommodate them. **Residual:** bounded replay does not cover unbounded behaviour, and there is no packaged `Failed` witness. [Mapping, bounds and counterexamples](../p5-004-session-refinement.md). |
| 6 | AppImage and Flatpak can read a host Steam library with narrow permissions | **PROVEN** | Merged [#156](https://github.com/seanburkes/playnite-linux/pull/156) at `c72614dda10fc9fa89995c2b58b41c37cecf2441` (final head `97b1fec46eae1df865019e289fb076384a6af45f`, 8/8 checks, both reviews clean) proves the read on both packaged formats. The packaged AppImage read the real host library directly: one library, 83 apps, root `~/.local/share/Steam`, with the expected 83-app set asserted. The installed Flatpak had no filesystem access and read the same 83 apps only through a user-granted `xdg-desktop-portal` `FileChooser`/`Documents` document. The manifest allowlist is unchanged at `wayland`, `fallback-x11`, `ipc` and `dri`; `packaging/verify-flatpak.py` is unchanged and accepts it exactly in CI. Effective permissions before and after the grant are `[Context] shared=ipc; sockets=fallback-x11;wayland; devices=dri;`: no `filesystem` grant and no `talk-name` grant. The pre-grant negative control fails: the sandbox reports `HIDDEN ~/.local/share/Steam` and `HIDDEN .../steamapps`, the direct read exits 2 with `status=unavailable`, and no grant file exists. The grant persists by document id in `steam-portal-grants.json` (0600, atomic replace) and is revalidated every run, so a revoked or deleted document is dropped and prompted for again. **Scope:** read/discovery only. No Steam launch, no session tracking and no host command; `flatpak-spawn --host` is not used. The unmounted external library `/run/media/bazzite/My Passport/SteamLibrary` was dismissed and recorded as `skipped (selection cancelled)`, contributing no apps and no grant. Criterion 3 is separate and stays deferred. |

Summary: **5 PROVEN / 0 PARTIAL / 1 DEFERRED.** Criteria 1, 2, 4, 5 and 6 are PROVEN. Criterion 3
is **DEFERRED BY OWNER DECISION** to v1.1. v1 scope is satisfied **on the owner-decided scope**.
**Phase 5 exit is the captain's call**: this assessment does not announce exit and writes no
acceptance. Criterion 1 is claimed for local artwork identity only; image bytes and the Steam
CDN/remote mapping remain UNKNOWN. The real Steam launch evidence applies to the host install.
Criterion 6 is a packaged read/discovery claim only: no Steam launch, no session tracking and no
host command from Flatpak. Criterion 5 is bounded: unbounded behaviour is not covered by bounded
replay, and there is no packaged `Failed` witness. No criterion is silently dropped.

## Criterion 1 real artwork identity evidence

Merged [#152](https://github.com/seanburkes/playnite-linux/pull/152) at squash commit
`74475aef9353c4a7459ca838d9c3736b1be5a189` added a read-only capture of the real Linux Steam
cache under the host Steam root's `appcache/librarycache`. The final reviewed head
`8e4de0b916d071c11e38811edeed771b8d0b173d` passed all nine checks, and its pull-request
linux-ci run
[44458722530](https://github.com/seanburkes/playnite-linux/actions/runs/44458722530) succeeded
on that exact head. The unfiltered Core suite passed 2460 tests with zero skipped, and both
adversarial reviews were clean after the P2 classifier fix.

`tests/fixtures/steam/real-artwork/manifest.tsv` records 31 files for app ids 219150, 770100,
975370, 1091500 and 1145360: 15 canonical identities (the app id plus one of the five
documented names) and 16 content-addressed hashed-name entries. App `1091500` is the
hashed-name-only case with no canonical name directly under its directory. The manifest stores
only app ids, paths relative to the Steam root, byte sizes and SHA-256 digests; its self-check
is `d561743a972469c6dcd10e5a7a70ffce35297cffc6ae4723d5e6ee3eff16ac25`. **No image bytes or
derived thumbnails are committed**, and the cache was only read.

`ArtworkIdentity.TryReadCachePath` classifies a captured path against a caller-supplied known
app set. `RealArtworkIdentityTests` verifies the manifest and its self-check, materialises an
empty-file tree from the manifest, asserts exactly the 15 canonical identities, accepts the 16
hashed entries as untyped, and rejects unknown names, unknown app ids, malformed paths and
case/extension variants. The review fix requires the `.jpg` suffix on the content-addressed
file form and exactly two segments on the content-addressed directory form, so a bare 40-hex
name and a hashed directory with extra children are rejected. The test performs no network
I/O, never reads the real cache, and has no skips.

**Scope:** this proves local artwork identity only. Image bytes, their content, artwork
availability, byte equality, and any Steam CDN or remote artwork id/mapping remain **UNKNOWN**
and are out of v1 scope. The product still resolves artwork only from explicitly configured
sources and may not derive a URL, remote id or fallback from this capture.

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

## Criterion 4 packaged AppImage evidence

Merged [#148](https://github.com/seanburkes/playnite-linux/pull/148) squash commit
`71f4578b2d574d345e08a6ac0045c47feeb40e2f` exposed the existing p5-008 acceptance harness
through the AppImage's explicit `--steam-acceptance` flag. The final reviewed head
`03ca0e930d2ec8adf2c08f263f05eae1b4bd7e8a` passed all eight checks, and the merged commit's
main-push linux-ci run [44449314928](https://github.com/seanburkes/playnite-linux/actions/runs/44449314928)
succeeded on that exact head. The packaged-code head `4eaf1777` passed linux-ci
[44441401426](https://github.com/seanburkes/playnite-linux/actions/runs/44441401426); the
final head's pull-request linux-ci run
[44446797838](https://github.com/seanburkes/playnite-linux/actions/runs/44446797838) passed.
The earlier relative-argv packaged rerun used the CI AppImage from packaging run
[44441401765](https://github.com/seanburkes/playnite-linux/actions/runs/44441401765); the
downloaded CI artifact was produced by packaging run
[44431326813](https://github.com/seanburkes/playnite-linux/actions/runs/44431326813). The
mounted image behind the final four flows was built locally from clean
`fa443d9730419758173a1e1033820b650b40f813` with `packaging/assemble.sh` and the same pinned
appimagetool and type2-runtime as CI: 46,471,672 bytes, SHA-256
`c216c50b8b809f9c810f879bcfb9f67840feedb9378f87135594dc52ad80317e`. It reported version
`1.0.0.0` and passed headless smoke and dry-run manifest inspection before the live run.
Provenance, build commands and per-flow observations are in
`docs/port/p5-008-appimage-acceptance.md`.

The live command exited 0 on the signed-in Steam desktop with no login, prompt or window
interaction. Executable paths are relative to the Steam library.

| Flow | Terminal state | PID / start ticks / PGID | Attributed game path | Elapsed |
| --- | --- | --- | --- | ---: |
| Native `770100` stop | `Stopped` | `3438401 / 235240530 / 3438368` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 4.6 s |
| Native `770100` cancel | `Cancelled` | `3449100 / 235242962 / 3449067` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 3.1 s |
| Proton `1145360` stop | `Stopped` | `3459391 / 235245795 / 3459391` | `steamapps/common/Hades/x64/Hades.exe` | 7.4 s |
| Proton `1145360` cancel | `Cancelled` | `3470661 / 235248553 / 3470661` | `steamapps/common/Hades/x64/Hades.exe` | 6.6 s |

Each flow produced one complete post-teardown scan with zero survivors and a non-degraded
stop report, and retained all 11 original Steam identities:

```text
SURVIVOR SCAN baseline_live=488 post_live=497 complete=True survivors=0
SURVIVOR SCAN baseline_live=495 post_live=496 complete=True survivors=0
SURVIVOR SCAN baseline_live=494 post_live=495 complete=True survivors=0
SURVIVOR SCAN baseline_live=495 post_live=495 complete=True survivors=0

AUDIT post-teardown app=770100 group=3438368 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT post-teardown app=770100 group=3449067 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT post-teardown app=1145360 group=3459391 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT post-teardown app=1145360 group=3470661 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT final packaged_exit=0 flows=4 clean_tables=True
```

The harness sent exactly one pidfd `SIGTERM` per flow to the attributed game identity. No
group, Steam client, or helper process received a signal. The independent same-user `/proc`
observer retained the original Steam client `122666 / 6590265 / 121295` in every sample and
printed the filtered post-teardown row counts above.

### Survivor-scan scope and residual

The claim rests on the read-only `SteamAcceptanceSurvivorScan`, which classifies a live
process from a complete post-disposal process-table snapshot as a survivor only when it is
one of:

1. in the attributed game's process group, or a tracked game identity (PID plus start ticks);
2. a process whose executable path or any argv path resolves under the selected game's
   install directory or Proton prefix. Absolute paths go through `realpath`; Windows drive
   paths go through the game's own `pfx/dosdevices/<drive>:` mapping; relative paths are
   resolved lexically against that process's own `/proc/PID/cwd`, including when the target
   no longer exists;
3. a process absent from the complete pre-launch snapshot whose name, executable or
   `argv[0]` is a rule-based Wine/Proton helper name (`wine`, `wine64`, `wine-preloader`,
   `wine64-preloader`, `wineserver`, `wineserver64`, `winedevice`, `services`, `explorer`,
   `rpcss`, `plugplay`, `conhost`, `rundll32`, with or without an `.exe` suffix).

**Residual:** a live process that leaves the game group, is not a tracked identity, carries no
install-directory or prefix path in its executable or argv, and has a name outside that
helper list is outside the scan's scope and is **not claimed** to have been detected. The
scan observes and reports only; it has no signalling path. The regression tests cover a
daemonized wineserver in its own group with no observed ancestry, PID reuse, executable and
argv path evidence, and a detached worker whose relative argv target was unlinked.

## Criterion 5 bounded refinement evidence

Merged [#154](https://github.com/seanburkes/playnite-linux/pull/154) squash commit
`49302a43a1a366b56b6e576a0c3bd5e8a3c9b3dc` adds the bounded refinement evidence described in
[`docs/port/p5-004-session-refinement.md`](../p5-004-session-refinement.md). The validated
implementation head `62e0aeb72e4161333ee0ac0563359f18f1158248` passed linux-ci run
[44462845464](https://github.com/seanburkes/playnite-linux/actions/runs/44462845464), and the
unfiltered Core suite passed 2468 tests with zero skipped.

The claim is the written terminal-state claim accepted by the captain ruling: the terminal set
is exactly `Stopped`, `Cancelled` and `Failed`; no terminal state is emitted without a proven
reap; and the observed terminal states match the formal model. Bounded TLC replay over the
frozen `GameSession` model contributes:

- **two complete matching paths**, with every synchronous `StateChanged` event and both public
  endpoints checked against the generated trace table;
- **15 positive executions** over at most five snapshots each, covering natural exit and the
  observer-failure case, with no randomness or wall-clock sleeps;
- the **observer-failure `Failed` path**: a scripted observer exception followed by a verified
  reap executes `StopCoreAsync(Failure)` and emits `Failed`;
- a **mutation control** that changes only the production `StopKind.Failure` arm from `Failed`
  to `Stopped`; the replay fails at tuple 4 (zero-based), so the check bites on a wrong terminal
  rather than passing a plausible result;
- the real observed terminals: the 2026-11-18 host Steam run reached `Running` through
  `Stopping` to `Stopped` or `Cancelled` with a non-degraded stop report, and the packaged
  AppImage native and Proton flows ended `Stopped` or `Cancelled`.

### Accepted implementation extensions

The replay identifies four intermediate-edge divergences. They are **ACCEPTED, DOCUMENTED
IMPLEMENTATION EXTENSIONS**, not model weakening:

| Input | Implementation edge | Why it is accepted |
| --- | --- | --- |
| Cancel before the first observation | `Starting -> Stopping -> Cancelled` | Cancellation reaps before it reports a terminal. `CancelLaunch` in the model goes directly from `Starting` to `Cancelled`. |
| Cancel during a running snapshot | `Stopping -> Cancelled` | The tracker emits `Stopping` at the start of the reap and the terminal only after the reap proves the tree gone. |
| All manual reap attempts fail | `Stopping -> Running` with a still-live process | A failed reap is a degradation, not a terminal, and the tracker permits retry. |
| Natural exit at zero elapsed time | `Stopping/TRUE/0 -> Stopped/FALSE/0` | A confirmed natural exit is a legitimate `Stopped` even when no whole second elapsed; `StopOk` otherwise requires playtime to increase by one. |

`docs/formal/GameSession.tla` was not edited, and no specification or test was relaxed to make
the replay pass. The divergences are at intermediate edges; the terminal states and the
no-terminal-without-a-proven-reap rule are unchanged.

**Residual:** bounded replay does not cover unbounded behaviour, general elapsed times, retry
schedules, concurrent callers, two active games, repeated sessions, native handle disposal or
liveness. The packaged AppImage run contains no `Failed` witness; `Failed` is proven by the
scripted observer fault with a verified reap, not by a packaged run.

## Criterion 6 packaged host-library evidence

Merged [#156](https://github.com/seanburkes/playnite-linux/pull/156) squash commit
`c72614dda10fc9fa89995c2b58b41c37cecf2441` adds the read/discovery half of p5-008. The final
reviewed head `97b1fec46eae1df865019e289fb076384a6af45f` passed all eight checks and both
adversarial reviews were clean. Its pull-request linux-ci run
[44591824472](https://github.com/seanburkes/playnite-linux/actions/runs/44591824472) succeeded on
that exact head with Core 2481/2481, Architecture 199/199, Smoke 4/4 and zero failed or skipped
tests; the packaging run
[44591824470](https://github.com/seanburkes/playnite-linux/actions/runs/44591824470) also
succeeded on that head with both AppImage and Flatpak smoke jobs. The merged commit's main-push
linux-ci run
[44596377243](https://github.com/seanburkes/playnite-linux/actions/runs/44596377243) succeeded on
`c72614d`.

The AppImage and Flatpak bundles were assembled from that head and installed from those bundles:
`Playnite-Linux-x86_64.AppImage` SHA-256
`881b7d5d1e7ebd71b2957bf93cea5e101c118e40e706a746310088698af00f21` and
`Playnite-Linux-x86_64.flatpak` SHA-256
`19af03a7c31320ce922ed32599dba13a9c451433a057b6c811debfa6435d9c56`, with
`source-commit.txt` = `97b1fec46eae1df865019e289fb076384a6af45f`.

| Path | Result |
| --- | --- |
| Packaged AppImage, direct read of the real host library | `complete`, one library, **83 apps**, root `~/.local/share/Steam`; the expected 83-app set is asserted. |
| Installed Flatpak, before any grant | Sandbox reports `HIDDEN ~/.local/share/Steam` and `HIDDEN .../steamapps`; the direct read exits 2 with `status=unavailable` and no grant file exists. This is the negative control. |
| Installed Flatpak, after granting the primary `steamapps` through the portal | `status=partial`, **83 apps**, identical to the AppImage set. The declared external library `/run/media/bazzite/My Passport/SteamLibrary` was not mounted; its chooser was dismissed and it is recorded as `skipped (selection cancelled)`. |
| Installed Flatpak, second run | Reuses the persisted grant by document id without prompting for the primary library (it still prompts for the unmounted external library) and returns the same 83 apps. |

The manifest allowlist is unchanged at `wayland`, `fallback-x11`, `ipc` and `dri`.
Effective permissions before and after the grant are identical:
`[Context] shared=ipc; sockets=fallback-x11;wayland; devices=dri;` — no `filesystem` and no
`talk-name`. Local Flatpak 1.18.4 prints `fallback-x11` without expanding `x11`; the CI packaging
job on Flatpak 1.14.6 validates the declared allowlist with the unchanged
`packaging/verify-flatpak.py`.

The portal route is `FileChooser.OpenFile` (`directory=true`) for one `steamapps` directory at a
time. The returned document is readable only through the portal mount, and each declared
`libraryfolders.vdf` library is rejected unless `Documents.GetHostPaths` reports exactly
`<declared root>/steamapps` (`Documents.Info` is refused for sandboxed callers). The grant is
stored in `steam-portal-grants.json` (mode 0600, atomic replace) and revalidated on every run; a
revoked, deleted or unmounted document no longer resolves, so the grant is dropped and the user
is asked to select that directory again. Unit coverage uses a scripted `ISteamLibraryPortal` fake
only: no session bus, no network and no skips. Design and non-claims are in
[`docs/port/p5-008-host-library-portal.md`](../p5-008-host-library-portal.md); the unit evidence is
`docs/port/units/p5-008.ledger.d/156-host-library-portal.tsv`.

**Residual:** this is a discovery claim for the Steam library selected on one host: the 83-app
set, the persisted grant and the negative control are recorded for that install, and the
unmounted external library was skipped rather than read. Criterion 6 says nothing about launching
Steam or tracking a game process; criterion 3 remains deferred and no `flatpak-spawn --host` or
host-command authority is present.

## Unit evidence relied on

| Unit | Merged commit | Merged-head linux-ci (main push) |
| --- | --- | --- |
| p5-001 | `777578cc81efb0130b41924c282542f0b346836c` (#109) | [44190373018](https://github.com/seanburkes/playnite-linux/actions/runs/44190373018) on `777578c`, success. |
| p5-002 | `c7b25dc7fa39238ab8f6fc47b9b21a4237e3ddb0` (#110), with follow-ups `f8aed2fe6b29918fbe6bc29dd5f3b54a7af6fcee` (#112), `c057be0f3e548ac31ac515b6da7ad4c606d17916` (#115) and `a37890f33110c70b8411bac4457d16e0aa41822f` (#116) | [44323525396](https://github.com/seanburkes/playnite-linux/actions/runs/44323525396) on the merged head `a37890f`, success; earlier merged-head runs 44200516358, 44203797675 and 44244649688 also succeeded. |
| p5-002 real-corpus discovery | `f82c3d46ae122098228dc34349fbd955cafa8177` ([merged change](https://github.com/seanburkes/playnite-linux/pull/146)) | [44429254978](https://github.com/seanburkes/playnite-linux/actions/runs/44429254978) on the merged head `f82c3d46`, success. |
| p5-003 | `22f051a92b6b5a82566fd382a04f9848c30ed756` (#118) | [44344905055](https://github.com/seanburkes/playnite-linux/actions/runs/44344905055) on `22f051a`, success. |
| p5-003 real-artwork identity | `74475aef9353c4a7459ca838d9c3736b1be5a189` ([merged change](https://github.com/seanburkes/playnite-linux/pull/152)) | [44460030207](https://github.com/seanburkes/playnite-linux/actions/runs/44460030207) on the merged head `74475ae`, success. The final reviewed head `8e4de0b` passed all nine pull-request checks, including its linux-ci run [44458722530](https://github.com/seanburkes/playnite-linux/actions/runs/44458722530). |
| p5-005a | `785b89200666ab63f07ca2fd1af4e757c3ef1e36` (#119) | [44344964418](https://github.com/seanburkes/playnite-linux/actions/runs/44344964418) on `785b892`, success. |
| p5-004 | `064e261b5bffa6c434515b9851160a21fd0e317d` (#120) | [44345002684](https://github.com/seanburkes/playnite-linux/actions/runs/44345002684) on `064e261`, success. |
| p5-004 bounded refinement | `49302a43a1a366b56b6e576a0c3bd5e8a3c9b3dc` ([merged change](https://github.com/seanburkes/playnite-linux/pull/154)) | [44467553176](https://github.com/seanburkes/playnite-linux/actions/runs/44467553176) on the merged head `49302a4`, success. The validated implementation head `62e0aeb72e4161333ee0ac0563359f18f1158248` passed linux-ci [44462845464](https://github.com/seanburkes/playnite-linux/actions/runs/44462845464). |
| p5-007a | `60e71ca950a21b72a08165e60c0e8066905ffed3` (#121) | [44345049906](https://github.com/seanburkes/playnite-linux/actions/runs/44345049906) on `60e71ca`, success. |
| p5-008 | `1e842c075aeb3412323fcd467ca93aaaef03b8cb` (#126) | [44366637238](https://github.com/seanburkes/playnite-linux/actions/runs/44366637238) on `1e842c0`, success. The pull-request run [44365780060](https://github.com/seanburkes/playnite-linux/actions/runs/44365780060) also succeeded on the verified pre-merge head `23e0797b`. The packaged AppImage follow-up merged at `71f4578b2d574d345e08a6ac0045c47feeb40e2f` (#148, final head `03ca0e93`); its main-push run [44449314928](https://github.com/seanburkes/playnite-linux/actions/runs/44449314928) succeeded on `71f4578`. |
| p5-008 host-library portal | `c72614dda10fc9fa89995c2b58b41c37cecf2441` ([merged change](https://github.com/seanburkes/playnite-linux/pull/156)) | [44596377243](https://github.com/seanburkes/playnite-linux/actions/runs/44596377243) on the merged head `c72614d`, success. The final reviewed head `97b1fec` passed all eight pull-request checks, including linux-ci [44591824472](https://github.com/seanburkes/playnite-linux/actions/runs/44591824472) and packaging [44591824470](https://github.com/seanburkes/playnite-linux/actions/runs/44591824470) on that exact head. |

The tooling row #122 (`p2-evidence2`, merge `7ea3a3752dca413fae3aac8e2e869678d2375c37`) had a
successful main-push linux-ci run [44345093914](https://github.com/seanburkes/playnite-linux/actions/runs/44345093914);
it is not a Phase 5 desktop unit and contributes no exit evidence.

These are merged-head main-push runs, not pull-request head runs. They do not measure this
documentation change and are not Phase 5 exit evidence by themselves.

## What would close a row

1. **Closed by the merged real artwork identity evidence above.** The manifest proves the
   local artwork identities observed in a real Linux Steam cache, including the hashed-name
   forms, without committing image bytes. Image bytes, artwork availability, byte equality and
   Steam CDN/remote mapping remain unknown and are out of v1 scope.
2. Proven by the native and Proton launches, stops, cancellations, and process-to-session
   attribution recorded above.
3. In v1.1, a Flatpak acceptance run that launches host Steam and observes the game process in
   the host PID namespace, using a transport whose authority is explicitly approved.
4. **Closed by the packaged evidence above.** The mounted AppImage proved no orphan after
   native and Proton stop and cancel, with a post-teardown process table showing no remaining
   game, group, fresh runtime or prefix process and the original Steam client still running.
   The claim is limited to the survivor-scan scope stated above; a process outside that scope
   is not claimed.
5. **Closed by the merged bounded refinement evidence above.** Bounded TLC replay over the
   frozen `GameSession` model matches two complete paths and 15 positive executions, covers
   `Failed` through a scripted observer fault with a verified reap, and rejects a wrong-terminal
   mutation at tuple 4; the real host and packaged flows observed `Running -> Stopping ->
   Stopped/Cancelled`. The four intermediate-edge divergences are accepted, documented
   implementation extensions and the model was not weakened. This does not cover unbounded
   behaviour and does not provide a packaged `Failed` witness.
6. **Closed by the merged portal evidence above.** The packaged AppImage read the real host
   Steam library directly (83 apps), and the installed Flatpak read the same 83 apps only
   through a user-granted `xdg-desktop-portal` document with the manifest allowlist unchanged at
   `wayland`, `fallback-x11`, `ipc` and `dri`, no filesystem or talk-name grant, and a pre-grant
   negative control that fails. The read is discovery only: no host launch, no session tracking
   and no host command. Criterion 3 remains deferred and is unaffected by this evidence.

## Non-claims

- Phase 5 is not accepted. Criterion 3 is deferred by owner decision, and Phase 5 exit is the
  captain's call. Criterion 5 is PROVEN only on the written terminal-state claim accepted by the
  captain ruling; its four intermediate-edge extensions are documented, and the model was not
  weakened for them. No acceptance is written here.
- Criterion 6 is a packaged read/discovery claim only. It records the 83-app set of the selected
  host Steam library, the portal-granted Flatpak read with the narrow allowlist, and the failing
  pre-grant negative control. It claims no Flatpak Steam launch, no session tracking, no host
  command and no read of the unmounted external library; none of those is covered.
- Criterion 5 is claimed only within bounded TLC replay and the observed host and packaged
  terminals. Unbounded behaviour, general elapsed times, retry schedules, concurrent callers,
  two active games, repeated sessions, native handle disposal and liveness are not covered, and
  there is no packaged `Failed` witness.
- Criterion 1 is claimed for local artwork identity only: the manifest proves observed local
  names and app-directory layout on one Linux host. It claims no image bytes, no artwork
  availability, no byte equality and no Steam CDN or remote artwork mapping; all of those
  remain UNKNOWN and out of v1 scope.
- The 2026-11-18 real-machine run is evidence for criterion 2. This assessment records no
  owner acceptance of the install or either game, and no Flatpak host launch or tracking.
- Criterion 4 is claimed only within the survivor-scan scope stated above: game group and
  tracked identities, install-directory or prefix paths (relative argv resolved lexically
  against the process cwd even when the target is gone), and rule-based Wine/Proton helper
  names. A process outside that scope is not claimed.
- No protected oracle, baseline, aggregate or product file is changed by this assessment.

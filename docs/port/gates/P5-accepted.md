# Gate P5 — Steam and Linux platform (accepted)

- Date: 2026-11-19
- Decision: **ACCEPTED by the captain on 2026-11-19.** This file records the captain's
  acceptance decision; it is not an agent self-assessment promoted to acceptance. The supporting
  evidence assessment remains [`P5-pending.md`](P5-pending.md), and the criteria are proven only
  as scoped below.
- Base: `c31b8a96d63da58921a0448f08295229cf2914d9` (`main`, merged
  [#158](https://github.com/seanburkes/playnite-linux/pull/158)).
- Owner: `p5-shards` (acceptance record); the decision owner is the captain.
- Review model: worker diff-only self-assessment of merged artifacts and unit shards, plus the
  named adversarial unit reviews recorded in the unit shards. This is not independent human
  approval; the ADR-024 single-owner exception applies. The acceptance is the captain's
  decision, not a reviewer signature.
- Result: **6 PROVEN / 0 PARTIAL / 0 DEFERRED on the v1 scope.** All six Phase 5 exit criteria
  have evidence on merged `main`. Criterion 3 is accepted on the **owner-approved launch-only
  scope**, not as the original `flatpak-spawn --host` host-tracking scope. The v1 scope is
  accepted with the residual list below; no residual is converted into a pass.

## Exit basis

Phase 5 exits on the owner-decided v1 scope:

- Criteria 1, 2, 4, 5 and 6 are proven as written by merged evidence.
- Criterion 3 is proven as the owner-approved **adjusted launch-only scope**: the shipped
  `playnite launch <appid>` opens Steam inside Flatpak through the desktop
  `org.freedesktop.portal.OpenURI` route, reports tracking **UNKNOWN** and leaves the game to be
  stopped in Steam. Flatpak process tracking is deferred to v1.1.
- AppImage remains the only Steam-integrated packaging target whose launch and session tracking
  are claimed for v1. The Flatpak ships the launch-only route and the host-library read, not
  host process tracking or host-command authority.
- `p5-006` WebView stays deferred to v1.1 by ADR-019 and is not one of the six Phase 5 exit
  criteria.

## Owner decisions recorded

| Decision | Date | Effect on v1 |
| --- | --- | --- |
| Flatpak Steam launch is **launch-only through OpenURI** | 2026-11-19 (ADR-020 amendment, merged in [#158](https://github.com/seanburkes/playnite-linux/pull/158)) | The v1 Flatpak surface is the shipped CLI `playnite launch <appid>`. It validates a decimal `uint` app id and passes one argv vector, `/usr/bin/xdg-open steam:&#47;&#47;rungameid/<appid>`, to the desktop portal. It reports tracking UNKNOWN and exposes no playtime, stop or signal surface. The desktop GUI play action is deferred. |
| **Flatpak process tracking is deferred to v1.1** | 2026-11-19 (ADR-020 amendment) | The original criterion 3 wording, `flatpak-spawn --host` launching Steam and tracking the game process, is **not** proven. v1 has no `flatpak-spawn`, no host-command authority and no host helper service. A v1.1 host helper, if demanded, must be minimal and audited. |
| **Artwork is local-identity only** | v1 scope, restated in the criterion 1 evidence | The real-cache manifest proves local artwork identities, names and app-directory layout. Image bytes, artwork availability, byte equality and any Steam CDN or remote artwork mapping remain **UNKNOWN** and out of v1 scope. The product may not derive a URL, remote id or fallback from the capture. |

## Criteria

| # | Phase 5 exit criterion | Verdict | Merged evidence and scope |
| --- | --- | --- | --- |
| 1 | Synthetic + redacted real fixture yields the expected app set, paths and artwork ids | **PROVEN** | [#152](https://github.com/seanburkes/playnite-linux/pull/152) at `74475aef9353c4a7459ca838d9c3736b1be5a189` adds the real-cache artwork identity manifest. `tests/fixtures/steam/real-artwork/manifest.tsv` records 31 files across five app ids: 15 canonical identities and 16 hashed-name entries, with self-check `d561743a972469c6dcd10e5a7a70ffce35297cffc6ae4723d5e6ee3eff16ac25` and no image bytes. `RealArtworkIdentityTests` verifies the manifest and classifier, and the synthetic plus redacted-real discovery tests remain in place. **Scope:** local identity only; CDN/remote mapping is out of v1. |
| 2 | A real Steam install launches and stops a native and a Proton game | **PROVEN** | The owner-authorized real Steam run launched and stopped native One Deck Dungeon (`770100`) and Proton Hades (`1145360`), then repeated both with cancellation. All four flows passed with a non-degraded stop report; the accepted run head `649d25f` has the same tree as merged `d54e897`. |
| 3 | Inside Flatpak, `flatpak-spawn --host` launches Steam and tracking observes the game process | **PROVEN on the owner-approved launch-only scope** | [#158](https://github.com/seanburkes/playnite-linux/pull/158) at `c31b8a96d63da58921a0448f08295229cf2914d9` (final head `161882d5a5c4f1be747bd5c3c23ff1e7400a069f`; evidence head `ca96207604ba4831842344212f66f24c33b533a2`; executable head `ddc86161683ffd1aae707cb675c9dcf475dea0cc`). The packaged Flatpak launched native and Proton titles through the shipped `playnite launch`, with the allowlist unchanged at `wayland`, `fallback-x11`, `ipc` and `dri` and no filesystem or `talk-name` grant. The packaged AppImage ran the same product command and the unchanged tracked native/Proton flows. **The original `flatpak-spawn --host` tracking wording is not proven**; Flatpak tracking is deferred to v1.1. |
| 4 | No orphaned processes after cancel/teardown on the v1 AppImage native and Proton paths | **PROVEN within the stated survivor-scan scope** | [#148](https://github.com/seanburkes/playnite-linux/pull/148) at `71f4578b2d574d345e08a6ac0045c47feeb40e2f` mounted the v1 AppImage and ran four live flows, each ending `POST-TEARDOWN complete=True survivors=0 tracked=1 original_clients_retained=11/11` with a non-degraded stop report and zero app/group or fresh-runtime rows in the independent observer. The read-only survivor scan scope and its residual are stated in [`P5-pending.md`](P5-pending.md). |
| 5 | Session state on the v1 AppImage native and Proton paths matches the formal model's terminal states | **PROVEN within the bounded claim** | [#154](https://github.com/seanburkes/playnite-linux/pull/154) at `49302a43a1a366b56b6e576a0c3bd5e8a3c9b3dc` adds bounded TLC replay over the frozen `GameSession` model: two complete matching paths, 15 positive executions, the observer-failure `Failed` path after a verified reap, and a wrong-terminal mutation rejected at tuple 4. The real host and packaged flows observed `Running -> Stopping -> Stopped/Cancelled`. Four intermediate-edge divergences are accepted, documented implementation extensions; `docs/formal/GameSession.tla` was not weakened. |
| 6 | AppImage and Flatpak can read a host Steam library with narrow permissions | **PROVEN** | [#156](https://github.com/seanburkes/playnite-linux/pull/156) at `c72614dda10fc9fa89995c2b58b41c37cecf2441` read the real host library: the packaged AppImage read 83 apps directly, and the installed Flatpak read the same 83 apps only through a user-granted `xdg-desktop-portal` document with the allowlist unchanged at `wayland`, `fallback-x11`, `ipc` and `dri`, no filesystem grant and no `talk-name` grant. The pre-grant negative control fails, the grant persists by document id and revocation is handled. **Scope:** read/discovery only; no launch, tracking or host command. |

## Merged commits

| PR | Merge commit | Unit / scope |
| --- | --- | --- |
| [#109](https://github.com/seanburkes/playnite-linux/pull/109) | `777578cc81efb0130b41924c282542f0b346836c` | p5-001 VDF/ACF parsers |
| [#110](https://github.com/seanburkes/playnite-linux/pull/110) | `c7b25dc7fa39238ab8f6fc47b9b21a4237e3ddb0` | p5-002 Steam root/library discovery |
| [#112](https://github.com/seanburkes/playnite-linux/pull/112) | `f8aed2fe6b29918fbe6bc29dd5f3b54a7af6fcee` | p5-002 follow-up (four blocking P2s) |
| [#115](https://github.com/seanburkes/playnite-linux/pull/115) | `c057be0f3e548ac31ac515b6da7ad4c606d17916` | p5-002 follow-up (realpath/handle P2s) |
| [#116](https://github.com/seanburkes/playnite-linux/pull/116) | `a37890f33110c70b8411bac4457d16e0aa41822f` | p5-002 follow-up (candidate contract) |
| [#118](https://github.com/seanburkes/playnite-linux/pull/118) | `22f051a92b6b5a82566fd382a04f9848c30ed756` | p5-003 artwork/metadata cache |
| [#119](https://github.com/seanburkes/playnite-linux/pull/119) | `785b89200666ab63f07ca2fd1af4e757c3ef1e36` | p5-005a XDG/single-instance/protocol |
| [#120](https://github.com/seanburkes/playnite-linux/pull/120) | `064e261b5bffa6c434515b9851160a21fd0e317d` | p5-004 launcher/session tracker |
| [#121](https://github.com/seanburkes/playnite-linux/pull/121) | `60e71ca950a21b72a08165e60c0e8066905ffed3` | p5-007a tray/notifications/sleep |
| [#126](https://github.com/seanburkes/playnite-linux/pull/126) | `1e842c075aeb3412323fcd467ca93aaaef03b8cb` | p5-008 launch native/Proton |
| [#141](https://github.com/seanburkes/playnite-linux/pull/141) | `d54e897ce6bae59dccc5d11d6a99a647e4572dd3` | p5-008 follow-up; same tree as accepted run head `649d25f` |
| [#148](https://github.com/seanburkes/playnite-linux/pull/148) | `71f4578b2d574d345e08a6ac0045c47feeb40e2f` | p5-008 packaged AppImage teardown |
| [#152](https://github.com/seanburkes/playnite-linux/pull/152) | `74475aef9353c4a7459ca838d9c3736b1be5a189` | p5-003 real artwork identity |
| [#154](https://github.com/seanburkes/playnite-linux/pull/154) | `49302a43a1a366b56b6e576a0c3bd5e8a3c9b3dc` | p5-004 bounded session refinement |
| [#156](https://github.com/seanburkes/playnite-linux/pull/156) | `c72614dda10fc9fa89995c2b58b41c37cecf2441` | p5-008 host-library portal read |
| [#158](https://github.com/seanburkes/playnite-linux/pull/158) | `c31b8a96d63da58921a0448f08295229cf2914d9` | p5-008 Flatpak launch-only through OpenURI |

Every merge commit above was read from the forge and is an ancestor of the acceptance base
`c31b8a9`. The citation for each criterion is the merged commit on `main`, not an unmerged
pull-request head. The #158 final, evidence and executable heads are named separately because
the acceptance artifacts and their linux-ci runs were produced before the final docs-only
commits.

## Residual list

1. **Flatpak process tracking is deferred to v1.1.** Criterion 3 is accepted only as the
   launch-only scope. The original `flatpak-spawn --host` wording is not proven. v1 has no
   `flatpak-spawn`, no host-command authority and no host service. A future host helper is v1.1
   work and would require explicit authority review.
2. **Flatpak launch is not a tracked start.** `RequestAccepted` means the portal opener exited 0,
   not that the game started. The Flatpak branch reports tracking UNKNOWN and has no playtime,
   stop or signal surface; the user stops the game in Steam. The desktop GUI play action remains
   deferred.
3. **Artwork is local-identity only.** Image bytes, content, artwork availability, byte equality
   and any Steam CDN or remote artwork id/mapping remain UNKNOWN and out of v1 scope.
4. **Criterion 5 is bounded.** Bounded TLC replay does not cover unbounded behaviour, general
   elapsed times, retry schedules, concurrent callers, two active games, repeated sessions,
   native handle disposal or liveness. There is no packaged `Failed` witness; `Failed` is proven
   by a scripted observer fault with a verified reap.
5. **Criterion 4 has a stated survivor-scan boundary.** A live process that leaves the game
   group, is not a tracked identity, carries no install-directory or prefix path in its
   executable or argv, and has a name outside the documented Wine/Proton helper list is outside
   the scan's scope and is not claimed to have been detected.
6. **Criterion 6 is one-host discovery.** The 83-app set, persisted grant and negative control
   are recorded for the selected host install. The unmounted external library was skipped, not
   read. The claim covers no launch, tracking or host command.
7. **Criterion 2 and criterion 4 are single-host real-machine evidence.** Games that outlast the
   graceful stop deadline still require manual Steam Stop, and the run did not verify launch
   options or offline mode.
8. **`p5-006` WebView remains deferred to v1.1 by ADR-019.** It is not one of the six Phase 5
   exit criteria and is not accepted here.
9. **Review independence.** ADR-024's single-owner exception applies: adversarial assistance is
   correlated and is not independent human approval. The acceptance is the captain's decision
   on the recorded evidence, not a claim of independent verification.

## Exit checklist

- [x] Criterion 1: synthetic and real-cache local artwork identity evidence is merged.
- [x] Criterion 2: real Steam native and Proton launch/stop/cancel evidence is merged.
- [x] Criterion 3: the owner-approved Flatpak launch-only scope is merged, with tracking
      explicitly deferred to v1.1, no host-command authority and no host service.
- [x] Criterion 4: packaged AppImage stop/cancel leaves no orphan within the stated scan scope.
- [x] Criterion 5: session terminals match the frozen model within the bounded refinement claim.
- [x] Criterion 6: AppImage and Flatpak read the host library with the allowlist unchanged.
- [x] The captain accepted the Phase 5 exit on 2026-11-19.
- [x] Phase 6 is the next phase.

## Decision

- [x] **Proceed to Phase 6 — Core services and SDK v7.**
- [ ] Hold: not applicable.

Accepted by the captain on **2026-11-19**. The six criteria, the owner decisions and the
residual list above are the basis of the acceptance. This record owns the decision; it does not
rewrite the evidence in [`P5-pending.md`](P5-pending.md).

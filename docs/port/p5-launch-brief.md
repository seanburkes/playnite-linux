# Phase 5 launch brief — Steam and Linux platform

- Date: 2026-11-18
- Board unit: `p5-shards`
- Base: `d758b93580627fb81bc0f22010c8eb0027a49677` (`main`)
- Scope: records the merged p5-001/p5-002 delivery and its follow-ups, and opens
  the next Phase 5 wave. This is a shard-board record, not a gate acceptance.
- Constraint: a pull request owns exactly one unit queue/evidence namespace, so
  this change edits only the `p5-shards` shards. The per-unit canonical queue
  rows (`p5-001`..`p5-008`) are not rewritten here; the tables below are the
  launch board's reconciled view of them. Their own namespace close-outs remain
  the canonical queue transitions.

## Delivered

| Unit | State | Merged evidence | Follow-up and limit |
| --- | --- | --- | --- |
| p5-001 VDF/ACF parsers | done | [#109](https://github.com/seanburkes/playnite-linux/pull/109) merged at `777578cc81efb0130b41924c282542f0b346836c` | Bounded pure parsers; no discovery, mount, launch, UI or network behaviour. |
| p5-002 Steam root/library discovery | done | [#110](https://github.com/seanburkes/playnite-linux/pull/110) merged at `c7b25dc7fa39238ab8f6fc47b9b21a4237e3ddb0` | Not a clean delivery: #110 merged before its four blocking P2 findings were closed. [#112](https://github.com/seanburkes/playnite-linux/pull/112) at `f8aed2fe6b29918fbe6bc29dd5f3b54a7af6fcee` closed those P2s and folded the P3s; [#115](https://github.com/seanburkes/playnite-linux/pull/115) at `c057be0f3e548ac31ac515b6da7ad4c606d17916` closed the realpath dot-dot and handle-bound read P2s; [#116](https://github.com/seanburkes/playnite-linux/pull/116) at `a37890f33110c70b8411bac4457d16e0aa41822f` closed the candidate-contract P2 and the remaining P3s. |

## Wave now starting

| Unit | State | Depends | Scope | Oracle / test |
| --- | --- | --- | --- | --- |
| p5-003 artwork/metadata cache | todo (starting) | p5-002 | Artwork ids and metadata fetch with a local HTTP stub; cache and rate limit. | http-stub; test:metadata |
| p5-004 launcher/session tracker | todo (pilot) | p2-002 | `IProcessLauncher` and `IGameSessionTracker`; cancellation at every await. | formal:GameSession; test:session |
| p5-005 XDG/single-instance/protocol | todo (starting) | p3-001 | XDG paths, single instance and the protocol handler. | smoke; test:platform |
| p5-007 tray/notifications/sleep | todo (starting) | p2-002 | Tray, notifications and sleep inhibition across the distro matrix. | smoke; test:platform |
| p5-008 launch native/Proton | held | p5-002, p5-004 | Launch native and Proton games, track start/stop and provide the manual stop fallback. Held until p5-004 lands. | manual; test:launch |

## Deferred

| Unit | State | Decision |
| --- | --- | --- |
| p5-006 WebView service | deferred | ADR-019 defers the WebView service to v1.1; v1 uses `Avalonia.HtmlRenderer` for non-interactive descriptions and plain HTTP for metadata, and the Phase 5 order in `docs/port/03-EXECUTION-PLAN.md` does not include WebView. |

## Merge ancestry

Each merge commit below was read from the forge and verified as an ancestor of
`main` at `d758b93` with `git merge-base --is-ancestor <merge-commit> d758b93`.
The cited evidence is the merged commit on `main`, not the pull-request head.

| PR | Merge commit | Unit | Ancestor of `d758b93` |
| --- | --- | --- | --- |
| [#109](https://github.com/seanburkes/playnite-linux/pull/109) | `777578cc81efb0130b41924c282542f0b346836c` | p5-001 | yes |
| [#110](https://github.com/seanburkes/playnite-linux/pull/110) | `c7b25dc7fa39238ab8f6fc47b9b21a4237e3ddb0` | p5-002 | yes |
| [#112](https://github.com/seanburkes/playnite-linux/pull/112) | `f8aed2fe6b29918fbe6bc29dd5f3b54a7af6fcee` | p5-002 follow-up | yes |
| [#115](https://github.com/seanburkes/playnite-linux/pull/115) | `c057be0f3e548ac31ac515b6da7ad4c606d17916` | p5-002 follow-up | yes |
| [#116](https://github.com/seanburkes/playnite-linux/pull/116) | `a37890f33110c70b8411bac4457d16e0aa41822f` | p5-002 follow-up | yes |

No product code, test, protected oracle or aggregate is changed by this brief.

# p5-008 — Flatpak Steam launch-only through OpenURI

- Unit: `p5-008`, `Port-Unit: p5-008`
- Date: 2026-11-19
- Scope: launch only. No host command, no `flatpak-spawn`, no manifest or permission change,
  no process tracking and no signal from the sandbox.

## Mode contract

`/.flatpak-info` selects the mode at one seam (`ISteamHostFormat` / `LinuxSteamHostFormat`):

| Host | Selected mode | Behaviour |
| --- | --- | --- |
| Flatpak (`/.flatpak-info` exists) | `FlatpakLaunchOnly` | Open one Steam URI through the desktop portal; report tracking UNKNOWN; stop in Steam. |
| Native install / AppImage | `Tracked` | Existing `SteamProcessLauncher` launch, attribution, playtime and stop path, unchanged. |

The Flatpak mode validates the app id as a strict decimal `uint` and builds exactly

```
/usr/bin/xdg-open <the §6.3 steam rungameid URI for the decimal app id>
```

as an argv vector. Signs, whitespace, hex, exponents, fractions, overflow and injected text are
rejected before any request is built. The sandbox `xdg-open` is the Flatpak portal client: it
forwards the URI to `org.freedesktop.portal.OpenURI` on the host. Nothing is resolved on the
host and no shell is started.

## Honest reporting

- `FlatpakLaunchStatus.RequestAccepted` means the opener exited 0. It is **not** proof that the
  game started; the detail string says so.
- Every result carries `Tracking = Unknown`. There is no playtime, stop or signal surface on
  the launch-only type, so it cannot claim a tracked start, a tracked stop or a playtime.
- `Rejected` (nonzero opener exit) and `Unavailable` (opener missing/permission failure) claim
  no launch.
- Cancellation propagates and releases only our waiter. A request that may have become a game
  is never signalled; the user stops it in Steam.

Native and AppImage keep the unchanged tracked path. The shipped product command is
`playnite launch <appid>`: Flatpak selects the OpenURI mode, while native/AppImage runs the
existing tracked launcher with a minimal console reporter (`Running -> Stopped/Cancelled`).
The desktop GUI play action remains deferred.

## Tests

`tests/Playnite.Core.Tests/p5-008/FlatpakSteamLaunchTests.cs`:

- marker-present and marker-absent mode selection through the probe seam;
- exact URI construction for `0`, `42` and `uint.MaxValue`;
- invalid ids: empty/whitespace, signs, leading/trailing spaces, hex, exponent, fraction,
  overflow, newline and injection strings;
- one absolute opener plus one URI argument, and `/bin/sh` rejection through the production
  `CreateRequest`/`LaunchAsync` path with the opener never consulted (the constructor accepts
  an absolute `/bin/sh`; `LaunchRequest` carries the shell guard);
- accepted, nonzero and unavailable opener outcomes all report UNKNOWN and a manual Steam stop;
- cancellation propagates with no stop/signal claim;
- the launch-only type exposes no stop, signal, track or playtime surface;
- a non-Flatpak host selects `Tracked` and `SteamProcessLauncher.CreateRequest()` still returns
  `steam -applaunch <appid>`.

The packaged `playnite launch <appid>` route dispatches to `Playnite.Launch`, which is published
into the payload by `packaging/assemble.sh` and routed by `packaging/playnite`. The Flatpak
manifest and `packaging/verify-flatpak.py` are unchanged.

## Packaging

The packaged `playnite launch <appid>` command dispatches to `Playnite.Launch` (published by
`packaging/assemble.sh` and routed by `packaging/playnite`). The Flatpak manifest and
`packaging/verify-flatpak.py` stay untouched, and the allowlist stays exactly `wayland`,
`fallback-x11`, `ipc` and `dri` with no `filesystem` or `talk-name` grant.

## Acceptance

The real-machine acceptance ran from clean code head
`d169fe719a4f2e493ca5db039c4be9c0b713dcc8` on 2026-11-19; the product launch surface was then
routed through the shipped `playnite launch <appid>` command. The packaged `Playnite.Launch` tool
reuses the same launch library, and the final head/run and packaged results are recorded below.

### Packaged Flatpak — launch-only

Artifacts:

| Artifact | Size | SHA-256 / id |
| --- | ---: | --- |
| `Playnite-Linux-x86_64-route-final.flatpak` | 36,338,640 bytes | `f9e8928444e59bffbffe34d1104d4133f9dbf6a0ad2cba4fa67060cd861283e8` |
| installed Flatpak OSTree commit | — | `fed6accb17bd1625257c2ae7405084b857e678fce37e51e72bc9148dfe88c4c7` |

The installed sandbox reported exactly:

```ini
[Context]
shared=ipc;
sockets=fallback-x11;wayland;
devices=dri;
```

The permission file was identical before install and after both launches: no `filesystems`
section and no `talk-name` grant. Inside the sandbox `/.flatpak-info` exists, the host Steam
directory is **hidden**, `xdg-open` is the Flatpak portal opener (1.0.6, `/usr/bin/xdg-open`),
and `flatpak-spawn --host true` fails with
`org.freedesktop.DBus.Error.ServiceUnknown` because there is no
`org.freedesktop.Flatpak` talk-name. The app never invokes it.

Commands and results:

```sh
flatpak run --user io.github.playnite_linux.Playnite launch 770100
flatpak run --user io.github.playnite_linux.Playnite launch 1145360
```

Both printed `status: RequestAccepted`, `tracking: unknown`, `stop: manual-steam`. The JSON
detail said game start and playtime are UNKNOWN and that the user must stop the game in Steam.

| Title | Selection | Launch evidence | Playnite tracking |
| --- | --- | --- | --- |
| native `770100` One Deck Dungeon | native/unknown (Steam owns selection) | host PID `2152718` started as `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | UNKNOWN; no Playnite process or handle remained |
| Proton `1145360` Hades | Proton (account override and prefix markers) | host PID `2165414` ran `S:\steamapps\common\Hades\x64Vk\Hades.exe` under Proton Experimental; Steam log recorded `gameID 1145360` | UNKNOWN; no Playnite process or handle remained |

The owner stopped each title outside Playnite with a manual `SIGTERM` to the game/runtime
process; the Flatpak mode sent no signal. Post-stop process tables contained no title or Wine
row. This is the product behaviour: Playnite cannot observe or stop the host game from the
sandbox.

### Packaged AppImage — tracked path unchanged

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Playnite-Linux-x86_64-route-final.AppImage` | 46,311,928 bytes | `4a15bd315dd074dbf41be08929bb5aab9f5e90a71726a22d9078d02fde4f9e26` |

The AppImage reported `1.0.0.0`, passed headless smoke and dry-run inspection, then ran the
unchanged tracked acceptance for native `770100` and Proton `1145360`:

| Flow | Terminal state | Attributed identity | Result |
| --- | --- | --- | --- |
| native stop | `Stopped` | pid `2198956`, start ticks `242414285`, pgid `2198923` | PASS, survivors 0, non-degraded |
| native cancel | `Cancelled` | pid `2207232`, start ticks `242416656`, pgid `2207199` | PASS, survivors 0, non-degraded |
| Proton stop | `Stopped` | pid `2218882`, start ticks `242419445`, pgid `2218882`; `Hades.exe` through Proton | PASS, survivors 0, non-degraded |
| Proton cancel | `Cancelled` | pid `2229580`, start ticks `242422227`, pgid `2229580`; `Hades.exe` through Proton | PASS, survivors 0, non-degraded |

Every flow printed `POST-TEARDOWN complete=True survivors=0 tracked=1
original_clients_retained=11/11` and `STOP REPORT degraded=False`. The tracked path therefore
still attributes, tracks and stops exactly as before; only the Flatpak branch changed.

The final `linux-ci` run for this PR head and the packaging workflow artifacts are cited in the
PR body and the unit ledger fragment. This unit does not claim Phase 5 exit.

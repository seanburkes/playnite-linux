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

The acceptance artifacts were built from `df2a97b2c5cbfb739a2a9dee993e57a31c4402ea`, which is
docs-only after the last executable head
`ddc86161683ffd1aae707cb675c9dcf475dea0cc`; the evidence commit after `df2a97b` is also
docs-only. The shipped route is `playnite launch <appid>`. The green linux-ci run on the
executable head is
[44631847661](https://github.com/seanburkes/playnite-linux/actions/runs/44631847661)
(Core 2518/2518, Architecture 199/199, Smoke 4/4, zero failures and zero skips); the final-head
run is cited in the PR body.

### Packaged Flatpak — launch-only

Artifacts:

| Artifact | Size | SHA-256 / id |
| --- | ---: | --- |
| `Playnite-Linux-x86_64-launch2.flatpak` | 36,597,712 bytes | `5d9f0fbf7d9d1f4bc8a554e27e2a3af42fc11bedd1b8d74f4aff7939e8f60eda` |
| installed Flatpak OSTree commit | — | `3043e0dbc767cf27c45c8f3e362214e7444e8ff5ffcef41b0df59cb8867e00aa` |

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
| native `770100` One Deck Dungeon | native/unknown (Steam owns selection) | host PID `3274643` started as `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | UNKNOWN; no Playnite process or handle remained |
| Proton `1145360` Hades | Proton (account override and prefix markers) | host PID `3290060` ran `S:\steamapps\common\Hades\x64Vk\Hades.exe` under Proton Experimental | UNKNOWN; no Playnite process or handle remained |

The owner stopped each title outside Playnite with a manual `SIGTERM` to the game process; the
Flatpak mode sent no signal. Post-stop process tables contained no title or Wine row. This is
the product behaviour: Playnite cannot observe or stop the host game from the sandbox.

### Packaged AppImage — tracked path unchanged

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Playnite-Linux-x86_64-launch2.AppImage` | 46,602,744 bytes | `6a08893ace54832067b42b208f587780c5db060d924ea7dccf87c354e8d52929` |

The AppImage reported `1.0.0.0` and ran the shipped `playnite launch 770100` product path:
`ATTRIBUTED pid=3310235 start_ticks=242650085 pgid=3310202`, then `TRACK Running` and
`TRACK Stopped degraded=False` after the owner ended the game. The product command therefore
reaches the unchanged tracked launcher off Flatpak.

It then ran the existing four-flow tracked acceptance for native `770100` and Proton `1145360`:

| Flow | Terminal state | Attributed identity | Result |
| --- | --- | --- | --- |
| native stop | `Stopped` | pid `3323259`, start ticks `242653112`, pgid `3323226` | PASS, survivors 0, non-degraded |
| native cancel | `Cancelled` | pid `3332078`, start ticks `242655472`, pgid `3332045` | PASS, survivors 0, non-degraded |
| Proton stop | `Stopped` | pid `3344409`, start ticks `242658314`, pgid `3344409`; `Hades.exe` through Proton | PASS, survivors 0, non-degraded |
| Proton cancel | `Cancelled` | pid `3356432`, start ticks `242661089`, pgid `3356432`; `Hades.exe` through Proton | PASS, survivors 0, non-degraded |

Every flow printed `POST-TEARDOWN complete=True survivors=0 tracked=1
original_clients_retained=11/11` and `STOP REPORT degraded=False`. The tracked path therefore
still attributes, tracks and stops exactly as before; only the Flatpak branch changed.

This unit does not claim Phase 5 exit; criterion 3 is closable only as this adjusted
launch-only product scope, not as `flatpak-spawn` host tracking.

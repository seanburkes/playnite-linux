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

Native and AppImage keep the unchanged tracked path. The packaged `steam-launch` command
refuses to run on a non-Flatpak host instead of inventing a second, untracked native launch.

## Tests

`tests/Playnite.Core.Tests/p5-008/FlatpakSteamLaunchTests.cs`:

- marker-present and marker-absent mode selection through the probe seam;
- exact URI construction for `0`, `42` and `uint.MaxValue`;
- invalid ids: empty/whitespace, signs, leading/trailing spaces, hex, exponent, fraction,
  overflow, newline and injection strings;
- one absolute opener plus one URI argument, and shell interpreter rejection;
- accepted, nonzero and unavailable opener outcomes all report UNKNOWN and a manual Steam stop;
- cancellation propagates with no stop/signal claim;
- the launch-only type exposes no stop, signal, track or playtime surface;
- a non-Flatpak host selects `Tracked` and `SteamProcessLauncher.CreateRequest()` still returns
  `steam -applaunch <appid>`.

`packaging/test_packaging.py` covers the `playnite steam-launch` route in both the payload
launcher and AppRun.

## Packaging

`packaging/assemble.sh` publishes `tools/p5-008-flatpak-launch` into
`payload/lib/playnite/steam-launch`; `packaging/playnite` routes the `steam-launch` subcommand
there. The Flatpak manifest and `packaging/verify-flatpak.py` are unchanged: the allowlist stays
exactly `wayland`, `fallback-x11`, `ipc` and `dri`, with no `filesystem` or `talk-name` grant.

## Acceptance

The real-machine acceptance ran from clean branch head `e32c6e9d02e1cc01128bbed922f6aa245b5d936c`
on 2026-11-19.

### Packaged Flatpak — launch-only

Artifacts:

| Artifact | Size | SHA-256 / id |
| --- | ---: | --- |
| `Playnite-Linux-x86_64.flatpak` | 36,333,360 bytes | `3fb865a889360d9f70893930a2d6be380068e876c5a019057c9a0503b9a93558` |
| installed Flatpak OSTree commit | — | `5305c62a408241381b275fda1b26ad4c0b0ed97e54d22ffd295af5e81bb3a440` |

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
flatpak run --user io.github.playnite_linux.Playnite steam-launch 770100
flatpak run --user io.github.playnite_linux.Playnite steam-launch 1145360
```

Both printed `status: RequestAccepted`, `tracking: unknown`, `stop: manual-steam`. The JSON
detail said game start and playtime are UNKNOWN and that the user must stop the game in Steam.

| Title | Selection | Launch evidence | Playnite tracking |
| --- | --- | --- | --- |
| native `770100` One Deck Dungeon | native/unknown (Steam owns selection) | host PID `1365749` started as `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64`; Steam log recorded `Adding process ... for gameID 770100` | UNKNOWN; no Playnite process or handle remained |
| Proton `1145360` Hades | Proton (account override and prefix markers) | Steam showed its launch-options dialog first (owner action); the accepted launch recorded `gameID 1145360` and host PID `1443293` as `S:\steamapps\common\Hades\x64\Hades.exe` under Proton Experimental | UNKNOWN; no Playnite process or handle remained |

The owner stopped each title outside Playnite with a manual `SIGTERM` to the game process; the
Flatpak mode sent no signal. Post-stop process tables contained no title or Wine row. This is
the product behaviour: Playnite cannot observe or stop the host game from the sandbox.

### Packaged AppImage — tracked path unchanged

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Playnite-Linux-x86_64.AppImage` | 46,344,696 bytes | `9d005374743ad517df3ebaf822742d75c83938c598f58252e815c4884ff4dfb5` |

The AppImage reported `1.0.0.0`, passed headless smoke and dry-run inspection, then ran the
unchanged tracked acceptance for native `770100` and Proton `1145360`:

| Flow | Terminal state | Attributed identity | Result |
| --- | --- | --- | --- |
| native stop | `Stopped` | pid `1480188`, start ticks `242227817`, pgid `1480155` | PASS, survivors 0, non-degraded |
| native cancel | `Cancelled` | pid `1488382`, start ticks `242230229`, pgid `1488349` | PASS, survivors 0, non-degraded |
| Proton stop | `Stopped` | pid `1499853`, start ticks `242233001`, pgid `1499853`; `Hades.exe` through Proton | PASS, survivors 0, non-degraded |
| Proton cancel | `Cancelled` | pid `1511108`, start ticks `242235750`, pgid `1511108`; `Hades.exe` through Proton | PASS, survivors 0, non-degraded |

Every flow printed `POST-TEARDOWN complete=True survivors=0 tracked=1
original_clients_retained=11/11` and `STOP REPORT degraded=False`. The tracked path therefore
still attributes, tracks and stops exactly as before; only the Flatpak branch changed.

The final `linux-ci` run for this PR head and the packaging workflow artifacts are cited in the
PR body and the unit ledger fragment. This unit does not claim Phase 5 exit.


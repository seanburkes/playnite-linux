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
`ddc86161683ffd1aae707cb675c9dcf475dea0cc` on 2026-11-19 through the shipped
`playnite launch <appid>` command. The evidence-only commits after that head change no code, so
the packaged artifacts below are the tree that ships. The final-head linux-ci run is cited in
the PR body.

### Packaged Flatpak — launch-only

Artifacts:

| Artifact | Size | SHA-256 / id |
| --- | ---: | --- |
| `Playnite-Linux-x86_64-product.flatpak` | 36,346,080 bytes | `9511347bba0e236046d7400c0524859d3eb415c1f6f3ee6a4470fd08513eda8b` |
| installed Flatpak OSTree commit | — | `a5cd30fb1372015c00948fa8fdc14aeb9f00054944554008860d23234ee7c310` |

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
| native `770100` One Deck Dungeon | native/unknown (Steam owns selection) | host PID `3025570` started as `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | UNKNOWN; no Playnite process or handle remained |
| Proton `1145360` Hades | Proton (account override and prefix markers) | host PID `3039187` ran `S:\steamapps\common\Hades\x64Vk\Hades.exe` under Proton Experimental | UNKNOWN; no Playnite process or handle remained |

The owner stopped each title outside Playnite with a manual `SIGTERM` to the game process; the
Flatpak mode sent no signal. Post-stop process tables contained no title or Wine row. This is
the product behaviour: Playnite cannot observe or stop the host game from the sandbox.

### Packaged AppImage — tracked path unchanged

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Playnite-Linux-x86_64-product.AppImage` | 46,352,888 bytes | `3265c548c421f33b54deb7664d265f1fa099fd1ad9e9d227f7f180f661191a1e` |

The AppImage reported `1.0.0.0`, passed headless smoke and dry-run inspection, then ran the
shipped `playnite launch <appid>` tracked path for native `770100` and Proton `1145360`:

| Flow | Terminal state | Attributed identity | Result |
| --- | --- | --- | --- |
| native tracked launch | `Stopped` | pid `3096089`, start ticks `242600915`, pgid `3096056` | PASS, non-degraded; owner ended the game and the tracker reported natural stop |
| Proton tracked launch | `Stopped` | pid `3104113`, start ticks `242602875`, pgid `3104113`; `Hades.exe` through Proton | PASS, non-degraded; owner ended the game and the tracker reported natural stop |

Both sessions printed `TRACK Starting`, `TRACK Running`, `TRACK Stopping` and `TRACK Stopped`
with `degraded=False`. The tracked path therefore still attributes, tracks and stops exactly as
before; only the Flatpak branch changed. No Playnite process signalled the game in either run.

This unit does not claim Phase 5 exit; criterion 3 is closable only as this adjusted
launch-only product scope, not as `flatpak-spawn` host tracking.

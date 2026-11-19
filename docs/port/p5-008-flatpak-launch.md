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

The real-machine acceptance runs one native and one Proton title through the packaged Flatpak
with `flatpak run io.github.playnite_linux.Playnite steam-launch <appid>`, records the effective
permissions and the UNKNOWN/manual-stop report, then repeats the packaged AppImage tracked path
to show it is unaffected. Artifact ids, sha256 values and the final `linux-ci` run are recorded
in the PR body and the unit ledger fragment. This unit does not claim Phase 5 exit.

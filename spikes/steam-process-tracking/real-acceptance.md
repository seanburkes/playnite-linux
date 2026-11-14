# p1-007 real Steam acceptance (2026-11-14)

**Result: passed for one already-installed native Linux title on this host.**
The spike's own `Session` observed the real Steam-launched process tree go
`running` → `stopped`, with all tracked identities disappearing on exit and the
Steam client left untouched. This is bounded, single-host acceptance evidence,
not a general claim about all Steam titles, Proton, Flatpak, or anti-cheat.

The synthetic/owned-process suite remains the regression evidence. This note
adds the real-environment observation the earlier README recorded as unverified.

## Host

| Fact | Value |
| --- | --- |
| Kernel | `7.3.0-3-cachyos` |
| Boot id | `0b1e6341-abd8-47ad-8aa8-80cc58eefbba` |
| PID namespace seen by observer and game | `pid:[4026531836]` (host namespace) |
| Session | Hyprland 0.56.2 on Wayland (`WAYLAND_DISPLAY=wayland-1`, `DISPLAY=:0` XWayland) |
| Steam root | `/home/sean/.local/share/Steam` (`~/.steam/steam` and `~/.steam/root` are symlinks to it) |
| Steam client | already running since 2026-10-23 08:16:41, PID 122666, start ticks 6590265; not started or restarted by this task |
| Steam client state | already logged in (auto-login); no credentials entered, no settings changed |
| Library folders (`steamapps/libraryfolders.vdf`) | `/home/sean/.local/share/Steam` (primary, used here) and `/run/media/bazzite/My Passport/SteamLibrary` (secondary, untouched) |

Steam was already running. The task did not start, stop, reconfigure, or update
Steam, and did not purchase, install, update, or download anything.

## Title and native classification

| Fact | Value |
| --- | --- |
| App ID / name | `105600` / Terraria |
| Manifest | `StateFlags 4`, `buildid 24893155`, `installdir Terraria` |
| Install path | `/home/sean/.local/share/Steam/steamapps/common/Terraria` |
| Launcher | `Terraria`, a Bourne-Again shell script that runs `./Terraria.bin.x86_64` without `exec` |
| Game binary | `Terraria.bin.x86_64`: ELF 64-bit LSB executable, x86-64, dynamically linked, GNU/Linux 3.2.0 |
| Compatibility mapping | none: `config/config.vdf` has no `CompatToolMapping`, `steamapps/compatdata/105600/` is empty |
| Runtime wrapper | Steam Linux Runtime *soldier* (`srt-bwrap` / `pv-adverb`), not Proton/Wine |
| Classification | **native Linux** |

The classification is from the installed files and the actual launched
executable path, not from the store page. No Windows binary (`Terraria.exe`) or
Wine/Proton process was involved.

## Launch path

Steam was already running, so the probe invoked the supported CLI handoff:

```sh
/usr/bin/steam -applaunch 105600
```

The CLI child returned 0 with no output. It handed the launch to the running
Steam client and exited; it is not the game process and must not be attributed
as one. Launch options, Proton settings, account state, and library folders were
not modified.

## Observed chain (primary run, 2026-11-14T22:59:52Z)

All rows were read from the host `/proc` and share the host PID namespace and
boot id above. `start_ticks` is `/proc/<pid>/stat` field 22, raw clock ticks, the
identity key used by `tracker.py`.

| Role | PID | start_ticks | comm | Parent | Executable |
| --- | --- | --- | --- | --- | --- |
| Steam client (context only) | 122666 | 6590265 | `steam` | 121295 (Steam's launcher bash) | `~/.local/share/Steam/ubuntu12_32/steam` |
| Steam reaper / launch anchor | 350947 | 199449346 | `reaper` | 122666 | `~/.local/share/Steam/ubuntu12_32/reaper` |
| Steam Linux Runtime bwrap | 350948 | 199449350 | `srt-bwrap` | 350947 | `.../SteamLinuxRuntime_soldier/pressure-vessel/libexec/steam-runtime-tools-0/srt-bwrap` |
| pressure-vessel adverb | 351353 | 199449438 | `pv-adverb` | 350948 | `/usr/lib/pressure-vessel/from-host/libexec/steam-runtime-tools-0/pv-adverb` |
| Terraria launcher script | 351387 | 199449441 | `Terraria` | 351353 | `/usr/bin/bash` (script under the install dir) |
| Game | 351420 | 199449455 | `Terraria.bin.x8` | 351387 | `.../common/Terraria/Terraria.bin.x86_64` |

Reaper command line (abridged):

```text
.../ubuntu12_32/reaper SteamLaunch AppId=105600 --
  .../SteamLinuxRuntime_soldier/_v2-entry-point --verb=waitforexitandrun --
  .../SteamLinuxRuntime/scout-on-soldier-entry-point-v2 --
  .../common/Terraria/Terraria
```

`_v2-entry-point` and `scout-on-soldier-entry-point-v2` appear in the reaper
command line but were not separate processes by the time the game was observed:
the entry points had already been replaced (exec) by `srt-bwrap`/`pv-adverb`.
The bash `Terraria` script does not `exec`, so it stays as a live parent for the
whole game session and exits with the game.

The tracker's closure from the game root therefore contained **one** identity
(the game); the closure from the reaper root contained **five** identities
(reaper, `srt-bwrap`, `pv-adverb`, bash launcher, game). Both are real,
caller-attributable roots; the reaper is the stable anchor for this launch
style.

## Tracker observation

The probe imported the spike's unmodified `tracker.py` and fed `Session` the real
`/proc` rows each sample. `Session` returned:

| Session root | Identity | First state (0.0 s) | Known at first state | Final state (12.426 s) | Known after stop |
| --- | --- | --- | --- | --- | --- |
| Game | 351420 / 199449455 | `running` | 1 | `stopped` | empty |
| Steam reaper | 350947 / 199449346 | `running` | 5 | `stopped` | empty |

State log (`at_s` is relative to the first observation):

```text
0.000   game    running  1 identity
0.000   reaper  running  5 identities
12.426  game    stopped  0 identities
12.426  reaper  stopped  0 identities
```

No root-gone-with-descendants-alive transition and no identity replacement was
observed. The game root and the reaper root both stayed `running` for the full
12-second observation. No tracker defect was observed in this run; the spike's
identity and fixed-point closure semantics held on the real runtime tree.

## Exit detection and cleanup

The game exposes a Wayland-native window (`class: Terraria.bin.x86_64`), which
`xdotool` could not see through XWayland. The probe asked the compositor to
close that exact window by PID using Hyprland 0.56's Lua dispatcher:

```lua
for _, w in ipairs(hl.get_windows()) do
  if w.pid == 351420 then
    hl.dispatch(hl.dsp.window.close({ window = w }))
  end
end
```

This is a normal window-manager close request (the same user action as clicking
the close button), not a signal. Steam was not signalled.

- Exit detected: **0.283 s** after the close request.
- `Session` transitioned to `stopped` at 12.426 s and cleared all known
  identities.
- No process whose executable was under the Terraria install directory
  remained; the Steam client (PID 122666) was still alive afterwards.
- The tracked rows were absent on exit rather than zombies; `Session` would not
  count a zombie as running either.
- The observer's last-resort SIGTERM fallback was **not used**.

## Duration

| Phase | Seconds |
| --- | --- |
| Launch to game discovery | 2.319 |
| Bounded observation | 12.000 |
| Close request to confirmed exit | 0.283 |
| Total probe wall time | 14.801 |

The game ran for roughly 12.4 seconds. The session was deliberately short.

## Repeat run

A second, independent run at 2026-11-14T22:56:37Z produced the same shape with a
different PID generation (this run predates the in-probe `Session` feed; it used
the same observer and close path):

| Role | PID | start_ticks |
| --- | --- | --- |
| reaper | 292426 | 199431265 |
| srt-bwrap | 292427 | 199431269 |
| pv-adverb | 292628 | 199431358 |
| bash launcher | 292661 | 199431361 |
| game (`Terraria.bin.x86_64`) | 292693 | 199431375 |

Exit was detected 0.357 s after the close request, with no install-directory
processes left; total wall time 29.031 s (startup took 16.533 s in that run).
The primary run above is the tracker-integrated evidence.

## Limitations and deviations

1. **Launch method.** The title was launched with `steam -applaunch 105600`, not
   by clicking Play in the client. This is the supported CLI handoff a desktop
   integration would use, and it produced the normal reaper/runtime chain. It is
   not a click-through UI test.
2. **Acceptance-only attribution.** The probe discovered the game root by
   scanning `/proc/<pid>/exe` under the install directory and the reaper by its
   `AppId=` command line. `tracker.py` still takes an already-attributed root;
   root attribution and launch-request disambiguation remain unimplemented in
   the spike (unchanged by this task).
3. **Short session.** Twelve seconds of observation is enough to see the steady
   tree and the exit, but not long-play behaviour such as map changes, Steam
   overlay activity, crashes, or game-spawned helper processes.
4. **Wayland close path.** `xdotool` cannot see the game's Wayland window on this
   host. The close uses Hyprland's compositor API; an X11 session, another
   compositor, or a different Steam runtime may need a different close path.
5. **Steam already running and logged in.** The task did not exercise Steam
   cold start, login, first-run dialogs, or a logged-out client.
6. **Steam Linux Runtime container.** The game runs under pressure-vessel
   (`srt-bwrap`/`pv-adverb`) in the *soldier* runtime. On this host it shares the
   host PID namespace, so host `/proc` identities worked. That is host/runtime
   specific, not a guarantee for other runtime versions or sandbox settings.
7. **Tooling trial (not evidence).** An earlier 22:52Z trial exposed two probe
   bugs, not tracker bugs: a false "passed" after a close timeout, and use of a
   legacy Hyprland dispatch string that 0.56 rejects. The game was closed
   manually and exit confirmed; that JSON is retained as
   `acceptance-terraria-attempt1-invalid.json` and is **not** acceptance
   evidence. Both probe bugs were fixed before the runs above.
8. **No configuration writes.** No Steam setting, launch option, Proton version,
   library folder, account state, or game file was changed. `compatdata/105600`
   was already empty and remains so.

## What remains unproven

- Proton/Wine titles, including multiple Wine processes, wineserver lifetime,
  prefix setup, and Proton-version variation. No Proton title was launched.
- Flatpak-hosted Steam and any host/namespace transport boundary (p1-010).
- Anti-cheat titles and titles that require an interactive launcher, login,
  first-run dialog, or EULA acceptance.
- Titles installed in the secondary library, and concurrent launches of the same
  or different App IDs.
- Steam cold start, a logged-out client, and the "launch handed to an existing
  broker" case against a real client.
- Long sessions, game crashes, games that re-exec or daemonize after launch, and
  games whose wrapper exits before the game (the README's short-lived-wrapper
  false-stop case) on real Steam.
- Other kernels, PID-namespace-isolated runtimes, permission-restricted `/proc`,
  X11-only or non-Hyprland sessions, and other distributions.
- Authoritative session *attribution* from a launch request: this run shows the
  tree is observable once the caller has a root; it does not implement choosing
  that root in production.

## Tracker defects

None observed in this run. Two design boundaries were confirmed rather than
newly discovered:

- `steam -applaunch` returns before the game appears and is not a session root;
  attributing it would report a false stop.
- The real chain contains several short-lived container helpers. The AppId-scoped
  reaper and the persistent bash launcher make the chain observable here, but a
  caller that attributes a wrapper that exits early can still miss a reparented
  game (already documented in the README's synthetic case).

No fix was attempted in this task.

## Reproduce

From the repository root, on a Linux host with Steam installed/running and an
already-installed copy of Terraria:

```sh
# 1. The existing spike suite must still pass.
TMPDIR=/tmp/opencode timeout 60s /usr/bin/python3 -B \
  spikes/steam-process-tracking/test_tracking.py

# 2. Verify the native classification from files, not assumptions.
grep -E '"(StateFlags|installdir|buildid)"' \
  "$HOME/.local/share/Steam/steamapps/appmanifest_105600.acf"
file "$HOME/.local/share/Steam/steamapps/common/Terraria/Terraria.bin.x86_64"
ls -A "$HOME/.local/share/Steam/steamapps/compatdata/105600" | wc -l

# 3. Run the bounded real acceptance (launch, observe, close, confirm exit).
mkdir -p /tmp/opencode
/usr/bin/python3 -B spikes/steam-process-tracking/real_acceptance_observe.py \
  --appid 105600 \
  --install-dir "$HOME/.local/share/Steam/steamapps/common/Terraria" \
  --window-name Terraria \
  --observe-seconds 12 --close-seconds 15 \
  --report /tmp/opencode/acceptance-terraria.json

# 4. Read back the tracker-visible facts.
jq '{result, root, tracker, close, total_seconds}' \
  /tmp/opencode/acceptance-terraria.json
```

The observer refuses to start if a process from the install directory is already
running, never signals Steam, and only closes the window it attributes to the
game. Its `SIGTERM` fallback exists solely to avoid leaving a game running if a
window-manager close fails; it was not used in the accepted runs and would be
recorded as a deviation in the report.

The raw reports and logs for the runs above are retained outside the repository
under `/home/sean/src/firstmate/data/p1-007-live/`
(`acceptance-terraria-tracker.json`, `acceptance-terraria-tracker.log`,
`acceptance-terraria.json`, `acceptance-terraria.log`, and the invalid first
trial `acceptance-terraria-attempt1-invalid.json`).

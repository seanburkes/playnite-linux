# Packaged AppImage Steam teardown evidence

On November 18, 2026, the mounted x86-64 AppImage launched, stopped, and cancelled
One Deck Dungeon and Hades through an existing signed-in Steam client. Both games
were installed in the host Steam library. All four flows ended with complete
post-teardown process observations, zero matching live processes, and every
original Steam client identity retained. The harness and host observer both
exited successfully.

This run supplies the packaged no-orphan evidence for criterion 4 of the pending
Phase 5 gate. It records the outcomes for these two installed games on one
Linux host. [Phase 5 gate](gates/P5-pending.md)

## AppImage provenance

| Artifact | Source commit | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| CI artifact `Playnite-Linux-x86_64-AppImage`, run 44431326813 | `55076999fb21590e34d970e48ea70851eabfc5d7` | 46,131,704 | `7b0425c44df7342200b2db007160ee21ac638050440ba680d7dc45b36f7a88d2` |
| Local `Playnite-Linux-x86_64.AppImage` | `3eca0aa407eca749ecd8b0a5a57fb55198d10552` | 46,467,576 | `7f0a9fad198d993840c6731448a76ba82e77503f1512cbac3c7ed105507188f3` |

The downloaded CI artifact reported version `1.0.0.0` and passed
`--headless --smoke` with `--appimage-extract-and-run`. The local image was
built from a clean Git checkout with `packaging/assemble.sh`, .NET SDK 11.0.100
and the self-contained .NET 11.0.0 linux-x64 runtime. The following commands
used the same appimagetool 1.9.1 and type2-runtime 20251108 binaries as CI, with
both files verified against the workflow SHA-256 pins.
[Baseline packaging run](https://github.com/seanburkes/playnite-linux/actions/runs/44431326813)

```sh
bash packaging/assemble.sh "$build"
mkdir "$build/artifacts"
ARCH=x86_64 "$appimagetool" --appimage-extract-and-run --no-appstream \
  --runtime-file "$runtime" "$build/AppDir" "$build/artifacts/Playnite-Linux-x86_64.AppImage"
image="$build/artifacts/Playnite-Linux-x86_64.AppImage"
"$image" --version
"$image" --headless --smoke
"$image" --steam-acceptance "$steam_library" 770100 "$steam_library" 1145360
"$image" --steam-acceptance "$steam_library" 770100 "$steam_library" 1145360 \
  --allow-launch --allow-stop
```

Here `build` is a new absolute directory and `steam_library` is the local Steam
library root. The local image reported version `1.0.0.0` and opened and closed
the headless smoke window. The first `--steam-acceptance` invocation read both
installed manifests and exited in dry-run mode. Both consent flags enabled the
live sequence shown below.

## Live results

The real-machine steps were reading the installed Steam library from the
AppImage, launching the two games in the logged-in Linux desktop session, and
observing and checking teardown in the host PID namespace. The test ran with
`DISPLAY=:0`, `WAYLAND_DISPLAY=wayland-1`, and the user's XDG runtime directory.
No login, prompt, or window interaction was required during the run.

The attribution window was 240 seconds. Elapsed time is the time to
attribution. The harness held each game in `Running` for five seconds before
calling tracker stop or cancelling the tracking token. Executable paths are
relative to the Steam library.

| Flow | Terminal state | PID / start ticks / PGID | Observed executable | Elapsed | Post-teardown live rows |
| --- | --- | --- | --- | ---: | --- |
| Native `770100` stop | `Stopped` | `1929730 / 234844122 / 1929692` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 4.5 s | 0 |
| Native `770100` cancel | `Cancelled` | `1939067 / 234846550 / 1938632` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 3.4 s | 0 |
| Proton `1145360` stop | `Stopped` | `1954090 / 234849537 / 1954090` | 9.2 s | 0 |
| Proton `1145360` cancel | `Cancelled` | `1964019 / 234852404 / 1964019` | 7.8 s | 0 |

For both Proton launches, the harness resolved Wine's Windows image argument
`S:\steamapps\common\Hades\x64\Hades.exe` through the game's own prefix
to `steamapps/common/Hades/x64/Hades.exe`. Steam selected the game runtime.

The packaged harness made one scoped pidfd `SIGTERM` request per flow for the
game identity above. No group, Steam client, or helper signal was issued. Both
native groups included a pressure-vessel wrapper. The host observer also saw
Proton's wineserver in a distinct group before each Hades teardown, with
PID/start ticks/PGID `1953705 / 234849393 / 1953705` for stop and
`1963627 / 234852262 / 1963627` for cancel.

After tracker and launcher disposal, the packaged harness checked the game
group, known identities, observed descendants and game executable evidence in
a fresh process-table snapshot. All four flows printed the same pair of lines.

```text
POST-TEARDOWN complete=True survivors=0 tracked=1 original_clients_retained=11/11
STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty
```

An independent same-user `/proc` observer also checked the game PGID, app IDs and
install/prefix paths visible in process metadata, and fresh Steam runtime and
Wine executable rows relative to the baseline. It saw the wrappers and the
Wine prefix processes before stop/cancel, then printed the following filtered
post-teardown process-table counts. The original Steam client remained
`122666 / 6590265 / 121295` in every sample.

```text
AUDIT post-teardown app=770100 group=1929692 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT post-teardown app=770100 group=1938632 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT post-teardown app=1145360 group=1954090 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT post-teardown app=1145360 group=1964019 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1
AUDIT final packaged_exit=0 flows=4 clean_tables=True
```

## Rerun with escaped Wine survivor detection

A review found that the original packaged scan could miss a wineserver that
entered its own group before the tracker observed its ancestry. The updated
harness compares PID and start ticks with a complete pre-launch snapshot for
each flow. Its post-disposal scan checks the game group, known identities,
executables and argument paths under the selected install or prefix, and fresh
Wine process names from kernel metadata, executables or the first argument.
A fresh Wine process with an ambiguous prefix also fails the isolated acceptance
run. The scan only observes and reports. A synthetic test covers a daemonized
wineserver in another group with no observed ancestry.

The rerun used a mounted `Playnite-Linux-x86_64.AppImage` assembled with the
commands above from clean commit `264554eb0670719e81e169eee6b5a3c4c0a99ddc`.
The artifact was 46,471,672 bytes with SHA-256
`7da405857ac0794755c7fb8705ee19f338968471abfbfcd75ad02922144b4bab`.
Version `1.0.0.0`, headless smoke, and dry-run manifest inspection passed.
The four real Steam launches then ran on the same signed-in Linux desktop
without a login, prompt, or window interaction.

| Flow | Terminal state | PID / start ticks / PGID | Observed executable | Elapsed |
| --- | --- | --- | --- | ---: |
| Native `770100` stop | `Stopped` | `2498098 / 234998905 / 2497957` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 7.1 s |
| Native `770100` cancel | `Cancelled` | `2514771 / 235001373 / 2514734` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 3.7 s |
| Proton `1145360` stop | `Stopped` | `2525220 / 235004487 / 2525220` | `steamapps/common/Proton - Experimental/files/lib/wine/i386-unix/wine64-preloader` | 10.2 s |
| Proton `1145360` cancel | `Cancelled` | `2532900 / 235007192 / 2532900` | `steamapps/common/Proton - Experimental/files/lib/wine/i386-unix/wine64-preloader` | 6.0 s |

The independent host observer saw wineserver in a separate group during both
Hades launches. Its PID/start ticks/PGID were
`2524814 / 235004332 / 2524814` for stop and
`2532530 / 235007052 / 2532530` for cancel. All four packaged scans finished
with no survivors. The packaged harness retained all 11 original Steam
identities after each flow and made one scoped game-identity pidfd `SIGTERM`
request per flow. No group, Steam client, or helper received a signal.

The relevant lines follow in flow order.

```text
SURVIVOR SCAN baseline_live=491 post_live=490 complete=True survivors=0
STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty
AUDIT post-teardown app=770100 group=2497957 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1

SURVIVOR SCAN baseline_live=487 post_live=495 complete=True survivors=0
STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty
AUDIT post-teardown app=770100 group=2514734 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1

SURVIVOR SCAN baseline_live=494 post_live=497 complete=True survivors=0
STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty
AUDIT post-teardown app=1145360 group=2525220 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1

SURVIVOR SCAN baseline_live=494 post_live=496 complete=True survivors=0
STOP REPORT degraded=False reason=Attributed game identities gone; observed group empty
AUDIT post-teardown app=1145360 group=2532900 app_or_group_rows=0 fresh_runtime_rows=0 original_steam_clients=1/1

AUDIT final packaged_exit=0 flows=4 clean_tables=True
```

## Rerun with relative Linux argv detection

The survivor scan reads `/proc/PID/cwd` alongside argv in the post-teardown
snapshot. It resolves relative Linux arguments from that process's working
directory and reports paths under the selected game's install directory or
prefix. The regression test starts `/usr/bin/python3 ./worker.py` in its own
session with the install directory as its cwd. The old scan returned an empty
survivor list. The fixed scan reported the worker and it stayed alive.

On November 18, 2026, the mounted CI AppImage passed version `1.0.0.0`, headless
smoke, and dry-run manifest inspection. GitHub built the artifact from merge
checkout `215801ae341891f41cac4a801a9496e84c2d1adb` for head
`4eaf177752143f9346d0b797d3ffb10fb231fbc2`. It was 46,246,392 bytes with SHA-256
`6501054052ade0d066d1db1002896988037aedc9565b9ec58027903b079f45b3`.
[AppImage packaging run](https://github.com/seanburkes/playnite-linux/actions/runs/44441401765)

The live command above exited 0 on the signed-in Steam desktop. The harness
issued one pidfd `SIGTERM` request for the attributed game identity per flow.
All four stop reports were non-degraded and all 11 original Steam identities
were present after each flow. Game paths are relative to the Steam library.

| Flow | Terminal state | PID / start ticks / PGID | Attributed game path | Elapsed |
| --- | --- | --- | --- | ---: |
| Native `770100` stop | `Stopped` | `2981486 / 235122189 / 2981399` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 6.5 s |
| Native `770100` cancel | `Cancelled` | `2994909 / 235124594 / 2994784` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 2.8 s |
| Proton `1145360` stop | `Stopped` | `3008122 / 235127627 / 3008122` | `steamapps/common/Hades/x64/Hades.exe` | 9.4 s |
| Proton `1145360` cancel | `Cancelled` | `3017182 / 235130481 / 3017182` | `steamapps/common/Hades/x64/Hades.exe` | 7.4 s |

```text
SURVIVOR SCAN baseline_live=487 post_live=489 complete=True survivors=0
SURVIVOR SCAN baseline_live=489 post_live=499 complete=True survivors=0
SURVIVOR SCAN baseline_live=496 post_live=497 complete=True survivors=0
SURVIVOR SCAN baseline_live=498 post_live=497 complete=True survivors=0
```

Linux CI for the packaged code head passed 2,629 tests with zero failures and
zero skips. [Linux CI run](https://github.com/seanburkes/playnite-linux/actions/runs/44441401426)

## Rerun with lexically resolved unlinked argv

A relative argument can name a target that no longer exists: a detached worker
may unlink its own script after loading. The pre-fix scan combined the argument
with the process's cwd and then required realpath to resolve it, so a missing
target produced no app-path evidence and the surviving worker was not reported.
The scan now normalises the cwd-relative path lexically, without requiring the
target to exist, and reports it when it is under the selected game's install
directory or prefix. Absolute arguments still use realpath, and Windows drive
resolution is unchanged. The regression starts `/usr/bin/python3 ./worker.py`
in its own session, deletes `worker.py` after the worker prints `ready`, proves
the pre-fix predicate misses the live row, and asserts the fixed scan reports
it.

On November 18, 2026, a local mounted AppImage was assembled from clean commit
`fa443d9730419758173a1e1033820b650b40f813` with `packaging/assemble.sh`, .NET
SDK 11.0.100 and the self-contained .NET 11.0.0 linux-x64 runtime. The same
appimagetool 1.9.1 and type2-runtime binaries verified in the CI workflow packaged
it. The artifact was 46,471,672 bytes with SHA-256
`c216c50b8b809f9c810f879bcfb9f67840feedb9378f87135594dc52ad80317e`.
Version `1.0.0.0`, headless smoke, and dry-run manifest inspection passed.

The live command above exited 0 on the same signed-in Steam desktop. The harness
issued one pidfd `SIGTERM` request for the attributed game identity per flow.
All four stop reports were non-degraded and all 11 original Steam identities
were present after each flow. Game paths are relative to the Steam library.

| Flow | Terminal state | PID / start ticks / PGID | Attributed game path | Elapsed |
| --- | --- | --- | --- | ---: |
| Native `770100` stop | `Stopped` | `3438401 / 235240530 / 3438368` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 4.6 s |
| Native `770100` cancel | `Cancelled` | `3449100 / 235242962 / 3449067` | `steamapps/common/One Deck Dungeon/OneDeckDungeon.x86_64` | 3.1 s |
| Proton `1145360` stop | `Stopped` | `3459391 / 235245795 / 3459391` | `steamapps/common/Hades/x64/Hades.exe` | 7.4 s |
| Proton `1145360` cancel | `Cancelled` | `3470661 / 235248553 / 3470661` | `steamapps/common/Hades/x64/Hades.exe` | 6.6 s |

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

The independent host observer retained the original Steam client
`122666 / 6590265 / 121295` throughout and printed the filtered post-teardown
row counts above.

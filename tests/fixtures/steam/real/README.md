# Steam real-world fixture corpus (redacted)

Captured on 2026-11-14 from a Linux host with a native Steam client installed at
`~/.local/share/Steam` (with `~/.steam/steam` a symlink to it). These are redacted copies of
Steam-written metadata files. They contain no source account identifiers, no real SteamID64, no
source home username, no play timestamps, and no game content. Field names, nesting, ordering,
whitespace (tabs, LF) and value shapes are preserved wherever redaction did not require a value
change, so the files still parse as Valve KeyValues text or JSON.

Provenance is recorded in the p1-009 corpus proposal. Source files were only read; their
SHA-256 digests were identical before and after capture.

## Layout

```
steamapps/libraryfolders.vdf
steamapps/appmanifest_427520.acf                  Factorio (native Linux, installed)
steamapps/appmanifest_1238040.acf                 Dragon Age II: Ultimate Edition (Proton, installed)
steamapps/appmanifest_1493710.acf                 Proton Experimental (runtime, StateFlags 6)
steamapps/appmanifest_105600.acf                  Terraria (native Linux, installed; empty compatdata)
steamapps/compatdata/105600/.gitkeep              empty native compatdata placeholder
steamapps/compatdata/1238040/config_info          Proton prefix marker
userdata/00000000/config/compat.vdf               per-account platform override map
userdata/00000000/config/librarycache/1238040.json  achievement cache metadata
```

`steamapps/libraryfolders.vdf` has two libraries. The first is `/home/test/.local/share/Steam`
and exists only if a test root is prepared there; the second is `/mnt/external/SteamLibrary`,
which intentionally does not exist, so the corpus also exercises an unavailable library path.
Tests should copy the tree and rewrite `/home/test` to a temporary root (the synthetic corpus
uses `${FIXTURE_ROOT}` for the same purpose).

There is no `shortcuts.vdf` in this corpus: the source install had none. Do not invent one.
There is no `config.vdf` and no `localconfig.vdf`: they were not captured (see "Not covered").
There are no images, executables, Proton prefixes, registry files or licence-bearing assets.

## What the files exercise

- `libraryfolders.vdf`: modern object form, `path`, `label`, `contentid`, `totalsize`,
  `update_clean_bytes_tally`, `time_last_update_verified`, nested `apps` map of appid to bytes.
- `appmanifest_427520.acf`: minimal installed native manifest with `UserConfig` and
  `MountedConfig` `language` entries, and no `compatdata/427520` directory.
- `appmanifest_1238040.acf`: `InstalledDepots` with `dlcappid`, `SharedDepots`, and
  `InstallScripts` values containing escaped backslashes (`addins\\da2_prc_one\\...`).
- `appmanifest_1493710.acf`: `StateFlags` `6` (installed with an update pending;
  `buildid` differs from `TargetBuildID`, `BytesToDownload > 0`), empty `UserConfig` and
  `MountedConfig` objects, runtime depot layout.
- `appmanifest_105600.acf`: native Terraria with `StateFlags 4`; its `compatdata/105600/`
  directory contains no Steam data (only this repository's `.gitkeep` placeholder). A test must
  assert that this app is **not** Proton, which makes the spec's "compatdata alone is not proof"
  rule fail under a naive directory-existence check.
- `config_info`: 14 logical lines (13 newline-terminated plus a final line without a trailing
  newline), a runtime/prefix marker used to associate a `compatdata` prefix with a Proton build;
  paths are normalised to `/home/test`.
- `compat.vdf`: `platform_overrides` mapping app ids to `{dest: linux, src: windows}`.
- `<appid>.json`: JSON array containing an `achievements` section; this copy has no achievement
  text because the source app had none recorded.

## Compatibility-tool mapping is absent on this host

Per-app and default compatibility-tool selection is stored by Steam under
`config/config.vdf` at `InstallConfigStore/Software/Valve/Steam/CompatToolMapping`. The capture
host had **no `CompatToolMapping` section** in `config/config.vdf` and no per-app Proton key in
`userdata/<accountid>/config/localconfig.vdf`; only tool shader-cache bookkeeping was present.
`platform_overrides` in `compat.vdf` is a different marker. Because no real mapping exists here,
this corpus cannot exercise tool/version selection; that path needs a bounded probe or an
explicit synthetic fixture before v1 may claim it. No mapping was invented.

## Redaction

- Source home username normalised: `/home/<user>` -> `/home/test`.
- Removable-drive path replaced with `/mnt/external/SteamLibrary`; no mount username is retained.
- `LastOwner` SteamID64 replaced with the documented sentinel `76561190000000000`, which is below
  the valid individual SteamID64 range and is not a real account.
- `lastupdated` set to `1700000000`; `LastPlayed` set to `0`; `ScheduledAutoUpdate` set to `0`.
- `contentid`, `totalsize`, `update_clean_bytes_tally` set to `0`;
  `time_last_update_verified` set to `1700000000`.
- Per-account directory name normalised to `00000000`.
- The only file added rather than captured is the empty `steamapps/compatdata/105600/.gitkeep`,
  which exists solely so git can carry the otherwise-empty directory. It is not Steam data.
- Otherwise nothing was added: line count, key order, and indentation match the source files.

## Not covered by this corpus

- Artwork: no images are included because no licence-safe real artwork may be redistributed;
  the `appcache/librarycache` artwork layout is documented in the spec but not exercised here.
- `shortcuts.vdf` (absent on the host), `config.vdf`, `loginusers.vdf`, `localconfig.vdf`,
  `registry.vdf`, Proton prefix contents, `tracked_files`, shader caches and logs.
- The legacy `libraryfolders.vdf` index form; it was not present on the host.
- `\"` and `\n` escape handling (not observed); only `\\` escaping is exercised.
- `//` comments in `compatibilitytool.vdf`; no such file is included.
- Duplicate-key policy and malformed-VDF handling; the synthetic `tests/fixtures/steam/broken/`
  corpus covers the malformed case, not this real corpus.

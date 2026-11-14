# p1-009 Steam corpus proposal — protected-path approval required before writes

No protected path has been written. Corpus staged outside the checkout at
`/home/sean/src/firstmate/data/p1-009-live/proposal/tests/fixtures/steam/real/`.
Worktree: task worktree on branch `fm/p1-009-live`. Spec: `docs/port/08-STEAM-SPEC.md`.

This document is the only repository change besides the spec. The corpus files are **not** in
this PR; they are proposed destinations for a later, separately approved change.

## Requested independent second-reviewer approval

Add exactly these new files under the protected `tests/fixtures/` root (an `oracle-change`
label and a second reviewer are required):

| # | Proposed destination | SHA-256 | Content |
| --- | --- | --- | --- |
| 1 | `tests/fixtures/steam/real/README.md` | `8483f82fb7c3b832e10ca83bcf79edc81807936178b5a926f4d709da7c45d2a4` | Corpus description, provenance, redaction record, usage notes |
| 2 | `tests/fixtures/steam/real/SHA256SUMS` | (list file; not self-hashed) | SHA-256 of files 1 and 3–9 |
| 3 | `tests/fixtures/steam/real/steamapps/libraryfolders.vdf` | `6fc3f9a0dd9322369493f314083c9764374625e4dd28df6a49de11c989b9868c` | Modern two-library VDF; library 1 path unavailable; `apps` map preserved |
| 4 | `tests/fixtures/steam/real/steamapps/appmanifest_427520.acf` | `1ad3b0041c87aecf33af3ca7fa59c209f866b265ada6fd0e58055f7ab22aaa92` | Factorio, installed native Linux title |
| 5 | `tests/fixtures/steam/real/steamapps/appmanifest_1238040.acf` | `2255ee141342055b4dc68822074d5109f866699db124bc681de04ff762c6c6c5` | Dragon Age II: Ultimate Edition, installed Proton title; `dlcappid`, `SharedDepots`, escaped `InstallScripts` paths |
| 6 | `tests/fixtures/steam/real/steamapps/appmanifest_1493710.acf` | `db7e4dd085612c45181515bd7bd56253e7eb5865e59a4a9d69697421c07d6bb9` | Proton Experimental runtime; `StateFlags 6` (installed + update pending), empty `UserConfig`/`MountedConfig` |
| 7 | `tests/fixtures/steam/real/steamapps/compatdata/1238040/config_info` | `43076bbbc877b8aa023ee1eacead9a7ab4213580414d60711b3b098003926212` | Proton prefix marker; runtime version/paths/DLL override shape |
| 8 | `tests/fixtures/steam/real/userdata/00000000/config/compat.vdf` | `f8944eecbcb19bf621a3ffda720e575273907de21b2ef21bebeed84e547a79f6` | Per-account `platform_overrides` map (`dest linux`, `src windows`) |
| 9 | `tests/fixtures/steam/real/userdata/00000000/config/librarycache/1238040.json` | `2200afe5bd5dccc0cfe9d34b29eedc49014dd673e5b9b2d1797e3f52a14b5617` | Representative `librarycache` JSON (empty achievements) |

No existing fixture, golden, baseline, parity, formal, legacy, or oracle-lock file changes.
No artwork, executables, prefix contents, registry files, credentials, or large binaries.
`tests/fixtures/steam/` (synthetic corpus) is unchanged.

`shortcuts.vdf` is **absent** from the source install and is intentionally not proposed.
Do not invent one.

## Why these files

- `libraryfolders.vdf` captures the modern object dialect, numeric library keys, the `apps`
  appid→bytes map, and a real second library whose mount path was missing. That gives the
  oracle a genuine "library on an unmounted path" case rather than a synthetic one.
- `appmanifest_427520.acf` is a plain installed native manifest (no `compatdata`).
- `appmanifest_1238040.acf` is a real Proton title that combines `InstalledDepots` with
  `dlcappid`, `SharedDepots`, and `InstallScripts` values containing escaped backslashes.
  That escapes case is the parser dialect most likely to be implemented incorrectly.
- `appmanifest_1493710.acf` is a real Proton runtime manifest with `StateFlags 6`, proving that
  `StateFlags == 4` is the wrong installed test. Its `TargetBuildID` differs from `buildid`
  and `BytesToDownload > 0`, corroborating the update-pending meaning.
- `config_info` is the Proton prefix marker a launcher must detect without parsing a prefix.
- `compat.vdf` is the file-level Proton signal; the native title has no entry, the Proton title
  has `{dest: linux, src: windows}`.
- The `librarycache` JSON is the per-account metadata shape (achievements), not artwork. It is
  the empty-achievement variant because populated entries contain copyrighted achievement
  text and image URLs; those were deliberately not copied.

## Redaction record

Applied with a read-only script (source opened for read, output written only under the
proposal directory):

| File | Redaction | Why |
| --- | --- | --- |
| `libraryfolders.vdf` | `/home/<user>` → `/home/test`; removable-drive path → `/mnt/external/SteamLibrary`; `contentid`, `totalsize`, `update_clean_bytes_tally` → `0`; `time_last_update_verified` → `1700000000` | removes home username, mount user, device/library identity, and update timestamps; keeps every key and the `apps` shape |
| `appmanifest_427520.acf`, `appmanifest_1238040.acf`, `appmanifest_1493710.acf` | `LastOwner` SteamID64 → sentinel `76561190000000000`; `lastupdated` → `1700000000`; `LastPlayed` → `0`; `ScheduledAutoUpdate` → `0` | removes account id and recent-play/update timestamps; keeps numeric string shape |
| `compatdata/1238040/config_info` | `/home/<user>` → `/home/test` | removes username and absolute home paths; keeps line order and field shapes |
| `compat.vdf` | none needed (app ids only; no account identifiers or paths) | app ids are store-published facts |
| `librarycache/1238040.json` | none needed (no achievements recorded; no user data) | avoids copyrighted achievement text |
| account directory name | `userdata/<accountid>/` → `userdata/00000000/` | removes account id while preserving the layout |

Preserved deliberately: app ids, game names, `installdir`, sizes, depot ids, build ids,
`StateFlags`, key order, indentation (tabs), LF line endings, and empty-object shapes.

Not captured at all: `config/loginusers.vdf`, `config/localconfig.vdf`, `registry.vdf`
(username, `AutoLoginUser`, offline ticket/signature material), Proton prefix contents,
`tracked_files`, shader caches, logs, artwork, executables.

## Source integrity

The seven source files were hashed before capture and again after, with `sha256sum -c`:

```
6262887f979cb165943027873add2dcdd77b53cad55d8e6828fa23150d3f4937  .../steamapps/libraryfolders.vdf
e89124bf9bd60176fa97a3f980c915cc83a46cfccae976ecbaf6f783bd60ff24  .../steamapps/appmanifest_427520.acf
6330a06adf31e8107a34abe0fa5188df017d082a051e5fe128010d035d06ec2c  .../steamapps/appmanifest_1238040.acf
ac7b54aa51d0b10818b3ca2054d78603d76a7593e82d75cc247e33f203370f77  .../steamapps/appmanifest_1493710.acf
310806d53eeb033afb29cf8bf9346b77802acbbf55e17c0b5aaa1a0bae4402ee  .../steamapps/compatdata/1238040/config_info
f8944eecbcb19bf621a3ffda720e575273907de21b2ef21bebeed84e547a79f6  .../userdata/<accountid>/config/compat.vdf
2200afe5bd5dccc0cfe9d34b29eedc49014dd673e5b9b2d1797e3f52a14b5617  .../userdata/<accountid>/config/librarycache/1238040.json
```

All seven verified `OK` after capture. No source file was modified. Only reads were performed
under `~/.steam` and `~/.local/share/Steam`; no game was launched.

## Narrow validation performed

No product code was added. A throwaway Python validator (`/tmp/opencode/`, outside the
repository) parsed every proposed file with a strict KeyValues text parser and checked:

- `libraryfolders.vdf`: exactly two libraries; absolute `path`; both `apps` maps; every app id
  and size numeric; library 1 path is the unavailable `/mnt/external/SteamLibrary`.
- Each `.acf`: `AppState` root; file name matches `appid`; expected `name`, `installdir`,
  `StateFlags` (`4`, `4`, `6`); installed bit set in all three; `SizeOnDisk` numeric;
  `lastupdated == 1700000000`; `LastPlayed == 0`; sentinel `LastOwner`; `InstalledDepots`
  non-empty; `UserConfig`/`MountedConfig` present.
- `appmanifest_1238040.acf`: `SharedDepots` decode correctly; `InstallScripts["1295714"]`
  decodes to `addins\da2_prc_one\installScript.vdf` (the raw file contains `\\`); at least one
  depot has `dlcappid`.
- `compat.vdf`: `platform_overrides["1238040"] == {dest: linux, src: windows}`; `427520` has
  no override.
- `config_info`: 14 logical lines, version line `11.0-100`, no non-`/home/test` absolute path.
- `librarycache` JSON parses as an array whose first element is `achievements` with
  `nTotal == 0`.
- Residual-identifier scan: no `sean`, no source SteamID64, no `<accountid>`, no `bazzite`,
  no `/run/media`, no `/home/sean` in any proposed file.

Result: `OK: 9 files parsed and checked`. `sha256sum -c SHA256SUMS` also passed.

The repository has no product Steam parser yet, so there is no project test to run against
this corpus; the throwaway validator stands in for it, and the proposal README tells the
fixture owner to substitute `/home/test` with a temporary root.

## Real titles recorded for later acceptance

| Class | Appid | Name | Install path (relative to Steam root) |
| --- | --- | --- | --- |
| Native Linux | 427520 | Factorio | `steamapps/common/Factorio` |
| Proton | 1238040 | Dragon Age II: Ultimate Edition | `steamapps/common/Dragon Age II` |

Evidence: Factorio has a native `bin/x64/factorio` launcher, no `compatdata/427520`, and no
`platform_overrides` entry. Dragon Age II has `compatdata/1238040/pfx`, a `config_info`, and
a `platform_overrides` entry. A second native title, Terraria (105600), was found but rejected
as the corpus representative because it has an empty `compatdata/105600/` directory, which
would make a naive "compatdata means Proton" test look correct for the wrong reason.

Install paths are given relative to the Steam root (`~/.local/share/Steam`) so the public
record does not repeat the home-directory username.

## Open questions for the reviewer

1. Approve the nine exact destination paths above, with the `oracle-change` label and a second
   reviewer, so a separate change can copy them into `tests/fixtures/steam/real/`.
2. Confirm the corpus location (`tests/fixtures/steam/real/`) and the `/home/test` substitution
   convention, or direct a different one.
3. Confirm that a real Proton-title launch and a real Flatpak-hosted launch remain Phase 2
   acceptance gates; this task did not launch anything.

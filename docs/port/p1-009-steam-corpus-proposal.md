# p1-009 Steam corpus proposal (corrected) — protected-path approval required before writes

No protected path has been written. This revision corrects the corpus after the two
independent reviews of PR #14. The corrected corpus is staged outside the checkout in the task
data directory under `p1-009-corpus/proposal/tests/fixtures/steam/real/`; it is not in this
repository. Worktree branch: `fm/p1-009-corpus`. Spec: `docs/port/08-STEAM-SPEC.md`.

This document is the only repository change. The corpus files are **not** in this PR; they are
proposed destinations for a later, separately approved change. The merged spec is unchanged;
see "Why the spec is unchanged".

## Corrections from the PR #14 reviews

| Review finding | Correction |
| --- | --- |
| P2: the corpus README leaked the source home username and the validation claim was false | README rewritten with neutral placeholders; no source home username, no source account id and no real SteamID64 remain. The residual scan was re-run for real and its output is recorded below. |
| P2: no native empty-`compatdata` counterexample | Added Terraria (105600) manifest plus `steamapps/compatdata/105600/` containing only a `.gitkeep` placeholder, so a naive "compatdata means Proton" rule fails on the fixture. |
| P2: real compatibility-tool mapping missing | Checked `config/config.vdf` and `userdata/<accountid>/config/localconfig.vdf` on the host: there is no `CompatToolMapping` and no per-app Proton key. Recorded explicitly below as a corpus limitation; no mapping was invented. |
| P3: `config_info` described as ten lines | README and proposal now say 14 logical lines (13 newline-terminated plus a final line without a trailing newline). |
| P3: unsupported "two copies were byte-identical" claim | Both source copies were hashed during this correction; `steamapps/libraryfolders.vdf` and `config/libraryfolders.vdf` are both `6262887f…` (full hash below). One copy is proposed because the two are byte-identical and exercise the same rule. |
| P3: absolute personal path in the proposal document | The staging location is described relative to the task data directory; no personal absolute path appears in this document. |
| P3: over-stated "strict KeyValues parser parsed every file" | Validation now says exactly what parsed each type: KeyValues text for the six VDF/ACF files, JSON for the JSON file, emptiness for the placeholder, and hash verification for `SHA256SUMS`. |
| P3: padded maps / minimality | The payloads are unmodified real captures except for redaction; trimming would make them hand-edited and weaken provenance. Justification is recorded under "Minimality". |

## Requested independent second-reviewer approval

Add exactly these new files under the protected `tests/fixtures/` root (an `oracle-change` label
and a second reviewer are required):

| # | Proposed destination | SHA-256 | Reason it is needed |
| --- | --- | --- | --- |
| 1 | `tests/fixtures/steam/real/README.md` | `d5769bc695aae15660a662e37efd3e72c5dc317cf5a69196cdd1f9abb02de2aa` | Corpus description, provenance, redaction record, and the explicit list of what the corpus does not prove |
| 2 | `tests/fixtures/steam/real/SHA256SUMS` | `a959d618d04597deda9ca126fb15821c04113dd967ab24b4e4f794d9a338cf8e` | Hash list covering the README and all nine payload files; does not self-hash |
| 3 | `tests/fixtures/steam/real/steamapps/libraryfolders.vdf` | `6fc3f9a0dd9322369493f314083c9764374625e4dd28df6a49de11c989b9868c` | Modern two-library VDF; library 1 path is unavailable; preserves the real `apps` appid→bytes maps |
| 4 | `tests/fixtures/steam/real/steamapps/appmanifest_427520.acf` | `1ad3b0041c87aecf33af3ca7fa59c209f866b265ada6fd0e58055f7ab22aaa92` | Factorio, installed native Linux title with no `compatdata` directory |
| 5 | `tests/fixtures/steam/real/steamapps/appmanifest_1238040.acf` | `2255ee141342055b4dc68822074d5109f866699db124bc681de04ff762c6c6c5` | Dragon Age II: Ultimate Edition, installed Proton title; `dlcappid`, `SharedDepots`, escaped `InstallScripts` paths |
| 6 | `tests/fixtures/steam/real/steamapps/appmanifest_1493710.acf` | `db7e4dd085612c45181515bd7bd56253e7eb5865e59a4a9d69697421c07d6bb9` | Proton Experimental runtime; `StateFlags 6` (installed + update pending), empty `UserConfig`/`MountedConfig` |
| 7 | `tests/fixtures/steam/real/steamapps/appmanifest_105600.acf` | `9f63d860edf98ae96cdc5ab20d812036f3b28708df5a8ba86fc0a6bb48d802aa` | Terraria, installed native title whose `compatdata/105600/` is empty of Steam data; makes "compatdata alone is not proof of Proton" fail under a naive check |
| 8 | `tests/fixtures/steam/real/steamapps/compatdata/105600/.gitkeep` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | Empty-file placeholder so git can carry the otherwise-empty `compatdata/105600/` directory; not Steam data |
| 9 | `tests/fixtures/steam/real/steamapps/compatdata/1238040/config_info` | `43076bbbc877b8aa023ee1eacead9a7ab4213580414d60711b3b098003926212` | Proton prefix marker; 14-logical-line runtime version/paths/DLL override shape |
| 10 | `tests/fixtures/steam/real/userdata/00000000/config/compat.vdf` | `f8944eecbcb19bf621a3ffda720e575273907de21b2ef21bebeed84e547a79f6` | Per-account `platform_overrides` map (`dest linux`, `src windows`); native apps have no entry |
| 11 | `tests/fixtures/steam/real/userdata/00000000/config/librarycache/1238040.json` | `2200afe5bd5dccc0cfe9d34b29eedc49014dd673e5b9b2d1797e3f52a14b5617` | Representative `librarycache` JSON (empty achievements, no copyrighted text) |

No existing fixture, golden, baseline, parity, formal, legacy, or oracle-lock file changes.
No artwork, executables, prefix contents, registry files, credentials, or large binaries.
`tests/fixtures/steam/` (synthetic corpus) is unchanged and is not part of this proposal.

`shortcuts.vdf` is **absent** from the source install and is intentionally not proposed. Do not
invent one. `config.vdf` and `localconfig.vdf` are also not proposed; see the compatibility-tool
section.

## The native counterexample (P2)

On this host, Terraria (105600) has an appmanifest and a `compatdata/105600/` directory that
contains no `config_info`, no `pfx/`, and no other Steam data. The directory is empty apart from
the `.gitkeep` placeholder this corpus adds so git can represent it. Factorio (427520) has no
`compatdata/427520` directory at all, and neither native title has a `platform_overrides` entry.

The fixture therefore contains three cases a Proton detector must distinguish:

1. native with no `compatdata` (427520);
2. native with an empty `compatdata/<appid>` (105600 — must **not** be Proton);
3. Proton with `compatdata/<appid>/config_info` and a `platform_overrides` entry (1238040).

A test that treats "`compatdata/<appid>` exists" as proof of Proton now fails on 105600, which
is what the spec's §5.3 rule requires.

## Compatibility-tool mapping (P2)

The known Steam location for per-app and default compatibility-tool selection is
`config/config.vdf` → `InstallConfigStore` → `Software` → `Valve` → `Steam` →
`CompatToolMapping`. On this host:

- `config/config.vdf` has **no `CompatToolMapping` section**;
- `userdata/<accountid>/config/localconfig.vdf` has no per-app Proton key (only shader-cache and
  deck-feedback bookkeeping);
- `compat.vdf`'s `platform_overrides` is a different marker and does not name a tool or version.

No real mapping exists on this host, so the corpus cannot exercise per-app/global tool or
Proton-version selection. That is a real coverage limitation, not a claim that the format is
unknown: the location above is Steam's known layout. A bounded probe that sets a tool, or an
explicit synthetic fixture, is needed before v1 may claim tool selection. No mapping was
invented for this corpus.

## Redaction record

Applied with a read-only script (source opened for read, output written only under the task data
directory):

| File | Redaction | Why |
| --- | --- | --- |
| `libraryfolders.vdf` | `/home/<user>` → `/home/test`; removable-drive path → `/mnt/external/SteamLibrary`; `contentid`, `totalsize`, `update_clean_bytes_tally` → `0`; `time_last_update_verified` → `1700000000` | removes home username, mount user, device/library identity, and update timestamps; keeps every key and the `apps` shape |
| `appmanifest_427520.acf`, `appmanifest_1238040.acf`, `appmanifest_1493710.acf`, `appmanifest_105600.acf` | `LastOwner` SteamID64 → sentinel `76561190000000000`; `lastupdated` → `1700000000`; `LastPlayed` → `0`; `ScheduledAutoUpdate` → `0` | removes account id and recent-play/update timestamps; keeps numeric string shape |
| `compatdata/1238040/config_info` | `/home/<user>` → `/home/test` | removes username and absolute home paths; keeps line order and field shapes |
| `compat.vdf` | none needed (app ids only; no account identifiers or paths) | app ids are store-published facts |
| `librarycache/1238040.json` | none needed (no achievements recorded; no user data) | avoids copyrighted achievement text and image URLs |
| account directory name | `userdata/<accountid>/` → `userdata/00000000/` | removes account id while preserving the layout |
| `compatdata/105600/.gitkeep` | added empty placeholder | git cannot track the empty counterexample directory otherwise; not Steam data |

Preserved deliberately: app ids, game names, `installdir`, sizes, depot ids, build ids,
`StateFlags`, key order, indentation (tabs), LF line endings, and empty-object shapes. Redaction
changed only the values listed above.

Not captured at all: `config/config.vdf`, `config/loginusers.vdf`,
`config/localconfig.vdf`, `registry.vdf` (username, `AutoLoginUser`, offline ticket/signature
material), Proton prefix contents, `tracked_files`, shader caches, logs, artwork, executables.
The raw source-hash evidence file that contains real absolute paths and the account id is kept
outside the repository and is not attached to the PR; only the relative paths and digests are
reproduced here.

## Source integrity

Source digests were taken with `sha256sum` immediately before capture and re-checked after; no
source file was modified. At this correction pass both `libraryfolders.vdf` copies were hashed
separately, which supports the spec's "two copies were byte-identical" claim:

```
6262887f979cb165943027873add2dcdd77b53cad55d8e6828fa23150d3f4937  <root>/steamapps/libraryfolders.vdf
6262887f979cb165943027873add2dcdd77b53cad55d8e6828fa23150d3f4937  <root>/config/libraryfolders.vdf
e89124bf9bd60176fa97a3f980c915cc83a46cfccae976ecbaf6f783bd60ff24  <root>/steamapps/appmanifest_427520.acf
6330a06adf31e8107a34abe0fa5188df017d082a051e5fe128010d035d06ec2c  <root>/steamapps/appmanifest_1238040.acf
ac7b54aa51d0b10818b3ca2054d78603d76a7593e82d75cc247e33f203370f77  <root>/steamapps/appmanifest_1493710.acf
9783aa954ac70f77b5d31f9b16f444dd77ac524a6c97c1841093d6edd49ca8e0  <root>/steamapps/appmanifest_105600.acf
310806d53eeb033afb29cf8bf9346b77802acbbf55e17c0b5aaa1a0bae4402ee  <root>/steamapps/compatdata/1238040/config_info
f8944eecbcb19bf621a3ffda720e575273907de21b2ef21bebeed84e547a79f6  <root>/userdata/<accountid>/config/compat.vdf
2200afe5bd5dccc0cfe9d34b29eedc49014dd673e5b9b2d1797e3f52a14b5617  <root>/userdata/<accountid>/config/librarycache/1238040.json
```

`<root>` is the Steam root (`~/.local/share/Steam`); `<accountid>` is the per-account userdata
directory. Neither placeholder is expanded in this document.

## Narrow validation performed

No product code was added. A throwaway validator lived in the task data directory (outside the
repository). It applied a type-appropriate check to every staged file:

- **KeyValues text** (strict parser: quoted/unquoted tokens, `//` comments, `\\`, `\"`, `\n`,
  `\t`, duplicate-key rejection, balanced braces) for `libraryfolders.vdf`, the four `.acf`
  manifests, and `compat.vdf` — 6 files.
- **JSON** (`json.load`) for `librarycache/1238040.json`.
- **Empty-file check** for `compatdata/105600/.gitkeep`.
- **Hash verification** for the 10 entries in `SHA256SUMS`; `README.md` and `SHA256SUMS` are
  text/hash-list files and are not claimed to be KeyValues.

The validator made 87 assertions: two libraries; absolute and unavailable second path; numeric
app maps; each manifest's `appid`/`name`/`StateFlags` (4, 4, 6, 4); installed bit; redacted
timestamps and sentinel; non-empty `InstalledDepots`; `SharedDepots`; decoded
`InstallScripts["1295714"] == addins\da2_prc_one\installScript.vdf`; at least one `dlcappid`;
`buildid != TargetBuildID` and `BytesToDownload > 0` for 1493710; `platform_overrides` for
1238040 and none for 427520 or 105600; `config_info` 14 lines with only `/home/test` absolute
paths and no final newline; the 105600 directory contains only `.gitkeep` and no `config_info`
or `pfx`; the JSON has `nTotal == 0` and `nAchieved == 0`; and every `SHA256SUMS` hash matches.

`sha256sum -c SHA256SUMS` (run in the corpus root) printed:

```
README.md: OK
steamapps/appmanifest_105600.acf: OK
steamapps/appmanifest_1238040.acf: OK
steamapps/appmanifest_1493710.acf: OK
steamapps/appmanifest_427520.acf: OK
steamapps/compatdata/105600/.gitkeep: OK
steamapps/compatdata/1238040/config_info: OK
steamapps/libraryfolders.vdf: OK
userdata/00000000/config/compat.vdf: OK
userdata/00000000/config/librarycache/1238040.json: OK
```

Residual scan over the corpus tree and this document (source home username,
source account id, host name, removable-drive paths, non-placeholder home-directory prefixes, real
SteamID64 values, NUL/binary/image content) printed:

```
Scanned 12 files.

Allowed placeholders/sentinels observed:
  .../README.md: placeholder /home/<user>
  .../README.md: placeholder /home/test
  .../README.md: sentinel SteamID64 76561190000000000
  .../appmanifest_105600.acf: sentinel SteamID64 76561190000000000
  .../appmanifest_1238040.acf: sentinel SteamID64 76561190000000000
  .../appmanifest_1493710.acf: sentinel SteamID64 76561190000000000
  .../appmanifest_427520.acf: sentinel SteamID64 76561190000000000
  .../config_info: placeholder /home/test
  .../libraryfolders.vdf: placeholder /home/test

FORBIDDEN identifiers/traces: 0
```

(Output elides repeated per-file placeholder lines; the scan's full output is retained in the
task data directory. The count is 11 corpus files plus this document.)

All home-directory prefixes are `/home/test` in payloads or the documented `/home/<user>` /
`/home/test` placeholders in the README. The only SteamID64-shaped value is the documented
sentinel `76561190000000000`; there is no source account id and no real SteamID64.

Content safety: `file` reports ASCII text or JSON text for every staged file; `.gitkeep` is
empty; there are no images, executables, archives, NUL bytes, or achievement text. The
`librarycache` JSON is the empty-achievement variant.

The repository has no product Steam parser yet, so there is no project test to run against this
corpus; the throwaway validator stands in for it, and the proposal README tells the fixture
owner to substitute `/home/test` with a temporary root. No dotnet build, packaging, benchmark,
or game launch was performed.

## What the corpus does NOT prove

- **Compatibility-tool selection.** This host has no `CompatToolMapping`, so per-app/global tool
  and Proton-version selection is untested (see above).
- **Artwork.** No images are included; the `appcache/librarycache` layout and fallback order are
  documented in the spec but not exercised by this corpus. A licence-safe synthetic image or an
  explicit acceptance decision is still needed if v1 claims artwork coverage.
- **Divergent `config/libraryfolders.vdf`.** Both source copies were byte-identical and only the
  `steamapps/` copy is proposed, so the corpus cannot exercise a client choosing between two
  differing copies. It does evidence the spec's byte-identical observation via both source
  hashes.
- **Legacy `libraryfolders.vdf` index form.** Not present on the host and not synthesized here.
- **`\"` and `\n` escapes.** Not observed; this corpus exercises only `\\` escaping.
- **`//` comments.** No `compatibilitytool.vdf` is included, so comment support is not exercised
  by this corpus even though the spec requires it for tool manifests.
- **Duplicate-key policy and malformed VDF.** Not represented by this real corpus; the synthetic
  `tests/fixtures/steam/broken/` corpus covers the malformed case.
- **`StateFlags` bits other than 4 and the update-pending bit.** Only `4` and `6` appear.
- **Multiple-account selection, offline mode, `loginusers.vdf`/`localconfig.vdf`/`registry.vdf`
  fields.** Not captured because they carry account/credential-like data.
- **Host observation counts and process chain.** The corpus carries four manifests and no
  artwork cache, so it cannot reproduce the spec's 83-manifest, 1374-artwork-directory or
  1150-header counts, nor the pressure-vessel process chain. Those remain author observations,
  not corpus-proven facts.
- **Launch options, Flatpak host behaviour, process attribution, CDN artwork, `shortcuts.vdf`.**
  Unchanged Phase 2/unknown items from the spec; no fixture here proves them.
- **A real installed game tree.** The corpus carries metadata only; `installdir` directories do
  not exist, so installed-vs-broken path checks need a prepared test root.

## Minimality

The reviewer flagged the padding (83 + 25 app rows, 45 `platform_overrides` entries, many DLC
depots). The payloads are unmodified real captures apart from the redaction listed above.
Trimming them would turn real host evidence into hand-authored files and would undermine the
provenance and byte-level hash record. The whole file is also what a parser must consume: the
extra rows exercise numeric app-id/size strings, large maps, unknown-key tolerance and ordering
without adding a distinct spec rule, and they are inert for the targeted assertions. The single
non-captured file is the empty `.gitkeep`; nothing else was synthesized. If the captain prefers a
strictly minimal hand-trimmed corpus, that is a different artifact and should be proposed and
labelled as synthetic.

## Why the spec is unchanged

`docs/port/08-STEAM-SPEC.md` is not modified in this PR:

- its `config_info` line count already says 14;
- its "two copies were byte-identical" claim was verified during this correction (both source
  hashes are above), so it is now evidenced rather than dropped;
- its "no `CompatToolMapping` was found on this host" statement is still true;
- review B's `LastPlayed` model observation is a v1 design choice, not a factual error in the
  corpus, and is left to the spec owner rather than changed here.

## Open questions for the reviewer

1. Approve the eleven exact destination paths above, with the `oracle-change` label and a second
   reviewer, so a separate change can copy them into `tests/fixtures/steam/real/`.
2. Confirm the corpus location (`tests/fixtures/steam/real/`) and the `/home/test` substitution
   convention, or direct a different one.
3. Confirm that a real Proton-title launch and a real Flatpak-hosted launch remain Phase 2
   acceptance gates; this task did not launch anything.
4. Confirm that the missing `CompatToolMapping` and artwork cases are acceptable as documented
   gaps for the first protected intake, or require synthetic fixtures before intake.

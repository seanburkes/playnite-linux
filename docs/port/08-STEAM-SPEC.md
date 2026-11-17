# Steam subsystem specification (v1)

Status: greenfield specification with a real-host evidence record. Steam has no v10
implementation in `legacy/` (ADR-017), so this document — not a port — defines v1 behaviour.
It is grounded in files inspected on a Linux host on **2026-11-14**; claims that could not be
checked on that host are marked **UNKNOWN** or **DECISION** rather than asserted.

Evidence labels used below:

- **[host]** — directly observed in a file or process on the capture host.
- **[decision]** — a v1 product/implementation choice, not a Steam-observed fact.
- **[unknown]** — not verified; do not treat as behaviour until a test or probe covers it.
- **[v10]** — reference from the frozen Windows oracle in `legacy/`.

Source files were read only. No file under `~/.steam`, `~/.local/share/Steam`, or any game
install directory was modified during capture. The redacted oracle corpus proposed from this
evidence is described in `docs/port/p1-009-steam-corpus-proposal.md`; it is **not** committed
until the `oracle-change` route lands.

## 1. Roots and discovery

### 1.1 Observed layout [host]

- `~/.steam/steam` is a symlink to `~/.local/share/Steam`; `~/.steam/root` resolves to the
  same realpath. `readlink -f` on both candidates returns one directory.
- The client is a distribution package: `/usr/bin/steam` execs `/usr/lib/steam/steam`.
- Root markers present: `steamapps/`, `steamapps/libraryfolders.vdf`, `config/config.vdf`,
  `config/loginusers.vdf`, `userdata/`, `registry.vdf`, `steam.pid`, `steam.pipe`.
- The client's helper chain ran under the Steam Runtime (pressure-vessel): process names
  `pv-adverb`, `steam-runtime-launcher-service`, `srt-logger`, and `steamwebhelper` were
  observed around the client's PID. A bare `steam` process tree is not a safe model.

### 1.2 Resolution order [decision]

Resolve, in order, the first root whose realpath exists and contains `steamapps`:

1. `$STEAM_ROOT` (explicit override; absolute path)
2. `~/.steam/steam`
3. `~/.local/share/Steam`
4. `~/.var/app/com.valvesoftware.Steam/.local/share/Steam` (Flatpak)

Deduplicate candidates by realpath so `~/.steam/steam` and `~/.local/share/Steam` do not
produce two libraries. Report every candidate considered and why it was rejected.

`steam.pid`, `steam.pipe`, and `steam.token` are not proof of a live client; treat a stale
`steam.pid` as a normal state. **[decision]**

Flatpak candidate: **UNKNOWN**. The capture host has no `flatpak` binary, no `flatpak-spawn`,
and no `~/.var/app/com.valvesoftware.Steam`, so the Flatpak layout and host transport were
not verified. The path above is the planned v1 probe, not observed fact.

### 1.3 Discovery decisions [decision]

- Candidates are probed in the order above. Every candidate is still reported after one is
  accepted: a later candidate that resolves to the accepted realpath is `duplicate-realpath`
  (the usual home `.steam/steam` link), and a different usable root is `shadowed`. Only the first
  accepted root produces libraries.
- A non-absolute `$STEAM_ROOT` is ignored rather than resolved against the working directory
  (`invalid-path`); an existing non-directory is `not-directory`; a failed probe such as a
  symlink loop is `unreadable` and never escapes as an exception.
- A candidate counts only when its realpath directory contains `steamapps`, and that
  `steamapps` must itself resolve inside the candidate realpath. A link that leaves the root is
  `steamapps-outside-root` and is not followed.
- Discovery only reads: it never creates, modifies or repairs anything under a Steam tree, and
  uses no network, process or UI surface.

## 2. Libraries and `libraryfolders.vdf`

### 2.1 Observed file [host]

Two copies were present and byte-identical at capture:

- `<root>/steamapps/libraryfolders.vdf`
- `<root>/config/libraryfolders.vdf`

Contents are Valve KeyValues text with root key `libraryfolders`, numeric object keys
(`"0"`, `"1"`), and per-library fields:

| Field | Observed meaning |
| --- | --- |
| `path` | absolute library root |
| `label` | user label, often empty |
| `contentid` | Steam library content id |
| `totalsize` | reported library capacity in bytes |
| `update_clean_bytes_tally` | Steam bookkeeping |
| `time_last_update_verified` | Unix seconds |
| `apps` | object mapping appid → `SizeOnDisk` bytes |

Library 0 pointed at the Steam root itself. Library 1 pointed at a removable-drive path
(`/run/media/<user>/My Passport/SteamLibrary`) that **did not exist** at capture, so that
library was unavailable while its `apps` map still listed installed games. This is the
canonical "library on an unmounted path" case; see §9.

### 2.2 Parsing rules [decision]

- Accept both dialects:
  - modern: `"0" { "path" "..." ... }`;
  - legacy index form: `"1" "/path"`.
    **[unknown]** — the legacy form was not present on the capture host; it is accepted by
    policy for older clients, with a test to be written before release.
- A library is usable only if `<path>/steamapps` is an existing directory. Report other
  entries as `Unavailable` with the reason; keep their apps visible but not launchable.
- Do not drop a library permanently when its path is missing; re-probe on refresh/mount.
- Duplicate app ids across libraries: first library in file order wins; report the duplicate.
  **[decision]**
- Ignore unknown keys and unknown nested objects; do not fail the whole file for one bad entry.
- Reject a file with unbalanced braces; report path and byte offset, skip that file, keep the
  root library. **[decision]**
- A path that escapes the declared root is not automatically invalid (libraries legitimately
  live elsewhere), but a non-absolute path is reported and skipped. **[decision]**

### 2.3 Discovery decisions [decision]

- `<root>/steamapps/libraryfolders.vdf` is read first; `<root>/config/libraryfolders.vdf` is the
  fallback only when the first copy is absent. A library file that is missing, unreadable,
  oversized or syntactically invalid is skipped and the root library is kept; the reason and
  byte offset are reported. A file that resolves outside the root realpath is not read.
- The root is always a library. When no valid file entry resolves to the root realpath, a
  synthetic `root` entry is added first with an empty apps map; app manifests are a later unit.
- Libraries are deduplicated by realpath in file order. The first entry is the library; a later
  entry resolving to the same realpath keeps its declared apps but is marked `Unavailable` with
  reason `duplicate-library` and reported.
- A library is available only when its realpath exists, is a directory, and contains a
  `steamapps` directory whose realpath stays inside the library root. A `steamapps` link that
  escapes is not followed. Unavailable entries keep their apps map visible, and the next
  discovery call re-probes them; nothing is deleted or repaired.

## 3. Valve KeyValues text grammar

### 3.1 Observed dialect [host]

- Quoted strings, `{`/`}` objects, tab separators, LF line endings, no BOM, ASCII or UTF-8.
- Nested objects occur to at least four levels (`AppState → InstalledDepots → <depot> → size`).
- Values are always strings, including numbers and timestamps; parse types at the model layer.
- Empty objects occur: `"UserConfig" { }`, `"MountedConfig" { }` (appmanifest), and empty
  `apps`-style maps are legal.
- UTF-8 in game names occurs (registered/trademark symbols, accented text). Compare and store
  as UTF-8; never assume ASCII.
- Escaped backslashes occur in `InstallScripts` values, e.g. a raw value
  `addins\\da2_prc_one\\installScript.vdf` decodes to `addins\da2_prc_one\installScript.vdf`.
  These are Windows-relative paths embedded in Linux-side metadata: decode them, but never
  treat `\` as a POSIX separator and never execute them.
- `\"` and `\n` escapes were **not** observed in app manifests or `libraryfolders.vdf` on this
  host. **[unknown]** — define and test them in the parser; do not silently pass a backslash
  through undecoded.
- `//` line comments occur in `compatibilitytools.d/<tool>/compatibilitytool.vdf` (see §5.4)
  but not in the app manifests or `libraryfolders.vdf` inspected. **[host]** — comment support
  is required for tool manifests.
- Key casing is inconsistent and must be preserved by exact lookups:
  `lastupdated`, `buildid` are lowercase; `StateFlags`, `LastPlayed`, `LastOwner`,
  `SizeOnDisk`, `InstalledDepots`, `SharedDepots`, `InstallScripts`, `UserConfig`,
  `MountedConfig` are mixed case. **[host]**
- Duplicate keys inside one object were not observed. **[decision]** — exact-case duplicates
  reject the file; case-distinct keys remain distinct. See the p5-001 contract below.
- Manifests were mode `0755` on the capture host; file mode carries no meaning. **[host]**

### 3.2 Error handling [decision]

Parsing is all-or-nothing per file: on a syntax error, report the file, line/offset, and a
stable reason code; the caller skips that app or library. Never return a partial object as if
it were complete. Never crash on malformed input from disk.

#### p5-001 approved parser contract [decision]

This table resolves the parser-policy unknowns, not Steam's undocumented behaviour. The
required-field/mismatch policy here supersedes the earlier mismatch-only reporting rule.

| Surface | Accepted / retained | Rejected / reported |
| --- | --- | --- |
| Document | Exactly one named root **object**; nested objects including empty objects; quoted or unquoted keys and scalar values. | Missing root/object, incomplete key/value, unbalanced braces, or any non-comment/non-whitespace content after the root. No partial tree. |
| Bare tokens | UTF-8 bytes until space, tab, LF, CRLF, quote, brace, or `//`; `/` alone is ordinary text. Quotes/braces delimit without requiring whitespace. | Empty bare tokens; a missing value before `}`/EOF. |
| Encoding | Strict UTF-8, without Unicode/case normalization; optionally **one** leading UTF-8 BOM; LF and CRLF. U+FEFF inside a quoted value is text, not another file BOM. | Malformed/overlong UTF-8, surrogate encodings, out-of-range scalars, truncated sequences; a repeated leading BOM; lone CR; NUL, C0/C1 controls and DEL except tab/LF/paired CRLF. Raw LF/CR in quoted strings rejects. |
| Escapes | In either token form: `\\`, `\"`, `\n`, `\t`, `\r` decode exactly once. Backslash is never a POSIX separator. | Any other escape, or EOF immediately after a backslash. No undecoded passthrough. |
| Comments | `//` through LF/CRLF/EOF outside quoted strings, including after the root. | Block comments are not a comment dialect; text after the root rejects as trailing garbage. Controls/invalid UTF-8 reject even inside comments. |
| Keys | Exact ordinal lookup, casing and file order preserved; case-distinct keys coexist. | Exact-case duplicate keys in the same object reject with key name and byte offset; never first/last-wins inside an object. |
| Bounds | Default 65,536 input bytes, configurable positive byte limit; at most 64 object levels including the root. | Oversized input before decoding/allocation; level 65 before recursion. Stable `size-limit`/`depth-limit` reasons. |
| Error locations | Original filename, zero-based UTF-8 byte offset (including BOM/CRLF), one-based line, stable reason; duplicate diagnostics include the key, never scalar data. | A model syntax skip retains filename appid or `libraryfolders` identity and byte offset; raw values are never placed in skip reports. |
| ACF root / identity | Root exactly `AppState`; filename `appmanifest_<uint decimal>.acf` supplies skip identity. Required scalar `appid` is strict uint decimal **and must match** the filename. Leading zeroes are decimal, not octal. | Missing/wrong/multiple roots; malformed filename; missing/invalid/mismatched appid skip the app. A mismatch does not import either identity. |
| Required ACF fields | `name` nonempty after decoding; `installdir` nonempty relative POSIX path; `StateFlags` strict uint decimal. Internal `a/../b` is allowed; normalization stays `Path.GetFullPath` only. | Missing, object-valued, empty or invalid required field skips with filename identity and field/reason. Absolute installdir or any `..` traversal above the install root rejects; `.`/paths resolving to the root itself reject. No synthetic defaults. |
| Optional numbers | `SizeOnDisk`, `StagingSize`, `Universe`, `LastPlayed`, `buildid`, update/progress fields, update settings; strict ulong decimal. `lastupdated`, `LastPlayed`, `ScheduledAutoUpdate` must also fit nonnegative Unix seconds in DateTimeOffset. | Missing stays absent. Invalid/overflowing values stay absent and report field/reason without value. `LastUpdated` is unknown, not an alias for `lastupdated`. `LastPlayed = 0` stays the never-played sentinel, not an inferred play session. |
| Optional objects | `InstalledDepots` (uint depot id; ulong manifest/size; optional uint dlcappid), `SharedDepots` (uint depot/owner appid), scalar `InstallScripts`, `UserConfig`, `MountedConfig`; empty objects are distinct from missing. | Wrong object shape reports and stays absent/empty. Bad depot/shared/script entries report and are omitted; invalid optional dlcappid is absent. Script paths are decoded metadata, **never executed**. |
| Privacy / unknown fields | Unknown fields/objects retained recursively in an ordered bag. | `LastOwner` is validated, then discarded; it never enters typed or unknown model fields, display, record formatting or diagnostics. Diagnostics never contain play timestamps. |
| Libraries | Root exactly `libraryfolders`; numeric entries in file order; modern object or legacy scalar path. Each path must be nonempty absolute; `Path.GetFullPath` only, with no filesystem access. Optional label/contentid/totalsize/bookkeeping/apps. | Bad path skips only that library with index identity. Invalid optional label/numbers report without guessing. Malformed apps map reports and becomes **entirely empty**, keeping the library. Unknown library/root fields retained. |
| Library app ids | Strict uint appid → ulong byte size. Across valid libraries, first in file order wins and each duplicate is reported with later library index and appid. | Bad map keys/values or numerically duplicate ids (e.g. `42` and `042`) invalidate the map. Exact textual duplicates already reject the whole syntax file. |
| Pure app projection | Caller supplies library/install availability and Proton observation. Priority: unavailable library → `Unavailable`; installed bit + missing directory → `Broken`; unknown flag bits → `Unknown`; installed + bit 2 → `UpdatePending`; installed only → `Installed`; otherwise `Unknown`. Raw flags always retained. | No filesystem probes, root discovery, mounting, launch, network, Proton guess, or unknown flag names in this unit. `MountedConfig` is retained; mount options/drive ids/filesystem hints remain UNKNOWN. |
| Writing / fixtures | Canonical quoted UTF-8 writing preserves tree semantics/casing/order and supported escapes. Synthetic seeds in `tools/Playnite.Fuzz/Corpus/p5-001/` are authored from this spec, hash-pinned, structurally round-tripped and fuzzed. | Whitespace/comments/BOM are not byte-round-tripped. No real-install corpus, protected fixture changes or oracle re-acceptance. |

`SteamManifest.SizeOnDisk` and the projected app size are nullable `ulong`: this is the explicit
optional-field refinement of the earlier app sketch, so a missing/invalid size is never fabricated
as zero. The parser retains decoded `installdir`; `SteamApp.InstallPath` combines it under the
absolute normalized library path using `Path.GetFullPath`. Availability and Proton evidence are
the later discovery unit's responsibility. Missing-manifest reports likewise require that unit's
file enumeration; this layer never invents a manifest for a library app id.

### Follow-up boundary clarifications (p5-001)

- `InstalledDepots`, `SharedDepots`, and `InstallScripts` use nullable read-only dictionaries:
  missing or wrong-shaped objects are `null`, with a diagnostic for the latter; a present empty
  object has count 0. `UserConfig` and `MountedConfig` use the same distinction with nullable
  lists. Invalid members are omitted from present objects rather than erasing their presence.
- `SteamLibrary.Apps` is also nullable: missing is `null`, present empty is count 0. A malformed
  present apps map reports and becomes a non-null empty map, preserving the existing all-or-nothing
  map policy. Numerically ambiguous library indexes (such as `0` and `00`) reject the whole file
  with `duplicate-library-index`, both raw indexes, and the later index's byte offset; discovery's
  implicit default Steam library is unaffected by this parser rejection.
- Diagnostic `File` and syntax-exception `File` contain only the basename, never caller directory
  prefixes. Caller filename arguments have trailing POSIX slashes removed before taking the
  basename; empty/root-only names and embedded null bytes are rejected as invalid arguments with
  value-free messages. Parser issue offsets identify offending keys/members rather than containers.
- Empty quoted roots and object keys reject. Numbers use ASCII decimal digits only and app ids
  must fit `uint`, without wrapping. A name is nonempty after decoding, not trimmed: whitespace
  is preserved, including a nonempty whitespace-only name.
- Install directories retain POSIX spelling, including literal backslashes and colons. Traversal
  above the install root is checked with both slash and backslash separators and with POSIX-only
  separators; the actual POSIX path may not resolve to the root itself. This does not impose
  Windows drive-name rules on legitimate POSIX names. Public `ToApp` and `InstallPath` projections
  enforce the same checks and normalized containment, even for programmatically constructed records.
- Owner-field exclusion is ordinal case-insensitive at every nesting level of model bags and
  scripts. No diagnostic identifies that field or its contents, including duplicate keys inside
  an owner-named root/subtree. Raw KeyValues intentionally preserves owner metadata for round trips;
  default raw-entry formatting emits neither keys nor values. `KeyValues.Write` is serialization,
  **not redaction**, and raw trees/written bytes must never be logged. The writer rejects empty keys
  and bounds object depth to the reader's 64 levels, including programmatically assembled trees.
- The Steam fuzz target exercises model-level syntax/semantic rejects and public path projection;
  non-syntax failures are not converted into expected syntax rejections. Oversized-probe budgets
  must fit a byte array, preventing `MaxInputBytes + 1` overflow before allocation.

Parser entry points live in the existing **Playnite.Steam** assembly, which already owns VDF/ACF
in §3.4 of the architecture. The pure types use only BCL (Core/Platform references remain the
existing assembly graph), not UI/network/process/OS APIs; a new pure assembly adds no isolation
needed by this unit. The additive fuzz target reuses the existing harness's budget policy,
mutator and timeout classification and runs from the unfiltered Core suite in `linux-ci`:
45 seconds or 100,000 cases, whichever is reached first; 2,000 ms/case, 65,536 bytes/input,
256 MiB retained heap, and transient parser allocation ≤ `2 MiB + 128 × maxInputBytes`.

## 4. `appmanifest_<appid>.acf`

### 4.1 Observed root and fields [host]

Root object is `"AppState"`. Fields that matter to a launcher:

| Field (exact case) | Observed shape | Launcher use |
| --- | --- | --- |
| `appid` | decimal string | identity; must match file name |
| `Universe` | `"1"` | ignore/store |
| `name` | UTF-8 display name | display |
| `StateFlags` | decimal bitmask string | installed/broken/updating |
| `installdir` | relative directory name | install path = `<library>/steamapps/common/<installdir>` |
| `lastupdated` | Unix seconds (lowercase key) | freshness |
| `LastPlayed` | Unix seconds; `0` = never | display only; privacy-sensitive |
| `SizeOnDisk` | decimal bytes; exceeds 32-bit | display/quota |
| `StagingSize` | decimal bytes | update state |
| `buildid` | decimal string | installed build |
| `LastOwner` | SteamID64 | **never surface; PII** |
| `DownloadType`, `UpdateResult`, `BytesTo*`, `TargetBuildID` | decimal strings | update progress/state |
| `AutoUpdateBehavior`, `AllowOtherDownloadsWhileRunning`, `ScheduledAutoUpdate` | decimal/timestamp | ignore unless surfacing settings |
| `InstalledDepots` | object of depot id → `{manifest, size[, dlcappid]}` | integrity/depot view |
| `SharedDepots` | object depot id → owning appid | explain shared runtimes |
| `InstallScripts` | object id → escaped Windows-relative path | do not execute |
| `UserConfig` | object, observed `{language: english}` | user config hint |
| `MountedConfig` | object, observed `{language: english}` | last mounted config |

`LastUpdated` (capitalised) is **not** a real field. The earlier draft of this spec named it
that way; the manifest key is `lastupdated`. **[host]**

### 4.2 StateFlags [host]

Two values were observed across 83 manifests: `4` (78 manifests) and `6` (5 manifests).

- `4` means fully installed on this host.
- `6` is `4 | 2`. At least one `StateFlags 6` app (`Proton Experimental`, appid 1493710)
  simultaneously had `BytesToDownload > 0` and `TargetBuildID != buildid`, which is direct
  evidence that the extra bit means an update is pending.
- Therefore installed means `(StateFlags & 4) != 0`, **not** `StateFlags == 4`.
- Other bits are **UNKNOWN** on this host; do not invent names for them. Map unknown bits to
  `Unknown` and keep the raw value for diagnostics.

### 4.3 App model [decision]

```csharp
sealed record SteamApp(
    uint AppId,
    string Name,
    string InstallDir,
    string LibraryPath,
    SteamAppState State,      // Installed, UpdatePending, Broken, Unavailable, Unknown
    bool IsProton,
    ulong? SizeOnDisk,
    DateTimeOffset? LastUpdated,
    uint StateFlags);         // raw value retained
```

Rules:

- `AppId` comes from the file name and the `appid` field; a mismatch skips that app and is
  reported with the filename's authoritative identity (p5-001 contract in §3.2). **[decision]**
- Paths are absolute POSIX paths; `Path.GetFullPath` is the only normalisation applied.
- `StateFlags` installed + missing `installdir` directory → `Broken`, never `Installed`.
- Missing manifest for a library `apps` entry → report `missing-manifest`; do not invent data.
- `SizeOnDisk` is `ulong`/`long`, not `int` (observed values above 66 GB).
- `LastUpdated` parses `lastupdated`; a missing or unparsable value is null, not zero-time.
- `LastOwner` and any play timestamps are never logged in import diagnostics.

### 4.4 Mounting options [host/unknown]

The only mount-related app state observed is `MountedConfig` (`language`), plus per-library
entries in `libraryfolders.vdf`. No per-library mount options, drive ids, or filesystem hints
were present. How Steam decides to re-mount an external library is **UNKNOWN**; the launcher
must only re-read the files and treat a missing path as unavailable.

## 5. Proton markers and runtimes

### 5.1 Prefix directory [host]

For a Proton title, `<library>/steamapps/compatdata/<appid>/` contained:

```
config_info
pfx/                  (drive_c, dosdevices, user.reg, system.reg, userdef.reg, ...)
pfx.lock
tracked_files
version
```

`pfx/` is a Wine/Proton prefix. `tracked_files` and `version` are Steam-runtime artifacts.
Treat all of these as opaque and read-only; never create, repair, or delete a prefix from the
launcher. **[decision]**

### 5.2 `config_info` [host]

Observed as 14 lines (the last line has no trailing newline). Stable shape, not stable
line-numbered meaning:

- line 1: runtime version string (`11.0-100` on this host);
- lines 2–3: absolute paths into the Proton tool's `files/share/fonts/` and `files/lib/`;
- line 4: absolute Steam root;
- lines 5–7: decimal epoch-like values;
- line 8: absolute path to the tool's `files/share/default_pfx/`;
- line 9: decimal float;
- lines 10–14: booleans, a comma-separated DLL override pattern list, and booleans.

Do not depend on exact line positions; parse defensively, and treat the file as a marker plus
best-effort diagnostics. Its full field semantics are **UNKNOWN**. Paths are user-identifying
and must be redacted before logging or test capture.

### 5.3 Detecting Proton [host]

**A `compatdata/<appid>` directory alone is not proof of Proton.** The native title Terraria
(appid 105600) had an empty `compatdata/105600/` directory, while the native title Factorio
(appid 427520) had none.

Use, in order:

1. `<root>/userdata/<accountid>/config/compat.vdf` → `platform_overrides → <appid> →
   {dest: linux, src: windows}` **[host]**. An override with `src = windows` means Steam runs
   the Windows build under a compatibility tool. Absence of an entry was observed for the
   native title.
2. A prefix directory with a `pfx/` and `config_info` under `compatdata/<appid>/`.
3. Process evidence at launch time.

Per-app tool/version selection is **UNKNOWN**: no `CompatToolMapping` or per-app Proton key was
found in `config/config.vdf` or `userdata/<accountid>/config/localconfig.vdf` on this host.
Do not claim a specific Proton version from the files inspected.

### 5.4 Tool and runtime manifests [host]

- Proton and runtimes are ordinary installed apps with `appmanifest_*.acf` and
  `steamapps/common/<installdir>`:
  - 1493710 `Proton Experimental`
  - 2805730 `Proton 9.0`
  - 3658110 `Proton 10.0`
  - 4628710 `Proton 11.0`
  - 1070560 `Steam Linux Runtime 1.0 (scout)`
  - 1391110 `Steam Linux Runtime 2.0 (soldier)`
  - 1628350 `Steam Linux Runtime 3.0 (sniper)`
  - 4183110 `Steam Linux Runtime 4.0`
  - 228980 `Steamworks Common Redistributables`
  - 1826330 `Proton EasyAntiCheat Runtime`, 3086180 `Proton Voice Files`
- These are not games. v1 must not import them as library entries. The exact filter is a
  **DECISION**: a curated runtime appid/installdir set is deterministic and reviewable; a
  name heuristic is fragile. Keep the set next to the spec and require a test for each entry.
- Additional compatibility tools live in `<root>/compatibilitytools.d/<tool>/` with a
  `compatibilitytool.vdf` root `compatibilitytools → compat_tools → <tool-id> → {install_path,
  display_name, from_oslist, to_oslist}`. The capture host had `UMU-Proton-10.0-4`. This file
  uses `//` comments, so the same text parser must support them. **[host]**
- Steam can override the prefix location with environment variables. **[unknown]** — do not
  assume every prefix is under `steamapps/compatdata`.

## 6. Launch paths

### 6.1 Principle [decision]

The launcher asks Steam to start the game; it does not run the game binary itself. Steam owns
native/Proton selection, the runtime container, the prefix, DRM, overlays, and launch options.
This keeps one source of truth for "what Steam will do".

### 6.2 Steam client present, native or Proton [host/decision]

```
steam -applaunch <appid>
```

Use an argument list, never a shell string. A zero exit status means "the command was
accepted", **not** "the game started". Session tracking (§7) decides whether a game ran.

Native and Proton use the same command. Do not select `wine`, `proton`, or the game's `.exe`
path from the manifest: that bypasses Steam and its runtime contract. **[decision]**

### 6.3 URI form [host/decision]

`steam://rungameid/<appid>`

- Open via the desktop opener (`xdg-open`) only as a fallback when the `steam` executable is
  not available. Argument-list spawning still applies.
- The frozen v10 oracle opens Steam URIs through `ProcessStarter.StartUrl`
  (`legacy/source/Playnite/Common/ProcessStarter.cs:70`) and has no Steam library logic;
  this is a process-launch reference only, not Steam behaviour parity. **[v10]**
- Other URI routes (for example a `run` or `launch` path) are **UNKNOWN**; do not use them
  without a probe.

### 6.4 Flatpak-hosted Steam [unknown]

Not present on the capture host. Planned fallbacks for Phase 2 validation:

```
flatpak-spawn --host flatpak run com.valvesoftware.Steam -applaunch <appid>
```

and, for a native install reachable from inside a sandbox:

```
flatpak-spawn --host steam -applaunch <appid>
```

Neither was executed. The p1-010 Flatpak spike concluded **inconclusive** for host launch and
host PID visibility. Do not report Flatpak acceptance until it is actually run.

### 6.5 Launch options [host/unknown]

A user's per-app launch options are Steam-owned settings. No `LaunchOptions` key was present in
the `localconfig.vdf`/`config.vdf` files inspected, because no options were set on this host.
v1 must not edit Steam configuration to inject options. Whether `steam -applaunch` applies the
user's configured options is expected but **UNVERIFIED**; a Phase 2 acceptance case must prove
it before the launcher claims option support.

## 7. Process and exit detection

### 7.1 Observed wrapper chain [host]

The Steam client itself runs inside pressure-vessel on this host (`pv-adverb`,
`steam-runtime-launcher-service`, `srt-logger`). Games launched by Steam must therefore not be assumed to be
simple direct children of the `steam` process; Steam's reaper/runtime wrappers sit between
the client and the game. A tracker that assumes "child of the process we spawned" will miss
games.

### 7.2 Tracker contract carried forward from p1-007 [host]

`spikes/steam-process-tracking/` established the parts that are reusable:

- Process identity is `(pid, start_ticks)`; `start_ticks` is raw `/proc/<pid>/stat` field 22
  clock ticks, valid only within one boot and PID namespace. Never compare across namespaces
  or to wall-clock time.
- Follow the descendant closure independent of row order, retain observed descendants across
  reparenting, and reject PID reuse by start ticks.
- A zombie or dead process is not running.
- An incomplete snapshot is `unknown`, never `stopped`.
- `stopped` means the known tree ended, not authoritative proof that Steam considers the game
  exited.
- Cancellation is distinct from stop and must leave no watcher or handle.

### 7.3 Attribution [unknown/decision]

- The `steam -applaunch` process is a request, not the game. Do not use its exit as the
  session end.
- Steam is expected to set `SteamAppId`/`SteamGameId` in the game environment, but that was
  **not verified** on this host; p1-007 deliberately did not read game environments.
- Until an attribution probe exists, use the observed process tree plus manifest
  `installdir`/executable name as a heuristic, and fall back to manual start/stop.
  Report low-confidence attribution rather than silently claiming a match.
- Proton adds `wine`/`wineserver` processes; a lingering `wineserver` must not keep a session
  alive after the game process is gone, and must not start a session on its own.
- **DECISION**: a session starts only when a matching process is observed, ends after a
  debounce (for example 5 s) of confirmed absence, and can always be stopped manually.
- Flatpak host PID visibility is **UNKNOWN** (p1-010 inconclusive).

## 8. Artwork and metadata caches

### 8.1 Artwork [host]

The real cache is not under `steamapps/`. On this host it is:

```
<root>/appcache/librarycache/<appid>/
    header.jpg
    library_600x900.jpg
    library_hero.jpg
    logo.png
    library_hero_blur.jpg
    <content-addressed hashed files>
```

Across 1374 app directories, `header.jpg` existed in 1150, `library_600x900.jpg` in 667,
`library_hero.jpg` in 659, `logo.png` in 557, and `library_hero_blur.jpg` in 540. Some
directories contained only hashed cache files. Therefore every named file is optional.

There was no `steamapps/librarycache` directory on the host. The earlier draft's path was
wrong; do not implement it without a fallback check on the target host.

Cache order **[decision]**:

1. `appcache/librarycache/<appid>/library_600x900.jpg`
2. `appcache/librarycache/<appid>/header.jpg`
3. Steam CDN header URL (public app header image) — **UNKNOWN/unverified on this host**
4. Placeholder

Cache by app id plus source size/mtime under `${XDG_CACHE_HOME}/playnite/steam/`; never block
the UI thread on network; a failed source falls through to the next.

### 8.2 Achievement/metadata JSON [host]

```
<root>/userdata/<accountid>/config/librarycache/<appid>.json
```

Observed shape: a JSON array whose first element is `["achievements", {...}]`, with
`version`, `data.vecHighlight`, `data.vecUnachieved`, `data.vecAchievedHidden`, `data.nTotal`,
`data.nAchieved`. Populated entries add per-achievement fields such as `strID`, `strName`,
`strDescription`, `strImage`, `bAchieved`, `rtUnlocked`, `flAchieved`, `flMinProgress`,
`flCurrentProgress`, `flMaxProgress`.

This file can contain copyrighted achievement text and remote image URLs. v1 reads it only as
optional metadata; the oracle corpus includes an empty-achievement copy. This is **not**
artwork, despite the `librarycache` name.

## 9. Failure modes

| Failure | Required behaviour |
| --- | --- |
| No Steam root | Empty library with a clear "Steam not found" state; fresh install and manual entries still work. |
| Unreadable/unbalanced VDF | Report path + offset + reason; skip that file/app/library; keep the rest. |
| Missing app manifest | Skip the library `apps` entry; report `missing-manifest`. |
| `appid` field ≠ file name | Report the mismatch; use the file name for identity, do not silently overwrite. |
| Installed flag but `installdir` missing | `Broken`; show why; do not launch. |
| Library path missing/unmounted | Mark the library `Unavailable`; keep its apps visible but not launchable; re-probe after mount; never delete or "repair" the library. Observed in `libraryfolders.vdf` library 1. |
| Stale/update-pending manifest | `StateFlags & 4` with pending update bit (`6` observed): `UpdatePending`, not corrupt. Launch may fail; surface Steam's state. |
| `buildid` ≠ `TargetBuildID` | Same case as above; record both, do not auto-update. |
| Proton override but no `pfx`/`config_info` | Proton state `Unknown`/`Broken`; do not create a prefix yourself. |
| Native title with empty `compatdata/<appid>` | Not Proton; ignore the empty directory (observed: 105600). |
| Non-game manifests (Proton/runtime/redistributables) | Exclude from the game library via the reviewed runtime set; keep them visible in diagnostics. |
| Steam not running / stale `steam.pid` | `steam -applaunch` may start the client (**UNVERIFIED** on this host); if launch does not produce a session, surface the error. Do not treat the pid file as liveness. |
| Launch command exits 0 | Not proof of start; the tracker decides. |
| Game exits immediately | Session ends; report a short-session warning. |
| Flatpak-hosted Steam | **UNKNOWN**; probe `flatpak-spawn --host` forms, report inconclusive until accepted. |
| Multiple accounts | See §9.1. |
| Offline mode | See §9.2. |
| Duplicate appid across libraries | First library in file order wins; report the duplicate. |
| Logging | Never log `LastOwner`, account ids, credentials, ticket blobs, or raw `localconfig.vdf`. |

### 9.1 Multiple accounts [host/unknown]

- `config/loginusers.vdf` has root `users`, keyed by SteamID64, with fields observed as
  `AccountName`, `PersonaName`, `RememberPassword`, `WantsOfflineMode`,
  `SkipOfflineModeWarning`, `AutoLogin`, `timestamp`. All of these are user-identifying and
  must not be logged.
- `userdata/<accountid>/` holds per-account `config/compat.vdf`, `config/localconfig.vdf`,
  `config/librarycache/`, and `config/shortcuts.vdf` when the user has shortcuts.
- The capture host had one account and one `userdata/` directory. Selecting the active account
  when several exist is **UNKNOWN**: `AutoLogin`/`MostRecent` are candidates but were not
  verified with more than one account. Treat account selection as an explicit decision and
  test with synthetic multi-account data before release.
- `shortcuts.vdf` was absent on this host. Its binary VDF format is out of scope for v1; do
  not parse it as text VDF.

### 9.2 Offline mode [host/unknown]

Observed signals:

- `loginusers.vdf`: `WantsOfflineMode` (`0` here).
- `localconfig.vdf`: an `Offline` object containing ticket/signature material. This is
  credential-like; **never read it into logs or fixtures**.
- `registry.vdf`: `AlreadyRetriedOfflineMode` (`0` here).

Offline mode does not change app manifests; the library still parses. Launch may fail if Steam
requires authentication. Reliable file-level detection of "currently offline" is **UNKNOWN**;
prefer observing the launch outcome and surfacing it.

## 10. Fixtures and acceptance

### 10.1 Synthetic corpus (checked in, unchanged)

`tests/fixtures/steam/` contains a synthetic root with two libraries, native and Proton apps,
a missing install, and a broken VDF. It uses `${FIXTURE_ROOT}` so paths are portable.

### 10.2 Real redacted corpus (proposed, not yet checked in)

`docs/port/p1-009-steam-corpus-proposal.md` enumerates the exact proposed paths, hashes, and
redaction record. The corpus is staged outside the checkout until the `oracle-change` route
lands. It covers: modern `libraryfolders.vdf` with an unavailable second library; a native
manifest; a Proton manifest with `dlcappid`, `SharedDepots`, and escaped install-script paths;
a Proton runtime manifest with `StateFlags 6`; a `config_info`; `compat.vdf`; and an
empty-achievement `librarycache` JSON.

### 10.3 Acceptance for v1 [decision]

- Fixture app set matches the expected list exactly, including `StateFlags 6` and the broken
  entries.
- Real install launches and stops a native title and a Proton title.
- Flatpak build launches and tracks a host Steam game (**pending**, not proven).
- No orphan processes after cancel/teardown.
- No network on the UI thread.
- No protected path changes without the declared `oracle-change` route through the
  protected-path gate.

## 11. Unknowns still open

1. Flatpak directory layout, `flatpak-spawn` behaviour, and host PID visibility.
2. Legacy `libraryfolders.vdf` index form (accepted by policy, not observed here).
3. Steam's own duplicate-key and escape semantics remain unobserved; the launcher's parser
   policy is resolved and tested in §3.2, not claimed as host evidence.
4. Meaning of `StateFlags` bits other than `4` and the update-pending bit.
5. Per-app compatibility-tool and Proton-version selection storage.
6. `config_info` field semantics beyond version/paths.
7. Game-process attribution (`SteamAppId`/`SteamGameId`) and wrapper/reaper process names
   under a real launch.
8. Multiple-account selection and offline-mode detection.
9. Whether `steam -applaunch` applies configured launch options.
10. Steam CDN artwork fetch and cache validation.
11. Non-Steam shortcuts (`shortcuts.vdf`) — out of scope for v1.

Any of these may be resolved only by a bounded probe or acceptance run; a plausible guess is
not evidence and must stay labelled unknown.

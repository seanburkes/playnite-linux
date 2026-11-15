# p3-001 — Paths and XDG leaf

## Behaviour contract

`Playnite.Core` owns `IPaths`, `IEnvironment`, `IFileSystem`, pure `Paths` helpers and the
immutable instance `PlaynitePaths` derivations. `Playnite.Platform.Linux` captures the ambient
environment once and supplies `LinuxPaths` and `LinuxFileSystem`. No new project/package or
Core → Platform edge is introduced. Later tests can use the reusable `MemoryEnvironment`
and case-sensitive `MemoryFileSystem` without touching host files or environment variables.

| Directory | Absolute override | Fallback | App suffix |
| --- | --- | --- | --- |
| UserData | XDG_DATA_HOME | HOME/.local/share | playnite |
| Config | XDG_CONFIG_HOME | HOME/.config | playnite |
| Cache | XDG_CACHE_HOME | HOME/.cache | playnite |
| State | XDG_STATE_HOME | HOME/.local/state | playnite |
| Logs | derived from State | State/logs | none |

Unset, empty and relative overrides (including `~`) are ignored; root `/` is a valid absolute
override. All resulting roots are absolute and lexically normalized. HOME must resolve to an
absolute path; `LinuxEnvironment` uses the OS user-profile directory if HOME is absent/relative.
It also captures cwd, program directory and all environment variables (including locale/TZ).
Tests supply explicit HOME/cwd/program values, never the developer's profile.

Documents uses XDG_CONFIG_HOME/user-dirs.dirs (or HOME/.config/user-dirs.dirs). A quoted
absolute path or leading `$HOME` expansion is supported; absent/malformed/relative entries
fall back to HOME/Documents. Shell substitutions are never executed. Escaped shell strings
and arbitrary environment expansion are not supported by this leaf and fall back safely.

Steam **candidates** follow Steam spec §1.2: absolute STEAM_ROOT, HOME/.steam/steam,
HOME/.local/share/Steam, then the Flatpak path. Exact normalized duplicates are removed,
with order retained. These properties do not claim that a Steam installation exists.

v10 `PlaynitePaths.UpdateUserDataDir` suffixes are preserved; config files go to Config,
extension/theme data to UserData, caches/JIT profiles to Cache, action flags to State and
playnite.log to Logs. Construction never creates directories. Filesystem creation is explicit,
idempotent and propagates failures. Normalization is lexical, **not a security containment or
symlink-resolution API**. Rooted later components retain `Path.Combine` replacement semantics.

## Oracle and intentional differences

Read/adapted `legacy/source/Playnite/Common/Paths.cs`,
`legacy/source/Playnite/Settings/PlaynitePaths.cs` and `legacy/source/Tests/Playnite.Tests/PathsTests.cs`.
The additive `tests/golden/paths` corpus records frozen-source hashes and the reproducible
`python3 scripts/port/generate_path_goldens.py` invocation. This is explicitly source-derived
evidence, not an execution of Windows APIs. Existing oracle bytes remain unchanged.

Linux differences are deliberate: ordinal case-sensitive equality; POSIX absolute paths and
invalid characters; legal backslashes preserved; no URI interpretation; `/` survives trimming;
explicit cwd instead of ambient cwd. Null/empty equality remains false as in v10. Safe-name
whitespace collapse and common-directory prefix/boundary behaviour retain the v10 algorithms.

Boundary checklist: paths are strings without lossy encoding/case conversion; null/empty XDG
values mean fallback; Steam candidate ordering is stable; invalid mandatory roots throw
ArgumentException; filesystem errors propagate; no logging or hidden handles; no shell,
registry, UI, network or process invocation. Synchronous directory/metadata operations mirror
the requested filesystem surface and have no async lifecycle or cancellation resource to own.

## Verification and deferred surfaces

- `PathsTests.LegacyDerivedHelpersMatchGolden`: every ported pure helper and POSIX deltas.
- `PathsTests.NormalizationIsIdempotentAndPreservesRootForGeneratedPaths`: generated names,
  Unicode, spaces, backslashes, trailing separators and dot segments.
- `LinuxPathsTests`: all four XDG variables unset/empty/relative/absolute, captured values,
  documents data, Steam candidate order, derived golden properties and no implicit creation.
- `FileSystemTests`: the same idempotence/ancestor-existence contract against memory and an
  isolated temporary directory; fake case sensitivity, collisions and missing-file errors.
- Existing architecture suite runs unchanged; no architecture allowance is widened.

Deferred, with no reachable stubs: Windows final-path handle resolution, long-path prefix
helpers, shell wildcard matching, executable/portable-install detection, variable expansion,
emulation and localisation-status paths. Full Steam root probing, symlink deduplication and
`libraryfolders.vdf` external-library discovery belong to steam-001/steam-002, not this path
candidate leaf. Existing platform-001 and Steam parity rows remain untouched; additive p3-001
rows identify the delivered subset without claiming those broader units are complete.

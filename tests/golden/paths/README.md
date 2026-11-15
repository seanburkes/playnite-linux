# p3-001 source-derived oracle

Regenerate from the repository root with:

```sh
python3 scripts/port/generate_path_goldens.py
```

Each JSON records the frozen v10 source SHA-256 and invocation. These are **source-derived
adaptations**, not claimed Windows execution results. `application.json` extracts constants
and `Path.Combine` suffixes directly from `legacy/source/Playnite/Settings/PlaynitePaths.cs`.
`helpers.json` translates the named cases from `legacy/source/Tests/Playnite.Tests/PathsTests.cs`;
each case records its origin. The generator never imports or executes the port.

Intentional Linux deltas: POSIX absolute roots replace drive/UNC syntax; comparisons are ordinal
and case-sensitive; backslashes are legal filename characters, not separators; invalid path
characters use the POSIX set; normalization retains `/` and uses an explicit working directory,
without URI interpretation. Data/config/cache/state are separated by XDG; logs use state/logs,
and JIT profiles use cache. Original filename spelling is retained for all derived v10 paths.

Win32 final-path handles, long-path prefixes and shell wildcard matching are not ported here.
Steam candidates are specified by `docs/port/08-STEAM-SPEC.md` §1.2, not v10; library discovery,
realpath deduplication and VDF parsing remain the Steam unit's responsibility.

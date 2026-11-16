# p3-008b — archive and backup layer

`Playnite.Core.Archive` and `Playnite.Core.Backup` port the v10 archive helpers and the
backup/restore format. They use the dependency already recorded in
`docs/port/05-DEPENDENCIES.md`: SharpCompress 1.0.0 (read side), with `System.IO.Compression`
for ZIP creation and restore, matching v10's own split. No package or project edge is added
except the SharpCompress allowance now recorded in `docs/port/02-ARCHITECTURE.md`.

## Source map and scope

| Port file | Frozen reference | Adaptation |
| --- | --- | --- |
| `src/Playnite.Core/Archive.cs` | `legacy/source/Playnite/Archive.cs` | SharpCompress read helpers; every member is bounded and name-validated before it is exposed. |
| `src/Playnite.Core/Backup.cs` | `legacy/source/Playnite/Backup.cs` | v10 categories, entry roots, naming, rotation, exclusions and restore semantics, plus staging and bomb limits. |

`Archive.CreateEntryFromDirectory` (a v10 `ZipArchive` extension used by the backup writer) is
folded into `Backup`'s deterministic walk instead of being exposed separately. The UI/process
options (`ClosedWhenDone`, `CancelIfGameRunning`), `GetAutoBackupOptions` (needs the settings
model) and `RestoreLibrarySettingsPath` (rewrites config content for the migration path) stay
with the settings/import/UI units; none is stubbed here. `GetAutoBackupFileName` and
`GetRestoreSelections` are ported because they define naming and category presence.

**No versioned format and no pre-write backup manifest are implemented here.** That work is
deferred to Phase 4, where the SQLite backup manifest already lives. A ZIP with no version or
member field is valid input, exactly as in v10; restore does not require or write either.

## What a backup contains

Entries are sorted ordinally by member name before writing. The roots and member names are
v10's, not a new layout:

| Category | Source under `DataDir` / `LibraryDir` | Entry root | Selected by |
| --- | --- | --- | --- |
| Settings | `config.json`, `fullscreenConfig.json` | archive root | always, as in v10 |
| Library | files directly in `LibraryDir` | `library/<file>` | always, as in v10 |
| LibraryFiles | `LibraryDir/files/**` | `libraryfiles/**` | `LibraryFiles` |
| Extensions | `DataDir/Extensions/**` | `extension/**` | `Extensions` |
| ExtensionsData | `DataDir/ExtensionsData/**` | `extensiondata/**` | `ExtensionsData` |
| Themes | `DataDir/Themes/{Desktop,Fullscreen}/<theme>/**` | `themes/{Desktop,Fullscreen}/<theme>/**` | `Themes` |

Exclusions are v10's:

- `themes/**/Default` is never packed (built-in themes are not user data).
- Files directly under `Themes/Desktop` or `Themes/Fullscreen` are not packed; only a theme
  directory and its contents are.
- Theme mode directories other than `Desktop` and `Fullscreen` are not packed.
- A selected root that does not exist contributes no entries.
- Unknown `BackupDataItem` values are ignored.

There is no extra root member. The archive is the v10 ZIP, byte-for-layout.

## Naming, rotation and replacement

Auto backups are `PlayniteBackup-yyyy-MM-dd-HH-mm-ss.zip` in `OutputDir`, formatted
culture-invariantly from the caller's `DateTimeOffset`. `OutputDir` takes precedence over
`OutputFile`, as in v10. After a successful write, only files matching that exact auto name
pattern are considered; the current backup plus `RotatingBackups` older files are retained.
`RotatingBackups` must be non-negative.

The output is written to a sibling temporary file and moved into place, so a failed or
cancelled backup never leaves a truncated target. An existing output is replaced, and restore
targets are replaced, exactly as v10 does; there is no force/overwrite flag and no new
non-destructive default.

## Restore validation order

Nothing live is written until each of these steps succeeds:

1. Check the backup path and work path for symlink components, open the ZIP, and validate every
   member: normalized portable name, no duplicates, no links, no unsafe external file type, no
   directory/file collision, and entry-count, per-entry and total declared-size limits.
2. Select members by category and `RestoreItems`; unrecognized root members are ignored
   silently, as v10 ignores them.
3. Preflight every selected target root for symlink components. No existence check is made:
   selected roots are replaced, as v10 replaces them.
4. Require an existing `WorkDirectory` that is outside every selected target root (a work
   directory inside a root would be deleted by the cleanup), re-check the created staging
   directory for symlinks, and stage **every selected** member there while enforcing the
   declared-size and remaining-budget cap and verifying ZIP CRC and declared length. The
   staging directory is re-checked before deletion.
5. Only then reproduce v10's cleanup of the selected roots: library root files are deleted,
   `libraryfiles`/`extension`/`extensiondata` roots are replaced, and
   `themes/{Desktop,Fullscreen}` lose every directory except `Default`; finally the selected
   staged files are moved into place. Theme cleanup preflights both mode directories, every
   theme directory and every root file before the first delete.

The move phase is not a transaction: an I/O failure after cleanup can leave a partially
restored category, exactly as v10 can. The staging gate guarantees that corrupt, truncated,
traversal and over-limit archives are rejected before that phase.

### Known limitation

A dangling symlink used as a theme mode directory (`Themes/Desktop` or `Themes/Fullscreen`
pointing at a missing target) is not enumerated by `Directory.Exists` and is therefore not
detected by the cleanup preflight. v10 has no symlink handling at all. This is recorded as a
known limitation rather than fixed in this unit.

## Failure modes

| Condition | Result |
| --- | --- |
| Partial/truncated ZIP or corrupt directory | `InvalidDataException`, no live write |
| Member CRC or declared-size mismatch | `InvalidDataException`, no live write |
| Path traversal (`..`, absolute, drive/colon, dot, empty segment, control character) | `InvalidDataException`, no live write |
| Duplicate member (case-insensitive) or file/directory collision | `InvalidDataException`, no live write |
| Hard/symbolic link member | `InvalidDataException`, no live write |
| Entry count or size limit exceeded | `InvalidDataException`, no live write |
| Missing work directory | `DirectoryNotFoundException` |
| Work directory inside a selected target root | `IOException`, no live write or cleanup |
| Symlink in a source, output, target, backup-file or work path | `IOException` |

## Bomb and traversal limits

`ArchiveLimits` defaults and is applied to both read helpers and restore:

| Limit | Default | Applied to |
| --- | --- | --- |
| `EntryCount` | 100,000 | archive members |
| `EntryBytes` | 256 MiB (268,435,456) | each declared member |
| `TotalBytes` | 4 GiB (4,294,967,296) | the sum of declared sizes |

The declared sum is checked before decompression. Every copy is then capped at
`min(declaredSize, remainingBudget)`, where `remainingBudget` is `TotalBytes` minus the sum of
the declared sizes of entries already extracted in this run (not actual bytes and not
`EntryBytes`). The copy reads at most that cap and fails as soon as a read would exceed it or
the budget is exhausted, so a member that lies about its size cannot reach the `EntryBytes`
ceiling. `Archive.GetEntryStreamAsync` applies the same cap for its single selected member.
The two discriminating tests are:

- `ArchiveTests.Lying_declared_size_fails_at_eight_bytes_under_a_sixteen_byte_total` — declared
  8, actual 64, `TotalBytes` 16; asserts
  `"Archive exceeds decompression limits: maximum 8 bytes."`.
- `ArchiveTests.Lying_declared_size_fails_at_eight_bytes_under_a_large_total` — declared 8,
  actual 64, `TotalBytes` 4096; asserts the same `maximum 8 bytes` message, so the cap is the
  declared size rather than the budget or `EntryBytes`.

Restore stages into a bounded temporary tree under the caller's `WorkDirectory`;
`Directory.Delete(stage, true)` runs in `finally` only after the directory is re-checked.
Backup applies the same count and size budgets to its source files.

Member names are normalized by converting `\` to `/` and are rejected when empty, rooted,
containing `:`, containing a control character, or containing an empty, `.` or `..` segment.
Backup refuses a Linux source file whose name would change meaning under that normalization
(for example a literal backslash) instead of silently restoring it under a different name.

## Evidence

`tests/fixtures/archives-v10/` is additive evidence.
`scripts/port/generate_archive_fixtures.py` places synthetic payloads in the exact v10 entry
roots and member names, records SHA-256 for every fixture, and refuses to overwrite any
existing byte. It is source-derived layout evidence, not a captured Windows archive. There is
no version or manifest fixture because that format is deferred.

- `tests/golden/archives/v10-full.entries.json` — deterministic listing of the v10-shaped
  archive.
- `tests/golden/archives/v10-source-backup.entries.json` — expected backup listing from the
  v10-shaped source tree, including the Default/theme-root/non-mode exclusions.
- `tests/golden/archives/v10-restore-tree.json` — expected data/library routing on restore.
- `lying-size.zip` — patched declared size for the bomb-cap tests above.

`ArchiveTests` covers listing, normalization, entry reads, tar/zip parity, traversal,
duplicates, links, limits and the two lying-size cases. `BackupTests` covers the full and
minimal round trips, the v10 fixture restore, the source-layout golden, exclusions,
naming/rotation, output and target replacement, no-version/no-manifest input, all validation
failures, cancellation, symlinked backup/work paths, work directories inside selected targets,
later-mode symlink preflight and bomb limits. Zero skips.

Local narrow checks at the implementation head: Core **1412 passed / 0 failed / 0 skipped**
(previous union floor 1365), Architecture **144 passed / 0 failed / 0 skipped**, Core Release
build **0 warnings / 0 errors**. After normally merging `origin/main` (the p3-007b localisation
union), the real linux-ci merged head reports **1429/1429** Core, **144/144** Architecture and
**4/4** Smoke, zero skips; the counted floor is raised to 1429 only from that run.

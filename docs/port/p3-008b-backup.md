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
| `src/Playnite.Core/Backup.cs` | `legacy/source/Playnite/Backup.cs` | v10 categories, entry roots, naming, rotation, exclusions and restore semantics, plus a versioned integrity manifest and a staging gate. |

`Archive.CreateEntryFromDirectory` (a v10 `ZipArchive` extension used by the backup writer) is
folded into `Backup`'s deterministic walk instead of being exposed separately. The UI/process
options (`ClosedWhenDone`, `CancelIfGameRunning`), `GetAutoBackupOptions` (needs the settings
model) and `RestoreLibrarySettingsPath` (rewrites config content for the migration path) stay
with the settings/import/UI units; none is stubbed here. `GetAutoBackupFileName` and
`GetRestoreSelections` are ported because they define naming and category presence.

## What a backup contains

Entries are sorted ordinally by member name before writing, then the manifest is appended.
The roots and member names are v10's, not a new layout:

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

The v11-only `backup-manifest.json` is a root member with `formatVersion: 1` and one
`{name, size, sha256}` record per other member. It is additive: v10's restore ignores root
members it does not recognize, so a v11 backup remains v10-readable. A v10 backup without a
manifest is still restorable and is reported as `LegacyFormat` with warnings.

## Naming and rotation

Auto backups are `PlayniteBackup-yyyy-MM-dd-HH-mm-ss.zip` in `OutputDir`, formatted
culture-invariantly from the caller's `DateTimeOffset`. `OutputDir` takes precedence over
`OutputFile`, as in v10. After a successful write, only files matching that exact auto name
pattern are considered; the current backup plus `RotatingBackups` older files are retained.
`RotatingBackups` must be non-negative.

The output is written to a sibling temporary file and moved into place, so a failed or
cancelled backup never leaves a truncated target. An existing output requires
`BackupOptions.Overwrite`; v10 deleted it unconditionally, and this is the deliberate v11
safety delta.

## Restore validation order

Nothing live is written until each of these steps succeeds:

1. Open the ZIP and validate every member: normalized portable name, no duplicates, no
   links, no unsafe external file type, no directory/file collision, and entry-count,
   per-entry and total declared-size limits.
2. If `backup-manifest.json` exists, reject an unknown format version, a member-count or
   name mismatch, an invalid size or hash shape, and a manifest that omits or invents a
   member. The member set is checked through one name-to-declared-size index built from the
   archive, so validation stays linear in the member count. Without a manifest, record the legacy warning and report missing optional v10
   members.
3. Select members by category and `RestoreItems`; report unrecognized root members and do
   not restore them.
4. Preflight every selected target root and file. A non-settings target that already exists
   requires `RestoreOptions.Overwrite`; no settings target is changed at this point either.
5. Require an existing `WorkDirectory` that is outside every selected target root (a work
   directory inside a root would be deleted by the overwrite cleanup), re-check the created
   staging directory for symlinks, stage **every** member there while enforcing the limits
   again against actual bytes, verify ZIP CRC, declared length and manifest SHA-256, then
   cancel-check once more and re-check the staging directory before deleting it.
6. Only then, with `Overwrite`, reproduce v10's cleanup of the selected roots: library root
   files are deleted, `libraryfiles`/`extension`/`extensiondata` roots are replaced, and
   `themes/{Desktop,Fullscreen}` lose every directory except `Default`; finally the selected
   staged files are moved into place. Theme cleanup preflights both mode directories, every
   theme directory and every root file before the first delete, so a symlink in a later mode
   cannot surface after an earlier theme is already gone.

The move phase is not a transaction: an I/O failure after cleanup can leave a partially
restored category, exactly as v10 can. The staging gate guarantees that corrupt, truncated,
wrong-version, missing-member and over-limit archives are rejected before that phase.

## Failure modes

| Condition | Result |
| --- | --- |
| Partial/truncated ZIP or corrupt directory | `InvalidDataException`, no live write |
| Manifest `formatVersion` other than 1 | `InvalidDataException`, no live write |
| Manifest missing/extra/duplicate member or bad hash shape | `InvalidDataException`, no live write |
| Member CRC, declared size or SHA-256 mismatch | `InvalidDataException`, no live write |
| Path traversal (`..`, absolute, drive/colon, dot, empty segment, control character) | `InvalidDataException`, no live write |
| Duplicate member (case-insensitive) or file/directory collision | `InvalidDataException`, no live write |
| Hard/symbolic link member | `InvalidDataException`, no live write |
| Entry count or size limit exceeded | `InvalidDataException`, no live write |
| Existing output or restore target without overwrite | `IOException`, no live write |
| Missing work directory | `DirectoryNotFoundException` |
| Work directory inside a selected target root | `IOException`, no live write or cleanup |
| Symlink in a source, output, target, backup-file or work path | `IOException` |

## Bomb and traversal limits

`ArchiveLimits` defaults and is applied to both read helpers and restore:

| Limit | Default | Applied to |
| --- | --- | --- |
| `EntryCount` | 100,000 | archive members (manifest included) |
| `EntryBytes` | 256 MiB (268,435,456) | each declared and each actually decompressed member |
| `TotalBytes` | 4 GiB (4,294,967,296) | the sum of declared sizes and the sum actually written |
| Manifest read | 64 MiB (`ManifestLimitBytes`), `EntryBytes` and the remaining total budget, whichever is smallest | manifest decompression |

The declared sum is checked before decompression, and every copy is capped at
`min(declared size, remaining total budget, EntryBytes)`: the loop fails as soon as a read
would exceed the declared size or the budget, so a member that lies about its size cannot
consume the `EntryBytes` ceiling. `GetEntryStreamAsync` applies the same cap after the other
members' declared sizes. The manifest's declared size is checked against the manifest cap
before any copy starts, so a manifest that lies cannot be read first and rejected afterwards. Restore stages into a bounded temporary tree under the caller's `WorkDirectory`;
`Directory.Delete(stage, true)` runs in `finally` only after the directory is re-checked.
Backup applies the same count and size budgets to its source files and manifest.

Member names are normalized by converting `\` to `/` and are rejected when empty, rooted,
containing `:`, containing a control character, or containing an empty, `.` or `..` segment.
Backup refuses a Linux source file whose name would change meaning under that normalization
(for example a literal backslash) instead of silently restoring it under a different name.

## Evidence

`tests/fixtures/archives-v10/` is additive evidence. `scripts/port/generate_archive_fixtures.py`
places synthetic payloads in the exact v10 entry roots and member names, records SHA-256 for
every fixture, and refuses to overwrite any existing byte. It is source-derived layout
evidence, not a captured Windows archive. `tests/golden/archives/v10-full.entries.json` is the
deterministic listing of the full v10-shaped fixture. The v11 manifest variants are synthetic
failure inputs for the version, member and hash checks; `lying-size.zip` and
`huge-manifest.zip` patch a declared size so the actual-byte budget and the pre-copy manifest
cap are exercised without shipping a real bomb. No pre-existing fixture or golden byte
was changed.

`ArchiveTests` covers listing, normalization, entry reads, tar/zip parity, traversal,
duplicates, links, limits and a member that lies about its declared size. `BackupTests` covers
the full and minimal round trips, the v10 fixture restore, exclusions, naming/rotation,
overwrite protection, manifest determinism, large-manifest validation through the single entry
index, the manifest size cap, all validation failures, cancellation, symlinked backup/work
paths, work directories inside selected targets, later-mode symlink preflight and bomb limits.

Local narrow checks at the implementation head: Core **1418 passed / 0 failed / 0 skipped**
(previous union floor 1365), Architecture **144 passed / 0 failed / 0 skipped**, Core Release
build **0 warnings / 0 errors**. The counted floor is raised only from a real linux-ci run on
the final head.

# p4-004 — settings migration

## Boundary and oracle

`Playnite.Settings.SettingsMigration` is a Core-only, settings-file operation. It does not
open LiteDB or SQLite, import games, initialize repositories, or reference the importer.
No SQLite schema/repository unit files are changed. Steam row migration belongs to the
separate database/import units; this unit makes no claim to have imported Steam rows.

References: frozen `legacy/source/Playnite/Settings/PlayniteSettings.cs` (public properties,
`LoadSettings`/`SaveSettings`), `docs/port/02-ARCHITECTURE.md` §3.5, and protected
`tests/golden/settings/{config,fullscreenConfig,windowPositions}.json`. All three goldens
round-trip byte-for-byte. A disposable variant of the frozen config exercises Windows
database/backup paths; protected fixtures/goldens are not edited. A separate test copies
the entire frozen v10 fixture library and verifies every source file remains byte-identical.

## Schema 1

One UTF-8 JSON destination contains:

- `schemaVersion`: migration envelope version, independent of v10 `Version`.
- `settings`: readable v10-compatible property names and values, with explicit path rewrites.
  No defaults, enum coercions, null elision, sorting, script evaluation or ambient path guesses.
- `legacy`: unknown top-level properties and entire owning properties containing unmapped
  Windows path candidates. Those properties are absent from `settings`, not discarded.
  Nested v10 object/collection graphs are opaque compatibility values; their members are
  preserved without guessing a new nested typed schema. Unknown-property reporting refers
  to the root PlayniteSettings schema, whose explicit property set is copied from v10.
- `settingsUtf8`: base64 of the lossless migrated v10 JSON bytes. This compatibility
  representation retains BOM, whitespace, property/collection order, numeric spelling,
  escaping, missing-vs-null and trailing newline exactly. Only mapped path string spans
  change. It also retains unmapped and unknown values in their original positions.
- `report`: `rewrittenPaths` (property, source, destination), `unmappedPaths` (property,
  source) and `preservedUnknownProperties` (names), in source order. Nested/array path
  locations use Newtonsoft's unambiguous token paths, e.g. `Future.Paths[0]`.

The compatibility representation is not an alternative authoritative settings store:
`ExportV10` verifies that the union of `settings` and `legacy` equals it, and rejects
divergence or overlap. It exports an unchanged migration artifact, not a general-purpose
settings editor. Native settings services and defaulting remain a later unit. The full
v10 graph is retained even for unsupported fields, rather than pretending those features
are active on Linux. The main config property classification is not an implementation of
fullscreen/window-position services; their goldens additionally exercise lossless storage.

Newtonsoft validates v10 objects with duplicate keys rejected and date parsing disabled.
The byte-span reader accepts standard JSON; malformed, duplicate-key, trailing-content,
commented or otherwise unsupported syntax fails before any destination write. New envelope
keys are camelCase. This is a data migration, not a credential extractor or script runner.

## Explicit path rules

The caller supplies absolute Windows-root → absolute normalized Linux-root pairs. There
are no automatic drive, home, XDG, Wine or installation guesses. Roots match Windows
case-insensitively, on separator boundaries, longest root first. Mapping order is irrelevant;
duplicate case-insensitive roots and relative/traversing mappings are rejected. Suffix case,
spaces and Unicode survive; separators become `/` only for rewritten values.

Only v10's typed root `DatabasePath` and `AutoBackupDir` are eligible for rewriting. Rooted
drive/UNC strings elsewhere (including unknown properties and collections), drive-relative
strings, root-relative strings, and backslash-relative values in typed path fields are
reported and retained, never guessed. Paths containing `.` or `..` segments are unmapped.
Unknown extension/install-directory structures are retained/reported instead of being
interpreted as supported path settings. Reports include the actual source path strings;
callers must treat these reports as user data, not public diagnostic logs.

## Writes, restart, cancellation and dry-run

`MigrateAsync` takes source/destination, mappings, dry-run, cancellation, and an optional
checkpoint observer. Destination must be outside the source file's directory. Symlinks in
source/destination/staging paths are rejected. The destination parent must already exist.
No source file is ever opened for writing; source bytes are checked again before commit.

Dry-run performs parsing, projection and reporting without creating a directory or file.
An existing identical destination returns `AlreadyMigrated`; a differing destination is
refused, including a different mapping/report. It is never overwritten.

The sibling `<destination>.settings-migration.tmp` is exclusively owned during writing.
An abandoned stage is unlinked (not truncated, avoiding hard-link writes) under an exclusive
handle before a fresh stage is created. An active writer prevents restart from acquiring
that handle. Bytes are written, flushed to disk, read back, byte-compared and schema/export
validated. Source bytes and cancellation are rechecked, then a no-overwrite same-directory
rename publishes the settings and report together. No separate report commit can tear.
Managed failure/cancellation removes the owned stage. Abrupt process death leaves no
destination, resumable staging, or a complete committed destination. This is a process-crash
contract, not a claim about filesystem directory durability across machine power loss.

`SettingsMigrationTests` tests every boundary (`BeforeWrite`, `Written`, `Flushed`,
`Verified`, `Committed`) both with injected exceptions and with a separate process killed
without running managed cleanup. Restart is verified in every case. Cancellation before
commit, source mutation, stale staging, symlink refusal and destination conflicts are tested.

## Evidence

Local Release checks: Core **1583 passed**, Architecture **144 passed**, zero failed/skipped.
The Core count includes 50 migration cases. Actual linux-ci counts and the additive baseline
capture must land before this change is marked ready; local counts do not raise the floor.
Independent reviews are firstmate-owned. Protected parity/aggregate files remain untouched.

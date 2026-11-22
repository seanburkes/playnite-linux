# p3-008 — archive and backup contract

Oracle: frozen `legacy/source/Playnite/Archive.cs`, `Backup.cs`, and
`legacy/source/Tests/Playnite.Tests/BackupTests.cs`. Surface `core-016`;
this unit is not `import-002`'s database migration.

## Layout and preserved behaviour

ZIP payloads are opaque bytes: this layer never parses or migrates databases/settings.
Root `config.json` and `fullscreenConfig.json`, when present, and every immediate library
file under `library/` are always included, even with no backup selections (v10 behaviour).
Selections additionally include recursive `libraryfiles/`, `extension/`, `extensiondata/`
and `themes/{Desktop,Fullscreen}/{theme}/`. These map back to library `files/`, data
`Extensions/`, `ExtensionsData/`, and `Themes/` respectively. Default theme directories,
loose files in theme mode directories, unrelated data/cache/logs and empty directories
are excluded. Custom themes only; default installed themes survive restore.

Names use `/` on Linux; import accepts both v10 Windows `\` and `/`.
Create sorts payload names ordinally, then appends `backup-manifest.json`.
Every ZIP member gets the caller's injected timestamp (ZIP stores wall time, no offset,
two-second resolution, years 1980–2107). Auto naming remains
`PlayniteBackup-yyyy-MM-dd-HH-mm-ss.zip`; rotation keeps current plus the requested older
automatically named backups. Settings/library always appear in restore selections.
Entry lookup is exact/case-sensitive as in v10; returned streams are bounded owned
copies, independent of archive lifetime. Listing preserves archive order.

## Authorized safety deviations

Captain ruling, relayed by firstmate in p3-008 inbox 001/002:

* New archives add version 1 camelCase JSON with `formatVersion` and `members`.
  Each member records `name`, `size` and uppercase `sha256`. Manifest size is capped at
  1 MiB (also subject to entry/total limits). It is not its own member.
* With a manifest, wrong version, invalid JSON, missing/extra/duplicate members, wrong
  size/hash, and corrupt/truncated ZIP fail. All members, including unselected/unknown
  members, are streamed to staging and checked against declared length and CRC32,
  and against SHA-256 when available, before any live target changes.
* Without a manifest, import is **permissive v10 compatibility**, not an integrity-
  guaranteed new backup: missing settings/library are tolerated and reported.
  Unknown members are validated but ignored with a report. Removing a manifest therefore
  makes an archive indistinguishable from a legacy backup; no authenticity is claimed.
* Create refuses an existing output; restore refuses existing selected category roots
  or settings files unless `Overwrite` is explicitly true. This opts into v10 category
  replacement: library top-level files only, full optional category trees, non-default
  themes, and selected settings files. Missing categories never trigger deletion.
* Invalid paths/links and case-insensitive duplicate names are rejected, not silently
  translated to Linux ambiguities. Symlinks in sources, destination ancestors and trees
  to be cleaned are refused. No new dependency: the recorded SharpCompress 1.0.0 handles
  general archives; in-box ZIP handles backup creation/restoration.

Defaults: **100,000 entries**, **256 MiB per entry**, **4 GiB total uncompressed**.
Limits include the manifest and directory entries. Checked subtraction bounds both
declared sizes and actual streamed bytes; customized positive limits can be smaller.
Reject absolute/UNC/drive/colon paths, empty/dot/dot-dot segments, controls,
links/special ZIP entries, duplicates after separator/case normalization, and
file/directory collisions. No extracted path can escape staging.

Caller supplies explicit data/library/output/work paths. Work directory must exist and
must be outside selected category roots. Caller must quiesce library writers and provide
exclusive/trusted destination directories: portable managed filesystem checks do not
defend against concurrent hostile rename/symlink races. Validation errors and cancellation
before promotion leave targets unchanged and remove staging/temp output. Create publishes
with a same-directory move; overwrite restore's final category replacement is **not a
multi-directory transaction**: I/O failures after promotion starts can leave partial live
replacement. Cancellation is observed during enumeration/streaming and before promotion,
not between destructive promotion steps. Restore staging requires bounded free disk
space; returned archive-entry copies require bounded memory. UI process lifecycle and
settings `DatabasePath` rewriting are deferred to their owning units.

## Evidence

`scripts/port/generate_archive_fixture.py` independently derives a Windows-separator ZIP
layout from frozen v10 source, without calling the port. Provenance records source,
payload and ZIP SHA-256 values and the generator command. It includes an unchanged real
frozen SQLite emulator database as opaque database-shaped bytes, **not** a Playnite
LiteDB library export. Other payload shapes are synthetic; this is source-derived
evidence, not a claim of having executed the Windows oracle.

Additive `tests/fixtures/archive-v10/` and `tests/golden/archive-listing.txt` only.
`ArchiveBackupTests` checks their hashes, imports and byte-round-trips the corpus,
the deterministic listing/timestamps/archive bytes, exclusions, rotations, selective
replacement, permissive warnings, manifest/version/integrity failures, truncation,
unselected corruption, traversal/collisions/links, declared and actual byte bounds,
compressed bombs, output preservation, cancellation, disposal, and seeded property/fuzz
corpora. Existing fixture/golden bytes remain unchanged. Protected additions and parity
row are submitted under `oracle-change`.

Independent adversarial reviews remain firstmate-owned: worker contract prohibits
delegation. Worker diff checks cover data-loss/preflight and archive-boundary attack paths;
they are not represented as independent reviews.

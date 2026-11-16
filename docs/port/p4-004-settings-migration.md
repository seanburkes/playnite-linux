# p4-004 — settings migration

## Boundary and oracle

`Playnite.Settings.SettingsMigration` is a Core-only, settings-file operation. It does not
open LiteDB or SQLite, import games, initialize repositories, or reference the importer.
No SQLite schema/repository unit files are changed. Steam row migration belongs to the
separate database/import units; this unit makes no claim to have imported Steam rows.

References: frozen `legacy/source/Playnite/Settings/PlayniteSettings.cs` (public properties,
`LoadSettings`/`SaveSettings`), `docs/port/02-ARCHITECTURE.md` §3.5, and protected
`tests/golden/settings/{config,fullscreenConfig,windowPositions}.json`. The compatibility
export of all three goldens is byte-identical to its input. The destination envelope's
`settings` object is a normalized projection, not a byte copy: known properties are projected
and unknown ones move to `legacy`, so `settings` ∪ `legacy` is semantically equal to the v10
object, and only mapped path strings change. A disposable variant of the frozen config exercises
Windows database/backup paths; protected fixtures/goldens are not edited. A separate test copies
the entire frozen v10 fixture library and verifies every source file remains byte-identical.

## Schema 1

One UTF-8 JSON destination contains:

- `schemaVersion`: migration envelope version, independent of v10 `Version`.
- `settings`: readable v10-compatible property names and values, with explicit path rewrites.
  This is a normalized projection: it equals the v10 object only up to the `legacy` split and
  mapped path rewrites, and is never claimed to be byte-identical to the source. No defaults,
  enum coercions, null elision, sorting, script evaluation or ambient path guesses.
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
duplicate case-insensitive roots and relative/traversing mappings are rejected. A Linux root
that starts with `//` is rejected rather than silently treated as `/`, because POSIX leaves a
leading double slash implementation-defined. Suffix case, spaces and Unicode survive;
separators become `/` only for rewritten values.

Only v10's typed root `DatabasePath` and `AutoBackupDir` are eligible for rewriting. Both
backslash-rooted UNC and forward-slash UNC forms are detected; the forward-slash form is mapped
when a configured root matches and otherwise reported in `unmappedPaths`. Rooted drive/UNC
strings elsewhere (including unknown properties and collections), drive-relative strings,
root-relative strings, forward-slash-relative typed paths (for example `games/x`, `./x`,
`../x`), backslash-relative values in typed path fields, and typed path
values containing characters that cannot occur in a Windows file name (`< > : " | ? *` or
control characters) are reported and retained, never guessed. Paths containing `.` or `..`
segments are unmapped. A typed path value that is already an absolute Linux path (`/...`) is
retained as-is. Unknown extension/install-directory structures are retained/reported instead of
being interpreted as supported path settings. Reports include the actual source path strings;
callers must treat these reports as user data, not public diagnostic logs.

Deliberate non-normalisation boundary: setting values are never repaired beyond mapped
separator rewriting. The migration does not resolve `.`/`..`, normalise Unicode, case-fold
suffixes, expand symlinks, or reinterpret a double leading slash; such values are reported
instead. Character restrictions are checked only against Windows file-name rules, because a
Windows name containing `< > : " | ? *` or a control character cannot be a real v10 path
component; Linux-only restrictions (NUL excepted) are not invented.

## Writes, restartability, cancellation and dry-run

`MigrateAsync` takes source/destination, mappings, dry-run, cancellation, and an optional
checkpoint observer. Destination must be outside the source file's directory. A source file
directly under the filesystem root is rejected explicitly: its directory is `/`, so every
absolute destination would otherwise be judged "inside" it and the containment rule would
refuse all destinations for the wrong reason. Symlinks in source/destination/staging paths are
rejected. The destination parent must already exist. No source file is ever opened for writing;
source bytes are checked again before commit.

Dry-run performs parsing, projection and reporting without creating a directory or file.
An existing identical destination returns `AlreadyMigrated`; a differing destination is
refused, including a different mapping/report. It is never overwritten.

The sibling `<destination>.settings-migration.tmp` is exclusively owned during writing.
Behaviour is restart-and-atomically-publish, not resumption: an abandoned stage is unlinked (not
truncated, avoiding hard-link writes) under an exclusive handle and recreated empty, so partial
staging is always discarded rather than adopted. An active writer prevents a restart from
acquiring that handle. Bytes are written, flushed to disk, read back, byte-compared and
schema/export validated. Source bytes and cancellation are rechecked, then a no-overwrite
same-directory rename publishes the settings and report together. No separate report commit can
tear. Managed failure/cancellation removes the owned stage. Abrupt process death leaves no
destination, or a complete committed destination plus at most a discarded partial stage that the
next attempt deletes. This is a process-crash contract, not a claim about filesystem directory
durability across machine power loss.

`SettingsMigrationTests` tests every boundary (`BeforeWrite`, `Written`, `Flushed`,
`Verified`, `Committed`) both with injected exceptions and with a separate process killed
without running managed cleanup; the child records the boundary immediately before killing
itself, so the test proves it reached that boundary rather than dying during startup. Restart is
verified for process death and after cancellation before commit. Source mutation, stale staging,
symlink refusal and destination conflicts are tested, including forward-slash UNC mapping and
unmapped reporting, forward-slash-relative typed paths, invalid Windows suffix characters,
`//` Linux-root rejection and a source directly under `/`.

## Evidence

Local Release checks on the merged tree: Core **1642 passed**, Architecture **144 passed**,
zero failed/skipped; Core build **0 warnings / 0 errors**. The Core count includes 62 migration
cases and the 1630-case floor from main.

Actual [linux-ci run 43815358729](https://github.com/seanburkes/playnite-linux/actions/runs/43815358729)
on head `d86368bfb58ba276eab2565d36017c4bcee6e8f3` of
https://github.com/seanburkes/playnite-linux/pull/91 passed warning-as-error build, bounded
serialization fuzzing and native smoke. Downloaded JUnit testcase elements, suite totals,
test-counts.json and exit files independently confirm **Core 1642, Architecture 144, Smoke 4**,
all passed, zero failed/skipped, every MTP exit 0. The run tested the head after normally merging
main's 1630 floor; this follow-up adds 12 migration cases, raising only Core's floor from 1630 to
1642. `p4_004_fix_capture` records that run. The floor commit changes only baseline and evidence,
never production or test cases, so its own linux-ci run on the final head is the evidence-only
delta revalidating the same 1642 counted cases. The additive baseline exception is authorized for
this review follow-up under `oracle-change` (as was `p4_004_capture` by firstmate steering
001-002). Independent reviews are firstmate-owned. Protected parity/aggregate files remain
untouched.

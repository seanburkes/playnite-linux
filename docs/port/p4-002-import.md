# p4-002 — isolated v10 reader and verified SQLite destination import

## Ownership and use

Firstmate steering 001 preserves the enforced architecture: `Playnite.LegacyImport`
is a reader-only executable (LiteDB 4.1.4 + BCL/contracts, never Core/SQLite).
`Playnite.Cli` launches it with argument lists; `Core/Import/LibraryMigration.cs`
consumes the JSON and owns SQLite through the existing repositories. Neither Core
nor Desktop references reader types or LiteDB. No schema/repository files changed.

```sh
dotnet src/Playnite.Cli/bin/Release/net11.0/Playnite.Cli.dll import-v10 \
  src/Playnite.LegacyImport/bin/Release/net11.0/Playnite.LegacyImport.dll \
  /absolute/v10/library /absolute/destination/library.sqlite [--dry-run]
```

The public Core API requires the source directory as well as the destination.
It checks canonical containment for the live path **and every generated artifact**,
including a source named exactly like the staging directory, before any write.
The destination parent must exist and be outside the source. Symbolic links in the
source, source/destination ancestors, and staging artifacts are refused by Core,
not just by the CLI; the directly invoked reader checks source ancestors too. Exit 0
prints a JSON report; exit 1 prints an explicit diagnostic on stderr. Dry-run reads
and transforms, validates references and reports its plan but creates no files,
locks, SQLite database or directories. Repeated dry-runs are identical no-ops.
Any existing live destination, including a successful previous import, is refused;
it is never overwritten. This unit publishes a fresh destination atomically; it
does not replace an already-open user library (the broader p4-003 rollback contract).

## Intermediate v1 (stdout, one JSON object)

`format = "playnite-v10-library"`, `version = 1`. `files` contains every source
file's relative `path`, `size` and lowercase SHA-256. `records` contains every raw
document with its `file`, `collection`, and normalized `data`. Original BSON `_id`
remains `_id`. GUIDs are lowercase D strings, dates are UTC ISO-8601, nulls/arrays/
numbers/booleans/text preserve their JSON domains; binary and ObjectId use explicit
`bsonType` plus `base64`/`hex` objects. Other BSON kinds fail, never disappear.
Only root `.db` files are parsed; assets are hashed, not interpreted as databases.
The v10 `database.json` Version 4 marker is required. Unknown envelope versions,
duplicate JSON keys, trailing content, duplicate identities, malformed Steam rows,
and missing metadata references fail rather than silently altering the input.

The reader copies and hashes source bytes into memory before parsing with raw
LiteEngine/BsonDocument APIs. It never opens a source read-write, repairs a source,
maps a typed LiteDB collection, or uses BsonMapper. A second complete source-byte
comparison refuses a source that changed during the read. The capture is not a
filesystem-wide atomic snapshot; users must quiesce v10 while exporting. Memory use
scales with the library (including assets); streaming/bounded large-library capture
is not claimed by this unit.

## Decisions, transformation and verification

Oracle references: frozen `ItemCollection.cs` (one database per named collection),
`GamesCollection.cs` (ReleaseDate string mapping and four transient flags reset),
the SDK model files and `GameDatabase.GetCollectionMapper`. Typed destination
binding uses explicit constructors, existing model validation, and
`TypeNameHandling.None`. `_type` and `$type` never select CLR types. `_id` becomes
the same model GUID, ReleaseDate retains partial precision, dates use the source's
UTC BSON instant rather than the host timezone. Windows paths are stored unchanged,
not guessed or executed. Numeric ranges are checked by the existing model guard
and SQLite repositories; ordering/null collections follow their existing contracts.

Steam is identified by the frozen plugin GUID
`cb91dfc9-b977-43bf-8e70-55f46e410fab`. All other providers are reported as skipped.
Eleven known named metadata collections are imported. Unsupported file/collection
records are reported by file, collection and identity. Every source document has
one decision, and the complete intermediate is retained as `source.json`, including
skipped and unknown fields. Complete normalized documents accompany imported values
in `LegacyJson`, so ignored/computed/unknown fields remain available for audit.
Malformed imported records fail with their identity, rather than reducing counts.

The existing Windows-produced `tests/fixtures/v10` oracle has **4 games: 3 Steam,
1 custom**, plus **5 metadata records**. Expected/imported/reported: **3/3/1 game**;
all **9 source documents** accounted for, **8 imported / 1 skipped**. Provenance is
recorded in `docs/port/units/p1-003.ledger.tsv` (Windows run 43650013648, artifact
14135728206), `tools/V10LibraryFixture/Program.cs` and the fixture's SHA256SUMS;
original bytes are unchanged. Tests assert the manifest file set and every hash.
The additive `v10-malicious` derivative records its generator and provenance and
does not pretend Linux mutation is independent Windows oracle evidence.

## Publication and recovery

An exclusive retained `<destination>.import.lock` serializes writers. Staging is
`<destination>.import/`; a matching destination/source fingerprint in `manifest.json`
is required before any restart cleanup. Unknown staging ownership or symlinks fail
closed. Only the owned SQLite file and its two sidecars are removed on restart;
the complete intermediate and manifest are recorded before the first SQLite write.
Metadata and games are written in one transaction to the temporary database.
Verification checks complete game values/legacy bags, metadata values/legacy bags,
counts, SQLite integrity and foreign keys. WAL is checkpointed and all connections
are closed before the main file is flushed and renamed without overwrite on the
same filesystem. The report is written only after verification and before rename.
A crash can leave a verified report with no live file: it is an audit/plan artifact,
not a commit marker; live-path existence determines publication. No post-publication
work is needed to make the database usable. Source and backup copies are untouched.
This tests SIGKILL recovery, not power-loss durability of directory metadata.
Managed failures also retain owned staging for the same explicit restart path;
they do not claim automatic cleanup. `SourceHash` is the canonical intermediate
envelope fingerprint (including its manifest and records), not a substitute for
the manifest's individual source-file hashes.

| SIGKILL checkpoint | Live path at kill | Restart |
| --- | --- | --- |
| Read | absent | clean rerun |
| Transformed | absent | clean rerun |
| Copied | absent | restart owned staging |
| PartialWrite | absent | discard owned uncommitted temp and rerun |
| Written | absent | rebuild owned temp |
| Verified | absent | rebuild and reverify |
| BeforeSwap | absent | rebuild and reverify |
| Committed | complete verified DB | clear existing-destination refusal |

Tests cite real `docs/formal/LibraryImport.tla` invariants: SourceUnchanged,
NoCommitWithoutVerify, ReaderNeverPartial, AtomicLive, BackupBeforeWrite. They use
child processes and SIGKILL (not exception-only simulations), check the live path
while paused, resume, verify 3 games and source hashes, and test cancellation up to
publication. This does not claim the model's liveness property is proved by tests.

## Explicit security exception (owner ruling, steering 003)

**LiteDB 4 is not safe.** Its frozen 4.1.4 file-format dependency is affected by
critical CVE-2022-23535 / [GHSA-3x49-g6rc-c284](https://github.com/advisories/GHSA-3x49-g6rc-c284).
The only `NuGetAuditSuppress` is that exact URL in the importer project, visibly
commented for removal when v10 import retires. All other advisories,
`NuGetAuditMode=all` and warnings-as-errors remain enabled. There is no global
suppression or exception in Core/Desktop/CLI/tests. Build-only reader references
keep test runners from consuming its runtime dependency graph.

The mitigation is raw LiteEngine BSON traversal without any BsonMapper or typed
binding in the reader. This executable is a dependency boundary, **not a sandbox**:
it still runs with user privileges. Other parser bugs, malformed inputs, memory
exhaustion, and future accidental typed mapping remain residual risks. Regression
coverage preserves `_type`/`$type` at every nesting level, a nonexistent-assembly
payload, and a ConstructorTrap with a positive working control. The writer likewise
preserves the payload without constructing it; source hashes stay unchanged.
An additional additive `v10-malicious-nested` fixture instruments object and array
`_type` **and** `$type` paths with NestedConstructorTrap. Its working constructor
control throws/increments, while both reader and writer leave both trap counters
zero and retain the exact full document. Existing original and adversarial fixture
library bytes remain untouched.
`RawImportTests.Negative_guard_rejects_typed_mapping_and_object_construction` proves
that BsonMapper, typed GetCollection, ToObject, JSON deserialization and Activator
patterns are rejected. The guard covers evaluated reader compiler sources.
It also rejects LiteCollection, BsonSerializer, every Deserialize*/Populate* API,
AssemblyLoadContext, dynamic dispatch, MethodInfo/ConstructorInfo and Invoke, with
negative controls for each previously uncovered path. This is a regression guard,
not a proof against arbitrarily obfuscated future code; review remains required.

The CLI's caller-supplied reader DLL is trusted executable configuration, not an
untrusted-input/plugin boundary. Do not expose that argument to untrusted callers.
The CLI currently has no reader timeout; memory/recursion and runaway execution
remain explicit residual resource risks under the owner's exception, not sandbox
guarantees. Directory-fsync/power-loss durability is outside this process-death
contract. None of these limitations weakens the verify-before-publish or read-only
source rules.

`RawImportTests.LiteDB_and_importer_types_cannot_escape_the_raw_reader_and_reader_cannot_load_SQLite_or_Core`
checks both dependency directions. It complements the existing architecture table
and compiled/source gates, which remain enabled. Two adversarial reviews and real
linux-ci-backed floor capture are delivery gates; local test counts are not floor evidence.

## CI-backed floor capture

Actual [linux-ci run 43913516510](https://github.com/seanburkes/playnite-linux/actions/runs/43913516510)
on merged implementation head `0bf3204e04db5c6829765a1241600a92a0c36bfa` passed.
Downloaded JUnit testcase elements and MTP exit files independently confirm
**1695 Core / 153 Architecture / 4 Smoke**, all passed, zero failures/skips, every
exit 0. Core includes main's hardened schema-version-3/settings union (1671) plus
24 importer cases; Architecture adds nine raw-reader policy cases to 144.
The additive floor commit retains every earlier capture and requires its own
linux-ci revalidation before readiness. Two firstmate-arranged adversarial reviews
remain a merge gate; this capture is not independent human approval.

## Review B disposition (before renewed readiness)

- **P2-1 fixed:** RawImportTests now rejects LiteCollection/BsonSerializer,
  Deserialize*/Populate*, AssemblyLoadContext, dynamic, MethodInfo/ConstructorInfo
  and Invoke. Ten added negative cases prove the missed surfaces fail. The audit
  exception test additionally scans test projects and global build/package files
  and rejects disabled audit mode or NU1904 NoWarn.
- **P2-2 fixed:** the additive nested fixture and
  `Nested_object_and_array_type_paths_have_real_constructor_traps_with_working_controls`
  instrument the actual nested object/array `_type` and `$type` paths, rather than
  relying only on Process/FileInfo string assertions. Both constructor counters
  remain zero through reader and writer, with working throwing/incrementing controls.
- **P2-3 in delivery gate:** the earlier floor commit `b13630d` was baseline/doc/shard
  only relative to reviewed `0bf3204`, with no reviewed-code change. Its final-head
  linux-ci run 43914304294 independently reconfirmed 1695/153/4 and all six checks
  passed. Review fixes add tests, so a new real-CI floor capture and board completion
  will follow the final fixes; the queue deliberately remains in-progress meanwhile.
- **P2-4 fixed:** public RunAsync now requires the source directory and checks source,
  destination and generated-artifact containment plus all ancestor symlinks itself,
  before writes, independent of the CLI. Direct-API tests cover source containment,
  a symlinked parent, a live-path symlink, and source/staging aliasing. The directly
  invoked reader also rejects source ancestor symlinks.
- **P3 fixed:** malformed GameId errors now retain record provenance; the broken
  README citation points to actual Windows provenance; all three fixture SHA256SUMS
  file sets and bytes are asserted. No earlier fixture library bytes changed.
- **P3 clarified/retained:** managed failure uses the same owned restartable staging
  as process death; SourceHash fingerprints the intermediate; no directory-fsync
  power-loss guarantee; trusted reader-DLL configuration, no reader timeout and
  unbounded capture remain explicit resource/security limitations. These are not
  represented as sandbox guarantees or silently claimed fixed.
- **P3 lock-graph evidence:** the floor head's real locked restore and warning-as-error
  build both passed. ReferenceOutputAssembly=false builds the reader without linking
  it into either test runner's deps.json; neither runner deps.json contains LiteDB.
  Generated lock metadata is not a shipped runtime reference or a second suppression.

Review A and final-head CI/floor/board gates remain required. No independent human
approval is claimed, and the PR stays draft until both reviews are disposed.

# p4-003 — replacement publication, receipt and reader safety

## Contract and mechanism

`LibraryMigration.RunAsync` now replaces an existing **unmigrated** SQLite library.
A destination containing a migration receipt is refused even if the staging tree
and external report have been removed. Dry-run still writes nothing. The CLI and
reader-only LiteDB executable are unchanged; the writer still references no LiteDB.

The verified, checkpointed, closed staging file is flushed to disk **with** its
`migration_receipt` row (source SHA-256 and complete serialized audit report).
Publication uses same-filesystem `File.Move(..., overwrite: true)` / atomic Linux
rename, never an in-place copy onto the live database. A durable byte copy of the
old, closed main file is retained as `<destination>.import/previous.sqlite`.
The source v10 directory remains byte-identical; ADR-005 rollback to the v10 copy
is unchanged. The external JSON report remains an audit artifact, not a commit bit.

SQLite WAL makes bare file rename unsafe when connections overlap generations.
Every supported `LibraryDatabase` initialization/read/write therefore takes a
shared `<canonical-destination>.publication.lock` lease before opening SQLite and
holds it until the connection/transaction is disposed. Publication takes the
exclusive lease, draining old connections and their WAL close/checkpoint first.
Linux `FileStream` uses shared/exclusive flock for these share modes. The lock
inode is retained, not deleted; symbolic-link lock files are refused. Contention
waits asynchronously and is cancellable. SQLite's existing writer and snapshot
semantics are unchanged outside the brief publication window.

Under that exclusive lease the importer refuses remaining live SQLite sidecars,
rechecks repeat-run protection, retains/flushes the old file, renames the new one,
confirms its receipt, then releases the lease. A caught failure/cancellation in
this window restores the retained old file by atomic rename (or removes a fresh
unconfirmed destination), **before readers can reopen**. A post-completion failure
never rolls back. The retained old copy remains available after success.

## Formal refinement (model unchanged)

Firstmate ruling `p4-003.inbox/001`: rollback is confined to publication, never
after completion. `PublishPartial` / `Publishing` corresponds to the exclusive
lease and unconfirmed private swap. The model's `liveDb` is the reader-visible
artifact, not an observation that bypasses the publication lease. A managed
failure restores old before lease release; no reader-visible new-to-old transition
is introduced. Successful confirmation/release corresponds to atomic `Publish`.
If the process dies after physical rename, OS lock release exposes the complete
new artifact with its co-published receipt: this refines `Publish` followed by
`Crash`. `Recover` must preserve this new artifact, as the unchanged model says.

`SourceUnchanged`, `NoCommitWithoutVerify`, `BackupBeforeWrite`, `AtomicLive`,
`ReaderNeverPartial`, `DurableReceipt`, `CrashSafe`, `CleanResumable` and
`RepeatRunSafe` are cited by name on the behavioral tests. `source.json` is the
source backup before staging writes (`BackupBeforeWrite`); the retained old-live
copy separately protects the publication window.

## Crash matrix and evidence

The additive `tests/golden/import-replacement/crash-matrix.json` has explicit
provenance and is consumed by real child-process SIGKILL tests over an existing
one-game library, not just a fresh path:

| Checkpoint | After process death | Restart |
| --- | --- | --- |
| Read, Transformed | Entire old database | Import succeeds |
| Copied, PartialWrite, Written, Verified | Entire old database; private staging only | Owned staging is rebuilt; import succeeds |
| BeforeSwap | Entire old database | Import succeeds |
| Publishing (retained old, before rename) | Entire old database | Import succeeds |
| Swapped (after rename, before confirmation) | Entire new database **and receipt** | Refuses even without external report |
| ReceiptConfirmed (before lease release) | Entire new database **and receipt** | Refuses even without external report |
| Committed (completed) | Entire new database **and receipt** | Refuses even without external report |

The Windows-produced `tests/fixtures/v10/library` is reused without modification:
3 expected/imported Steam games, 1 custom game reported, all 9 documents accounted
for. The old destination is explicitly synthetic. Tests snapshot every source
byte throughout; no Windows-oracle output is reaccepted.

Managed exception/cancellation is injected at BeforeSwap, Publishing, Swapped and
ReceiptConfirmed. Each compares the restored main file with the complete pre-run
byte array **before reopening SQLite**, checks old rows, and successfully retries.
A real missing-source-file rename failure separately proves the retained backup
and old live file are byte-identical. A post-completion failure proves no rollback.

The reader-window theory launches the actual CLI in another process, pauses it
inside Publishing/Swapped/ReceiptConfirmed, starts a real `LibraryDatabase` read,
proves it cannot finish inside the window, resumes publication and checks every
new game identity. A second blocked read is cancelled at each window. Another
test holds an old snapshot open across the replacement attempt, reads the old
data twice, then releases it and verifies publication. A rollback-window reader
test starts after physical rename and proves it sees only old after rollback.

## Boundaries

The supported connection boundary is `LibraryDatabase`; arbitrary raw SQLite
clients do not honor the publication lease and must be closed before import.
An already-open raw client with sidecars is explicitly refused and tested. This
is not protection against a hostile process racing a raw open or replacing files
in the private regular-file namespace. Hard-linked alternate database spellings
are not supported publication aliases. Symlinked paths are rejected by import;
normal API symlink aliases resolve to the same canonical lease.

Crash evidence is process death, not sudden power loss of directory metadata;
this unit does not claim parent-directory fsync. The receipt itself is inside the
checkpointed/fsynced artifact, never a post-rename external marker. Quiesce v10
while exporting (the existing source-reader contract). Retained staging/report/
backup files are deliberate audit/recovery material, not exposed partial data.

## Validation and review

Local narrow suites and real final-head linux-ci evidence are recorded in the
unit shard. The CI-backed floor capture is added only after downloading actual
linux-ci results. Two firstmate-arranged adversarial diff-only reviews are
requested; no independent human approval or merge authority is claimed.

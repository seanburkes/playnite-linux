# p4-003 — replacement publication, receipt and reader safety

## Contract and mechanism

`LibraryMigration.RunAsync` can replace an existing **unreceipted** SQLite library
only with `replaceExisting: true` (`--replace-existing` in the CLI). A library made
by the earlier importer has no in-artifact receipt and cannot reliably be
distinguished from an unmigrated library; default refusal protects either kind,
including later user edits. The explicit opt-in accepts replacement of either.
A destination containing a migration receipt is always refused, even with opt-in
and even if the staging tree
and external report have been removed. Dry-run still writes nothing. The CLI and
reader-only LiteDB executable are unchanged; the writer still references no LiteDB.

The verified, checkpointed, closed staging file is flushed to disk **with** its
`playnite_migration_receipt` row (both identity fingerprints below and complete
serialized audit report, all checked at confirmation).
Publication uses same-filesystem `File.Move(..., overwrite: true)` / atomic Linux
rename, never an in-place copy onto the live database. A durable byte copy of the
old, closed main file is retained as `<destination>.import/previous.sqlite`.
The source v10 directory remains byte-identical; ADR-005 rollback to the v10 copy
is unchanged. The external JSON report remains an audit artifact, not a commit bit.
An earlier external report is retained as staging `previous-report.json` before
the active report is removed. A new success report is staged privately and moved
atomically to its audit path only after database rename, receipt verification,
lease release and the `Committed` completion checkpoint. A fault at `Committed`
leaves no success audit, but leaves the whole new database and its repeat-run
receipt intact. Rollback also leaves no new success report at the live audit path.

### Fingerprint definitions (report v1, additive field)

`SourceHash` is the SHA-256 of the **normalized intermediate envelope**, not a raw
source-tree hash: UTF-8 of Newtonsoft `JObject.ToString(Formatting.None)`, parsed
with `DateParseHandling.None`, preserving property/record order and JSON values.
Whitespace in the input envelope is immaterial; changing normalized records is not.
The historical field name is retained for report compatibility, but is never
claimed to hash concatenated raw source bytes.

`SourceManifestHash` independently fingerprints the reader's source-byte manifest:
sort `files` ordinally by relative `path`, project exactly `{path,size,sha256}` in
that property order, serialize the array with the default System.Text.Json UTF-8
encoder, then SHA-256. Each entry's `sha256` is the reader's raw-file SHA-256 and
`size` its byte length. This binds relative names, sizes and every captured file's
bytes, rather than only imported rows. It is a reader-supplied manifest identity,
not an independent writer rehash or a filesystem-wide atomic snapshot. The test
reconstructs it independently from every frozen fixture file and proves a changed
record changes `SourceHash` but not `SourceManifestHash`.
Unreadable entries from the asset reader retain null size/hash in this manifest;
no raw bytes are claimed for them. The full-envelope `SourceHash` separately
binds their status and the complete receipt report retains per-asset outcomes.

### SQLite generation lease

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
this window removes derived `-wal`, `-shm`, `-journal` and matching `-mj*`
super-journals of the discarded generation, then restores the retained old file by
atomic rename (or removes a fresh unconfirmed destination), **before readers can
reopen**. A post-completion failure
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
| Committed (completed, before external audit publication) | Entire new database **and receipt**, no success audit | Refuses from artifact alone |

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
inside Publishing/Swapped/ReceiptConfirmed, starts a real `LibraryDatabase` open,
proves it cannot finish inside the window, resumes publication and checks every
new game identity. A second blocked read is cancelled at each window. Another
test holds an old snapshot open across the replacement attempt, reads the old
data twice, then releases it and verifies publication. A rollback-window reader
test starts after physical rename and proves it sees only old after rollback.
The window tests use an async-flow-local contention rendezvous fired by an actual
failed OS lease acquisition, not a sleep or a scheduling guess. In each held CLI
window a real `OpenAsync` reaches that barrier; the CLI is then resumed and the
opened database is queried for the complete expected new identity set. Cancellation
and old-reader drain each have their own actual-contention barrier. The rollback
reader also proves it attempted the held OS lock before rollback.

## Boundaries

The supported connection boundary is `LibraryDatabase`; arbitrary raw SQLite
clients do not honor the publication lease and must be closed before import.
An already-open raw client with sidecars is explicitly refused and tested. All
WAL/SHM files, including empty ones, are refused at the closed-file boundary;
rollback journals and super-journals are refused before even probing the live
artifact. A real DELETE-journal transaction and a retained super-journal test prove
refusal without mutation. This
is not protection against a hostile process racing a raw open or replacing files
in the private regular-file namespace. Hard-linked alternate database spellings
are not supported publication aliases. Symlinked paths are rejected by import;
normal API symlink aliases resolve to the same canonical lease. `OpenAsync` leases
initialization, not the entire `LibraryDatabase` object's lifetime. Each later
read/write acquires its own connection-lifetime lease, so an idle object does not
block replacement and its next operation sees the new generation. An active
snapshot retains old until it finishes; both cases are tested.

The retained old copy is main-file-only **after** all supported SQLite handles
have drained and the old WAL has checkpointed/closed, and only when no rollback
journal, super-journal, WAL or SHM remains. It is not a backup of an arbitrary
open or unrecovered SQLite file. The explicit sidecar refusal is the precondition
that makes a standalone main-file copy valid; no previous journal mode is assumed.

Crash evidence is process death, not sudden power loss of directory metadata;
this unit does not claim parent-directory fsync. The receipt itself is inside the
checkpointed/fsynced artifact, never a post-rename external marker. Quiesce v10
while exporting (the existing source-reader contract). Retained staging/report/
backup files are deliberate audit/recovery material, not exposed partial data.

## Validation and review

Local narrow suites and real final-head linux-ci evidence are recorded in the
unit shard. The CI-backed floor capture is added only after downloading actual
linux-ci results. Both firstmate-arranged adversarial diff-only reviews filed
findings on f0c8e8e; fix-head re-reviews and final CI remain gates. No independent
human approval or merge authority is claimed.

### Review A/B disposition

Both adversarial reviews of f0c8e8e found no P1. Their six P2s are addressed in
the follow-up implementation:

1. Earlier unreceipted successful imports: default Core/CLI refusal, explicit
   replacement consent, and a prior-format imported library with user edits test.
2. Mixed-generation rollback sidecars: remove the discarded generation's sidecars
   before restoration. The ReceiptConfirmed fault test creates real WAL/SHM via
   a read-only probe plus journal/super-journal remnants, and asserts absence of
   every derived file and byte-identical old main **after** rollback.
3. Misleading audit on rollback: archive the earlier audit, publish the new audit
   only after the completion checkpoint, remove it on rollback; failure/cancellation
   tests and the ReceiptConfirmed fault test assert no success report remains.
4. Source identity wording: retain/document the normalized-intermediate
   `SourceHash`, add and verify the separately named `SourceManifestHash` from the
   reader's byte manifest; independently check both in a test.
5. Timing-dependent reader proof: actual OS-lock contention barriers replace all
   elapsed-time guesses; real initialization/read operations are exercised and
   the complete returned identities asserted on either publication or rollback.
6. Rollback/super-journal gap: fail closed before the live probe and final copy;
   real rollback-journal and super-journal refusal/retry tests.

P3 dispositions: use the namespaced `playnite_migration_receipt` table and verify
both identity hashes **and complete ReportJson** (tamper controls for all three).
The suggested object-lifetime lease is deliberately not adopted: operation/SQLite
connection lifetime is the required isolation boundary, and an idle database
object must not prevent replacement forever. That boundary and the old-copy
standalone-file precondition are explicit above. No model changes or new public
test hooks are introduced; the contention rendezvous is internal and async-local.

Closure Review A additionally required acknowledgment/audit ordering: the external
audit move now follows `Committed`. The post-completion fault test asserts no audit
exists both inside the checkpoint and after the thrown failure, proves all new
rows remain, removes all staging material, and proves artifact-only repeat refusal.

### Combined asset/publication integration

This unit builds on p4-001 SQLite, p4-002 reader/importer and the reviewed p4-005
asset implementation at `6c38c22da6a80fc1d4907a7f152bbf84dfed4516` from
https://github.com/seanburkes/playnite-linux/pull/96. Per steering006 that reviewed
head is merged directly while its unrelated shared Flatpak/main gate is repaired
by firstmate; this worker does not rerun or edit packaging.

The merge explicitly retains **both** `MigrationReport.SourceManifestHash` and
`Assets` (plus its missing/unreadable/empty counters), in both dry-run and real-run
construction. The namespaced receipt is created only after asset copying and
reference rewriting; its verified ReportJson includes the complete asset report.
Generated-artifact containment includes both the `.assets` store and publication
lease. Explicit replacement consent and artifact-only refusal remain authoritative,
not the asset branch's superseded unconditional existing-path refusal.

The combined regression replaces an existing library using the unchanged Windows
v10 databases plus p4-005's provenance-bearing `v10-assets` derivative (applying
its `references.json` to the captured intermediate as that unit does), checks two
copied asset blobs by hash and one missing reference, checks both fingerprint
columns and exact full ReportJson/audit equality, removes audit/staging, and proves
repeat refusal from the artifact alone while the source remains byte-identical.
The complete narrow Core suite covers asset, receipt, report, replacement and crash
tests together; its measured combined-head counts and real CI run are recorded in
the shard/floor evidence, never inferred by summing the two branches.

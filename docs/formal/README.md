# Formal models

Three TLA+ models cover the state machines where a bug is expensive and concurrency/crash
recovery is real. They are **safety models**: they check invariants over every reachable
state. `LibraryImport` checks safety only, not eventual success under repeated crashes.
Other models retain their existing configured temporal checks; unconfigured liveness
claims are design intent, not verified properties.

| Model | Invariants checked | Status |
| --- | --- | --- |
| `LibraryImport.tla` | `TypeOK`, `SourceUnchanged`, `ReadSnapshotImmutable`, `NoCommitWithoutVerify`, `ReaderNeverPartial`, `AtomicLive`, `BackupBeforeWrite`, `DurableReceipt`, `CrashSafe`, `CleanResumable`, `RepeatRunSafe` | TLC: no error, 117 distinct states; two source rows, three run attempts, crash/recovery and both repeat-run outcomes; `InPlacePublish = FALSE` |
| `GameSession.tla` | `OneSessionPerGame`, `NoOrphanProcess`; property `PlaytimeNonDecreasing` | TLC: no error, 400 distinct states (bounded playtime); design spec, not a falsifiable concurrency model |
| `SettingsMigration.tla` | `OldFileUntouched`, `NoUnknownLoss`, `AtomicWrite`, `RollbackAvailable`, `BackupBeforeWrite` | TLC: no error, 11 distinct states |

## Run

```sh
# one-time
mkdir -p /tmp/opencode/tla
curl -sSL -o /tmp/opencode/tla/tla2tools.jar \
  https://github.com/tlaplus/tlaplus/releases/latest/download/tla2tools.jar

# check all models
scripts/formal/run_tlc.sh

# baseline plus a required counterexample for every LibraryImport invariant
python3 scripts/formal/check_library_import_mutations.py
```

`run_tlc.sh` uses a separate metadir per model so parallel runs never collide.

## What the models do not cover

- Data transformation correctness (that is the migration test corpus).
- That the C# implementation matches the model; that is what the invariant-tagged tests are
  for, and it is not a proof.
- UI behaviour (that is the headless test suite).
- WebKit, DB, or filesystem implementation details.
- Liveness under real scheduling. `CancelTerminates` and `SessionTerminates` are stated in
  `GameSession.tla` as design intent; checking them needs per-action fairness that made the
  two-game model intractable. A one-game refinement is a later task. Do not claim liveness as
  verified.

## Implementation linkage

Implementation tests must cite the applicable invariant using
`[Trait("Invariant", "<Model>.<Invariant>")]`. The LibraryImport linkage below is a
requirement for the importer/swap units, not a claim that those tests already exist.
Model changes require the `oracle-change` route; this formal-only unit does not establish
implementation conformance.

### Library import (Phase 4)

| Implementation surface | Required `LibraryImport` invariants | Evidence required in that unit |
| --- | --- | --- |
| p4-002 source reader/importer | `SourceUnchanged`, `ReadSnapshotImmutable`, `BackupBeforeWrite` | Read-only source handles; byte-identical source tree after success, failure, cancellation and process death; immutable captured input; backup before staging writes |
| p4-002 temporary database and verifier | `NoCommitWithoutVerify`, `CleanResumable` | Incomplete/invalid staging cannot reach publication; verification failure discards staging on restart; restart reads the unchanged source again |
| p4-003 atomic publish and readers | `AtomicLive`, `ReaderNeverPartial`, `NoCommitWithoutVerify` | Reader sees whole old or whole new database at each boundary, including death immediately before/after publish; no live-path partial state awaiting rollback |
| p4-003 recovery and durable source receipt; p4-002 repeat-run entry point | `DurableReceipt`, `CrashSafe`, `CleanResumable`, `RepeatRunSafe` | Kill/restart at every boundary; discard staging, preserve published destination/receipt; second invocation after successful publish (including lost success response) no-ops or explicitly refuses without republishing |

`LibraryImport.cfg` checks exactly the eleven invariants listed in the index, with
`SourceRows = {rowA, rowB}`, `OldRows = {oldRow}`, `MaxRuns = 3` and
`InPlacePublish = FALSE`. `TypeOK` checks the finite state domains, deliberately
allowing partial subsets in `liveDb` and `readerSees`; it does not encode atomicity.
`AtomicLive` applies to **all** reachable
states, including failures, unlike the former terminal-state-only predicate. The source
is a fixed two-row set in the TLC instance; row writes occur one at a time in either
order. `MaxRuns = 3` covers a failed attempt, successful retry and repeated invocation.
The bound counts attempts, not crashes: recovery can itself be interrupted repeatedly.
Failures are possible at every active stage, including verification and before publish;
process death is possible before reading, between row writes, before/after verification,
before/after publication and after a repeat-run response. The explicit `Publishing`
state between `PublishPartial` and `Publish` also permits failure, process death and
concurrent reads. `Recover` clears staging and
volatile read/verification state without changing the destination or receipt. Publication
never rolls back after success, even if the caller did not observe that success.

The receipt abstracts a stable identity of this one source, stored **inside the artifact
published atomically with the data**. It is not a separately written completion file.
`Start` atomically selects the observable no-op/refusal response for an existing receipt;
the model does not specify UI delivery after a process dies. There is one importer and
one reader; concurrent importers, source changes by other processes, hash collisions,
multiple distinct sources and database transformation correctness are outside this model.
The source is never written by any action; snapshots are discarded on restart, not reused
without reading. With `InPlacePublish = FALSE`, staging may remain partial after death,
but is never the live database.

`InPlacePublish = TRUE` represents the rejected implementation: `PublishPartial`
overwrites the live destination with a strict subset of the verified staging database
before the final swap. This makes in-place publication, a reader observing it, and a
crash leaving it behind representable. TLC finds counterexamples for `AtomicLive`,
`ReaderNeverPartial` and `CrashSafe` respectively, without changing those predicates
or narrowing the state domains. Atomicity is conditional on choosing the safe protocol;
it is not a theorem that arbitrary in-place implementations are safe.

**Durability assumption:** `Publish` is a single durable filesystem operation over a
fully verified, closed staging artifact, including its receipt. The platform/swap unit
must justify same-filesystem atomic replacement, flush/file/directory ordering and SQLite
WAL/sidecar handling with real crash tests. TLC does not prove Linux/filesystem power-loss
semantics, backup content integrity or C# refinement. The backup flag records ordering
only; crash safety here comes from not touching the old destination before publication.
The old `Termination` property is deliberately no longer configured: arbitrary recurring
failures do not guarantee completion, and no eventual-success claim is made.

### Reproducible mutation checks

`scripts/formal/check_library_import_mutations.py` first checks the unchanged baseline
with all eleven invariants. It then runs each mutant with `TypeOK` and its intended
invariant (only `TypeOK` for the domain mutant). Each run must fail with TLC exit 12
and the **named invariant violation**, not a syntax error, timeout, unrelated invariant
or incidental state-domain failure. The three publication counterexamples must include
`PublishPartial`; the reader/crash checks must also include `ReadVisible`/`Crash`.
Mutants target assignments within named actions, not source line numbers or whole-file
text snippets. Coverage must exactly match the configuration's invariant list.

| Invariant | Mutant that must be rejected | Check exercised |
| --- | --- | --- |
| `TypeOK` | `Start` sets attempts beyond `MaxRuns` | Post-state domain |
| `SourceUnchanged` | `Read` erases the source | Post-state source preservation |
| `ReadSnapshotImmutable` | `Read` captures an empty snapshot | Post-state capture integrity, not a read/write guard bypass |
| `NoCommitWithoutVerify` | `Publish` loses the verification flag on commit | Post-state assertion after the verification guard passed, not a guard bypass |
| `ReaderNeverPartial` | Unsafe in-place publication followed by a concurrent read | Unsafe protocol variant and observed post-state |
| `AtomicLive` | Unsafe in-place publication exposes partial live data | Unsafe protocol variant and live post-state |
| `BackupBeforeWrite` | `Backup` proceeds without recording a backup | Post-state ordering assertion, not bypass of a backup-exists write guard |
| `DurableReceipt` | `Publish` omits the durable receipt | Post-state receipt/data consistency |
| `CrashSafe` | Crash between unsafe partial publication and final swap | Unsafe protocol variant and crashed post-state |
| `CleanResumable` | `Recover` retains dirty staging | Post-state cleanup |
| `RepeatRunSafe` | `Start` reports another import instead of no-op/refusal | Post-state repeated-run response |

These mutations demonstrate falsifiability of the named **state invariants**. None is
a guard-bypass mutation: in particular the verification, snapshot and backup mutations
do not independently establish that implementation guards reject invalid input.

The harness uses the same `TLA2TOOLS_JAR` override as the all-model runner. It writes
mutated models/configs to automatically removed temporary directories; it never edits
the checked-in oracle. Set `FORMAL_MUTATION_OUTPUT` to a scratch directory to retain
full TLC logs for the baseline and every mutant.

The existing `plan-guards` formal job invokes `scripts/formal/run_tlc.sh` for
`LibraryImport.cfg`, `SettingsMigration.cfg` and `GameSession.cfg`, then executes the
mutation harness using the same pinned jar. No separate job or model fork is needed.
The CI step sets `FORMAL_MUTATION_OUTPUT` and uploads the baseline/per-mutant TLC logs
as `library-import-mutation-tlc`, on failure as well as success when logs are available.

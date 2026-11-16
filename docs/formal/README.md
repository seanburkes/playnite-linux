# Formal models

Three TLA+ models cover the state machines where a bug is expensive and concurrency/crash
recovery is real. They are **safety models**: they check invariants over every reachable
state. `LibraryImport` checks safety only, not eventual success under repeated crashes.
Other models retain their existing configured temporal checks; unconfigured liveness
claims are design intent, not verified properties.

| Model | Invariants checked | Status |
| --- | --- | --- |
| `LibraryImport.tla` | `TypeOK`, `SourceUnchanged`, `ReadSnapshotImmutable`, `NoCommitWithoutVerify`, `ReaderNeverPartial`, `AtomicLive`, `BackupBeforeWrite`, `DurableReceipt`, `CrashSafe`, `CleanResumable`, `RepeatRunSafe` | TLC: no error, 114 distinct states; two source rows, three run attempts, crash/recovery and both repeat-run outcomes |
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

`TypeOK` checks the finite state domains. `AtomicLive` applies to **all** reachable
states, including failures, unlike the former terminal-state-only predicate. The source
is a fixed two-row set in the TLC instance; row writes occur one at a time in either
order. `MaxRuns = 3` covers a failed attempt, successful retry and repeated invocation.
The bound counts attempts, not crashes: recovery can itself be interrupted repeatedly.
Failures are possible at every active stage, including verification and before publish;
process death is possible before reading, between row writes, before/after verification,
before/after publication and after a repeat-run response. `Recover` clears staging and
volatile read/verification state without changing the destination or receipt. Publication
never rolls back after success, even if the caller did not observe that success.

The receipt abstracts a stable identity of this one source, stored **inside the artifact
published atomically with the data**. It is not a separately written completion file.
`Start` atomically selects the observable no-op/refusal response for an existing receipt;
the model does not specify UI delivery after a process dies. There is one importer and
one reader; concurrent importers, source changes by other processes, hash collisions,
multiple distinct sources and database transformation correctness are outside this model.
The source is never written by any action; snapshots are discarded on restart, not reused
without reading. Staging may remain partial after death, but is never the live database.

**Durability assumption:** `Publish` is a single durable filesystem operation over a
fully verified, closed staging artifact, including its receipt. The platform/swap unit
must justify same-filesystem atomic replacement, flush/file/directory ordering and SQLite
WAL/sidecar handling with real crash tests. TLC does not prove Linux/filesystem power-loss
semantics, backup content integrity or C# refinement. The backup flag records ordering
only; crash safety here comes from not touching the old destination before publication.
The old `Termination` property is deliberately no longer configured: arbitrary recurring
failures do not guarantee completion, and no eventual-success claim is made.

The existing `plan-guards` formal job invokes `scripts/formal/run_tlc.sh`, which explicitly
checks `LibraryImport.cfg`, `SettingsMigration.cfg` and `GameSession.cfg`. Updating this
model/config therefore updates the existing CI check without a separate job or model fork.

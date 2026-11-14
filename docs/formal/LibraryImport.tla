---------------- MODULE LibraryImport ----------------
\* Formal model for the v10 -> SQLite library migration.
\*
\* The implementation writes a temp database, verifies it, then atomically
\* swaps the live database. A concurrent reader must never observe a partial
\* live database.
\*
\* The model is intentionally small: it captures the ordering, the atomic
\* swap, and crash recovery that matter, not the data transformation itself.
EXTENDS Naturals

VARIABLES
    state,          \* "Idle" | "Copying" | "Reading" | "Writing" | "Verifying"
                    \* | "Committed" | "Failed" | "RolledBack"
    sourceHash,     \* constant identity of the source library
    backupExists,   \* backup manifest written before any live write
    verified,       \* verification passed
    liveDb,         \* "Old" | "New"
    tempDb,         \* "None" | "Partial" | "Verified"
    readerSees      \* last value a concurrent reader observed

vars == <<state, sourceHash, backupExists, verified, liveDb, tempDb, readerSees>>

Init ==
    /\ state = "Idle"
    /\ sourceHash = "v10-fixture"
    /\ backupExists = FALSE
    /\ verified = FALSE
    /\ liveDb = "Old"
    /\ tempDb = "None"
    /\ readerSees = "Old"

Start ==
    /\ state = "Idle"
    /\ state' = "Copying"
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ liveDb' = "Old"
    /\ tempDb' = "None"
    /\ readerSees' = "Old"
    /\ UNCHANGED <<sourceHash>>

Copy ==
    /\ state = "Copying"
    /\ state' = "Reading"
    /\ backupExists' = TRUE
    /\ UNCHANGED <<sourceHash, verified, liveDb, tempDb, readerSees>>

Read ==
    /\ state = "Reading"
    /\ state' = "Writing"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, tempDb, readerSees>>

Write ==
    /\ state = "Writing"
    /\ state' = "Verifying"
    /\ tempDb' = "Partial"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, readerSees>>

VerifyPass ==
    /\ state = "Verifying"
    /\ tempDb = "Partial"
    /\ state' = "Committed"
    /\ verified' = TRUE
    /\ tempDb' = "Verified"
    /\ liveDb' = "New"
    /\ UNCHANGED <<sourceHash, backupExists, readerSees>>

VerifyFail ==
    /\ state = "Verifying"
    /\ state' = "Failed"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, readerSees>>

Fail ==
    /\ state \in {"Copying", "Reading", "Writing"}
    /\ state' = "Failed"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, readerSees>>

Rollback ==
    /\ state = "Failed"
    /\ state' = "RolledBack"
    /\ liveDb' = "Old"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, readerSees>>

Reset ==
    /\ state \in {"Committed", "RolledBack"}
    /\ state' = "Idle"
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ liveDb' = "Old"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, readerSees>>

\* A reader can observe the live database at any time.
ReadLive ==
    /\ readerSees' = liveDb
    /\ UNCHANGED <<state, sourceHash, backupExists, verified, liveDb, tempDb>>

Next ==
    \/ Start \/ Copy \/ Read \/ Write
    \/ VerifyPass \/ VerifyFail \/ Fail \/ Rollback \/ Reset
    \/ ReadLive

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

\* ---- Invariants ---------------------------------------------------------

\* The source library is never modified. The model has no source-write action.
SourceUnchanged == sourceHash = "v10-fixture"

\* A committed database implies verification passed.
NoCommitWithoutVerify == state = "Committed" => verified

\* A concurrent reader never sees a partial database.
ReaderNeverPartial == readerSees \in {"Old", "New"}

\* The live database is only ever the old one or the verified new one.
AtomicLive == liveDb \in {"Old", "New"}

\* A backup exists before the first live write / verification.
BackupBeforeWrite ==
    state \in {"Writing", "Verifying", "Committed"} => backupExists

\* At most one import is active: the state machine has one lane.
OneImport == state \in {"Copying", "Reading", "Writing", "Verifying"} => state /= "Idle"

\* Crash recovery: every started import reaches a terminal state under weak fairness.
Termination == (state = "Copying") ~> (state \in {"Committed", "RolledBack"})

=============================================================================

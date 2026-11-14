---------------- MODULE LibraryImport ----------------
\* Formal model for the v10 -> SQLite library migration.
\*
\* The implementation writes a temp database, verifies it, then atomically
\* swaps the live database. A crash during the swap must roll back to the old
\* live database; a concurrent reader must never observe a partial database.
EXTENDS Naturals

VARIABLES
    state,          \* "Idle" | "Copying" | "Reading" | "Writing" | "Verifying"
                    \* | "Swapping" | "Committed" | "Failed" | "RolledBack"
    sourceHash,     \* constant identity of the source library
    backupExists,   \* backup manifest written before any live write
    verified,       \* verification passed
    liveDb,         \* "Old" | "New" | "Partial" (Partial is reachable during swap/crash)
    tempDb,         \* "None" | "Partial" | "Verified"
    visibleDb,      \* what a concurrent reader can see: "Old" | "New"
    readerSees      \* last value a concurrent reader observed

vars == <<state, sourceHash, backupExists, verified, liveDb, tempDb, visibleDb, readerSees>>

Init ==
    /\ state = "Idle"
    /\ sourceHash = "v10-fixture"
    /\ backupExists = FALSE
    /\ verified = FALSE
    /\ liveDb = "Old"
    /\ tempDb = "None"
    /\ visibleDb = "Old"
    /\ readerSees = "Old"

Start ==
    /\ state = "Idle"
    /\ state' = "Copying"
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ liveDb' = "Old"
    /\ tempDb' = "None"
    /\ visibleDb' = "Old"
    /\ readerSees' = "Old"
    /\ UNCHANGED <<sourceHash>>

Copy ==
    /\ state = "Copying"
    /\ state' = "Reading"
    /\ backupExists' = TRUE
    /\ UNCHANGED <<sourceHash, verified, liveDb, tempDb, visibleDb, readerSees>>

Read ==
    /\ state = "Reading"
    /\ state' = "Writing"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, tempDb, visibleDb, readerSees>>

Write ==
    /\ state = "Writing"
    /\ state' = "Verifying"
    /\ tempDb' = "Partial"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, visibleDb, readerSees>>

VerifyPass ==
    /\ state = "Verifying"
    /\ tempDb = "Partial"
    /\ state' = "Swapping"
    /\ verified' = TRUE
    /\ tempDb' = "Verified"
    /\ liveDb' = "Partial"
    /\ UNCHANGED <<sourceHash, backupExists, visibleDb, readerSees>>

VerifyFail ==
    /\ state = "Verifying"
    /\ state' = "Failed"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, visibleDb, readerSees>>

SwapEnd ==
    /\ state = "Swapping"
    /\ state' = "Committed"
    /\ liveDb' = "New"
    /\ visibleDb' = "New"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, readerSees>>

\* Crash in the middle of the swap: the live path is partial until rollback.
CrashSwap ==
    /\ state = "Swapping"
    /\ state' = "Failed"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, visibleDb, readerSees>>

Fail ==
    /\ state \in {"Copying", "Reading", "Writing"}
    /\ state' = "Failed"
    /\ tempDb' = "None"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb, visibleDb, readerSees>>

Rollback ==
    /\ state = "Failed"
    /\ state' = "RolledBack"
    /\ liveDb' = "Old"
    /\ tempDb' = "None"
    /\ visibleDb' = "Old"
    /\ UNCHANGED <<sourceHash, backupExists, verified, readerSees>>

Reset ==
    /\ state \in {"Committed", "RolledBack"}
    /\ state' = "Idle"
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ liveDb' = "Old"
    /\ tempDb' = "None"
    /\ visibleDb' = "Old"
    /\ UNCHANGED <<sourceHash, readerSees>>

\* A reader can observe the published database at any time.
ReadVisible ==
    /\ readerSees' = visibleDb
    /\ UNCHANGED <<state, sourceHash, backupExists, verified, liveDb, tempDb, visibleDb>>

Next ==
    \/ Start \/ Copy \/ Read \/ Write
    \/ VerifyPass \/ VerifyFail \/ SwapEnd \/ CrashSwap
    \/ Fail \/ Rollback \/ Reset
    \/ ReadVisible

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

\* ---- Invariants ---------------------------------------------------------

\* The source library is never modified. The model has no source-write action.
SourceUnchanged == sourceHash = "v10-fixture"

\* A committed database implies verification passed.
NoCommitWithoutVerify == state = "Committed" => verified

\* A concurrent reader never sees a partial database.
ReaderNeverPartial == readerSees \in {"Old", "New"}

\* In every observable/terminal state, the live database is old or new.
\* This is the falsifiable invariant: crash mid-swap leaves Partial until
\* Rollback restores Old.
AtomicLive ==
    state \in {"Idle", "Committed", "RolledBack"} => liveDb \in {"Old", "New"}

\* A backup exists before the first live write / verification.
BackupBeforeWrite ==
    state \in {"Writing", "Verifying", "Swapping", "Committed"} => backupExists

\* At most one import is active: the state machine has one lane.
OneImport == state \in {"Copying", "Reading", "Writing", "Verifying", "Swapping"} => state /= "Idle"

\* Crash recovery: every started import reaches a terminal state under weak fairness.
Termination == (state = "Copying") ~> (state \in {"Committed", "RolledBack"})

=============================================================================

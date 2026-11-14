---------------- MODULE LibraryImport ----------------
\* Formal model for the v10 -> SQLite library migration.
\*
\* The implementation must preserve these invariants:
\*   SourceUnchanged, NoCommitWithoutVerify, AtomicLive, BackupBeforeWrite.
\*
\* The model is intentionally small: it captures the ordering and crash
\* recovery that matter, not the data transformation itself.
EXTENDS Naturals

VARIABLES
    state,          \* "Idle" | "Copying" | "Reading" | "Writing" | "Verifying"
                    \* | "Committed" | "Failed" | "RolledBack"
    sourceHash,     \* constant identity of the source library
    backupExists,   \* backup manifest written before any live write
    verified,       \* verification passed
    liveDb          \* "Old" | "Partial" | "New"

vars == <<state, sourceHash, backupExists, verified, liveDb>>

Init ==
    /\ state = "Idle"
    /\ sourceHash = "v10-fixture"
    /\ backupExists = FALSE
    /\ verified = FALSE
    /\ liveDb = "Old"

Start ==
    /\ state = "Idle"
    /\ state' = "Copying"
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ liveDb' = "Old"
    /\ UNCHANGED <<sourceHash>>

Copy ==
    /\ state = "Copying"
    /\ state' = "Reading"
    /\ backupExists' = TRUE
    /\ UNCHANGED <<sourceHash, verified, liveDb>>

Read ==
    /\ state = "Reading"
    /\ state' = "Writing"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb>>

Write ==
    /\ state = "Writing"
    /\ state' = "Verifying"
    /\ liveDb' = "Partial"
    /\ UNCHANGED <<sourceHash, backupExists, verified>>

VerifyPass ==
    /\ state = "Verifying"
    /\ state' = "Committed"
    /\ verified' = TRUE
    /\ liveDb' = "New"
    /\ UNCHANGED <<sourceHash, backupExists>>

VerifyFail ==
    /\ state = "Verifying"
    /\ state' = "Failed"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb>>

Fail ==
    /\ state \in {"Copying", "Reading", "Writing"}
    /\ state' = "Failed"
    /\ UNCHANGED <<sourceHash, backupExists, verified, liveDb>>

Rollback ==
    /\ state = "Failed"
    /\ state' = "RolledBack"
    /\ liveDb' = "Old"
    /\ UNCHANGED <<sourceHash, backupExists, verified>>

Reset ==
    /\ state \in {"Committed", "RolledBack"}
    /\ state' = "Idle"
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ liveDb' = "Old"
    /\ UNCHANGED <<sourceHash>>

Next ==
    \/ Start \/ Copy \/ Read \/ Write
    \/ VerifyPass \/ VerifyFail \/ Fail \/ Rollback \/ Reset

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

\* ---- Invariants ---------------------------------------------------------

\* The source library is never modified. The model has no source-write action.
SourceUnchanged == sourceHash = "v10-fixture"

\* A committed database implies verification passed.
NoCommitWithoutVerify == state = "Committed" => verified

\* Observers never see a partial live database in an idle/terminal state.
AtomicLive ==
    state \in {"Idle", "Committed", "RolledBack"} => liveDb \in {"Old", "New"}

\* A backup exists before the first write to the live database.
BackupBeforeWrite ==
    state \in {"Writing", "Verifying", "Committed"} => backupExists

\* At most one import is active: the state machine has one lane.
OneImport == state \in {"Copying", "Reading", "Writing", "Verifying"} => state /= "Idle"

\* Crash recovery: every started import reaches a terminal state under weak fairness.
Termination == (state = "Copying") ~> (state \in {"Committed", "RolledBack"})

=============================================================================

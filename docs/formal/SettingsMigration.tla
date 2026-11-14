---------------- MODULE SettingsMigration ----------------
\* Formal model for migrating v10 settings to the new settings file.
\*
\* The implementation must preserve: OldFileUntouched, NoUnknownLoss,
\* AtomicWrite, RollbackAvailable, BackupBeforeWrite.
EXTENDS Naturals

VARIABLES
    state,          \* "Idle" | "Reading" | "Transforming" | "Writing"
                    \* | "Verifying" | "Committed" | "Failed" | "RolledBack"
    oldFile,        \* always "v10"; the source is never written
    newFile,        \* "Old" | "Partial" | "New"
    unknown,        \* every unknown property was preserved
    backup          \* a backup of the old file exists

vars == <<state, oldFile, newFile, unknown, backup>>

Init ==
    /\ state = "Idle"
    /\ oldFile = "v10"
    /\ newFile = "Old"
    /\ unknown = FALSE
    /\ backup = FALSE

Start ==
    /\ state = "Idle"
    /\ state' = "Reading"
    /\ newFile' = "Old"
    /\ unknown' = FALSE
    /\ backup' = FALSE
    /\ UNCHANGED <<oldFile>>

Read ==
    /\ state = "Reading"
    /\ state' = "Transforming"
    /\ UNCHANGED <<oldFile, newFile, unknown, backup>>

Transform ==
    /\ state = "Transforming"
    /\ state' = "Writing"
    /\ unknown' = TRUE
    /\ backup' = TRUE
    /\ UNCHANGED <<oldFile, newFile>>

Write ==
    /\ state = "Writing"
    /\ state' = "Verifying"
    /\ newFile' = "Partial"
    /\ UNCHANGED <<oldFile, unknown, backup>>

VerifyPass ==
    /\ state = "Verifying"
    /\ state' = "Committed"
    /\ newFile' = "New"
    /\ UNCHANGED <<oldFile, unknown, backup>>

VerifyFail ==
    /\ state = "Verifying"
    /\ state' = "Failed"
    /\ UNCHANGED <<oldFile, newFile, unknown, backup>>

Fail ==
    /\ state \in {"Reading", "Transforming", "Writing"}
    /\ state' = "Failed"
    /\ UNCHANGED <<oldFile, newFile, unknown, backup>>

Rollback ==
    /\ state = "Failed"
    /\ state' = "RolledBack"
    /\ newFile' = "Old"
    /\ UNCHANGED <<oldFile, unknown, backup>>

Reset ==
    /\ state \in {"Committed", "RolledBack"}
    /\ state' = "Idle"
    /\ newFile' = "Old"
    /\ unknown' = FALSE
    /\ backup' = FALSE
    /\ UNCHANGED <<oldFile>>

Next ==
    \/ Start \/ Read \/ Transform \/ Write
    \/ VerifyPass \/ VerifyFail \/ Fail \/ Rollback \/ Reset

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

\* ---- Invariants ---------------------------------------------------------

OldFileUntouched == oldFile = "v10"

NoUnknownLoss == state = "Committed" => unknown

AtomicWrite ==
    state \in {"Idle", "Committed", "RolledBack"} => newFile \in {"Old", "New"}

RollbackAvailable == state = "Failed" => oldFile = "v10"

BackupBeforeWrite ==
    state \in {"Writing", "Verifying", "Committed"} => backup

Termination ==
    (state = "Reading") ~> (state \in {"Committed", "RolledBack"})

=============================================================================

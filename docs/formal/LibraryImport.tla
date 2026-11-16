---------------- MODULE LibraryImport ----------------
\* Single importer, immutable source, disposable staging, atomic publication.
\* Destination data and source identity receipt belong to ONE durable artifact.
\* PublishPartial exposes preparation and its crash boundary. InPlacePublish
\* selects an unsafe implementation that overwrites the live path before swap.
\* The checked production configuration is FALSE; mutation checks exercise TRUE.
EXTENDS Naturals, FiniteSets

CONSTANTS SourceRows, OldRows, MaxRuns, InPlacePublish
ASSUME /\ SourceRows # {}
       /\ SourceRows # OldRows
       /\ MaxRuns \in Nat \ {0}
       /\ InPlacePublish \in BOOLEAN

VARIABLES state, source, snapshot, tempDb, liveDb, receipt,
          backupExists, verified, runs, publications, repeatRun, outcome,
          readerSees

vars == <<state, source, snapshot, tempDb, liveDb, receipt,
          backupExists, verified, runs, publications, repeatRun, outcome,
          readerSees>>

Active == {"BackingUp", "Reading", "Writing", "Verifying", "Ready", "Publishing"}
Terminal == {"Idle", "Committed", "Recovered", "NoOp", "Refused"}

Init ==
    /\ state = "Idle"
    /\ source = SourceRows
    /\ snapshot = {}
    /\ tempDb = {}
    /\ liveDb = OldRows
    /\ receipt = FALSE
    /\ backupExists = FALSE
    /\ verified = FALSE
    /\ runs = 0
    /\ publications = 0
    /\ repeatRun = FALSE
    /\ outcome = "None"
    /\ readerSees = OldRows

\* Repeat detection uses the receipt in the published database, not RAM or
\* a marker written separately after rename. Both permitted policies are checked.
Start ==
    /\ state \in Terminal
    /\ runs < MaxRuns
    /\ runs' = runs + 1
    /\ repeatRun' = receipt
    /\ IF receipt
          THEN /\ state' \in {"NoOp", "Refused"}
               /\ outcome' = state'
          ELSE /\ state' = "BackingUp"
               /\ outcome' = "None"
    /\ snapshot' = {}
    /\ tempDb' = {}
    /\ backupExists' = FALSE
    /\ verified' = FALSE
    /\ UNCHANGED <<source, liveDb, receipt, publications, readerSees>>

Backup ==
    /\ state = "BackingUp"
    /\ state' = "Reading"
    /\ backupExists' = TRUE
    /\ UNCHANGED <<source, snapshot, tempDb, liveDb, receipt, verified,
                   runs, publications, repeatRun, outcome, readerSees>>

Read ==
    /\ state = "Reading"
    /\ state' = "Writing"
    /\ snapshot' = source
    /\ UNCHANGED <<source, tempDb, liveDb, receipt, backupExists, verified,
                   runs, publications, repeatRun, outcome, readerSees>>

\* Each row is a separate write/crash boundary. Only staging may be partial.
Write ==
    /\ state = "Writing"
    /\ tempDb # snapshot
    /\ \E row \in snapshot \ tempDb : tempDb' = tempDb \cup {row}
    /\ UNCHANGED <<state, source, snapshot, liveDb, receipt, backupExists,
                   verified, runs, publications, repeatRun, outcome, readerSees>>

FinishWrite ==
    /\ state = "Writing"
    /\ tempDb = snapshot
    /\ state' = "Verifying"
    /\ UNCHANGED <<source, snapshot, tempDb, liveDb, receipt, backupExists,
                   verified, runs, publications, repeatRun, outcome, readerSees>>

Verify ==
    /\ state = "Verifying"
    /\ tempDb = snapshot
    /\ state' = "Ready"
    /\ verified' = TRUE
    /\ UNCHANGED <<source, snapshot, tempDb, liveDb, receipt, backupExists,
                   runs, publications, repeatRun, outcome, readerSees>>

\* Preparation stays private for atomic replacement. In-place publication is
\* representable too: any strict subset (including empty) can become live.
\* Readers and Crash are enabled in Publishing, before Publish completes.
PublishPartial ==
    /\ state = "Ready"
    /\ state' = "Publishing"
    /\ IF InPlacePublish
          THEN liveDb' \in (SUBSET tempDb) \ {tempDb}
          ELSE liveDb' = liveDb
    /\ UNCHANGED <<source, snapshot, tempDb, receipt, backupExists, verified,
                   runs, publications, repeatRun, outcome, readerSees>>

Publish ==
    /\ state = "Publishing"
    /\ verified /\ tempDb = snapshot /\ ~receipt
    /\ state' = "Committed"
    /\ liveDb' = tempDb
    /\ receipt' = TRUE
    /\ publications' = publications + 1
    /\ tempDb' = {}
    /\ outcome' = "Imported"
    /\ UNCHANGED <<source, snapshot, backupExists, verified, runs,
                   repeatRun, readerSees>>

\* Includes read/write/verification/publication failure before the atomic step.
\* Failure and process death leave disposable staging; neither changes liveDb.
Fail ==
    /\ state \in Active
    /\ state' = "Failed"
    /\ UNCHANGED <<source, snapshot, tempDb, liveDb, receipt, backupExists,
                   verified, runs, publications, repeatRun, outcome, readerSees>>

Crash ==
    /\ state \notin {"Crashed", "Failed"}
    /\ state' = "Crashed"
    /\ snapshot' = {}
    /\ verified' = FALSE
    /\ UNCHANGED <<source, tempDb, liveDb, receipt, backupExists, runs,
                   publications, repeatRun, outcome, readerSees>>

\* Restart discards staging, even if it was verified. It never rolls back an
\* already-published destination or loses its durable migration receipt.
Recover ==
    /\ state \in {"Crashed", "Failed"}
    /\ state' = "Recovered"
    /\ snapshot' = {}
    /\ tempDb' = {}
    /\ verified' = FALSE
    /\ backupExists' = FALSE
    /\ UNCHANGED <<source, liveDb, receipt, runs, publications, repeatRun,
                   outcome, readerSees>>

ReadVisible ==
    /\ readerSees' = liveDb
    /\ UNCHANGED <<state, source, snapshot, tempDb, liveDb, receipt,
                   backupExists, verified, runs, publications, repeatRun, outcome>>

Next == Start \/ Backup \/ Read \/ Write \/ FinishWrite \/ Verify
        \/ PublishPartial \/ Publish
        \/ Crash \/ Fail \/ Recover \/ ReadVisible

\* Safety only: repeated crashes/failures need not eventually permit progress.
Spec == Init /\ [][Next]_vars

TypeOK ==
    /\ state \in Active \cup Terminal \cup {"Failed", "Crashed"}
    /\ source \in SUBSET SourceRows
    /\ snapshot \in SUBSET SourceRows
    /\ tempDb \in SUBSET SourceRows
    /\ liveDb \in {OldRows} \cup (SUBSET SourceRows)
    /\ receipt \in BOOLEAN /\ backupExists \in BOOLEAN /\ verified \in BOOLEAN
    /\ runs \in 0..MaxRuns /\ publications \in 0..MaxRuns
    /\ repeatRun \in BOOLEAN
    /\ outcome \in {"None", "Imported", "NoOp", "Refused"}
    /\ readerSees \in {OldRows} \cup (SUBSET SourceRows)

SourceUnchanged == source = SourceRows
ReadSnapshotImmutable == state \in {"Writing", "Verifying", "Ready", "Publishing", "Committed"}
                         => snapshot = SourceRows
AtomicLive == liveDb \in {OldRows, SourceRows}
ReaderNeverPartial == readerSees \in {OldRows, SourceRows}
NoCommitWithoutVerify == state = "Committed" => verified /\ liveDb = snapshot
BackupBeforeWrite == state \in {"Writing", "Verifying", "Ready", "Publishing", "Committed"}
                     => backupExists
\* This equivalence also forbids publishing data without the repeat-run guard.
DurableReceipt == (receipt <=> liveDb = SourceRows)
                 /\ publications = (IF receipt THEN 1 ELSE 0)
CrashSafe == state \in {"Crashed", "Failed"}
             => (liveDb = OldRows /\ ~receipt)
                \/ (liveDb = SourceRows /\ receipt)
CleanResumable == state = "Recovered" => tempDb = {} /\ snapshot = {} /\ ~verified
RepeatRunSafe == /\ publications <= 1
                 /\ (repeatRun => receipt /\ outcome \in {"NoOp", "Refused"})

=============================================================================

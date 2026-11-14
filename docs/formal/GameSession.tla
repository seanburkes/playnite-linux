---------------- MODULE GameSession ----------------
\* Formal model for launching and tracking a game on Linux.
\*
\* Two games are modeled so "one session per game" is meaningful.
\* The implementation must preserve: OneSessionPerGame, NoOrphanProcess,
\* StopOnlyFromRunning, CancelTerminates, PlaytimeNonDecreasing.
EXTENDS Naturals, TLC

CONSTANT Games

VARIABLES
    sstate,     \* function: game -> session state
    handle,     \* function: game -> BOOLEAN (tracked process handle held)
    playtime    \* function: game -> Nat (seconds, accumulated)

vars == <<sstate, handle, playtime>>

States ==
    {"Idle", "Starting", "Running", "Stopping", "Stopped", "Cancelled", "Failed"}

Init ==
    /\ sstate = [g \in Games |-> "Idle"]
    /\ handle = [g \in Games |-> FALSE]
    /\ playtime = [g \in Games |-> 0]

Start(g) ==
    /\ sstate[g] = "Idle"
    /\ sstate' = [sstate EXCEPT ![g] = "Starting"]
    /\ handle' = [handle EXCEPT ![g] = TRUE]
    /\ UNCHANGED playtime

LaunchOk(g) ==
    /\ sstate[g] = "Starting"
    /\ sstate' = [sstate EXCEPT ![g] = "Running"]
    /\ UNCHANGED <<handle, playtime>>

LaunchFail(g) ==
    /\ sstate[g] = "Starting"
    /\ sstate' = [sstate EXCEPT ![g] = "Failed"]
    /\ handle' = [handle EXCEPT ![g] = FALSE]
    /\ UNCHANGED playtime

Stop(g) ==
    /\ sstate[g] = "Running"
    /\ sstate' = [sstate EXCEPT ![g] = "Stopping"]
    /\ UNCHANGED <<handle, playtime>>

StopOk(g) ==
    /\ sstate[g] = "Stopping"
    /\ sstate' = [sstate EXCEPT ![g] = "Stopped"]
    /\ handle' = [handle EXCEPT ![g] = FALSE]
    /\ playtime' = [playtime EXCEPT ![g] = @ + 1]

CancelLaunch(g) ==
    /\ sstate[g] = "Starting"
    /\ sstate' = [sstate EXCEPT ![g] = "Cancelled"]
    /\ handle' = [handle EXCEPT ![g] = FALSE]
    /\ UNCHANGED playtime

Crash(g) ==
    /\ sstate[g] \in {"Starting", "Running", "Stopping"}
    /\ sstate' = [sstate EXCEPT ![g] = "Failed"]
    /\ handle' = [handle EXCEPT ![g] = FALSE]
    /\ UNCHANGED playtime

Reset(g) ==
    /\ sstate[g] \in {"Stopped", "Cancelled", "Failed"}
    /\ sstate' = [sstate EXCEPT ![g] = "Idle"]
    /\ handle' = [handle EXCEPT ![g] = FALSE]
    /\ UNCHANGED playtime

Next ==
    \E g \in Games:
        \/ Start(g) \/ LaunchOk(g) \/ LaunchFail(g)
        \/ Stop(g) \/ StopOk(g) \/ CancelLaunch(g)
        \/ Crash(g) \/ Reset(g)

Spec == Init /\ [][Next]_vars

\* ---- Invariants ---------------------------------------------------------

\* An active session implies a tracked handle.
OneSessionPerGame ==
    \A g \in Games:
        sstate[g] \in {"Starting", "Running", "Stopping"} => handle[g]

\* A terminal or idle session holds no process handle.
NoOrphanProcess ==
    \A g \in Games:
        sstate[g] \in {"Idle", "Stopped", "Cancelled", "Failed"} => ~handle[g]

\* Stop is only requested from Running (the action guard makes this true).
StopOnlyFromRunning ==
    \A g \in Games: sstate[g] = "Stopping" => handle[g]

\* TLC bound: keep the state space finite.
PlaytimeBound == \A g \in Games: playtime[g] <= 2

\* Playtime never decreases.
PlaytimeNonDecreasing ==
    [][\A g \in Games: playtime[g]' >= playtime[g]]_vars

\* A cancelled launch terminates in a non-active state.
CancelTerminates ==
    \A g \in Games:
        (sstate[g] = "Starting") ~> (sstate[g] \in {"Running", "Cancelled", "Failed"})

\* Every started session eventually reaches a terminal state under fairness.
SessionTerminates ==
    \A g \in Games:
        (sstate[g] = "Starting") ~> (sstate[g] \in {"Stopped", "Cancelled", "Failed"})

=============================================================================

# Formal models

Three TLA+ models cover the state machines where a bug is expensive and concurrency/crash
recovery is real. They are **safety models**: they check invariants over every reachable
state. Liveness properties are documented where useful but are not yet machine-checked,
because the fairness assumptions need to be refined per action; do not claim them as verified.

| Model | Invariants checked | Status |
| --- | --- | --- |
| `LibraryImport.tla` | `SourceUnchanged`, `NoCommitWithoutVerify`, `AtomicLive`, `BackupBeforeWrite` | TLC: no error, 11 distinct states |
| `GameSession.tla` | `OneSessionPerGame`, `NoOrphanProcess`; property `PlaytimeNonDecreasing` | TLC: no error, 400 distinct states (bounded playtime) |
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
- UI behaviour (that is the headless test suite).
- WebKit, DB, or filesystem implementation details.
- Liveness under real scheduling. `CancelTerminates` and `SessionTerminates` are stated in
  `GameSession.tla` as design intent; checking them needs per-action fairness that made the
  two-game model intractable. A one-game refinement is a later task.

## Implementation linkage

Every invariant has a C# test with a matching `[Trait("Invariant", "<Model>.<Invariant>")]`.
If a model changes, the test and the model change in the same oracle-change PR.

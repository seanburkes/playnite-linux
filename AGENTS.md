# AGENTS.md — playnite-linux

Cross-agent rules for this repository. Read before any work. The plan is in
`docs/port/`; the frozen oracle is `legacy/`.

## What this repo is

A Linux-first .NET 11 / Avalonia 12 port of Playnite 10.64. `legacy/` is the frozen Windows
oracle. New code is in `src/` (Phase 1 onward). Do not modify the Windows app.

## Hard rules

1. **`legacy/` is read-only.** Never edit, format, or delete anything under it. CI fails if it
   changes.
2. **Oracle paths are protected.** Do not edit `tests/golden/`, `tests/fixtures/`,
   `tests/baseline.json`, `docs/formal/`, or `docs/port/surface-parity.tsv` unless the task
   explicitly says `oracle-change` and a second reviewer approves. `legacy/` and
   `oracle-lock/` are unconditionally immutable; no label overrides them. A porting change may
   not redefine correctness.
3. **Do not weaken tests.** No `[Skip]`, `[Ignore]`, `--filter` exclusions, snapshot
   re-acceptance, or deleting a failing test. Executed test cases must not fall; skips must stay zero.
4. **No stubs.** No `TODO`, placeholder constants, `NotImplementedException` on a reachable
   path, or fake success. If it is not implemented, mark the parity row `deferred`.
5. **No banned APIs outside the platform project:**
   - `System.Windows.*` except `System.Windows.Input.ICommand` (cross-platform)
   - `System.Drawing.*`, `Microsoft.Win32.*`
   - `System.Management`, `ServiceController`, `BinaryFormatter`, `Thread.Abort`
   - `DllImport`/`LibraryImport` outside `Playnite.Platform.Linux`
   - shell-string `Process.Start`; use argument lists.
6. **Dependency direction** (see `docs/port/02-ARCHITECTURE.md`):
   `SDK.Abstractions ← Core ← Desktop`; `SDK` (Avalonia UI) references Abstractions;
   `Platform`/`Platform.Linux` below Core; `Core`/`SDK.Abstractions` never reference Avalonia,
   directly or transitively; `Desktop` never references `Microsoft.Win32`.
7. **One unit, one commit.** Keep diffs reviewable. Update `docs/port/queue.tsv` and
   `docs/port/ledger.tsv` with the unit id and evidence.
8. **No destructive git.** No `stash`, `reset --hard`, `clean -fdx`, `checkout -- .`, or
   force-push. Stop and report a wedged worktree.
9. **Do not run solution-wide builds, packaging, or benchmarks.** The parent owns those; run
   the narrow check named in the task.
10. **Ask, don't guess.** Ambiguity is a decision request. Stop and report the question.

## Working agreements

- Read `docs/port/PORTING.md` before changing code.
- Read the parity row and the v10 reference file before implementing.
- Prefer copying/adapting v10 code over inventing a new shape; keep names 1:1 where practical.
- Every implemented unit gets a test and an oracle reference. "It compiles" is not evidence.
- Two blind reviews for every unit: diff only, "assume this is wrong". Reviewer never edits.
- If the same failure happens twice, fix the generator (analyzer, template, standing rule),
  not the artifact.
- If a workaround needs a paragraph to justify, the code is wrong. Fix the code.

## Commands (once Phase 1 lands)

```sh
dotnet build Playnite.slnx -c Release          # parent only
dotnet test tests/Playnite.Core.Tests          # unit
python3 scripts/port/scan_legacy.py --format md
python3 scripts/port/check_parity.py
scripts/formal/run_tlc.sh
```

## Definition of done

See `docs/port/04-VERIFICATION.md` §11. In short: parity row, behaviour contract, tests,
golden/differential where a boundary is crossed, no banned APIs, cancellation/disposal
covered, two reviews, ledger updated, CI green, counts non-decreasing.

## Where to look

- Decisions: `docs/port/00-DECISIONS.md`
- Inventory: `docs/port/01-INVENTORY.md`
- Architecture: `docs/port/02-ARCHITECTURE.md`
- Execution: `docs/port/03-EXECUTION-PLAN.md`
- Verification: `docs/port/04-VERIFICATION.md`
- Dependencies: `docs/port/05-DEPENDENCIES.md`
- Operations: `docs/port/06-OPERATIONS.md`
- Status: `docs/port/PORT-STATUS.md`

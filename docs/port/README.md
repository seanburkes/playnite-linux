# Port plan index

**Status:** Phase 0 (decisions and inventory). No production code has landed.
**Last updated:** 2026-11-13
**Owner:** Sean
**Oracle:** `legacy/` = Playnite 10.64, frozen.

## The one invariant

> The port is only as good as the oracle that outlives the old runtime.

The Windows app is the oracle. It stays buildable, runnable, and unmodified in `legacy/`.
Every new behaviour is compared against it at a **process or data boundary**, because `net462`
and `net11.0` assemblies cannot coexist in one process.

## Read this in order

| Doc | Question it answers |
| --- | --- |
| [`00-DECISIONS.md`](00-DECISIONS.md) | What did we decide, and what did we reject? |
| [`01-INVENTORY.md`](01-INVENTORY.md) | What is actually in the codebase, and what is coupled to Windows? |
| [`02-ARCHITECTURE.md`](02-ARCHITECTURE.md) | What are we building, in what projects, with which boundaries? |
| [`03-EXECUTION-PLAN.md`](03-EXECUTION-PLAN.md) | In what order, with what gates, and how long? |
| [`04-VERIFICATION.md`](04-VERIFICATION.md) | How do we know a unit is correct, and how is the oracle protected? |
| [`05-DEPENDENCIES.md`](05-DEPENDENCIES.md) | What replaces every v10 package, and what are the risks? |
| [`06-OPERATIONS.md`](06-OPERATIONS.md) | How do agents, worktrees, review, and firstmate run this? |
| [`07-FIRSTMATE.md`](07-FIRSTMATE.md) | What was actually configured, and how do I launch it? |
| [`08-STEAM-SPEC.md`](08-STEAM-SPEC.md) | What exactly is the greenfield Steam subsystem, and what is its oracle? |
| [`PORTING.md`](PORTING.md) | What are the coding rules for every ported unit? |
| [`RISKS.md`](RISKS.md) | What can kill this, and what is the mitigation? |
| [`PORT-STATUS.md`](PORT-STATUS.md) | Where are we right now? |
| [`surface-parity.tsv`](surface-parity.tsv) | The omission ledger: one row per public surface. |

Formal models live in [`../formal/`](../formal/). Deterministic inventory tooling lives in
[`../../scripts/port/`](../../scripts/port/).

## Strategy in one page

1. **Freeze the oracle.** Build and run v10.64 on a Windows CI runner. Record library exports,
   settings round-trips, manifest parses, HTTP traffic and log lines; index official/public UI
   references (no captured images). Check the goldens in.
2. **Scaffold the new stack.** `net11.0`, Avalonia 12, SQLite, central package management,
   analyzers that ban WPF/Win32 outside the platform project, Linux CI, Windows oracle CI.
3. **Port leaves first.** Pure utilities and models, then the data layer and the v10 importer,
   then Steam discovery, then platform services, then the Avalonia shell.
4. **Cut over per surface, not per file.** A feature is "ported" only when its public surface
   row has a passing acceptance test, a golden, and a ledger entry.
5. **Ratchet.** Executed test cases never fall; skip count stays zero; oracle paths are
   protected. Shim/legacy-reference count only falls.
6. **Release continuously.** Nightly AppImage and Flatpak from the first runnable shell; a
   pre-release channel before the first stable.

## Why this is a port and not a rewrite

The **library data, settings semantics, Steam discovery, launch lifecycle, localisation keys,
plugin contracts, and user-visible behaviour** are preserved. The **UI toolkit and platform
integration** are replaced, because WPF/Win32 cannot run on Linux. That is a platform
adaptation, not a licence to redesign.

If a behaviour is wrong in v10, port the wrongness and file a follow-up. If a v10 feature is
out of v1 scope, write it in the parity ledger as `out-of-scope-v1`, not as a silent omission.

## Current blockers / decisions still open

- GitHub repository creation and first push (public `seanburkes/playnite-linux`) await the
  plan review.
- The Windows oracle CI needs a runner with .NET Framework MSBuild + the Playnite.Deps feed.
- Herdr is installed but its server is not running in the current agent session; firstmate
  will start on the tmux backend unless launched inside Herdr.
- Steam process-exit detection needs a spike; there is no stable public API equivalent to
  the Windows process tree.
- The exact v10 LiteDB 4 read path on `net11.0` needs a spike (isolated importer or Windows
  exporter fallback).

## Definition of done for the plan

- [x] Decisions recorded as ADRs.
- [x] Inventory reproducible from `legacy/`.
- [x] Target architecture and dependency rules written.
- [x] Phase gates with exit criteria.
- [x] Oracle, test layers, formal models, and CI gates specified.
- [x] Package replacement matrix.
- [x] Agent/firstmate operating model.
- [ ] Plan reviewed and Phase 1 authorised.

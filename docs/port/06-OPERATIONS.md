# Operating model

## 1. Roles

| Role | Who | Owns |
| --- | --- | --- |
| Captain | Sean | Scope, ADRs, gate evidence, merge approval |
| First mate | Firstmate primary (OpenCode/Claude/Codex) | Intake, dispatch, supervision, escalation |
| Implementer | Crewmate per worktree | One unit: code + tests + ledger |
| Reviewer A/B | Two independent agents, diff only | "Assume this is wrong; enumerate how" |
| Fixer | A separate crewmate | Apply review findings |
| Oracle owner | Captain + a separate agent | Goldens, fixtures, formal models; never the implementer |

Roles never overlap. The implementer never reviews its own unit. The reviewer never edits the
implementation. The oracle owner is not the person changing the implementation.

## 2. Firstmate setup

Actual configured state and launch commands: [`07-FIRSTMATE.md`](07-FIRSTMATE.md).

Firstmate is cloned at `~/src/firstmate`. Its private home is the repo root by default. Create
a project clone and registry entry **after** the first commit exists:

```sh
# Canonical repo stays at ~/src/playnite-linux.
# Tell firstmate to use ~/src as the project root for this home.
export FM_HOME=/home/sean/src/firstmate
export FM_PROJECTS_OVERRIDE=/home/sean/src

# Registry entry (data/projects.md):
# - playnite-linux [local-only] - Linux .NET 11/Avalonia Playnite port; legacy/ is the
#   frozen v10.64 oracle (added 2026-11-13)
```

Why `local-only` first: the GitHub repo is not created until the plan is reviewed. Once the
public repo exists and `origin` is set, change the registry to `direct-PR` (or
`no-mistakes-prod-only` if the no-mistakes pipeline is installed). Do not enable `+yolo`;
merges stay with the captain.

Firstmate refuses to spawn into an unborn `HEAD`; make the first commit before dispatching.

### Runtime backend

- **Herdr:** preferred when available. Requires a running Herdr server and protocol ≥ 14.
  Set `config/backend` to `herdr` or rely on auto-detection when the primary runs under
  `HERDR_ENV=1`.
- **tmux:** reference default. Use it if Herdr is not running.
- Current session fact: `HERDR_ENV` is unset and no Herdr server socket is running, so a
  firstmate launched now will use its configured default (tmux unless changed).

```sh
# Only if the user wants Herdr and has started it:
printf 'herdr\n' > /home/sean/src/firstmate/config/backend
```

### Worker harness

Firstmate supports OpenCode as a worker. Keep the default unless a specific model is needed
for a reviewer. The reviewer should use a different model from the implementer where possible;
"the biggest change to a non-deterministic system is changing the model."

## 3. Worktrees and branches

- One worktree per unit, created by firstmate/treehouse, never the primary checkout.
- Branch: `port/<phase>-<unit>` (e.g. `port/w1-vdf-parser`).
- Commit subject: `port(<unit-id>): <what changed> [oracle:<id>]`.
- One logical unit per commit. Commit a specific file at a time; never `git add -A` across
  unrelated work.
- No destructive git: no `stash`, `reset --hard`, `clean -fdx`, `checkout -- .`, force-push.
  If a worktree is wedged, stop and report; do not "fix" it with destructive commands.
- Rebase on `main` at the start of a unit, not in the middle of a review round.
- Delete the branch/worktree after merge.

## 4. Queue and ledger

The durable contract is a TSV, not a conversation. The two aggregates are generated; workers
edit unit shards, never the aggregates themselves.

- `docs/port/units/<unit-id>.queue.tsv` holds exactly one queue row for that unit.
- `docs/port/units/<unit-id>.ledger.tsv` holds that unit's append-only ledger rows (optional
  when there are none).
- `docs/port/units/removed.tsv` holds one `unit`/`reason` row per deliberately retired unit.
- `docs/port/queue.tsv` and `docs/port/ledger.tsv` are generated from those shards. Commit
  their regenerated output, but do not hand-edit them.

A unit id is lowercase letters, digits, and single hyphens. The units directory is enumerated
strictly: `README.md`, `removed.tsv`, and correctly named shards are the only accepted entries.
A subdirectory, wrong-case suffix, backup suffix, trailing space, or other unexpected file is an
error rather than a silently ignored shard. Ledger rows are append-only: append at the end,
never edit, delete, duplicate, or reorder existing rows.

The shard contract is in [`units/README.md`](units/README.md). Queue columns:
```
id	wave	area	unit	depends	status	owner	oracle	test	notes
p3-003	W3	leaves	JSON/YAML/TOML/INI helpers	p2-001	todo	core	golden:serialization	test:serialization	PILOT
```

Ledger columns:
```
timestamp	unit	from	to	actor	evidence
```

After changing your shard, regenerate and verify:
```sh
python3 scripts/port/build_port_index.py build
python3 scripts/port/build_port_index.py check --against origin/main
```

`check` regenerates both aggregates in memory, byte-compares them with the committed files,
and prints any differing lines. `--against <ref>` also fails when a queue shard, queue row,
ledger shard, or ledger row present at `<ref>` is missing or altered at head. CI passes the
pull request's base commit (or the previous commit on `main`) and also rejects a pull request
that changes an aggregate without changing a contract shard or `removed.tsv`.

To retire a unit, delete its queue and ledger shards and add a new `removed.tsv` row with a
reason in the same change. A removal without a new record fails, and unit ids are never
renamed.

Wave numbers in the queue are offset from the phase numbers: W0 = planning, W1 = oracle,
W2 = scaffolding, W3 = leaves, W4 = data, W5 = Steam/platform, W6 = core/SDK, W7 = UI,
W8 = packaging, W9 = release. Do not use the phase number as the wave number.

Rules:
- One queue row per unit. Update the shard's status before/after work, then regenerate.
- If `check` reports drift, fix the shard and rerun `build`; never patch the aggregate.
- `attempts` increments on a failed review or CI run. At 2, stop and escalate; do not thrash.
- A unit is `done` only when its Definition of Done is checked and CI is green.
- Progress survives a crash, rate limit, or context reset because the queue is on disk.

## 5. Build and test serialization

Builds are shared-state and expensive. On one machine:
- **Workers do not run full builds or packaging.** They run the named fast check for their
  unit (`dotnet test <project> --filter <unit>`).
- The parent/first mate owns `dotnet build`, full `dotnet test`, packaging, and benchmark
  runs. Use a simple lease:

```sh
# build lease
mkdir -p state
flock state/build.lock -c 'dotnet build Playnite.slnx -c Release'
```

- At most one packaging job at a time; AppImage/Flatpak are CPU and disk heavy.
- If a worker needs a compile to iterate, it may run `dotnet build <its project>`; it must not
  run the solution, packaging, or benchmarks concurrently with another build.
- Record wall-clock time for build/test in the ledger if it exceeds 2 minutes; the inner loop
  is a first-class metric.

## 6. Review protocol

For every unit:

1. **Implementer** writes code + tests + ledger row in its worktree.
2. **Reviewer A** gets only the diff, the unit's parity row, and the relevant oracle artifacts.
   Prompt: "Assume this code is wrong. Compare it to the v10 reference and the contract.
   Enumerate behavioural differences, omissions, and lifetime/ordering bugs, file:line."
3. **Reviewer B** gets the same, with a different model. It must independently enumerate; do
   not show A's findings.
4. **Fixer** applies accepted findings. It does not add scope.
5. Re-run both reviewers on the updated diff until clean or until a finding is explicitly
   escalated to the captain.
6. Parent runs CI. A red CI is not a review finding; it is a gate.
7. Parent merges (captain approval for v1) and tears down the worktree.

Reviewer prompt skeleton:

```
You are a read-only reviewer. You did not write this code. Assume it is wrong.
Inputs: <diff path>, <parity row>, <oracle artifact paths>, <PORTING.md>.
Do not edit files. Do not start agents.
Report findings as file:line + one sentence of why, ordered by risk.
Check specifically:
- behaviour vs v10 reference (not style)
- omission vs the parity row
- null/empty/zero/culture/path/ordering
- async cancellation, disposal, event ordering
- banned APIs and dependency direction
- tests that assert the implementation rather than the behaviour
Say plainly if you found nothing worth changing.
```

## 7. Check-ins and escalation

Cadence:
- After each pilot (Phase 1/3/7), a written review with evidence.
- At each phase gate, a gate artifact and a captain decision.
- Daily during active execution: a short status digest (done / in flight / blocked / next).
- Immediately on: an ADR-level decision, an oracle change, a security finding, a repeated
  failure (2 attempts), or a build/infra blocker.

Escalation format:
```
Decision needed: <one sentence>
Options: A / B / C with consequences
Recommendation: <one>
Evidence: <links/paths>
Deadline/impact: <what stalls>
```

## 8. Agent instructions that are standing rules

Put these in the project `AGENTS.md` (not repeated per task):
- `legacy/` is read-only.
- Do not edit tests, fixtures, goldens, `docs/formal/`, or `tests/baseline.json`.
- Do not add a package without an ADR/decision row.
- Do not use `System.Windows`, `Microsoft.Win32`, `DllImport`, or `System.Drawing` outside
  `Playnite.Platform.Linux`.
- Do not use `git stash/reset --hard/clean`.
- Do not run solution-wide builds or packaging; ask the parent.
- Do not guess. Stop and report a question.
- A stub that makes a test pass is a failure, not progress. No `TODO`, placeholder constants,
  or paragraph-long justification comments.
- If a workaround needs a paragraph to justify, the code is wrong; fix the code.

## 9. Herdr usage

If Herdr is running and this session is inside it:

```sh
test "${HERDR_ENV:-}" = 1 || { echo "not inside Herdr"; exit 1; }
herdr agent list
herdr pane split --current --direction right --cwd "$PWD" --no-focus
herdr agent start reviewer --kind codex --pane <pane-id>
herdr agent prompt reviewer "Review the diff at <path>. Assume it is wrong." --wait --timeout 180000
herdr agent read reviewer --source recent-unwrapped --lines 120
```

Herdr is a visibility/control layer, not the source of truth. The queue and ledger remain the
contract. If Herdr is unavailable, firstmate's tmux backend is equivalent for execution; do
not block the port on it.

## 10. Handoff template

When handing a discrete unit to another agent:

```
Unit: <id> — <one-line goal>
Repo/worktree: <path> (branch <branch>)
Read first: docs/port/PORTING.md, docs/port/surface-parity.tsv row <id>, <v10 files>
Scope: <files you may touch; files you must not touch>
Oracle: <golden/fixture path>; <command to run>
Definition of done: <checklist>
Do not: edit legacy/, tests/golden/, fixtures, formal models; add packages; run full builds
Report: what changed, commands run + results, what you could not do, open questions
```

## 11. Fix the generator, not the artifact

When the same failure appears twice:
- add a standing instruction to `AGENTS.md`,
- add an analyzer/CI check,
- add a template or queue-generator rule,
- add an eval or a reviewer checklist item.

Never hand-fix unit #400. The port's throughput comes from the queue and the checks, not from
individual heroics.

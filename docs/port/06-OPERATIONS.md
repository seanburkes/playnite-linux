# Operating model

## 1. Roles

| Role | Who | Owns |
| --- | --- | --- |
| Captain (owner) | Sean | Scope, ADRs, gate evidence, merge decision |
| First mate | Firstmate primary (OpenCode/Claude/Codex) | Intake, dispatch, supervision, escalation |
| Implementer | Crewmate per worktree | One unit: code + tests + ledger |
| Reviewer A/B | Two adversarial agents, diff only; a different model from the implementer is preferred, not required | "Assume this is wrong; enumerate how" |
| Fixer | A separate crewmate | Apply review findings |
| Oracle owner | Captain + a separate agent, ideally a different model; never the implementer | Goldens, fixtures, formal models |

Roles never overlap. The implementer never reviews its own unit. The reviewer never edits the
implementation. The oracle owner is the Captain working with a separate agent that is not the
implementer; that is agent role separation, not independent human approval. Review is
adversarial agent assistance: the owner makes the merge decision, and no agent review is
independent human approval (ADR-024).

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

The durable contract is a TSV, not a conversation. The two aggregates are generated with a
single writer on `main`; workers edit unit shards and never the aggregates themselves.

- `docs/port/units/<unit-id>.queue.tsv` holds exactly one queue row for that unit.
- `docs/port/units/<unit-id>.ledger.tsv` holds that unit's append-only ledger rows (optional
  when there are none).
- `docs/port/units/removed.tsv` holds one `unit`/`reason`/`approved-by` row per deliberately
  retired unit.
- `docs/port/units/ownership.paths` is the bot-generated map of the product source, shared
  tests, non-prefixed fixtures and tools each unit introduced or changed. A path may list
  several owners; `*` marks explicitly shared material and any declared unit may change it. The
  unit guard reads the map from the pull request base, rejects a change to a path owned only by
  other units, and fails closed with `ownership map unavailable` when the map is missing, empty
  or fails validation. In that state any product/test/tool change fails, even for a declared
  unit, because foreign ownership cannot be checked. New and unowned product paths still require
  a unit declaration and a ledger fragment, and a queue or fragment change never infers a unit
  from the queue. The one bootstrap change that adds the first map may do so only when it touches
  no product/test/tool path, and the map must match a deterministic regeneration of head.
- `docs/port/queue.tsv` and `docs/port/ledger.tsv` are generated from those shards by the
  parent-owned `port-index` workflow after merge. A pull request must not include either file
  in its diff, even with freshly generated content; CI rejects that change.

A unit id is lowercase letters, digits, and single hyphens. The units directory is enumerated
strictly: `README.md`, `removed.tsv`, `ownership.paths`, and correctly named shards are the only
accepted entries.
A subdirectory, wrong-case suffix, backup suffix, trailing space, or other unexpected file is an
error rather than a silently ignored shard. Ledger history is append-only in content: never
edit, delete, or duplicate an existing row. Order is not part of the contract; a new row may
carry any instant, including one older than rows already recorded. The aggregates are ordered
by instant then unit (queue: wave then unit), and that ordering is generated output owned by
the parent job. Contributors never hand-order or hand-edit an aggregate.

The shard contract is in [`units/README.md`](units/README.md). Queue columns:
```
id	wave	area	unit	depends	status	owner	oracle	test	notes
p3-003	W3	leaves	JSON/YAML/TOML/INI helpers	p2-001	todo	core	golden:serialization	test:serialization	PILOT
```

Ledger columns:
```
timestamp	unit	from	to	actor	evidence
```

After changing your shard, validate the shard tree:
```sh
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
```

`check --shards-only` validates and regenerates both aggregates in memory without requiring the
committed files to match. `--against <ref>` guarantees content against the base as a multiset:
it fails when a queue shard or queue aggregate row is missing, a ledger shard is missing, a
ledger row is missing, altered, or duplicated at head, or when an existing retirement record
was edited. It does not check order, so a new row with an earlier instant passes. The ref must
be a strict ancestor of HEAD; HEAD, a descendant, and an unrelated commit are refused. CI
passes the merge base of the recorded pull-request base and `HEAD` (the fork point) with
`--shards-only` and `--forbid-aggregate-changes`, so a PR branched before an unrelated
aggregate regeneration on `main` is not charged for main's own commit; if the merge base cannot
be computed, the step fails closed. An aggregate file whose content exactly matches the fork
point or the recorded event base is not an aggregate edit. Any other content, and every add,
delete, or rename of an aggregate, still fails. The recorded-base content check is therefore
not redundant; it is the second allowed base-side snapshot, alongside the fork point. Because
the aggregate check runs the base-materialised `build_port_index.py`, a new tool capability is
live only once the merge carrying it has landed. That transition is complete: the base tool
now understands `RECORDED_BASE`. Before it landed, a tree carrying recorded-base aggregates
different from the fork point was charged by the live step; that historical pre-merge behaviour
is retained in the ledger and is no longer asserted by the suite. A real aggregate edit is
still charged. Pushes to `main` pass the immediate previous main
commit. Inventory, parity, generator-test, TLC, and frozen-step checks
either compare the working tree, run a model, or use the shared merge-base gate; none compares
a recorded base to `HEAD` with two-dot semantics.

The post-merge `port-index` workflow runs only on a push to `main`. It first validates the
shard tree and history against the previous `main` commit; an invalid shard fails the run with
no commit. It then runs `build`, verifies the result with `check`, and commits only
`docs/port/queue.tsv` and `docs/port/ledger.tsv` with a bot-style message. Because the history
check guarantees content rather than order, a row that lands with an earlier instant is
accepted and `build` sorts it into its generated position, so concurrent merges cannot wedge
the writer on ordering. It has no
manual-dispatch path, never accepts a branch/ref input, never force-pushes, and retries once
against the new `main` if a concurrent update rejects the first push. It never writes a shard.
A PR already in flight that regenerated the aggregates should drop those two file changes and
keep only its shards.

The migration PR that first adds `docs/port/units` is the only legitimate base without shards.
It must pass `--allow-pre-migration-base`, which prints an explicit skip notice; ordinary runs
refuse a base with no shards instead of silently skipping history. The CI job passes that
acknowledgement only when the base commit has no `docs/port/units` tree, so the skip is printed
there too.

To retire a unit, delete its queue and ledger shards and add a new `removed.tsv` row with a
reason and a non-empty `approved-by` in the same change. A removal without a new record fails,
unit ids are never renamed, and an existing retirement record's reason and approver are
immutable. CI checks the record's shape and history, not who approved it: retirement is a
captain-authorized decision enforced by human review of that record plus the deleted shards.

Wave numbers in the queue are offset from the phase numbers: W0 = planning, W1 = oracle,
W2 = scaffolding, W3 = leaves, W4 = data, W5 = Steam/platform, W6 = core/SDK, W7 = UI,
W8 = packaging, W9 = release. Do not use the phase number as the wave number.

Rules:
- One queue row per unit. Update the shard's status before/after work; the parent workflow
  regenerates the aggregates after merge.
- If the shard-only check reports drift, fix the shard; never patch or commit the aggregate.
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

### CI matrix and required checks

The Linux build and the existing plan/oracle workflows form one CI matrix. The two Linux
workflows both run on **every pull request** and every push to `main`, without path filters.
The `merge_group` triggers in `linux-ci.yml`, `packaging.yml` and `plan-guards.yml` are
retained but dormant: GitHub does not offer a merge queue for this user-owned repository (see
[Merge queue and up-to-date policy](#merge-queue-and-up-to-date-policy) below). Plan checks
remain in `plan-guards.yml`; they are not duplicated in the build workflow.

| Workflow / check | Runner | Trigger and guarantee |
| --- | --- | --- |
| `linux-ci` / `linux-build-and-test` | `ubuntu-24.04`, Python 3.12 | Test the result policy, restore `Playnite.slnx` in locked mode, build Release, and run every project under `tests/` on PR and `main` push events. Restore/build warnings and xUnit skipped/warned tests fail; the bounded empty-scaffold policy below applies before the new-suite baseline is recorded, then the committed baseline is a ratchet. The dormant `merge_group` trigger would run the same job if a queue exists. |
| `plan-guards` / `inventory-and-parity` | `ubuntu-latest` | Reproduce inventory, validate parity, run port-index generator tests, validate shard history, and reject aggregate edits on PRs. Also enforce immutable `legacy/`/`oracle-lock/` and the protected-path `oracle-change` gate. The dormant `merge_group` trigger is retained for a future organization-owned repository. |
| `plan-guards` / `formal` | `ubuntu-latest`, Temurin 21 | Run `scripts/formal/run_tlc.sh` for the existing formal models. |
| `legacy-oracle` / `windows-oracle` | `windows-latest`, x86 Release | Existing oracle-path PR filters, `oracle-*` tags, or manual dispatch. Verify pinned Playnite.Deps, build frozen v10, byte-compare manifest goldens with a negative probe, run NUnit via VSTest, and retain TRX/counts. |
| `settings-library-oracle` / `build and generate scratch artifacts`, `validate when approved corpus exists` | `windows-latest` | Existing corpus/tool-path PR filters or manual dispatch; generate scratch output and validate the approved settings/library corpus. |
| `windows-fixture-validation` / `fixture` | `windows-latest` | Existing fixture/tool-path filters or manual dispatch; validate regeneration, mutations, source preservation, and the committed fixture. |
| `port-index` / `regenerate` | `ubuntu-latest` | Push to `main` only; validate history, regenerate aggregates, and commit them on `main` with no force-push. |

Require exactly these five contexts on every PR: `inventory-and-parity`, `formal`,
`linux-build-and-test`, `AppImage packaging smoke` and `Flatpak packaging smoke`, with
strict/up-to-date intentionally **off** and no required reviews. The `legacy-oracle`,
`settings-library-oracle` and `windows-fixture-validation` jobs are path-filtered additional
checks: they run when their paths change, so they are not required contexts (an untriggered
workflow would stay pending). The aggregate writer is a post-merge check, not a PR check.
These are the required-check names for repository configuration; this document does not change
branch protection.

#### Merge queue and up-to-date policy

GitHub merge queue is unavailable for this repository: the owner is a user account, not an
organization, and ruleset 27326579 has no `merge_queue` rule. The `merge_group` triggers are
retained so the workflows are ready if the repository ever moves to an organization, but they
cannot fire here. There is no queue entry, and no queue-related check is part of the merge
contract.

The owner's decision is to leave strict/up-to-date off
(`strict_required_status_checks_policy=false`) rather than require a branch update that a
queue cannot automate. The five required contexts and the deploy-key bypass are unchanged, and
force pushes and deletion remain blocked.

Residual risk: a pull request whose head was tested against an older `main` can merge even when
`main` has advanced. The post-merge `linux-ci` run on `main` is the backstop; it catches a
regression only after merge, not before. Reviewers should re-run or locally check a change that
depends on newer `main`, and the owner may still ask for an update. The post-merge `port-index`
aggregate commit no longer makes a pull request unmergeable: it advances `main`, but it is not
a required check and strict up-to-date is off. An operator who sees `BEHIND` or an
out-of-date branch should treat it as informational; it does not block merge. The bot can still
create main churn, which is a throughput and cost issue, not a mergeability gate.

`linux-ci.yml` installs the exact SDK version from `global.json` (currently `11.0.100`) with
`actions/setup-dotnet`'s `global-json-file` input and checks `dotnet --version` for equality.
The equality check rejects unintended SDK patch roll-forward from a runner's preinstalled
SDKs. NuGet/test-runner versions remain pinned by central versions and package lock files.
The Linux test command explicitly selects Microsoft.Testing.Platform through .NET 11's
`DOTNET_TEST_RUNNER` setting, runs without case filters, and uses the pinned xUnit runner's
`--fail-skips on --fail-warns on` options. It uploads per-project TRX and JUnit files in
`linux-test-results`, together with `test-counts.json` containing every project's executed,
passed, failed and skipped counts, MTP exit code and classified outcome. Counts also appear in
the job summary. The preceding solution restore and Release build produce the test
binaries and project assets used by `--no-build --no-restore`; a discovered project without
those outputs fails rather than being skipped. It discovers project files rather than gating
on test source or case count: `Playnite.Architecture.Tests` runs whenever its `.csproj` exists,
including today's scaffold and the tests added by p2-002. An absent architecture project emits
a notice; a present project's build or test failure fails the job. The core project is
mandatory. Empty scaffold assemblies are not evidence of tested application behavior.

The pre-baseline bootstrap policy is deliberately self-closing. The workflow runs **every**
discovered project, records each exit code, then calls
`scripts/ci/check_linux_test_results.py`. It checks out full history, resolves one comparison
base in the workflow from the event fields, and materialises that commit's
`tests/baseline.json` plus its test-project list (`list_test_projects.py`, not every
`tests/**/*.csproj`) for that call. A pull-request event with no usable base SHA fails closed;
the dormant `merge_group` branch keeps the same rule, and only a push before the branch's first
commit may report an absent baseline.
When the base commit exists, `tests/baseline.json` must exist and be readable; a missing or
empty base baseline is a hard failure, not an absent state. A supplied-but-missing or
unreadable previous baseline or project list is a hard failure, not a silent return to
bootstrap. Only MTP exit **8** accompanied
by a valid JUnit report declaring and containing **zero cases** is non-failing while no
new-suite baseline is recorded (`tests/baseline.json` has `new: null`). Each such project
gets a prominent **NO TESTS EXIST YET** notice in the log and job summary, plus a recorded
zero count; the result is `bootstrap-empty`, never a tested behavior claim. Missing, malformed
or inconsistent reports fail. Every other nonzero exit, exit 8 with nonempty results, any
failed case and any skipped case fail, regardless of baseline state. All projects still run
so an earlier failure cannot hide another project's counts.

Once `tests/baseline.json` contains a non-null `new` object, the bootstrap outcome closes
automatically: **zero executed cases always fail**, even for a newly added project or a
baseline entry containing zero. A project's executed count must also be at least its recorded
baseline, and every baselined project must appear in the run. An invalid recorded baseline
fails instead of reopening bootstrap. The `new` object is keyed directly by project name;
each project's object must contain a nonnegative integer `executed` count and may also record
passed/failed/skipped counts, coverage and capture provenance. Python policy tests in
`scripts/ci/` exercise the bootstrap-to-baseline transition and these failure paths in CI.

The enforcement scripts are self-protecting. Before the protected-path gate or the results
checker runs, the workflow selects a materialiser from the comparison base and uses it to
materialise `scripts/ci`; the protected-path gate, results checker, and base project lister
then run from the base copy. The aggregate and shard-history steps materialise
`scripts/port/build_port_index.py` from the same base and run that copy, so a PR cannot weaken
`changed_aggregates` or `validate_history` in its own checkout. If the base commit contains a
script but it cannot be materialised, the job fails instead of falling back. Only while the
base genuinely lacks a script (including the materialiser itself) does the head copy run, with
a named `BOOTSTRAP` warning; once `main` contains the script, every later run uses the base
copy. This applies to the protected gate/checker/capture/lister policy scripts. The base SHA
and the recorded event base come directly from the event fields in the workflow, not from a
PR-controlled helper; `policy_base.sha` is their fork point and `policy_base.recorded` is the
event base. Those policy scripts consume `policy_base.sha` (the fork point). Generated-path
ownership does not use the event base: the `Select the port guard tool` step fetches the
current `main` tip and selects the generator from it. If the PR head contains current `main`,
the head tool runs (it carries main's rules plus any reviewed PR change); if the PR is stale
and does not contain current `main`, the current-main tool is materialised inside the checkout
and runs instead, so an old PR cannot keep an old tool that predates the baseline and status
checks. A head tool difference from current `main` on a contained PR requires the
`oracle-change` label. The baseline/status comparison uses the fetched current-main tip, never
`policy_base.recorded`.
This keeps a pull request from weakening the gate,
the results check, the aggregate tool, or the base project list in its own checkout. The protected-path diff is
taken from the merge base to `HEAD`, so a PR
branched before a protected-path change landed on `main` is not charged for that main-side
change; failure to compute the merge base fails closed. The frozen `legacy/`/`oracle-lock/`
step uses the same merge-base diff through the shared gate and has no label override. The
dormant `merge_group` protected-path step
re-reads the PR's `oracle-change` label through the API because merge-group events do not
carry PR labels. It is unreachable while no queue exists; if a queue is ever enabled, it fails
closed on a batched group, which does not identify exactly one PR, and reports that single-PR
limitation explicitly.

The committed baseline is a ratchet and is bot-owned. A pull request does not edit
`tests/baseline.json`; the post-merge `port-index` workflow reads the completed merged-main
`linux-ci` run's downloaded JUnit and MTP exit artifacts, validates every project and exit
code, and refuses to write if any recorded floor would fall. The floor is the maximum over
every structured capture in the document (`new`, each top-level capture with a `counts`
object, and every `main_captures` entry) and over the previous `main` baseline, not merely the
current `new` block. It refuses to delete or rewrite a `main_captures` identity and refuses a
`removed_projects` entry that is not backed by real capture evidence or whose project still
exists. Those identity and floor checks run even when the capture itself is skipped, so a bot
commit cannot launder a deleted capture past the writer.

A run adds evidence only when it can change the floor. A merged-main run whose head commit
changed nothing but the five generated files (`queue.tsv`, `ledger.tsv`, `PORT-STATUS.md`,
`ownership.paths`, `baseline.json`), decided from the commit's own tree rather than its
message, is skipped: it measured the same source tree as its parent and cannot raise a floor,
so recording it would let the bot capture its own commit in a loop. A run whose counts equal
the latest capture is deduplicated by run id and commit: the writer keeps a single mutable
`latest_run` provenance field for it and appends nothing, so repeated runs over an unchanged
tree are a no-op. Only a real count increase appends a `main_captures` entry, and the list is
bounded to the newest captures; because `new` always holds the latest validated observation,
which is at or above every historical maximum, dropping the oldest capture objects cannot
lower a floor.
A change may add the first non-null `new` object only when
its counts meet or exceed the observed run and it names every observed project, so a first
baseline cannot record zero or omit a project and leave a false floor. It may not delete
`tests/baseline.json`, reset `new` to null, lower a retained
project's `executed` or `passed` count, or silently delete or rename a project that was present
at the base commit. Retiring a project requires a reviewed `removed_projects` entry in the
protected baseline, carrying a non-empty `reason` and `approved_by`; a project that is still
discovered and run cannot be retired. Prior removal records are immutable, and `replaced_by`
plus `replacement_executed` must be a string and a nonnegative integer. A recorded rename must
carry the retired project's executed floor to the replacement. Test discovery for the run uses
the same test-project filter as the base list. On a pull request, `changed_aggregates` compares
the baseline and generated status with the current `main` tip fetched at guard time, so a
stale branch that restores an older floor, deletes a capture, or carries an older generated
table fails even when the three-dot diff cannot see the restore. A human note edit in the
baseline is allowed only in a reviewed `oracle-change` PR and only when every floor and capture
identity is unchanged; floors and captures remain immutable. `tests/baseline.json`, the entire
`.github/workflows/` directory (including new or renamed workflows), `.github/CODEOWNERS`,
`packaging/`, and `scripts/ci/`, `scripts/port/`, `scripts/formal/` are in the protected-path
set, so changing the policy surface requires the declared `oracle-change` route through the
protected-path gate. Existing policy files remain CODEOWNED. The protected-path step itself
is asserted to have no `continue-on-error` and to exit nonzero on a violation. An absent base
commit (a brand-new branch's first push) keeps the first-baseline addition path open.

The shard ownership rules are enforced on both sides of the merge: `plan-guards` runs the
generator selected from the current `main` tip with `--forbid-aggregate-changes` (which includes
ownership) against the fork point, and the post-merge writer runs
`--forbid-shard-ownership` against the previous `main` commit before it writes. The latter rejects an edited legacy ledger, a second fragment, a
non-verbatim `0000-history.tsv` migration, or a retirement delete that `removed.tsv` does not
record. `PORT-STATUS.md` keeps only one hand-written narrative window; it must begin with
`## Hand-written narrative` and may not contain a generated banner or generated heading.
The negative proofs for these bypasses live in `scripts/port/test_build_port_index.py` and
`scripts/ci/test_aggregate_ownership.py`, which `plan-guards` and `linux-ci` run; both paths are
in the protected `scripts/` set, so an unlabelled PR cannot weaken them and an `oracle-change`
PR is the owner-authorised route.

The Windows oracle remains independent, with its existing triggers and VSTest filters:
EX-1 `FileSystemTests.CanWriteToFolderTest`, EX-2
`SafeFileEnumeratorTests.OverUnsafeWorksTest`, and EX-3
`ProcessStarterTests.ShellExecuteTest`. Its accepted baseline remains **281 executed / 281
passed / 0 skipped / 3 excluded**, as recorded in `tests/baseline.json` and
`tests/legacy-exclusions.md`. The Linux workflow neither runs nor replaces the Windows oracle.
No shared reusable workflow is needed: existing plan guards and the new Linux build have
unfiltered triggers, and the Windows jobs retain their narrower oracle triggers.

### New-suite baseline at Phase 2 exit

`tests/baseline.json` currently has `new: null`; Core.Tests is still a marker-only scaffold,
while p2-002 supplies real architecture cases. This historical bootstrap is superseded: the
baseline is now bot-owned by the post-merge `port-index` workflow, and PRs never edit it.
Do not replace `new: null` with a fabricated
zero-test baseline. At Phase 2 exit, after every remaining empty project has real tests and the
Linux CI run is green, the oracle owner records the new suite **per project under
`tests/baseline.json`'s `new` object**, in a separate `oracle-change` PR through the
protected-path gate. Preserve the complete `legacy` object. Record executed cases,
passed/failed cases, zero skips, the CI run URL/commit and capture time; coverage is
informational and may remain null until measured. Count cases from result XML, never from
source attributes or legacy counts.

On the exit candidate, the parent runs the same locked restore and Release build as CI, then
produces the JUnit evidence required by verification §4.1 with the pinned xUnit native runner:

```bash
# Run from the repository root, after the Release build; output stays outside the checkout.
set -euo pipefail
baseline_results=$(mktemp -d)
export baseline_results
while IFS= read -r project; do
  name=$(basename "$project" .csproj)
  dotnet run --project "$project" --configuration Release --no-build --no-restore -- \
    -result-junit "$baseline_results/$name.xml" -failSkips -failWarns
done < <(find "$PWD/tests" -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' | sort)
python3 - <<'PY'
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET

results = Path(os.environ["baseline_results"])
counts = {}
for report in sorted(results.glob("*.xml")):
    cases = ET.parse(report).findall(".//testcase")
    if not cases:
        raise SystemExit(f"Cannot close bootstrap with an empty project: {report}")
    skipped = sum(case.find("skipped") is not None for case in cases)
    failed = sum(case.find("failure") is not None or case.find("error") is not None
                 for case in cases)
    if skipped or failed:
        raise SystemExit(f"Cannot baseline skipped or failed cases: {report}")
    counts[report.stem] = {"executed": len(cases), "passed": len(cases),
                           "failed": failed, "skipped": skipped, "coverage": None}
if not counts or not sum(row["executed"] for row in counts.values()):
    raise SystemExit("No executed cases; leave new: null")
candidate = results / "new-suite-counts.json"
candidate.write_text(json.dumps(counts, indent=2) + "\n")
print(candidate)
PY
```

This command produces a review input, `new-suite-counts.json`, alongside each project's JUnit
report. The oracle owner must match the reports to every discovered project, add the green
CI run provenance, and review the candidate before recording `new`; it does not update the
protected baseline automatically. Count-regression enforcement starts from that reviewed
Phase 2 exit baseline, with zero-case rejection activated at the same time. Local YAML parsing
and plan checks validate the wiring only: a real GitHub Actions run is the evidence for SDK
provisioning, restore/build/test execution, and artifact upload. Do not claim that CI passed
from local checks.

## 6. Review protocol

For every unit:

1. **Implementer** writes code + tests + ledger row in its worktree.
2. **Reviewer A** gets only the diff, the unit's parity row, and the relevant oracle artifacts.
   Prompt: "Assume this code is wrong. Compare it to the v10 reference and the contract.
   Enumerate behavioural differences, omissions, and lifetime/ordering bugs, file:line."
3. **Reviewer B** gets the same, ideally with a different model from A. It must enumerate
   separately; do not show A's findings. If the same model or provider has to review twice,
   record the correlation in the findings.
4. **Fixer** applies accepted findings. It does not add scope.
5. Re-run both reviewers on the updated diff until clean or until a finding is explicitly
   escalated to the captain.
6. Parent runs CI. A red CI is not a review finding; it is a gate.
7. Parent merges under the owner's merge decision and tears down the worktree.

Both reviews are adversarial agent assistance, not independent human approval: this repository
has one human owner, who reads the findings and decides the merge (ADR-024). Branch protection
requires the pull request and the five required checks (`inventory-and-parity`, `formal`,
`linux-build-and-test`, `AppImage packaging smoke`, `Flatpak packaging smoke`); strict/up-to-date
is intentionally off (see §5), and it has no approval requirement. CODEOWNERS is audit metadata
rather than an approval gate. The path-filtered oracle jobs run in addition when their paths
change.

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

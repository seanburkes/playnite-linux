# Port unit shards

`docs/port/queue.tsv`, `docs/port/ledger.tsv`, `docs/port/PORT-STATUS.md` and
`tests/baseline.json` are generated or bot-owned with a single writer: the post-merge
`port-index` workflow on `main`. A pull request owns only its own per-unit shards in this
directory and must not change any of those files, even to their freshly generated content.
Each unit owns a shard in this directory:

| File | Contents |
| --- | --- |
| `<unit-id>.queue.tsv` | The exact queue header plus exactly one queue row. |
| `<unit-id>.ledger.tsv` | Legacy, frozen history: the exact ledger header plus zero or more ledger rows. Readable, never edited in a pull request. |
| `<unit-id>.ledger.d/<pr>-<short-slug>.tsv` | One immutable ledger fragment per pull request: header plus that PR's rows. |
| `<unit-id>.ledger.d/0000-history.tsv` | Optional one-time migration target holding a legacy shard's rows verbatim. |
| `removed.tsv` | Optional; the exact `unit`/`reason`/`approved-by` header plus one row per retired unit. |
| `ledger-corrections.tsv` | Optional; documented one-off timestamp corrections. See below. |
| `README.md` | This contract. |

A shard uses the same columns, order, and TSV syntax as the aggregate it feeds. The queue
row's `id` and every ledger row's `unit` must equal the owning unit id. Missing ledger shards
mean the unit has no ledger entries yet.

## Unit ids and file names

A unit id is a stable identity: lowercase letters, digits, and single hyphens, for example
`p3-003`. Dots, spaces, underscores, path separators, mixed case, and empty ids are rejected
in both the file name and the row's `id`/`unit` field. The units directory is enumerated
strictly: anything other than `README.md`, `removed.tsv`, `ledger-corrections.tsv`,
`<unit-id>.queue.tsv`, `<unit-id>.ledger.tsv`, and a `<unit-id>.ledger.d/` directory holding
`<pr>-<short-slug>.tsv` or `0000-history.tsv` is an error. That includes a wrong-case suffix
such as `p1-012.QUEUE.TSV`, a backup such as `p1-012.queue.tsv.bak`, a trailing space, and a
directory named like a shard. A misnamed shard fails instead of quietly disappearing from the
aggregates.

## Add or update a unit

Add or update only your unit's shards, then validate the shard tree against the base branch:

```sh
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
```

A pull request that records ledger evidence adds exactly one new fragment,
`docs/port/units/<unit>.ledger.d/<pr>-<short-slug>.tsv`, named with its own pull-request
number and a short lowercase slug. The fragment is append-only after merge: a later pull
request never edits or deletes it. Existing rows live in the frozen legacy
`<unit>.ledger.tsv`; they stay readable and are concatenated first.

Never commit `docs/port/queue.tsv`, `docs/port/ledger.tsv`, `docs/port/PORT-STATUS.md` or
`tests/baseline.json` in a pull request. `build` is the parent-owned post-merge step; do not
run it to produce a PR diff. `check --shards-only` validates every shard in memory and prints
any error, without requiring the committed aggregates to match. The generator rejects a
missing or reordered header, ragged or blank rows, invalid UTF-8, more than one queue row per
shard, duplicate unit ids, duplicate ledger rows, filename/id disagreement, ledger rows for a
unit without a queue shard, a fragment directory without a queue shard, and unexpected
entries in this directory. To inspect what main will generate, run `build` in a scratch
checkout and discard the generated files; never include them in the PR.

## Ownership enforcement

The one-unit ownership check runs wherever the shard tree is compared with a base. On a pull
request, `plan-guards` passes `--forbid-aggregate-changes` against the fork point, which
enforces both generated-file ownership and shard ownership with the head commit's rules. The
post-merge `port-index` writer passes `--forbid-shard-ownership` against the previous `main`
commit before it writes, so a merge cannot launder an edited legacy ledger, a second fragment,
or a non-verbatim migration through the bot. `--forbid-shard-ownership` is the narrow form for
a tree-only check; `--forbid-aggregate-changes` implies it.

`docs/port/units/<unit>.queue.tsv` is a single file per unit. Two open PRs for the same unit
both change it, so the second must rebase and re-run CI; the guard cannot predict a concurrent
branch, and git's normal merge conflict is the lock. Non-prefixed unit assets are attributed
through the queue row's `oracle` and `test` columns (for example
`tests/golden/settings/*.json`), so a PR that touches an owned non-prefixed asset and another
unit's evidence fails instead of treating the asset as shared. The shared
`README.md`/`removed.tsv`/`ledger-corrections.tsv` files remain process material; validate them
with `check --against` so retirement and correction records are checked.

`docs/port/PORT-STATUS.md` is regenerated from the queue and ledger shards. Its only
hand-written part is the prose between the `port-status:narrative:start` and
`port-status:narrative:end` markers; every table outside those markers is generated. The
narrative must begin with `## Hand-written narrative` and may not contain the generated banner
or a generated heading (`# Port status`, `## Unit summary`, `## Ledger`, `## Units`), so a
hand-written block cannot impersonate a generated table.

## Ledger history is append-only in content

Ledger history is append-only in content. The legacy `<unit>.ledger.tsv` shard is frozen: it
stays readable, but a pull request may not add, edit or delete its rows. New evidence goes in
that PR's fragment. A row may never be edited, deleted, or duplicated. A duplicate is the same
unit, instant, and transition content; timestamps that name the same instant (for example `Z`
and `+00:00`) are compared as instants.

A unit may migrate its legacy shard once: move the rows verbatim into
`<unit>.ledger.d/0000-history.tsv` and delete the legacy file in the same change. History rows
keep the aggregate's historical ordering; fragments always follow them. The ownership gate
accepts that migration only as this exact paired move: the history bytes must equal the deleted
legacy bytes, the history file may only be added, and deleting the legacy file without either
the migration or a recorded retirement is rejected. A retirement that deletes the legacy file
must also delete the queue shard and add the unit to `removed.tsv` in the same change. A pure
verbatim migration is a process change: the unit-evidence guard accepts the paired delete/add
without a queue transition or a new PR fragment, but it still fails if the bytes differ or if
the change also touches the queue shard.

`check --against <ref>` enforces this against a base commit that must be a strict ancestor of
HEAD, per unit, as a multiset: every base ledger row must still exist at head with identical
field values. Order is not part of the guarantee, so a new row may carry any instant, including
one older than rows already recorded, and a row may be appended in any position. A head row
that matches a base row on every field except its timestamp is treated as a timestamp edit, not
a new event, and is rejected unless a correction names that unit and previous/corrected pair,
so retaining an old row and adding a changed copy cannot launder an edit. Every queue
shard and queue aggregate row present at `<ref>` must still be present at head; ledger shards
and rows may only be removed by a recorded retirement. In CI the base is the pull request's
base commit (or the previous commit on `main`).

## Ordering is generated, not authored

`docs/port/ledger.tsv` places legacy/history rows first, ordered by instant, then unit, then
the row's remaining fields, and then appends per-PR fragments in numeric pull-request id and
filename order, regardless of their timestamps. `docs/port/queue.tsv` is ordered by wave, then
unit. That ordering is derived output: `build` produces it, and the post-merge
`port-index` workflow on `main` is the only writer that commits it. Contributors never
hand-order or hand-edit an aggregate. The parent job's regeneration normalises order after
every merge.

### One-off timestamp corrections

A row that already landed with a wrong timestamp may be corrected once, in a change that also
adds a matching record to `ledger-corrections.tsv`:

```
unit	previous-timestamp	corrected-timestamp	actor	reason
p2-001	2026-11-15T01:51:31Z	2026-11-15T03:30:55Z	fm/p2-ledger-fix	merged out of order: #28 landed before #29, so this row sorted ahead of already-committed history
```

That is the record already in `ledger-corrections.tsv`; records are append-only, so its text is
kept verbatim even though the rule it describes is no longer how ordering is handled.

The record must be new in that change and match a base row exactly: same unit, same previous
instant, and the corrected row keeps every other field. A correction is a move: the previous
row must be gone from the head shard, not retained beside the corrected copy. A correction may
only move a row later, never earlier; it corrects recorded content, not ordering. At most one correction is allowed
per unit. A second record for the same unit, including one whose previous timestamp is an
earlier correction's corrected value, is rejected, so corrections cannot chain across changes.
Correction records are themselves append-only, so an earlier correction cannot be reused.

A new correction must be recorded while its unit is live. A record whose unit never existed, or
whose unit has no queue or ledger shard other than through retirement, is a dead record and is
rejected; a record added in the same change that retires the unit is also rejected. A historical
correction stays valid after a later retirement, because the append-only record describes a row
that did exist when it was recorded. `check` prints a notice naming every correction record it
accepts, so a reviewer sees which record authorized the history rewrite.

A correction is a captain-authorised operation, not something a contributor can self-approve.
It requires the same review gate as an oracle change: the `oracle-change` label through the
protected-path gate, in addition to the normal two adversarial reviews. CI enforces the
record's shape and history, not who approved it; human review of the record against the
corrected row is the authority boundary.

`build` does not reject a row by its instant; it validates every shard and writes both
aggregates in generated order. After the correction, regenerate local aggregates with `build`
to verify, but leave `docs/port/queue.tsv` and `docs/port/ledger.tsv` out of the pull request;
the post-merge `port-index` workflow commits them.

## Retiring a unit

Removal is never silent. To retire a unit, delete its queue and ledger shards and add a row
to `removed.tsv` in the same change:

```
unit	reason	approved-by
p1-012	merged into p1-011; no separate tracking row	captain
```

The retirement record must be new in that change and `approved-by` must be non-empty. The check
fails when a base shard vanishes without a new record, when a record names a unit that never
existed at the base, when a record is deleted, when an existing record's reason or approver is
edited, when `approved-by` is missing, or when a recorded unit's shard still exists. Unit ids
are not renamed: retire the old id and add a genuinely new unit instead. Retirement records are
permanent.

CI enforces the record's shape and history, not who approved it. A retirement is a
captain-authorized decision: human review of the record plus the deleted shards is the
authority boundary, not a signature scheme or a second approval file.

## CI

On a pull request, CI validates the shard tree itself:

- `check --shards-only --against <base>` parses the shards (schema, one row per unit id,
  filename/id agreement, duplicate rejection, strict enumeration) and enforces the ledger
  content-set and retirement history against the pull request base. Ordering is not checked
  on a pull request; the parent regeneration owns it.
- `--forbid-aggregate-changes` fails the pull request when it changes
  `docs/port/queue.tsv`, `docs/port/ledger.tsv`, `tests/baseline.json`, or generated
  `docs/port/PORT-STATUS.md` content (the narrative between the markers stays editable), even
  when the new content is exactly what `build` would produce. Edit only your unit shard and
  leave those files to the parent step.
- The generator also checks unit ownership: a pull request may touch one unit's queue and
  evidence namespace and add at most one immutable fragment named for its own PR. Editing the
  legacy `<unit>.ledger.tsv` fails; adding a fragment passes.

CI does not require the committed aggregates to equal freshly generated output. The
`port-index` workflow runs on a push to `main` and on successful `main` `linux-ci`
completions: before writing anything it validates the shard tree and history against the
previous `main` commit (a bad shard fails the run with no commit), then runs `build`, verifies
the generated bytes, and commits `docs/port/queue.tsv`, `docs/port/ledger.tsv`,
`docs/port/PORT-STATUS.md` and, from a completed run's downloaded artifacts,
`tests/baseline.json` with a bot commit. It has no manual-dispatch path, never accepts a
branch or ref as input, never force-pushes, and retries once against the new `main` if a
concurrent push rejects its first attempt. That single writer is what keeps `main` current and
what removes the shared-file conflict from every merge.

`tests/baseline.json` is a ratchet. The bot only raises a recorded floor after re-parsing the
merged-main run's JUnit and MTP exit artifacts and refuses to write a lower count. Existing
capture records and their provenance text are preserved, and a recorded project cannot vanish
without a `removed_projects` record.

The history check is what prevents a shard or ledger row from being deleted and regenerated
with every other check green. Deletions, renames, unrecorded retirements, and edited ledger
rows still fail on a pull request.

### PRs already in flight

If an open pull request already regenerated the aggregates, drop those two file changes and
keep only its shards:

```sh
git restore --source=origin/main -- docs/port/queue.tsv docs/port/ledger.tsv docs/port/PORT-STATUS.md tests/baseline.json
git commit --amend
```

Then re-run `check --shards-only --against origin/main`. No shard content needs to change; the
parent step regenerates the aggregates after merge.

The migration PR that first adds this directory is the one legitimate pre-migration base. Its
base has no `docs/port/units` tree, so the CI job passes `--allow-pre-migration-base`, which
prints an explicit history-skip acknowledgement; ordinary runs refuse a shardless base instead
of skipping history silently.

The initial migration mechanically reordered eight historical append rows into wave-then-id
order (`p1-009`, `p1-010`, `p2-006`, `p3-009`, `p7-009`, `p8-006`, `p8-007`, `p8-008`).
No row field changed, and the ledger regenerated byte-for-byte.

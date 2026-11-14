# Port unit shards

`docs/port/queue.tsv` and `docs/port/ledger.tsv` are generated aggregates with a single writer:
the post-merge `port-index` workflow on `main`. A pull request owns only the per-unit shards in
this directory and must not change either aggregate, even to its freshly generated content.
Each unit owns a shard in this directory:

| File | Contents |
| --- | --- |
| `<unit-id>.queue.tsv` | The exact queue header plus exactly one queue row. |
| `<unit-id>.ledger.tsv` | Optional; the exact ledger header plus zero or more append-only ledger rows. |
| `removed.tsv` | Optional; the exact `unit`/`reason`/`approved-by` header plus one row per retired unit. |
| `README.md` | This contract. |

A shard uses the same columns, order, and TSV syntax as the aggregate it feeds. The queue
row's `id` and every ledger row's `unit` must equal the filename's `<unit-id>`. Missing
ledger shards mean the unit has no ledger entries yet.

## Unit ids and file names

A unit id is a stable identity: lowercase letters, digits, and single hyphens, for example
`p3-003`. Dots, spaces, underscores, path separators, mixed case, and empty ids are rejected
in both the file name and the row's `id`/`unit` field. The units directory is enumerated
strictly: anything other than `README.md`, `removed.tsv`, `<unit-id>.queue.tsv`, and
`<unit-id>.ledger.tsv` is an error. That includes a subdirectory, a wrong-case suffix such as
`p1-012.QUEUE.TSV`, a backup such as `p1-012.queue.tsv.bak`, a trailing space, and a
directory named like a shard. A misnamed shard fails instead of quietly disappearing from the
aggregates.

## Add or update a unit

Add or update only your unit's shards, then validate the shard tree against the base branch:

```sh
python3 scripts/port/build_port_index.py check --shards-only --against origin/main
```

Never commit `docs/port/queue.tsv` or `docs/port/ledger.tsv` in a pull request. `build` is the
parent-owned post-merge step; do not run it to produce a PR diff. `check --shards-only`
validates every shard in memory and prints any error, without requiring the committed
aggregates to match. The generator rejects a missing or reordered header, ragged or blank rows,
invalid UTF-8, more than one queue row per shard, duplicate unit ids, duplicate ledger rows,
filename/id disagreement, ledger rows for a unit without a queue shard, and unexpected entries
in this directory. To inspect what main will generate, run `build` in a scratch checkout and
discard the two aggregate files; never include them in the PR.

## Ledger history is append-only

Ledger rows are append-only history. A later commit may append new rows at the end of a unit's
ledger shard, but it may never edit an existing row, delete one, duplicate one, or reorder
existing rows. A duplicate is the same unit, instant, and transition content; timestamps that
name the same instant (for example `Z` and `+00:00`) are compared as instants.

`check --against <ref>` enforces this against a base commit that must be a strict ancestor of
HEAD: the base ledger rows must be an exact prefix of the head rows, and the base ledger
aggregate must be an exact prefix of the regenerated ledger. A row may not be inserted before
existing history, and a new row may not carry a backdated instant that sorts ahead of it. Every
queue shard and queue aggregate row present at `<ref>` must still be present at head; ledger
shards and rows may only be removed by a recorded retirement. In CI the base is the pull
request's base commit (or the previous commit on `main`).

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
  prefix/append-only and retirement history against the pull request base.
- `--forbid-aggregate-changes` fails the pull request when it changes
  `docs/port/queue.tsv` or `docs/port/ledger.tsv`, even when the new content is exactly what
  `build` would produce. Edit only your unit shard and leave the aggregates to the parent step.

CI does not require the committed aggregates to equal freshly generated output. The
`port-index` workflow runs only on a push to `main`: before writing anything it validates the
shard tree and history against the previous `main` commit (a bad shard fails the run with no
commit), then runs `build`, verifies the generated bytes, and commits only
`docs/port/queue.tsv` and `docs/port/ledger.tsv` with a bot commit. It has no manual-dispatch
path, never accepts a branch or ref as input, never force-pushes, and retries once against the
new `main` if a concurrent push rejects its first attempt. That single writer is what keeps
`main` current and what removes the shared-file conflict from every merge.

The history check is what prevents a shard or ledger row from being deleted and regenerated
with every other check green. Deletions, renames, unrecorded retirements, and edited ledger
rows still fail on a pull request.

### PRs already in flight

If an open pull request already regenerated the aggregates, drop those two file changes and
keep only its shards:

```sh
git restore --source=origin/main -- docs/port/queue.tsv docs/port/ledger.tsv
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

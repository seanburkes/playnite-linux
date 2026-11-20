# Port unit shards

`docs/port/queue.tsv`, `docs/port/ledger.tsv`, `docs/port/PORT-STATUS.md`,
`docs/port/units/ownership.paths` and `tests/baseline.json` are generated or bot-owned with a
single writer: the post-merge `port-index` workflow on `main`. A pull request owns only its own
per-unit shards in this directory and must not change any of those files, even to their freshly
generated content.
Each unit owns a shard in this directory:

| File | Contents |
| --- | --- |
| `<unit-id>.queue.tsv` | The exact queue header plus exactly one queue row. |
| `<unit-id>.ledger.tsv` | Legacy, frozen history: the exact ledger header plus zero or more ledger rows. Readable, never edited in a pull request. |
| `<unit-id>.ledger.d/<pr>-<short-slug>.tsv` | One immutable ledger fragment per pull request: header plus that PR's rows. |
| `<unit-id>.ledger.d/0000-history.tsv` | Optional one-time migration target holding a legacy shard's rows verbatim. |
| `removed.tsv` | Optional; the exact `unit`/`reason`/`approved-by` header plus one row per retired unit. |
| `ledger-corrections.tsv` | Optional; documented one-off timestamp corrections. See below. |
| `ownership.paths` | Bot-generated path ownership map. Never edited or added by a pull request. |
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

## Unit-owned paths beyond evidence

A unit also owns the product source, shared tests, non-prefixed fixtures and tools that its
merged commits introduced or changed. The generator derives that map from reachable merged
commits: the queue shard a commit changes is the PR's recorded unit declaration, and a
product-only commit falls back to an unambiguous ledger claim. Only `src/`, `tests/` and
`tools/` paths are candidates; unit evidence namespaces (including a file under a
unit-named directory), generated aggregates, `tests/baseline.json` and shared build metadata
such as project and lock files stay outside the map.

`docs/port/units/ownership.paths` is the generated map, with the exact header
`path<TAB>unit<TAB>commit<TAB>pull` and one row per `(path, owner)`:

```
src/Playnite.Core/Import/AssetCopy.cs	p4-005	f12a4325c58cf66d47b93c5768e46861696d3052	pull/96
src/Playnite.Core/Import/LibraryMigration.cs	p4-003	8ff04700cf62708a5dbf3fbbc9dae08016fed9f5	pull/97
src/Playnite.Core/Import/LibraryMigration.cs	p4-005	f12a4325c58cf66d47b93c5768e46861696d3052	pull/96
```

Rows are sorted by path, then unit. A path may have several owners; any listed owner may
change it and a non-owner fails. Where attribution is uncertain (a commit spans several units,
a first-parent split, or an unrecorded product change), the path is recorded with the explicit
shared owner `*`; a path with a concrete owner does not also get `*`. A new file under a
unit-named directory belongs to that unit through the evidence namespace. A path no commit
touched is not in the map, but a unit PR that adds one must still declare its unit and carry a
fragment.

The map is regenerated by `build` on `main` and committed by the `port-index` workflow. A pull
request may not add, edit, rename or delete it, except for the one bootstrap change that adds
the first map when the base has none and the change touches no product/test/tool path. That
first map is compared byte-for-byte with a deterministic regeneration of the head history; an
arbitrary seed map fails. The guard reads the map from the pull request's base commit, never from
its head. It validates the header, known units, row order, commit existence and ancestry, and
that each non-`-` pull column agrees with the commit subject; a forged or stale map fails closed
with `ownership map unavailable`. A missing or empty map is reported as unavailable rather than
treated as permission, and **any** product/test/tool change fails in that state even when the
PR declares a unit: foreign ownership cannot be checked without data.

The unit guard fails a pull request that changes a path owned only by other units, naming the
path, the owning units and each map row's `pull/<number>` plus commit evidence. It also fails a
pull request that changes any unit-owned, product, test or tool path without declaring a unit.
Declare the unit with an `fm/<unit>-...` branch, a `Port-Unit: <unit>` trailer in the head
commit, or `--unit`; the declaration must carry that unit's queue transition and its own ledger
fragment. A queue or ledger fragment change without a branch or trailer declaration fails; the
guard never infers a unit from the queue on a process-style PR. A genuine process PR that
touches no unit evidence and no product/test/tool path needs no declaration.

Diff entries are repository-relative and may not contain `..`, an absolute path, or another
traversal component. Renames are charged as an explicit delete plus add of both paths, and
deletions are checked like any other change. Every intermediate component of a changed path and
of a symlink target is resolved; a symlink that escapes the repository, loops, passes through a
symlinked ancestor, or points at a foreign-owned file or directory fails. A directory target
must allow every claim in its subtree; mixed ownership is rejected rather than accepted because
one owner is permitted. The guard fails closed on duplicate map rows for the same owner,
duplicate diff entries, and unresolvable symlinks.

## Approved cross-unit moves

A pull request may move a path out of another unit's ownership only with an explicit manifest on
its head commit. Put the normal moving-unit declaration and the manifest in the same commit:

```
Port-Unit: p4-003
Cross-Unit-Move: p4-002:tests/golden/p4-002-legacy/PROVENANCE.md
```

The value is a comma-separated list of `<owning-unit>:<path>` pairs with no whitespace. The unit
is the path's actual owner, not the moving unit; the path is repository-relative. Multiple
`Cross-Unit-Move` lines are concatenated, and a path may appear only once.

Both required guard jobs, `unit-ownership` and `unit-evidence`, read this same head-commit
manifest through the shared `unit_guard_common.authorised_cross_unit_move` validator. Each job
exempts exactly the listed, validated foreign paths from its own foreign-path checks, and only
when all of these hold:

1. Every path the pull request changes that is owned by another unit (through the ownership map
   or a unit evidence namespace) appears in the manifest with its actual owning unit, and its diff
   status is `D` (deleted) or `M` (modified). A foreign added path cannot be listed.
2. The pull request adds at least one new file in the declaring unit's namespace: an
   evidence-namespace path, a path the base map already allows that unit, or unowned
   product/test/tool material that the unit's merge will own.
3. Every listed `D` path pairs one-to-one with its own added path with the same basename, and
   that added path must be owned by the declaring unit: an evidence-namespace path, a path the
   base map already allows that unit, or unowned product/test/tool material that the unit's
   merge will own. One add cannot pair with two deletes, and an add outside the declaring
   unit's namespace cannot stand in for the moved file.
4. Every listed `M` path pairs one-to-one with its own listed `D` path with the same basename.
   No `D` may back two modifications, and an `M` with no paired `D` fails, so the trailer
   cannot authorise an in-place foreign edit.

An empty or malformed manifest, a path the diff does not change, a wrong owner, a listed added
foreign path, a delete without its own same-basename declaring-unit counterpart, two deletes
sharing one add, a modification without its own delete, and two modifications sharing one delete
all fail closed with a named reason. Because both jobs call that one validator, a manifest cannot
satisfy the ownership check while being weaker or absent in the evidence check. Both jobs still
apply their own remaining rules: one unit declaration, the queue transition and fragment, the
base ownership map, symlink checks and the read-only diff all still apply. For the evidence job
this means the listed paths are exempt from both its foreign unit-evidence check and its
map-owned foreign path check, while every unlisted foreign path still fails each job. This
exception is not a general permission to edit another unit's files.

A cross-unit move is a captain-authorised operation, not something a contributor can
self-approve. It requires the `oracle-change` label through the protected-path gate, and the
captain reviews the manifest by hand against the diff: every pair names the real owner and the
moved file, the added counterpart is in the moving unit's namespace, and no unrelated foreign
path is covered. CI enforces the shape of the manifest, not the decision; human review of the
pair against the change is the authority boundary.

A fragment's cited head must be the pull request head, a commit on the pull request's own
first-parent spine after both the merge base and the event base, or the merge commit the
fragment describes. A run from an unrelated base-branch ancestor, from a main commit merged
after the branch point, or from the event base itself is rejected, so a rebased or
fast-forwarded branch cannot borrow a main commit's CI.

Every cited head must also resolve to a commit in the repository. A mistyped, abbreviated
beyond reach, or invented SHA fails; it is never skipped in favour of another citation.

The fragment's run id and head SHA must agree with the GitHub Actions API. The unit-evidence
job always passes `--verify-ci`, which resolves every cited run id through the read-only
Actions API using `GITHUB_TOKEN` from the environment, a bounded timeout and no cache. The
run must exist in this repository, be the completed `linux-ci` workflow with conclusion
`success`, and report a `head_sha` equal to a head the fragment cites. A missing run id, a
run from another repository, an unfinished or failed run, a `head_sha` that names a different
commit, or an unreachable API fails the check with a named reason instead of passing. Write
the fragment from the run's own page and copy both values exactly; do not transcribe a SHA by
hand or shorten one.

The negative proofs themselves are protected by `scripts/port/check_guard_tests.py`, run by
plan-guards' dedicated `guard-inventory` job. That job materialises the checker from the pull
request's **event base** into `$RUNNER_TEMP` and runs that copy before `inventory-and-parity`,
which declares `needs: guard-inventory`; there is no head-side enforcement step and the head
copy is never executed. The event-base copy is the trust root: emptying, renaming or changing
the head checker fails a byte-identity check, so it cannot hide a deleted or renamed guard test.
It enumerates `scripts/port/test_*.py` at the event base, the optional fork point and head, and
fails when a base/fork test file is deleted or renamed away, a test added after the fork and then
deleted is detected through head history, a base test case disappears, or the assertion count
drops. `scripts/port/test_guard_integrity.py` keeps the negative proofs, including emptied-head-
checker-only, changed-head-checker, fork-predating-checker, post-fork deletion, removed-
integrity-test, delete, rename and honest cases.

Limits: the workflow file itself and the base-materialisation step are protected by the
`oracle-change` protected-path gate, not cryptographically; a bootstrap PR whose **event base**
predates the checker runs an inline file-set check for that transition only. A head checker that
differs from the base checker is rejected; changing the checker requires a base-side transition.

## Generator selection and generated-file ownership

The aggregate and shard generator is selected from the current `main` tip fetched at guard
time. A PR head that contains current `main` runs its head tool; a stale head that does not
contain current `main` runs the current-main tool, so an old PR cannot keep an old tool that
predates the baseline, status or ownership rules. The aggregate step passes the fetched
current-main tip as the baseline/status comparison side, never the PR event base. A head tool
that differs from current `main` on a contained PR requires `oracle-change`.

`--forbid-aggregate-changes` is the pull request gate and implies shard ownership. The
post-merge `port-index` writer passes `--forbid-shard-ownership` against the previous `main`
commit before it writes, so a merge cannot launder an edited legacy ledger, a second fragment,
a non-verbatim migration, or a retirement delete that `removed.tsv` does not record. The
writer also passes `--allow-generated-ownership`, because its own earlier regeneration can
appear in that `--against` change set; the flag is rejected with the pull request gate, a pull
request still fails on any map change, and `build` rewrites the map deterministically before
the post-build `check` verifies the committed bytes.

`docs/port/units/<unit>.queue.tsv` is a single file per unit. Two open PRs for the same unit
both change it, so the second must rebase and re-run CI; the guard cannot predict a concurrent
branch, and git's normal merge conflict is the lock. The shared
`README.md`/`removed.tsv`/`ledger-corrections.tsv` files remain process material; a unit PR may
change a shared retirement or correction record only for its own unit, and `check --against`
validates the record itself.

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
- The generator also checks unit ownership: a pull request may touch one unit's queue, evidence
  namespace and mapped product/test/tool paths, and add at most one immutable fragment named for
  its own PR. Editing the legacy `<unit>.ledger.tsv` or the generated `ownership.paths` fails;
  adding a fragment passes.

CI does not require the committed aggregates to equal freshly generated output. The
`port-index` workflow runs on a push to `main` and on successful `main` `linux-ci`
completions: before writing anything it validates the shard tree and history against the
previous `main` commit (a bad shard fails the run with no commit), then runs `build`, verifies
the generated bytes, and commits `docs/port/queue.tsv`, `docs/port/ledger.tsv`,
`docs/port/PORT-STATUS.md`, `docs/port/units/ownership.paths` and, from a completed run's
downloaded artifacts, `tests/baseline.json` with a bot commit. It has no manual-dispatch path,
never accepts a branch or ref as input, never force-pushes, and retries once against the new
`main` if a concurrent push rejects its first attempt. That single writer is what keeps `main`
current and what removes the shared-file conflict from every merge.

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

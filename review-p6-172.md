# Review — PR #172 (p6-001 library-service paging)

- PR: https://github.com/seanburkes/playnite-linux/pull/172
- Head: `17183bc44b6e2c484e2358c2e83e4f0f7e38e195` (`docs(p6-001): record paging adoption evidence`)
- Reviewed diff: `data/p6-172/candidate.diff`, sha256
  `15c4db7ba4b2dc80a336a602559a775e2922eeba7eb1b3921eafd6fc7e60dd53`
  (6 files, +472/−13). `git diff 7b4c960..17183bc` reproduces that file byte-for-byte.
- Scope: static review of the supplied diff and the head tree; no build, no test execution,
  no other reviewer input. The code head is `aa5c7ce`; `17183bc` is docs-only (it reverts a
  transient p6-012 doc edit from `aa5c7ce` and adds the ledger fragment), so the net change
  under review is the six files in the diff.

## Verdict

**Changes requested — no P1, three blocking P2 findings.** The paging shortcut itself is
sound: the path predicate, the remainder check and the fallback all match the contract, the
pre-read validations are unchanged, and the SQL/comparer ordering agrees for the tested keys.
The findings are verification and contract-integrity defects: the changed in-memory sort is
not exercised by any test that can fail, the ledger overstates the fixture used by the CLI
equality test, and p6-012's own contract/queue are left asserting the opposite of this PR at
head.

## P2 — the changed in-memory sort path has no failing test

`src/Playnite.Core/Library/LibraryService.cs:90` (`new PagedLibraryComparer(...)`)

An unfiltered, ungrouped query returns from the paged shortcut at `:77-87`; every filtered,
term-bearing or grouped query falls through to `:90`. This PR changes that line from
`GameComparer` (current culture) to the paged NOCASE comparer, and
`docs/port/p6-001-library-service.md:31-43` makes NOCASE the contract for both paths. No test
would notice a revert:

- `tests/Playnite.Core.Tests/Library/LibraryServicePagingTests.cs:136-164`
  (`Text_order_matches_the_paged_reader...`) calls
  `service.ListAsync(new(SortBy: Name, Descending: descending, Limit: 20))` at `:149-150`.
  The query is unfiltered/ungrouped with a non-null limit, so it returns from
  `LibraryService.cs:77-87`; it never reaches `:90`. `Non_name_order...` at `:167-190` is
  the same shape.
- `Search_and_grouped_queries_keep_the_snapshot_path` (`:60-74`) does reach `:90`, but its
  assertions are membership only (one search match; a grouped page with no order
  assertion).
- The rest of the suite uses ASCII names whose current-culture order equals NOCASE
  (`LibraryServiceContractTests`, `LibraryServiceImportTests`, the CLI tests).

Concrete failure: replace `:90` with `new GameComparer(query.SortBy, query.Descending)`; the
full suite stays green, yet a filtered, searched or grouped page containing
`apple`/`Apple`/`Éclair` now sorts by current culture while the paged page sorts by NOCASE,
contradicting `docs/port/p6-001-library-service.md:31-43`. Add a mixed-case/accented fixture
on a term-bearing or filtered query (the `FakeReader`/internal constructor can be used) and
assert it against the paged order.

A related, narrower gap: the ledger claims a blank or null search reads one SQLite window
(`docs/port/units/p6-001.ledger.d/172-library-service-paging.tsv:2`). Nothing asserts that
path. `LibraryServiceContractTests.Search_matches...` uses the internal constructor with
`pagedReader == null` (`tests/Playnite.Core.Tests/Library/LibraryServiceContractTests.cs:44-57`),
and `SearchCommandTests.Search_with_a_blank_term...` would produce the same output on either
path (`tests/Playnite.Core.Tests/Cli/SearchCommandTests.cs:32-48`). Changing
`LibraryService.cs:77` to `term is null` leaves the suite green.

## P2 — the ledger says the CLI equality test used the frozen v10 fixture; the test is synthetic

`docs/port/units/p6-001.ledger.d/172-library-service-paging.tsv:2`

The evidence row says "the real CLI list and search first page and unpaged list are
byte-identical before and after on the frozen v10 fixture." The named test never opens
`tests/fixtures/v10/library`. `LibraryCliPagingEqualityTests` creates a fresh
`DatabaseTestDirectory` and seeds five hard-coded ASCII names
(`tests/Playnite.Core.Tests/Library/LibraryCliPagingEqualityTests.cs:77-95`, names at `:84`).
The updated p6-001 contract correctly calls this "a fixed library"
(`docs/port/p6-001-library-service.md:101-102`); the ledger is the outlier.

Concrete failure: the claimed oracle is not exercised. The test proves no CLI regression on a
synthetic five-game library (and only for titles where the old and new orders agree); it
cannot detect an ordering regression on the real fixture (Unicode names, sorting-name
overrides, more than one page). Either point the test at the frozen fixture as
`LibraryServiceImportTests` does, or correct the evidence row to "synthetic fixed library".

## P2 — p6-012's contract and queue still say the opposite at head

`docs/port/p6-012-paged-reader.md:39-40`, `:67-70`; `docs/port/units/p6-012.queue.tsv:2`

After this PR `LibraryService` is a production caller of `PagedLibraryReader`
(`src/Playnite.Core/Library/LibraryService.cs:15-18`, `:77-87`) and uses
`PagedLibraryComparer` (`:90`). At the same head the p6-012 material says:

- `docs/port/p6-012-paged-reader.md:39-40`: "`GameComparer`/`LibraryService` in p6-001 keep
  current-culture text order; this unit does not change that comparator."
- `docs/port/p6-012-paged-reader.md:67-70`: "no production caller is wired ... list and
  search still read the full snapshot until then."
- `docs/port/units/p6-012.queue.tsv:2`: "no production caller is wired, list/search still
  read the full snapshot".

The ledger records why this PR does not touch the p6-012 namespace (one-unit evidence guard)
and calls the wired note a follow-up (`172-library-service-paging.tsv:2`). Concrete failure:
merging this PR makes two authoritative contracts contradict each other, and a later change
that relies on "no production caller" starts from a false premise. Land the small p6-012
doc/queue correction before or with this PR, or have the captain explicitly accept the
deferral as part of the merge; do not treat the p6-012 adoption as complete from this PR
alone.

## Confirmations

- **Path predicate and fallback:** `LibraryService.cs:77-87` uses the paged reader only for
  an empty term, no filter and no group, and returns only when the query is bounded or the
  returned window reaches `TotalCount`. The offset/remainder arithmetic is correct for
  offset 0, a nonzero offset and a past-end offset; `LibraryServicePagingTests.cs:76-94`
  covers the >10,000 remainder fallback.
- **Validation before reads:** offset/limit/sort validation is unchanged, and the added
  `Enum.IsDefined(query.GroupBy)` check at `LibraryService.cs:69-72` throws before any
  reader is opened; `LibraryServicePagingTests.cs:96-131` covers the offset/limit bounds and
  the group key, while the existing contract theory keeps the sort case.
- **Ordering parity:** `PagedLibraryComparer` and `PagedLibraryReader.PageSql` agree on
  direction-for-primary-only, ascending effective name, ascending stored identity and null
  placement. The dedicated p6-012 final-key test pins the identity key
  (`PagedLibraryReaderTests.cs:301-356`), so this PR does not regress it.
- **Unchanged surface:** `GetAsync`, `LibraryPage(Games, TotalCount, Groups)` and the
  unfiltered pre-page count are unchanged; grouped pages still sort groups before members
  and apply paging after grouping (`LibraryService.cs:103-123`).
- **Scope:** no `legacy/` edits, no banned APIs, no aggregate files in the diff, and
  `git diff 7b4c960..17183bc` is byte-identical to the supplied candidate diff.

## Non-blocking notes

- `docs/port/units/p6-001.queue.tsv:2` still lists only
  `LibraryServiceContractTests; LibraryServiceImportTests` in the `test` column; add
  `LibraryServicePagingTests; LibraryCliPagingEqualityTests`.
- `LibraryServicePagingTests.cs:145-161` derives `expected` from the comparer and hard-codes
  an order whose tied `apple`/`Apple`/`APPLE` group is declared in identity order (10, 11,
  15). Removing the comparer's final identity key would leave that assertion green;
  p6-012's dedicated test covers the key, but this test's comment overstates what it pins.
- For a null limit with more than 10,000 games, `LibraryService.cs:79-87` first reads and
  discards a 10,000-graph window, then reads the full snapshot. A count-first check would
  avoid the extra load; performance only.
- The comparer folds ASCII and compares UTF-16 code units, while SQLite NOCASE compares
  UTF-8 bytes. BMP text agrees; astral-plane characters can still diverge despite the
  "exactly matching NOCASE" wording in `docs/port/p6-012-paged-reader.md:36-40`.

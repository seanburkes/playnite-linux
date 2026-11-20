# p6-012 — bounded ordered paging

`Playnite.Core.Library.PagedLibraryReader` reads one page of the stored library in a total
order computed by SQLite. This unit adds the reader without editing `LibraryService` or
`GameRepository`; a later p6-001 change wires the unfiltered, ungrouped list path to it.

## Operations

| Operation | Result |
| --- | --- |
| `ListAsync(query)` | A page of `StoredGame` graphs in the requested total order, with the pre-page count. Groups are always empty. |

`PagedLibraryQuery` carries `SortBy`, `Descending`, zero-based `Offset` and a required
`Limit` (default 100). A null limit is not representable, so a page is always bounded.

## Ordering

The page statement orders in SQL by the stored sort value, then the effective name
(`COALESCE(NULLIF(SortingName, ''), Name)`) when the primary key is not name, then the stored
identity text in ordinal order. This is the same shape as `LibraryService`: direction applies
only to the primary key; name and identity ties stay ascending. Null primaries sort first
ascending and last descending.

| `SortOrder` | Stored order value |
| --- | --- |
| `Name` | effective name (NOCASE) |
| `LastActivity`, `Added`, `Modified` | UTC ISO-8601 column |
| `ReleaseDate` | null flag, year (0 maps to 1 so `Empty` ties with year 1), month (default 1), day (default 1) |
| `Version`, `InstallDirectory` | text column (NOCASE) |
| `PlayCount`, `Playtime`, `InstallSize` | fixed-width decimal column |
| `UserScore`, `CriticScore`, `CommunityScore` | integer column |
| `IsInstalled`, `Hidden`, `Favorite` | integer flag |
| `RecentActivity` | `CASE` reproducing `GetGameRecentActivity` |
| `RomList` | ordered ROM paths joined with `", "` (NOCASE) |

Text columns (`Name`, `Version`, `InstallDirectory`, `RomList`) order with SQLite's NOCASE
collation. `PagedLibraryComparer` is the in-memory mirror: it folds ASCII `A`-`Z` and compares
code units, exactly matching NOCASE, so SQL and the comparer share one canonical text order.
The p6-001 adoption now uses this canonical order in `LibraryService` too, replacing the old
current-culture text comparer; numeric, date and flag keys are unchanged.

## SQL and memory

One snapshot is used for both statements:

```sql
SELECT COUNT(*) FROM game;
SELECT game.Id FROM game ORDER BY <terms> LIMIT @Limit OFFSET @Offset;
```

Ordering, `LIMIT` and `OFFSET` run in SQLite, so the full table is never materialised in the
process. Game graphs are loaded only for the returned page identities. The final tie-breaker
is `game.Id ASC`, which makes a page stable when a primary key repeats. An internal session
seam lets a test double record the statement and the requested window; production uses the
SQLite snapshot adapter.

## Bounds and cancellation

`Offset` is accepted from 0 to `LibraryQuery.MaxOffset` (1,000,000); `Limit` from 1 to
`LibraryQuery.MaxLimit` (10,000). Values outside those ranges and undefined `SortOrder`
values throw `ArgumentOutOfRangeException` before a destination is opened. The
`CancellationToken` reaches the snapshot, each SQL command and the per-page graph loads; a
cancelled call throws `OperationCanceledException` without a partial page.

## Boundary

Wiring status: **wired**. The p6-001 follow-up makes `LibraryService.ListAsync` delegate an
unfiltered, ungrouped list to this reader, so a bounded page reads only its window and a null
limit reads the remainder as one window while it fits the page cap. Filters, v10 text search
and grouping stay with p6-001 and keep their one-snapshot in-memory path, because the v10
matcher, filter helpers and group keys are not stored SQL expressions.

## Tests

`PagedLibraryReaderTests` covers first, middle, last, empty and past-end pages; offset and
limit bounds; duplicate primary keys with name and identity ties; descending null order;
determinism across repeated calls and reversed insertion order; pre-read and mid-load
cancellation; the generated statement's `LIMIT`/`OFFSET`/tie-breaker; an instrumented session
seam proving a page of N requests only the N-row window; numeric and date parity with
`LibraryService` in both directions; text order against `PagedLibraryComparer` for
mixed-case, accented and numeric-prefixed titles, including a case-only test that fails under
BINARY order; and a total-order test that reads the stored `Id` text and pins the comparer's
final ordinal identity key. Every game in that test ties on the primary key and the name and is
inserted in reverse text order, so dropping the identity key leaves the stable sort in insertion
order and fails the test; the same holds for both directions.

`LibraryServicePagingTests` covers the adoption: an instrumented reader session records the
requested offset and limit for `LibraryService.ListAsync`, and the mixed-case, accented and
numeric-prefixed text order is compared across `LibraryService`, `PagedLibraryReader` and
`PagedLibraryComparer`.

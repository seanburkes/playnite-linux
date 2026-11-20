# Library service contract

`Playnite.Core.Library.LibraryService` is a read surface in Core. Create it with the
`LibraryDatabase` opened by the host. The returned `StoredGame.Value` contains game
fields and actions. Core supplies these types and can be loaded headlessly.

The CLI query commands belong to p6-011. That unit can call `ListAsync` for list,
`SearchAsync` for search, and `GetAsync` to resolve a game before a launch dry-run.

## Operations and query order

| Operation | Result |
| --- | --- |
| `GetAsync(Guid)` | The detached stored game, including its ordered relations and preserved legacy JSON. An unknown ID returns null. |
| `ListAsync(query)` | A page of matching games and the full count before paging. The default query includes hidden and uninstalled games. |
| `SearchAsync(term, query)` | The same page shape, restricted by the v10 text matcher on the display name. The term is trimmed. Null and blank terms behave like list. A named search excludes null and blank game names. |

Each call reads one SQLite snapshot through the existing repositories. Editing a returned
game affects that object only. Callers can pass a cancellation token and receive an
`OperationCanceledException` without a partial result. A later request reads a fresh
snapshot, so a write between requests can change page membership.

An unfiltered, ungrouped list delegates to `PagedLibraryReader`, which runs the total order,
`LIMIT` and `OFFSET` in SQL and loads game graphs only for the requested window. A text term,
filter or grouping keeps the v10 in-memory semantics and evaluates one snapshot, because the
v10 matcher and filter helpers are not expressible in the stored SQL columns.

1. Search and any supplied filter both have to match. Name search supports the v10
   case-insensitive fuzzy, acronym, and word matching. The library page uses the selected
   sort order and has no quick-search result cap.
2. Sort by a defined `Playnite.Core.Queries.SortOrder` key. The default is ascending name
   using SQLite's NOCASE collation over the game's sorting-name override when present.
   NOCASE folds ASCII `A`-`Z` and then compares code units, so mixed-case titles compare
   equal and accented or non-ASCII titles order by their code units. Sort keys other than
   name break ties by ascending NOCASE name. The final tie-breaker is the stored identity
   text in ordinal order. Null primary fields sort first ascending and last descending. For
   ROM list sorting, null and empty lists use the empty string as their sort value. Undefined
   sort and group enum values throw `ArgumentOutOfRangeException`.

   This replaces the earlier current-culture text order. The change is deliberate: the paging
   reader and `PagedLibraryComparer` define one canonical text order so an in-memory page and
   a SQL page cannot disagree. Numeric, date and flag keys are unchanged, and case-only titles
   now tie on the name and break by stored identity instead of culture rules.
3. When grouping, the group order takes precedence over the game sort. Release year,
   playtime, and score groups descend. The remaining groups ascend by their key. Year
   groups use an integer and missing years use null. Name groups use the v10 initial
   character, with `#` for missing names, punctuation-only names, or digit initials.
   Other groups use their corresponding Core model enums, including `Never` for missing
   dates and `DateTime.MinValue`. Date filters and groups treat stored ticks as UTC,
   convert them to the host's time zone, and use one clock reading per request.
   The host can inject a `TimeProvider` to control the time zone and reading.
4. Page the ordered results by zero-based `Offset` (0 to 1,000,000) and optional
   `Limit` (1 to 10,000). Values outside those ranges throw
   `ArgumentOutOfRangeException` before reading SQLite. A null limit returns the
   remainder. An offset past the end returns an empty page with the pre-page count.
   Group results contain the members on the current page and preserve their order.
   Ungrouped queries return an empty group list.

A bounded unfiltered, ungrouped list never materialises the library: `PagedLibraryReader`
issues `COUNT(*)` and one ordered `LIMIT`/`OFFSET` statement in the same snapshot, then loads
only the window's game graphs. A null limit asks for the remainder; when that remainder fits
the reader's 10,000-row page cap it is read as one window, and a larger remainder keeps the
one-snapshot in-memory path so the "one snapshot per call" guarantee still holds. Text search,
filters and grouping also keep the one-snapshot in-memory path, because the v10 matcher, filter
helpers and group keys are not stored SQL expressions; their paging bounds are unchanged.

The grouping keys cover name, release year, installation status, playtime, user, critic
and community score, install size, last and recent activity, added and modified. Plugin
display names, metadata-name sorting, and multi-membership groups are outside this
service's stored-field query surface.

## Filter input

Pass `LibraryFilter.FromSettings(FilterSettings)` for typed v10 criteria or
`LibraryFilter.Parse(json)` for named criteria. JSON keys are case-insensitive and follow
the writable properties of `FilterSettings` and its nested criteria. Unknown keys at
any level and ill-typed values throw `JsonException`. Null, blank input and JSON null omit
the filter. Empty or whitespace-only name and version criteria have no effect.

Supplying criteria applies v10 filter behaviour, including the `Hidden` selector.
`Hidden: false` selects visible games and `Hidden: true` selects hidden games. With no
filter the service returns both. Text criteria support the v10 name prefixes `^` for a
name initial and `!` for literal substring matching. ID criteria use `Guid.Empty` as the
existing missing-value sentinel, including null or empty metadata lists. Release year
criteria use the existing `{}` sentinel for missing values.

## References and tests

- `legacy/source/Playnite/ViewModels/SearchViewModel.cs` supplies the name matcher,
  `legacy/source/Playnite/Database/GameDatabase_Filters.cs` supplies filter semantics, and
  `legacy/source/Playnite.DesktopApp/DesktopCollectionView.cs` and
  `legacy/source/Playnite/GamesCollectionViewEntry.cs` supply game and group ordering.
  The query helpers from p3-006 already port their headless behaviour.
- `LibraryServiceContractTests` uses a fake reader to cover list, ID lookup, search,
  strict filter binding, sort ties and nulls, grouping, paging and cancellation.
- `LibraryServicePagingTests` proves through an instrumented paged-reader session that a
  bounded unfiltered list requests only its window, that search and grouping stay on the
  snapshot path, that a remainder beyond the page cap falls back once, and that bounds and
  the group key still fail before any read. It also pins the NOCASE text order for mixed-case,
  accented and numeric-prefixed titles against `PagedLibraryReader` and `PagedLibraryComparer`.
- `LibraryCliPagingEqualityTests` runs the real `list` and `search` commands on a fixed
  synthetic library and pins the first page and unpaged list to the pre-wiring output.
- `LibraryServiceImportTests` runs the existing `import-v10` command on the frozen
  `tests/fixtures/v10/library` oracle and queries the imported SQLite file. It verifies
  persisted IDs, graph detachment, Unicode search, hidden filtering, grouped pages
  and UTC activity grouped and filtered by local calendar day.

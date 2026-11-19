# Library service contract

`Playnite.Core.Library.LibraryService` is the read surface for the CLI and Desktop. Create it
with the `LibraryDatabase` opened by the host. The CLI unit can call `ListAsync` for list,
`SearchAsync` for search, and `GetAsync` to resolve a game before a launch dry-run. It can
consume the returned `StoredGame.Value` for game fields and actions. The Core project
supplies all of these types and can be loaded headlessly.

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

1. Search and any supplied filter both have to match. Name search supports the v10
   case-insensitive fuzzy, acronym, and word matching. The library page uses the selected
   sort order and has no quick-search result cap.
2. Sort by a defined `Playnite.Core.Queries.SortOrder` key. The default is ascending name
   using the current culture and the game's sorting-name override when present. Sort keys
   other than name break ties by ascending name. The final tie-breaker is ascending GUID.
   Null primary fields sort first ascending and last descending. For ROM list sorting,
   null and empty lists use the empty string as their sort value. Undefined sort and
   group enum values throw `ArgumentOutOfRangeException`.
3. When grouping, the group order takes precedence over the game sort. Release year,
   playtime, and score groups descend. The remaining groups ascend by their key. Year
   groups use an integer and missing years use null. Name groups use the v10 initial
   character, with `#` for missing names, punctuation-only names, or digit initials.
   Other groups use their corresponding Core model enums, including `Never` for missing
   dates. Date filters and groups convert the stored UTC dates to the host's time zone
   and use one clock reading per request. The host can inject a `TimeProvider` to control
   the time zone and reading.
4. Page the ordered results by zero-based `Offset` and optional positive `Limit`. A null
   limit returns the remainder. An offset past the end returns an empty page with the
   pre-page count. Group results contain the members on the current page and preserve
   their order. Ungrouped queries return an empty group list.

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
- `LibraryServiceImportTests` runs the existing `import-v10` command on the frozen
  `tests/fixtures/v10/library` oracle and queries the imported SQLite file. It verifies
  persisted IDs, graph detachment, Unicode search, hidden filtering and grouped pages.

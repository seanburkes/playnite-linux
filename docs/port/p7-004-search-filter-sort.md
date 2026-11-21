# p7-004 — Search / filter / sort

## Scope

Search term, bounded quick filters and selected sorts for the p7-003 library grid. Game edit and
metadata (p7-005), settings/import (p7-006/009), tray/protocol (p7-007), pixel baselines (p7-008),
SDK UI freeze (p8-007), grouping, add-on discovery and store/network/WebView/plugin execution are
out of scope. No protected fixture, golden, parity, baseline or aggregate is edited.

## Oracle and integration

- Parity rows `ui-007` (Search) and `ui-008` (Filters/sort/group) in
  `docs/port/surface-parity.tsv:75-76` stay `planned` and untouched. The v10 behaviour references
  are `legacy/source/Playnite/ViewModels/SearchViewModel.cs` (name matcher),
  `legacy/source/Playnite.DesktopApp/Controls/Views/FilterPanel.cs`,
  `Controls/Menus/SortSettingsMenu.cs`, `Controls/SearchBox.cs` and
  `Themes/Desktop/Default/Views/FilterPanelView.xaml`, read only.
- There is no public composition seam in the p7-002 shell or the p7-003 library screen. The three
  library-screen files were moved by exact one-to-one Cross-Unit-Moves from their true owner
  p7-003 into `src/Playnite.Desktop/p7-004/Shell/`, preserving CLR namespace
  `Playnite.Desktop.Shell`, class names, `x:Class`, control names and bindings. `ContentHostView`
  and all p7-002 shell files stay byte-for-byte unchanged; the `using:Playnite.Desktop.Shell`
  references and the compiled class-to-resource association remain valid. No guard was weakened.
- New labels are resolved through the existing frozen `LOC.*` keys with
  `BootstrapLocalization.Resolve`; no SDK/Core/Avalonia surface is moved.

## Behaviour contract

- The query bar is a real part of the library screen: a search `TextBox`, five quick-filter
  buttons (All / Installed / Uninstalled / Hidden / Favorite), a sort `ComboBox` (Name,
  LastActivity, RecentActivity, ReleaseDate, Playtime, UserScore) and an ascending/descending
  button. Every interactive control carries an `AutomationProperties.Name`.
- Each control change goes through a real Avalonia routed event to the instance-owned
  `LibraryViewModel`, which builds `LibraryQuery(Filter, SortBy, Descending, GroupBy: None,
  Offset: 0, Limit: PageSize)` and calls `LibraryService.SearchAsync(term, query, ct)` for a
  non-blank term or `ListAsync(query, ct)` otherwise. The v10 term is trimmed; null/blank behaves
  like a list. The selected filter is built with `LibraryFilter.FromSettings(FilterSettings)`;
  `Hidden:false/true`, installed/uninstalled and favorite follow the p6-001 filter semantics.
- Each query change cancels the previous in-flight query and **resets paging to page 1**. Ordering
  and ties come from p6-012 (`PagedLibraryComparer` / SQL total order: primary key with NOCASE
  text order, then name, then ordinal stored Id). Repeated identical queries return the same
  ordered identities; a query is always bounded by `PageSize = 50`.
- p7-003 selection, details and paging are preserved: real selectable `ListBoxItem` rows with
  semantic names, `SelectedIndex` synchronization, non-first selection details through
  `LibraryService.GetAsync`, `PreviousButton`/`NextButton` no-ops at bounds and the `Page n / m`
  indicator. No static locator; the view-model keeps its single-`string` constructor.
- The headless `--search-filter-sort-smoke` driver raises the real control events and emits markers
  only after the applied controls and model agree, including semantic names and the page reset.

## Evidence

- `tests/Playnite.Architecture.Tests/Bootstrap/SearchFilterSortSeamsTests.cs` (8) — default query
  state, trimmed v10 term search, acronym/word matching, bounded quick filters, stable selected
  sort with direction, bounded stable page reset on query change, instance-ownership guard, and
  moved namespace/`x:Class`/p7-003 control-name invariants.
- `tests/Playnite.Smoke.Tests/SearchFilterSortSmokeTests.cs` (2) — headless seeded six-game library
  driving real search/filter/sort/reset controls and an empty-library control check, under isolated
  XDG roots; writes stay inside the temporary profile.
- Mutation proofs: removing the search `TextChanged` wiring, removing the sort `SelectionChanged`
  wiring, and removing the page reset each fail the smoke/architecture assertion; restored files
  pass. Zero skips; executed-case counts non-decreasing (Architecture 246 → 254, Smoke 9 → 11).

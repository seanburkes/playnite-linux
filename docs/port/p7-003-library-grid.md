# p7-003 — Library grid/list/details

## Scope

Grid/list/details only, over the merged p6-001 `LibraryService` and p6-012 bounded ordered paging.
Search/filter/sort (p7-004), game edit/metadata (p7-005), settings/import wizard (p7-006/009),
tray/protocol/keyboard (p7-007), pixel/screenshot baselines (p7-008), SDK UI extension freeze and
add-on discovery are out of scope. No store/network/WebView/plugin execution.

## Oracle and integration

- Parity row `ui-004` (`docs/port/surface-parity.tsv:72`, protected, stays `planned` here) points at
  `legacy/source/Playnite.DesktopApp/Themes/Desktop/Default/Views/LibraryGridView.xaml`; the
  v10 behaviour references are `legacy/source/Playnite.DesktopApp/Controls/Views/LibraryGridView.cs`,
  `LibraryListView.cs` and `LibraryDetailsView.cs` (read-only).
- `ShellView.axaml` already names a `ContentHost` Border and statically instantiates
  `Playnite.Desktop.Shell.ContentHostView`. The p7-002-owned `ContentHostView.axaml/.axaml.cs` were
  moved by a one-to-one Cross-Unit-Move into `src/Playnite.Desktop/p7-003/Shell/`; the CLR namespace
  and class were preserved, so the `using:Playnite.Desktop.Shell` reference and the compiled XAML
  class→resource index remain valid with no p7-002 file edited in place.
- The moved host owns an instance `LibraryGridView`, its `LibraryViewModel`, and a cancellation
  source disposed on unload. No static locator reaches p7-001's private `ServiceRegistry`.

## Library destination

The grid reads a new app-owned SQLite destination at
`Path.Combine(XdgDirectories.Data, "library.sqlite")` — i.e. `$XDG_DATA_HOME/playnite/library.sqlite`
(`PlaynitePaths.UserData`). A missing file is created with the empty schema by
`LibraryDatabase.OpenAsync`; this is the empty-library state. The frozen v10 fixture under
`tests/fixtures/v10/library` is read only by tests that import it into a separate temporary
destination; the grid never points at the fixture or its source. Import transport/wizard remains
p7-009.

## Behaviour contract

- Rows are real, accessible `ListBoxItem` controls in a single-selection `ListBox`, each with an
  explicit `AutomationProperties.Name`. Rows come from
  `LibraryService.ListAsync(new LibraryQuery(Offset, Limit: 50, SortBy: Name))` in one bounded
  p6-012 SQLite window; repeated first-page loads return the same ordered identities.
- Selecting any row (not only the first) raises the ListBox `SelectionChanged` event, routes through
  `LibraryViewModel.SelectAsync`, reads that game's details with `LibraryService.GetAsync` and
  applies the result to the real details TextBlocks and semantic names. An empty library renders
  zero rows and `(none)` details.
- Paging is bounded at `PageSize = 50`: `PreviousPage`/`NextPage` buttons are enabled from
  `CanGoPrevious`/`CanGoNext` and route through `LoadPageAsync`; requests beyond page 1 or the last
  page are no-ops. The page indicator reports `Page n / m` from the real total. No search, filter
  or sort control exists (p7-004).
- The headless `--library-smoke` sequence drives the actual ListBox selection and button click
  routed events, then emits markers only after the real applied controls match the view-model.

## Evidence

- `tests/Playnite.Architecture.Tests/Bootstrap/LibraryViewSeamsTests.cs` (5) — empty new library,
  fixture-imported rows/details including a non-first selection, bounded 60-game page navigation
  with distinct ordered windows, repeated stable pages, instance-ownership constructor guard.
- `tests/Playnite.Smoke.Tests/LibrarySmokeTests.cs` (3) — headless empty, fixture-backed non-first
  selection, and a 60-game XDG-owned library with page 1/2 rows, page 2/2 rows, back navigation and
  out-of-range no-ops, under isolated XDG roots. The fixture is read-only.
- Mutation proofs: removing the ListBox `SelectionChanged` wiring and removing the Next button
  `Click` wiring each make the smoke fail; restored files pass. Zero skips; UI executed-case count
  non-decreasing.

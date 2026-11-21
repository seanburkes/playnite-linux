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

- Rows come from `LibraryService.ListAsync(new LibraryQuery(Offset, Limit: 50, SortBy: Name))` in
  one bounded p6-012 SQLite window; repeated first-page loads return the same ordered identities.
- A selected first row's details are read back through `LibraryService.GetAsync` and applied to the
  real details TextBlocks. An empty library renders zero rows and `(none)` details.
- The view applies row text and `AutomationProperties.Name` from the view-model; the headless
  `--library-smoke` marker is emitted only after the real applied controls match the model.

## Evidence

- `tests/Playnite.Architecture.Tests/Bootstrap/LibraryViewSeamsTests.cs` — empty new library,
  fixture-imported rows/details with repeated paged loads, instance-ownership constructor guard.
- `tests/Playnite.Smoke.Tests/LibrarySmokeTests.cs` — headless empty and fixture-backed loads with
  isolated XDG roots; mutation of one semantic-name assignment was shown to fail the smoke and the
  restored file passes. Zero skips; UI executed-case count non-decreasing.

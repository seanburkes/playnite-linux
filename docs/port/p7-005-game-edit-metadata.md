# p7-005 — Game edit and metadata display

## Scope

Single selected game edit and metadata display over the p6-001 `LibraryService` read surface and
the p4-001 `LibraryDatabase.WriteAsync` → `GameRepository.UpdateAsync` write path. Settings/first-run
(p7-006/009), tray/protocol/keyboard (p7-007), screenshot baselines (p7-008), SDK UI freeze
(p8-007), add-on discovery, store/network/WebView/plugin execution, search/filter/sort (p7-004) and
the library grid (p7-003/004) are out of scope. No protected fixture, golden, parity row, baseline,
aggregate or ownership-map edit.

**Image picker is deferred** (captain ruling). The image-picker prose in Phase 7 order 4 is not
claimed by this unit: the p7-005 queue oracle is the generic `screens` token and there is no
image-picker parity row. No OS file dialog, gallery chooser, web image search, byte loading or
download is included. `Icon`, `CoverImage` and `BackgroundImage` are displayed and editable as raw
string references only.

## Oracle and integration

- Parity row `ui-009` (`docs/port/surface-parity.tsv:77`, protected, stays `planned`) points at
  `legacy/source/Playnite.DesktopApp/Windows/GameEditWindow.xaml`. The behaviour references are
  `legacy/source/Playnite.DesktopApp/ViewModels/GameEditViewModel.cs` (editable fields, non-empty
  name validation, `ConfirmDialog` and its single `database.Games.Update(game)` at line 944),
  `GameEditViewModelCommands.cs`, `GameEditViewModelFieldChecks.cs`, `GameEditViewModelMetadata.cs`
  and `legacy/source/PlayniteSDK/Plugins/MetadataPlugin.cs` (`sdk-009`), all read only.
- The v10 image-picker commands (`GameEditViewModelCommands.cs:135-205`, `GameEditViewModel.cs:997-1131`)
  and the separable gallery/web dialogs are the deferred boundary, not delivered here.
- **Route A (captain approved):** two true one-to-one Cross-Unit-Moves of the p7-003-owned
  `ContentHostView.axaml` and `.axaml.cs` into `src/Playnite.Desktop/p7-005/Shell/`. CLR namespace
  `Playnite.Desktop.Shell`, class name, `x:Class`, control names and every p7-003 `--library-smoke`
  hook are preserved, so `ShellView.axaml:16` and the compiled XAML class/resource index stay valid.
  Every commit carries `Port-Unit: p7-005` and the two exact
  `Cross-Unit-Move: p7-003:src/Playnite.Desktop/p7-003/Shell/ContentHostView.axaml{,.cs}` trailers.
  No p7-004 grid file, p7-002 shell file, p7-001 bootstrap file, `App.cs` or `Program.cs` is edited.
- There is no public composition seam: `ShellView` statically hosts `ContentHostView`, which
  statically hosted `LibraryGridView`; `ServiceRegistry` is private to p7-001, and ADR-023 defers
  SDK UI extension points to p8-007. The moved host is the only legal integration point.

## Behaviour contract

- The moved host owns its p7-004 `LibraryGridView`/`LibraryViewModel` instances and listens to the
  existing `GameList` `SelectionChanged` event. It reads the selected `ListBoxItem.Tag` (`LibraryRow`),
  loads the full `StoredGame` through its own second `LibraryDatabase` wrapper on the same
  `$XDG_DATA_HOME/playnite/library.sqlite`, and applies a deep `Game.GetCopy()` draft to the edit view.
- The bounded editable set is Name, Description, Version, InstallDirectory, IsInstalled, Hidden,
  Favorite, UserScore and the three raw image references. Other metadata is displayed read-only
  (`Release`, platform/tag/developer counts). Multi-edit `Use*Changes` is deferred.
- Save collects values from the real controls, rejects a blank name without a write (v10 parity),
  then commits one atomic full-graph replace preserving every untouched field, all relations and the
  original `StoredGame.LegacyJson`. It reloads the current bounded page and re-applies the grid;
  selection, details, paging and search/filter/sort remain p7-003/p7-004 behaviour. No nested write.
- Cancel reloads the draft from the database without writing. Cancellation and unload dispose the
  instance-owned tokens; no static locator is introduced. The second wrapper opens per-operation
  connections and never reaches the p7-004 view-model's private database.
- Headless `--game-edit-smoke` raises the real control events and emits markers only after the
  applied controls, the re-read database values and the grid details agree.

## Evidence

- `tests/Playnite.Architecture.Tests/Bootstrap/GameEditMetadataSeamsTests.cs` (8) — empty draft,
  bounded load and read-only metadata, save with untouched fields/relations/LegacyJson preserved,
  blank-name rejection, cancellation without partial write, clear without write, moved
  namespace/class/`x:Class`/control-name and p7-004 guard-path invariants, single instance
  constructor without a service locator.
- `tests/Playnite.Smoke.Tests/GameEditMetadataSmokeTests.cs` (2) — headless seeded load/save/re-read
  and empty-library controls under isolated XDG roots, with semantic-name checks; fixture and all
  protected files read only.
- Mutation proofs: removing the field collection, removing the `SaveButton` `Click` wiring, and
  removing the `GameList` `SelectionChanged` subscription each fail the headless save assertion;
  restored files pass. Zero skips.
- Local Release: Desktop 0 warnings/0 errors; Core 3126/3126; Architecture 264/264 (prior 256);
  Smoke 13/13 (prior 11). Ownership/shard/parity guards and CI evidence are recorded in the shard.

## Deferred follow-up

The actual v10 image picker (OS file dialog, gallery chooser, web image search, byte loading) needs a
Platform file-dialog/image-decode surface and is intentionally not part of this unit.

# Desktop game sessions, playtime tracking, and launch configuration (`p7-004`)

Extends the desktop library (`src/Playnite.Desktop/p7-004/`) to execute custom/native Linux games directly via `LaunchRequest` argv vectors, observe active processes through `GameSessionTracker` and `SessionSleepInhibition`, persist playtime/play count/last activity in SQLite, and configure custom games and GameMode/MangoHud wrappers in-app without breaking existing Steam Play behavior.

## v10 references

- `legacy/source/Playnite/GamesEditor.cs`
- `legacy/source/Playnite/Controllers/GameControllerFactory.cs`
- `legacy/source/Playnite/Controllers/GenericGameController.cs`
- `legacy/source/Playnite.DesktopApp/Windows/GameEditWindow.xaml`
- `legacy/source/Playnite.DesktopApp/ViewModels/GameEditViewModel.cs`
- `legacy/source/Playnite.DesktopApp/ViewModels/DesktopAppViewModel.cs`

## Behaviour contract

- **Argv planning and wrapper composition (`GameLaunchPlanner.cs`):**
  - Parses user-supplied launch argument strings safely into argv arrays (quoted tokens, backslash escapes; rejects NUL bytes and unclosed quotes) with zero shell execution.
  - Validates executable paths and optional working directories against `LaunchRequest` rules (absolute paths required; shell interpreters `sh`, `bash`, `dash`, `zsh`, etc. rejected).
  - Supports composing `gamemoderun` (`GameMode`) and `mangohud` (`MangoHud`) prefixes from standard system directories (`/usr/bin`, `/usr/local/bin`, `/bin`) and round-trips wrapper flags back into editor controls.
- **Tracked sessions and playtime persistence (`DesktopGameSessionManager.cs`):**
  - Runs `IProcessLauncher` (`LinuxProcessLauncher`), `IProcessObserver` (`LinuxProcessObserver`), `GameSessionTracker`, and `SessionSleepInhibition` off the UI thread.
  - Shows `SessionBadge` (`Running` / `Running (degraded)`) and `StopButton` during active tracked sessions while disabling conflicting launch/navigation/sync actions.
  - `StopButton` or unloading the library view terminates the active process tree without leaving orphans and releases the sleep-inhibition lease.
  - Once `Running` is observed, atomically increments `Playtime`, `PlayCount`, and updates `LastActivity` in SQLite (`library.sqlite`) and refreshes `DetailsPane` and card metadata.
  - Installed Steam games without a custom `File` play action preserve `SteamPlayRequest` URI launching.
- **Add custom game & configure launch (`GameLaunchEditorWindow.axaml`, `LibraryViewModel.cs`):**
  - Header `AddGameButton` opens `GameLaunchEditorWindow` to create a custom Linux game with an executable `GameAction` (`GameActionType.File`, `IsPlayAction = true`).
  - Details `ConfigureLaunchButton` edits an existing game's `File` play action (or adds one while preserving non-play actions and `LegacyJson`).

## Smoke verification

- `tests/Playnite.Smoke.Tests/p7-004/GameSessionExperienceSmokeTests.cs`:
  - `GameLaunchPlanner_parses_argv_composes_wrappers_and_rejects_shells_and_bad_paths`
  - `Tracked_session_runs_updates_live_ui_persists_playtime_playcount_and_last_activity_on_natural_exit`
  - `Stop_button_terminates_real_linux_process_records_playtime_and_leaves_no_orphan`
  - `Add_custom_game_and_configure_launch_editor_validates_persists_and_launches`
  - `Unload_stops_active_tracked_session_releases_sleep_inhibition_without_orphans_or_ui_errors`

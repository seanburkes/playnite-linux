# p7-009 — First-run import and Steam setup

## Oracle and scope

- `ui-011`: `legacy/source/Playnite.DesktopApp/Windows/FirstTimeStartupWindow.xaml`,
  `ViewModels/FirstTimeStartupViewModel.cs`, and `DesktopApplication.ProcessStartupWizard`.
  The v10 wizard offered provider setup before entering Desktop and persisted completion.
  ADR-021 replaces provider selection with a mounted-library choice or Steam-only setup.
  The existing p7-006 welcome remains a separate acknowledgment after the wizard.
- `core-007`, `core-010`, `import-001`–`import-007`: p4-002/p4-003/p4-004/p4-005,
  `legacy/source/Playnite/Database/GameDatabase.cs` and the frozen
  `tests/fixtures/v10/library` define the verified migration handoff.
- Firstmate approved moving only `src/Playnite.Desktop/App.cs` to
  `src/Playnite.Desktop/p7-009/App.cs` and staging the reader with Desktop. No shell,
  bootstrap, settings UI, CLI, importer, or package script was changed.

## Behavior contract

1. Before constructing the shell, read `FirstTimeWizardComplete` from the native XDG
   settings file. The shell otherwise opens/creates `library.sqlite` on load. If the
   wizard is not complete, run it before showing the shell. Existing headless
   diagnostics retain their own entry points.
2. Choose a local path from a mounted Windows partition, USB drive, or network share.
   The wizard accepts the directory containing `database.json` or the parent whose
   `library` child contains it. The original v10 app must be closed. No Windows export
   executable, authentication, network download, or add-on setup is needed.
3. Desktop starts `legacy-import/Playnite.LegacyImport` by absolute apphost path, passes the
   selected path as one argument, and reads the existing p4-002 v1 neutral JSON envelope
   from stdout. Only the sidecar depends on LiteDB. The build and publish stage it in its
   own directory, with its own runtime for self-contained builds. No LiteDB project
   reference, assembly load, or package enters Desktop. The Desktop process adapter
   implements the p6-002 reader interface, not a second import pipeline.
4. `LibraryImportService`/`LibraryMigration` validate, copy, verify and publish, with
   replacement disabled. The progress display follows the typed checkpoints; cancellation
   waits for reader exit and uses the migration's cancellation/publication contract.
   Failures leave setup incomplete and show diagnostics. On success the UI identifies
   `library.sqlite.import/source.json` (neutral document backup, not a Windows database
   backup) and `library.sqlite.import-report.json` beside the SQLite library. The
   source is untouched and remains the v10 rollback copy. If a library already exists,
   including after interruption after publication, the wizard will not replace it.
5. Selecting the parent folder also captures its optional regular `config.json`. The
   p4-004 settings migration writes `library.sqlite.import-settings.json`, with a `legacy`
   bag and path report. A user's explicit original-Windows-to-selected-mounted-folder
   correspondence is the only supplied path mapping; the existing settings service
   only rewrites supported typed paths under that root. No drive-letter or name guesses.
   An existing native settings file wins. Otherwise Finish can adopt the validated
   archive, with the Linux wizard flag set atomically. A settings error after the library
   publishes is reported separately; the user can finish without the old settings.
6. Steam setup uses p6-003 local manifest discovery and sync. No Steam installation is a
   visible empty-library result, not an import error. Only explicit Finish writes
   `FirstTimeWizardComplete` and opens the shell; closing without Finish leaves the
   wizard incomplete. Already-published data stays in place and can be kept at relaunch.

## Evidence

The setup window uses the shared Playnite dark palette and groups import and Steam
actions into separate panels. When a native library exists, the entire import panel
is hidden. The import, cancellation, and completion contracts above are unchanged.

`FirstTimeSetupSmokeTests` drives real Avalonia actions against isolated XDG profiles:
reader progress, valid and invalid mounted paths, the unchanged copied v10 corpus,
cancellation and retry, backup/report contents, settings mapping and unmapped retention,
settings-failure handoff, completion persisted independently of the welcome, Steam with
and without local games, and LiteDB absent from the Desktop dependency manifest.

# p6-004 — settings service

## Boundary and oracle

`Playnite.Settings.PlayniteSettingsService` is a UI-free Core service over the p4-004
settings envelope: `schemaVersion`, `settings`, `legacy`, `settingsUtf8` and `report`. It adds
no Avalonia, network, process or Windows dependency. `SettingsMigration.ExportV10` remains the
authoritative compatibility oracle: every successful load and write proves that
`settings ∪ legacy` still equals the archived v10 graph.

The p4-004 migration artifact is the storage shape. A raw v10 `config.json` is never adopted
silently: `LoadAsync` refuses it with `SettingsFailure.LegacyShape`, and
`MigrateFromV10Async` is the explicit migration entry point. Migration refuses an existing
store (`SettingsFailure.AlreadyExists`) and writes only after the migration and envelope
validation both pass.

References: `docs/port/02-ARCHITECTURE.md` §3.5, `docs/port/p4-004-settings-migration.md`,
frozen `tests/golden/settings/config.json`, and `legacy/source/Playnite/Settings/PlayniteSettings.cs`
for the typed defaults.

## Typed contract and defaults

`SettingsSnapshot.Get(PlayniteSettingsKeys.X)` resolves a value from `settings` and then
`legacy`. An absent value reads as the documented default; defaults are not persisted by a
read or by an unrelated update. The modeled keys are:

| Key | Default | v10 source |
| --- | --- | --- |
| `Version` | `7` | settings object version |
| `StartInFullscreen` | `false` | `startInFullscreen` |
| `StartMinimized` | `false` | `startMinimized` |
| `MinimizeToTray` | `false` | `minimizeToTray` |
| `CloseToTray` | `true` | `closeToTray` |
| `EnableTray` | `true` | `enableTray` |
| `ShowSidebar` | `true` | `showSidebar` |
| `FirstTimeWizardComplete` | `false` | `firstTimeWizardComplete` |
| `Language` | `"english"` | `language` |
| `Theme` | `"Playnite_builtin_DefaultDesktop"` | `ThemeManager.DefaultDesktopThemeId` |
| `DatabasePath` | `null` | `databasePath` |
| `InstallInstanceId` | `null` | `installInstanceId` |
| `AutoBackupEnabled` | `false` | `autoBackupEnabled` |
| `AutoBackupFrequency` | `OnceAWeek` (`2`) | `autoBackupFrequency` |
| `CheckForProgramUpdates` | `OnEveryStartup` (`0`) | `checkForProgramUpdates` |

`SettingsSnapshot.Settings`, `.Legacy` and `.Report` expose copies of the remaining graph, so
unmodeled v10 properties remain readable. The key type is extensible for a known v10 root
property; unknown names are rejected on update and stay in `legacy`.

## Read and validation

`LoadAsync` returns a snapshot and never creates a file. It validates:

- the root is a JSON object with no duplicate or trailing content;
- `schemaVersion` is the integer `1`;
- `settings`, `legacy` and `report` are objects and `settingsUtf8` is a string;
- `SettingsMigration.ExportV10` accepts the envelope, proving `settings ∪ legacy` equals the
  archived v10 bytes;
- every modeled value that is present has its documented JSON type and, for enums, a defined
  v10 value.

Failures carry a stable category: `Unreadable`, `Corrupt`, `UnsupportedSchema`, `LegacyShape`,
`InvalidValue`, `AlreadyExists` or `WriteUnavailable`.

## Update and data preservation

`UpdateAsync` validates typed updates, takes a fresh read of the store, applies only the
requested known root properties, and rebuilds the envelope. Unknown and unmapped properties
remain in `legacy`; unknown properties already in `settings` are never dropped. Updating a
property preserved in `legacy` moves that property to `settings` and leaves unrelated legacy
properties untouched. The compatibility bytes are regenerated canonically, so untouched
values keep their value and unknown members but not necessarily their original whitespace or
number spelling.

A missing store is created from the documented defaults plus the requested changes. A
no-op update (same value, or an empty list) reads once, writes nothing and raises no event.
`Changed` is raised after the write completes and after the service gate is released, so a
handler may call the service again.

## Atomic writes and unwritable directories

`SettingsFileStorage` stages a sibling `.<name>.<random>.tmp`, writes and flushes it to disk,
then renames it over the destination with overwrite. If the config directory cannot be created
or written, or the rename fails, the call throws `SettingsFailure.WriteUnavailable`, deletes
its own stage, and leaves the previous file unchanged. Symbolic-link paths are rejected.
Reads report a missing file or directory as an absent store; other IO and permission failures
surface as `Unreadable` rather than silently loading defaults.

The migration entry point stages in the system temporary directory, then uses the same atomic
storage write. It never overwrites an existing store.

## Evidence and test map

Local Release checks on this branch: Core **2704 passed**, Architecture **199 passed**, zero
failed/skipped; build **0 warnings / 0 errors**.

`tests/Playnite.Core.Tests/Settings/SettingsServiceTests.cs` covers every public operation:

| Contract | Test |
| --- | --- |
| Missing store/defaults and no file creation | `Missing_store_returns_documented_defaults_without_creating_a_file` |
| Real fixture round-trip and typed values | `Load_real_settings_fixture_round_trips_v10_bytes_and_typed_values` |
| Full real-fixture preservation through an update | `Updating_a_real_fixture_preserves_every_other_value` |
| Missing-store creation | `Update_creates_a_store_from_documented_defaults` |
| Unknown/legacy preservation and event | `Update_preserves_unknown_and_legacy_properties_and_raises_one_change` |
| Legacy-property move | `Updating_a_legacy_property_moves_it_to_settings_without_losing_siblings` |
| No-op/empty update | `No_op_update_writes_nothing_and_raises_no_event`, `Empty_update_returns_the_current_snapshot_without_a_write` |
| Corrupt JSON refusal without overwrite | `Corrupt_json_fails_with_a_clear_category_and_is_never_overwritten` |
| Explicit v10 boundary | `Raw_v10_config_is_refused_as_an_explicit_migration_boundary` |
| Schema refusal | `Unsupported_schema_is_refused_without_writing` |
| Typed-value validation | `Wrong_typed_value_is_rejected_by_load_validation` |
| Unknown-name refusal | `Unknown_property_name_is_not_written_into_settings` |
| Write/read failure contract | `Write_failure_keeps_the_previous_snapshot_and_does_not_raise`, `Read_failure_is_categorized_and_never_writes` |
| Atomic-write failure and stage cleanup | `Atomic_write_failure_leaves_the_target_untouched_and_no_stage`, `Unwritable_config_directory_is_reported_before_the_store_exists` |
| Explicit migration and refusal | `Migration_is_explicit_atomic_and_refuses_to_replace_an_existing_store` |
| Migration validation failure | `Migration_validation_failure_leaves_no_store` |
| Cancellation | `Pre_cancelled_operations_do_not_read_or_write` |
| Disposal | `Disposed_service_refuses_further_operations` |

The fake `ISettingsStorage` cases exercise the service contract without the file system; the
fixture and file-storage cases exercise the real envelope, atomic rename and POSIX failure
behaviour. No network or UI is involved.

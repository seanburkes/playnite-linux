# p6-003 — Steam library sync

`Playnite.Steam.Sync.SteamLibrarySyncService` reconciles the installed Steam library
discovered by p5-002 into the SQLite game library. It is headless: the service references no
Avalonia, UI, network or process-launch surface, and it writes only through the supplied
`LibraryDatabase`. The production discovery host, `SteamDiscoverySource`, composes the merged
p5-002 root/library discovery with the p5-001 manifest parser. Both hosts are replaceable with
a fake for contract tests.

The service lives in `Playnite.Steam` rather than `Playnite.Core` because the architecture
keeps Core independent of the Steam subsystem (dependency rule 6). Core supplies
`LibraryDatabase`, `GameRepository`, the game model and the SQLite transaction.

## Contract

| Input | Result |
| --- | --- |
| `ISteamSyncDiscovery.Discover` | Installed apps, per-app skips, global issues and `CanReconcile`. |
| `SteamLibrarySyncService.SyncAsync(LibraryDatabase)` | `SteamSyncResult` with `Added`, `Updated`, `Removed`, `Unchanged`, `Skipped`, `Issues` and `Reconciled`. |

One call discovers once and performs every library write in one `LibraryDatabase.WriteAsync`
transaction. An exception or cancellation rolls the whole run back; there is no partial sync
and no file outside the library database is created or modified. The database is not opened
at all when discovery has nothing to write or reconcile.

### Identity

A Steam game is identified by the frozen v10 Steam plugin id
`cb91dfc9-b977-43bf-8e70-55f46e410fab` plus the canonical decimal app id. An existing record
from the v10 import is matched by that pair regardless of its GUID, so importing and then
syncing does not duplicate it. A newly added record gets a deterministic RFC 9562 version-5
identity derived from the plugin id and app id. `SteamLibrarySyncService.GameIdFor(appId)`
exposes the same value. Generated identities are not created with `Guid.NewGuid`, which the
repository bans in logic projects.

If the deterministic identity already belongs to a different game, the app is reported as
`identity-conflict` and nothing is overwritten. If the database already holds two records for
one Steam app, the lowest-id record is updated and every extra record is reported as
`duplicate-library-record`; the extras are never mutated or deleted.

### Provider fields and user edits

The service writes only provider-owned fields:

| Field | Rule |
| --- | --- |
| `Name` | Current Steam manifest name. |
| `InstallDirectory` | `<library>/steamapps/common/<installdir>`, unless `OverrideInstallState` is set. |
| `InstallSize` | Manifest `SizeOnDisk` when present; a missing value never clears a stored size. |
| `IsInstalled` | `Installed` or `UpdatePending` is installed; `Broken`, `Unknown` and absent are not. |
| `Added` | Set once, when the record is created. |
| `Modified` | Set only when a provider field changes. |

Every other field is preserved verbatim on update: sorting name, favorite/hidden flags, notes,
description, artwork paths, release date, scores, playtime, play count, last activity, tags,
genres, links, actions, ROMs, scripts and `LegacyJson`. A record is never deleted. Playtime
belongs to the local session tracker (p5-004), not to Steam, so sync does not write it.

`Game.OverrideInstallState` is the model's explicit user override. While it is set, the sync
never changes `IsInstalled` or `InstallDirectory` for that record, including on removal. Name and
install size remain provider metadata and still update. The override is not cleared by the sync.

### Removal

`Removed` counts records that left the installed set, not deleted records: an app that was
observed as installed before and is absent from a complete discovery is marked
`IsInstalled = false`, its install path and every user field stay in place, and it is installed
again if the app reappears.

Removal runs only when the discovery is authoritative (`CanReconcile`), which the production
host grants only on positive proof of a complete, non-empty observation: a Steam root, one parsed
library file, every declared entry projected, every library either enumerated or declaring a
non-empty app set, and at least one app observed. An unavailable library's apps are reported as
skipped rather than treated as absent, so a detached drive cannot uninstall those games. An empty
result proves nothing, because it is equally consistent with a library the user emptied or lost.
The host withdraws authority and reports a degraded issue whenever the declared library set is
not known to be complete:

| Degraded issue | Cause |
| --- | --- |
| `library-file-rejected` | The library file was not parsed, so the declared library list is unknown. p5-002 names a copy as selected even when the parser rejects it; the root library is still enumerated, so its apps are observed while every other library may be missing. |
| `library-entry-dropped` | The parser dropped a declared entry (its path is not absolute), so neither its path nor its declared apps reached discovery. |
| `library-apps-unknown` | An unavailable library did not declare its app set, so the apps that lived there are unknown rather than absent. |
| `library-apps-emptied` | A declared library's app set is empty and nothing was enumerated there either, so an emptied block cannot be told apart from a lost one. |

A run that suppresses removal still adds and updates every observed app; it reports
`Reconciled = false`, `Removed = 0` and the issue naming the cause. When a rejected primary copy
is replaced by a usable `config/libraryfolders.vdf` the primary's diagnostic remains in the issue
stream, so the host cannot tell it apart from a rejected-only copy and also withholds removal;
that direction can only keep a game installed. The absent-app rule is applied after every
declared app id in the discovery, including skipped ones, has been marked as observed.

### Skips and issues

Every app that is not written appears in `Skipped` with its numeric app id and a stable reason:

| Reason | Meaning |
| --- | --- |
| `library-missing`, `library-not-directory`, `library-steamapps-missing`, `library-steamapps-outside`, `library-stale-mountpoint`, `library-duplicate`, `library-unreadable` | The declared library is unavailable; its app ids come from `libraryfolders.vdf`. |
| `manifest-missing` | A declared app has no `appmanifest_<appid>.acf`; it is not invented as a game. |
| `manifest-not-regular`, `manifest-too-large`, `manifest-unreadable`, `manifest-rejected` | The manifest exists but the bounded reader or parser refused it. |
| `manifest-invalid-name` | A manifest file name is not an app id; reported as an issue without inventing an id. |
| `appid-duplicate` | The same app id was already projected from an earlier library. |
| `invalid-install-path` | The parsed install directory does not produce a safe path under the library. |
| `identity-conflict`, `duplicate-library-record` | Library reconciliation found an ambiguous record; see above. |
| `library-file-rejected`, `library-entry-dropped`, `library-apps-unknown`, `library-apps-emptied` | The observation is incomplete, so removal is suppressed; see Removal. |

Global `Issues` carry discovery parse diagnostics, a missing Steam root, a library enumeration
failure and their optional app id/detail. No skip is silent.

## Idempotency and reconciliation

The stored record is compared with the discovery before every update. A second sync over
unchanged input therefore reports `Added = Updated = Removed = 0` and leaves `Modified`
unchanged, even when skipped apps are still present in the discovery. `SteamSyncResult.Changes`
is `Added + Updated + Removed`; `Unchanged` counts records compared and left alone. The tests
assert this at both the contract and real-corpus levels.

`Reconciled` mirrors the discovery's `CanReconcile`: a false value means absent records were
not marked uninstalled, not that the sync failed. A degraded discovery (missing root, rejected
or dropped library declaration, unknown or emptied app set, no observed app, or a library that
could not be enumerated) reports `Reconciled = false` together with an issue naming the cause.

## Tests

| Test | Evidence |
| --- | --- |
| `SteamLibrarySyncServiceTests.First_sync_adds_each_app_and_a_repeat_changes_nothing` | Add counts, identity fields, deterministic timestamp, second-run zero changes and byte-stable model JSON. |
| `SteamLibrarySyncServiceTests.Existing_Steam_game_is_matched_by_app_id_and_user_edits_survive_an_update` | Imported GUID matching, provider-field update and preservation of user fields and `Added`. |
| `SteamLibrarySyncServiceTests.A_removed_app_is_marked_not_installed_and_never_deleted` | `Removed` count, record retention, user fields and repeat no-op. |
| `SteamLibrarySyncServiceTests.Reappearing_app_is_installed_again_without_duplicating_the_record` | Symmetric reinstall without a new record. |
| `SteamLibrarySyncServiceTests.Unreconcilable_or_unavailable_discovery_never_marks_a_game_uninstalled` | No-root and unavailable-library safety. |
| `SteamLibrarySyncServiceTests.OverrideInstallState_is_respected_for_both_provider_state_and_removal` | The model's explicit install-state override. |
| `SteamLibrarySyncServiceTests.OverrideInstallState_protects_the_install_directory_from_a_provider_path_change` | The override also protects `InstallDirectory`, not only `IsInstalled`. |
| `SteamLibrarySyncServiceTests.Missing_install_size_does_not_clear_a_stored_size` | Missing is not zero. |
| `SteamLibrarySyncServiceTests.Every_skipped_app_is_reported_with_its_id_and_reason` | Per-app skip reporting and issue passthrough. |
| `SteamLibrarySyncServiceTests.Duplicate_library_records_are_reported_and_the_extra_record_is_untouched` | Duplicate identities are visible and safe. |
| `SteamLibrarySyncServiceTests.Deterministic_identity_conflict_is_reported_instead_of_overwriting_another_game` | Generated-id collision handling. |
| `SteamLibrarySyncServiceTests.Pre_cancelled_sync_writes_nothing` | Cancellation leaves the library untouched. |
| `SteamLibrarySyncServiceTests.Game_identity_is_stable_and_distinct_per_app` | Deterministic identity. |
| `SteamLibrarySyncIntegrationTests.Real_corpus_syncs_four_manifests_reports_every_other_declared_app_and_is_idempotent` | The redacted real corpus: 4 manifest apps added, every other declared app reported, unavailable external library preserved, second sync zero changes. |
| `SteamLibrarySyncIntegrationTests.Truncated_library_file_suppresses_removal_and_reports_a_degraded_sync` | A truncated library file hides a second library: `Removed = 0`, the unobserved app stays installed, `Reconciled = false` and `library-file-rejected`. |
| `SteamLibrarySyncIntegrationTests.Dropped_library_entry_suppresses_removal_and_reports_a_degraded_sync` | A declared entry the parser drops leaves its app unobserved: `Removed = 0` and `library-entry-dropped`. |
| `SteamLibrarySyncIntegrationTests.Emptied_library_app_set_suppresses_removal_and_reports_a_degraded_sync` | A detached library whose app set was emptied: `Removed = 0` and `library-apps-emptied`. |
| `SteamLibrarySyncIntegrationTests.Emptied_app_set_on_a_reachable_library_suppresses_removal` | An emptied declaration on a reachable library with no manifest is still no proof: `Removed = 0` and `library-apps-emptied`. |
| `SteamLibrarySyncIntegrationTests.Unavailable_library_without_a_declared_app_set_suppresses_removal` | A detached library that declares no app set: `Removed = 0` and `library-apps-unknown`. |

The integration test copies `tests/fixtures/steam/real` into a temporary home, verifies every
`SHA256SUMS` entry, remaps the two recorded library paths and runs the production discovery
host. It never reads the developer's Steam installation, uses no network, and loads no UI
assembly. The corpus contains no game content, so all four projected install directories are
absent and the pure projection reports them `Broken`; that is asserted rather than assumed.

## References

- `docs/port/02-ARCHITECTURE.md` §3.3: Steam sync is repeatable, upserts apps and never deletes
  user data.
- `docs/port/08-STEAM-SPEC.md` §§1–4: discovery order, library statuses, manifest projection and
  the runtime/Proton boundary.
- p5-002: `SteamDiscovery`, `SteamDiscoveredLibrary`, `SteamLibraryStatus`.
- p5-001: `SteamManifestParser`, `SteamManifest.ToApp`, `SteamAppState`.
- p4-001/p4-002: `LibraryDatabase`, `GameRepository`, the frozen Steam plugin GUID and the
  no-loss import report.

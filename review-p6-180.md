# p6-003 review — Steam library sync service

- PR: https://github.com/seanburkes/playnite-linux/pull/180
- Reviewed head: `877fcb0f33b767546d0f3381967b0ad1044bed7f` (`docs(p6-003): record Steam sync evidence`)
- Implementation head: `d3e12f12b74b8b43ba75b91ece59378213e2c9f7` (`feat(p6-003): add Steam library sync service`); `git diff d3e12f 877fcb0` is the one ledger fragment, +2 lines, so the cited CI run covers every code, test and doc byte under review.
- Base: `423b501fac07058e04fd320384a78ed0de7e9db2`
- Candidate diff: `/home/sean/src/firstmate/data/p6-180/candidate.diff`, SHA-256 `1579cd1ed6eaf95c4597b6116f464095d76ddc285937fa94ba23e1472e5e59ee`; `git diff 423b501 877fcb0` reproduces it byte-for-byte (8 files, +1261/-1).
- Method: static review of the diff and the APIs it calls. No build and no test execution, per the brief; CI is cited, not re-run.

## Verdict

**Not clean: two blocking P1 findings.** The sync is a good, mostly well-bounded design, and its
single-transaction, provider-field-only, record-preserving core is sound. But the production host
can report an authoritative discovery after a declared library's app list was lost, which lets the
sync mark installed games uninstalled; and the sync overwrites the one user field that
`OverrideInstallState` explicitly protects. Both violate the unit's own written safety contract and
are not covered by the tests.

## P1-1 — `CanReconcile` remains true after a declared library is dropped or emptied

**Location:** `src/Playnite.Steam/Sync/SteamDiscoverySource.cs:46` (predicate), with
`src/Playnite.Steam/Sync/SteamDiscoverySource.cs:51-58` (declared apps come only from
`library.Apps`), `src/Playnite.Steam/Sync/SteamLibrarySyncService.cs:147-169` (removal pass), and
the promise at `docs/port/p6-003-steam-sync.md:70-73` and
`docs/port/units/p6-003.queue.tsv:2` ("unavailable or incomplete discovery never marks games
uninstalled").

`canReconcile` is derived only from `result.LibraryFilePath is not null`, plus the one later
`Directory.GetFiles` exception path. It is not derived from whether the library file and every
declared library/app map were actually observed. The p5-002 parser already has two lossy-but-valid
outcomes that leave `LibraryFilePath` non-null:

- `src/Playnite.Steam/SteamLibraryParser.cs:63-66` — a library entry with a non-absolute/invalid
  path is skipped with `invalid-library-path`; the rest of the file still parses, so the library is
  absent from `result.Libraries` and its apps are unknown to the host.
- `src/Playnite.Steam/SteamLibraryParser.cs:90-94` — any malformed app entry invalidates the whole
  `apps` map (`apps.Clear()`, `invalid-apps-map`); the library survives with an empty app list, so
  every app it declares is unknown to the host.

A whole-file parse rejection has the same effect through `SteamDiscoveryResult.LibraryFilePath`:
`TryReadAndParse` marks the file `observed`, returns `null`, and `SteamDiscovery.Discover` then
returns only the implicit root library. In every case the host's `declared` list for the lost
library is empty, no `SteamSyncSkip` is emitted for its app ids, yet `canReconcile` is `true`.

**Concrete failure:** a user has app 440 installed in `/mnt/games/SteamLibrary` and a synced record
(`PluginId = cb91dfc9-b977-43bf-8e70-55f46e410fab`, `GameId = "440"`, `IsInstalled = true`). A
truncated/edited `libraryfolders.vdf` produces `invalid-apps-map` (or `invalid-library-path`, or a
whole-file `library-parse-*`). The host discovers no app 440, adds no skip for 440, and returns
`CanReconcile = true`. `SteamLibrarySyncService` builds `observed` from `Apps` + `Skipped`, does not
contain 440, and the removal pass at `SteamLibrarySyncService.cs:157-169` sets
`IsInstalled = false` and `Modified = now` on the record. The user's installed game is shown
uninstalled, exactly the detached-drive/incomplete-discovery outcome the contract says cannot
happen.

This is not only a doc mismatch: the removal pass is gated on a global boolean, so one lost
library entry authorises removals for app ids the same run never observed.

**Required:** make `CanReconcile` false whenever the file was selected but not fully parsed, a
library entry was skipped, an `apps` map was cleared, or an unavailable library has a null app map
(the host currently silently substitutes `[]` at `SteamDiscoverySource.cs:51`). If p5-002 does not
expose per-library completeness, the conservative fix in this PR is to treat the relevant
library-level `SteamParseIssue` reasons as non-authoritative; a follow-up can add an explicit
completeness flag. `CanReconcile = false` must also suppress the database-open shortcut only in the
sense that removal does not run; updates/additions can still proceed.

**Why the tests miss it:** `SteamLibrarySyncIntegrationTests` covers an unavailable library whose
`apps` map is intact (`tests/Playnite.Core.Tests/Steam/SteamLibrarySyncIntegrationTests.cs:48-58`
and the corpus), and `SteamLibrarySyncServiceTests.Unreconcilable_or_unavailable_discovery_never_marks_a_game_uninstalled`
supplies the skip explicitly. No test feeds the host a malformed library path, a cleared apps map,
or a rejected `libraryfolders.vdf` and then asserts `CanReconcile == false` / `Removed == 0`.

## P1-2 — `OverrideInstallState` protects `IsInstalled` but not `InstallDirectory`

**Location:** `src/Playnite.Steam/Sync/SteamLibrarySyncService.cs:195-200` (unconditional
install-directory write) versus the guard that only wraps `IsInstalled` at
`SteamLibrarySyncService.cs:207-217`; the class summary at `:11-13` and
`docs/port/p6-003-steam-sync.md:59-61` describe the override as install-state-only.

The model/UI contract is broader. `src/Playnite.Core/Localization/English.json:650` says the
override means "ignore installation state (including installation directory) set by the integration
plugin", and the frozen v10 update path wraps both fields in one guard:
`legacy/source/Playnite/Database/GameDatabase.cs:1282-1295` updates neither `IsInstalled` nor
`InstallDirectory` when `OverrideInstallState` is set. The PR's ledger claims the service "honours
OverrideInstallState" (`docs/port/units/p6-003.ledger.d/180-steam-sync.tsv:2`).

**Concrete failure:** a record has `OverrideInstallState = true`,
`InstallDirectory = "/games/moved/Game"`, `IsInstalled = true`. Discovery finds the app in Steam at
`<library>/steamapps/common/Game`. `Apply` compares the directory at `:195`, overwrites it at `:197`,
and the override branch at `:209` only prevents the `IsInstalled` write. The user's explicit override
and custom path are silently replaced by the provider path; because the app remains installed, the
next sync keeps the provider path, so the user edit is lost rather than transiently stale.

**Required:** when `OverrideInstallState` is set, skip the install-directory write (and any other
install-state-derived field the override is meant to cover), or narrow the documented contract and
the model's UI text — the former is the v10-compatible behaviour and the one the field name/UI
promise. Keep the provider path available in the discovery result, but do not write it over the
record while the override is set.

**Why the tests miss it:** `OverrideInstallState_is_respected_for_both_provider_state_and_removal`
(`tests/Playnite.Core.Tests/Steam/SteamLibrarySyncServiceTests.cs:212-239`) seeds
`InstallDirectory = InstallPath("Alpha")`, the same path the fake discovery supplies, so the update
branch never sees a directory difference. A test that seeds a different path and asserts it survives
would fail today.

## Confirmations (no finding)

1. **Steam is read-only for the sync.** `SteamDiscoverySource` only enumerates and reads through
   `ISteamFileSystem`/`Directory.GetFiles`; it never creates, modifies, mounts or launches. The
   service writes only the supplied `LibraryDatabase`.
2. **One transaction, rollback on failure.** `SteamLibrarySyncService.SyncAsync` does all writes in
   one `database.WriteAsync` callback (`SteamLibrarySyncService.cs:74-175`), and `LibraryDatabase`
   rolls back on exception/cancellation (`src/Playnite.Core/Database/LibraryDatabase.cs:110-136`).
   The empty/unreconcilable discovery case proves the no-open/no-removal path (`:61-65`); the
   database layer owns the mid-write rollback proof.
3. **Identity and imported-record matching are coherent.** Existing records are matched by
   `PluginId == SteamPlugin` plus a canonical numeric `GameId` (`SteamLibrarySyncService.cs:76-92`),
   which matches the v10 fixture's `"220"/"570"/"400"` shape (`tools/V10LibraryFixture/Program.cs:29-33`);
   the deterministic new identity is stable, version/variant-marked, and collision-checked against
   `byId` (`:35-46`, `:107-112`). No `Guid.NewGuid` or other banned logic API is used.
4. **Provider-field and preserved-field behaviour matches the unit doc for the normal path.**
   `Apply` changes only name, install directory, size and install state; `UpdateAsync` writes the
   loaded graph, so sorting name, flags, notes, description, artwork, dates, scores, playtime,
   counts, tags, genres, links, actions, ROMs and `LegacyJson` survive an update. Missing
   `SizeOnDisk` leaves a stored size intact (`:201-205`); removal keeps the directory and user
   fields and never deletes (`:157-169`).
5. **Idempotency holds for unchanged input.** The comparisons are ordinal and the clock is injected;
   the second-sync zero-change assertions in both test classes are meaningful for the current
   branch.
6. **Evidence is a docs-only delta after the validated code head.** The ledger cites run
   `44680509053` on `d3e12f1`; the only commit after it is `877fcb0`, which changes just the ledger
   fragment, so the cited run covers the code/tests/docs under review. The queue shard moves only
   `p6-003` and does not touch the generated aggregates or any protected oracle path.

## Non-blocking observations (P3)

1. **Duplicate extras are mutated on removal, contrary to the identity rule.**
   `docs/port/p6-003-steam-sync.md:39` says extra duplicate records are "never mutated or deleted",
   but the removal loop iterates every `matches` entry at `SteamLibrarySyncService.cs:157-169` and
   sets `IsInstalled = false`/`Modified` on the extras too. No user field is lost, but the doc and
   behaviour need to agree; a test with two records plus a removal would pin the choice.
2. **Library-level parse issues are attributed to an app id.** `SteamDiscoverySource.cs:33-35`
   runs `ParseIdentity(issue.Identity)` for every `SteamParseIssue`, but p5-002 uses the library
   index (e.g. `"1"`) as `Identity` for library-level issues (`SteamLibraryParser.cs:65,93`), so
   `invalid-library-path` for library 1 becomes `SteamSyncIssue("invalid-library-path", 1, "path")`.
   `SteamSyncIssue` is documented as having an optional app id; a library index is not one. This is
   diagnostic misattribution, not library-state corruption.
3. **The root-only comment and predicate disagree.** The comment at
   `SteamDiscoverySource.cs:43-45` says a root-only observation is safe to reconcile, but `:46`
   makes `LibraryFilePath == null` non-authoritative. The current predicate is the safer choice
   (without a library file the external set is unknown), so the comment, not the code, should be
   corrected or the intent stated explicitly.
4. **"RFC 9562 version-5" is not literally true.** `GameIdFor` uses SHA-256
   (`SteamLibrarySyncService.cs:40-45`); RFC 9562 v5 is SHA-1. The id is deterministic and stable,
   and setting the version/variant nibbles makes it look like v5, but the doc sentence at
   `docs/port/p6-003-steam-sync.md:31-32` should say "v5-shaped" or move to a named v8/SHA-256
   derivation if interop with a real v5 generator is ever expected.
5. **Every sync reads the complete game graph.** `session.Games.GetAllAsync()` at
   `SteamLibrarySyncService.cs:76` loads every game and all dependent rows to find Steam records. No
   repository query by provider/app id exists today, so this is not avoidable within the current API,
   but it is O(library) work for a per-sync operation and worth a follow-up query seam.
6. **Manifest enumeration bypasses `ISteamFileSystem`.** `SteamDiscoverySource.cs:67-68` uses
   `Directory.GetFiles` directly, so the enumeration-failure branch cannot be exercised by a fake
   host and the class's injected filesystem does not cover the listing. The production path is still
   read-only and the reader refuses symlinked components; this is a testability/consistency gap.

## Test coverage summary

The add/update/remove/reappear/override/missing-size/skip/duplicate/identity/cancellation cases are
present and read correctly; the real-corpus test is checksum-verified and remaps both recorded paths.
The two P1s are precisely the cases the suite does not exercise: incomplete library observation
(P1-1) and a provider install-directory change while the user override is set (P1-2).

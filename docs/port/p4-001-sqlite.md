# p4-001 — SQLite destination

## Ownership and scope

This unit owns `src/Playnite.Core/Database/` and the destination schema/repository tests.
The importer unit owns reading v10; it must not modify these files without an ADR.
`Playnite.Core` is the **only product project directly referencing Microsoft.Data.Sqlite
11.0.0 and Dapper 2.1.89**, exactly the choices in `05-DEPENDENCIES.md`. No new dependency
choice was introduced. Desktop's project references are unchanged; SQLite flows through
Core, not a Desktop database API/package reference. LiteDB and the importer executable
remain outside Desktop's dependency graph. The importer executable still must not reference
Core; its host is responsible for translating its contract messages into destination writes.

This is a destination, not an importer. Source copying/hashing, Steam fixture extraction,
100% row accounting, dry-run, migration report, settings path mapping, verification of a
temporary imported database and atomic destination swap remain p4-002/p4-003/p4-004 work.
Nothing here reads or writes the v10 source directory. In particular, `OpenAsync` is for a
caller-selected **destination**; it is not permission to open a v10 source database.

## Schema and public surface

Schema version **3**, with committed, forward-only SQL migrations:

1. Version 1 creates games, eleven named metadata tables, ten ordered metadata relations,
   links, actions and ROMs. All persistent writable v10 Game fields have explicit columns;
   playtime, play count and install size retain their full unsigned 64-bit domain.
2. Version 2 adds provider, sort-name, playtime, activity and reverse-relation indexes.
3. Version 3 enforces canonical lowercase 36-character identities on the eleven metadata
   tables and on `game_action.EmulatorId`. The game table already carries that rule as a
   `CHECK`; SQLite cannot add a `CHECK` to an existing table, and the metadata tables are
   foreign-key parents, so version 3 installs a matching BEFORE INSERT/UPDATE trigger pair per
   table instead. A fresh and an upgraded destination therefore carry the identical rule, and
   a non-canonical write fails even when it bypasses the repositories.

`schema_version(version, applied_at)` records each committed step. Every step holds the
SQLite writer lock and commits DDL and its version together. A future or gapped version
history is refused; there is no downgrade or re-accept operation. Reopening a current schema
does not rewrite its history or rows.

- `LibraryDatabase.OpenAsync(absolutePath, cancellationToken)` creates/upgrades a destination.
- `ReadAsync` owns a query-only snapshot transaction; `WriteAsync` owns an immediate write
  transaction. Both close their unpooled connection when the callback finishes, including
  failure/cancellation. Sessions/repositories are serial and callback-scoped, not thread-safe.
- A session exposes no raw connection. Repository SQL goes through transaction-bound session
  helpers, so nothing a caller can reach can skip the transaction, the graph savepoint or the
  callback commit. The importer host uses `LibraryDatabase.ReadAsync`/`WriteAsync` and holds no
  destination `SqliteConnection` of its own.
- `LibrarySession.Games`: `AddAsync`, `GetAsync`, `GetAllAsync`, `UpdateAsync`, `DeleteAsync`,
  plus `FindDanglingEmulatorReferencesAsync(knownEmulators)` for the emulator reference that no
  in-schema foreign key can express. A game graph includes its metadata references, links,
  actions and ROMs. Graph writes use a savepoint, so even a caught child-row failure cannot
  commit a partial replacement.
- `LibrarySession.Metadata<T>()`: the same CRUD surface for Tag, Genre, Company, GameFeature,
  Series, Category, GameSource, AgeRating, Region, CompletionStatus and Platform. Platform's
  specification ID and image fields are explicit columns, not an opaque document.
- `StoredGame` / `StoredMetadata<T>` carry a value and `LegacyJson`, an uninterpreted JSON
  object for unknown fields. Consumers must carry the envelope through updates. Invalid
  objects fail; valid JSON bytes, including unmapped Windows paths, are retained verbatim.

These repositories deliberately do not implement SDK collection events, game-library
services, assets/settings tables or import tracking. Existing parity rows `core-003`,
`core-005` and `import-003` are broader contracts and remain planned: this unit supplies
their destination dependency, not a claim of full parity. No new surface-parity row is needed.

## Boundary contract / oracle references

The oracle is `legacy/source/PlayniteSDK/Models/{Game,GameAction,GameRom,Link,Platform}.cs`,
`legacy/source/Playnite/Database/Collections/{GamesCollection,ItemCollection}.cs` and the
other named metadata model files. Existing ported `Core/Models` retain their model tests.
The database adds an explicit SQL destination rather than copying the LiteDB engine.

| Boundary | Destination behavior / evidence |
| --- | --- |
| Identity | Caller-supplied GUIDs stored in lowercase D form; empty entity IDs rejected. Empty optional source/completion references become SQL NULL and read back as Guid.Empty. Version 3 enforces the lowercase 36-character rule in metadata tables and `game_action.EmulatorId`, not only in `game`. A null provider `GameId` is a legal stored NULL and round-trips as a null model value. |
| Null/empty | Nullable text and dates are distinct from empty text; each nullable collection has an explicit presence bit, retaining null versus empty. |
| Ordering | Relation/link/action/ROM positions preserve insertion order and duplicates. A missing referenced metadata row fails explicitly; no silent reference removal. Metadata deletion is restricted while referenced; deleting a game cascades only its dependent rows. `game_action.EmulatorId` is the one reference this schema cannot constrain: v10 keeps emulators in a separate collection the destination does not store, so there is no parent row to point at. Its identity format is enforced and `Games.FindDanglingEmulatorReferencesAsync` reports ids absent from the caller's source catalogue; the importer report must surface them rather than dropping them. |
| Numbers | UInt64 values are fixed-width, 20-digit invariant decimal TEXT, constrained to the UInt64 domain. This avoids SQLite's signed-integer overflow and retains numeric lexicographic ordering. Scores and unknown enum numeric values are not clamped. |
| Dates | Activity/add/modify/scan dates are UTC ISO-8601 with offset. Unspecified DateTime values are refused pending an explicit source-timezone decision by the caller. Release dates have separate nullable year/month/day columns, preserving partial precision and ReleaseDate.Empty. |
| Paths/encoding | Text is passed as parameters unchanged, including Windows paths, quotes, Unicode, empty strings and null. Path mapping/reporting belongs to the importer, not the destination. |
| Transient state | IsInstalling/IsUninstalling/IsLaunching/IsRunning reset false, matching the v10 GamesCollection initialization callback. Read-only computed model properties are not stored. |
| Errors | Duplicate identities, missing updates, invalid bags and foreign-key failures surface. No INSERT OR IGNORE, replacement inserts, truncation, or exception swallowing. |
| Lifetime/concurrency | FK enforcement, WAL, 5000ms busy timeout on every connection; immediate writer transactions prevent stale read-to-write upgrades. One writer per destination: a `WriteAsync` callback that starts another `WriteAsync` for the same destination fails immediately with `InvalidOperationException` instead of waiting on its own outer writer until the busy timeout; the guard is keyed by destination path, so it covers a second `LibraryDatabase` instance on the same file. A `WriteAsync` inside a `ReadAsync` is allowed and does not join the outer snapshot; a `ReadAsync` inside a `WriteAsync` is allowed and sees only committed state. SQLite's provider executes I/O synchronously even through its async methods; no Task.Run facade in Core. Cancellation is checked at commands and before commit. |

## Verification

`RepositoryTests` exercises complete graph and every metadata kind CRUD against real temp
SQLite files, full-model serialized round trips, source-mapper-backed transient reset,
mapping completeness, unknown-field byte preservation, null/empty and release precision,
UInt64 boundaries/ordering, FK failures, rollback (including a caught graph error), cancellation,
query-only enforcement, connection disposal, competing writers and repeatable snapshots.

`DatabaseContractTests` pins the importer-host contract: no public or non-public property or
method reaches a `SqliteConnection`; nested writes fail fast (same and different instance on the
same file) without consuming the busy timeout and the outer callback rolls back; write-inside-read
and read-inside-write follow the documented rules; every metadata table rejects uppercase and
malformed identities on insert and update; `game_action.EmulatorId` does the same, and
`FindDanglingEmulatorReferencesAsync` reports exactly the ids outside the supplied catalogue; a
null provider `GameId` stores SQL NULL and round-trips.

`SchemaTests` compares fresh and v1-upgraded `sqlite_schema` definitions and preserved rows,
pins reopen idempotence/refusal semantics and tests cancellation rollback. Twelve child-process
SIGKILL cases cover before SQL, after SQL, after version insertion and after commit in each of the
three migrations. Each resumes twice, checks integrity and foreign keys, compares against a fresh
schema, and preserves seeded v1 user data when a later upgrade is killed. This verifies schema
migration crash recovery only; it does not claim importer workflow crash coverage.

CI-backed counts and the two adversarial diff-only reviews are delivery gates. The additive
baseline floor capture is authorized by firstmate steering p4-001/001 and /002 under
`oracle-change`; existing capture records, fixtures and goldens stay intact.

Merged implementation head `7c339cc6373672c79aa83a3782c4864acdfaee81` includes current main's
settings migration. Actual [linux-ci run 43813507631](https://github.com/seanburkes/playnite-linux/actions/runs/43813507631)
passed with **1630 Core / 144 Architecture / 4 Smoke**, zero failures/skips, every MTP exit 0,
independently counted from downloaded JUnit artifacts. This unit contributes 47 database cases;
the pre-unit merged main floor is 1583 Core, not the earlier 1533 floor. The narrow Core Release
build had zero warnings/errors, and the CI warning-as-error build and all five PR checks passed.
The evidence-only floor commit must be revalidated on its own pushed head before readiness.

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
3. Version 3 enforces canonical lowercase hyphenated 36-character identities on the eleven
   metadata tables and on `game_action.EmulatorId`. The game table already carries the weaker
   length/lowercase form as a `CHECK`; SQLite cannot add a `CHECK` to an existing table, and the
   metadata tables are foreign-key parents, so version 3 installs a matching BEFORE
   INSERT/UPDATE trigger pair per table instead. The storable identity is exactly
   `Guid.ToString("D")`, so a stored id is always parseable. Before installing the triggers the
   upgrade scans existing metadata and emulator ids and refuses the upgrade with
   `InvalidDataException` naming the table when one is non-canonical; that rejects corrupt v1/v2
   rows rather than rewriting references or discovering them later in a reader. A fresh and an
   upgraded destination therefore carry the identical rule, and a non-canonical write fails even
   when it bypasses the repositories.

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
  in-schema foreign key can express. That method only ever sees canonical ids: the upgrade
  refuses non-canonical rows and the version 3 triggers reject them afterwards. A game graph includes its metadata references, links,
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
| Identity | Caller-supplied GUIDs stored in lowercase D form; empty entity IDs rejected. Empty optional source/completion references become SQL NULL and read back as Guid.Empty. Version 3 enforces the canonical D form in metadata tables and `game_action.EmulatorId`, not only in `game`, and refuses to upgrade a v1/v2 destination that already holds a non-canonical identity. A null provider `GameId` is a legal stored NULL and round-trips as a null model value. |
| Null/empty | Nullable text and dates are distinct from empty text; each nullable collection has an explicit presence bit, retaining null versus empty. |
| Ordering | Relation/link/action/ROM positions preserve insertion order and duplicates. A missing referenced metadata row fails explicitly; no silent reference removal. Metadata deletion is restricted while referenced; deleting a game cascades only its dependent rows. `game_action.EmulatorId` is the one reference this schema cannot constrain with a foreign key: v10 keeps emulators in a separate collection the destination does not store, so there is no parent row to point at. The mechanism is a trigger plus a report, not an FK: triggers enforce the canonical identity, and `Games.FindDanglingEmulatorReferencesAsync` reports ids absent from the caller's source catalogue; the importer report must surface them rather than dropping them. |
| Numbers | UInt64 values are fixed-width, 20-digit invariant decimal TEXT, constrained to the UInt64 domain. This avoids SQLite's signed-integer overflow and retains numeric lexicographic ordering. Scores and unknown enum numeric values are not clamped. |
| Dates | Activity/add/modify/scan dates are UTC ISO-8601 with offset. Unspecified DateTime values are refused pending an explicit source-timezone decision by the caller. Release dates have separate nullable year/month/day columns, preserving partial precision and ReleaseDate.Empty. |
| Paths/encoding | Text is passed as parameters unchanged, including Windows paths, quotes, Unicode, empty strings and null. Path mapping/reporting belongs to the importer, not the destination. |
| Transient state | IsInstalling/IsUninstalling/IsLaunching/IsRunning reset false, matching the v10 GamesCollection initialization callback. Read-only computed model properties are not stored. |
| Errors | Duplicate identities, missing updates, invalid bags and foreign-key failures surface. No INSERT OR IGNORE, replacement inserts, truncation, or exception swallowing. |
| Lifetime/concurrency | FK enforcement, WAL, 5000ms busy timeout on every connection; immediate writer transactions prevent stale read-to-write upgrades. One writer per destination: a `WriteAsync` callback that starts another `WriteAsync` for the same destination fails immediately with `InvalidOperationException` instead of waiting on its own outer writer until the busy timeout, and that rule includes a write started in the background from inside the callback, fire-and-forget included. The guard key is the canonical absolute path with each directory component and the file resolving their symlink target, so `/tmp/x/./library.sqlite`, `../x/library.sqlite` and a symlink share one key; a hard-link alias is not detected and is backstopped by the SQLite busy timeout rather than failing fast, because hard links have no managed identity API and no inode-level detection is attempted. A `WriteAsync` inside a `ReadAsync` is allowed and does not join the outer snapshot; a `ReadAsync` inside a `WriteAsync` is allowed and sees only committed state. SQLite's provider executes I/O synchronously even through its async methods; no Task.Run facade in Core. Cancellation is checked at commands and before commit. |

## Verification

`RepositoryTests` exercises complete graph and every metadata kind CRUD against real temp
SQLite files, full-model serialized round trips, source-mapper-backed transient reset,
mapping completeness, unknown-field byte preservation, null/empty and release precision,
UInt64 boundaries/ordering, FK failures, rollback (including a caught graph error), cancellation,
query-only enforcement, connection disposal, competing writers and repeatable snapshots.

`DatabaseContractTests` pins the importer-host contract: no public or non-public property or
method reaches a `SqliteConnection`; nested writes fail fast for the same instance, a second
instance and a different spelling (`./`, `../` and a symlink) of the same file without consuming
the busy timeout, and the outer callback rolls back; a background write started inside a write
callback fails fast; write-inside-read and read-inside-write follow the documented rules; every
metadata table rejects uppercase, malformed and 36-character non-hex identities on insert and
update; `game_action.EmulatorId` does the same, and `FindDanglingEmulatorReferencesAsync` reports
exactly the ids outside the supplied catalogue; a null provider `GameId` stores SQL NULL and
round-trips.

`SchemaTests` compares fresh and v1-upgraded `sqlite_schema` definitions and preserved rows,
pins reopen idempotence/refusal semantics, refuses an upgrade holding a non-canonical metadata or
emulator id without changing its version history or rows, and tests cancellation rollback. Twelve
child-process
SIGKILL cases cover before SQL, after SQL, after version insertion and after commit in each of the
three migrations. Each resumes twice, checks integrity and foreign keys, compares against a fresh
schema, and preserves seeded v1 user data when a later upgrade is killed. This verifies schema
migration crash recovery only; it does not claim importer workflow crash coverage.

CI-backed counts and the two adversarial diff-only reviews are delivery gates. The additive
baseline floor capture is authorized by firstmate steering p4-001/001 and /002 under
`oracle-change`; existing capture records, fixtures and goldens stay intact.

The merged #90 final head was `83a8e338f7e8afa90306567362f77e22226f2e96`; its own
[linux-ci run 43814054645](https://github.com/seanburkes/playnite-linux/actions/runs/43814054645)
revalidated **1630 Core / 144 Architecture / 4 Smoke** there, zero failures/skips, every MTP exit
0. `p4_001_capture` cites the earlier implementation head `7c339cc` and run 43813507631; that
record is retained as history, and this follow-up records the final-head provenance in its own
capture record. The pre-unit merged main floor for #90 was 1583 Core.

This review follow-up measured **1669 Core / 144 Architecture / 4 Smoke** on
[linux-ci run 43819539551](https://github.com/seanburkes/playnite-linux/actions/runs/43819539551)
at final head `0b01b99a8dbfebbd5f711bf92f345f65cc217742`, independently counted from the
downloaded JUnit testcase elements, `test-counts.json` and MTP exit files (all exit 0, zero
failures/skips). Current main's floor is 1642 Core; the 27 added cases are 18
destination-contract cases, four additional schema version 3 SIGKILL checkpoints, and five
Review B cases. Earlier runs in this PR (`43816863124` at `f05ca56` and `43819146506` at
`652389a`) were superseded by later fixes. The capture-citation commit changes only
`tests/baseline.json`, this document and the p4-001 queue/ledger shards, so its own pushed head's
linux-ci run revalidates the same 1669 cases.

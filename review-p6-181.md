# p6-004 review — UI-free settings service

- PR: https://github.com/seanburkes/playnite-linux/pull/181
- Reviewed head: `40335bc2293d287447606575b47849658301c48a` (`feat(p6-004): add the UI-free settings service`)
- Base: `423b501fac07058e04fd320384a78ed0de7e9db2`
- Candidate diff: `/home/sean/src/firstmate/data/p6-181/candidate.diff`, SHA-256 `b0967494d8fed98236ddf37dc5068319f9a962f9c1b0b1439fa3c0f77a6f3902`; `git diff 423b501 40335bc` reproduces it byte-for-byte (6 files, +1427/-1).
- Method: static review of the diff and the p4-004 APIs it calls. No repository build or test execution; the 21 new tests were read, not run.

## Verdict

**No P1. Three blocking P2s before the unit can be recorded `done`:** the head has no
forge-verifiable final-head run, the typed key contract cannot read several v10 properties its
own documentation says are supported (and range errors escape as raw exceptions), and
Newtonsoft-lenient envelopes/sources that `SettingsMigration.ExportV10` rejects with
`System.Text.Json` escape as raw `JsonReaderException` rather than `SettingsException`. The
normal-path data-preservation, atomic-write and event design is sound; the P2s are malformed-input,
API-surface and evidence gaps, not ordinary-run data loss.

## P2-1 — No final-head run citation; the queue row is already `done`

**Location:** `docs/port/p6-004-settings-service.md:93-94` and
`docs/port/units/p6-004.queue.tsv:2`.

The only evidence is the prose "Local Release checks on this branch: Core 2704 passed,
Architecture 199 passed, zero failed/skipped; build 0 warnings / 0 errors." There is no head SHA,
no linux-ci run URL and no `docs/port/units/p6-004.ledger.d/<pr>-<slug>.tsv` fragment in the diff.
The p6-002 and p6-003 units were accepted with a ledger fragment naming the validated head and the
GitHub Actions run; p6-003's own row is still `in-progress` while its PR is open. This PR moves
`p6-004` to `done` without the attributable run.

**Concrete failure:** the merge authority cannot reproduce which tree the 2704/199/21-test claim
belongs to; a later force-push or a locally modified test file would leave the row `done` with no
forge record. The candidate diff is the implementation commit itself (no docs-only evidence commit
follows it), so there is no validated-head delta to point at either.

**Required:** add the final-head CI run (head SHA + run URL + test counts, including the 21 new
cases) as a `p6-004.ledger.d` fragment, or keep the row `in-progress` until that run is cited. The
doc's local prose is useful but is not the project's evidence record.

## P2-2 — Typed validation does not cover the documented key surface and lets range errors escape

**Locations:** `src/Playnite.Core/Settings/SettingsKey.cs:67-68,107-112,123-132`;
`src/Playnite.Core/Settings/SettingsService.cs:107-118,386-390,423-431`.

The doc promises that "the key type is extensible for a known v10 root property"
(`docs/port/p6-004-settings-service.md:46-47`), but `SettingsKey<T>.ValidateToken` only recognizes
`bool`, `int`, `long`, `string` and enums (`SettingsKey.cs:107-112`). Every other v10 root type gets
`SettingsFailure.InvalidValue` on `Get` even when the value present is valid. Two concrete cases:

1. **Extensibility is false for most of the v10 graph.** `GridItemWidth`, `GridItemHeight`,
   `FontSize`, `DetailsVisibility`, `DisabledPlugins`, etc. are listed in
   `V10SettingsProperties.Names`, but `new SettingsKey<double>("GridItemWidth", 200)` cannot read a
   present value: `ValidateToken` falls through to `false` at `SettingsKey.cs:112` and `Get` throws
   InvalidValue at `SettingsService.cs:117`. A future unit that follows the doc's "extensible" rule
   for a property such as `GridItemWidth` or `FontSize` gets a broken key.
2. **Present integers are not range-checked.** `Version: 2147483648` in `settings` or `legacy` is
   `JTokenType.Integer`, so `ValidateToken` passes at `SettingsKey.cs:108`; `Get(Version)` then
   calls `ToObject<int>()` at `SettingsService.cs:118` and throws a raw `OverflowException`
   (verified with `JToken.ToObject<int>`). A `schemaVersion` of `2147483648` likewise passes the
   "integer" check at `SettingsService.cs:380` and throws `OverflowException` at
   `SettingsService.cs:386` instead of `SettingsException(UnsupportedSchema/Corrupt)`. A huge enum
   value reaches `token.Value<long>()` at `SettingsKey.cs:130` and throws `InvalidCastException`
   during `LoadAsync`. These are corrupt-input paths that must fail with a stable
   `SettingsException`, not an unhandled BCL exception on a typed read.

**Required:** either narrow the doc to the five supported scalar shapes (and keep "extensible"
scoped to them), or add `double`/`float`/`DateTime`/collection validation. Range-check `int`/`long`
before `ToObject<T>()`, validate enums without an unchecked `Value<long>()` cast, and map
`OverflowException`/`InvalidCastException`/`FormatException` from typed reads to
`SettingsFailure.InvalidValue`. Add cases for an out-of-range `Version`, an out-of-range
`schemaVersion` and a `double`/custom key.

## P2-3 — Strict `System.Text.Json` failures escape as raw exceptions

**Locations:** `src/Playnite.Core/Settings/SettingsService.cs:353-360,412-421`
(`LoadAsync`/`UpdateAsync` envelope path) and `:295-309` (migration path);
`src/Playnite.Core/Settings/SettingsStorage.cs` is not involved.

The service parses the envelope with Newtonsoft, which accepts a UTF-8 BOM, comments and trailing
commas (the adapter explicitly trims the BOM at `SettingsService.cs:353`). It then calls
`SettingsMigration.ExportV10(bytes)` on the **raw** bytes at `:414`; `ExportV10` parses the envelope
with `System.Text.Json`, which rejects all three. The catch filter at `:416` names
`JsonException`, which in this file resolves to `Newtonsoft.Json.JsonException` (the only
`using Newtonsoft.Json;`; there is no `using System.Text.Json;`), so
`System.Text.Json.JsonReaderException` is not caught. The same omission is in
`MigrateFromV10Async` at `:295`: a v10 source with a comment or trailing comma passes the
migration's Newtonsoft `ReadV10` but is rejected by its `Utf8JsonReader` scan with
`System.Text.Json.JsonReaderException`, which escapes the migration unwrapped.

**Concrete failure:** write a valid migrated envelope to the store with a leading UTF-8 BOM (or a
trailing comma), then call `LoadAsync`. `Deserialize` parses it with Newtonsoft, then
`ExportV10`/`JsonDocument.Parse` throws `System.Text.Json.JsonReaderException`; the caller's
`catch (SettingsException)` does not see it, and `SettingsFailure` is never populated. A raw v10
config with a comment passed to `MigrateFromV10Async` fails the same way. The doc promises every
failure carries one of the stable categories (`docs/port/p6-004-settings-service.md:61-62`).

**Required:** either normalize/reject the envelope once with the same parser used by `ExportV10`
before the second pass, or catch `System.Text.Json.JsonException` explicitly in both the load/update
and migration paths and map it to `SettingsFailure.Corrupt` (the existing `InvalidDataException`
catch already covers the migration's other validation failures). Add a BOM, comment and
trailing-comma case to `SettingsServiceTests`.

## Confirmations (no finding)

1. **Unknown and legacy data is preserved.** `BuildReplacement` derives the archived v10 graph from
   the authoritative `ExportV10` bytes, mutates only requested known names, removes an updated name
   from `legacy`, and rebuilds `settings` as `archived` minus every remaining legacy name
   (`SettingsService.cs:436-484`). The top-level envelope is cloned, the report is cloned, and
   `ExportV10` re-validates the resulting union before the write. The real-fixture and
   unknown/legacy tests exercise the preservation rules.
2. **Raw v10 is an explicit boundary, and corrupt/unsupported files never overwrite.** `Deserialize`
   maps a missing `schemaVersion` plus a `Version` property to `LegacyShape`
   (`SettingsService.cs:368-378`), a non-1 integer to `UnsupportedSchema` (`:380-390`), and
   `ExportV10`/typed failures to `Corrupt`/`InvalidValue`; `UpdateAsync` and
   `MigrateFromV10Async` both call it before any write. The corrupt, raw-v10, unsupported and
   wrong-type tests assert the file bytes are unchanged.
3. **Atomic writes are sibling-temp plus rename with failure cleanup.** `SettingsFileStorage`
   creates a unique `.<name>.<random>.tmp` in the destination directory, writes, flushes to disk and
   closes it, then renames with overwrite (`SettingsStorage.cs:63-100`); on an IO/type failure it
   maps to `WriteUnavailable` and the `finally` deletes the stage (`:101-115`). The blocked-target
   and unwritable-directory tests assert `WriteUnavailable`, an unchanged target/marker and no
   `*.tmp`.
4. **Typed defaults match the frozen v10 initializers.** `PlayniteSettingsKeys` defaults match
   `legacy/source/Playnite/Settings/PlayniteSettings.cs`: `Version 7` (`:312`),
   `StartInFullscreen false` (`:941`), `StartMinimized false` (`:1241`), `MinimizeToTray false`
   (`:1096`), `CloseToTray true` (`:1111`), `EnableTray true` (`:1126`), `ShowSidebar true`
   (`:1046`), `FirstTimeWizardComplete false` (`:714`), `Language "english"` (`:1142`),
   `Theme ThemeManager.DefaultDesktopThemeId` (`:1188`/`Themes.cs:29`), `DatabasePath null`
   (`:956`), `InstallInstanceId null` (`:1220`), `AutoBackupEnabled false` (`:2216`),
   `AutoBackupFrequency OnceAWeek` (`:2219`), `CheckForProgramUpdates OnEveryStartup` (`:2141`).
5. **Subscribe/notify ordering is correct on the normal path.** `UpdateAsync` and
   `MigrateFromV10Async` set `notification` only after a successful write and invoke `Changed` after
   the `finally` releases the gate (`SettingsService.cs:238-250,310-320`), so a handler may call
   back into the service. No-op/empty updates and failed writes raise no event (`:224-237`), and
   `Current` is only replaced after a successful load/write/no-op. The tests cover the one-event,
   no-event and event-after-write cases.
6. **Tests are fake-host plus real-fixture with no skips or sleeps.** The 21 `[Fact]` cases cover
   every public operation listed in the doc, use `FakeStorage` for the contract and the copied
   `golden/settings/config.json` for the real envelope; there is no `[Skip]`, `Sleep` or `Delay`.
   No Avalonia/UI, network, process or Windows API appears in the new files.
7. **Scope is clean.** The diff adds one Core service, its storage adapter, keys and tests, plus the
   unit doc and its own shard row; `tests/golden/settings` is read, not modified, and no protected
   oracle, baseline, aggregate or schema path is touched.

## Non-blocking observations (P3)

1. **Missing-store creation does not persist the documented defaults.** The doc says a missing
   store is "created from the documented defaults plus the requested changes"
   (`docs/port/p6-004-settings-service.md:74`) and the test is named
   `Update_creates_a_store_from_documented_defaults`, but `BuildReplacement` starts from an empty
   `archived` graph when `SourceBytes` is null (`SettingsService.cs:439`), so the raw `settings`
   contains only the requested key; `Version`, `CloseToTray` and the others are virtual defaults.
   The typed reads agree, but a raw consumer of `Settings`/the store sees a sparse object. Either
   persist the 15 defaults on creation or state that defaults are virtual everywhere.
2. **Symlink reads are allowed while symlink writes are rejected.** `SettingsFileStorage.ReadAsync`
   simply calls `File.ReadAllBytesAsync(Location)` (`SettingsStorage.cs:40-60`) and has no
   `RejectSymbolicLinks`; `WriteAtomicAsync` rejects the path (`:74-75,118-127`). A store file that
   is a symlink therefore loads but every update fails `WriteUnavailable`, contrary to the
   unqualified "Symbolic-link paths are rejected" in the doc (`p6-004-settings-service.md:84`).
3. **Migration stages in the system temp directory with default permissions.** `SettingsService.cs:265`
   creates `/tmp/playnite-settings-<random>` and `MigrateAsync` writes the v10 graph there; the
   directory/file inherit the process umask (typically 0755/0644), so another local user can read
   the staged settings during the migration. The p4-004 destination stage sits in the user's config
   directory instead. Use a private temp root/mode or stage beside the destination.
4. **A migration source directly in the system temp directory cannot be migrated.** The service
   stages under `Path.GetTempPath()`, while `SettingsMigration.MigrateAsync` refuses a destination
   inside the source directory (`SettingsMigration.cs:43-51`). For `/tmp/config.json` the staged
   `/tmp/playnite-settings-*/settings.json` is inside `/tmp` and the migration fails `InvalidValue`.
5. **`MigrateFromV10Async` accepts an already-migrated envelope as a "v10 config" and nests it.**
   The staged `settingsUtf8` would be the old envelope, its settings disappear into `legacy`, and
   every typed key reads its default. The doc says the entry point is for a raw v10 config; a
   `schemaVersion`/`settingsUtf8` source should be refused (`Corrupt`/`InvalidValue`) rather than
   silently re-wrapped.
6. **`SettingsKey<T>.AllowsNull` is true for non-nullable reference types** because
   `!typeof(T).IsValueType` cannot distinguish `string` from `string?` (`SettingsKey.cs:67-68`).
   A caller-created `SettingsKey<string>` can therefore write JSON null and `Get<string>` can
   return null, contrary to the non-nullable `T`.
7. **Concurrent writers can silently lose an update.** The service gate is per-instance and the
   read-then-write has no compare-and-swap (`SettingsService.cs:217-237`); the storage write
   overwrites. The XML summary claims "a corrupt or concurrently changed file is never overwritten
   silently" (`SettingsService.cs:144-148`), and the migration does a non-atomic
   exists-then-`File.Move(overwrite:true)` (`:270-282`). If the product guarantees a single writer,
   say so; otherwise add a storage version/CAS or a cross-process lock.
8. **Migration write IO is categorized as `Unreadable` for non-file storages.**
   `MigrateFromV10Async` wraps both `SettingsMigration.MigrateAsync` and
   `storage.WriteAtomicAsync` in the same `IOException` catch (`SettingsService.cs:300-305`); the
   production file storage throws `SettingsException(WriteUnavailable)` and is rethrown correctly at
   `:287-290`, but any other `ISettingsStorage` that throws a raw `IOException` on write is reported
   as a read failure.
9. **`ChangedProperties` is empty for a migration** (`SettingsService.cs:285`) even though the whole
   store was written; a subscriber cannot distinguish "migration" from "no property changed"
   without inspecting `Snapshot.Exists`/comparing. Document the sentinel or populate the changed
   root names.
10. **`SettingsFileStorage.WriteAtomicAsync` does not fsync the directory** after the rename
    (`SettingsStorage.cs:89`); the rename is atomic within the running process but not crash-durable
    without a directory fsync. The doc's "atomic" claim is accurate for visibility, not for power
    loss.

## Test coverage summary

The 21 cases are strong on the normal contract: missing store/defaults, real-fixture round-trip,
full-fixture preservation, unknown/legacy preservation, legacy move, no-op/empty, corrupt/raw-v10/
unsupported/wrong-type refusal, read/write failure, atomic-write failure, migration and refusal,
migration validation failure, cancellation and disposal. The P2s above are precisely the untested
edges: the final-head run record, a double/custom key and out-of-range integer/enum, and
BOM/comment/trailing-comma inputs that are lenient for Newtonsoft but strict for
`System.Text.Json`.

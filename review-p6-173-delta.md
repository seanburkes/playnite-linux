# Review — PR https://github.com/seanburkes/playnite-linux/pull/173 (delta)

- Unit: `p6-002` (Import service)
- Delta range: `773c2534a742b4a52100f9c7cc12f6ad16211d0d..58e48cd5d4d586ae48677e6ba41d36edf546d656`
- Head: `58e48cd5d4d586ae48677e6ba41d36edf546d656`
- Base: `7b4c9603e4a93b70ab78bab65a1ff1cede9960c5` (`main`)
- Delta diff: `/home/sean/src/firstmate/data/p6-173/delta.diff`
- Delta diff SHA-256: `eb98cf4487b5a546af6de74475a74da2a70e9281309662687aa49d6cfb93fde5`
- Diff identity: byte-for-byte `git diff 773c2534..58e48cd` (4 files, +54/−0)
- Verdict: **PASS — no blocking P1/P2 findings**

## Scope

This review covers only the delta from the previously reviewed head `773c2534` to
`58e48cd`; the full PR was reviewed at `773c2534` in
`review-p6-173.md`. The delta delivers the two requested P2 fixes on the same branch:

- `74e77f6` — `fix(p6-002): classify access and JSON reader failures` (service + tests).
- `58e48cd` — `docs(p6-002): cite P2 fix validation run` (ledger fragment only, 1 file).

## 1. Access failures map to `DestinationUnavailable`

- `src/Playnite.Core/Import/LibraryImportService.cs:205-208` adds
  `catch (Exception exception) when (exception is UnauthorizedAccessException or
  System.Security.SecurityException)` → `LibraryImportFailure.DestinationUnavailable`.
  This is the correct catch type: `UnauthorizedAccessException` derives from
  `SystemException`, not `IOException`, so the existing `catch (IOException)` at `:201`
  never covered it (`AssetCopy.cs:58` and `:123` already catch both explicitly for the
  same reason).
- The first write the migration performs is
  `src/Playnite.Core/Import/LibraryMigration.cs:124`,
  `new FileStream(destination + ".import.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
  FileShare.None)`. On a destination directory without write permission this is the exact
  `EACCES` path and .NET raises `UnauthorizedAccessException`. Nothing earlier in
  `RunAsync` writes: the path checks (`:34-46`), `RequirePlainDirectory` (`:318-323`, read
  plus `lstat` on a searchable directory), JSON load (`:49`) and in-memory transform
  (`:52-115`) do not need directory write permission.
- `SecurityException` is caught with it. Neither type can shadow an existing category: the
  `LibraryImportException` passthrough (`:183`) and `InvalidDataException`/`ArgumentException`/
  `IOException` catches (`:187-204`) all precede it and none of their hierarchy includes
  `UnauthorizedAccessException`.
- Destination-side `UnauthorizedAccessException` from staged database creation or asset
  writes therefore now reaches callers as
  `LibraryImportException(LibraryImportFailure.DestinationUnavailable)` instead of a raw
  exception, which is what the service contract documents at `:87-88`.

## 2. Newtonsoft parse failures map to `SourceRejected`

- `src/Playnite.Core/Import/LibraryImportService.cs:192-196` adds
  `catch (Newtonsoft.Json.JsonException)` → `LibraryImportFailure.SourceRejected`.
- The envelope is parsed by `JObject.Load(reader, …)` at
  `src/Playnite.Core/Import/LibraryMigration.cs:49`. An array root (`[]`), a non-object
  root, malformed JSON (`{`), a duplicate property (the settings request
  `DuplicatePropertyNameHandling.Error`) or excessive depth all raise
  `Newtonsoft.Json.JsonReaderException` / `JsonSerializationException`, both subclasses of
  `JsonException`, before any destination artifact is created.
- The new catch does not intercept the row-transform JSON handling: that block already
  catches `JsonException` and wraps it in `InvalidDataException`
  (`LibraryMigration.cs:96-99`), which keeps its existing `SourceRejected` mapping at
  `LibraryImportService.cs:187-191`. The catch ordering at `:187` then `:192` is therefore
  correct and no previously classified path changes category.

## 3. The added tests genuinely fail without the mappings

- Unwritable-destination test (`tests/Playnite.Core.Tests/Import/LibraryImportServiceIntegrationTests.cs:71-91`):
  sets the destination directory to mode `0555` and calls the real service over the real
  migrator. The first write is the lock file at `LibraryMigration.cs:124`, so with the new
  catch removed `UnauthorizedAccessException` propagates out of `ImportAsync` and
  `Assert.ThrowsAsync<LibraryImportException>` fails; the `Failure` and
  `InnerException` assertions are secondary. The directory mode is restored in `finally`,
  and `Assert.False(File.Exists(destination))` holds because the failure precedes any
  destination creation.
- Contract case (`tests/Playnite.Core.Tests/Import/LibraryImportServiceTests.cs:130-154`,
  new row `"access"` at `:135`): a fake migrator throws
  `UnauthorizedAccessException`; without the catch the raw exception escapes and the
  `Assert.ThrowsAsync<LibraryImportException>` fails, so this row is mapping-dependent on
  its own.
- Malformed/array-root theory (`LibraryImportServiceTests.cs:78-93`): `"[]"` and `"{"`
  through the real `LibraryMigrator` raise `JsonReaderException` at `LibraryMigration.cs:49`.
  Without the `JsonException` catch the theory fails at `ThrowsAsync<LibraryImportException>`;
  the `InnerException` assertion additionally pins the cause to `JsonException` rather than
  a pre-wrapped `InvalidDataException`, so it cannot pass through the older mapping.
- Case-count consistency: +5 test cases (1 integration fact, 2 theory rows for the envelope,
  2 theory rows for the categories) matches the ledger's Core `2660` → `2665` claim.

## 4. No other import behaviour changed

- The code hunk is additive only: two `catch` clauses, nothing reordered, renamed or
  removed in `LibraryImportService.cs`. Public surface (`LibraryImportRequest`,
  `LibraryImportResult`, `LibraryImportFailure`, `ILibraryImportReader`), checkpoint
  mapping (`StageFor`), the empty-envelope guard and the post-migration
  `PublicationFailed` check are untouched by the delta.
- The only other delta file is the unit ledger fragment
  `docs/port/units/p6-002.ledger.d/173-import-service.tsv`, which appends one evidence row
  with the same six columns as the existing rows; aggregates (`queue.tsv`, `ledger.tsv`)
  are not touched. `58e48cd` itself is a one-file docs delta, so the code reviewed here is
  exactly the code validated at `74e77f6`.
- No protected path, baseline, fixture or other unit is touched; no banned API is
  introduced.

## 5. Forge and CI evidence

- `gh-axi pr view 173` during this review: open, `draft: no`, merge state clean, head
  `58e48cd5d4d586ae48677e6ba41d36edf546d656`, 8 checks passed / 0 failed.
- `gh-axi run view 44677202408` (the run cited in the new ledger row): `linux-ci`,
  `completed`, `conclusion: success`, branch `fm/p6-002`. This matches the ledger's
  `ci_run=44677202408` and validation head `74e77f6dc812084f3053851baf10945d85a6fcb4`,
  which is an ancestor of, and the only code-bearing commit behind, the reviewed head.
- `58e48cd` carries the `Port-Unit: p6-002` trailer.

## Non-blocking observations

- The reader-side catch at `src/Playnite.Core/Import/LibraryImportService.cs:157` still
  covers only `IOException`, `InvalidDataException` and `ArgumentException`, so an
  `ILibraryImportReader` that throws `UnauthorizedAccessException` directly would still
  escape unclassified. No in-repo reader does: `LegacyLibraryReaderProcess.cs:31,50,56,82`
  wraps every failure as `LibraryImportException(SourceReadFailed)`. Recorded for a follow-up
  hardening pass, not a failure of this delta's stated scope.
- The `0555` test depends on the process lacking `CAP_DAC_OVERRIDE`; the Linux CI runner is
  a normal user. Like the existing `SteamDiscoveryTests` permission case it is not
  root-guarded, and `[SupportedOSPlatform("linux")]` is an analyzer annotation rather than a
  runtime skip, so a non-Linux local run would throw `PlatformNotSupportedException`. CI is
  Linux-only, so this does not block.

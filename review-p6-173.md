# p6-173 review

- PR: https://github.com/seanburkes/playnite-linux/pull/173
- Range: `7b4c960..773c2534`
- Head: `773c2534a742b4a52100f9c7cc12f6ad16211d0d` (`docs(p6-002): record import service evidence`)
- Code head: `af6180b65b7a23b44179f558492c75f8caa6eba4` (`feat(p6-002): add import service over verified migration`); `git diff --stat af6180b 773c2534` is the one ledger fragment, +2 lines.
- Diff: `/home/sean/src/firstmate/data/p6-173/candidate.diff`, sha256 `39bfbf51ff06b939750d3a029b92c42c36f81a79362f16137ed6ba7dd415afd2`
- Diff identity: `git diff 7b4c960 773c2534` reproduces that file byte-for-byte (8 files, +923/-1).

## Verdict

No blocking P1/P2 findings. The service composes the existing verified migration instead of
duplicating it, the new CLI `import` surface is reachable without touching `import-v10` or
`Program.cs`, the failure/cancellation claims are backed by tests that ran on the cited CI head,
and the evidence shard reproduces. Non-blocking observations (P3) are listed at the end.

## Confirmations

### 1. The service delegates to `LibraryMigration` and claims publication only after it returns

`src/Playnite.Core/Import/LibraryImportService.cs:174-177` passes the envelope, source,
destination, `DryRun`, the mapped checkpoint callback and `ReplaceExisting` straight to
`LibraryMigration.RunAsync`, whose parameter list is identical (`LibraryMigration.cs:31-32`); the
internal `LibraryMigrator` wrapper (`LibraryImportService.cs:236-241`) adds nothing. No staging,
verification or swap logic is reimplemented. `Published` is returned only when `!DryRun` and the
destination exists after the call (`LibraryImportService.cs:201-207`); dry runs return `Planned`
and never write (`LibraryImportService.cs:207`, integration test
`LibraryImportServiceIntegrationTests.cs:54-68`).

### 2. Re-run refusal and the classified failure surface are real

Reader failures become `SourceReadFailed` (`LibraryImportService.cs:157-161`),
`InvalidDataException`/`ArgumentException`/`IOException` from the migration map to
`SourceRejected`/`InvalidRequest`/`DestinationUnavailable`
(`LibraryImportService.cs:187-199`), and the migration's receipt refusal
(`LibraryMigration.cs:285`) surfaces as `DestinationUnavailable` with the original `IOException`
(`LibraryImportServiceIntegrationTests.cs:70-85`, asserting the inner type and unchanged bytes).

### 3. Cancellation is propagated, not converted into a result

`LibraryImportService` checks the token before reading and between phases
(`LibraryImportService.cs:140-141,169`), rethrows reader/migration cancellation unwrapped
(`:149-156,179-186`), and never returns a result on it. The reader process is killed on reader
cancellation and the `OperationCanceledException` rethrown
(`src/Playnite.Cli/LegacyLibraryReaderProcess.cs:62-76`). The integration theory cancels at
`Read`/`Copied`/`Written`/`Verified`/`BeforeSwap`, asserts no live destination or report, and
proves a clean rerun (`LibraryImportServiceIntegrationTests.cs:87-112`).

### 4. CLI surface is reachable and the p4-002 surface is untouched

`ImportLibraryCommand` implements `ICliCommand` with the name `import`
(`src/Playnite.Cli/Commands/ImportLibraryCommand.cs:15-17`) and is discovered by the existing
reflection registry (`CliCommandRegistry.cs:17-36`), so no shared dispatch edit is needed. Exit
codes 2 (usage) and 4 (service failure) are covered by
`tests/Playnite.Core.Tests/Cli/ImportLibraryCommandTests.cs:35-70`; the diff never touches
`ImportCommand.cs` (`import-v10`) or `Program.cs`, matching the launch brief's p6-002 scope
(`docs/port/p6-launch-brief.md:31,68`).

### 5. Evidence and shards reproduce

- Cited run 44672707824 is `linux-ci` on `af6180b65b7a23b44179f558492c75f8caa6eba4`, completed
  success, pull_request event.
- Downloaded `linux-test-results`: `test-counts.json` reports Core 2660/2660, Architecture
  199/199, Smoke 4/4, zero failed/skipped; every `exit-code.txt` is 0. The trx contains all 24
  new test cases (12 unit + 9 integration + 3 CLI).
- Head `773c2534` has its own successful run 44673162330, and the PR shows 8/8 checks passing.
- `docs/port/units/p6-002.queue.tsv` moves only `p6-002` `todo`→`done`, aligning the dependency
  with the 2026-11-20 brief (`p6-001,p4-003`), and adds one fragment
  `docs/port/units/p6-002.ledger.d/173-import-service.tsv` whose claims match the tests above.
  `docs/port/queue.tsv`, `docs/port/ledger.tsv`, `tests/baseline.json` and the protected oracle
  paths are untouched; `unit-ownership`, `unit-evidence`, `inventory-and-parity` and the port-index
  guard pass on the PR.

## Non-blocking observations (P3)

1. **Post-publication failures are classified as source rejection.**
   `LibraryImportService.cs:187-191` maps every `InvalidDataException` from the migration to
   `SourceRejected`, including failures that are not source validation: the SQLite integrity check
   (`LibraryMigration.cs:213`) and the post-swap receipt check (`LibraryMigration.cs:249-250`).
   Concrete case: force the receipt check to fail after the rename; the
   migration rolls back (`LibraryMigration.cs:254-266`) and the caller receives
   `LibraryImportFailure.SourceRejected` for a publication fault, even though the source is intact.
   A later refinement could split by phase or widen `PublicationFailed`.

2. **Reader kill race can replace cancellation with `InvalidOperationException`.**
   `LegacyLibraryReaderProcess.cs:68-71` checks `HasExited` and then calls
   `Process.Kill(entireProcessTree: true)`; if the child exits in that window, `Kill` throws
   `InvalidOperationException`, which is neither the rethrown `OperationCanceledException` nor a
   `LibraryImportException`. Wrapping the kill in `try/catch (InvalidOperationException)` would keep
   the cancellation contract.

3. **`LibraryImportStage` is documented "in migration order" but is not.**
   `LibraryImportService.cs:13` claims migration order, yet `AssetPartialCopy` and `AssetFlushed`
   are emitted inside `AssetCopy` (`AssetCopy.cs:105,114`) before the migration reports `Copied`
   (`LibraryMigration.cs:172`); the enum lists `Copied` first
   (`LibraryImportService.cs:25-32`). Only a doc/ordering nit; the name-based mapping is correct.

4. **The real reader's cancellation and missing-assembly branches have no test.**
   `LegacyLibraryReaderProcess.cs:29-33` (missing assembly) and `:62-76` (kill on cancellation)
   are unexercised; the cancellation test uses the fake reader
   (`LibraryImportServiceTests.cs:152-173`). Severity is low because the branch is small, but the
   unit claims cancellation safety.

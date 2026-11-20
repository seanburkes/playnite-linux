# Review — PR https://github.com/seanburkes/playnite-linux/pull/173

- Unit: `p6-002` (Import service)
- Head: `773c2534a742b4a52100f9c7cc12f6ad16211d0d`
- Base: `7b4c9603e4a93b70ab78bab65a1ff1cede9960c5` (`main`)
- Candidate diff: `/home/sean/src/firstmate/data/p6-173/candidate.diff`
- Candidate diff SHA-256: `39bfbf51ff06b939750d3a029b92c42c36f81a79362f16137ed6ba7dd415afd2`
- Diff identity: byte-for-byte `git diff 7b4c960..773c253` (8 files, +923/−1)
- Verdict: **Changes requested — 2 blocking P2 findings**

## Verification performed

- Read the complete candidate diff and matched it byte-for-byte against
  `git diff 7b4c9603e4a93b70ab78bab65a1ff1cede9960c5..773c2534a742b4a52100f9c7cc12f6ad16211d0d`.
- `gh-axi pr view 173`: open, not a draft, merge state clean, 8 checks passed.
- CI run `44673162330` (`linux-ci`) passed on the reviewed head `773c2534...`; its
  `linux-test-results` artifact reports Core 2660/2660, Architecture 199/199 and Smoke 4/4,
  zero failures, zero skips, every exit code 0. Run `44672707824` passed on the implementation
  head `af6180b65b7a23b44179f558492c75f8caa6eba4` with the same counts, matching the ledger
  fragment's evidence.
- `scripts/port/build_port_index.py check --shards-only --against 7b4c960` passes on a
  temporary clone of the head; the `p6-002` queue-row edit (dependency now `p6-001,p4-003`) and
  the single new `173-import-service.tsv` fragment are consistent and touch only this unit.
- Test coverage reviewed: 12 `LibraryImportServiceTests` cases, 9
  `LibraryImportServiceIntegrationTests` cases and 3 `ImportLibraryCommandTests` cases. The
  fixture expectations (3 Steam games, ids `220`/`570`/`400`, 9 decisions, one skipped) match the
  existing `LibraryMigrationTests`, and the cancellation points (`Read`, `Copied`, `Written`,
  `Verified`, `BeforeSwap`) are real migration checkpoints.
- No banned API, protected path, aggregate, baseline or unrelated unit is touched. The existing
  `import-v10` command and its p4-002 evidence are unchanged.

## Blocking findings

### P2-1 — Unwritable destination escapes the `DestinationUnavailable` contract

- Location: `src/Playnite.Core/Import/LibraryImportService.cs:196` (the `catch (IOException)`
  that maps migration failures to `LibraryImportFailure.DestinationUnavailable`; catch chain at
  lines 187–199).
- Problem: the catch set omits `UnauthorizedAccessException`. `UnauthorizedAccessException`
  derives from `SystemException`, not `IOException`; the codebase already knows this and catches
  both explicitly at `src/Playnite.Core/Import/AssetCopy.cs:58` and `:123`.
- Concrete failure: point the destination at an existing directory that is not writable (for
  example mode `0555`). `LibraryMigration.RunAsync` reaches
  `src/Playnite.Core/Import/LibraryMigration.cs:124`,
  `new FileStream(destination + ".import.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
  FileShare.None)`, which throws `UnauthorizedAccessException` on Unix. `ImportAsync` lets that
  raw exception escape, so a Core caller does not receive
  `LibraryImportException(LibraryImportFailure.DestinationUnavailable)` as the service contract
  promises. The CLI only turns it into exit code 4 through its catch-all at
  `src/Playnite.Cli/Commands/ImportLibraryCommand.cs:53`. The same gap exists for destination
  asset writes at `src/Playnite.Core/Import/AssetCopy.cs:92` and `:122`, and for the reader catch
  at `LibraryImportService.cs:157` (there it should map to `SourceReadFailed`).
- Requested change: catch `UnauthorizedAccessException` in the migration failure block (and the
  reader block), map it to the appropriate category, and add a fake-migrator test that throws
  `UnauthorizedAccessException`.

### P2-2 — Malformed non-empty reader envelope escapes the `SourceRejected` contract

- Location: `src/Playnite.Core/Import/LibraryImportService.cs:187` (the
  `catch (InvalidDataException)` that maps to `LibraryImportFailure.SourceRejected`).
- Problem: the reader envelope is parsed by Newtonsoft at
  `src/Playnite.Core/Import/LibraryMigration.cs:49`
  (`JObject.Load(reader, ...)`). A non-object root or malformed JSON throws
  `Newtonsoft.Json.JsonReaderException` (a `JsonException`), which is neither
  `InvalidDataException` nor `IOException` nor `ArgumentException`, so it escapes `ImportAsync`
  unclassified.
- Concrete failure: an `ILibraryImportReader` that returns `[]` (valid JSON, wrong root) or any
  malformed non-empty string makes `ImportAsync` throw `JsonReaderException` instead of
  `LibraryImportException(LibraryImportFailure.SourceRejected)`. The service already guards the
  adjacent empty-envelope case at `LibraryImportService.cs:163`, so malformed non-empty
  envelopes are in the same documented “intermediate source failed validation” class and are
  currently reachable by any reader implementation.
- Requested change: catch `Newtonsoft.Json.JsonException` as `SourceRejected` (or validate the
  envelope shape in the service before handing it to the migration), and add a fake-reader test
  that returns `[]`.

Both findings are contract/robustness defects in the new public service rather than data-loss
bugs; neither is covered by the current tests, and both are masked in the CLI only by its
catch-all exit-code 4 path.

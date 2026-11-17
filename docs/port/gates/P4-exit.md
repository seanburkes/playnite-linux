# Gate P4 — data, settings and migration

- Date: 2026-11-17
- Original gate snapshot: `0ecee2607cb6539e6fbbd479d349f04ce795fb68` (`main`, merged Phase 4 head).
  Historical runs below measure their explicitly named trees, not this later correction.
  Test counts are backed by the named run's downloaded JUnit/count/exit artifacts.
  Formal counts are distinguished per model: the uploaded mutation artifact backs
  LibraryImport; SettingsMigration and GameSession counts are log-backed (not in that
  artifact), with the original local rerun recorded separately. No historical capture
  object is rewritten. The follow-up evidence below binds the additional crash tests to
  their actual tested head, never to the capture commit's own run.
- Owner: p4-exit
- Review model: adversarial assistance on the owner's behalf, not independent approval. The
  ADR-024 single-owner exception applies; no external-human review is claimed. Unit evidence
  was produced with two firstmate-arranged adversarial reviews per product unit where the
  unit shard records them; this exit artifact carries worker diff-only self-review.
- Result: **PENDING CORRECTED-HEAD CI CAPTURE** — the two missing crash points are now
  implemented; the corrected verdict requires fresh linux-ci and packaging evidence.
  Acceptance and the residual-risk
  dispositions are the owner's; this document does not assert owner acceptance, and the
  deferrals and uncovered items below remain gaps, not passes.

## Evidence

| Item | State | Commit / run | Evidence and boundary |
| --- | --- | --- | --- |
| Post-merge floor capture | pass | `0ecee2607cb6539e6fbbd479d349f04ce795fb68`; [43998974769](https://github.com/seanburkes/playnite-linux/actions/runs/43998974769) | Main-branch push linux-ci on the gate commit, conclusion success. Downloaded `linux-test-results/test-counts.json`, the three JUnit XML files and the three MTP exit files agree: **Core 1779 executed / 1779 passed / 0 failed / 0 skipped**, **Architecture 173 / 173 / 0 / 0**, **Smoke 4 / 4 / 0 / 0**; every MTP exit code is 0 and each project's `testsuite` element has `skipped="0"`. The capture entry in `tests/baseline.json` cites this run and this commit. |
| Executed-case ratchet | pass | same main run; `tests/baseline.json` | Core **1779 ≥ 1779**, Architecture **173 ≥ 173**, Smoke **4 ≥ 4**; the legacy oracle remains **281 executed / 281 passed / 0 failed / 0 skipped** with its three documented exclusions. This gate lowers no floor and changes no existing capture object. |
| Zero skips | pass | same main run | `skipped: 0` for all three projects in `test-counts.json`; `skipped="0"` and zero `<skipped>` elements in every JUnit file. No `[Skip]`, `[Ignore]`, filter exclusion or re-accepted snapshot is used to obtain the counts. |
| Plan guards and formal models | pass | same commit; [43998974777](https://github.com/seanburkes/playnite-linux/actions/runs/43998974777) | `inventory-and-parity` and `formal` both succeeded. The formal job used the pinned TLC 1.7.4 jar (`sha256 936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`). **Artifact-backed:** `library-import-mutation-tlc` contains the passing LibraryImport baseline (328 generated / 117 distinct / depth 20) and 11 intended invariant-violation mutant logs. **Log-backed, not uploaded in that artifact:** the same formal job reports SettingsMigration 16/11/depth 6 and GameSession 1401/400/depth 27. The original gate also recorded a separate local `scripts/formal/run_tlc.sh` rerun on `0ecee26` reproducing all three; that rerun is not downloaded-artifact evidence. |
| Packaging | pass | same commit; [43998974804](https://github.com/seanburkes/playnite-linux/actions/runs/43998974804) | Packaging workflow succeeded on the gate commit. It is supporting platform evidence, not one of the six Phase 4 criteria. |
| Aggregates regenerated on main | pass | same commit; [43998974860](https://github.com/seanburkes/playnite-linux/actions/runs/43998974860) | The parent-owned `port-index` workflow regenerated `docs/port/queue.tsv` and `docs/port/ledger.tsv` from unit shards. This gate PR edits shards and documentation only; it never hand-edits an aggregate. |
| `legacy/` unchanged since Phase 1 | pass | `0ecee26` against Phase 1 `8c5a811` | `git diff --name-only 8c5a811..0ecee26 -- legacy/` is empty and `git log --oneline 8798035..0ecee26 -- legacy/` is empty. The frozen Windows oracle has not changed. |

### Merge commits

Every merge commit below was read from the forge and verified with
`git merge-base --is-ancestor <mergeCommit> 0ecee26`; each returned true.

| PR | Merge commit | Unit |
| --- | --- | --- |
| [#89](https://github.com/seanburkes/playnite-linux/pull/89) | `704b7b83e69aee31ebfd5f32c83eccee8e0ae657` | p4-004 settings migration |
| [#91](https://github.com/seanburkes/playnite-linux/pull/91) | `a6f8bfc7e3cf94a37c5c043da6f2e90609b2ba6b` | p4-004 review follow-up |
| [#90](https://github.com/seanburkes/playnite-linux/pull/90) | `ff928db55f998fd65c51f61599453f4fc753d3f5` | p4-001 SQLite destination |
| [#92](https://github.com/seanburkes/playnite-linux/pull/92) | `5b0b8eb33aec76dee92b56bf846ab1a825793e25` | p4-001 post-merge review follow-up |
| [#93](https://github.com/seanburkes/playnite-linux/pull/93) | `0d7c4a08ee1f1fda241633d4a8251121d2010a34` | p4-tla formal model |
| [#94](https://github.com/seanburkes/playnite-linux/pull/94) | `9f81d30b3cc4a6beb0e15f39610ecd219ebc19f7` | p4-002 importer and p4-006 skip report |
| [#95](https://github.com/seanburkes/playnite-linux/pull/95) | `e374280993b9de0c3ac840bdc3199823d401c4ce` | shard-board reconciliation |
| [#96](https://github.com/seanburkes/playnite-linux/pull/96) | `f12a4325c58cf66d47b93c5768e46861696d3052` | p4-005 asset copy/hash |
| [#98](https://github.com/seanburkes/playnite-linux/pull/98) | `dfd53f9cff6ed60033b78e7a26693f898017f47d` | shared packaging repair |
| [#97](https://github.com/seanburkes/playnite-linux/pull/97) | `8ff04700cf62708a5dbf3fbbc9dae08016fed9f5` | p4-003 replacement publication |

### Per-unit evidence

Historical unit runs are observations on the unit's own head, not the gate floor. The
authoritative final-tree counts are the main run above.

| Unit | Delivery | Tested-head evidence (downloaded test artifacts unless stated) | Contract and oracle |
| --- | --- | --- | --- |
| p4-001 — SQLite schema/repositories | [#90](https://github.com/seanburkes/playnite-linux/pull/90) at `ff928db`, follow-up [#92](https://github.com/seanburkes/playnite-linux/pull/92) at `5b0b8eb` | **PR implementation head `9e354919ea81683ebe1e4ea2f29e497c2a3ec6ca`**, linux-ci [43821347337](https://github.com/seanburkes/playnite-linux/actions/runs/43821347337): Core 1671/1671, Architecture 144/144, Smoke 4/4, zero failures/skips. Not a merged-main run; `9e35491..5b0b8eb` changes only unit docs/shards and baseline evidence, so these test counts carry to the merged tree. | `docs/port/p4-001-sqlite.md`; `SchemaTests`, `RepositoryTests`, `DatabaseContractTests`. Schema version 3, canonical lowercase-D identities for `game.Id`, every metadata table and `game_action.EmulatorId`, fail-fast nested writes, no reachable raw connection; importer/report/dry-run/swap are p4-002/p4-003, not this unit. |
| p4-002 — isolated v10 reader and verified import | [#94](https://github.com/seanburkes/playnite-linux/pull/94) at `9f81d30` | **PR implementation head `69e00be324d186dd7688c65c3eca1bc1d1f84d49`**, linux-ci [43918884660](https://github.com/seanburkes/playnite-linux/actions/runs/43918884660): Core 1706/1706, Architecture 173/173, Smoke 4/4, zero failures/skips. Not a merged-main run; `69e00be..9f81d30` changes only unit docs/shards and baseline evidence. | `docs/port/p4-002-import.md`; `LibraryMigrationTests`, `RawImportTests`. Reader-only `Playnite.LegacyImport` executable over raw LiteEngine BSON, versioned JSON v1, dry-run, owned restartable staging, verify-before-publish; the later p4-003 contract adds replacement and the in-artifact receipt. |
| p4-003 — atomic replacement, receipt and reader safety | [#97](https://github.com/seanburkes/playnite-linux/pull/97) at `8ff0470` | PR final head `ae15133` linux-ci [43998496126](https://github.com/seanburkes/playnite-linux/actions/runs/43998496126) and merged-main run [43998948443](https://github.com/seanburkes/playnite-linux/actions/runs/43998948443): both Core 1779/1779, Architecture 173/173, Smoke 4/4, zero failures/skips | `docs/port/p4-003-publication.md`; `LibraryReplacementTests`; `tests/golden/p4-003-replacement/crash-matrix.json`. Shared reader/exclusive publication lease, `--replace-existing` consent, `playnite_migration_receipt` inside the artifact, rollback confined to the unconfirmed publication window with sidecar cleanup, deterministic OS-lock reader-window tests. |
| p4-004 — settings migration | [#89](https://github.com/seanburkes/playnite-linux/pull/89) at `704b7b8`, follow-up [#91](https://github.com/seanburkes/playnite-linux/pull/91) at `a6f8bfc` | **PR implementation head `d86368bfb58ba276eab2565d36017c4bcee6e8f3`**, linux-ci [43815358729](https://github.com/seanburkes/playnite-linux/actions/runs/43815358729): Core 1642/1642, Architecture 144/144, Smoke 4/4, zero failures/skips. The previous merged-head label was false. Counts hold for merge `a6f8bfc` because `d86368b..a6f8bfc` changes only `docs/port/p4-004-settings-migration.md`, `docs/port/units/p4-004.ledger.tsv` and `tests/baseline.json` (evidence-only). | `docs/port/p4-004-settings-migration.md`; `SettingsMigrationTests`. Lossless `settingsUtf8` compatibility export of the three frozen goldens, explicit caller-supplied path roots, unmapped/unknown values reported and retained, five process-death write boundaries. |
| p4-005 — asset copy/hash | [#96](https://github.com/seanburkes/playnite-linux/pull/96) at `f12a432` | **Main push on merge `f12a4325c58cf66d47b93c5768e46861696d3052`**, linux-ci [43986790037](https://github.com/seanburkes/playnite-linux/actions/runs/43986790037): Core 1737/1737, Architecture 173/173, Smoke 4/4, zero failures/skips. The reader diagnostic is evidenced only later by merged-main `0ecee26`, [43998974769](https://github.com/seanburkes/playnite-linux/actions/runs/43998974769), Core 1779. | `docs/port/p4-005-assets.md`; `AssetCopyTests`, plus `LibraryMigrationTests`/`RawImportTests` integration. Content-hash blob store, streaming dedup, verified atomic final-name publication, PNG/GIF/JPEG header-only dimensions, explicit per-reference accounting. The prior review-closure attribution was false: `f12a432` still had `copies.Single` and no absent/duplicate captured-marker controls. `ReadMarker`, `Missing_database_json_refuses_with_expected_filename_and_found_count_without_mutating_source` and `Missing_or_duplicate_captured_markers_refuse_explicitly_with_a_working_single_marker_control` (0 and 2) shipped with [#97](https://github.com/seanburkes/playnite-linux/pull/97). Identical asset implementation bytes do not prove that finding was closed in the asset delivery. |
| p4-006 — skip report | delivered inside [#94](https://github.com/seanburkes/playnite-linux/pull/94), no duplicate implementation | Same **PR-head artifact** as p4-002 (`69e00be`, run 43918884660), with the same evidence-only merge delta; no separate merged-head run is claimed. | `MigrationReport.Decisions` emits exactly one `MigrationDecision` per source document (file, collection, identity, outcome, reason); non-Steam rows are `skipped` with a provider reason and stay byte-preserved in staging `source.json`. Tests `Frozen_Windows_fixture_imports_every_Steam_row_and_reports_every_document` and `Unknown_collection_is_reported_by_identity_instead_of_dropped`. |
| p4-tla — `LibraryImport.tla` | [#93](https://github.com/seanburkes/playnite-linux/pull/93) at `0d7c4a0` | **PR head `1e1facdd18db98b912372139e5ee9727cfaf2a1f`**, plan-guards formal run [43895223656](https://github.com/seanburkes/playnite-linux/actions/runs/43895223656); merged-main `0ecee26` rerun [43998974777](https://github.com/seanburkes/playnite-linux/actions/runs/43998974777), whose downloaded mutation artifact backs the counts. | `docs/formal/LibraryImport.tla`, `scripts/formal/run_tlc.sh`, `scripts/formal/check_library_import_mutations.py`; artifact-backed 328/117/depth 20 and 11 rejected mutants. Formal-only safety evidence: the atomic-replacement assumption, in-place counterexamples and the no-C#-conformance/no-power-loss boundary remain explicit in `docs/formal/README.md`. |

The base-versus-variant duplicates are resolved explicitly: p4-006 is delivered inside
#94 and has no second implementation; p4-002 and p4-003 are sequential contracts, not
duplicates (p4-002 publishes a fresh verified destination, p4-003 adds explicit-consent
replacement of an existing one); p4-005 owns the asset store while p4-003 integrates it
into the receipt and replacement path. No aggregate (`queue.tsv`, `ledger.tsv`) is edited
by this PR; the post-merge `port-index` workflow regenerates them on `main`.

## Exit criteria

### 1. A fixture v10 library migrates with 100% of expected Steam rows and zero silent drops

**Verdict: PASS for the frozen Windows fixture. Not claimed for a real user library.**

`tests/fixtures/v10/library` is the Windows-produced oracle, reused without modification.
`LibraryMigrationTests.Frozen_Windows_fixture_imports_every_Steam_row_and_reports_every_document`
asserts 3 expected and 3 imported Steam games (`220`, `570`, `400`), 9 report decisions,
exactly one skipped decision at `10000000-0000-0000-0000-000000000004` with a `Non-Steam`
reason, and preserved field values including a Unicode name, release date, install
directory and playtime. `Unknown_collection_is_reported_by_identity_instead_of_dropped`
proves an unsupported collection is reported by identity rather than dropped. By
construction every source document produces one decision, so there is no silent path.
`Before_publication_the_main_file_alone_has_every_game_and_WAL_is_empty` proves the
published main file is complete. This is fixture-scale evidence, not a claim about every
real Playnite library shape.

### 2. Crash injection at every migration state leaves a resumable or clean state, including replacement over an existing library, rollback and the concurrent-reader window

**Verdict: PASS within the process-death boundary stated below.**

Fresh-path publication has eight real child-process SIGKILL checkpoints (`Read`,
`Transformed`, `Copied`, `PartialWrite`, `Written`, `Verified`, `BeforeSwap`, `Committed`)
in `SIGKILL_at_each_pipeline_checkpoint_leaves_absent_or_complete_live_and_restartable_staging`;
the live path is absent until a complete verified database exists. Existing-library
replacement has eleven checkpoints driven by the additive
`tests/golden/p4-003-replacement/crash-matrix.json` in
`SIGKILL_replacing_live_at_every_state_leaves_whole_old_or_receipted_new_and_can_resume`:
the live path is the whole old library through `Publishing` and the complete new library
with its receipt from `Swapped` onward. Rollback is confined to the unconfirmed
publication window: `Publication_window_failure_or_cancellation_restores_exact_pre_run_bytes`
covers managed failure and cancellation at `BeforeSwap`, `Publishing`, `Swapped` and
`ReceiptConfirmed` and compares the restored main file with the complete pre-run byte
array before reopening SQLite, while `ReceiptConfirmed_failure_discards_new_sidecars_and_success_audit_before_restoring_old_bytes`
proves discarded-generation `-wal`/`-shm`/`-journal`/`-mj*` sidecars and the success audit
are removed. `Post_completion_failure_leaves_no_success_audit_never_rolls_back_and_refuses_from_artifact_alone`
proves a post-`Committed` fault never rolls back. The concurrent-reader window is covered
by `Reader_opening_inside_actual_cross_process_swap_window_waits_then_reads_complete_new`
(Publishing, Swapped, ReceiptConfirmed),
`Existing_reader_finishes_old_snapshot_before_replacement_can_close_and_rename_its_WAL_database`
and `Reader_started_after_unconfirmed_rename_sees_only_old_after_rollback`; the window
tests rendezvous on an actually failed OS-lock acquisition, not a sleep.

The correction adds two observation-only seams without renaming or dropping any
existing state, matrix row or test. `AssetFlushed` follows the asset's
`Flush(flushToDisk: true)` and precedes its rename;
`SIGKILL_after_asset_flush_before_rename_leaves_only_complete_temporary_bytes_and_retry_publishes_verified_blob`
kills the real child there and proves only a full hash-matching `.tmp` exists,
no final blob/database/report is published, and retry publishes the correct blob
and database reference without changing source bytes (`LibraryImport.CrashSafe`,
`CleanResumable`, `ReaderNeverPartial`, `SourceUnchanged`). `BeforeReportMove`
follows `Committed` immediately before the external report rename;
`SIGKILL_at_report_move_after_Committed_leaves_no_success_report_and_repeat_refuses_from_receipt_alone`
kills the real CLI child there, proving no stale or new success report survives,
the complete new library and report-bearing receipt remain, and after removing
staging a repeat refuses explicitly from that receipt without changing published
bytes (`LibraryImport.AtomicLive`, `ReaderNeverPartial`, `CrashSafe`,
`DurableReceipt`, `RepeatRunSafe`, `SourceUnchanged`). These are concrete
implementation checks associated with the model invariants, not formal
asset/report-state conformance proofs. The original gate snapshot lacked these
two kill points; its unqualified crash-coverage verdict was not supported by
that tree. The later tested-head evidence below is required for this corrected verdict.

Supporting boundaries: settings migration kills a real child at `BeforeWrite`, `Written`,
`Flushed`, `Verified` and `Committed` (`ProcessDeathAtEveryWriteBoundaryIsRestartableWithoutManagedCleanup`),
and asset copying kills a real child mid-copy leaving only a `.tmp`
(`SIGKILL_mid_copy_leaves_only_a_temporary_file_then_retry_publishes_verified_blob`).
These are process-death tests. They do not claim parent-directory fsync or power-loss
durability of directory metadata.

### 3. The source directory is byte-identical after migration

**Verdict: PASS.**

The fixture tests snapshot every source file before and after migration and compare the
complete snapshot (`Frozen_Windows_fixture_imports_every_Steam_row_and_reports_every_document`,
`SIGKILL_at_each_pipeline_checkpoint_leaves_absent_or_complete_live_and_restartable_staging`
and the replacement tests), carrying the `LibraryImport.SourceUnchanged` invariant trait.
Settings migration separately asserts `SettingsMigrationTests.EntireV10FixtureLibraryRemainsByteIdentical`
after copying the whole frozen library. `LibraryMigrationTests.Fixture_file_set_and_every_byte_match_the_recorded_SHA256SUMS`
pins the committed fixture set and bytes. The reader opens source files read-only; the
writer never writes into the source directory, and Core refuses source/destination
containment and symlinked ancestors.

### 4. A second run is idempotent or refuses with a clear message, including refusal from the in-artifact receipt alone and explicit consent for an unreceipted prior import

**Verdict: PASS.**

`Repeated_dry_run_is_an_idempotent_plan_and_writes_nothing` proves repeated planning is
identical and writes nothing. `Second_real_run_refuses_clearly_and_preserves_existing_database`
proves a fresh-path second real run refuses with a clear message and preserves the tree.
`Replacement_embeds_source_receipt_and_refuses_without_external_audit_artifacts` deletes
both the external report and the staging directory and proves refusal still comes from
the `playnite_migration_receipt` row inside the database.
`Post_completion_failure_leaves_no_success_audit_never_rolls_back_and_refuses_from_artifact_alone`
repeats that from the artifact alone after a post-completion fault.
`Unreceipted_earlier_import_requires_explicit_replacement_consent_in_Core_and_CLI` proves
an earlier unreceipted import (including one with user edits) is refused by default and
only replaced with explicit `replaceExisting: true` / `--replace-existing` consent; a
receipted artifact is always refused, even with consent. The receipt verifies both
identity fingerprints and the complete `ReportJson`
(`Receipt_confirmation_failure_restores_byte_identical_old_library_before_readers_resume`
with tamper controls for `SourceHash`, `SourceManifestHash` and `ReportJson`).

### 5. Settings round-trip is byte-identical for stable fields with unmapped Windows paths reported

**Verdict: PASS for the three frozen settings goldens and the explicit path rules; the
destination `settings` object is a normalized projection, not a byte copy.**

`SettingsMigrationTests.FrozenV10SettingsRoundTripByteForByte` runs for `config.json`,
`fullscreenConfig.json` and `windowPositions.json` and exports each byte-identically
through the lossless `settingsUtf8` compatibility representation (BOM, whitespace, order,
numeric spelling, escaping, missing-vs-null and trailing newline preserved; only mapped
path string spans change). `FrozenV10ConfigWithWindowsPathsChangesOnlyExplicitlyMappedStringBytes`
pins that mapped-only delta. `UnmappedPathsAreNeverDroppedOrGuessed` covers 16 drive, UNC,
root-relative, relative and invalid-suffix forms; `ForwardSlashUncPathIsMappedWhenAConfiguredRootMatches`
and `ForwardSlashUncPathIsReportedWhenNoConfiguredRootMatches` cover forward-slash UNC;
`NativeSettingsRewriteOnlyMappedPathsAndPreserveUnknownAndUnmappedInBag` proves unmapped
and unknown values stay in the `legacy` bag and `report.unmappedPaths`, not silently
dropped. `LosslessExportRejectsDivergenceFromNativeSettings` refuses divergence between
the projection and the compatibility export. Path roots are caller-supplied; there are no
ambient drive, home, XDG, Wine or installation guesses.

Change control: the only owner scope waiver of 2026-11-16 recorded in the merged tree is
the Phase 3 waiver for culture handling and the SDK.Abstractions v7 freeze / ApiCompat
report (`docs/port/gates/P3-leaves.md`, repeated in `docs/port/PORT-STATUS.md`). No
separate Phase 4 settings scope waiver exists in the repository, so none is claimed here.
This criterion is scoped to the three frozen goldens and the documented path rules; native
settings services, defaulting and fullscreen/window-position service semantics are not
claimed.

### 6. Desktop has no reference to LiteDB or the importer assembly

**Verdict: PASS, with the exact edge stated.**

`Playnite.Desktop.csproj` references Core, SDK, Platform, Steam, Avalonia and
`Playnite.LegacyImport.Contracts`; it has no reference to `Playnite.LegacyImport` and no
LiteDB package. `DependencyTests.Product_dependency_closures_exclude_Windows_and_importer_leaks`
asserts the resolved Desktop dependency closure excludes LiteDB and
`Playnite.LegacyImport`; `RawImportTests.LiteDB_and_importer_types_cannot_escape_the_raw_reader_and_reader_cannot_load_SQLite_or_Core`
checks both dependency directions. The allowed edge is the **Contracts** assembly:
`src/Playnite.LegacyImport.Contracts/Playnite.LegacyImport.Contracts.csproj` is an empty
SDK project with no package or project references, and its only source file is an empty
`internal static class AssemblyMarker`. It contains no reader/writer API, no LiteDB types,
no Core/Desktop/SDK types and no data model — it is a marker assembly, not an importer
surface. `docs/port/02-ARCHITECTURE.md` §2 records it as "nothing but BCL" and forbids
LiteDB/Core/Desktop/SDK. Today the CLI is the process that invokes the importer; Desktop
has only the marker edge, and no Desktop importer invocation is claimed.

## Security exception — LiteDB advisory

**GHSA-3x49-g6rc-c284 / CVE-2022-23535** affects the frozen LiteDB 4.1.4 file-format
dependency used by the v10 reader. The exception is reviewable, scoped to
`Playnite.LegacyImport` and expressed as a single `NuGetAuditSuppress` entry for that exact
advisory URL in `src/Playnite.LegacyImport/Playnite.LegacyImport.csproj`, visibly commented
for removal when v10 import retires. `NuGetAuditMode=all`, warnings-as-errors and all other
advisories remain enabled; there is no global or Core/Desktop/CLI/test suppression.
`RawImportTests.The_only_audit_exception_is_the_owner_authorized_importer_advisory` pins
the scope.

Honest residual risk: the reader cannot be made safe by the exception. It executes with
the user's privileges and is a dependency boundary, not a sandbox. The mitigation is raw
LiteEngine BSON traversal with no `BsonMapper` or typed binding (`Negative_guard_rejects_typed_mapping_and_object_construction`
and the extended guard tests), plus fixtures that instrument `_type`/`$type` constructor
traps at every nesting level. Other parser bugs, malformed-input handling, memory or
recursion exhaustion, future accidental typed mapping, the CLI's trusted caller-supplied
reader DLL and the absence of a reader timeout remain residual risks. **Removal condition:**
the suppression and the LiteDB dependency must be deleted when v10 import retires; until
then this is a reviewed exception, not a security pass.

## Deferrals and accepted residual risk

- **Platform floor:** `Playnite.Smoke.Tests` is 4 host smoke cases (one native Avalonia
  window under Xvfb plus three CLI host checks), not a product acceptance or accessibility
  result.
- **Formal model scope:** `LibraryImport.tla` models the publication protocol with a
  concurrent reader; it is not a C# conformance proof, liveness proof or power-loss proof.
  The production atomic-replacement assumption and the unsafe in-place counterexamples stay
  documented in `docs/formal/README.md`.
- **Reader boundary:** supported connections hold the publication lease. Arbitrary raw
  SQLite clients, hostile file replacement in the private namespace and hard-linked
  alternate spellings are outside it and are documented in `docs/port/p4-003-publication.md`.
- **Assets:** the frozen v10 fixture has no asset payload; the separate provenance-bearing
  `tests/fixtures/v10-assets` derivative supplies the asset evidence. Only PNG, GIF and
  JPEG dimensions are decoded, from headers; other formats are copied with explicit
  status. An unreferenced `.tmp` orphan can remain after a crash and is visible under its
  temporary name; directory fsync/power-loss durability is not claimed.
- **Settings:** the destination is a normalized projection plus a lossless compatibility
  export, not a native settings service. Native defaulting and fullscreen/window-position
  services remain later work.
- **ADR-024 single-owner exception:** review assistance is correlated and can miss shared
  blind spots. Executed-case ratchets, frozen-source hashes, protected-path gates and CI are
  structural checks, not proof of semantic correctness.
- **Empty-library-open performance:** still **NOT MEASURED**. No loaded-library startup or
  working-set measurement is claimed by this gate.

## What is NOT covered

- Real user-library shapes beyond the frozen Windows fixture; Windows-run execution of the
  port itself; UI integration, theme rendering, game launch, Steam discovery and packaging
  acceptance beyond the passing packaging workflow.
- Exhaustive parser correctness or security beyond the stated bounds; multi-process hostile
  races; power-loss durability of directory metadata.
- Native settings services and fullscreen/window-position behaviour; culture handling and
  the SDK.Abstractions v7 freeze / ApiCompat report remain the owner-waived Phase 3 items
  and are not delivered or claimed here.
- Any acceptance claim. The owner decides whether this evidence closes Phase 4.

## Exit checklist

- [x] A fixture v10 library migrates with 3/3 expected Steam rows, one explicitly reported
      non-Steam skip and 9/9 documents accounted for; no silent-drop path exists.
- [x] Crash injection at every fresh-path and replacement state leaves a resumable or clean
      state, including rollback with sidecar cleanup and the concurrent-reader window.
- [x] The source directory is byte-identical after migration in tests, including the whole
      frozen fixture library.
- [x] A second run is an idempotent dry-run plan or a clear refusal; refusal works from the
      in-artifact receipt alone and unreceipted prior imports need explicit consent.
- [x] Settings round-trip is byte-identical for the three frozen goldens through the
      lossless compatibility export, with unmapped Windows paths reported, not dropped.
- [x] Desktop has no LiteDB or importer reference; the only edge is the marker-only
      `Playnite.LegacyImport.Contracts` assembly.
- [x] Main linux-ci executed cases are non-decreasing with zero skips: Core **1779/1779**,
      Architecture **173/173**, Smoke **4/4**, no floor lowered.
- [x] The LiteDB advisory exception is scoped, reviewable and carries an explicit removal
      condition.

**Verdict: PENDING CORRECTED-HEAD CI CAPTURE.** The historical merged-main artifacts
cannot evidence tests absent from `0ecee26`. A fresh tested-head capture is required
for the two new process-death boundaries; the deferrals, uncovered items and residual
risks remain explicit. Acceptance is not asserted here; it does not convert any
deferral or unmeasured item into a pass.

# Review — playnite-linux PR #198

**PR:** https://github.com/seanburkes/playnite-linux/pull/198
**Reviewed head:** `73f188e4cb7267b6ccec0df477ba98ad5078677f` (`73f188e`)
**Input:** `data/p6-198/candidate.diff`
**SHA-256:** `751f89e6dd71117886acb12a97a17017a772acbb86054a687161d99d303cde0f`
**Scope:** 9 files, 1496 insertions; diff-only review of the SDK Abstractions freeze and the v6→v7 ApiCompat record.
**Verdict:** **No blocking P1/P2 findings. Ready for the captain's merge decision.**

## Blocking findings

None.

## What I verified

### 1. Freeze surface is complete and unchanged

- `docs/port/p6-008-frozen-api.txt` is 67 declarations (plus `#nullable enable`), exported as nine
  types: `IDataSerializer`, `DontSerializeAttribute`, `SerializationPropertyNameAttribute`,
  `OnApplicationStartedEventArgs`, `IIdentifiable`, `InstallationStatus`, `LibraryGetGamesArgs`,
  `MetadataField`, `SdkVersions`.
- `IDataSerializer` has 27 methods, `MetadataField` has 19 values (`Name = 0` … `InstallSize = 18`),
  and the enum/class members match the doc table exactly.
- The diff changes no `src/Playnite.SDK.Abstractions` file, no `PublicAPI.*.txt`, and no
  `api-baseline/*`. `Frozen_manifest_is_the_committed_PublicAPI_surface` compares the manifest to
  `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt`, so the record cannot silently drift.
- `Frozen_assembly_exports_exactly_the_declared_types` and
  `Frozen_type_members_have_the_declared_signatures` pin the exported type/member shape at runtime.

### 2. ApiCompat report is internally complete

- `docs/port/p6-008-apicompat-v6-v7.txt`: 218 `CP0001` lines, zero `CP0002`–`CP0009`; the four
  header lines cite the tool, v6 package, scratch v7 side, and path normalization.
- `docs/port/p6-008-apicompat-classification.tsv`: exactly 218 rows, one per `CP0001` type, no
  duplicates; 26 `adapted`, 178 `deferred`, 14 `excluded`; every row has a non-empty group and
  decision; no `breaking` row.
- Group counts match the doc exactly: 59 plugins, 46 models, 26 host-api-adapted, 22 infrastructure,
  20 events, 14 excluded-boundary, 13 webview, 8 database, 4 logging, 4 settings, 1 emulation, 1 data.
- `ApiCompat_report_classifies_every_v6_difference` enforces the 218/one-per-type/classification
  invariants and rejects any non-`CP0001` line; `ApiCompat_report_cites_the_v6_baseline_and_the_tool`
  pins the baseline hashes and tool version in the record.

### 3. Contract tests cover every interface

- `docs/port/p6-008-interface-coverage.tsv` has 12 rows; `SdkInterfaceContractTests.cs` has 12
  `[Fact]`s and 12 fakes.
- Covered: `IDataSerializer` and `IIdentifiable` from Abstractions; `INotificationHost`,
  `INotificationsAPI`, `IDialogHost`, `IDialogsFactory`, `IMainViewHost`, `IMainViewAPI` from
  Core.Hosting; `IUriCommandHost`, `IUriHandlerAPI` from Core.UriHandling;
  `IHtmlDescriptionProvider`, `IHtmlSanitizer` from Core.Descriptions.
- `IDataSerializer_every_member_is_exercised` drives all 27 methods and asserts 27 distinct calls;
  the other contracts call every member through the real Core service.
- `Every_SDK_interface_has_a_fake_host_contract_test` reflects the product assemblies and fails on
  an uncovered interface; the 7 `SdkFreezeTests` cases cover the freeze, ApiCompat, coverage, and
  dependency checks.

### 4. Dependency boundary

- The diff changes no project file, so no new direct dependency is introduced.
- `Core_and_abstractions_have_no_UI_dependency` consumes the build graph and asserts Core depends
  only on `Playnite.SDK.Abstractions` among `Playnite.*`, and neither project pulls Avalonia/UI
  packages; no Core→Avalonia edge appears in the diff.

### 5. Final-head evidence

- The reviewed head `73f188e` has 8/8 checks green on the forge (linux-ci
  `44722297785`, packaging, plan-guards, port-unit-guard); the PR is open, non-draft, and
  `merge_state` is clean.
- The ledger fragment cites the earlier code-head run `44721678532` on `a3416280` and explicitly
  records that the final commit is a docs-only delta. The final head's own green run is the stronger
  evidence; see the non-blocking note below.

### 6. Scope and protected paths

- The diff is nine new files only: seven `docs/port/p6-008-*` / unit-record files and two test files.
  No `legacy/`, `tests/golden/`, `tests/fixtures/`, `tests/baseline.json`, `docs/formal/`,
  `docs/port/surface-parity.tsv`, source, project, or sample file is touched.
- No skips, suppressions, baseline re-acceptance, or `NotImplementedException`/TODO stubs were added.

## Non-blocking observations

- **Ledger run citation:** `docs/port/units/p6-008.ledger.d/198-sdk-freeze.tsv:2` cites
  `ci_run=44721678532` on `a3416280`, while the reviewed head `73f188e` has its own green linux-ci
  run `44722297785`. The fragment's docs-only-delta sentence explains the gap and the final head is
  green, so this does not block. Updating the fragment to cite the final-head run would make the
  record self-contained.
- **Adapted host surfaces:** the 26 `adapted` rows are explicitly ruled in the classification/doc as
  pre-existing Core host surfaces (ADR-018/ADR-023) and are not claimed source-compatible; no new
  protected-surface decision was needed for this unit.

## Bottom line

The freeze is a genuine nine-type, 67-declaration snapshot with no source or protected-file edits;
the v6→v7 ApiCompat record is complete and every difference is classified; all 12 interfaces have
compile-time fakes and executable contract tests; the Core/Avalonia boundary is unchanged; and the
final head is green. No blocking P1/P2 issue found.

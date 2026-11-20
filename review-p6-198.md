# Review — PR #198: p6-008 SDK Abstractions freeze and v6→v7 ApiCompat report

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/198
- Reviewed head: `73f188e4cb7267b6ccec0df477ba98ad5078677f` (`73f188e`), base `main`
  `15833157a2843d011e01ea0b5fb9736cd25711f0`
- Reviewed diff: `data/p6-198/candidate.diff` (9 files, 1496 additions, 0 deletions)
- Diff sha256: `751f89e6dd71117886acb12a97a17017a772acbb86054a687161d99d303cde0f`
- Diff identity: `git diff 15833157a2843d011e01ea0b5fb9736cd25711f0 73f188e4cb7267b6ccec0df477ba98ad5078677f`
  reproduces the supplied diff byte-for-byte (same sha256), so the review covers exactly the
  reviewed head.
- Scope: the p6-008 freeze record and its four data artifacts, the p6-008 queue and ledger
  shards, and the two new test files.

## Verdict

**One blocking P2 finding.** The freeze record, the ApiCompat report and the coverage map are
genuine and internally consistent, and I found no fabricated evidence, no hidden public-surface
change, no weakened test, no Avalonia/Core dependency and no protected-path edit. However,
p6-008's mandated scope — move the p6-006/p6-007 host surfaces into
`Playnite.SDK.Abstractions` and freeze them — is explicitly not delivered, and the PR closes
the unit as `done` while rewriting its own queue row to drop that deliverable. That is a scope
acceptance decision above the implementer, and it must be made before p6-008 is closed.

## Blocking P2 — the unit's mandated surface move is absent and the unit row is redefined

`docs/port/p6-008-sdk-freeze.md:43` says the p6-006 (notifications, dialogs, main view) and
p6-007 (URI handler, HTML description) host surfaces "are **not moved into this assembly by
p6-008**", and `:51` offers the move only if a reviewer requires it. The plan requires it
outright:

- `docs/port/p6-launch-brief.md:156` — "**Scope.** Move the p6-006 and p6-007 host surfaces
  into `Playnite.SDK.Abstractions`, freeze the v7 surface in
  `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`, and produce the reviewed v6-to-v7
  `Microsoft.DotNet.ApiCompat` breaking-change report." `:160` also says `IMainViewAPI`'s Core
  `Game` dependency "must be reshaped before it can move".
- `docs/port/p6-006-notifications-dialogs-mainview.md:17` — "`p6-008` **must move this surface
  into Abstractions** and freeze it with the reviewed ApiCompat report."
- `docs/port/p6-007-uri-html-description.md:15` — "p6-008 **owns moving this surface into
  Abstractions** and freezing it."

The new queue row then records the narrower outcome as complete:
`docs/port/units/p6-008.queue.tsv:2` describes p6-008 as "Freezes the versioned v7
Abstractions surface **without changing a public member**", dropping the move from the unit
contract. After merge, `docs/port/p6-launch-brief.md:156` and the p6-006/p6-007 records still
say the move belongs to p6-008, while the queue says it was not part of p6-008. That is a
concrete, reviewable contradiction, not a wording preference: the headless host interfaces
(`INotificationsAPI`, `IDialogsFactory`, `IMainViewAPI`, `IUriHandlerAPI`, their hosts and
DTOs) remain in `Playnite.Core`, which plugin code cannot reference, so the "frozen v7 SDK"
does not expose the surfaces p6-006/p6-007 built for it.

The three stated reasons do not dispose of the requirement:

1. **Ownership of the Core files.** The p6-006/p6-007 records explicitly hand the move to
   p6-008; the move is supposed to edit files the plan already assigns to this unit.
2. **Protected `PublicAPI`/`api-baseline` files.** The plan anticipates this and says the
   dispatch "must explicitly authorise the protected change through the `oracle-change` label"
   (`docs/port/p6-launch-brief.md:166`). PR #198 has no labels at all, so the gate was not
   requested rather than satisfied.
3. **ADR-018/ADR-023.** ADR-018 puts "models, events, **non-UI interfaces**, `IDataSerializer`,
   library/metadata contracts" in `Playnite.SDK.Abstractions`
   (`docs/port/00-DECISIONS.md:460`), and ADR-023 defers only "`Playnite.SDK` **UI extension
   points**" (`:629`). The headless host interfaces are the non-UI contracts ADR-018 assigns
   to Abstractions; the WPF dialogs/main-view shapes are the UI extension points. The two ADRs
   do not cover the interfaces the plan orders moved.

Acceptable resolutions, for the captain:

1. Implement the move and the `IMainViewAPI` reshape in this unit (with the `oracle-change`
   label and the protected declaration update), then the freeze is complete; or
2. Keep this narrower freeze, but do not close p6-008: correct
   `docs/port/p6-launch-brief.md:156` and the p6-006/p6-007 sentences, keep the queue row out
   of `done`, and give the move its own unit id. The queue row must not silently narrow a
   planned deliverable.

I did not verify a build or re-run ApiCompat, so this finding is about scope and record
integrity, not about the correctness of the emitted report bytes.

## Verified — no blocking finding in the four attack areas

### 1. Freeze integrity and intentionality

- `p6-008-frozen-api.txt` (67 declarations + `#nullable enable`) has exactly the same
  declaration set as `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` once the two
  `#nullable enable` directives and blank lines are removed; I diffed the files directly and
  the only difference is the duplicate directive that the test filters. `PublicAPI.Unshipped`
  content is tracked: `DontSerializeAttribute` and `SerializationPropertyNameAttribute` are
  in the manifest.
- The pinned text hash matches: `src/Playnite.SDK.Abstractions/api-baseline/PublicAPI.Shipped.sha256`
  (`8d54786f…`) equals the current `PublicAPI.Shipped.txt` hash. The binary baseline hash
  (`78623d27…`) equals `api-baseline/Playnite.SDK.Abstractions.dll`.
- The assembly surface is pinned independently of the new tests: `PublicApiAnalyzers` is a
  direct package, `RS0016`/`RS0017` cannot be suppressed (`Playnite.SDK.Abstractions.csproj`),
  and `ApiCompatValidateAssemblies`/`ApiCompatContractAssembly` are enabled. The PR changes no
  source, declaration or baseline file, so there is no accidental addition or removal.
- I independently enumerated the v6 assembly's public top-level types: **227 public types =
  218 reported differences + the 9 frozen types**, and the reported 218 are exactly
  v6-minus-v7 after normalising generic notation (`` `1 `` vs `<T>`). The report is not a
  cherry-picked subset.

### 2. ApiCompat report genuineness

- The v6 baseline is real and correctly pinned: I downloaded
  `https://api.nuget.org/v3-flatcontainer/playnitesdk/6.18.0/playnitesdk.6.18.0.nupkg` and got
  package sha256 `63ae943bf226a782738d3d6b9977dcb77861e5d2ea1aadcf620332618c93e236` and
  `lib/net462/Playnite.SDK.dll` sha256
  `ce7511941aba4d4b17dd7b226fadfd0ecefdf51ab73775b25631089fd7542fc7`, matching the report.
- The v6 assembly contains all nine frozen types, and their public member sets match the
  frozen manifest (27 `IDataSerializer` methods in the same shapes, `IIdentifiable.Id`,
  `SdkVersions.SDKVersion`, 19 `MetadataField` values, `InstallationStatus` values 0/1, both
  attribute shapes, both `LibraryGetGamesArgs` constructors and the getter; the internal
  `CancelToken` setter is not public). No v6 member is missing from a frozen v7 type, so the
  "zero member-level differences" claim holds on the baseline side.
- All 218 report lines are `CP0001` and the classification file covers exactly the same 218
  type names, one row each, with 26 `adapted` / 178 `deferred` / 14 `excluded` and the group
  totals quoted in the document. Suppressions: none are present or needed.

### 3. Contract coverage and dependency boundary

- Twelve interfaces are covered by twelve executable tests: the two frozen interfaces plus the
  ten host contracts in `Playnite.Core.Hosting`, `Playnite.Core.UriHandling` and
  `Playnite.Core.Descriptions`. The fakes are bound at compile time through the real Core
  constructors, and the reflection test recomputes the expected set and fails on any
  uncovered or extra interface.
- `Core_and_abstractions_have_no_UI_dependency` reuses the shipped `SdkBoundaryPolicy`:
  `Playnite.Core.csproj` references only `Playnite.SDK.Abstractions` from `Playnite.*`, and
  the resolved Core package closure contains no Avalonia library. The four Avalonia strings in
  `obj/project.assets.json` are central package versions, not resolved dependencies. I found
  no direct or transitive UI reference in the diff.

### 4. Evidence

- The final head `73f188e` has a green forge rollup: 8/8 checks pass, including
  `linux-build-and-test`, the two packaging smokes, `formal`, `inventory-and-parity` and both
  unit guards. The ledger cites run `44721678532` on `a341628`; the reviewed head is a
  docs-only child of that commit (the only tree delta is the ledger fragment itself), and the
  final head has its own green run, so the evidence claim is not stale in substance.
- No sample or product source is touched; the build is unaffected by construction.

## Non-blocking observations (P3, not merge-blocking)

1. **The coverage test proves file text, not implementation.** 
   `tests/Playnite.Architecture.Tests/SdkFreezeTests.cs:183-184` accepts any file containing
   `"class " + Fake` and `Test + "("`; a fake that stopped implementing the interface would
   still pass that string check. The compile-time constructor wiring in the current tests is
   what actually proves implementation, so this is a future-proofing gap rather than a current
   failure.

2. **The manifest test is a copy check, not an assembly check.** 
   `SdkFreezeTests.Frozen_manifest_is_the_committed_PublicAPI_surface` (`SdkFreezeTests.cs:24`) proves the doc equals
   the PublicAPI files; assembly-to-text coverage comes from the analyzer, and
   assembly-to-baseline coverage from ApiCompat. Both are enabled, so the chain holds today,
   but the test name overstates what it checks.

3. **The report test only string-matches metadata.** 
   `ApiCompat_report_cites_the_v6_baseline_and_the_tool` (`SdkFreezeTests.cs:138`) asserts that hashes and tool names
   appear in the markdown. The real evidence is the committed output plus the pinned NuGet
   package; the test would not catch a fabricated report with the right strings.

4. **"Calls every member through the real Core service" is not true for the two frozen
   interfaces.** §4 of the freeze record says every test exercises its interface through the
   real Core service, but `IDataSerializer` and `IIdentifiable` are exercised only against
   fakes (they are SDK contracts, not Core services). Wording only.

5. **The v6 report is a removals-only view.** ApiCompat reports v6→v7 removals; v7 additions
   such as the explicit `LibraryGetGamesArgs` constructors are not listed. That is expected
   for the tool, but the document's "complete" should be read as "complete for breaking
   changes".

## Review method and limits

Read-only: I used the supplied diff (verified equal to `git diff base..73f188e`), the repository
files needed to check the plan, baselines, project graph and v6 surface, and the published v6
NuGet package for the hash and metadata checks. I did not edit the candidate, build the
solution, run the tests, or read any other reviewer's output. The blocking finding is a scope
and record-integrity finding; the report's numeric consistency was verified mechanically, not
by re-running ApiCompat.

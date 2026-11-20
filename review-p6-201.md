# Adversarial review — PR #201 (p6-007 URI and description contracts move)

**Verdict: no blocking P1/P2 defect found. Hold for the captain's merge decision.**
Review-only: this review authored no tracked change to the reviewed branch, did not merge, and did
not push to it.

| | |
|---|---|
| Repository | https://github.com/seanburkes/playnite-linux |
| Pull request | https://github.com/seanburkes/playnite-linux/pull/201 |
| Reviewed head | `253d669f50919c044b7f62efc7e41e4a54b661c7` (`253d669f`) |
| Base / merge base | `0187b09fc7e85c25dcba64f9458f88769b2b1622` (`origin/main` at review time) |
| Candidate diff | `data/p6-201/candidate.diff`, sha256 `c0aaca52d76a0c807f1e3db6d37eee423042b9f0365da35aaeb29c46adab1b0e` |
| Diff check | `git diff origin/main...refs/review/201` has exactly that sha256; 23 files, +639 / −189 |
| PR state | open, not draft, 8/8 checks pass, `oracle-change` label present |
| Ledger-validated head | `7675b0455db6207cb69deae36f3a6ca94d6c0eda` (ancestor of the reviewed head), linux-ci run `44732565813` |

## Method

Fetched `refs/pull/201/head` with `git fetch`, confirmed it equals the candidate head above,
generated `git diff origin/main...refs/review/201`, and confirmed its SHA-256 is byte-identical to
the supplied candidate diff. Checked out the head detached in this isolated worktree, ran the
narrow builds, the full Core and Architecture suites, both unit guards, and read the complete
diff plus the moved files at the head.

## Build and test evidence

All commands ran in the reviewed worktree at `253d669f`; full transcripts are in
`data/p1-review-by/logs/201-validated-253d669.log` and `data/p1-review-by/logs/201-guards.log`.

```text
$ dotnet build src/Playnite.SDK.Abstractions -c Release -warnaserror --no-incremental
Build succeeded.  0 Warning(s)  0 Error(s)                 # abstractions_build_exit=0
$ dotnet build src/Playnite.Core -c Release -warnaserror --no-incremental
Build succeeded.  0 Warning(s)  0 Error(s)                 # build_exit=0
$ dotnet test tests/Playnite.Core.Tests -c Release
  total: 3044  failed: 0  succeeded: 3044  skipped: 0      # test_exit=0
$ dotnet test tests/Playnite.Architecture.Tests -c Release
  total: 199  failed: 0  succeeded: 199  skipped: 0        # arch_exit=0
$ python3 scripts/port/check_unit_ownership.py --base origin/main --head HEAD --branch fm/p6-007b3
  offending paths: (none)                                 # ownership_exit=0
$ python3 scripts/port/check_unit_evidence.py --base origin/main --head HEAD --pr 201 --branch fm/p6-007b3
  unit: p6-007; queue status done; fragment head 7675b04, run 44732565813   # evidence_exit=0
```

`gh-axi pr checks 201` reports `linux-build-and-test`, `unit-ownership`, `unit-evidence`,
`inventory-and-parity`, `guard-inventory`, `formal` and both packaging smokes all passing
(8/8). `gh-axi run view 44732565813` reports `completed / success` on branch `fm/p6-007b`, which
matches the ledger fragment's cited run. The PR body's paragraph claiming three checks are red by
design is stale at this head, not a repo artifact.

## Required checks

1. **The move is behaviour-preserving.** `git diff -M --summary` shows
   `src/Playnite.Core/UriHandling/PlayniteUriRoute.cs` renamed to
   `src/Playnite.SDK.Abstractions/UriHandling/PlayniteUriRoute.cs` at 99 % similarity; the only
   hunk is the namespace line. The other moved files are the same bodies under
   `Playnite.SDK` / `Playnite.SDK.Events` / `Playnite.SDK.Descriptions`. The moved declarations
   are deleted from `HtmlDescriptionSanitizer.cs`, `HtmlDescriptionService.cs` and
   `UriHandlerApi.cs`, leaving the concrete implementations in Core. The one non-namespace
   change in `UriHandlerApi.DispatchAsync` replaces the former internal
   `UriDispatchResult.Rejected(...)` factory with `new UriDispatchResult(UriDispatchStatus.Rejected,
   null, ...)`; the internal factory is not visible across assemblies and the produced value is
   identical. No other logic changed.
2. **No duplicate moved types remain in Core.** `grep` for `interface IUriHandlerAPI`,
   `interface IUriCommandHost`, `class PlayniteUriEventArgs`, `interface IHtmlDescriptionProvider`,
   `interface IHtmlSanitizer`, `record UriDispatchResult` and `record HtmlDescriptionResult` finds
   only the Abstractions files.
3. **Sanitisation and size limits are intact.** `HtmlDescriptionSanitizer` still enforces
   `MaxInputLength = 256 * 1024`, `MaxOutputLength = 512 * 1024`, `MaxDepth = 64`,
   `MaxHrefLength = 2048`, the element allowlist and the drop-content list; the diff removes only
   the DTO/interface declarations. The existing sanitizer and service suites run unchanged inside
   the 3,044 passing Core tests.
4. **No Avalonia or foreign Playnite reference leaked.** `Playnite.SDK.Abstractions.csproj` has no
   `ProjectReference` and no Avalonia package; `Playnite.Core.csproj` references only
   `Playnite.SDK.Abstractions`. The Architecture suite (199/199) includes the SDK-boundary,
   dependency-graph and banned-construct tests.
5. **PublicAPI matches the real surface.** The Abstractions build enables
   `ApiCompatValidateAssemblies` against `api-baseline/Playnite.SDK.Abstractions.dll` and requires
   `TreatWarningsAsErrors`; it passed with 0 warnings. The Architecture `PublicApiTests` (baseline
   pin, shipped/unshipped declaration checks) passed as part of 199/199. The regenerated baseline
   binary and its SHA-256 pin are consistent (the pin test passed).
6. **Every moved interface is exercised through a fake host.** `UriHtmlContractTests` (new)
   drives `IUriCommandHost` through all five dispatch paths, `IUriHandlerAPI` through
   register/dispatch/remove, `IHtmlDescriptionProvider` and `IHtmlSanitizer` through the real
   `HtmlDescriptionService`, with recording fakes. The route and description suites keep every
   pre-existing case. Zero skips.
7. **Docs, ledger and queue describe the delivered move.** The unit doc, queue row and ledger
   fragment describe the Abstractions move and cite the contract test. The ApiCompat delta file's
   `216 CP0001 after / 218 before` is consistent with PR #202's independent report, which records
   `210 CP0001` and still lists the two p6-007 rows (`IUriHandlerAPI`,
   `PlayniteUriEventArgs`); 216 minus the six host types #202 moves gives 210. No narrowed-scope
   claim was found.

## Non-blocking observation (P3)

1. **No plugin-only compile check for these contracts.** Unlike PR #202's
   `SdkHostSurfaceTests.Sample_plugin_compiles_against_the_abstractions_assembly_alone`, this PR
   adds no compilation of a sample consumer against `Playnite.SDK.Abstractions` alone. The
   contract tests do compile against the moved types (with Core also referenced), and the
   dependency gate prevents an Avalonia or Core type entering the signatures, so the residual risk
   is low. A small sample-plugin compile test would close the brief's "plugin-facing compile
   check" item cheaply.
2. **Cosmetic:** the new `Abstractions/Descriptions/HtmlDescriptionSanitizer.cs` retains
   `using System.Globalization;` and `using System.Text;` that only the Core implementation uses.
   The warning-free build does not flag them.

## Scope limitation

I did not re-run Microsoft.DotNet.ApiCompat: neither the 6.18.0 `PlayniteSDK` assembly nor the
ApiCompat tool is present in this worktree's package cache. I verified the recorded report's
internal consistency (counts, the two removed rows, and agreement with #202's report) and relied
on the repo's own ApiCompat baseline validation, which did run in the build. The merge decision
remains the captain's; this review did not merge, approve, or push anything.

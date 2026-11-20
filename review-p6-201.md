# Adversarial review — https://github.com/seanburkes/playnite-linux/pull/201 (p6-007 move)

**Verdict: NOT CLEAN — one blocking P2 (missing plugin-facing compile check for the moved
surface). The namespace move itself is behaviour-preserving. Hold for the captain.**
Review-only: no tracked product change was authored by this review.

| | |
|---|---|
| Repository | https://github.com/seanburkes/playnite-linux |
| Pull request | https://github.com/seanburkes/playnite-linux/pull/201 |
| Reviewed head | `253d669f50919c044b7f62efc7e41e4a54b661c7` (`253d669`, merge of current main into the branch) |
| Base / merge base | `0187b09fc7e85c25dcba64f9458f88769b2b1622` (`0187b09`, current `origin/main`) |
| Candidate diff | `/home/sean/src/firstmate/data/p6-201/candidate.diff`, sha256 `c0aaca52d76a0c807f1e3db6d37eee423042b9f0365da35aaeb29c46adab1b0e` |
| Diff shape | 23 files, +639 / −189 (recomputed with `git diff --shortstat`; digest byte-identical to the artifact) |
| Ledger-validated head | `7675b0455db6207cb69deae36f3a6ca94d6c0eda` (ancestor of the reviewed head; linux-ci run `44732565813`) |
| PR state | open, not draft, merge state clean, `oracle-change` label present, 8/8 checks pass |

## Commands run on the reviewed head

All commands were run after `git checkout --detach 253d669f…` in the disposable worktree.

```console
$ sha256sum /home/sean/src/firstmate/data/p6-201/candidate.diff
c0aaca52d76a0c807f1e3db6d37eee423042b9f0365da35aaeb29c46adab1b0e  …/candidate.diff

$ git diff 0187b09fc7e85c25dcba64f9458f88769b2b1622 253d669f50919c044b7f62efc7e41e4a54b661c7 | sha256sum
c0aaca52d76a0c807f1e3db6d37eee423042b9f0365da35aaeb29c46adab1b0e  -

$ git diff --shortstat 0187b09fc7e85c25dcba64f9458f88769b2b1622 253d669f50919c044b7f62efc7e41e4a54b661c7
 23 files changed, 639 insertions(+), 189 deletions(-)

$ python3 scripts/port/check_unit_ownership.py \
    --base 0187b09fc7e85c25dcba64f9458f88769b2b1622 \
    --head 253d669f50919c044b7f62efc7e41e4a54b661c7 --branch fm/p6-007b
unit: p6-007
allowed paths touched: … (18 paths)
offending paths:
  (none)
EXIT=0

$ GH_TOKEN=… python3 scripts/port/check_unit_evidence.py \
    --base 0187b09fc7e85c25dcba64f9458f88769b2b1622 \
    --head 253d669f50919c044b7f62efc7e41e4a54b661c7 --pr 201 --branch fm/p6-007b \
    --repo-slug seanburkes/playnite-linux --verify-ci
unit: p6-007
queue: docs/port/units/p6-007.queue.tsv status done
fragment: docs/port/units/p6-007.ledger.d/201-uri-html-abstractions.tsv
          (head 7675b0455db6207cb69deae36f3a6ca94d6c0eda, run 44732565813)
EXIT=0

$ dotnet build … -c Release -t:Rebuild      # Abstractions, Core, Core.Tests, Architecture.Tests
Build succeeded. 0 Warning(s) 0 Error(s)   (all four projects)

$ dotnet test tests/Playnite.Core.Tests -c Release --no-build
total: 3044  failed: 0  succeeded: 3044  skipped: 0

$ dotnet test tests/Playnite.Architecture.Tests -c Release --no-build
total: 199  failed: 0  succeeded: 199  skipped: 0

$ dotnet test tests/Playnite.Core.Tests -c Release --no-build --filter "FullyQualifiedName~UriHandling"
total: 102  failed: 0  skipped: 0

$ dotnet test tests/Playnite.Core.Tests -c Release --no-build --filter "FullyQualifiedName~Descriptions"
total: 64  failed: 0  skipped: 0

$ python3 scripts/port/build_port_index.py check --shards-only --against origin/main
port index shards are valid; aggregates are generated on main   (exit 0)
```

`gh-axi pr checks 201`: `8 passed, 0 failed` — including `unit-ownership`, `unit-evidence`,
`inventory-and-parity` (the `oracle-change` label is present on the PR, so the body's "expected
red" text about the guards no longer applies).

## What was reviewed

- The R099 `PlayniteUriRoute` rename is a namespace-only edit: `git diff -M --summary` reports
  `rename … (99%)` and the diff contains one hunk changing `namespace Playnite.Core.UriHandling;`
  to `namespace Playnite.SDK;`. No parser, validation or dispatch logic changed.
- The other moved contracts are verbatim bodies with the namespace changed. The only internal
  helper dropped in the move is `UriDispatchResult.Rejected` (internal, no remaining caller:
  `git grep "Rejected("` at the head shows only unrelated methods).
- No Core duplicate declarations remain:
  `git grep -E "(interface|class|record|enum) (IUriHandlerAPI|IUriCommandHost|…|HtmlSanitizationResult)"`
  matches only the Abstractions files. Core keeps only the concrete services
  (`UriHandlerApi`, `HtmlDescriptionService`, `HtmlDescriptionSanitizer`), and the sanitizer still
  implements `IHtmlSanitizer`.
- Sanitisation and limits are untouched and still enforced:
  `HtmlDescriptionSanitizer.MaxInputLength = 256 * 1024`, `MaxDepth = 64`, output/`href` bounds
  present; `Depth_is_bounded` and `Overlong_input_is_truncated` pass in the 64-test description
  filter.
- No Avalonia leak: `git grep -i avalonia -- src/Playnite.SDK.Abstractions src/Playnite.Core`
  returns nothing; Abstractions has no `ProjectReference` at all and Core references only
  Abstractions (both csproj files inspected). `DependencyTests` passes in the 199-test suite.
- PublicAPI/analyzer: Abstractions builds with `TreatWarningsAsErrors=true`; the architecture
  suite's RS0016/RS0017 and ApiCompat baseline tests pass, so `PublicAPI.Unshipped.txt` matches
  the compiled surface.
- Binary baseline: the committed `Playnite.SDK.Abstractions.dll` hashes to
  `b857c115…dbd0`, equal to its `.sha256` pin and README, and contains the moved type names
  (`grep -a` finds `PlayniteUriRoute`, `UriHandlerAPI`, `IHtmlSanitizer`).
- The moved `PlayniteUriEventArgs` is `public class`, i.e. no longer `sealed` as it was in the
  interim Core type. That matches v10 (`legacy/source/PlayniteSDK/Events/PlayniteUriEventArgs.cs:12`)
  and creates no v6 ApiCompat break; it is deliberate, not drift.
- Evidence consistency: the v6 package `playnitesdk/6.18.0` downloaded from NuGet hashes
  `63ae943b…e236`, and its `lib/net462/Playnite.SDK.dll` hashes `ce751194…2fc7`, exactly as both
  PRs' ApiCompat artifacts claim. (I did not re-run ApiCompat for the 216 count; the v6 package
  is not in the offline cache, and the committed report plus the passing baseline test are
  internally consistent.)

## P2 — the moved p6-007 surface has no plugin-facing compile check

The brief's verification item is "tests cover each moved interface with a fake host **plus a
plugin-facing compile check**". The fake-host half is delivered by
`tests/Playnite.Core.Tests/SdkContracts/UriHtmlContractTests.cs` (four tests through
`UriHandlerApi`/`HtmlDescriptionService` and recording fakes). The compile half is absent:

- The reviewed diff touches no file under `tests/Playnite.Architecture.Tests/`
  (`git diff --name-only` lists only Core.Tests files).
- `git grep "IUriHandlerAPI\|IHtmlSanitizer\|IHtmlDescriptionProvider"` over `tests/` matches only
  `tests/Playnite.Core.Tests/**` — a project that references Core, not a plugin-only consumer.
- The only architecture test that asserts Abstractions public types,
  `SdkBoundaryTests.Seed_contracts_are_public_in_the_Avalonia_free_versioned_assembly`, enumerates
  the seven seed types only. The sibling unit PR https://github.com/seanburkes/playnite-linux/pull/202
  adds `SdkHostSurfaceTests.Sample_plugin_compiles_against_the_abstractions_assembly_alone`, which
  compiles a consumer against Abstractions alone; p6-007 has no equivalent.

This is the unit's central claim ("a plugin can consume it with a reference to that assembly
alone"). The dependency test already forbids an Abstractions→Core reference, so the runtime risk
is low, but the review checklist and the sibling unit both require the consumer compile proof, and
a namespace move is exactly where a stray Core-typed signature would otherwise go unnoticed by
reflection-only assertions.

Fix: add a small Roslyn compile test (same shape as `SdkHostSurfaceTests`) that references
`Playnite.SDK.Abstractions` only and implements/uses `IUriHandlerAPI` +
`PlayniteUriEventArgs`, `IHtmlDescriptionProvider` and `IHtmlSanitizer`; then cite it in the queue
row and ledger fragment.

## Non-blocking observations

- `docs/port/units/p6-007.ledger.d/201-uri-html-abstractions.tsv` still ends with
  "the oracle-change label, which this branch does not add; unit-ownership and unit-evidence are
  expected red". At the reviewed head the label **is** present and both guards exit 0. The
  fragment was written before `253d669` merged main; the sentence is stale, not a false
  description of the delivered move.
- `src/Playnite.SDK.Abstractions/Descriptions/HtmlDescriptionSanitizer.cs` (which contains
  `IHtmlSanitizer`, not the implementation) carries unused `using System.Globalization;` and
  `using System.Text;`. Harmless; style only.
- A fresh local `obj/Release/net11.0/ref` build is not byte-identical to the committed baseline
  (MVID/PDB bytes differ by worktree path). The repo pins and tests the committed bytes, not
  reproducibility; this predates the PR and is not a defect in this change.

## Verdict

The move is clean at the code level: byte-for-byte namespace move for the parser, verbatim bodies
for the contracts, no duplicates, no Avalonia, matching PublicAPI, and green unit guards, build
and tests at the reviewed head. The one blocker is the missing plugin-facing compile check, which
the brief explicitly requires and the sibling unit supplies.

# p6-010 / PR #215 — focused DELTA adversarial review

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/215
- Reviewed head: `50993180cccb2a639ae8d9f7e84127a175d4d7c8` (`5099318`, docs-only over the code
  head `6daf89e60f7a46e40519a3b28fa82b27656cd7d4`)
- Delta base: `4aaf227bd2f0683425d9aa2fa7b46ab6c5661b54` (`4aaf227`, already covered by two full
  reviews; settled scope is not re-litigated here)
- Delta diff: `/home/sean/src/firstmate/data/p6-215delta/delta.diff`
  SHA-256 `76c3e6e4495afbda8f8ea84dcc956eba08bc9ba358ba1fb0d2002c7f49517d7f`
- Full diff: `/home/sean/src/firstmate/data/p6-215delta/full.diff`
  SHA-256 `41bef3144ba68d49cb0b9490f5e7bdf545573feb87d1b7d74a440cb0f596b54b`
- Delta size: 10 files, +538/−140 (4 commits: `24bb690`, `d008d96`, `6daf89e`, `5099318`).
- Method: verified both digests, checked out the reviewed head in the disposable worktree, ran
  Release builds and both test suites, re-ran all four mutation claims documented in the unit doc
  and ledger, downloaded both CI artifacts, replayed the cited runs/checks with `gh`/`gh-axi`,
  and read every cited v10 line. Proof logs are outside the worktree in
  `/home/sean/src/firstmate/data/p1-review-ci/`. No product file was left modified.
- Verdict: **no blocking P1 or P2 finding; recommend merge.** All four original P2s are genuinely
  fixed and load-bearing under mutation; every count, run id, head SHA and mutation claim in the
  delta's doc/ledger/queue is true; scope and non-blocking claims hold. Merge authority remains
  with the captain.

## 1. Diff integrity and isolation

```text
$ sha256sum delta.diff full.diff
76c3e6e4495afbda8f8ea84dcc956eba08bc9ba358ba1fb0d2002c7f49517d7f  delta.diff
41bef3144ba68d49cb0b9490f5e7bdf545573feb87d1b7d74a440cb0f596b54b  full.diff

$ git diff 4aaf227..5099318 | sha256sum
76c3e6e4495afbda8f8ea84dcc956eba08bc9ba358ba1fb0d2002c7f49517d7f  -
$ git diff origin/main..5099318 | sha256sum
41bef3144ba68d49cb0b9490f5e7bdf545573feb87d1b7d74a440cb0f596b54b  -

$ git log --oneline 4aaf227..5099318
5099318 docs(p6-010): cite the final linux-ci run for the follow-up head
6daf89e test(p6-010): prove the normalized containment guard directly
d008d96 fix(p6-010): type-check the package manifest before reading it
24bb690 fix(p6-010): match v10 discovery and reject unsafe package entries
```

Both supplied diffs are byte-identical to the reviewed commits. The worktree top level resolved to
the disposable treehouse path, and the common dir to the primary checkout, so no branch/commit was
made in the primary checkout.

## 2. Baseline by execution (reviewed head `5099318`)

```text
$ dotnet build src/Playnite.Platform.Linux/Playnite.Platform.Linux.csproj -c Release -warnaserror
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:05.49
EXIT=0

$ dotnet test tests/Playnite.Core.Tests -c Release --no-restore
  total: 3125
  failed: 0
  succeeded: 3125
  skipped: 0
  duration: 35s 316ms
EXIT=0

$ dotnet test tests/Playnite.Architecture.Tests -c Release --no-restore
  total: 210
  failed: 0
  succeeded: 210
  skipped: 0
EXIT=0
```

These reproduce the three local numbers quoted in the unit doc and ledger exactly.

## 3. The four original P2s are fixed, and the fixes are load-bearing

### (a) Recursive discovery incl. `<root>/<id>/extension.yaml`, symlink refusal, depth bound

`GetInstalled()` now walks a stack from the root, checks every directory for `extension.yaml`,
skips symlinked directories (`child.LinkTarget is null`) and symlinked manifests
(`manifest.LinkTarget is not null`), skips unreadable directories, bounds descent with
`MaxDiscoveryDepth = 8`, and sorts by manifest path. The id is the first segment under the root,
so uninstall still targets the owning directory. The oracle is real:

```text
$ sed -n '248,251p' legacy/source/Playnite/Plugins/ExtensionFactory.cs
var enumerator = new SafeFileEnumerator(PlaynitePaths.ExtensionsUserDataPath,
    PlaynitePaths.ExtensionManifestFileName, SearchOption.AllDirectories);
```

Tests `GetInstalled_discovers_an_add_on_in_the_v10_one_directory_layout`,
`GetInstalled_does_not_follow_a_symlinked_directory` and
`GetInstalled_stops_at_the_maximum_discovery_depth` pass in the baseline and are present as
passing cases in the CI artifact (Core XML). The doc's asymmetry fix is honest: it says v10 has no
depth bound and that this model adds one.

### (b) Non-regular package entry and non-regular `extension.yaml` rejected without blocking

The installer checks `fileTypeProbe.IsRegularFile(sourceManifest)` before `File.ReadAllText`, and
`ListEntries` only admits `FileInfo` when the injected probe says regular; every other entry type
hits `unsupported-entry`. The Linux implementation uses
`openat(O_PATH|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC)` + `statx(AT_EMPTY_PATH, STATX_TYPE)`; the `statx`
mode field is at offset 28 of the 256-byte ABI, matching `LinuxBoundedFileReader`, and the three
entry points are on the reviewed allow-list (`SourcePolicy.cs:37`, architecture suite 210/210).

```text
$ dotnet test tests/Playnite.Core.Tests -c Release --no-restore --filter "FullyQualifiedName~Install_rejects_a_fifo"
  total: 2, failed: 0, succeeded: 2, skipped: 0, duration: 1s 451ms
EXIT=0 WALL=4s
```

Both FIFO tests finish in milliseconds, not at their 10-second guard. The two mutation proofs
below confirm each guard is the reason.

### (c) Rollback proven by a test that fails when the block is deleted

Mutation M1 (exact replacement, then restore from a saved copy):

```text
$ dotnet test tests/Playnite.Core.Tests -c Release --no-restore --filter "FullyQualifiedName~AddonInstallationTests"
failed Playnite.Core.Tests.Addons.AddonInstallationTests.Install_removes_the_partial_add_on_when_a_later_copy_fails (2ms)
  Assert.False() Failure
  Expected: False
  Actual:   True
    at .../AddonInstallationTests.cs:226
  total: 21, failed: 1, succeeded: 20, skipped: 0
EXIT=2
```

Line 226 is `Assert.False(Directory.Exists(Path.Combine(root, "Test_Plugin")))`, so the failure is
specifically the leftover partial add-on. Restoring gives 3125/3125 again. The test is not
root-dependent: CI's `linux-ci` runs on `ubuntu-24.04` as the non-root `runner` user (no root
container), and the rollback case passes in the downloaded CI artifact:

```text
$ grep -o 'testcase name="[^"]*Install_removes_the_partial_add_on_when_a_later_copy_fails[^"]*"[^>]*' \
    ci-artifact/Playnite.Core.Tests/Playnite.Core.Tests.xml
testcase name="...AddonInstallationTests.Install_removes_the_partial_add_on_when_a_later_copy_fails" time="0.002000" ... />
```

### (d) Theme install removed and deferred with correct citations

`InstallTheme`, `ThemeManifestFileName` and the theme-layout test are gone; `ValidateTheme`
remains as the document boundary. The v10 lines cited for the deferral are correct:

```text
$ sed -n '199,205p' legacy/source/Playnite/Plugins/ExtensionInstaller.cs
if (manifest is ThemeManifest themeMan)
{
    rootDir = Path.Combine(rootDir, themeMan.Mode.ToString());
}
...
var installDir = Path.Combine(rootDir, Paths.GetSafePathName(manifest.Id));

$ sed -n '237,258p' legacy/source/Playnite/Themes.cs
public static List<ThemeManifest> GetAvailableThemes(ApplicationMode mode)
...
foreach (var dir in Directory.GetDirectories(userPath))
...
var descriptorPath = Path.Combine(dir, PlaynitePaths.ThemeManifestFileName);
```

v10 inserts the `Mode` segment and discovers exactly one level down (no recursion); an
`AddonInstallLayout` rooted at `Extensions` could not represent that, so deferral is the right
call and is recorded in both the doc (§3, §4, §6) and the new queue/ledger text.

## 4. Mutation re-runs — all four documented claims reproduced

Each mutation was applied to the reviewed head, the relevant tests were run, and the product files
were restored from byte-copies. `git status --short` was empty after restoration, and the full
suite was re-run green.

| Mutation | Observed | Doc/ledger claim |
| --- | --- | --- |
| M1 delete rollback block | `AddonInstallationTests` 21 total / **1 failed** / 20 passed, `EXIT=2`; failure is the residue assertion | "rollback removed -> rollback test fails" ✓ |
| M2 `IsWithin` → `return true` | `AddonInstallLayoutTests` 7 total / **4 failed** / 3 passed, `EXIT=2` | "4 failed/3 passed, restored 7/7" ✓ |
| M3 probe → `return true` | both FIFO tests **failed at 10s 028ms / 10s 003ms**, 2 failed / 0 passed, `EXIT=2` | "probe guard removed -> FIFO tests fail at their 10s bound" ✓ |
| M4 manifest type check removed | manifest FIFO **failed at 10s 004ms**, package FIFO passed: 2 total / 1 failed / 1 passed, `EXIT=2` | "manifest type check removed -> manifest FIFO test fails at 10s" ✓ |

M2 failing cases (verbatim from the log): sibling prefix collision, absolute path outside the root,
and both `..` traversal theory rows (`../escape/Plugin.dll`, `1.0/../../escape/Plugin.dll`). The two
positive rows and the trailing-separator root row passed, so the predicate is prefix-boundary
correct and the tests distinguish it from a bare `StartsWith`.

After restoring all four mutations:

```text
$ dotnet test tests/Playnite.Core.Tests -c Release --no-restore
  total: 3125, failed: 0, succeeded: 3125, skipped: 0
EXIT=0
```

## 5. Containment honesty

`AddonInstallLayout.IsWithin` only normalises both paths and appends the directory separator to the
root before `StartsWith`; the doc calls it defence-in-depth and names `AddonPathSafety`'s lexical
rules as the primary guard. That is accurate: the new direct tests cover a valid child, sibling
prefix collision (`/ext/ab` does not accept `/ext/abc`), `..` traversal, an absolute path outside
the root and a trailing-separator root. No stronger claim is made for the predicate, and the
five-case direct proof is load-bearing under M2.

## 6. Doc/ledger/queue counts, run ids and heads — all verified

```text
$ gh-axi pr view 215
  draft: no
  checks: "8 passed, 0 failed, 8 total"
  merge_state: clean

$ gh-axi pr checks 215
summary: "8 passed, 0 failed, 8 total"
  linux-build-and-test,pass   ... (8 checks, all pass)

$ gh api repos/seanburkes/playnite-linux/actions/runs/44840650650 --jq '{id,name,status,conclusion,head_sha}'
{"conclusion":"success","head_sha":"b67d995653fe7f376e629a96615ab75832603544","id":44840650650,"name":"linux-ci","status":"completed"}
$ gh api repos/seanburkes/playnite-linux/actions/runs/44847415395 --jq '{id,name,status,conclusion,head_sha}'
{"conclusion":"success","head_sha":"6daf89e60f7a46e40519a3b28fa82b27656cd7d4","id":44847415395,"name":"linux-ci","status":"completed"}
$ gh api repos/seanburkes/playnite-linux/actions/runs/44847415494 --jq '{id,name,conclusion,head_sha}'
{"conclusion":"success","head_sha":"6daf89e60f7a46e40519a3b28fa82b27656cd7d4","id":44847415494,"name":"port-unit-guard"}
$ gh api repos/seanburkes/playnite-linux/actions/runs/44847415375 --jq '{id,name,conclusion,head_sha}'
{"conclusion":"success","head_sha":"6daf89e60f7a46e40519a3b28fa82b27656cd7d4","id":44847415375,"name":"plan-guards"}
$ gh api repos/seanburkes/playnite-linux/actions/runs/44847415383 --jq '{id,name,conclusion,head_sha}'
{"conclusion":"success","head_sha":"6daf89e60f7a46e40519a3b28fa82b27656cd7d4","id":44847415383,"name":"packaging"}
```

Downloaded `linux-test-results` artifacts confirm the quoted counts:

```text
run 44840650650 test-counts.json:
  Core 3113/3113, Architecture 210/210, Smoke 4/4, Plugin 15/15, 0 failed, 0 skipped, exit 0
run 44847415395 test-counts.json:
  Core 3125/3125, Architecture 210/210, Smoke 4/4, Plugin 15/15, 0 failed, 0 skipped, exit 0
```

Both heads also have eight successful check runs each:

```text
$ gh api repos/seanburkes/playnite-linux/commits/50993180cccb2a639ae8d9f7e84127a175d4d7c8/check-runs --jq '[.check_runs[] | {name,conclusion,head_sha}] | {count:length, items:.}'
{"count":8,"items":[ ... 8 entries, each conclusion="success", head_sha="50993180cccb..." ... ]}
$ gh api repos/seanburkes/playnite-linux/commits/6daf89e60f7a46e40519a3b28fa82b27656cd7d4/check-runs --jq '[.check_runs[] | {name,conclusion,head_sha}] | {count:length, items:.}'
{"count":8,"items":[ ... 8 entries, each conclusion="success", head_sha="6daf89e60f7a..." ... ]}
```

So the doc's "8 passed" is true for the code head and the docs head is separately green; the
`head_sha` and run id claims are exact.

## 7. Scope, protected paths and no-execution claim

```text
$ git diff --name-only 4aaf227..5099318
docs/port/p6-010-addon-model.md
docs/port/units/p6-010.ledger.d/215-addon-model.tsv
docs/port/units/p6-010.queue.tsv
src/Playnite.Core/Addons/AddonInstallLayout.cs
src/Playnite.Core/Addons/AddonInstaller.cs
src/Playnite.Core/Addons/AddonOperationResults.cs
src/Playnite.Core/IRegularFileProbe.cs
src/Playnite.Platform.Linux/LinuxRegularFileProbe.cs
tests/Playnite.Core.Tests/Addons/AddonInstallLayoutTests.cs
tests/Playnite.Core.Tests/Addons/AddonInstallationTests.cs
```

- No change under `legacy/`, `oracle-lock/`, `tests/golden/`, `tests/fixtures/`, `tests/baseline.json`,
  `docs/formal/`, `docs/port/surface-parity.tsv`, or the generated `queue.tsv`/`ledger.tsv`
  aggregates; the p3-005 `Manifests/*`, fixtures and manifest tests are untouched.
- Added `DllImport`s are confined to `Playnite.Platform.Linux` and use allow-listed entry points;
  architecture suite 210/210. The only new `Process.Start` is a test helper that runs `mkfifo`
  with `UseShellExecute = false` and an argument list, not a shell string, and no product path
  starts a process or downloads/executes a package.
- No `[Skip]`, `[Ignore]`, filter exclusion, snapshot re-acceptance or test deletion: skipped stays
  0 in every run and artifact.

## 8. Non-blocking observations (P3 — do not hold the merge)

1. `docs/port/p6-010-addon-model.md` §5 says "Two negative proofs were run locally" but lists three
   bullets (rollback, probe guard, `IsWithin`); the ledger lists four mutations including the
   manifest type check. Pure editorial count; every underlying proof was reproduced above.
2. The queue row's `test:` column still lists only `AddonManifestValidationTests` and
   `AddonInstallationTests`; the new `AddonInstallLayoutTests` is named only in the notes and doc
   table. Omission, not a false claim.
3. `GetInstalled()` reads a discovered manifest with `File.ReadAllText` without the regular-file
   probe, so a FIFO planted directly in the data tree could block discovery. The probe's documented
   scope is the install path, both supported install layouts produce regular files, and this read
   path existed before the delta; not a delta regression.
4. The installation tests construct `LinuxRegularFileProbe` unconditionally, so they would throw
   `PlatformNotSupportedException` if ever run on Windows. The Core suite is gated by Linux CI
   only; the Windows workflows run oracle/fixture validation.

## Recommendation

**No blocking findings; recommend merge** of https://github.com/seanburkes/playnite-linux/pull/215
at head `5099318`. The four original P2s are fixed by code that is actually observed by tests, all
documented mutation evidence reproduces exactly, the forge/CI counts and heads are true, and the
delta stays inside its ownership scope. This review PR contains only this document; the merge of
https://github.com/seanburkes/playnite-linux/pull/215 remains with the configured merge authority.

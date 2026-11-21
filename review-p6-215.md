# p6-010 / PR #215 review — add-on manifest validation and install directory model

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/215
- Reviewed head: `4aaf227bd2f0683425d9aa2fa7b46ab6c5661b54`
- Base: `8bb26178d75e4bf59dbf6ad7561730e4d39ed56f` (current `origin/main` and the PR
  merge base)
- Candidate diff: `data/p6-215/candidate.diff`
- Candidate diff SHA-256: `ba17809756e57c93b65e1e628b4dbb1119b4dc5d6dc0c86593832848a6daccef`
- Size: 13 files, +1472/−0
- Method: fetched `refs/pull/215/head`, recomputed `git diff 8bb2617 4aaf227` and confirmed it
  is byte-identical to the candidate diff (same SHA-256), built and tested the reviewed head in
  a scratch clone outside the worktree, read the frozen v10 sources for every cited oracle line,
  wrote adversarial probe tests, and ran a five-way mutation matrix against the new add-on
  tests. Proof output is kept in a local directory outside the worktree. No file in the
  worktree was edited except this review.
- Verdict: **no P1 finding; one blocking P2** — the installer's failure/rollback surface has no
  test, and the rollback code can be deleted with the whole add-on suite green.

## Blocking findings

### P2-1 — the install/uninstall failure surface is untested; the rollback branch can be deleted with the suite green

`AddonInstaller` documents and the unit ledger claims two failure behaviours as delivered:
"on a copy failure remove the partial id directory and report `install-failed`" and "rolls back
a partial install" (`docs/port/p6-010-addon-model.md` §3 item 5 and the queue/ledger evidence).
No test exercises a copy failure. Removing the rollback body
(`src/Playnite.Core/Addons/AddonInstaller.cs:216`, the `Directory.Delete` in the copy-failure
handler) leaves **47/47** of the new add-on tests passing:

```text
$ dotnet test tests/Playnite.Core.Tests -c Release \
    --filter "FullyQualifiedName~AddonInstallationTests|FullyQualifiedName~AddonManifestValidationTests"
m0-baseline                     total 47  failed 0  succeeded 47  skipped 0
m4-rollback-removed             total 47  failed 0  succeeded 47  skipped 0
```

The rollback is the only code path in the unit that recursively deletes a directory on an error
path, so it is exactly the branch a regression would damage silently. The behaviour itself is
correct today: an independent probe (package containing a mode-`000` file, so the second
`File.Copy` fails) produced

```text
install-failed at $: Copying into '…/Extensions/Rollback_Probe/1.0' failed: Access to the path
'…/package/secret.dll' is denied.; addonDirExists=False; rootExists=True
```

so the gap is evidence, not product behaviour: the fix is a test, not a code change.

Related, same class: 10 of the 17 diagnostic codes the unit can produce are never asserted by a
test — `install-failed`, `uninstall-failed`, `missing-source`, `missing-manifest`,
`unreadable-manifest`, `unreadable-source`, `not-installed`, `unsafe-path`,
`unsupported-entry`, `unsafe-version`. Four of those (`missing-source`, `missing-manifest`,
`not-installed`, `unsafe-version`) need no fault injection at all. Coverage asserted today is
`invalid-manifest`, `unsafe-id`, `unsafe-module`, `unsafe-icon`, `duplicate-addon`,
`version-mismatch` and `symlink`.

Asks (all test-only, no product change expected): a rollback test that fails a copy after the
version directory exists and asserts `install-failed` plus no `<id>` directory; and direct cases
for `missing-source`, `missing-manifest`, `not-installed` and `unsafe-version`.

## Checks performed

### 1. Ownership, base and protected paths

```sh
git merge-base origin/main 4aaf227             # 8bb26178d75e4bf59dbf6ad7561730e4d39ed56f
git diff 8bb2617 4aaf227 | sha256sum           # ba1780…, identical to candidate.diff
git diff --numstat 8bb2617 4aaf227             # 13 files, 1472 added, 0 deleted
python3 scripts/port/check_unit_ownership.py \
  --base 8bb2617 --head 4aaf227 --merge-base 8bb2617 --unit p6-010
```

Observed: 12 added files and one modified file
(`tests/Playnite.Core.Tests/Playnite.Core.Tests.csproj`, +1 line copying
`../golden/manifests/*` into the test output). All seven product files are new
`src/Playnite.Core/Addons/*` files; there is no modification to
`src/Playnite.Core/Manifests/*`, `tests/Playnite.Core.Tests/ManifestTests.cs`,
`tests/fixtures/manifests-v10/*` or `docs/port/p3-005-manifests.md`, so the p3-005 namespace is
genuinely untouched. The ownership check reports `offending paths: (none)`.

A diff over `tests/golden`, `tests/fixtures`, `tests/baseline.json`, `docs/formal`,
`docs/port/surface-parity.tsv` and `legacy/` is empty (0 lines), so no `oracle-change` label is
needed and the golden add-on documents are read-only inputs. The port-index shard check passes
against `origin/main` (`port index shards are valid; aggregates are generated on main`), so the
unit carries its own queue/ledger fragment and does not rewrite an aggregate.

### 2. The p3-005 boundary is reused, not weakened

The strict parser is imported and called unchanged; the PR never edits it. A document that
p3-005 rejects still fails here, with one explicit exception keyed to the v10 default-`Type`
capture:

```text
ManifestSerialization.FromYaml<ExtensionManifest>("Id: x\nVersion: 1.0\nModule: x")
  -> SerializationException: YAML: $: missing Type.
AddonManifestValidator.ValidateExtension(same document)
  -> valid, Type = GenericPlugin
```

Probes confirmed the retry is not general leniency. Every one of these still fails with the same
code/path the strict boundary produces:

```text
Id: '../x' …                                             -> unsafe-id at $.Id
Id: x … Links: [null]                                    -> invalid-manifest at $.Links[0]
Id: x … Version: nope                                    -> invalid-manifest at $.Version
Id: x … Type: null                                       -> invalid-manifest at $.Type
Id: x … Type: NotAType                                   -> invalid-manifest at $
Id: x / Id: y …                                          -> invalid-manifest at $
Id: x … (no Module)                                      -> invalid-manifest at $.Module
```

Exact messages are kept in the review's probe log outside the worktree; the
null-`Type` case keeps the parser's quoted path form
(`value at '$.Type': null cannot be stored in Playnite.Core.Manifests.ExtensionType.`), which
`MemberPath` extracts as `$.Type`.

The mechanism is visible in `src/Playnite.Core/Manifests/ManifestSerialization.cs:131-134`: the
discriminator requirement is gated on `type == typeof(ExtensionManifest)`, and the retry parses
with a private derived wire type. Everything else in that boundary is type-independent, so the
only rule suspended for the retry is the discriminator itself. This reviewer's judgement is that
the workaround is sound and honestly documented, but see P3-4 for the fragility of keying it to
an exception message.

### 3. Hostile input is rejected with specific diagnostics and no disk writes

Probes fed a validated rejection matrix through `ValidateExtension` and through
`AddonInstaller.InstallExtension`, then checked the filesystem:

```text
id=../escape  unsafe-id      id=..   unsafe-id      id=a/b  unsafe-id
id=a\b        unsafe-id      id="/tmp/x" unsafe-id   id="C:\x" unsafe-id
id=" x"       unsafe-id      id="x " unsafe-id       id="a<TAB>b" unsafe-id
id="."        unsafe-id      rooted/absolute: unsafe-id
module ../../evil.dll, /usr/lib/evil.dll, lib/../../evil.dll, lib\evil.dll -> unsafe-module at $.Module
icon ../icon.png, /tmp/icon.png                                            -> unsafe-icon at $.Icon
```

One probe case is *accepted* rather than rejected: `CON` (likewise `nul`, `Addon.`) — see P3-3,
which is about the portability wording, not about traversal.

`Install_rejects_path_traversal_in_the_add_on_id` (4 theory rows) and the module theory (3 rows)
pass; after every rejected install in that matrix the extensions root had never been created
(`Directory.Exists(root) == false`). That is structural: in `AddonInstaller.InstallCore` the
manifest is parsed, validated, version-checked, duplicate-checked and every package entry is
validated *before* the first write, which is `Directory.CreateDirectory(versionDirectory!)` at
`AddonInstaller.cs:203`. `version-mismatch`, `duplicate-addon` and `symlink` are all returned
before that line as well, and the duplicate test asserts the existing install is untouched.

Package symlinks are rejected from `FileSystemInfo.LinkTarget` before the entry is queued, so a
link escapes neither the enumeration nor the copy. Mutation m3 (disabling the `LinkTarget`
check) fails one test, so that guard is load-bearing.

### 4. Deterministic layout, real containment and uninstall residue

The reviewed layout is `<extensionsRoot>/<id>/<version>/…` with the version normalised through
`System.Version`, and the tests prove the same package produces the same relative tree and the
same bytes under two different roots.

Containment is not a bare string prefix: `AddonInstallLayout.IsWithin`
(`AddonInstallLayout.cs:155-162`) normalises both paths and appends the directory separator to
the root before comparing. A reflection probe against the real method shows the boundary
behaviour:

```text
IsWithin(/a/b, /a/b/c)      = True
IsWithin(/a/b, /a/b/./c)    = True
IsWithin(/a/b, /a/bc)       = False      # sibling with a shared prefix
IsWithin(/a/b, /a/b/../c)   = False
```

Every copy target is checked with it before the copy loop. Note that the check is defence in
depth: package entries come from real directory enumeration and are additionally constrained by
`AddonPathSafety.IsSafeRelativePath`, so mutation m5 (disabling the `IsWithin` call) also leaves
the suite green. That is acceptable for a second line of defence, unlike the rollback gap.

Uninstall deletes the whole `<id>` tree and re-checks it is gone. An independent probe planted an
unexpected file in `<id>/1.0` plus a second, hand-made `<id>/2.0/nested` version tree, then
uninstalled:

```text
uninstall-extra removed=True succeeded=True idExists=False rootEntries=0
```

so residue removal covers files the installer did not write and versions it did not install.

Both symlink probes for the destructive path behaved safely: when `<id>` is a symlink to an
outside directory, `Uninstall` removes the link and leaves the target intact
(`succeeded=True removed=True outsideExists=True`); the same holds when a *version* directory
inside `<id>` is a symlink. No deletion escaped the extensions root.

### 5. No store, network, download, update or execution path

Grep over the new product and test files finds no `HttpClient`, `WebClient`, `Socket`,
`Process.`, `Assembly.Load`, `DllImport` or shell use. The only filesystem work is `System.IO`
under the extensions root. `tests/Playnite.Architecture.Tests` (which owns the banned-API and
project-graph gates) passes 210/210 at this head, so the new public types add no forbidden
dependency and Core still has no Avalonia reference. There is no caller of the add-on model in
`src/` yet (only tests), consistent with a model-only unit whose store is v1.1.

### 6. Goldens are real and `GetInstalled` is *not* the same rule as v10 discovery

`tests/golden/manifests/*.yaml` are the phase-1 oracle capture
(`523e090 phase1: add v10 manifest goldens from the Windows oracle`); the PR copies them into
the test output without writing them. `TestPlugin.extension.yaml` really does omit `Type`, which
is why the compatibility retry exists, and both goldens plus the frozen
`tests/fixtures/manifests-v10/*` extension/theme documents validate through the real corpus.

The PR body says "`GetInstalled()` walks the same recursive `extension.yaml`/`theme.yaml` rule v10
discovery uses", and the unit doc's §3 says it returns the manifest "under the recursive
discovery rule used by `ExtensionFactory`". That is not what the code does: v10 enumerates
`PlaynitePaths.ExtensionsUserDataPath` with `SafeFileEnumerator(..., SearchOption.AllDirectories)`
(`legacy/source/Playnite/Plugins/ExtensionFactory.cs:248-262`), while `GetInstalled` inspects
exactly two levels, `<root>/<id>` then `<root>/<id>/<version>`
(`AddonInstallLayout.cs:116,125`). A flat v10 install therefore stays invisible:

```text
Extensions/Test_Plugin/extension.yaml   (v10 install layout)
flat-v10-layout discovered count=0
```

New installs are visible (the tests assert it, and v10's own recursive walk still finds
`<id>/<version>/extension.yaml`), so no v1 consumer is wrong today and the add-on ecosystem
migration is explicitly post-v1 (`docs/port/00-DECISIONS.md`). This is a documentation accuracy
problem, graded P3-1, not a functional blocker.

### 7. Tests and CI evidence

Local Release runs at the reviewed head; the logs are kept outside the worktree:

```text
dotnet build src/Playnite.Core/Playnite.Core.csproj -c Release -warnaserror
  Build succeeded. 0 Warning(s), 0 Error(s)                      exit 0

dotnet test tests/Playnite.Core.Tests -c Release
  total 3113, failed 0, succeeded 3113, skipped 0                exit 0

dotnet test tests/Playnite.Architecture.Tests -c Release
  total 210, failed 0, succeeded 210, skipped 0                  exit 0
```

This reproduces the unit's cited numbers exactly, and 3113 includes the 47 new add-on tests
(16 installation, 31 validation). The reviewed head is a docs-only delta over the CI head:

```sh
git log --oneline 4aaf227 -3     # 4aaf227 docs: evidence, b67d995 feat: code, 8bb2617 main
git diff --stat b67d995 4aaf227  # 2 files, +21 (unit doc, ledger fragment)
```

The cited run is real and matches its claim:

```text
gh api repos/seanburkes/playnite-linux/actions/runs/44840650650
{"conclusion":"success","event":"pull_request","head_branch":"fm/p6-010",
 "head_sha":"b67d995653fe7f376e629a96615ab75832603544","status":"completed"}

gh-axi pr view 215 -> draft: no, checks: 8 passed, 0 failed, 8 total, merge_state: clean
```

I did not download the artifact; the local counts above are an independent reproduction of the
same totals.

### 8. Load-bearing tests (mutation matrix)

Each mutation was applied to the reviewed head, the two add-on test classes were run, and the
file was restored (`git status` clean afterwards). Baseline is 47/47.

| Mutation | Result |
| --- | --- |
| m1 `AddonPathSafety.IsSafeSegment` always true | **12 failed** |
| m2 `AddonPathSafety.IsSafeRelativePath` always true | **9 failed** |
| m3 package symlink rejection disabled | **1 failed** |
| m4 rollback body removed (`AddonInstaller.cs:216`) | 0 failed — see P2-1 |
| m5 `IsWithin` target check disabled | 0 failed — defence in depth (see §4) |

The id/module/icon traversal guards and the symlink guard are therefore observed by the tests,
and the rollback is not.

## Non-blocking observations (P3)

1. **`GetInstalled` is depth-2, not v10-recursive** (see §6). Either narrow the sentence in
   `docs/port/p6-010-addon-model.md` §3 and the PR body to "the layout this unit creates", or
   make the enumeration actually recursive and validate the discovered manifest's `Id` against
   the directory name. Today a v10-installed add-on is invisible to the read model while
   `Uninstall` would still delete it, which is an asymmetry worth naming before the store is
   built.
2. **The documented diagnostic-code set is incomplete.** §2's table lists only
   `invalid-manifest`, `unsafe-id`, `unsafe-module` and `unsafe-icon`, but the code also emits
   `unsafe-version`, `duplicate-addon`, `version-mismatch`, `install-failed`,
   `uninstall-failed`, `not-installed`, `missing-source`, `missing-manifest`,
   `unreadable-manifest`, `unreadable-source`, `unsafe-path`, `unsupported-entry` and
   `symlink`. Only the four listed codes are public constants; the rest are bare string literals
   inside the implementations, which makes the "stable codes for callers" contract partly
   unenforced. Promote them to constants and document them, or state that only the four are the
   frozen contract.
3. **"One portable directory name" is not literally portable.** `IsSafeSegment` accepts Windows
   device names and trailing-dot names, which Windows normalises or reserves:

   ```text
   id-name=CON: ok=True   dir=…/CON
   id-name=nul: ok=True   dir=…/nul
   id-name=Addon.: ok=True dir=…/Addon.
   id-name=Addon : ok=False
   ```

   On Linux these are distinct, so this is a claim-versus-reality nit for a Linux-first port, but
   the comment says "safe on every supported host". If the host set is Linux-only, say so; if
   not, reject `CON`/`PRN`/`AUX`/`NUL`/`COM1-9`/`LPT1-9` and trailing dots.
4. **The v10 default-`Type` retry is keyed to exception message text**
   (`exception.Message.Contains("missing Type")`). It works and it is bounded to the
   discriminator, but a reworded p3-005 message would silently disable the compatibility rule.
   The oracle-golden test would go red, so the regression is caught; a typed error or an
   explicit parser option would be sturdier than string matching.
5. **`ValidateExtension` can return an instance of a private derived type.**
   `AddonManifestValidator.ValidateExtension` types the result as `ExtensionManifest`, but for
   the omitted-`Type` document the runtime type is the private `V10ExtensionManifest`, so
   `value.GetType() == typeof(ExtensionManifest)` is false and a serializer sees a different
   wire type name. Nothing in the unit depends on it; worth a note if the API is frozen later.
6. **The version segment is currently unreachable as a multi-version feature.** The layout is
   `<id>/<version>`, but installation refuses any existing `<id>` (`duplicate-addon`), so a
   second version can never be installed; the doc's "two versions cannot silently overwrite each
   other" is true but vacuous until the update path exists. Fine for v1 — just do not let later
   work infer that version coexistence is already proven.
7. **An empty optional `Icon: ''` is rejected as `unsafe-icon`.** v10 treats an empty icon as
   "no icon"; no golden exercises the empty or whitespace-only value. Also, installation
   succeeds when the manifest's `Module` file is absent from the package
   (`installed=True … moduleExists=False`), which matches v10 but means "installed" does not
   imply "loadable". Both are documentation/precision items.
8. `GetInstalled` follows a symlinked `extension.yaml`/`theme.yaml` manifest when deciding what
   is installed (the directory symlink cases are guarded/safe, the file case is not), and it
   lets `UnauthorizedAccessException` escape instead of returning diagnostics. Low impact for a
   same-user data directory.

## Recommendation

**Hold for P2-1.** The product code is careful, the validators are genuinely load-bearing under
mutation, hostile input never reaches the disk, containment and uninstall are correct in my
probes, there is no network/process/loader path, the p3-005 boundary is reused rather than
loosened, and the cited CI head and local counts reproduce. The single blocker is evidence: the
rollback and most of the failure diagnostics are untested, so the ledger's "rolls back a partial
install" claim is not guarded by anything. Adding the rollback test plus the four trivial
diagnostic cases should be enough, and it needs no product change. All P3 items are wording or
hardening and none is blocking.

This review PR contains only this document; the merge of
https://github.com/seanburkes/playnite-linux/pull/215 remains with the configured merge
authority.

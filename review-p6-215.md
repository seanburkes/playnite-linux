# p6-010 / PR #215 review — add-on manifest validation and install model

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/215
- Reviewed head: `4aaf227bd2f0683425d9aa2fa7b46ab6c5661b54`
- Base: `8bb26178d75e4bf59dbf6ad7561730e4d39ed56f` (`origin/main`, equal to the worktree HEAD)
- Candidate diff: `data/p6-215/candidate.diff`
- Candidate diff SHA-256: `ba17809756e57c93b65e1e628b4dbb1119b4dc5d6dc0c86593832848a6daccef`
- Diff integrity: `git diff 8bb2617 4aaf227` has the same SHA-256 and is byte-identical to the
  candidate.
- Size: 13 files, +1472/−0 (12 additions and one line in the Core test project).
- Method: applied the candidate to a disposable worktree at the reviewed base, built with
  warnings as errors, ran the Core and Architecture suites, replayed the cited CI run, and ran
  public-API probe and mutation tests from outside the worktree
  (`data/p1-review-ce/probe-p6-215.txt`, `data/p1-review-ce/evidence-p6-215.md`).
- Verdict: **four blocking P2 findings; changes requested before merge. No P1 found.**

## Blocking findings

### P2-1 — `GetInstalled()` is not recursive and cannot see any v10-installed add-on

`AddonInstallLayout.GetInstalled()` walks exactly two levels —
`<root>/<id>/<version>/{extension,theme}.yaml` — (`src/Playnite.Core/Addons/AddonInstallLayout.cs:108-152`).
The oracle it cites is recursive: v10 enumerates `extension.yaml` under
`ExtensionsUserDataPath` with `SearchOption.AllDirectories`
(`legacy/source/Playnite/Plugins/ExtensionFactory.cs:248-251`). The unit document says
`GetInstalled()` returns the manifest "under the recursive discovery rule used by
`ExtensionFactory`" (`docs/port/p6-010-addon-model.md:62-63`), and the API summary says the
same (`AddonInstallLayout.cs:104-106`).

Probe on a root containing all three layouts returned only the new one:

```text
<root>/V10_Layout/extension.yaml          (v10 layout)       NOT found
<root>/folder/Nested_Id/extension.yaml    (arbitrary depth)  NOT found
<root>/Port_Layout/1.0/extension.yaml     (p6-010 layout)    found
```

This is not a hypothetical layout. v10 installs every add-on to
`Path.Combine(rootDir, GetSafePathName(manifest.Id))`, so the manifest sits at
`<ExtensionsUserDataPath>/<id>/extension.yaml`
(`legacy/source/Playnite/Plugins/ExtensionInstaller.cs:204-205`). Because the only discovery API
delivered by this unit is fixed at `<id>/<version>`, every add-on a user already has from v10 is
invisible to a later loader built on it, while the document claims the opposite.

Fix (either is acceptable, but the claim and the code must agree):

- enumerate manifests recursively under the root (same symlink refusal and depth bound) and
  derive id/version from the manifest path or parsed manifest; or
- scope the method explicitly to the new layout, rename/annotate it as such, correct §3, and
  record how v10 extensions data is migrated.

### P2-2 — a FIFO in the package hangs `InstallExtension` indefinitely

`ListEntries` classifies every `FileSystemInfo` that is not a `DirectoryInfo` as a file
(`src/Playnite.Core/Addons/AddonInstaller.cs:272-285`). On Linux a FIFO is reported as a
`FileInfo` (measured: `is FileInfo=True`, `Attributes=Normal`, `LinkTarget=null`), so it falls
into the `FileInfo` branch and `File.Copy` blocks opening it for read. The
`unsupported-entry` branch is therefore dead for FIFOs and (in practice) for device/socket
entries as well. The symlink test shows the suite is meant to reject hostile package entries;
this class of entry is missed.

Probe: a package containing `zzz-fifo` made with `mkfifo` never completed:

```text
FAIL  FIFO entry does not hang the install  :: HUNG >5s (File.Copy on a FIFO)
```

A package directory can reach this API from a future extractor or from the user, and the hang is
unbounded on the calling thread. Fix: reject everything that is not a regular file or directory
using a real file-type check (a `Platform.Linux` `lstat` helper or an injected file-system
abstraction; `FileAttributes` cannot distinguish FIFOs, as measured). Add a bounded regression
test for a FIFO entry. A socket already fails fast today, but it is also misclassified rather
than diagnosed as `unsupported-entry`.

### P2-3 — the copy-failure rollback has no test, and the suite stays green when it is deleted

The rollback block (`src/Playnite.Core/Addons/AddonInstaller.cs:210-233`) works when exercised:
an unreadable source file sorted after other entries produces `install-failed` and removes the
partial `<root>/<id>` tree (probe E, full output in the evidence file). But no test in the PR
forces a copy failure, so removing the whole cleanup block leaves the suite unchanged:

```text
mutation: delete the rollback block
dotnet test tests/Playnite.Core.Tests -c Release
  total: 3113  failed: 0  succeeded: 3113  skipped: 0
```

The same probe against that mutated build reports the residue
(`Rollback_Test/1.0/aaa.dll`, `Plugin.dll`, `extension.yaml`, `zzz`), so the behavior is
observable — it is only the suite that does not pin it. `docs/port/04-VERIFICATION.md` §1 makes
"an executable test or golden that would fail if the behaviour changed" the definition of
verified, and the unit document lists rollback as delivered behavior.

Fix: add one install test that forces a late copy failure (for example a source file with
`UnixFileMode.None` ordered after another file) and asserts `install-failed` plus no `<id>`
directory. That test fails against the mutation above (verified).

### P2-4 — theme install drops the v10 `Mode` level, and the recursive-discovery justification is extension-only

For themes, v10 inserts the application mode before the id:
`rootDir = Path.Combine(rootDir, themeMan.Mode.ToString())`
(`legacy/source/Playnite/Plugins/ExtensionInstaller.cs:199-205`), and v10 theme discovery is
*not* recursive: it enumerates `<ThemesUserDataPath>/<modeDir>/<id>/theme.yaml` one level at a
time (`legacy/source/Playnite/Themes.cs:237-260`). The port's `InstallTheme` ignores
`ThemeManifest.Mode` and installs to `<root>/<id>/<version>` (asserted by
`Themes_use_the_same_id_and_version_layout`). The unit document's source map cites
`ExtensionInstaller.cs:205-215`, a range that excludes the mode branch at 199-202, and §3/§4
justify discoverability with v10's *extension* recursion
(`docs/port/p6-010-addon-model.md:16,62-63,84-86`), which does not apply to themes.

Impact: a theme installed through the delivered API is not where a v10-faithful theme manager
looks, and `GetInstalled()`'s two-level reader is the only consumer of the new layout. Fix:
either include the mode segment for themes (`<root>/<Mode>/<id>/<version>`) and teach
`GetInstalled`, or take `InstallTheme` out of this unit and record the deferral in the queue row
and document until a theme-manager unit fixes the layout. P2 rather than P1 because no theme
consumer exists yet; P2 rather than P3 because the PR ships and tests the theme install path and
the document asserts v10 compatibility for it.

## Checks performed

### 1. Diff integrity and scope

```sh
sha256sum data/p6-215/candidate.diff
git diff 8bb2617 4aaf227 | sha256sum
git diff --name-status 8bb2617 4aaf227
```

Observed: identical SHA-256, 13 files, +1472/−0. All additions except one line added to
`tests/Playnite.Core.Tests/Playnite.Core.Tests.csproj` (copying `tests/golden/manifests/*` to the
test output as `golden-manifests/`).

No path owned by another unit, the oracle, or a generated aggregate is touched: no
`src/Playnite.Core/Manifests/*`, `tests/Playnite.Core.Tests/ManifestTests.cs`,
`tests/fixtures/manifests-v10/*`, `tests/golden/*`, `tests/baseline.json`, `docs/formal/*`,
`docs/port/surface-parity.tsv`, `docs/port/queue.tsv`, `docs/port/ledger.tsv`, `legacy/*`, or
`oracle-lock/*`. The p6-010 unit shards and the unit document are in the permitted unit
namespace. The PR's unit-ownership and unit-evidence checks are green on the forge.

### 2. Build and suites at the reviewed head

```text
dotnet build src/Playnite.Core/Playnite.Core.csproj -c Release -warnaserror
  Build succeeded. 0 Warning(s), 0 Error(s)

dotnet test tests/Playnite.Core.Tests -c Release
  total: 3113  failed: 0  succeeded: 3113  skipped: 0

dotnet test tests/Playnite.Architecture.Tests -c Release
  total: 210  failed: 0  succeeded: 210  skipped: 0
```

These reproduce the ledger/CI counts exactly. The architecture suite covers the banned-API and
project-graph gates; a grep of the new code for `System.Windows`, `System.Drawing`,
`Microsoft.Win32`, `System.Management`, `ServiceController`, `BinaryFormatter`, `Thread.Abort`,
`DllImport`, `LibraryImport`, `Process.Start`, HTTP/socket clients and `Assembly.Load` found no
match.

### 3. p3-005 strictness is preserved

A probe re-ran strict p3-005 failures through `AddonManifestValidator.ValidateExtension`. Each is
still rejected with a single `invalid-manifest` diagnostic (or the specific member path), and
`Value` is null:

| Document | Validator path |
| --- | --- |
| duplicate key | `$` |
| multiple YAML documents | `$` |
| undefined `Type` enum | `$`/`$.Type` |
| explicit `Type: null` | `$.Type` |
| `Links: [null]` | `$.Links[0]` |
| missing `Module` | `$.Module` |
| empty document / wrong shape | `$` |

The direct parser (`ManifestSerialization.FromYaml<ExtensionManifest>`) rejects the same inputs,
so the p3-005 boundary is not loosened globally. The documented omitted-`Type` exception works:
the golden `TestPlugin.extension.yaml` and a synthetic no-`Type` document both validate as
`GenericPlugin`, while `Type: null` and `Type: NotAType` are still rejected. The retry is keyed
to the exception text and applies to any extension document that omits `Type` (see P3-1); that
matches v10's deserializer default, where the enum defaults to zero.

### 4. Hostile input is rejected before any directory is created

Every case below returned the named diagnostic, and the extensions root did not exist afterwards:

- ids `" id"`, `"id "`, `.`, `..`, `/abs`, `C:\evil`, `a/b`, `a\b`, a tab, `U+0001`, 256
  characters -> `unsafe-id`;
- modules `../../evil.dll`, `/usr/lib/evil.dll`, `lib/../../evil.dll`, `lib\evil.dll`,
  `C:\evil.dll` -> `unsafe-module` at `$.Module`;
- icons `../icon.png`, `/tmp/icon.png`, `a\b.png` -> `unsafe-icon` at `$.Icon`;
- hostile theme id and hostile catalogue `AddonId` -> `unsafe-id`;
- duplicate id -> `duplicate-addon`, with the first install intact;
- requested version 2.0 vs manifest 1.0 -> `version-mismatch`;
- package symlink -> `symlink`, with no `<root>/<id>` left behind.

### 5. Deterministic layout, containment, uninstall

- The existing tests assert the stable `<root>/<id>/<version>` path, identical relative file
  sets and identical bytes across two roots. A probe confirms `Version: 01.0` normalizes to
  `1.0`.
- Targets are built from validated relative entries and checked with a separator-bounded prefix
  (`Path.GetFullPath(candidate).StartsWith(root + separator, StringComparison.Ordinal)`), not a
  raw string prefix: `AddonInstallLayout.IsWithin` (`AddonInstallLayout.cs:154-162`). The check
  itself is untested (P3-2), but the inputs reaching it have already passed
  `IsSafeRelativePath`, so it is defense in depth rather than the only guard.
- Uninstall deletes the whole `<id>` tree and checks it is gone. Probe: files added after install
  (`unexpected.bin`, `unexpected-dir/deep/leftover.txt`) are removed and the root is empty. A
  symlinked `<id>` removes the link only and leaves the target directory and its file intact.
- The rollback code works (probe), but is not covered by the suite (P2-3).

### 6. No store, network, download, update or package-execution path

The new code contains no process launch, assembly load, HTTP/socket client, store, catalogue,
download, update or package-execution call. `InstallExtension`/`InstallTheme` only read an
already-unpacked directory with `System.IO` and copy files; they do not load the module. The
`InstallTheme` path is layout-affected by P2-4 but still executes nothing.

### 7. Goldens are real and read-only

The two files under `tests/golden/manifests/` are the v10 oracle capture committed earlier
(`523e090`), with the extension golden omitting `Type` exactly as v10's writer does. The PR only
adds a read-only copy to the test output; the tests read that copy and no test writes to
`tests/golden`. The p3-005 fixtures and their provenance file are unchanged.

### 8. Tests are load-bearing — except for rollback

Each mutation was reverted and the worktree verified byte-identical to the reviewed head:

| Mutation | Core suite |
| --- | --- |
| allow `..` in `IsSafeSegment` | `total 3113, failed 6` — guard is load-bearing |
| disable the package symlink check | `total 3113, failed 1` — guard is load-bearing |
| delete the copy-failure rollback | `total 3113, failed 0` — **not covered (P2-3)** |
| `IsWithin` always returns true | `total 3113, failed 0` — defense-in-depth untested (P3-2) |

### 9. Forge and CI evidence

- `gh-axi pr view 215`: head `4aaf227`, base `8bb2617`, `draft: no`,
  `merge_state: clean`, 8/8 checks green.
- The cited linux-ci run `44840650650` is real: conclusion `success`, head
  `b67d995653fe7f376e629a96615ab75832603544`, the implementation commit. `4aaf227` is a
  two-file docs delta on top, so citing the implementation-head run is truthful.
- The ledger fragment and queue row name the head/run and the test ids; the counts they claim
  (Core 3113, Architecture 210, Smoke 4, Plugin 15, zero failed/skipped) match the local runs
  and the cited artifact.

## Non-blocking observations (P3)

1. **The omitted-`Type` retry matches on exception text and is broader than the named oracle
   case.** It applies to any extension document without `Type`, not only
   `TestPlugin.extension.yaml` (probe confirms a synthetic id is accepted). That matches v10's
   default and is defensible, but `IsMissingDefaultType` does
   `exception.Message.Contains("missing Type")`; a future message rewrite silently removes the
   rule. Prefer a structured error code, and say "any omitted `Type`" in §2 instead of "that
   exact failure".
2. **The containment check and the `unsupported-entry` branch are untested.** `IsWithin` can be
   replaced with `return true` without a failure; the `default:` branch never sees a FIFO (P2-2)
   and is effectively dead. A direct test of `IsWithin` (internal) and a regular-file classifier
   test would pin both.
3. **Two hostile-input cases exist only in this review's probe, not in the suite:** control
   characters/outer whitespace in ids, and uninstall after adding unexpected files. The code
   rejects/removes them correctly today; adding the cases would keep it that way.
4. **`GetInstalled()` does not validate the manifest.** The XML summary says directories without
   "a valid id, version and manifest" are skipped, but the implementation only calls
   `File.Exists`; an invalid `extension.yaml` is returned as installed. Reword to "manifest
   file", or parse it and reuse the validator.
5. **"One portable directory name" overstates the id rule.** Trailing dots and Windows reserved
   device names (`CON`, `NUL`, `COM1`, …) still pass `IsSafeSegment`; on Windows the former is
   stripped by Win32 and the latter are unusable. Linux-first today, so not blocking.
6. **Manifest size is bounded only after the read.** `File.ReadAllText` reads the whole document
   before `ManifestSerialization` applies its byte limit. A bounded read would make the limit
   effective against a large local package.
7. **Duplicate-check → create is not atomic.** Two concurrent installs of the same id can both
   pass `Directory.Exists` and interleave into the same version directory. No locking or staging
   directory is used; v10 has the same shape, so this is a hardening note.
8. **A declared module need not exist.** `Module: does-not-exist.dll` installs successfully
   (probe G); v10 also defers the failure to load time, so this matches the oracle but means a
   "successful" install is not necessarily loadable.
9. **The queue row narrows the launch-brief oracle.** The brief named
   `legacy/source/Playnite/API/AddonsAPI.cs` and
   `legacy/source/Tests/Playnite.Tests/Manifests/AddonManifestTests.cs` as p6-010 oracle paths;
   the delivered row lists the manifest, installer, factory and paths files instead. That is
   consistent with the unit scope (store/API v1.1), but the narrowing should be stated rather
   than left implicit.

## Bottom line

The core security work is sound: the validator reuses p3-005 without weakening it, every hostile
id/module/icon path is rejected with a specific diagnostic and no disk write, install targets are
deterministic, uninstall removes the whole id tree, and no store/network/execution path exists.
The blocking problems are contract and coverage gaps around the boundaries this unit defines:
`GetInstalled()` is fixed-depth while the document and the v10 oracle are recursive, special file
entries can wedge the installer, the rollback behavior is not pinned by any test, and theme
installs omit the v10 mode level that the cited oracle applies. The cited CI run, counts and head
are accurate, and the reviewed diff is byte-identical to the forge PR.

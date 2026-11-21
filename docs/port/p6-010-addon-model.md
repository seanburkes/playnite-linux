# p6-010 — add-on manifest validation and install directory model

**Status:** delivered for review. **Unit:** `p6-010`. **Branch:** `fm/p6-010`.

This unit validates add-on documents and defines the deterministic install/uninstall directory
model for extensions. It adds new files under `src/Playnite.Core/Addons/`, the host entry-type
probe `src/Playnite.Core/IRegularFileProbe.cs` with its Linux implementation, and tests; it does
not edit the p3-005 parser, its fixtures or its tests. The add-on store, download, update and
online catalogue remain v1.1 (Phase 10): there is no network call, no downloader, no package
execution and no store UI here. Theme *installation* is deliberately not part of this unit; §3
records why and where it is deferred.

## 1. Source map

| Port file | Frozen reference | Adaptation |
| --- | --- | --- |
| `Addons/AddonManifestValidator.cs` | `legacy/source/Playnite/Manifests/AddonManifestBase.cs`, `ExtensionManifest.cs`; `legacy/source/Playnite/Plugins/ExtensionInstaller.cs:327-361` | Reuses `ManifestSerialization` read-only, then applies install-path safety to the id, module and icon. |
| `Addons/AddonPathSafety.cs` | `legacy/source/Playnite/Common/Paths.cs:175-180` (`GetSafePathName`), `legacy/source/Playnite/Common/Paths.cs:182-190` (`IsFullPath`) | v10 sanitises invalid characters; this model rejects the value instead, because a rewritten id can collide with another add-on and make the target ambiguous. |
| `Addons/AddonInstallLayout.cs` | `legacy/source/Playnite/Plugins/ExtensionInstaller.cs:199-215` (theme mode branch at 199-202, install directory at 205); `legacy/source/Playnite/Plugins/ExtensionFactory.cs:248-276`; `legacy/source/Playnite/Settings/PlaynitePaths.cs:96,100` | v10 installs an extension to `<Extensions>/<safe-id>` and discovers `extension.yaml` recursively. This model installs `<Extensions>/<id>/<version>` and `GetInstalled()` mirrors the recursive rule, so both the v10 `<id>/extension.yaml` layout and the versioned layout are discovered. Themes are not modelled here (§3). |
| `Addons/AddonInstaller.cs` | `legacy/source/Playnite/Plugins/ExtensionInstaller.cs:138-232,327-361` | Installs an already-unpacked extension directory. The manifest is parsed and checked before any target directory is created; v10's zip extraction and queue are not ported. |
| `IRegularFileProbe.cs`; `Playnite.Platform.Linux/LinuxRegularFileProbe.cs` | No v10 equivalent: v10 installs from a zip archive, whose entries are regular files. | An unpacked directory on Unix can contain a FIFO, socket or device, which the managed `FileInfo` type does not distinguish from a regular file. The Linux probe classifies one entry with `openat(O_PATH \| O_NOFOLLOW \| O_NONBLOCK)` plus `statx` and the reviewed `openat`/`statx`/`close` allow-list entries already used by `LinuxBoundedFileReader`; `AddonInstaller` rejects every non-regular entry. |

## 2. Manifest validation

`AddonManifestValidator` returns `AddonValidationResult<T>` with zero or more
`AddonDiagnostic(Code, Path, Message)` values. A valid result carries the parsed manifest; a
failed result carries at least one diagnostic. The codes are stable:

| Code | Meaning |
| --- | --- |
| `invalid-manifest` | The p3-005 document boundary rejected the text, or a required field is missing. The diagnostic path is the offending member where the parser reported one. |
| `unsafe-id` | The id is not one portable directory name: empty, `.`/`..`, rooted, a separator, a Windows-invalid character, a control character, or outer whitespace. |
| `unsafe-module` | The module path is absolute, uses a backslash separator, or contains `.`/`..` segments. |
| `unsafe-icon` | The optional icon path has the same escape problem. |

Malformed input is rejected before it can become a directory: duplicate keys, multiple
documents, wrong scalar/list/mapping shapes, undefined enum values, explicit nulls in value
members and null collection items all keep the p3-005 diagnostics. Unknown fields remain
ignored, matching the v10 `IgnoreUnmatchedProperties` rule already ruled on by p3-005.

`ValidateTheme` remains the theme document boundary: it applies the same parser and id rule to
`theme.yaml`. It validates a document only; this unit has no theme install path (§3).

### The v10 default `Type` exception

`tests/golden/manifests/TestPlugin.extension.yaml` is the oracle capture of v10's
`ExtensionManifest.FromFile` serialised by v10's YAML writer. That writer omits default-valued
members, so the golden has no `Type`; v10 binds the enum default, `GenericPlugin`. The p3-005
document boundary deliberately rejects a missing discriminator. The install validator therefore
retries only that exact failure through a derived wire type that leaves `Type` at the v10
default. It is an explicit compatibility rule, not a silent parser change: the p3-005 parser and
its tests are untouched, `Type: null` and an undefined `Type` still fail, and every other
document rule still runs. `TestGameLibrary.extension.yaml` carries `Type: GameLibrary` and
validates strictly.

## 3. Install and uninstall model

The layout is `AddonInstallLayout(extensionsRoot)`:

```text
<extensionsRoot>/<id>/<version>/extension.yaml
<extensionsRoot>/<id>/<version>/<module files>
```

- The root is the v10 `Extensions` user-data directory (`PlaynitePaths.ExtensionsUserDataPath`).
- The id is one validated directory name. It is never sanitised or rewritten.
- The version is the normalised `System.Version` string. The same id and version always resolve
  to the same directory, and two versions cannot silently overwrite each other.
- `GetInstalled()` discovers extensions **recursively**, matching
  `ExtensionFactory.GetInstalledManifests`: every `extension.yaml` at most
  `AddonInstallLayout.MaxDiscoveryDepth` (8) directory levels below the root is a candidate.
  The owning directory's first path segment under the root is the add-on id, and the version
  and validity come from the parsed manifest. A v10-installed add-on at
  `<extensionsRoot>/<id>/extension.yaml` is therefore visible beside this model's
  `<extensionsRoot>/<id>/<version>/extension.yaml`. Symlinked directories and manifests are
  never followed, a directory that cannot be enumerated is skipped, a manifest that does not
  validate is skipped, and the result is ordered by manifest path.
- `Uninstall` validates the id, deletes the whole `<id>` directory, and re-checks that it is
  gone, so every version, manifest and module file is removed and no add-on residue remains.
  This also removes a v10-installed `<id>/extension.yaml` add-on.

`AddonInstaller.InstallExtension` takes an unpacked package directory:

1. Check that `extension.yaml` is a regular file, then read it and run the validator above.
   The check comes first because reading a FIFO named `extension.yaml` would block forever.
2. Refuse a version that differs from the caller's requested version (`version-mismatch`).
3. Refuse an existing `<id>` directory (`duplicate-addon`); v1 has no update path.
4. Walk the package, reject symlinks (`symlink`), reject unsafe relative entries
   (`unsafe-path`), reject every entry that is not a regular file or directory
   (`unsupported-entry`), and verify every target stays inside the version directory.
   The regular-file check uses the injected `IRegularFileProbe`: on Unix `FileInfo` reports a
   FIFO, socket or device as a file, and copying a FIFO blocks forever.
5. Copy in a deterministic relative-path order; on a copy failure remove the partial `<id>`
   directory and report `install-failed`.

No package code is loaded or executed. The filesystem work is bounded to the extensions root
and uses `System.IO` plus the host entry-type probe; no shell or network API is used during
install.

The `AddonPathSafety` lexical rules are the primary install-path guard: every package entry is
rejected unless it is a relative path whose segments are all safe, so a `..` component or an
absolute path never reaches a target. `AddonInstallLayout.IsWithin` is a second, normalized
containment check that verifies a candidate path resolves below its expected directory. It is
defense-in-depth rather than the guard a hostile manifest meets first, and it is proven directly
by `AddonInstallLayoutTests.Containment_check_*`: a valid child, a sibling that only shares the
root's textual prefix, `..` traversal, an absolute path outside the root, and a root written
with a trailing separator. No stronger claim is made for it.

### Why themes are not installed here

v10 inserts a mode level for themes and does not discover them recursively. `InstallPackedTheme`
uses the `Themes` user-data root, and `InstallPackedFile` prepends `themeMan.Mode` before the id
(`legacy/source/Playnite/Plugins/ExtensionInstaller.cs:199-205`), producing
`<Themes>/<Mode>/<id>/theme.yaml`. `Themes.GetAvailableThemes(mode)` then looks exactly one
level down, in `<Themes>/<Mode>/<id>/theme.yaml`, and never recurses
(`legacy/source/Playnite/Themes.cs:237-258`).

`AddonInstallLayout` is the `Extensions` root and has no mode segment, so an `InstallTheme`
method on it would drop `Mode` and write a theme where v10 and this model would never discover
it. Theme installation and theme discovery are therefore **deferred** to a follow-up unit that
models the `Themes` root and the mode level; `AddonManifestValidator.ValidateTheme` is retained
because the theme document boundary is complete and tested, but no install path consumes it in
this unit. This is recorded in the unit's queue shard as well.

## 4. Extension-point contract

An installed extension is expected to load through the same contract proved by p6-009:

- v10 extension discovery is recursive: `ExtensionFactory.GetInstalledManifests` enumerates
  `extension.yaml` under `PlaynitePaths.ExtensionsUserDataPath` with
  `SearchOption.AllDirectories` and then deduplicates by id
  (`legacy/source/Playnite/Plugins/ExtensionFactory.cs:248-276`). `GetInstalled()` mirrors that
  recursive rule over the extensions root; deduplication by id is left to the loader, matching
  the point at which v10 deduplicates.
- v10 theme discovery is **not** recursive: it enumerates only
  `<Themes>/<Mode>/<id>/theme.yaml` (`legacy/source/Playnite/Themes.cs:237-258`). No theme
  discovery is claimed by this unit.
- p6-009 defines the headless preview discovery contract as
  `Playnite.SDK.Plugins.IPlayniteExtension` in
  `src/Playnite.SDK/Plugins/IPlayniteExtension.cs`, and `FakePluginHost` discovers concrete
  implementations by loading an assembly and constructing the interface implementation
  (`tests/Playnite.Plugin.Tests/FakePluginHost.cs:20-47`). See
  `docs/port/p6-009-sample-plugins.md` §2 and §4.
- `AddonInstallationTests.Installed_add_on_resolves_its_manifest_and_module_under_the_discovery_root`
  proves that after install the manifest is found by the recursive layout and the `Module`
  path resolves to a real file inside the version directory. This unit stops at that boundary;
  it does not load the module or claim plugin execution.

## 5. Test map

| Test file | Coverage |
| --- | --- |
| `tests/Playnite.Core.Tests/Addons/AddonManifestValidationTests.cs` | Real `tests/golden/manifests/*.yaml`, the frozen extension/theme fixtures and the synthetic catalogue fixture; malformed fields with member diagnostics; hostile ids, modules and icons; the v10 omitted-`Type` compatibility rule; valid nested paths. |
| `tests/Playnite.Core.Tests/Addons/AddonInstallationTests.cs` | Install into a temporary root; deterministic id/version path across roots; uninstall and residue; duplicate id; id traversal and absolute id; module traversal and absolute module; manifest/version mismatch; package symlink; a real FIFO entry and a real FIFO `extension.yaml`, both rejected without blocking; a late copy failure that must roll back the whole id directory; discovery of a v10 `<id>/extension.yaml` add-on and its uninstall; a symlinked discovery directory; the discovery depth bound; installed manifest/module discovery. |
| `tests/Playnite.Core.Tests/Addons/AddonInstallLayoutTests.cs` | Direct proof of the normalized containment predicate: a valid child, a `..` traversal that normalizes outside, an absolute outside path, a sibling that shares only the root's textual prefix, and a root with a trailing separator. |

Evidence: `tests/Playnite.Core.Tests/Playnite.Core.Tests.csproj` copies
`tests/golden/manifests/*` read-only into the test output as `golden-manifests/`; the existing
`tests/fixtures/manifests-v10/*` copy is unchanged. No oracle, p3-005 fixture or manifest test
is edited.

Two negative proofs were run locally, then the product code was restored:

- Deleting the rollback block makes
  `Install_removes_the_partial_add_on_when_a_later_copy_fails` fail because `Test_Plugin`
  remains under the root.
- Removing the `IRegularFileProbe` guard makes
  `Install_rejects_a_fifo_without_blocking_on_it` and
  `Install_rejects_a_fifo_manifest_without_blocking_on_it` fail at their 10-second bound,
  because `File.Copy`/`File.ReadAllText` blocks reading the FIFO.
- Replacing `AddonInstallLayout.IsWithin` with `return true` makes four
  `AddonInstallLayoutTests` cases fail: the sibling prefix collision, the absolute outside
  path, and both `..` traversal cases. Restoring the predicate makes them pass.

Local evidence:

```text
dotnet build src/Playnite.Platform.Linux/Playnite.Platform.Linux.csproj -c Release -warnaserror
  0 Warning(s), 0 Error(s)

dotnet test tests/Playnite.Core.Tests -c Release
  total 3125, failed 0, succeeded 3125, skipped 0

dotnet test tests/Playnite.Architecture.Tests -c Release
  total 210, failed 0, succeeded 210, skipped 0
```

Real linux-ci: [run 44840650650](https://github.com/seanburkes/playnite-linux/actions/runs/44840650650)
on head `b67d995653fe7f376e629a96615ab75832603544`, based on main `8bb2617`, completed
successfully. The downloaded `linux-test-results` artifact reports Core 3113/3113,
Architecture 210/210, Smoke 4/4 and Plugin 15/15 total/executed/passed, every suite 0 failed
and 0 skipped with exit 0. That run predates the follow-up commits that add recursive discovery,
FIFO rejection, the rollback test and the theme deferral; the follow-up head's run is recorded
in this unit's ledger fragment.

## 6. Out of scope

No add-on store, catalogue download, package download, version update/rollback path, licence
UI, zip extraction, plugin loading, UI extension-point freeze or SDK/Abstractions change. The
store and its online catalogue remain v1.1. **Theme installation and theme discovery are
deferred** (§3): v10 installs `<Themes>/<Mode>/<id>/theme.yaml` and discovers that exact shape
without recursion, which this extensions-root layout cannot represent; a follow-up unit owns the
`Themes` root and the mode level.

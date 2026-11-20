# p6-010 — add-on manifest validation and install directory model

**Status:** delivered for review. **Unit:** `p6-010`. **Branch:** `fm/p6-010`.

This unit validates add-on documents and defines the deterministic install/uninstall directory
model. It adds new files under `src/Playnite.Core/Addons/`; it does not edit the p3-005 parser,
its fixtures or its tests. The add-on store, download, update and online catalogue remain v1.1
(Phase 10): there is no network call, no downloader, no package execution and no store UI here.

## 1. Source map

| Port file | Frozen reference | Adaptation |
| --- | --- | --- |
| `Addons/AddonManifestValidator.cs` | `legacy/source/Playnite/Manifests/AddonManifestBase.cs`, `ExtensionManifest.cs`; `legacy/source/Playnite/Plugins/ExtensionInstaller.cs:327-361` | Reuses `ManifestSerialization` read-only, then applies install-path safety to the id, module and icon. |
| `Addons/AddonPathSafety.cs` | `legacy/source/Playnite/Common/Paths.cs:175-180` (`GetSafePathName`), `legacy/source/Playnite/Common/Paths.cs:182-190` (`IsFullPath`) | v10 sanitises invalid characters; this model rejects the value instead, because a rewritten id can collide with another add-on and make the target ambiguous. |
| `Addons/AddonInstallLayout.cs` | `legacy/source/Playnite/Plugins/ExtensionInstaller.cs:205-215`; `legacy/source/Playnite/Settings/PlaynitePaths.cs:14-24,100`; `legacy/source/Playnite/Plugins/ExtensionFactory.cs:248-276` | v10 installs to `<Extensions>/<safe-id>`. This model adds the normalised version segment: `<Extensions>/<id>/<version>`. v10 discovery is recursive, so the installed `extension.yaml` remains visible. |
| `Addons/AddonInstaller.cs` | `legacy/source/Playnite/Plugins/ExtensionInstaller.cs:138-232,327-361` | Installs an already-unpacked directory. The manifest is parsed and checked before any target directory is created; v10's zip extraction and queue are not ported. |

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
- `GetInstalled()` walks `<id>/<version>` and returns the `extension.yaml` or `theme.yaml` path
  under the recursive discovery rule used by `ExtensionFactory`.
- Uninstall validates the id, deletes the whole `<id>` directory, and re-checks that it is gone,
  so every version, manifest and module file is removed and no add-on residue remains.

`AddonInstaller.InstallExtension` / `InstallTheme` take an unpacked package directory:

1. Read the manifest and run the validator above.
2. Refuse a version that differs from the caller's requested version (`version-mismatch`).
3. Refuse an existing `<id>` directory (`duplicate-addon`); v1 has no update path.
4. Walk the package, reject symlinks (`symlink`), reject unsafe relative entries
   (`unsafe-path`), and verify every target stays inside the version directory.
5. Copy in a deterministic relative-path order; on a copy failure remove the partial id
   directory and report `install-failed`.

No package code is loaded or executed. The filesystem work is bounded to the extensions root
and uses `System.IO` only; no shell, process launch or network API is used.

## 4. Extension-point contract

An installed add-on is expected to load through the same contract proved by p6-009:

- v10 discovery finds `extension.yaml` recursively under the extensions root
  (`legacy/source/Playnite/Plugins/ExtensionFactory.cs:248-276`), then loads the manifest's
  `Module` assembly.
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
| `tests/Playnite.Core.Tests/Addons/AddonInstallationTests.cs` | Install into a temporary root; deterministic id/version path across roots; uninstall and residue; duplicate id; id traversal and absolute id; module traversal and absolute module; manifest/version mismatch; package symlink; theme layout; installed manifest/module discovery. |

Evidence: `tests/Playnite.Core.Tests/Playnite.Core.Tests.csproj` copies
`tests/golden/manifests/*` read-only into the test output as `golden-manifests/`; the existing
`tests/fixtures/manifests-v10/*` copy is unchanged. No oracle, p3-005 fixture or manifest test
is edited.

## 6. Out of scope

No add-on store, catalogue download, package download, update/rollback path, licence UI, zip
extraction, plugin loading, UI extension-point freeze or SDK/Abstractions change. The store and
its online catalogue remain v1.1.

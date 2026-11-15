# p3-005 — manifest document boundary

`Playnite.Core.Manifests.ManifestSerialization` reads/writes decoded text, with no filesystem,
network, install, script execution, application state or UI dependencies. It uses the existing
Newtonsoft.Json 13.0.4 and YamlDotNet 18.1.0 dependencies; no package was added.

## Source map and scope

All paths below are relative to `legacy/source/`.

| Port file under `src/Playnite.Core/Manifests/` | Frozen reference | Adaptation |
| --- | --- | --- |
| `ExtensionManifest.cs` | `Playnite/Manifests/ExtensionManifest.cs:14-64`, `ThemeManifest.cs:13-19` | Base/extension/theme wire properties and enum order; `ManifestLink` carries `PlayniteSDK/Models/Link.cs` Name/Url; local ApplicationMode follows `PlayniteSDK/ApplicationMode.cs` |
| `AddonManifestBase.cs` | `Playnite/Manifests/AddonManifestBase.cs:10-61` | All catalogue, screenshot, agreement, installer and package wire fields |
| `AddonManifests.cs` | `Playnite/Manifests/AddonManifests.cs:19-49,69` | Derived shapes and explicit-api compatible-package selection (same-major, <= API, descending package version, null for absent/empty packages) |
| `UpdateManifest.cs` | `Playnite/App/UpdateManifest.cs:9-32` | Update and release-note shapes, including all four API/application versions and VersionHistory |
| `EmulatorDefinition.cs` | `PlayniteSDK/Models/Emulator.cs:15-217` | Definition/profile/platform/region wire fields; separate Core DTOs, not an SDK API declaration |
| `ManifestSerialization.cs`, `ManifestVersionConverter.cs` | `Playnite/Common/Serialization.cs:200-219,297-305,326-346`; `Playnite/Plugins/ExtensionInstaller.cs:338-354`; `Playnite/Emulators/Emulation.cs:61,103,154` | Guarded boundary over p3-003 helpers; explicit errors rather than caller-side empty-object fallbacks |

The ruling for this unit scopes the vague game/library/settings wording to the actual v10
add-on family. There is no invented game/library/settings manifest class. Steam ACF remains
steam-003. Theme compatibility/built-in status, runtime paths, ObservableObject notifications,
extension discovery, installation, download/license UI and no-argument API selection are not
document parsing; they remain deferred to their consuming units. `DirectoryPath`,
`DirectoryName` and `DescriptionPath` are caller metadata, excluded from both formats.

## Tolerated versus rejected

All parse failures are `Playnite.Core.Serialization.SerializationException`, carrying a format
and, for manifest checks, a member path. Null API arguments are programmer errors and retain
`ArgumentNullException`. The boundary never returns an empty success object after failure.

| Field/rule | Behavior and reason |
| --- | --- |
| Unknown fields, including nested unknown fields and `$type` | Ignored, never resolved as CLR types. v10 `IgnoreUnmatchedProperties` and Newtonsoft default binding do not retain extension data. No v10 manifest in scope has an unknown-field preservation bag, so none is invented; writer drops these fields by a documented rule. Dictionary entries (`Links`) are data, not unknown fields, and survive round-trip. |
| Extension `Id`, `Version`, `Module` | Nonblank Id/Module and a real System.Version string required. Id/version are checked at the legacy install boundary; Module is needed to identify what loads. Missing Type rejected to avoid silently selecting GenericPlugin. |
| Theme `Id`, `Version`, `ThemeApiVersion` | Id/version required as above. ThemeApiVersion may be absent/null (v10 compatibility check treats it as unavailable), but when present must be a real version. Mode may be absent and means Desktop: the shipped desktop theme omits it. Explicit undefined Mode rejected. |
| Add-on `AddonId`, `Type` | Nonblank ID and explicit defined Type required; otherwise absent Type would silently select GameLibrary. All catalogue presentation/URL fields remain optional. |
| Installer `AddonId`, `Packages` | Nonblank ID required. Absent/null/empty Packages retained (v10 returns null compatible packages); every supplied package requires Version and nonblank PackageUrl. RequiredApiVersion may be null/absent: v10 excludes such packages from compatible results. Installer AddonType is optional because v10 sets it from the parent catalogue after reading. |
| Update `Version`, `Sha256`, `PackageUrls` | Version, nonblank hash and nonempty URL list required so incomplete updates do not become success. Hashes/URLs are opaque strings as in v10; parsing does not authorize downloads or validate a signature. SDK/theme versions and VersionHistory remain optional. |
| Emulator `Id`, `Profiles`; platform/region `Id` | Nonblank IDs required. Emulator requires a nonempty profile list. Other metadata/profile fields are optional as in the source; this parser neither infers startup commands nor executes them. |
| Optional strings/lists/objects | Null/absence retained; empty strings/lists distinct from null. Null collection items and null dictionary values rejected to prevent later dereferences. Names, authors, links, descriptions, tags, screenshots, agreement object, profile members, platform databases/emulators, region codes and release-note members are optional. |
| Optional value fields | Omission keeps the v10 zero/false value: ForceLTRLayout, profile ScriptStartup/ScriptGameImport, DefaultImport, IgdbId, agreement Updated and package ReleaseDate. Omission is not an invented timestamp or an imported region/platform. Explicit null for a value member is rejected. |
| JSON field types | Strings/Version/DateTime require string tokens; booleans require boolean tokens; unsigned IDs require integer tokens; enums accept only defined names or defined integer values. Known names are case-insensitive, matching Newtonsoft. |
| YAML field types | Native scalar text is accepted for string members (including numeric-looking names/version strings), as v10 YAML has no separate JSON-like scalar token types. Booleans must parse as true/false; IDs must be unsigned decimal UInt64; enums must be defined names/numbers. YAML member names remain case-sensitive. Mapping/sequence/scalar shape mismatches rejected. |
| Versions | System.Version, not semantic versioning: 2–4 nonnegative Int32 components. Pre-release/build suffixes are rejected, never stripped. Converter emits only Version nodes as quoted version strings and null Version nodes as plain null. |
| Syntax and resource limits | Shared p3-003 boundary rejects malformed/truncated documents, duplicate keys, trailing JSON content, multiple YAML documents, unresolved tags, overflow, fractional integers, explicit null value members, >128 nesting and >8 MiB UTF-8. Unknown fields still obey syntax/depth limits. |

Writers validate required fields and enum values. JSON retains legacy names and numeric enum
representation and omits null properties. YAML writes quoted scalars (not literal blocks),
preserving CR, LF, quotes, backslashes and Unicode; aliases are disabled. Re-emission is
semantic, not a promise of original comments, formatting or unknown-field bytes. No
cancellation/disposal lifecycle exists: these APIs are bounded synchronous pure string
transformations; internal readers are disposed.

## Evidence

`tests/fixtures/manifests-v10/` is additive evidence authorized through the unit's
`oracle-change` ruling. `scripts/port/generate_manifest_fixtures.py` copies the frozen extension,
both theme, Dolphin emulator and complete platform/region documents byte-for-byte. For
catalogue/installer/update/release-note shapes, it generates explicitly **synthetic** payloads
from the declared fields; installer versions follow the frozen AddonManifestTests. These are
not misrepresented as captured service responses. The old TestUpdateManifest.json is a
different, obsolete shape and is deliberately not treated as the current update contract.
`provenance.json` records each source, derivation and SHA-256. Re-running the generator checks
existing bytes and refuses to overwrite any difference.

`ManifestTests` covers semantic JSON fixture equality (all input members/values), stable
JSON/YAML round-trip, unknown-field ignoring, optional defaults, exact version type/component
count, suffix rejection, Unicode/escaping including CR, package selection matching v10,
documented failure classes, BOM/limits, 128 seeded text property trials, all proper JSON
truncations, and six fixture mutation targets (64 deterministic mutations each, 20-second
budget, reproducer input on failure). No snapshots were re-accepted or old evidence changed.

Local narrow checks: Core **475 passed / 0 failed / 0 skipped** (previous union floor 412,
recorded by linux-ci run **43769779701**); Architecture **144 passed / 0 failed / 0 skipped**;
Core Release build **0 warnings / 0 errors**. PR CI must corroborate the new head. Two
independent adversarial diff reviews are requested before merge; no self-review is claimed
as independent review. Shards remain in-progress until that acceptance gate, without editing
the protected parity table or generated aggregates.

# Legacy oracle harness

Windows-only console that calls **existing v10 public APIs** and writes raw output to stdout.
It is not a new export format and must not normalise bytes.

```
LegacyOracle manifest <extension.yaml>   # ExtensionManifest.FromFile -> v10 YAML serializer
LegacyOracle theme <theme.yaml>          # ThemeManifest -> v10 YAML serializer
LegacyOracle raw <file>                  # byte-for-byte copy
LegacyOracle settings-bytes <synthetic-settings-dir> <new-scratch-dir>
LegacyOracle library-export <synthetic-library-dir> <new-scratch-dir>
```

The Windows oracle workflow builds it and uploads the outputs; the Phase 1 comparator will
diff the new app's output against these bytes. Any change to this harness is an oracle change.

## Settings and library contract

Run on Windows, never on a real user's data. Both directory commands require a nonexistent
destination outside the source tree and reject linked paths and overlap in either direction.
Failures return 1; incorrect directory-command arguments return 2. Existing
manifest/theme/raw behaviour is unchanged. Outputs are files, not console text: do not
capture them through `Out-File` or any text pipeline.

`settings-bytes` requires `config.json`, `fullscreenConfig.json`, and `windowPositions.json`.
It validates with v10's public deserializer, copies those files to scratch, redirects
`PlaynitePaths.UpdateUserDataDir`, then calls `PlayniteSettings.LoadSettings()` and
`SaveSettings()`. The three saved files are the settings bytes; v10 also writes `Backup/`
inside scratch during loading. That backup is not a golden. No fields are removed, sorted,
rewritten, or normalized. The source is never passed to the write-capable loader.

Synthetic inputs must pin every reachable constructor/ambient dependency with public v10
input properties: `InstallInstanceId` (otherwise random), `BackgroundImageAnimation`
(otherwise GPU-dependent, `PlayniteSettings.cs` constructor), and fullscreen `Monitor`
(otherwise `Computer.GetGetPrimaryScreenIndex()`, a machine-dependent primary-screen index,
`FullscreenSettings.cs:98` and `Computer.cs:239-250`). A repeat on one machine cannot detect
that last dependency, so it is seeded as data rather than patched in output. Loading still
performs the authentic v10 defaulting/migrations.

`library-export` copies the complete synthetic library tree unchanged to scratch; it does
not open LiteDB, enumerate game objects, or define JSON/JSONL. This captain-approved raw-byte
representation is not a semantic export. `GameDatabase.OpenDatabase` would open collections
exclusively and ensure indexes (`legacy/source/Playnite/Database/Collections/ItemCollection.cs`,
`InitializeCollection`), so it must not be used to read the source fixture.

## Narrow Windows validation

```
./oracle-lock/fetch.ps1
dotnet restore tools/LegacyOracle/LegacyOracle.csproj
msbuild tools/LegacyOracle/LegacyOracle.csproj /p:Configuration=Release /p:Platform=x86 /m /verbosity:minimal
$exe = Get-ChildItem tools/LegacyOracle/bin -Recurse -Filter LegacyOracle.exe |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
./tools/LegacyOracle/Verify.ps1 -Mode GenerateSettings -Exe $exe.FullName `
  -Scratch "$env:RUNNER_TEMP/p1-002-generate"
./tools/LegacyOracle/Test-Validation.ps1 -Scratch "$env:RUNNER_TEMP/p1-002-selftest" -Exe $exe.FullName
./tools/LegacyOracle/Verify.ps1 -Mode Validate -Exe $exe.FullName `
  -Library tests/fixtures/v10/library `
  -LibraryManifest tests/fixtures/v10/library.sha256 `
  -Scratch "$env:RUNNER_TEMP/p1-002-validate" -Golden tests/golden
```

`tools/LegacyOracle/NuGet.config` clears inherited sources, resolves `Playnite.Deps` from
the checksum-verified `oracle-lock/packages` folder written by `oracle-lock/fetch.ps1`, and
uses nuget.org for the remaining packages. The tools project cannot see
`legacy/source/nuget.config`, so this keeps the oracle dependency pinned without a
solution-wide restore.

`Verify.ps1` preflights the scratch location before its first write: it canonicalizes the
path, rejects an existing scratch, rejects overlap with the repo, executable, library,
library manifest and protected goldens in either direction, and rejects linked ancestors.
`Test-Validation.ps1` exercises that preflight plus the whole-set comparator on disposable
synthetic unit bytes. It also drives the built executable's own overlap/link/existing
destination negatives and checks the source bytes/file set are unchanged. Those tests are
bounded tooling checks, not v10 fixture or Windows acceptance evidence.

`Verify.ps1` compares the three primary settings bytes after write/read/write and an
independent repeat, asserts the seeded public values survived serialization, and checks the
raw library copy and SHA-256 tree before/after. It removes only its copied input files before
saving, so a swallowed `SaveSettings` error cannot return stale input as success.

Mode behaviour:

- `GenerateSettings` emits only the three settings payloads plus v10 scratch; it neither
  needs nor claims a fixture or goldens.
- `Generate` additionally requires the approved library and its SHA-256 pin manifest, then
  emits all 21 payloads in scratch for human review. It does not write protected paths.
- `Validate` (default) requires the fixture, library pin and the protected goldens. It
  compares the exact 21 emitted payloads to `tests/golden/settings/**` and
  `tests/golden/library/**`, verifies `tests/golden/settings-library.sha256` covers exactly
  those 21 relative paths with matching hashes, requires `settings-library.md`, and fails on
  a one-byte emitted or golden change, a missing/extra/renamed path, a bad/duplicate/traversal
  manifest line, or a missing protected corpus. Absent fixture/goldens are reported as
  acceptance pending, never as success, and no mode updates or accepts goldens.

Manifest formats are deliberately dead simple and hash-pinned:

- `tests/golden/settings-library.sha256`: 21 lines,
  `<lowercase sha256><two spaces><path relative to tests/golden>`, e.g.
  `...  settings/config.json`.
- The p1-003 fixture pin, passed with `-LibraryManifest`: 18 lines in the same format with
  paths relative to the fixture library directory. Its exact repository path is pending
  p1-003's approved capture; the workflow's `tests/fixtures/v10/library.sha256` default is a
  provisional wiring point, not a claim about that capture.

Golden regeneration remains a separately reviewed human operation. Do not write anything
under `tests/golden/` until exact paths and derivation receive independent approval. Do not
claim fixture-dependent validation before p1-003's actual approved fixture is available.
Generation artifacts are review inputs, not accepted goldens.

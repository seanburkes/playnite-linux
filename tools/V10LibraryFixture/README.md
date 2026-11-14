# Synthetic v10 library fixture

Windows .NET Framework 4.6.2/x86 tooling. References frozen v10 source projects;
no copied models, substitute mapper, JSON library export, or hand-written BSON.
Only this project and its dependencies are built, never the legacy solution.

## Current evidence

**Candidate only. Windows build, read-back, and logical regeneration are pending.**
Do not copy outputs into protected paths before two independent diff-only reviews.

Logical determinism, not byte-identical fresh regeneration, is the approved
acceptance contract. LiteDB
4.1.4's `IndexService` initializes a private `Random` without a seed and uses
`FlipCoin` to choose each persisted index node's level. Fixed entities and insertion
order do not fix those index bytes. See the exact dependency source:
<https://github.com/litedb-org/LiteDB/blob/v4.1.4/LiteDB/Engine/Services/IndexService.cs>.
`SHA256SUMS` pins the one committed capture, and a one-byte mutation of that
capture must fail. Fresh generations must have the same complete logical documents
and actual typed v10 read-back, but their index bytes and SHA-256 hashes may differ.
No binary normalization, private-field reflection, or frozen-source modification
is used. The captain approved this distinction before fixture intake.

## Proposed contents and API derivation

The bundle has `SHA256SUMS` and `library/`. A proposed protected README sits beside
the checksum list, not inside the v10 library. All inputs are synthetic: no accounts,
credentials, downloaded assets, real user database, or Steam corpus intake.

`library/database.json` is serialized by `Playnite.Common.Serialization` from
`DatabaseSettings { Version = GameDatabase.NewFormatVersion }` (4). Seeding this
marker avoids new-library initialization's random default metadata. Public
`GameDatabase.OpenDatabase` creates all 17 `.db` files/indexes using v10's collection
paths; see `legacy/source/Playnite/Database/GameDatabase.cs:91-164,272-292,502-579`.
`files/` is empty and recreated in read-back copies.

| File | Typed LiteDB collection | Rows |
| --- | --- | ---: |
| games.db | Game | 4 |
| platforms.db | Platform | 1 |
| sources.db | GameSource | 1 |
| genres.db | Genre | 1 |
| tags.db | Tag | 1 |
| completionstatuses.db | CompletionStatus | 1 |
| emulators.db | Emulator | 0 |
| companies.db | Company | 0 |
| categories.db | Category | 0 |
| ageratings.db | AgeRating | 0 |
| series.db | Series | 0 |
| regions.db | Region | 0 |
| features.db | GameFeature | 0 |
| tools.db | AppSoftware | 0 |
| scanners.db | GameScannerConfig | 0 |
| filterpresets.db | FilterPreset | 0 |
| importexclusions.db | ImportExclusionItem | 0 |

The names above are model types, not invented wire names: actual collection names
come from `LiteDB.GetCollection<T>()` inside v10 `ItemCollection<T>`.
Mapper is exactly `GameDatabase.GetCollectionMapper`, including ReleaseDate mapping
and ignored computed properties (`GamesCollection.cs:26-60`). Public base
`ItemCollection<Game>.Add` preserves fixed dates instead of derived
`GamesCollection.Add`'s clock overwrite (`GamesCollection.cs:82-97`). Both inherited
Id and GameId constructor randomness are explicitly overwritten.

Games have IDs `10000000-0000-0000-0000-000000000001` through `...0004`:

- `Synthetic Steam Installed`: app 220, installed Windows path with spaces, favorite,
  playtime 3600, playcount 2, fixed activity.
- `Synthetic Steam Uninstalled`: app 570, uninstalled, null path/activity, zero counts.
- `Synthetic Steam Unicode — 雪`: app 400, installed Unicode Windows path, hidden,
  month-precision ReleaseDate July 2019.
- `Synthetic Custom Game`: custom GameId, empty PluginId, no launch action.

All Added/Modified dates are `2020-01-02T03:04:05Z`; first game's LastActivity is
`2020-02-03T04:05:06Z`. Metadata IDs `20000000-0000-0000-0000-000000000001`
through `...0005` identify platform `Synthetic Windows`, source `Steam`, genre
`Synthetic Genre`, tag `Synthetic Tag`, status `Synthetic Not Played`; game 1
references each. Unspecified properties retain actual frozen model defaults.

Steam PluginId is verified against the authoritative extension constructor, not
guessed from the extension YAML Id (which is a different identifier):
<https://github.com/JosefNemec/PlayniteExtensions/blob/04b46a233e20f44db4f04194fe4fd0a73ed51295/source/Libraries/SteamLibrary/SteamLibrary.cs#L31-L33>.
It is `cb91dfc9-b977-43bf-8e70-55f46e410fab`. Only this identifier is reused; no
extension code/data is ingested.

## Windows procedure

Use `.github/workflows/windows-fixture-validation.yml`. It runs
`oracle-lock/fetch.ps1` (which downloads and verifies the existing immutable
Playnite.Deps checksum into `oracle-lock/packages`), restores through the
tool-local `NuGet.config`, builds only `V10LibraryFixture.csproj`, runs the
output-path safety tests, then invokes:

```powershell
./tools/V10LibraryFixture/Validate.ps1 -Exe <built-V10LibraryFixture.exe> `
  -Output <nonexistent-external-scratch-directory> -Fixture tests/fixtures/v10
```

`NuGet.config` clears inherited sources, uses the real local
`oracle-lock/packages` folder, and adds the standard nuget.org index. It is
needed because the tools project cannot see `legacy/source/nuget.config` and
command-line URL sources were misinterpreted on Windows.

Before the first directory creation, `Validate.ps1` resolves both paths to their
canonical form, including reparse-point (junction/symlink) ancestors, and rejects
equality, either-direction nesting, and any output inside the repository. Scratch
must be genuinely external and disposable. `Validate.Tests.ps1` proves that nested,
equal, parent-of, linked and repository-internal outputs are rejected using
synthetic disposable trees, and asserts each rejected attempt leaves the source
tree's directory set, file set and bytes unchanged. Tests never write into
`tests/fixtures/` or another protected path.

Generation uses a nonexistent scratch output, never overwrites an existing output.
Validation requires two fresh generations with equal logical documents, rejection
of a one-byte mutation and an extra file, verification of the committed capture's hashes, a
read-only LiteDB game layout check and actual v10 GameDatabase read-back. v10
opening is write-capable: it receives only a disposable library copy, never the
committed source. Source hashes are checked before/after. Typed read-back asserts
all game fields listed above, references and metadata counts, empty collections,
ReleaseDate and omitted computed fields. In-memory document comparisons cover every
persisted field in every collection, including empty collections, with UTC dates
and stable row ordering. They are comparison-only, not a new persisted library
export format. Freshly generated documents must also equal the committed capture's
documents. No test exclusions or conditional skips.

The committed-fixture gate is strict only when `tests/fixtures/v10/SHA256SUMS`
exists. Before protected intake, the workflow still generates, runs every
negative check, uploads the candidate bytes, and passes with an explicit
`PENDING: committed-fixture comparison pending` notice. Once the manifest is
present, `Validate.ps1` verifies the pinned capture and compares fresh logical
documents against it; a bad or incomplete capture fails the run. A non-empty
fixture directory without `SHA256SUMS` also fails rather than being treated as
pending. The workflow writes the active gate to its step summary.

The workflow retains first-generation candidate bytes even if a later assertion
fails; an uploaded artifact is not by itself evidence of a passing validation.

The oracle consumer (p1-002) is authorized to copy the library directory as raw,
unchanged bytes into temporary storage. This generator does not define a new
library export format or authorize source writes by consumers.

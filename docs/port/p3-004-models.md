# p3-004 — pure v10 models

## Source and surface

Core owns these types under `Playnite.Core.Models`, not the UI SDK assembly. The
1:1 source oracle is `legacy/source/PlayniteSDK/Models/`:

- `Game.cs`: every stored property, its default values and notifications, `GameField`,
  identity inherited from DatabaseObject, `GetCopy`, `CopyDiffTo`, `GetDifferences`,
  and pure name/score/playtime/install-size/recent-activity projections.
- `GameMetadata.cs`: all metadata properties, `MetadataFile`, `MetadataProperty`,
  `MetadataIdProperty`, `MetadataNameProperty`, `MetadataSpecProperty`.
- `DatabaseObject.cs`, `GameAction.cs`, `GameRom.cs`, `Link.cs`, `ReleaseDate.cs`.
- `AgeRating.cs`, `Category.cs`, `Company.cs` (including Developer/Publisher),
  `CompletionStatus.cs`, `GameFeature.cs`, `GameSource.cs`, `Genre.cs`, `Platform.cs`,
  `Region.cs`, `Series.cs`, `Tag.cs`.
- `AgeRatingOrg.cs`, `InstallSizeGroup.cs`, `PastTimeSegment.cs`, `PlaytimeCategory.cs`,
  `ScoreGroup.cs`, plus action/tracking enums in `GameAction.cs`. Existing
  `InstallationStatus` and `IIdentifiable` are reused from SDK.Abstractions.

Support is adapted from SDK `Collections/ObservableObject.cs`, the three collection
helpers used by Game in `Extensions/ListExtensions.cs`, and copy-path exceptions in
`Exceptions/ReferenceException.cs` and `Exceptions/TypeMismatchException.cs`.

## Approved differences and deferred surfaces

Firstmate authorized this boundary in steering record p3-004/003; additive golden
creation and a real-CI-run baseline raise were authorized in p3-004/001 under
`oracle-change`. Existing oracle files remain untouched.

1. DatabaseObject and named-entity constructors require a caller-supplied `Guid`.
   Game additionally requires its provider `GameId`. No implicit IDs, clock reads,
   filesystem access or platform services are introduced. `Guid.Empty` remains valid,
   including mutable static `Empty` entities. Copies preserve existing IDs without
   allocating and discarding random ones.
2. Database navigation (`IGameDatabase`, static DatabaseReference and expanded entity
   getters) is **deferred**, not implemented with null-returning stubs. Emulator,
   filter, scanner, AppSoftware and import-exclusion models are separate deferred
   behavior surfaces. They are not claimed ported by this unit.
3. Ambient date-group properties/helper are **deferred**. `GetDifferences` therefore
   does not evaluate or return conditional AddedSegment/LastActivitySegment changes.
   The golden keeps v10's original results and tests explicitly pin these two approved
   differences. The unconditional ModifiedSegment notification/diff marker is retained
   exactly as v10 emits it; no ambient date calculation is needed for that marker.
   Notifications for deferred navigation/group names remain string-identical and in
   v10 order, but no corresponding getters pretend to exist.
4. Windows drive/root grouping and resource-localized install-drive helpers are
   **deferred**. Model `ReferenceException` and `TypeMismatchException` derive directly
   from Exception, not LocalizedException. Literal copy-path messages and exception
   type names are preserved; `LOC...` resource-key translation is not provided.
5. Copied model reference contracts retain v10's nullable-oblivious annotations using
   file-local nullable context. Null/default behavior is preserved rather than adding
   constructor validation or replacing null collections with empty collections. New
   support code and tests use nullable annotations. Core has no PublicAPI text files;
   SDK.Abstractions' tracked API is unchanged.
6. Stored DateTime kinds/precision are preserved; these are model values, not new UTC
   persistence boundaries. MetadataFile's single-path constructor still delegates to
   `Path.GetFileName`: on Linux it follows POSIX separators, not Windows backslashes.
   This is a platform difference, not evidence of Windows-path migration support.

## Equality and copying contract

- DatabaseObject equality is **ID-only across different derived types**; names and
  other fields do not participate. Empty IDs compare equal and hash to zero. IDs are
  mutable, so objects must not be mutated while used as hash keys. `==` is reference
  comparison, not ID equality. CompareTo uses case-insensitive current-culture names,
  puts null/empty names last, and throws for null/wrong-type inputs as v10 did.
- GameAction, Link and GameRom implement typed IEquatable value comparison with
  ordinal strings. They **do not override object.Equals or GetHashCode** and do not
  overload `==`. SequenceEqual therefore compares values, but object equality and
  hashing retain identity semantics. Adding value hashes would silently change v10.
- Metadata name/spec/ID classes override object equality and hashes, include the
  concrete property kind, and compare strings case-sensitively. MetadataIdProperty
  has a typed Equals method but does not declare IEquatable, matching v10. Whitespace
  names/specs and empty IDs throw ArgumentNullException with the original parameter.
  GameMetadata and MetadataFile themselves retain reference equality/hashing.
- ReleaseDate equality includes **precision**: 2024, 2024-1 and 2024-1-1 compare as the
  same Date but are not equal. XOR hashing has collisions (swapped month/day), not a
  guaranteed unique numeric identity. Empty equals default. Nullable absence differs
  from Empty. Operators, boxed equality and partial-date parsing are preserved.
- Game GUID lists compare count plus membership, ignoring order **and multiplicity
  distribution** (`[A,A,B]` equals `[A,B,B]`). Action/link/ROM lists compare ordered
  typed values. Null differs from empty. GetCopy clones lists and value-like children;
  CopyDiffTo aliases changed collections, and leaves equal collections alone. Name
  and ID are absent from Game.GetDifferences even though base CopyDiffTo copies Name.

## Evidence and reproduction

`tests/golden/models-v10/equality.json` contains 755 individually executed observations.
`provenance.json` pins its SHA-256, every frozen input file, shared probe, generator
and compile-only shim. The generator compiles **unmodified full frozen source files**
in a temporary standalone project and does not reference Core. Database/resource
shims throw if reached. This is v10-source execution on .NET 11/Linux, not a claim
of a Windows/.NET Framework run. String and object hashes are runtime-dependent:
goldens assert stability within a run, equality relationships and the declaring
hash implementation, not portable numeric string/identity hash constants.

Reproduce without changing a committed oracle:

```sh
python3 scripts/port/generate_model_goldens.py --output "$MODEL_CAPTURE"
cmp "$MODEL_CAPTURE/equality.json" tests/golden/models-v10/equality.json
dotnet test --project tests/Playnite.Core.Tests -c Release
dotnet test --project tests/Playnite.Architecture.Tests -c Release
```

Set `MODEL_CAPTURE` to a new scratch directory outside the checkout. The generator
refuses an existing output directory. Core executes the same probe
against the port, compares every observation, checks exact key coverage, and verifies
provenance hashes. Additional tests cover collection aliasing, observable event order,
derived copy errors/partial mutation, named-entity copies, ordinal Unicode differences,
parser tables, seeded valid-date round trips and bounded parser fuzzing. These pure
models own no resources or asynchronous work; cancellation/disposal are not applicable.

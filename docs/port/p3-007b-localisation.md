# p3-007b: localisation declarations and key-set diff

## Frozen oracle and boundary

- `legacy/source/Playnite/Localization/LocSource.xaml`: English declarations, **1,134**
  keyed nonempty strings (the plan estimate of 1,134 is exact).
- `legacy/build/buildLocConstants.ps1`: direct keyed children with nonempty `InnerXml`
  produce constants; remove only an initial, case-sensitive `LOC` from the member name.
- `legacy/source/Playnite/Localization/LocalizationKeys.cs`: independently checked frozen
  constant names and values, including the three unprefixed keys `LanguageName`,
  `NameFilterUseFuzzyMatching`, and `NameFilterUseFuzzyMatchingTooltip`.
- `legacy/source/Playnite/Localization.cs`, `SetLanguage` / `LoadAddonLocalization`: empty
  translations are omitted, leaving the original English resource visible. Whitespace-only
  translations are not empty. Later loaded addon strings can introduce their own keys.
- `legacy/source/PlayniteSDK/ResourceProvider.cs`, `GetString`: ordinal resource lookup;
  absent keys render as `<!key!>`.

The pure Core extraction handles the string-only resource declaration subset, not arbitrary
XAML objects, WPF resource merging, culture selection, or UI bindings. It never executes XAML.
English character data is XML-decoded with original whitespace, not rendered/normalized by a
XAML loader. Constants retain spelling (including `AditionalFilters`), case and key values;
no prefix validation or renaming is imposed. Core contains no UI framework dependency.
`Resolve` takes the English and optional translation sets explicitly, with no ambient language
state. The later p3-009 unit owns mapping and migration, not these declarations or goldens.

## Artifacts and regeneration

Run `python3 scripts/loc/generate_english.py --check` to verify, or omit `--check` to generate
these **unit-owned** artifacts from the immutable oracle:

- `src/Playnite.Core/Localization/English.json`: embedded English dictionary.
- `src/Playnite.Core/Localization/LOC.cs`: v10-compatible constant declarations.
- `tests/golden/loc/english-v10.json`: additive golden, with source/constants/v10-generator
  SHA-256 hashes, generation command, text contract, and extracted count.

The sibling migration unit must not edit any files under `src/Playnite.Core/Localization/`,
`tests/golden/loc/`, or `scripts/loc/`, nor `tests/Playnite.Core.Tests/LocalizationKeyTests.cs`
or this contract. It may consume these APIs and introduce its mapping in separate paths.
No pre-existing golden/fixture file was modified, renamed, or removed.

## Diff and verification

`before.Diff(after)` returns ordinally sorted added, removed and changed **key names**.
Changed means the same exact key has different text (ordinal comparison, including whitespace).
A rename is removal plus addition; no migration inference is performed. Set construction copies
inputs, rejects duplicate/empty keys and null values, and exposes read-only entries. Diff lists
are read-only snapshots. DTDs and external entities are prohibited; the caller owns the reader.
There is no asynchronous work, subscription or retained disposable resource.

`LocalizationKeyTests` checks every English value and constant against the golden, full frozen
source extraction, all three recorded input hashes and count, empty English diff, removed key
reporting, changed text and ordering, resolution, naming exceptions, malformed XML/duplicate/DTD
rejection, immutability, and seeded diff laws / bounded XML text round-trips.

Local Release runs: Core **1,382 passed**, Architecture **144 passed**, zero skips/failures.
Actual `linux-ci` run 43782366884 on head `a6abbcb` reports the same: Core 1382/1382,
Architecture 144/144, Smoke 4/4, zero failed or skipped, every MTP exit 0. That run's JUnit
evidence raises the counted Core floor from 1365 to 1382 in an additive `p3_007b_capture`; no
historical capture or floor is lowered. The p3-007b shard records this unit instead of editing
the generated aggregates or the protected shared parity table. Independent adversarial reviews
are firstmate-owned.

This unit was recovered from an interrupted lane's uncommitted tree. The recovered
implementation and goldens were kept; this contract was renamed to the unit id and the golden
test now verifies every recorded input hash, not only `LocSource.xaml`.

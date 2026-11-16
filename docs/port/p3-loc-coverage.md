# p3-loc-coverage-ds: frozen language coverage and placeholder validation

## Frozen inputs and boundary

The 37 locale files under `legacy/source/Playnite/Localization/*.xaml` are read-only oracle
inputs, together with `LocSource.xaml` as the English source. The merged p3-007b English
artifacts (`src/Playnite.Core/Localization/English.json`, `LOC.cs`,
`tests/golden/loc/english-v10.json`) are fixed inputs and are not regenerated or edited here.
`locstatus.json` is not a source: its percentages are advisory metadata, not a checked report.

`LocalizationCoverage` in `src/Playnite.Core/Localization/LocalizationCoverage.cs` extracts
keyed string declarations with an ordinary XML reader, never executes XAML, keeps empty values
and rejects duplicate keys. It compares each language with `LocalizationKeySet.English` and
returns an ordinal-sorted, immutable result. Culture selection and fallback rendering stay out
of scope; the unit reports the declarations the v10 loader consumes.

## Report and generator

`python3 scripts/loc/generate_coverage.py` writes `docs/port/p3-loc-coverage.json`; `--check`
verifies it without writing. `linux-ci` runs the `--check` form. The report records the exact
input paths, their SHA-256 hashes, the English key count, the language count, and the rules
below, so a stale or hand-edited report is detectable. The report is small because it stores
per-language counts plus the short missing, extra and placeholder-defect lists; the large
untranslated sets are represented by their count.

Per language the report records:

- `present`: English keys with a nonempty translation.
- `missing` / `missingKeys`: English keys not declared by the language file, ordinal sorted.
- `extra` / `extraKeys`: declared keys absent from English, ordinal sorted.
- `untranslated`: declared English keys whose value is empty. v10 omits empty translations and
  falls back to English, so these are missing translations rather than missing keys.
  Whitespace-only text is a translation.
- `placeholderMismatches`: strings whose format placeholders differ from English.

## Placeholder rule

Compare the ordinal multiset of unescaped .NET composite-format placeholders. A placeholder is
an unescaped `{`, a nonnegative index, an optional `,alignment` (with optional `-`), an optional
`:format`, and `}`. Doubled `{{` and `}}` are literal braces and are not placeholders. Dropping,
adding, duplicating, re-indexing or reformatting a placeholder is a defect, not a missing
string; an empty translation is untranslated and is not a placeholder defect because English is
used.

The frozen data has 7 real defects in 5 languages: `id_ID` drops `{2}` in
`LOCSkinApplyError`; `ja_JP` drops `{0}` in `LOCSignInExternalNotif`; `ko_KR` drops `{0}` in
`LOCExtensionInstallFail` and `LOCThemeInstallFail`; `nl_NL` adds `{0}` to the untemplated
`LOCMenuKofiSupport`; `vi_VN` drops `{1}` (as malformed `1}`) in `LOCExtensionInstallPrompt`
and `LOCThemeInstallPrompt`. There are no absent or extra keys in the frozen files.

## Verification

`LocalizationCoverageTests` recomputes every language from the repository `legacy/` paths and
requires the committed report to match exactly, including its input hashes; it also proves the
English source is complete and unchanged, exercises dropped and renamed placeholders, exact
missing-key reporting, extra keys, empty/whitespace fallback, escaped braces, and determinism.
No test is skipped and no historical golden is re-accepted or edited.

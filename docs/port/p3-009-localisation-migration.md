# p3-009: lossless localisation key migration

## Contract and provenance

The new English set deliberately retains v10 resource key names. The migration is an
explicit **1,134-entry identity map**, not a prefix-stripping rename. The frozen generator
`legacy/build/buildLocConstants.ps1`, lines 22–37, removes `LOC` only from C# member names;
the constant *values* remain the original resource keys. The two data groups are:

- `loc-prefixed`: 1,131 `LOC…` resource keys, spelling and case unchanged.
- `unprefixed`: `LanguageName`, `NameFilterUseFuzzyMatching`, and
  `NameFilterUseFuzzyMatchingTooltip`, unchanged as well.

Each group in `src/Playnite.Core/Migrations/localization-v10.json` names the frozen
`LocSource.xaml`, `LocalizationKeys.cs`, and generator as its provenance. Those files were
read before implementing the map. There is no inferred name correction (including the
v10 `AditionalFilters` spelling) or platform-specific key removal.

The mapping is independent data, not generated at runtime from the destination catalog.
Adding/removing a destination key cannot silently update the migration. The pure Core
`LocalizationKeyMigration` applies it ordinally, preserving text byte-for-character at the
.NET string boundary, including empty strings, whitespace and Unicode. No English fallback
is inserted during migration. `legacy/source/Playnite/Localization.cs`, `SetLanguage`,
lines 164–171, applies empty-translation fallback during loading, not key migration.

## Evidence and error model

`LocalizationMigrationTests.All_1134_frozen_English_keys_migrate_with_empty_sibling_diff_and_no_loss`
extracts the frozen source independently with the sibling API, migrates it, and calls
`LocalizationKeySet.English.Diff(result.Migrated)`. Added, removed and changed are all empty;
all 1,134 keys resolve to their original English text.

Unknown keys and mappings to missing destinations are reported in `Unmapped`, retaining
their original values. Explicit obsolete mappings use a null destination and retain their
values in `Obsolete`. `MissingKeys` reports destination keys not supplied by the input;
`OrphanMappings` reports dangling destinations even if their source is absent. English has
no obsolete keys. Colliding mappings, conflicting old/new inputs, duplicate sources and
non-idempotent rename chains fail rather than overwrite values. Successful migrated output
is a fixed point on a second application. Diagnostic partitions are not a replacement for
the input: callers must inspect `IsComplete` and retain/report diagnostics, not discard them.

There is no asynchronous I/O, ambient state, subscription, or owned disposable in the Core
migration. The embedded stream/document are disposed during static loading. Public inputs
are copied; exposed maps/lists are read-only. Null/empty key validation is explicit. No UI
or Windows types are introduced.

The deterministic small artifact `docs/port/p3-009-migration-report.json` records mapped,
unmapped and obsolete counts, orphan mappings, empty English diff, group provenance and
SHA-256 hashes of the three frozen inputs, fixed new English catalog and mapping data.
Regenerate **only this report** with:

```sh
python3 scripts/migrations/localization_report.py
python3 scripts/migrations/localization_report.py --check
```

The report checker also fails on incomplete migration. A normal Core test runs its `--check`
path in linux-ci, and another binds its hashes/counts to the real files and runtime result.
Tests cover missing mapping/input, unknown and obsolete keys, dangling destinations,
idempotence, collisions, ordinal comparison, exact text preservation and immutability.

## Strict read-only inputs

- All of `legacy/`, especially the three provenance inputs and `Localization.cs` above.
- All of `src/Playnite.Core/Localization/` (`English.json`, `LOC.cs`, `LocalizationKeySet.cs`).
- All of `tests/golden/loc/` and every other existing golden/fixture.
- All of `scripts/loc/`, `tests/Playnite.Core.Tests/LocalizationKeyTests.cs`, and
  `docs/port/p3-007b-localisation.md`.
- Protected shared parity/aggregate files. This unit records its contract in the p3-009 shard.

## Validation and counted floor

After normally merging current main `4048641` (recorded Core floor **1,429**), both local
Release suites passed: Core **1,445**, Architecture **144**, zero failures/skips. The narrow
Release build completed with zero warnings/errors. Shards-only history validation passed.

Actual `linux-ci` run [43791358506](https://github.com/seanburkes/playnite-linux/actions/runs/43791358506)
on integration head `926c4fc1e9e45a1a147b45b97fdd26126ec40b99` passed warning-as-error build,
every suite and the native window smoke. Downloaded JUnit testcase elements and suite totals
independently confirm Core **1,445/1,445**, Architecture **144/144**, Smoke **4/4**, zero
failed/skipped, every MTP exit 0. This supplies the additive `p3_009_capture` raising only the
Core floor **1,429 → 1,445**; no local estimate or stale pre-integration count was used. All
historical captures and other suite floors remain unchanged. The final evidence-only head
must pass its own fresh linux-ci run before delivery. Independent adversarial reviews are
firstmate-owned.

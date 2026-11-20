# PR #181 focused delta review — post-review fixes

- **Reviewed head:** `3155636c37c3c65f68f5f696b0c7d9acb3021d64`
- **Delta range:** `9288259d21da5536a0161bc5f1b519bc2946b3fc..3155636c37c3c65f68f5f696b0c7d9acb3021d64`
- **delta.diff SHA-256:** `84b2cf6ad4b8d560db5cae9cad0cc079f86d0b242887e2619cb9e911bc689883`
  (the file was compared byte-for-byte with `git diff 9288259..3155636`; identical — 6 files, 70 insertions, 17 deletions)
- **Verdict: no blocking P1/P2 findings.**

## Findings

None.

## Verification of the requested points

### 1. Every documented key type is validated; no range or type error escapes raw — pass

- `SettingsKey<T>.ValidateToken` (`src/Playnite.Core/Settings/SettingsKey.cs:144-190`) dispatches on the
  CLR type: bool (`:157`), string (`:167`), double (`:177`), int/long/enum (`:183`), and rejects every
  other type with `InvalidValue`.
- Integer ranges are checked against a `BigInteger` (`SettingsKey.cs:215-245`); a JSON integer beyond
  `long` is kept exact by `TryGetInteger` (`:65-97`) rather than narrowed or overflowed. Enums are
  bounded to `int` and checked with `Enum.IsDefined` (`:245-248`).
- `double` requires a numeric token, converts under a catch for `OverflowException`/`InvalidCastException`/
  `FormatException`, and then requires `double.IsFinite` (`:192-213`).
- Load validates all 15 documented keys from `settings` or `legacy` (`SettingsService.cs:415-423`); every
  typed read validates the token before `FromToken` (`SettingsService.cs:117-118`), so the unguarded
  `ToObject<T>` at `SettingsKey.cs:142` is never reached with an unchecked token.

### 2. Strict System.Text.Json failures map to the documented typed error — pass for the mapped paths

- The new `EnsureEnvelopeRoundTrips` (`SettingsService.cs:481-498`) catches
  `System.Text.Json.JsonException` (plus Newtonsoft, `InvalidDataException`, `FormatException`,
  `ArgumentException`, `InvalidOperationException`, and the newly added `KeyNotFoundException`) and
  rethrows `SettingsException(SettingsFailure.Corrupt)`.
- It is used on both service-owned `ExportV10` boundaries: the initial read (`SettingsService.cs:413`)
  and the rebuilt-envelope consistency check (`:477`). The latter was previously a bare
  `_ = SettingsMigration.ExportV10(bytes)` (base `SettingsService.cs:487`), so this closes the raw-parser
  escape the prior review called out.
- The catch set covers every exception `ExportV10` can produce: `KeyNotFoundException` from a missing
  property, `InvalidOperationException`/`FormatException` from `GetInt32`/`GetString`, `FormatException`
  from base64/UTF-8 decoding, `InvalidDataException` from the round-trip mismatch, and both JSON parsers.
- `Strict_json_parser_failure_is_reported_as_corrupt` (`SettingsServiceTests.cs:515-530`) proves the
  strict rejection surfaces as `Corrupt` with a `System.Text.Json.JsonException` inner exception.

### 3. Update tokens are validated before writing — pass

- `UpdateAsync` pre-validates every requested token at `SettingsService.cs:210-216`, before the gate,
  the store read, and the write. `BuildReplacement` re-creates the token at `:446`, and `ToToken` now
  runs `ValidateToken` before returning it (`SettingsKey.cs:134-139`).
- `Undefined_enum_update_is_rejected_before_any_write` and the three
  `Non_finite_double_update_is_rejected_before_any_write` cases (`SettingsServiceTests.cs:532-563`) assert
  `InvalidValue`, the key name in the message, and `storage.Writes == 0`.

### 4. Final-head citation is accurate — pass

- Row 4 of `docs/port/units/p6-004.ledger.d/181-settings-service.tsv` cites
  `ci_run=44688776676` on validated head `828b69b7446a29b5d1ad5eba2ed8d5506c1637ed`, based on main
  `3f06c970035f52f54d083c1adaec2b43c4d0531f`.
- Read back from the forge: run 44688776676 is `linux-ci`, `completed`/`success`, `head_sha`
  `828b69b7446a29b5d1ad5eba2ed8d5506c1637ed`; PR #181 base is `3f06c970035f52f54d083c1adaec2b43c4d0531f`.
- Downloaded artifact `linux-test-results`: `test-counts.json` and the xunit XMLs give Core 2770/2770,
  Architecture 199/199, Smoke 4/4, zero failures, zero skips, all three exit-code files `0`. The new
  test names are present in the Core trx.
- `3155636` is the PR head and adds exactly one line to the ledger fragment after `828b69b`
  (`git diff --stat 828b69b..3155636`: 1 file, 1 insertion), so "docs-only delta committed after the
  cited head" is accurate. The `unit-evidence` check on PR #181 also passes at this head.

### 5. No other behaviour change — pass

- The delta touches only the six files in `delta.diff`. The semantic changes are the write-path
  `ValidateToken` call and the consolidated `EnsureEnvelopeRoundTrips`; the remaining edits are the
  four new tests, the docs test map/evidence counts (2766 -> 2770, matching +4 executed cases), the queue
  note, and the appended ledger row.
- Adding `KeyNotFoundException` to the mapping only narrows a previously raw escape on corrupt envelopes;
  it cannot change the result of a valid load. No other code path changed.

## Non-blocking notes (outside the reviewed delta; not counted against this PR)

1. `MigrateFromV10Async`'s catch filter (`src/Playnite.Core/Settings/SettingsService.cs:296`) does not
   include `System.Text.Json.JsonException`, while `SettingsMigration.Convert` uses a strict
   `Utf8JsonReader` (`src/Playnite.Core/Settings/SettingsMigration.cs:182`) and `MigrateAsync` calls
   `ExportV10` (`SettingsMigration.cs:97`). A v10 source that Newtonsoft accepts but System.Text.Json
   rejects (e.g. a `//` comment, `NaN`, a single-quoted string) therefore escapes migration as a raw
   STJ `JsonException` instead of `SettingsException(Corrupt)`. This predates `9288259` and is unchanged
   by the delta, but it is the one remaining site of the "no parser exception escapes the service" claim.
2. `UpdateAsync` checks only that a name is in `V10SettingsProperties.Names`
   (`SettingsService.cs:440`) and validates the token with the caller-supplied key's type. A caller can
   pass a `SettingsKey<string>("Version", ...)`/`SettingsKey<long>("Version", ...)` for a modeled name and
   write a value the documented key rejects on the next load. Also pre-existing and out of contract
   (callers are expected to use `PlayniteSettingsKeys` for modeled names), so it is not a delta blocker.

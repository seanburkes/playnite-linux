# Review — PR #181, p6-004 settings service

**Outcome: HOLD — one blocking P2.** The envelope handling, preservation, atomic write and event
contract is careful and well tested, but integer range is never checked, so a settings value that
has the right JSON type and cannot be represented by its `SettingsKey<T>` passes load validation
and later escapes as `System.OverflowException` instead of the documented `SettingsFailure`.

- PR: https://github.com/seanburkes/playnite-linux/pull/181
- Reviewed snapshot head: `40335bc2293d287447606575b47849658301c48a` (this is the frozen
  `candidate.diff`; the forge head at review time is
  `2761bc4f71873e76f2f58eec1cf689c8f114fba6`, which adds only the evidence-only
  `docs/port/units/p6-004.ledger.d/181-settings-service.tsv` fragment).
- Candidate diff digest:
  `b0967494d8fed98236ddf37dc5068319f9a962f9c1b0b1439fa3c0f77a6f3902`
  (`data/p6-181/candidate.diff`). `git diff main...40335bc` has the same SHA-256: 6 files,
  1427 insertions, 1 deletion. `git diff 40335bc 2761bc4` is one evidence-only file, so the
  code verdict applies to both heads.
- Forge state at review time: open, `draft: no`, 8/8 checks passed, including `unit-evidence`
  and `unit-ownership`; `merge_state: clean`.
- Evidence runs, both `success` and matching their heads:
  - https://github.com/seanburkes/playnite-linux/actions/runs/44681355028 on
    `40335bc2293d287447606575b47849658301c48a`
  - https://github.com/seanburkes/playnite-linux/actions/runs/44681857665 on
    `2761bc4f71873e76f2f58eec1cf689c8f114fba6`

  The frozen candidate predates the evidence commit, so its queue row alone would fail
  `scripts/port/check_unit_evidence.py`; the live head carries the fragment and the CI check
  passes. That is a snapshot artifact and not a finding against the PR.

## Review identity

| Input | SHA-256 |
| --- | --- |
| `data/p6-181/candidate.diff` | `b0967494d8fed98236ddf37dc5068319f9a962f9c1b0b1439fa3c0f77a6f3902` |
| `src/Playnite.Core/Settings/SettingsKey.cs` (head) | `699e94e5e8a199595d68909fb19cadfc62d3c03685a9f929cde8c8625d7f9009` |
| `src/Playnite.Core/Settings/SettingsService.cs` (head) | `7d74fd93dd429b9d6ba223144bd867bbf9c030cfe5a34204ecb9ea15018b1651` |
| `src/Playnite.Core/Settings/SettingsStorage.cs` (head) | `dbed0874f9a6dd721020dfffb6f8874acf44022f35bea78dbddca92753cad1c5` |
| `tests/Playnite.Core.Tests/Settings/SettingsServiceTests.cs` (head) | `ad36268674b30a401acb691c612b8b3d1b705e68278ed2640c5b0d371fea10e3` |
| `docs/port/p6-004-settings-service.md` (head) | `503690c39fd03b88947516b065f8fff3b1bd7b7af7c28bec259b03b4515e1117` |

## Blocking finding

### P2 — integer values outside the CLR range are not validated; load succeeds and typed reads throw `OverflowException`

`SettingsKey<T>.ValidateToken` accepts every `JTokenType.Integer` for `int` and `long` without
attempting the conversion:

```csharp
// src/Playnite.Core/Settings/SettingsKey.cs:107-108
var matches = type == typeof(bool) ? token.Type == JTokenType.Boolean
    : type == typeof(int) ? token.Type == JTokenType.Integer
```

`SettingsSnapshot.Get` then relies on that validation before converting
(`src/Playnite.Core/Settings/SettingsService.cs:117-118`, conversion at
`SettingsKey.cs:92`):

```csharp
key.ValidateToken(token);
return (T)key.FromToken(token)!;
```

Concrete failure, both reachable from files the service accepts:

1. A migrated/loaded v10 graph containing `"Version": 999999999999` (`Version` is
   `SettingsKey<int>`). `MigrateFromV10Async` succeeds, `LoadAsync` succeeds — both `settings`
   and `settingsUtf8` carry the same token, so `SettingsMigration.ExportV10` sees agreement — and
   then `snapshot.Get(PlayniteSettingsKeys.Version)` throws
   `System.OverflowException: Value was either too large or too small for an Int32.`
   I verified that Newtonsoft `JValue.Value<int>()`/`ToObject<int>()` throw exactly this for a
   parsed 64-bit integer outside `Int32`.
2. A store whose envelope has `"schemaVersion": 4294967296` and otherwise valid fields.
   `LoadAsync` reaches `src/Playnite.Core/Settings/SettingsService.cs:386`
   (`version.Value.Value<int>() != 1`) and throws `OverflowException` out of the service rather
   than `SettingsFailure.Corrupt` or `UnsupportedSchema`.

The first path contradicts the stated typed contract and makes a snapshot that `LoadAsync`
declared valid explode on a typed read; the second contradicts
`docs/port/p6-004-settings-service.md:61-62` (“Failures carry a stable category”). Neither path
overwrites the store, so this is a correctness/robustness failure, not data loss.

Fix direction: make `ValidateToken` prove representability (`token.ToObject<T>()` inside the
validation, or explicit range checks for `int`/`long`), and read `schemaVersion` as `long` /
`TryGetInt32` before the equality check so an out-of-range value is `Corrupt` or
`UnsupportedSchema`.

## Non-blocking observations

- `docs/port/p6-004-settings-service.md:74` says a missing store is “created from the documented
  defaults plus the requested changes”, but `BuildReplacement` starts from an empty archived
  object (`src/Playnite.Core/Settings/SettingsService.cs:439-440`), so the new file contains
  only the requested keys. Typed reads still return the defaults, so behaviour is consistent;
  the wording and the `Update_creates_a_store_from_documented_defaults` name overstate what the
  file contains.
- `MigrateFromV10Async` checks for an existing store with a read (`SettingsService.cs:270`) and
  then replaces the destination with `WriteAtomicAsync` (`:232`). A concurrently created store
  between those two calls would be overwritten, so “never overwrites an existing store” holds
  only for the single-instance case, which the docs do not state.
- Reads follow a symlinked settings file while writes reject any symlink component
  (`SettingsStorage.cs:40-61` vs `:74-75,118`). The doc's “Symbolic-link paths are rejected”
  therefore describes the write side only.

## What was verified

- Static review of the frozen candidate; no build or test execution (per the review brief), so
  the cited counts were not re-run here.
- The 15 modelled defaults match v10:
  `CloseToTray`/`EnableTray`/`ShowSidebar` true, `StartInFullscreen`/`StartMinimized`/
  `MinimizeToTray`/`FirstTimeWizardComplete`/`AutoBackupEnabled` false, `Language` `"english"`,
  `Theme` `Playnite_builtin_DefaultDesktop`, `Version` 7, `AutoBackupFrequency.OnceAWeek`,
  `CheckForProgramUpdates.OnEveryStartup` (`legacy/source/Playnite/Settings/PlayniteSettings.cs`,
  `legacy/source/Playnite/Themes.cs:29`).
- The update path preserves the p4-004 invariant: it rebuilds `settings` as
  `archived − legacy`, so `settings ∪ legacy` stays equal to `settingsUtf8` and still passes
  `SettingsMigration.ExportV10`; unknown root properties and the envelope `report` are cloned
  through. Legacy-property moves and unknown-property refusal are exercised by the tests.
- Atomic write is a same-directory sibling temp, flush-to-disk, then rename; failures are
  wrapped as `WriteUnavailable`, the stage is deleted, and the destination is untouched. The
  real-directory and file-as-parent failure tests exercise the POSIX cases.
- `Changed` is raised once per successful write, after the gate is released; no-op/empty
  updates read once, write nothing and raise nothing (`SettingsService.cs:222-250`).
- Tests use a fake `ISettingsStorage` plus the frozen `tests/golden/settings/config.json`
  round-trip; no UI/Avalonia, network, skips or sleeps appear in the diff.

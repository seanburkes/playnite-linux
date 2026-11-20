# Review — PR #180, p6-003 Steam library sync service

**Outcome: HOLD — one blocking P2.** The sync/merge, identity, preservation and idempotency
contract is implemented as documented and the evidence checks out, but the production discovery
host can report an *incomplete* library observation as authoritative. In that state the service
marks installed games uninstalled, which is the exact failure the contract says cannot happen.

- PR: https://github.com/seanburkes/playnite-linux/pull/180
- Reviewed head: `877fcb0f33b767546d0f3381967b0ad1044bed7f` (`refs/pull/180/head`); the code
  head is `d3e12f12b74b8b43ba75b91ece59378213e2c9f7`; `877fcb0` adds only the ledger fragment.
- Candidate diff digest:
  `1579cd1ed6eaf95c4597b6116f464095d76ddc285937fa94ba23e1472e5e59ee`
  (`data/p6-180/candidate.diff`). `git diff main...877fcb0` has the same SHA-256, so the
  candidate is exactly the PR change: 8 files, 1261 insertions, 1 deletion.
- Forge state at review time: open, `draft: no`, 8/8 checks passed, `merge_state: clean`.
- Evidence run: `linux-ci` https://github.com/seanburkes/playnite-linux/actions/runs/44680509053
  is `success` with `head_sha` `d3e12f12b74b8b43ba75b91ece59378213e2c9f7`, matching the ledger
  fragment. `scripts/port/check_unit_evidence.py --base 423b501 --head 877fcb0 --pr 180`
  passes locally (`p6-003`, queue `in-progress`, fragment `180-steam-sync.tsv`).
- No protected oracle, fixture, baseline, schema or UI file is touched. No merge was performed.

## Review identity

| Input | SHA-256 |
| --- | --- |
| `data/p6-180/candidate.diff` | `1579cd1ed6eaf95c4597b6116f464095d76ddc285937fa94ba23e1472e5e59ee` |
| `src/Playnite.Steam/Sync/SteamDiscoverySource.cs` (head) | `ee1341d34ed97621fe6453a760c150e2adb0b47f9cee247b43b34465b1601f43` |
| `src/Playnite.Steam/Sync/SteamLibrarySyncService.cs` (head) | `b4b3838cc8f5fe05d6d4b2d89afe76b3798611f1539031521992493f1b6634db` |
| `src/Playnite.Steam/Sync/SteamSyncContracts.cs` (head) | `298a3c12c0137c0688de6272657c5c4c02ed900f8435512c7b5c14ff4f543099` |
| `tests/Playnite.Core.Tests/Steam/SteamLibrarySyncServiceTests.cs` (head) | `0e759e16b97376aeee62d6282bf9d8b750a08b1b49f305b347614f663ea8fa99` |
| `tests/Playnite.Core.Tests/Steam/SteamLibrarySyncIntegrationTests.cs` (head) | `cb45e85e2f776cd416bb1cbfbf136a015c43e4baa4162fd14dd2741fcf88284b` |
| `docs/port/p6-003-steam-sync.md` (head) | `63218f382b7c78906c6e0e2471cc67580db30d822b20eff2a98488bb79296ab6` |

## Blocking finding

### P2 — an incomplete library parse still reports `CanReconcile = true`, so the sync uninstalls games from a library it never enumerated

`src/Playnite.Steam/Sync/SteamDiscoverySource.cs:46` decides authority from the *presence* of a
library-file path:

```csharp
var canReconcile = result.LibraryFilePath is not null;
```

`SteamDiscoveryResult.LibraryFilePath` is non-null as soon as the primary
`steamapps/libraryfolders.vdf` was **seen**, even if the parser rejected it or dropped entries.
`SteamDiscovery.TryReadAndParse` sets `observed = true` before parsing
(`src/Playnite.Steam/SteamDiscovery.cs:108`), so a rejected file still sets `file = primary`
(`SteamDiscovery.cs:40-44`), while `parsed` is null and `result.Libraries` ends up holding only
the synthesized root entry with an empty `Apps` map (`SteamDiscovery.cs:72-77`). Separately, a
single library entry with a malformed `path` is dropped from `Libraries` with an
`invalid-library-path` issue (`src/Playnite.Steam/SteamLibraryParser.cs:60-66`) — it is not
represented as an unavailable library at all.

`SteamDiscoverySource` clears `canReconcile` only for an IO exception while enumerating an
*available* library's `steamapps` (`SteamDiscoverySource.cs:70-76`). It never looks at whole-file
rejection, dropped entries, or an unavailable library whose declared `apps` object is absent
(`library.Apps` is null, `SteamDiscoverySource.cs:51`).

Concrete failure: a `libraryfolders.vdf` entry

```text
"1"
{
    "path" ""
    "apps" { "10" "1" }
}
```

parses to `invalid-library-path`; `SteamDiscovery.Libraries` contains only the root entry;
`LibraryFilePath` is non-null; `CanReconcile` is `true`; the app id `10` appears in neither
`Apps` nor `Skipped`. The sync then takes the removal branch
(`src/Playnite.Steam/Sync/SteamLibrarySyncService.cs:147-168`), finds the stored Steam record for
app 10 absent from `observed`, and writes `IsInstalled = false` plus a new `Modified`, with no
`SteamSyncSkip` entry. The same happens when the whole file is corrupt and no usable
`config/libraryfolders.vdf` fallback parses, or when a detached library's `apps` object is
absent: every game that lived in that library is flipped to uninstalled.

This contradicts the unit contract directly: `docs/port/p6-003-steam-sync.md:70-71` says the
production host sets `CanReconcile` false when “a declared library could not be enumerated”, and
lines 72-73 promise that “a detached drive cannot uninstall those games”. Records and user data
survive, and a later good run repairs the state, but the persisted installed state is silently
wrong in the meantime and the failure is not reported as a skip.

Fix direction: have `SteamDiscovery` expose whether the selected library file parsed and whether
any declared entry was dropped (not merely whether a path was seen), and set
`canReconcile = false` when that completeness signal is false or when an unavailable library's
app set is unknown. A whole-file parse rejection, an `invalid-library-path` entry and a
null `Apps` map on an unavailable library must all disable removal.

## Non-blocking observations

- `GameIdFor` labels a SHA-256-derived identity as “RFC 9562 … version 5”
  (`src/Playnite.Steam/Sync/SteamLibrarySyncService.cs:38-44`; `docs/port/p6-003-steam-sync.md:31`).
  RFC 9562 UUIDv5 is SHA-1 based; the nibble is set to 5 while the hash is SHA-256. The identity is
  deterministic and stable for this consumer, but the standard claim is inaccurate. Use a
  custom/v8 label or SHA-1 if “v5” must be true.
- `SteamDiscoverySource.cs:43-45` says a missing library file is “still a complete observation of
  the root, so reconciliation is safe”, but line 46 sets `canReconcile` false in exactly that
  case. The code is conservative, so this is safe, but comment and code disagree.
- `docs/port/p6-003-steam-sync.md:23-24` says the database is not opened when discovery “has
  nothing to write or reconcile”. `SteamLibrarySyncService.cs:62` still opens and reads the
  database for a skipped-only, non-reconcilable discovery (correctly writing nothing). Minor
  contract wording.
- In the removal branch every duplicate record for an app is mutated
  (`SteamLibrarySyncService.cs:158-168`), while `docs/port/p6-003-steam-sync.md:38-39` says extra
  duplicate records are never mutated. This only triggers on already-duplicated DB records.

## What was verified

- Static review of the frozen candidate at `877fcb0`; I did not build or run the project (per the
  review brief), so test counts are the cited run's, not re-executed here.
- Removal, duplicate reporting, `OverrideInstallState`, missing-size, identity-conflict, skip
  reporting, cancellation and idempotency paths were traced against the tests and the
  `LibraryDatabase`/`GameRepository` contracts (`GetAllAsync` orders by id; `UpdateAsync` writes the
  complete graph; `WriteAsync` commits one transaction).
- Identity matching relies on the frozen plugin GUID plus a decimal `GameId`; the importer
  preserves both (`src/Playnite.Core/Import/LibraryMigration.cs:80`, `GameRow`), so imported
  records are matched rather than duplicated.
- The fixture corpus is read-only in this PR and the integration test copies it to a temporary
  home, verifies `SHA256SUMS`, and opens no network or UI assembly.
- The candidate contains no `legacy/`, protected-oracle, baseline, aggregate, schema or UI edit.

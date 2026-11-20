# Delta review — p6-003 Steam sync coverage tests (delta 2)

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/180
- Base: `8eb13df`
- Head: `91f139a`
- Delta: `/home/sean/src/firstmate/data/p6-180/delta2.diff`
- SHA-256: `3753a49e7b2b6ab9bc30f6d76f9c2bc4e699fe4866b8e50c839d3dc16c9d03f3`
- Size: 5 files, +156/−14
- Verdict: **no blocking P1/P2 findings**

Scope is limited to this delta: the new coverage tests, the reason-label change, and the
documentation/queue rows that describe them. No other reviewers' output was read.

## Blocking findings

None.

## Checks performed

### 1. Empty-observation gate — `tests/Playnite.Core.Tests/Steam/SteamLibrarySyncIntegrationTests.cs:385`

The test seeds one manifest, then rewrites the library file with a present-but-empty `apps`
object and replaces the manifest with `appmanifest_not-an-app-id.acf`
(`SteamLibrarySyncIntegrationTests.cs:409-411`). In discovery the available-library guard at
`src/Playnite.Steam/Sync/SteamDiscoverySource.cs:118` does not fire because `files.Length == 1`;
the invalid name only adds a `manifest-invalid-name` issue at `SteamDiscoverySource.cs:177` (it is
not a skip and does not clear `complete`). `CanReconcile` at `SteamDiscoverySource.cs:216` is
therefore false solely because `apps.Count + skipped.Count == 0`. The sync early return at
`src/Playnite.Steam/Sync/SteamLibrarySyncService.cs:62` writes nothing.

This test does pin the non-empty half: removing `apps.Count + skipped.Count > 0` makes
`CanReconcile` true, the removal loop at `SteamLibrarySyncService.cs:147` marks app 105600 not
installed, and the `Removed == 0` / `CanReconcile == false` assertions at
`SteamLibrarySyncIntegrationTests.cs:414` and `:421` fail.

### 2. Detached library with an emptied app set — `SteamLibrarySyncIntegrationTests.cs:221`

The test genuinely detaches the external library with `Directory.Delete(external, recursive: true)`
(`:252`) and declares an empty `apps` block (`:253-254`). It asserts the unavailable path
(`IsAvailable == false`, `MissingPath`, `Declared.Apps.Count == 0`) at `:256-260`, the
`library-apps-emptied` reason and absence of an app/skip for 105600 at `:262-266`, and
`Removed == 0`, `Changes == 0`, `Reconciled == false`, both records installed at `:270-276`.

The root library still holds appmanifest 427520, so the root does not trip the available-library
guard; the only authority withdrawal is the unavailable empty-apps branch at
`SteamDiscoverySource.cs:81-85`. Removing `complete = false` there would leave `complete` true and
one observed app, making `CanReconcile` true and removing 105600 (`Removed == 1`), so the test
does fail on that regression.

### 3. Reachable library with an undeclared app set — `SteamLibrarySyncIntegrationTests.cs:331`

The external library stays available, its manifest is deleted and its `apps` object is omitted
(`:357-358`). The available-library guard at `SteamDiscoverySource.cs:118-123` selects
`library-apps-unknown` because `library.Apps is null`, and the test asserts that reason while
asserting `library-apps-emptied` is absent (`:362-363`). Root still has manifest 427520, so the
external entry is the only one feeding the guard. Reverting the label ternary to always
`library-apps-emptied` fails `:362`.

### 4. Reason labels and product scope

The `null`/empty distinction is applied consistently: unavailable libraries at
`SteamDiscoverySource.cs:76-84`, available libraries at `SteamDiscoverySource.cs:120-122`. Both
branches still set `complete = false`, so the only product change is the issue reason;
`docs/port/p6-003-steam-sync.md:83-84` and the unit queue note match. No other product behavior
changes in the delta.

## Non-blocking note (P3)

`src/Playnite.Steam/SteamDiscovery.cs:72-76` synthesizes a root library with an empty `apps` map
when the library file never declared the root. Under the new available-library rule
(`SteamDiscoverySource.cs:120`), that synthetic root with zero manifests reports
`library-apps-emptied` even though the file never declared an app set; the wording at
`docs/port/p6-003-steam-sync.md:83` would call it `library-apps-unknown`. This is a pre-existing
model limitation, not a regression in this delta, and it does not affect removal authority, so it
is recorded as P3 only.

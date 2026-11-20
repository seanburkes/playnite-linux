# p6-005 review — metadata service with provider seam and bounded cache

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/189
- Reviewed head: `dd670eb4c9f8ba117f5ddea9545cda51eb32ce01` (`docs(p6-005): cite the fake-provider contract oracle`; implementation commit `29b7b671eaa4fe9c4dac09054fb1e218c4eedd50`)
- Base: `main` at `19a4637f78852fa87b5a009e7b61f047585093a9`
- Candidate diff: `/home/sean/src/firstmate/data/p6-189/candidate.diff`
- SHA-256: `18165c3327101f1d8a39fd18a06358f5328bc0e6cbb8aadf1e2743fd2c53bf6a`
- Size: 9 files, +1632/−1 (three `src/Playnite.Core/Metadata` files, three test files, one contract doc, the p6-005 shard and its ledger fragment); matches the forge's changed-file and line counts for the head.
- Method: static review of the candidate diff only (extracted to a scratch tree for stable line numbers). No build or test execution, no other reviewer's output, no other repository slug.

## Verdict

**No blocking P1/P2 findings.** The provider seam, cache key/bounds/eviction, atomic publish, stale/offline
semantics, per-game failure reporting and cancellation all match the stated contract, and the head has
forge-verifiable evidence. Merge can proceed.

## Blocking findings

None.

## Checks performed

### 1. Provider seam isolation and no live network — sound

`IMetadataProvider` (`src/Playnite.Core/Metadata/MetadataContracts.cs:218`) is the only lookup path;
`MetadataService.FetchAsync` calls nothing else (`MetadataService.cs:141-163`). The service never owns a
transport, and `MetadataProviderResult`'s only public factories are `Success`/`Failure`, so a result can
never carry both a document and a reason (`MetadataContracts.cs:178-216`). A null provider result is mapped
to `provider-invalid-response` (`MetadataService.cs:149`). I independently grepped every added line for
`System.Net`, `HttpClient`, `WebRequest`, `System.Windows`, `Avalonia`, `DllImport`, `Process.Start` and
found none in product code; the test-side source scan at `tests/Playnite.Core.Tests/Metadata/MetadataServiceTests.cs:319`
asserts the same over the whole metadata folder, so a later network addition inside that folder fails the
suite. Tests use `FakeMetadataProvider` and a temp directory only.

### 2. Cache key, bounds and eviction — correct, including replacement

- Key is SHA-256 over `provider + "\n" + appId` and only the digest reaches the filesystem
  (`MetadataCache.cs:306-307`), so no key can inject a path component.
- On read, the stored `Provider`/`AppId`/schema must match the requested key or the entry is deleted as
  corrupt (`MetadataCache.cs:344-349`); a hash collision or a copied file is therefore a miss, never
  another game's metadata.
- Entry-count and byte accounting subtract the entry being replaced before the limit test
  (`MetadataCache.cs:213-220`), then evict oldest-mtime victims until both bounds fit
  (`:222-240`). An entry bigger than `MaxBytes` is rejected *before* the directory is touched
  (`:187-190`), so a doomed write cannot evict live entries.
- The LRU test is deterministic: store `a`, store `b`, read `a`, store `c` with a monotonic manual clock
  (`MetadataCacheTests.cs:46-65`); the read refreshes `a`'s write time via `Touch` (`MetadataCache.cs:390-403`),
  so `b` is unambiguously the victim.
- Unsafe roots are rejected (`MetadataCache.cs:73-91`; `MetadataCacheTests.cs:147-162`).

### 3. Atomic publish and corrupt-entry repair — sound

The entry is serialised to bytes, written to a sibling random-name `.tmp`, flushed with `flushToDisk: true`,
given the fetch timestamp, then renamed over the target (`MetadataCache.cs:187-258`); the temp file is
removed in a `finally` and stale temps are swept at the start of every store (`:199-201`). A reader either
sees the previous complete entry or the new complete entry. Malformed JSON, a zero-length/oversized file,
a bad schema, a bad enum name or a malformed link/artwork is deleted and reported `cache-corrupt`
(`MetadataCache.cs:122-155`, `:337-388`); the online path then refetches and rewrites, covered by
`MetadataCacheTests.cs:98` and `MetadataServiceTests.cs:204`.

### 4. Failure, timeout and honest per-game results — sound

Caller cancellation is rethrown before any work and again if `provider.FetchAsync` observes the caller's
token (`MetadataService.cs:35-41`, `:151-153`); a provider `OperationCanceledException` the caller did not
request becomes `provider-timeout`, any other exception `provider-exception` (`:153-163`), and no document
is invented. The stale-on-failure path returns the validated previous document with `Stale` plus the
provider reason (`:99-109`); a miss is `Failed` with the provider reason (`:113-118`). Offline never calls
the provider and maps `cache-corrupt`/`cache-read-failed`/none to
`offline-cache-corrupt`/`offline-cache-unreadable`/`offline-cache-miss` (`:43-67`, `:165-174`).
Cache problems that do not invalidate a result travel only as `CacheWarning` (`:96`, `:118`).

### 5. Batch ordering and cancellation — sound

`GetManyAsync` awaits each query in input order and appends one result per game (`MetadataService.cs:125-139`);
a per-game failure/exception is captured inside `GetAsync`, so later games still run
(`MetadataServiceTests.cs:231`), while a caller cancellation rethrows out of the loop
(`tests/.../MetadataServiceTests.cs:264` pins both the pre-cancelled and mid-batch cases and asserts later
games are never requested).

### 6. Determinism and no UI dependency — sound

Fixed schema, camelCase JSON, no dictionaries, victim order by `(LastWriteTimeUtc, Name)` and a hash key:
output order and eviction order are stable for a given clock and key set. No UI, dispatcher, Avalonia or
`System.Windows` reference exists in the product files (also asserted by the source scan).

### 7. Evidence and ownership — acceptable

- Head `dd670eb` has all eight checks green: `inventory-and-parity`, `formal`, `unit-ownership`,
  `guard-inventory`, `unit-evidence`, `linux-build-and-test`, Flatpak and AppImage smoke; the
  `linux-build-and-test` run on this head is
  [44698082590](https://github.com/seanburkes/playnite-linux/actions/runs/44698082590) and `unit-evidence`
  is [44698082739](https://github.com/seanburkes/playnite-linux/actions/runs/44698082739).
- The diff adds exactly 18 `[Fact]` cases (10 service + 8 cache) with no `Skip`/`Ignore`, matching the PR
  body and the ledger fragment. The claimed Core total (2,809) is exactly the current main baseline
  (2,791, which p6-006's 2,842 total also implies after its 51 new hosting cases) plus these 18, so the
  count is self-consistent.
- All nine files are additive except the seeded `p6-005` shard row, which is redefined to this unit and
  cites the contract, the Steam-spec section and the two test classes; a ledger fragment records the
  validated implementation head and the CI run. No protected oracle, baseline, aggregate, PublicAPI or UI
  file is touched.

## Non-blocking notes (P3, not findings)

- `MetadataCache.Dispose()` (`MetadataCache.cs:283-292`) sets `disposed` and disposes the semaphore without
  waiting for an in-flight operation; a caller that disposes while a read/store is running can get
  `ObjectDisposedException` from the `finally` release. The class documents a single owner and disposal at
  shutdown, and no test disposes concurrently, so this is not raised as a blocker but is worth a guard when
  cache lifetime leaves process scope.
- The cache serialises per instance; two `MetadataCache` instances over one directory can evict each
  other's temp files. The contract doc states the directory is private to one owner, so this is accepted.

## Scope

Read only PR 189's candidate diff and forge metadata for its head. PR 190 was reviewed after this one, from
its own candidate file; no review of either PR by another reviewer was read. Historical review files in
this repository were inspected only to match the review-report layout.

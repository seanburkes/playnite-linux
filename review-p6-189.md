# p6-005 review — Metadata service

- Repository: https://github.com/seanburkes/playnite-linux
- Pull request: https://github.com/seanburkes/playnite-linux/pull/189
- Reviewed head: `dd670eb4c9f8ba117f5ddea9545cda51eb32ce01`
- Base: `19a4637f78852fa87b5a009e7b61f047585093a9`
- Candidate diff: `/home/sean/src/firstmate/data/p6-189/candidate.diff`, SHA-256 `18165c3327101f1d8a39fd18a06358f5328bc0e6cbb8aadf1e2743fd2c53bf6a`
- Size: 9 files, +1632/−1
- Method: static review of the candidate diff and the APIs it names. No build and no test execution, per the brief; the cited CI run is not re-run.
- Verdict: **no blocking P1/P2 findings**

## Blocking findings

None.

## Checks performed

### 1. Provider seam and cache identity

`MetadataService` captures `IMetadataProvider.Identity` once and uses it for every cache key
(`src/Playnite.Core/Metadata/MetadataService.cs:27`, `:43-44`, `:86-87`); the service has no live
transport and calls only the injected provider (`MetadataService.cs:141-163`). `MetadataQuery`
requires a non-empty app id (`src/Playnite.Core/Metadata/MetadataContracts.cs:34-44`) and
`MetadataQuery.FromGame` copies `GameId`, `Name` and a non-empty plugin id
(`MetadataContracts.cs:56-68`). The cache key includes both provider identity and app id
(`src/Playnite.Core/Metadata/MetadataCache.cs:294-308`), and content validation re-checks the
stored provider and app id before returning a document (`MetadataCache.cs:337-350`). The tests
exercise the seam with `FakeMetadataProvider` only and never open a socket
(`tests/Playnite.Core.Tests/Metadata/MetadataServiceTests.cs:1-297`).

### 2. Bounded cache, freshness, eviction and atomic replacement

`MetadataCacheOptions` defaults to 256 entries, 8 MiB and seven days and rejects non-positive
bounds and negative ages (`MetadataCache.cs:8-37`). Reads enforce the byte bound, delete corrupt
entries and report the problem (`MetadataCache.cs:103-169`). Stores compute count and byte usage,
evict least-recently-used entries by filesystem write time, refuse an entry that cannot fit, and
return `cache-size-limit` or `cache-entry-too-large` without fabricating a value
(`MetadataCache.cs:171-240`). The write path uses a sibling temporary file, flush-to-disk and
rename (`MetadataCache.cs:242-266`), and the read path validates schema, app/provider identity,
timestamp and every link/artwork element (`MetadataCache.cs:337-388`). A single `SemaphoreSlim`
serialises read and store operations (`MetadataCache.cs:103-109`, `:171-193`), matching the
documented one-owner contract.

### 3. Failure, stale and offline semantics

The outcome table in the unit document is implemented as written: fresh hit
(`MetadataService.cs:70-80`), provider success (`:82-97`), stale-on-provider-failure
(`:99-109`), no-entry failure (`:111-119`), offline hit/miss (`:46-68`). The offline reason
mapping covers corrupt, unreadable and miss (`MetadataService.cs:165-173`). Provider exceptions,
non-caller cancellations and null results become stable reasons, while caller cancellation
rethrows (`MetadataService.cs:151-162`). `GetManyAsync` preserves order and continues after a
per-game failure (`MetadataService.cs:125-139`). Cache problems travel separately as
`CacheWarning` and never replace a provider result (`MetadataService.cs:96`, `:118`).

### 4. Determinism and dependency direction

The cache filename is a SHA-256 digest (`MetadataCache.cs:306-308`), the entry shape is
schema-versioned and field-validated, and serialization uses fixed camel-case options
(`MetadataCache.cs:61-65`). The product source has no `System.Net`, `HttpClient`, `WebRequest`,
`System.Windows` or `Avalonia` reference, and the source scan test checks the same strings
(`MetadataServiceTests.cs:319-333`). The unit lives in `Playnite.Core` and depends on no Steam
type, as documented.

### 5. Ownership and evidence

The change is additive under `src/Playnite.Core/Metadata` and the test project; it updates only
the p6-005 unit shard and its own ledger fragment. The PR body and ledger cite linux-ci
`44696697852` on implementation head `29b7b671eaa4fe9c4dac09054fb1e218c4eedd50` with Core
2809/2809, Architecture 199/199 and Smoke 4/4, zero failures and zero skips, and the PR currently
reports 8/8 checks passing. No protected oracle, baseline, aggregate or UI file is edited in the
candidate.

## Non-blocking observations

- `MetadataCache.PathFor` builds its digest from `provider + "\n" + appId`
  (`MetadataCache.cs:306`). Two arbitrary strings whose embedded newline falls in different
  positions can produce the same key; content validation then reports the overwritten entry as
  corrupt rather than returning a foreign document. This needs a provider identity or app id
  that embeds a newline, and the service already validates the stored content, so it is not
  blocking; a length-prefixed or component-hashed key would remove the ambiguity.
- The service clock supplies `FetchedAt` (`MetadataService.cs:85`) while the cache clock supplies
  freshness (`MetadataCache.cs:157`). Callers that inject different `TimeProvider` instances
  into the two constructors will get surprising stale/hit decisions; the tests always pass the
  same clock to both.
- `File.Exists` is false for some permission-denied paths, so an unreadable cache directory can
  be reported as `offline-cache-miss` rather than `offline-cache-unreadable`
  (`MetadataCache.cs:113`); the file-open path does map `IOException`/`UnauthorizedAccessException`
  correctly (`MetadataCache.cs:145-147`).

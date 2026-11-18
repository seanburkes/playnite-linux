# p5-003: explicit-source artwork and optional metadata acquisition

Greenfield contract: ADR-017 and Steam spec §8. This is a transport/cache unit, not a
claim of Steam CDN acceptance. **§8.1 CDN remains UNKNOWN. Remote mapping and real artwork
fetch remain a Phase 5 exit evidence gap** until captured HTTP/artwork-id evidence exists.
No URL, content-addressed Steam id, account selection, or alternate source is inferred.
The recorded corpus explicitly excludes artwork (`tests/fixtures/steam/real/README.md`,
“Not covered”); its empty-achievement JSON is exercised read-only in the tests.

## Evidence-backed identities

For decimal uint app id `A`, relative to the explicit Steam root:

| Kind | Observed identity (§8.1) |
| --- | --- |
| Portrait | `appcache/librarycache/A/library_600x900.jpg` |
| Header | `appcache/librarycache/A/header.jpg` |
| Hero | `appcache/librarycache/A/library_hero.jpg` |
| Logo | `appcache/librarycache/A/logo.png` |
| HeroBlur | `appcache/librarycache/A/library_hero_blur.jpg` |

All are optional. The spec records names/layout, **not per-title availability or remote ids**.
There is no `A_library_600x900.jpg` identity in the recorded layout. Hashed files are opaque.
Achievements are separate optional metadata: `config/librarycache/A.json` relative to an
**explicitly selected account's userdata directory**, as observed in §8.2. No account id is
logged or inferred. Title exclusion/unconfigured mapping is a reported result, not a drop.

## API and failures

`IArtworkSource.Resolve` is injected and local/pure. It supplies an explicit absolute file,
HTTP or HTTPS URI, optionally with expected SHA-256. There is intentionally no production
remote mapper. A null source reports `source-unconfigured-or-title-excluded` without HTTP.
The caller supplies a reusable `HttpClient` **with automatic redirects disabled**, to prevent
implicit URL fallback, and owns its lifetime. Tests use loopback TCP HTTP only, never external
network or host Steam data. Inject the cache root from `IPaths` under the application's Steam
cache directory; no environment/home discovery occurs here.

`FetchAsync` returns either an existing cache path or null plus issues containing app id,
kind, and stable reason. Callers must surface the issues. Diagnostics contain no source URL,
absolute path, account data, raw metadata, or exception text. There are no automatic retries
or source fallbacks. An unwritable/unreadable cache or failed publication yields a reported
null path; there is **no uncached temporary/source-path success**. Cancellation is reported
as `cancelled`; HTTP timeouts are distinct. Local source reads never modify Steam files.

Offline mode permits a validated cache hit or explicit local-file import; a remote miss
reports `offline-cache-miss` without network. Successful bodies are bounded before and during
streaming, length-checked, and validated as JPEG envelope, PNG signature, or JSON array
according to kind. This is not image decoding or achievement-model projection; a downstream
decoder still must validate complete image semantics. An optional expected hash is checked
before publication. Cache hits verify their byte length and SHA-256. Corruption fails clearly
rather than returning a stale or partial path.

## Cache, crash and lifetime rules

One fetcher/cache owner per **private application-owned directory**, no concurrent external
writers. Operations are serialized. Cache keys are SHA-256 of invariant decimal app id,
kind (including nominal artwork size), exact explicit source URI and optional expected hash.
Final names are `<key>-<byte-length>-<content-sha256>.blob`. Source spelling changes invalidate
the cache key; no filesystem source mtime is guessed. A source mapping can supply an expected
hash to explicitly version mutable sources. Without that, cached immutable content remains
until eviction (no implicit freshness/network probe).

The configured positive byte bound covers owned blobs plus the active temporary file. Evict
oldest `LastWriteTimeUtc` first, then ordinal filename on ties, **before** writing incoming
bytes; reads do not change eviction order (FIFO, not LRU). An entry larger than either the
body limit or total cache capacity is rejected without publication. Orphan `.tmp` files from
an interrupted write never count as hits and are reclaimed before the next write. Only this
private cache's `.blob` and `.tmp` entries are managed; unrelated files are not cache entries.

Write to a create-new `.tmp` in the same directory, flush bytes to disk, close, then atomic
rename to the final `.blob`. Cancellation/failure cleans the temp; process interruption may
leave only an invisible-to-lookup temp, never a partially published blob. This promises atomic
visibility, not directory-fsync power-loss durability. Symlink cache paths/entries are rejected;
private ownership is required to exclude hostile concurrent symlink swaps. No writes occur
outside the cache. Returned paths are verified at return, not pinned against later eviction;
consumers must handle a subsequent eviction normally.

## Rate limit

One request start per second by default (configurable nonnegative interval), shared by every
title/kind in the fetcher, including failures. A 429 reports immediately and raises the next
eligible request time using `Retry-After` delta seconds or HTTP date; the regular interval
remains a floor. Missing Retry-After uses that floor. No retry is sent for the failed call.
Cache hits/offline misses/local imports consume no HTTP slot. `IArtworkClock` supplies both
time and cancellation-aware delay; stub tests advance virtual time instead of sleeping.

## Verification and review

`tests/Playnite.Core.Tests/Artwork/ArtworkTests.cs` is SDK-globbed, as are these product files;
no shared project/lock file changes are needed. The unfiltered Core suite covers success,
404, 429 (both Retry-After forms), 5xx, redirects rejected, empty/invalid/truncated/oversized
declared and streamed bodies, source hash mismatch, offline hit/miss, excluded title,
read-only real corpus metadata, bounded FIFO eviction/ties, deterministic keys, corrupted
cache, orphan temp recovery, cancelled atomic publication, unwritable cache, symlinks and
path-shaped keys, rate-limit timing and cancellation at queue/network/rate wait boundaries.

Owner adversarial diff passes (no delegation permitted by worker role):
1. Boundary/security pass found public cache keys needed traversal/glob rejection; added
   validation and tests. Checked immutable oracle and shared-project paths remained untouched.
2. Failure/lifetime pass found truncated streaming uses `HttpIOException`; added a specific
   reason rather than misclassifying it as disk I/O. Added queue/network/rate cancellation
   tests. Independent external review remains the PR review gate, not claimed here.

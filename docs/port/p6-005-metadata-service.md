# p6-005 — Metadata service

`Playnite.Core.Metadata.MetadataService` resolves description, web links and artwork
references for one game at a time and caches the result. It is headless: the service references
no Avalonia or UI type, opens no socket, and delegates every lookup to an injected
`IMetadataProvider`. The unit ships the provider seam, a bounded atomic cache and an honest
per-game failure contract; the tests use a local fake provider and never touch the network.

The service lives in `Playnite.Core` because it is a UI-free core service. It has no dependency
on `Playnite.Steam`: artwork references are opaque locations, not fetched bytes, so no Steam type
is needed here. p5-003 remains the owner of artwork acquisition and its own cache.

## Why there is no live Steam provider in this unit

`docs/port/08-STEAM-SPEC.md` §8.1 records that the Steam CDN artwork mapping and remote artwork
identities are unverified on the reference host, and ADR-019 keeps JavaScript scraping out of
v1. The captain's scope for this unit is an injectable provider, a bounded cache and honest
failure reporting with no live network. A production Steam JSON provider can be added over this
seam once verified endpoint evidence exists; inventing a URL or a fallback source here would
violate the no-fabrication rule. p6-007 consumes the resolved description through its HTML
rendering path.

## Contract

| Input | Result |
| --- | --- |
| `MetadataQuery(appId, name?, pluginId?)` | One provider game identity. `AppId` is required. |
| `MetadataService.GetAsync(query, offline, cancellationToken)` | One `MetadataResult` with a document or a stable reason. |
| `MetadataService.GetManyAsync(queries, offline, cancellationToken)` | Ordered `MetadataBatchResult`; one failure never aborts later games. |
| `IMetadataProvider.Identity` | Stable provider identity; part of every cache key. |
| `IMetadataProvider.FetchAsync(query, cancellationToken)` | `MetadataProviderResult.Success(document)` or `.Failure(reason)`. |

`MetadataDocument` carries `Description` (HTML or null), `Links` and `Artwork`. An absent field
stays absent: the service never synthesises a description, a link or an artwork location. An
empty list means the provider supplied none, not that the service guessed.

`MetadataQuery.FromGame(Game)` is the p6-001 bridge: it copies the game's provider `GameId`,
display name and non-empty plugin id, and refuses a game with no provider app id. A caller with a
`StoredGame` passes `stored.Value`.

## Cache

The cache is a private directory with one JSON entry per `(provider identity, app id)` pair. The
filename is the SHA-256 of the two values, so a key cannot contain a path component. Each entry
records a schema version, the app id, the provider identity, the fetch timestamp, and the
document. An entry is accepted only when all of those values are present and the app/provider
match the requested key; an unknown schema, bad enum name or malformed link/artwork makes the
entry corrupt.

| Property | Behaviour |
| --- | --- |
| Bound | `MetadataCacheOptions.MaxEntries` (default 256) and `MaxBytes` (default 8 MiB). |
| Freshness | `MetadataCacheOptions.MaxAge` (default seven days) measured from the injected clock. |
| Eviction | Least recently used first; a hit refreshes the entry's filesystem write time. |
| Atomicity | The entry is written to a sibling temporary file, flushed, and renamed over the target. A reader never sees a partial entry and an interrupted write leaves no live entry. |
| Corrupt entry | Deleted and reported as `cache-corrupt`; the service falls back to the provider when online. |
| Cache failure | A write failure is a `CacheWarning` on an otherwise successful result, never a fabricated value and never a lost provider result. |
| Concurrency | One cache owner serialises read/store operations; the directory must not be shared by separate processes. |

## Outcomes and reasons

| Situation | Status | Document | Reason |
| --- | --- | --- | --- |
| Fresh cache entry, online | `CacheHit` | Cached entry | none |
| No entry or stale entry, provider succeeds | `Resolved` | Provider document | none |
| Stale entry, provider fails | `Stale` | Previous entry | provider reason |
| No entry, provider fails | `Failed` | null | provider reason |
| Offline, entry present (fresh or stale) | `OfflineCacheHit` | Cached entry | none; `IsStale` is explicit |
| Offline, no entry | `Failed` | null | `offline-cache-miss` |
| Offline, corrupt entry | `Failed` | null | `offline-cache-corrupt` |
| Offline, unreadable entry | `Failed` | null | `offline-cache-unreadable` |

Provider results carry their own stable reason (`not-found`, `steam-unavailable`, and so on).
The service maps a provider exception to `provider-exception`, a provider cancellation that the
caller did not request to `provider-timeout`, and a null/invalid result to
`provider-invalid-response`. Cancellation requested by the caller is never converted into a
failure: it propagates as `OperationCanceledException` and stops the batch.

`CacheWarning` is separate from `FailureReason` because a cache problem does not invalidate a
fresh provider answer. It reports `cache-entry-too-large`, `cache-size-limit`,
`cache-write-failed`, `cache-read-failed`, `cache-corrupt` or `cache-touch-failed`.

## Tests

| Test | Evidence |
| --- | --- |
| `MetadataServiceTests.Provider_contract_resolves_description_links_and_artwork_without_inventing_values` | The fake provider supplies description, one link and two artwork references; absent values stay null/empty. |
| `MetadataServiceTests.Cache_hit_avoids_a_second_call_and_is_keyed_by_app_id_and_provider_identity` | App id and provider identity both isolate entries; a cache hit calls no provider. |
| `MetadataServiceTests.Stale_cache_is_refreshed_online_and_served_with_a_reason_when_the_provider_fails` | Max-age refresh and the stale-on-failure path with the previous document and provider reason. |
| `MetadataServiceTests.Provider_failure_without_a_cache_entry_is_reported_and_never_fabricated` | Typed failure, exception, internal timeout and invalid result each produce a failed result with no document. |
| `MetadataServiceTests.Offline_mode_uses_only_the_cache_and_never_calls_the_provider` | Fresh and stale offline hits plus the offline miss; provider call count stays zero. |
| `MetadataServiceTests.Corrupt_cache_is_reported_offline_and_repaired_online` | A corrupted entry is refused offline and replaced from the provider online. |
| `MetadataServiceTests.Batch_reports_each_failure_and_continues_with_later_games` | Ordered batch results; a typed failure and an exception do not stop the fourth game. |
| `MetadataServiceTests.Cancellation_stops_the_batch_and_writes_no_result_for_later_games` | Pre-cancelled and mid-batch cancellation; later games are not requested and no partial result is written. |
| `MetadataServiceTests.Game_queries_carry_the_provider_identity_and_reject_a_missing_app_id` | The p6-001 `Game` bridge and its invalid-input refusal. |
| `MetadataServiceTests.Metadata_source_has_no_network_or_ui_dependency` | Source scan of the metadata folder for network and UI types. |
| `MetadataCacheTests.Entries_are_isolated_by_provider_and_app_id_and_round_trip_exactly` | Three key combinations coexist and round-trip. |
| `MetadataCacheTests.A_read_marks_an_entry_as_recently_used_so_eviction_drops_the_oldest_untouched_entry` | LRU eviction after an explicit hit. |
| `MetadataCacheTests.The_byte_bound_evicts_entries_before_an_entry_would_exceed_it` | Byte-bounded eviction and the resulting single entry. |
| `MetadataCacheTests.An_entry_larger_than_the_bound_is_rejected_without_writing` | Oversized entries are refused, not truncated. |
| `MetadataCacheTests.Replacing_an_entry_is_atomic_and_leaves_no_temporary_file` | Atomic replacement and temp cleanup. |
| `MetadataCacheTests.A_corrupt_entry_is_deleted_and_reported_without_throwing` | Corrupt JSON is removed and reported. |
| `MetadataCacheTests.Freshness_is_reported_from_the_injected_clock` | Fresh, almost-stale and stale boundaries on a fixed clock. |
| `MetadataCacheTests.Unsafe_cache_directories_are_rejected` | Relative, root and invalid-bound configurations fail fast. |

The tests create their cache under the system temporary directory, inject a manual `TimeProvider`
and use only `FakeMetadataProvider`. They do not read the developer's Steam installation, start a
server, or issue a network request.

## References

- `docs/port/03-EXECUTION-PLAN.md` Phase 6: metadata service with cache, injectable provider and
  no WebView.
- `docs/port/02-ARCHITECTURE.md` §3: Core stays free of Avalonia and UI services.
- `docs/port/00-DECISIONS.md` ADR-019: no WebView in v1; plain HTTP and HTML rendering.
- `docs/port/08-STEAM-SPEC.md` §8: artwork and metadata caches; remote Steam artwork mapping is
  unverified.
- p6-001: `LibraryService` and `StoredGame`; p5-003: `Playnite.Steam.Artwork.ArtworkCache` and
  `ArtworkFetcher` for the separate artwork-byte cache.

namespace Playnite.Core.Metadata;

/// <summary>
/// Resolves and caches provider metadata for one game at a time. The service is UI-free and
/// network-free: it delegates every lookup to <see cref="IMetadataProvider"/> and only reads or
/// writes the bounded cache. No value is invented when a provider or the cache has nothing.
/// </summary>
public sealed class MetadataService
{
    private readonly IMetadataProvider provider;
    private readonly MetadataCache cache;
    private readonly string providerIdentity;
    private readonly TimeProvider clock;

    /// <summary>Creates a service over an injectable provider and cache.</summary>
    public MetadataService(IMetadataProvider provider, MetadataCache cache, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(cache);
        if (string.IsNullOrWhiteSpace(provider.Identity))
        {
            throw new ArgumentException("A provider identity is required for cache keys.", nameof(provider));
        }

        this.provider = provider;
        this.cache = cache;
        providerIdentity = provider.Identity;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Resolves metadata for one game.</summary>
    /// <param name="query">The provider game identity.</param>
    /// <param name="offline">When true, the provider is never called and only a cache entry may answer.</param>
    /// <param name="cancellationToken">Cancels the provider call and cache access.</param>
    public async Task<MetadataResult> GetAsync(
        MetadataQuery query,
        bool offline = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        MetadataCacheRead cached = await cache.ReadAsync(
            providerIdentity, query.AppId, cancellationToken).ConfigureAwait(false);

        if (offline)
        {
            if (cached.Document is not null)
            {
                return new MetadataResult(
                    query,
                    cached.Document,
                    MetadataResultStatus.OfflineCacheHit,
                    FailureReason: null,
                    FetchedAt: cached.FetchedAt,
                    IsStale: cached.IsStale,
                    CacheWarning: cached.Warning);
            }

            return new MetadataResult(
                query,
                Document: null,
                MetadataResultStatus.Failed,
                FailureReason: OfflineFailureReason(cached.Problem),
                FetchedAt: null,
                IsStale: false,
                CacheWarning: cached.Warning);
        }

        if (cached.Document is not null && !cached.IsStale)
        {
            return new MetadataResult(
                query,
                cached.Document,
                MetadataResultStatus.CacheHit,
                FailureReason: null,
                FetchedAt: cached.FetchedAt,
                IsStale: false,
                CacheWarning: cached.Warning);
        }

        MetadataProviderResult fetched = await FetchAsync(query, cancellationToken).ConfigureAwait(false);
        if (fetched.Document is not null)
        {
            DateTimeOffset fetchedAt = clock.GetUtcNow();
            MetadataCacheWrite stored = await cache.StoreAsync(
                providerIdentity, query.AppId, fetched.Document, fetchedAt, cancellationToken)
                .ConfigureAwait(false);
            return new MetadataResult(
                query,
                fetched.Document,
                MetadataResultStatus.Resolved,
                FailureReason: null,
                FetchedAt: fetchedAt,
                IsStale: false,
                CacheWarning: stored.Problem ?? cached.Problem ?? cached.Warning);
        }

        if (cached.Document is not null)
        {
            return new MetadataResult(
                query,
                cached.Document,
                MetadataResultStatus.Stale,
                FailureReason: fetched.FailureReason,
                FetchedAt: cached.FetchedAt,
                IsStale: true,
                CacheWarning: cached.Warning);
        }

        return new MetadataResult(
            query,
            Document: null,
            MetadataResultStatus.Failed,
            FailureReason: fetched.FailureReason,
            FetchedAt: null,
            IsStale: false,
            CacheWarning: cached.Problem ?? cached.Warning);
    }

    /// <summary>
    /// Resolves metadata for every query in order. A provider failure is reported on that game's
    /// result and the batch continues; cancellation still aborts the batch.
    /// </summary>
    public async Task<MetadataBatchResult> GetManyAsync(
        IEnumerable<MetadataQuery> queries,
        bool offline = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var results = new List<MetadataResult>();
        foreach (MetadataQuery query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await GetAsync(query, offline, cancellationToken).ConfigureAwait(false));
        }

        return new MetadataBatchResult(results.AsReadOnly());
    }

    private async Task<MetadataProviderResult> FetchAsync(
        MetadataQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            MetadataProviderResult result = await provider.FetchAsync(query, cancellationToken)
                .ConfigureAwait(false);
            return result ?? MetadataProviderResult.Failure("provider-invalid-response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return MetadataProviderResult.Failure("provider-timeout");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return MetadataProviderResult.Failure("provider-exception");
        }
    }

    private static string OfflineFailureReason(string? cacheProblem)
    {
        return cacheProblem switch
        {
            "cache-corrupt" => "offline-cache-corrupt",
            "cache-read-failed" => "offline-cache-unreadable",
            _ => "offline-cache-miss",
        };
    }
}

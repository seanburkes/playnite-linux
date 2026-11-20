using System.Text;
using Playnite.Core.Metadata;
using Playnite.Core.Models;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Metadata;

public sealed class MetadataServiceTests : IDisposable
{
    private static readonly MetadataDocument Alpha = new(
        "<p>Alpha description</p>",
        [new MetadataLink("Store", "https://store.example/alpha")],
        [
            new MetadataArtwork(MetadataArtworkKind.Cover, "https://cdn.example/alpha-cover.jpg"),
            new MetadataArtwork(MetadataArtworkKind.Background, "https://cdn.example/alpha-background.jpg"),
        ]);

    private static readonly MetadataDocument Beta = new(
        "<p>Beta description</p>",
        [new MetadataLink("Wiki", "https://wiki.example/beta")],
        [new MetadataArtwork(MetadataArtworkKind.Icon, "https://cdn.example/beta-icon.png")]);

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "playnite-metadata-service-" + Path.GetRandomFileName());
    private readonly ManualTimeProvider clock = new();

    public MetadataServiceTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Provider_contract_resolves_description_links_and_artwork_without_inventing_values()
    {
        var provider = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(Alpha));
        using var cache = NewCache();
        var service = new MetadataService(provider, cache, clock);
        var query = new MetadataQuery("570", "Team Fortress 2");

        MetadataResult result = await service.GetAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MetadataResultStatus.Resolved, result.Status);
        Assert.True(result.Succeeded);
        Assert.False(result.IsStale);
        Assert.Null(result.FailureReason);
        AssertDocument(Alpha, result.Document);
        Assert.Equal(clock.UtcNow, result.FetchedAt);
        MetadataQuery call = Assert.Single(provider.Calls);
        Assert.Same(query, call);

        var empty = new MetadataDocument(description: null);
        provider = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(empty));
        MetadataResult emptyResult = await new MetadataService(provider, cache, clock)
            .GetAsync(new MetadataQuery("571"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MetadataResultStatus.Resolved, emptyResult.Status);
        Assert.NotNull(emptyResult.Document);
        Assert.Null(emptyResult.Document.Description);
        Assert.Empty(emptyResult.Document.Links);
        Assert.Empty(emptyResult.Document.Artwork);
    }

    [Fact]
    public async Task Cache_hit_avoids_a_second_call_and_is_keyed_by_app_id_and_provider_identity()
    {
        var provider = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(Alpha));
        using var cache = NewCache();
        var service = new MetadataService(provider, cache, clock);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await service.GetAsync(new MetadataQuery("570", "First name"), cancellationToken: cancellationToken);
        MetadataResult hit = await service.GetAsync(
            new MetadataQuery("570", "A different name", Guid.Parse("11111111-1111-1111-1111-111111111111")),
            cancellationToken: cancellationToken);

        Assert.Equal(MetadataResultStatus.CacheHit, hit.Status);
        AssertDocument(Alpha, hit.Document);
        Assert.Single(provider.Calls);

        MetadataResult miss = await service.GetAsync(
            new MetadataQuery("5700"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Resolved, miss.Status);
        Assert.Equal(2, provider.Calls.Count);

        var otherProvider = FakeMetadataProvider.Returning("other-fake", MetadataProviderResult.Success(Beta));
        MetadataResult other = await new MetadataService(otherProvider, cache, clock)
            .GetAsync(new MetadataQuery("570"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Resolved, other.Status);
        AssertDocument(Beta, other.Document);
        Assert.Single(otherProvider.Calls);

        MetadataResult originalStillCached = await service.GetAsync(
            new MetadataQuery("570"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.CacheHit, originalStillCached.Status);
        AssertDocument(Alpha, originalStillCached.Document);
    }

    [Fact]
    public async Task Stale_cache_is_refreshed_online_and_served_with_a_reason_when_the_provider_fails()
    {
        var provider = new FakeMetadataProvider(
            "fake",
            (query, _) => Task.FromResult(MetadataProviderResult.Success(
                query.AppId == "570" ? Alpha : Beta)));
        using var cache = NewCache();
        var service = new MetadataService(provider, cache, clock);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var query = new MetadataQuery("570");

        await service.GetAsync(query, cancellationToken: cancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        var refreshed = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(Beta));
        var refreshingService = new MetadataService(refreshed, cache, clock);
        MetadataResult second = await refreshingService.GetAsync(query, cancellationToken: cancellationToken);

        Assert.Equal(MetadataResultStatus.Resolved, second.Status);
        AssertDocument(Beta, second.Document);
        Assert.Single(refreshed.Calls);

        clock.Advance(TimeSpan.FromHours(2));
        var failing = FakeMetadataProvider.Returning(
            "fake", MetadataProviderResult.Failure("steam-unavailable"));
        MetadataResult stale = await new MetadataService(failing, cache, clock)
            .GetAsync(query, cancellationToken: cancellationToken);

        Assert.Equal(MetadataResultStatus.Stale, stale.Status);
        Assert.True(stale.IsStale);
        Assert.Equal("steam-unavailable", stale.FailureReason);
        AssertDocument(Beta, stale.Document);
        Assert.Equal(clock.UtcNow - TimeSpan.FromHours(2), stale.FetchedAt);
    }

    [Fact]
    public async Task Provider_failure_without_a_cache_entry_is_reported_and_never_fabricated()
    {
        using var cache = NewCache();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var failing = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Failure("not-found"));
        MetadataResult missing = await new MetadataService(failing, cache, clock)
            .GetAsync(new MetadataQuery("404"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Failed, missing.Status);
        Assert.False(missing.Succeeded);
        Assert.Null(missing.Document);
        Assert.Equal("not-found", missing.FailureReason);

        var throwing = new FakeMetadataProvider(
            "fake", (_, _) => throw new InvalidOperationException("provider exploded"));
        MetadataResult exception = await new MetadataService(throwing, cache, clock)
            .GetAsync(new MetadataQuery("500"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Failed, exception.Status);
        Assert.Null(exception.Document);
        Assert.Equal("provider-exception", exception.FailureReason);

        var cancelling = new FakeMetadataProvider(
            "fake", (_, _) => throw new OperationCanceledException());
        MetadataResult timeout = await new MetadataService(cancelling, cache, clock)
            .GetAsync(new MetadataQuery("501"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Failed, timeout.Status);
        Assert.Null(timeout.Document);
        Assert.Equal("provider-timeout", timeout.FailureReason);

        var nullResult = new FakeMetadataProvider(
            "fake", (_, _) => Task.FromResult<MetadataProviderResult>(null!));
        MetadataResult invalid = await new MetadataService(nullResult, cache, clock)
            .GetAsync(new MetadataQuery("502"), cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Failed, invalid.Status);
        Assert.Null(invalid.Document);
        Assert.Equal("provider-invalid-response", invalid.FailureReason);
    }

    [Fact]
    public async Task Offline_mode_uses_only_the_cache_and_never_calls_the_provider()
    {
        var provider = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(Alpha));
        using var cache = NewCache();
        var service = new MetadataService(provider, cache, clock);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var query = new MetadataQuery("570");

        await service.GetAsync(query, cancellationToken: cancellationToken);
        provider.Calls.Clear();

        MetadataResult fresh = await service.GetAsync(query, offline: true, cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.OfflineCacheHit, fresh.Status);
        Assert.False(fresh.IsStale);
        AssertDocument(Alpha, fresh.Document);
        Assert.Empty(provider.Calls);

        clock.Advance(TimeSpan.FromHours(2));
        MetadataResult stale = await service.GetAsync(query, offline: true, cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.OfflineCacheHit, stale.Status);
        Assert.True(stale.IsStale);
        AssertDocument(Alpha, stale.Document);
        Assert.Empty(provider.Calls);

        MetadataResult miss = await service.GetAsync(
            new MetadataQuery("missing"), offline: true, cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Failed, miss.Status);
        Assert.Null(miss.Document);
        Assert.Equal("offline-cache-miss", miss.FailureReason);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Corrupt_cache_is_reported_offline_and_repaired_online()
    {
        var provider = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(Alpha));
        using var cache = NewCache();
        var service = new MetadataService(provider, cache, clock);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var query = new MetadataQuery("570");

        await service.GetAsync(query, cancellationToken: cancellationToken);
        string entry = Assert.Single(Directory.GetFiles(directory, "*.json"));
        await File.WriteAllTextAsync(entry, "not a cache entry", Encoding.UTF8, cancellationToken);
        provider.Calls.Clear();

        MetadataResult offline = await service.GetAsync(query, offline: true, cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Failed, offline.Status);
        Assert.Null(offline.Document);
        Assert.Equal("offline-cache-corrupt", offline.FailureReason);
        Assert.Empty(provider.Calls);

        MetadataResult repaired = await service.GetAsync(query, cancellationToken: cancellationToken);
        Assert.Equal(MetadataResultStatus.Resolved, repaired.Status);
        AssertDocument(Alpha, repaired.Document);
        Assert.Single(provider.Calls);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task Batch_reports_each_failure_and_continues_with_later_games()
    {
        var provider = new FakeMetadataProvider("fake", (query, _) => query.AppId switch
        {
            "alpha" => Task.FromResult(MetadataProviderResult.Success(Alpha)),
            "beta" => Task.FromResult(MetadataProviderResult.Failure("not-found")),
            "gamma" => throw new InvalidOperationException("provider exploded"),
            _ => Task.FromResult(MetadataProviderResult.Success(Beta)),
        });
        using var cache = NewCache();
        var service = new MetadataService(provider, cache, clock);

        MetadataBatchResult batch = await service.GetManyAsync(
            [new MetadataQuery("alpha"), new MetadataQuery("beta"),
             new MetadataQuery("gamma"), new MetadataQuery("delta")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, batch.Results.Count);
        Assert.Equal(["alpha", "beta", "gamma", "delta"], batch.Results.Select(result => result.Query.AppId));
        Assert.Equal(2, batch.SucceededCount);
        Assert.Equal(2, batch.FailedCount);
        Assert.Equal(MetadataResultStatus.Resolved, batch.Results[0].Status);
        AssertDocument(Alpha, batch.Results[0].Document);
        Assert.Equal(MetadataResultStatus.Failed, batch.Results[1].Status);
        Assert.Equal("not-found", batch.Results[1].FailureReason);
        Assert.Equal(MetadataResultStatus.Failed, batch.Results[2].Status);
        Assert.Equal("provider-exception", batch.Results[2].FailureReason);
        Assert.Equal(MetadataResultStatus.Resolved, batch.Results[3].Status);
        AssertDocument(Beta, batch.Results[3].Document);
        Assert.Equal(4, provider.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_stops_the_batch_and_writes_no_result_for_later_games()
    {
        using var cache = NewCache();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var preCancelled = new CancellationTokenSource();
        await preCancelled.CancelAsync();
        var preProvider = FakeMetadataProvider.Returning("fake", MetadataProviderResult.Success(Alpha));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MetadataService(preProvider, cache, clock).GetAsync(
                new MetadataQuery("alpha"), cancellationToken: preCancelled.Token));
        Assert.Empty(preProvider.Calls);
        Assert.Empty(Directory.GetFiles(directory, "*.json"));

        using var source = new CancellationTokenSource();
        var provider = new FakeMetadataProvider("fake", (query, token) =>
        {
            if (query.AppId == "beta")
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.FromResult(MetadataProviderResult.Success(Alpha));
        });
        var service = new MetadataService(provider, cache, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetManyAsync(
            [new MetadataQuery("alpha"), new MetadataQuery("beta"), new MetadataQuery("gamma")],
            cancellationToken: source.Token));

        Assert.Equal(["alpha", "beta"], provider.Calls.Select(call => call.AppId));
        Assert.Single(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public void Game_queries_carry_the_provider_identity_and_reject_a_missing_app_id()
    {
        var pluginId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var game = new Game(Guid.Parse("33333333-3333-3333-3333-333333333333"), "570", "Team Fortress 2")
        {
            PluginId = pluginId,
        };

        MetadataQuery query = MetadataQuery.FromGame(game);

        Assert.Equal("570", query.AppId);
        Assert.Equal("Team Fortress 2", query.Name);
        Assert.Equal(pluginId, query.PluginId);
        Assert.Throws<ArgumentException>(() => MetadataQuery.FromGame(
            new Game(Guid.Parse("44444444-4444-4444-4444-444444444444"), " ")));
        Assert.Throws<ArgumentException>(() => new MetadataQuery(" "));
    }

    [Fact]
    public void Metadata_source_has_no_network_or_ui_dependency()
    {
        string sourceDirectory = RepositoryPaths.Combine("src", "Playnite.Core", "Metadata");
        string[] files = Directory.GetFiles(sourceDirectory, "*.cs");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("System.Net", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WebRequest", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private MetadataCache NewCache()
    {
        return new MetadataCache(
            directory,
            new MetadataCacheOptions { MaxAge = TimeSpan.FromHours(1) },
            clock);
    }

    private static void AssertDocument(MetadataDocument expected, MetadataDocument? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Links, actual.Links);
        Assert.Equal(expected.Artwork, actual.Artwork);
    }
}

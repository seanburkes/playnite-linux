using System.Text;
using Playnite.Core.Metadata;
using Xunit;

namespace Playnite.Core.Tests.Metadata;

public sealed class MetadataCacheTests : IDisposable
{
    private static readonly MetadataDocument First = new(
        "<p>First</p>",
        [new MetadataLink("Store", "https://store.example/first")],
        [new MetadataArtwork(MetadataArtworkKind.Cover, "https://cdn.example/first.jpg")]);

    private static readonly MetadataDocument Second = new(
        "<p>Second</p>",
        [new MetadataLink("Wiki", "https://wiki.example/second")],
        [new MetadataArtwork(MetadataArtworkKind.Hero, "https://cdn.example/second.jpg")]);

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "playnite-metadata-cache-" + Path.GetRandomFileName());
    private readonly ManualTimeProvider clock = new();

    public MetadataCacheTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Entries_are_isolated_by_provider_and_app_id_and_round_trip_exactly()
    {
        using var cache = NewCache();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.True((await Store(cache, "fake", "570", First, cancellationToken)).Stored);
        Assert.True((await Store(cache, "fake", "5700", Second, cancellationToken)).Stored);
        Assert.True((await Store(cache, "other", "570", Second, cancellationToken)).Stored);

        MetadataCacheRead first = await cache.ReadAsync("fake", "570", cancellationToken);
        MetadataCacheRead second = await cache.ReadAsync("fake", "5700", cancellationToken);
        MetadataCacheRead other = await cache.ReadAsync("other", "570", cancellationToken);

        AssertDocument(First, first.Document);
        AssertDocument(Second, second.Document);
        AssertDocument(Second, other.Document);
        Assert.Equal(3, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public async Task A_read_marks_an_entry_as_recently_used_so_eviction_drops_the_oldest_untouched_entry()
    {
        using var cache = NewCache(maxEntries: 2);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Store(cache, "fake", "a", First, cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Store(cache, "fake", "b", Second, cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        MetadataCacheRead touched = await cache.ReadAsync("fake", "a", cancellationToken);
        AssertDocument(First, touched.Document);
        clock.Advance(TimeSpan.FromSeconds(1));

        await Store(cache, "fake", "c", First, cancellationToken);

        Assert.Null((await cache.ReadAsync("fake", "b", cancellationToken)).Document);
        AssertDocument(First, (await cache.ReadAsync("fake", "a", cancellationToken)).Document);
        AssertDocument(First, (await cache.ReadAsync("fake", "c", cancellationToken)).Document);
        Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public async Task The_byte_bound_evicts_entries_before_an_entry_would_exceed_it()
    {
        using var cache = NewCache(maxEntries: 100, maxBytes: 500);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var first = new MetadataDocument(new string('a', 200));
        var second = new MetadataDocument(new string('b', 200));

        Assert.True((await Store(cache, "fake", "a", first, cancellationToken)).Stored);
        Assert.True((await Store(cache, "fake", "b", second, cancellationToken)).Stored);

        Assert.Null((await cache.ReadAsync("fake", "a", cancellationToken)).Document);
        AssertDocument(second, (await cache.ReadAsync("fake", "b", cancellationToken)).Document);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task An_entry_larger_than_the_bound_is_rejected_without_writing()
    {
        using var cache = NewCache(maxBytes: 200);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        MetadataCacheWrite write = await Store(
            cache, "fake", "huge", new MetadataDocument(new string('x', 500)), cancellationToken);

        Assert.False(write.Stored);
        Assert.Equal("cache-entry-too-large", write.Problem);
        Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task Replacing_an_entry_is_atomic_and_leaves_no_temporary_file()
    {
        using var cache = NewCache();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Store(cache, "fake", "570", First, cancellationToken);
        await Store(cache, "fake", "570", Second, cancellationToken);

        AssertDocument(Second, (await cache.ReadAsync("fake", "570", cancellationToken)).Document);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.Empty(Directory.GetFiles(directory).Where(file => file.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_corrupt_entry_is_deleted_and_reported_without_throwing()
    {
        using var cache = NewCache();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Store(cache, "fake", "570", First, cancellationToken);
        string entry = Assert.Single(Directory.GetFiles(directory, "*.json"));
        await File.WriteAllTextAsync(entry, "{ not json", Encoding.UTF8, cancellationToken);

        MetadataCacheRead read = await cache.ReadAsync("fake", "570", cancellationToken);

        Assert.Null(read.Document);
        Assert.Equal("cache-corrupt", read.Problem);
        Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task Freshness_is_reported_from_the_injected_clock()
    {
        using var cache = NewCache();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Store(cache, "fake", "570", First, cancellationToken);

        MetadataCacheRead fresh = await cache.ReadAsync("fake", "570", cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(59));
        MetadataCacheRead almost = await cache.ReadAsync("fake", "570", cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        MetadataCacheRead stale = await cache.ReadAsync("fake", "570", cancellationToken);

        Assert.False(fresh.IsStale);
        Assert.False(almost.IsStale);
        Assert.True(stale.IsStale);
        Assert.Equal(clock.UtcNow - TimeSpan.FromMinutes(61), stale.FetchedAt);
    }

    [Fact]
    public void Unsafe_cache_directories_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new MetadataCache("relative/path"));
        Assert.Throws<ArgumentException>(() => new MetadataCache(Path.GetPathRoot(directory)!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetadataCache(
            directory, new MetadataCacheOptions { MaxEntries = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetadataCache(
            directory, new MetadataCacheOptions { MaxBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetadataCache(
            directory, new MetadataCacheOptions { MaxAge = TimeSpan.FromSeconds(-1) }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private MetadataCache NewCache(int maxEntries = 256, long maxBytes = 8 * 1024 * 1024)
    {
        return new MetadataCache(
            directory,
            new MetadataCacheOptions
            {
                MaxEntries = maxEntries,
                MaxBytes = maxBytes,
                MaxAge = TimeSpan.FromHours(1),
            },
            clock);
    }

    private async Task<MetadataCacheWrite> Store(
        MetadataCache cache,
        string provider,
        string appId,
        MetadataDocument document,
        CancellationToken cancellationToken)
    {
        // Every store takes a distinct mtime so eviction order is deterministic.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        return await cache.StoreAsync(provider, appId, document, clock.UtcNow, cancellationToken);
    }

    private static void AssertDocument(MetadataDocument expected, MetadataDocument? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Links, actual.Links);
        Assert.Equal(expected.Artwork, actual.Artwork);
    }
}

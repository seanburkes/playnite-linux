using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Playnite.Steam.Artwork;
using Xunit;

namespace Playnite.Core.Tests.Artwork;

public sealed class ArtworkTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 1, 2, 0xff, 0xd9];
    private static readonly ArtworkIdentity Header = new(1238040, ArtworkKind.Header);

    [Theory]
    [InlineData(ArtworkKind.Portrait, "library_600x900.jpg")]
    [InlineData(ArtworkKind.Header, "header.jpg")]
    [InlineData(ArtworkKind.Hero, "library_hero.jpg")]
    [InlineData(ArtworkKind.Logo, "logo.png")]
    [InlineData(ArtworkKind.HeroBlur, "library_hero_blur.jpg")]
    public void Identity_uses_only_observed_names(ArtworkKind kind, string name)
    {
        var identity = new ArtworkIdentity(1238040, kind);
        Assert.Equal(name, identity.FileName);
        Assert.Equal("appcache/librarycache/1238040/" + name, identity.RelativePath);
    }

    [Fact]
    public void Metadata_identity_is_relative_to_explicit_account_root()
    {
        Assert.Equal("config/librarycache/1238040.json", new ArtworkIdentity(1238040, ArtworkKind.Achievements).RelativePath);
    }

    [Fact]
    public async Task Success_is_atomic_content_addressed_and_offline_hit_never_requests_http()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg);
        var source = new Source(stub.Url);
        using var fetcher = Fetcher(source, temp.Path);
        var first = await fetcher.FetchAsync(Header);
        Assert.Empty(first.Issues);
        Assert.NotNull(first.Path);
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(first.Path));
        Assert.Contains("-6-" + Convert.ToHexString(SHA256.HashData(Jpeg)), first.Path);
        var second = await fetcher.FetchAsync(Header, offline: true);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, stub.Requests);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(404, "http-404")]
    [InlineData(500, "http-500")]
    [InlineData(503, "http-503")]
    [InlineData(302, "http-302")]
    public async Task Http_failures_report_identity_and_no_path(int status, string reason)
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg, status);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        AssertFailure(await fetcher.FetchAsync(Header), reason);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("date")]
    public async Task Retry_after_controls_next_stub_request_without_sleep(string format)
    {
        using var temp = new CacheDirectory();
        var clock = new Clock();
        string retry = format == "delta" ? "7" : clock.UtcNow.AddSeconds(7).ToString("R");
        await using var stub = new HttpStub(Jpeg, 429, extraHeaders: "Retry-After: " + retry + "\r\n", clock: clock);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path, clock);
        AssertFailure(await fetcher.FetchAsync(Header), "http-429-retry-after");
        AssertFailure(await fetcher.FetchAsync(Header), "http-429-retry-after");
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(clock.Delays));
        Assert.Equal(2, stub.Requests);
        Assert.Equal(TimeSpan.FromSeconds(7), stub.Times[1] - stub.Times[0]);
    }

    [Fact]
    public async Task Rate_limit_is_shared_across_titles_and_failures()
    {
        using var temp = new CacheDirectory();
        var clock = new Clock();
        await using var stub = new HttpStub(Jpeg, 404);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path, clock);
        await fetcher.FetchAsync(Header);
        await fetcher.FetchAsync(new(427520, ArtworkKind.Portrait));
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(clock.Delays));
        Assert.Equal(2, stub.Requests);
    }

    [Theory]
    [InlineData("empty", "body-empty")]
    [InlineData("invalid", "invalid-content")]
    [InlineData("oversized-declared", "body-size-limit")]
    [InlineData("oversized-streamed", "body-size-limit")]
    [InlineData("truncated", "http-transfer-failed-or-truncated")]
    public async Task Broken_bodies_never_publish(string mode, string reason)
    {
        using var temp = new CacheDirectory();
        byte[] body = mode switch { "empty" => [], "invalid" => [1, 2], _ => new byte[64] };
        long? length = mode switch { "truncated" => 100, "oversized-streamed" => null, _ => body.Length };
        await using var stub = new HttpStub(body, declaredLength: length, omitLength: mode == "oversized-streamed");
        using var fetcher = new ArtworkFetcher(new Source(stub.Url), new(temp.Path, 1024, Path.Combine(temp.Path, "steam")), new Clock(), maxBodyBytes: mode == "truncated" ? 200 : 32);
        AssertFailure(await fetcher.FetchAsync(Header), reason);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData(false, "source-unconfigured-or-title-excluded")]
    [InlineData(true, "offline-cache-miss")]
    public async Task Unconfigured_excluded_title_and_offline_miss_are_explicit(bool configured, string reason)
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(configured ? stub.Url : null), temp.Path);
        var excluded = new ArtworkIdentity(999999, ArtworkKind.Header);
        var result = await fetcher.FetchAsync(excluded, offline: true);
        Assert.Null(result.Path);
        Assert.Equal(new ArtworkIssue(999999, ArtworkKind.Header, reason), Assert.Single(result.Issues));
        Assert.Equal(0, stub.Requests);
    }

    [Fact]
    public async Task Source_hash_mismatch_is_not_cached()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url, new string('0', 64)), temp.Path);
        AssertFailure(await fetcher.FetchAsync(Header), "source-hash-mismatch");
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("[[\"achievements\",{\"data\":{\"nTotal\":0}}]]", true)]
    [InlineData("{}", false)]
    [InlineData("[", false)]
    public async Task Metadata_is_bounded_valid_json_not_an_image(string json, bool valid)
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Encoding.UTF8.GetBytes(json));
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        var result = await fetcher.FetchAsync(new(Header.AppId, ArtworkKind.Achievements));
        Assert.Equal(valid, result.Path != null);
        if (!valid) Assert.Equal("invalid-content", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public async Task Local_source_is_read_only_and_usable_offline()
    {
        using var temp = new CacheDirectory();
        string local = Path.Combine(temp.Path, "source.jpg");
        await File.WriteAllBytesAsync(local, Jpeg);
        using var fetcher = Fetcher(new Source(new Uri(local)), Path.Combine(temp.Path, "cache"));
        var result = await fetcher.FetchAsync(Header, offline: true);
        Assert.NotNull(result.Path);
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(local));
        Assert.NotEqual(local, result.Path);
    }

    [Fact]
    public async Task Missing_local_file_reports_without_creating_it()
    {
        using var temp = new CacheDirectory();
        string missing = Path.Combine(temp.Path, "absent.jpg");
        using var fetcher = Fetcher(new Source(new Uri(missing)), Path.Combine(temp.Path, "cache"));
        AssertFailure(await fetcher.FetchAsync(Header), "source-or-cache-io-failed");
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Local_source_changes_are_observed_even_with_identical_size_and_mtime()
    {
        using var temp = new CacheDirectory();
        string local = Path.Combine(temp.Path, "source.jpg");
        await File.WriteAllBytesAsync(local, Jpeg);
        File.SetLastWriteTimeUtc(local, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var fetcher = Fetcher(new Source(new Uri(local)), Path.Combine(temp.Path, "cache"));
        var first = await fetcher.FetchAsync(Header, offline: true);
        byte[] changed = [0xff, 0xd8, 3, 4, 0xff, 0xd9];
        await File.WriteAllBytesAsync(local, changed);
        File.SetLastWriteTimeUtc(local, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = await fetcher.FetchAsync(Header, offline: true);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(changed, await File.ReadAllBytesAsync(second.Path!));
    }

    [Fact]
    public async Task Unwritable_cache_reports_and_never_returns_source_path()
    {
        using var temp = new CacheDirectory();
        string blocked = Path.Combine(temp.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "unchanged");
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url), blocked);
        AssertFailure(await fetcher.FetchAsync(Header), "source-or-cache-io-failed");
        Assert.Equal("unchanged", await File.ReadAllTextAsync(blocked));
    }

    [Fact]
    public async Task Cache_eviction_is_oldest_write_then_ordinal_name_with_strict_byte_bound()
    {
        using var temp = new CacheDirectory();
        var cache = new ArtworkCache(temp.Path, 12, Path.Combine(temp.Path, "steam"));
        string a = await cache.StoreAsync("A", Jpeg, default);
        string b = await cache.StoreAsync("B", Jpeg, default);
        File.SetLastWriteTimeUtc(a, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
        string c = await cache.StoreAsync("C", Jpeg, default);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(c));
        Assert.Equal(12, Directory.GetFiles(temp.Path).Sum(p => new FileInfo(p).Length));
    }

    [Fact]
    public async Task Orphan_temporary_is_never_visible_as_hit_and_is_reclaimed_before_write()
    {
        using var temp = new CacheDirectory();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "A.tmp"), [1, 2, 3]);
        var cache = new ArtworkCache(temp.Path, 6, Path.Combine(temp.Path, "steam"));
        Assert.Null(await cache.FindAsync("A", default));
        string path = await cache.StoreAsync("A", Jpeg, default);
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Equal(path, await cache.FindAsync("A", default));
    }

    [Fact]
    public async Task Cancelled_write_cannot_publish_partial_blob()
    {
        using var temp = new CacheDirectory();
        var cache = new ArtworkCache(temp.Path, 6, Path.Combine(temp.Path, "steam"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.StoreAsync("A", Jpeg, cts.Token));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task Corrupted_cache_is_reported_without_returning_a_path()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        var first = await fetcher.FetchAsync(Header);
        await File.WriteAllBytesAsync(first.Path!, [1, 2]);
        AssertFailure(await fetcher.FetchAsync(Header, offline: true), "cache-hash-mismatch");
        Assert.Equal(1, stub.Requests);
    }

    [Fact]
    public async Task Cancellation_is_reported_and_does_not_consume_gate()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        AssertFailure(await fetcher.FetchAsync(Header, cancellationToken: cts.Token), "cancelled");
        Assert.NotNull((await fetcher.FetchAsync(Header)).Path);
    }

    [Fact]
    public void Key_is_deterministic_and_separates_source_kind_app_and_expected_hash()
    {
        var source = new ArtworkSource(new Uri("http://127.0.0.1/a"));
        string key = ArtworkCache.SourceKey(Header, source);
        Assert.Equal(key, ArtworkCache.SourceKey(Header, source));
        Assert.NotEqual(key, ArtworkCache.SourceKey(Header with { AppId = 42 }, source));
        Assert.NotEqual(key, ArtworkCache.SourceKey(Header with { Kind = ArtworkKind.Portrait }, source));
        Assert.NotEqual(key, ArtworkCache.SourceKey(Header, source with { Location = new Uri("http://127.0.0.1/b") }));
        Assert.NotEqual(key, ArtworkCache.SourceKey(Header, source with { ExpectedSha256 = "AB" }));
    }

    [Fact]
    public async Task Recorded_metadata_corpus_is_read_without_modification()
    {
        using var temp = new CacheDirectory();
        string file = Serialization.RepositoryPaths.Combine("tests", "fixtures", "steam", "real", "userdata", "00000000", "config", "librarycache", "1238040.json");
        byte[] before = await File.ReadAllBytesAsync(file);
        using var fetcher = Fetcher(new Source(new Uri(file)), temp.Path);
        var result = await fetcher.FetchAsync(new(1238040, ArtworkKind.Achievements), offline: true);
        Assert.Empty(result.Issues);
        Assert.Equal(before, await File.ReadAllBytesAsync(result.Path!));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Cache_symlink_is_rejected_without_writing_through_it()
    {
        using var temp = new CacheDirectory();
        string target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(temp.Path, "link");
        Directory.CreateSymbolicLink(link, target);
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url), link);
        AssertFailure(await fetcher.FetchAsync(Header), "source-or-cache-io-failed");
        Assert.Empty(Directory.GetFiles(target));
        Assert.Equal(0, stub.Requests);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("A*B")]
    public async Task Cache_rejects_path_shaped_keys(string key)
    {
        using var temp = new CacheDirectory();
        var cache = new ArtworkCache(temp.Path, 6, Path.Combine(temp.Path, "steam"));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.StoreAsync(key, Jpeg, default));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.FindAsync(key, default));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task Cancellation_during_http_and_while_queued_reports_both_and_releases_gate()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg, block: true);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        using var running = new CancellationTokenSource();
        Task<ArtworkResult> first = fetcher.FetchAsync(Header, cancellationToken: running.Token);
        await stub.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var queued = new CancellationTokenSource();
        Task<ArtworkResult> second = fetcher.FetchAsync(Header, cancellationToken: queued.Token);
        queued.Cancel();
        AssertFailure(await second, "cancelled");
        running.Cancel();
        AssertFailure(await first, "cancelled");
        Assert.Equal(1, stub.Requests);
        AssertFailure(await fetcher.FetchAsync(Header, offline: true), "offline-cache-miss");
    }

    [Fact]
    public async Task Cancellation_during_rate_wait_does_not_send_request()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg, 404);
        using var cts = new CancellationTokenSource();
        var clock = new Clock { OnDelay = cts.Cancel };
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path, clock);
        AssertFailure(await fetcher.FetchAsync(Header), "http-404");
        AssertFailure(await fetcher.FetchAsync(Header, cancellationToken: cts.Token), "cancelled");
        Assert.Equal(1, stub.Requests);
    }

    [Theory]
    [InlineData(ArtworkKind.Logo, true)]
    [InlineData(ArtworkKind.Header, false)]
    public async Task Image_envelope_must_match_kind(ArtworkKind kind, bool valid)
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub([137, 80, 78, 71, 13, 10, 26, 10]);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        var result = await fetcher.FetchAsync(new(Header.AppId, kind));
        Assert.Equal(valid, result.Path != null);
        if (!valid) Assert.Equal("invalid-content", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public async Task Online_same_uri_changed_content_replaces_stale_snapshot_and_offline_reads_latest()
    {
        using var temp = new CacheDirectory();
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path);
        var first = await fetcher.FetchAsync(Header);
        byte[] changed = [0xff, 0xd8, 3, 4, 0xff, 0xd9];
        stub.Body = changed;
        var second = await fetcher.FetchAsync(Header);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(changed, await File.ReadAllBytesAsync(second.Path!));
        var offline = await fetcher.FetchAsync(Header, offline: true);
        Assert.Equal(second.Path, offline.Path);
        Assert.Equal(2, stub.Requests);
        Assert.Single(Directory.GetFiles(temp.Path, "*.blob"));
    }

    [Fact]
    public async Task Redirect_policy_is_enforced_by_fetcher_and_never_contacts_target()
    {
        using var temp = new CacheDirectory();
        await using var target = new HttpStub(Jpeg);
        await using var redirect = new HttpStub([], 302, extraHeaders: "Location: " + target.Url.AbsoluteUri + "\r\n");
        using var fetcher = Fetcher(new Source(redirect.Url), temp.Path);
        AssertFailure(await fetcher.FetchAsync(Header), "http-302");
        Assert.Equal(1, redirect.Requests);
        Assert.Equal(0, target.Requests);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("steam")]
    [InlineData("inside-steam")]
    [InlineData("steam-alias")]
    public async Task Unsafe_cache_scope_is_reported_before_network_or_writes(string scope)
    {
        using var temp = new CacheDirectory();
        string steam = Path.Combine(temp.Path, "steam");
        Directory.CreateDirectory(steam);
        string alias = Path.Combine(temp.Path, "steam-alias");
        Directory.CreateSymbolicLink(alias, steam);
        string cachePath = scope switch { "root" => "/", "steam" => steam, _ => Path.Combine(steam, "cache") };
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = new ArtworkFetcher(new Source(stub.Url), new(cachePath, 1024, scope == "steam-alias" ? alias : steam), new Clock());
        AssertFailure(await fetcher.FetchAsync(Header), "cache-directory-unsafe");
        Assert.Empty(Directory.GetFileSystemEntries(steam));
        Assert.Equal(0, stub.Requests);
    }

    [Theory]
    [InlineData("2147483647")]
    [InlineData("Fri, 31 Dec 9999 23:59:59 GMT")]
    public async Task Retry_after_is_capped_at_five_minutes(string retry)
    {
        using var temp = new CacheDirectory();
        var clock = new Clock();
        await using var stub = new HttpStub(Jpeg, 429, extraHeaders: "Retry-After: " + retry + "\r\n");
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path, clock);
        AssertFailure(await fetcher.FetchAsync(Header), "http-429-retry-after");
        AssertFailure(await fetcher.FetchAsync(Header), "http-429-retry-after");
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(clock.Delays));
    }

    [Fact]
    public async Task Rate_delay_releases_gate_so_offline_cache_hits_can_complete()
    {
        using var temp = new CacheDirectory();
        var clock = new Clock { HoldDelay = true };
        await using var stub = new HttpStub(Jpeg);
        using var fetcher = Fetcher(new Source(stub.Url), temp.Path, clock);
        var first = await fetcher.FetchAsync(Header);
        using var cts = new CancellationTokenSource();
        var delayed = fetcher.FetchAsync(Header, cancellationToken: cts.Token);
        await clock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var offline = await fetcher.FetchAsync(Header, offline: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.Path, offline.Path);
            Assert.Equal(1, stub.Requests);
        }
        finally { cts.Cancel(); }
        AssertFailure(await delayed, "cancelled");
    }

    private static void AssertFailure(ArtworkResult result, string reason)
    {
        Assert.Null(result.Path);
        Assert.Equal(new ArtworkIssue(Header.AppId, Header.Kind, reason), Assert.Single(result.Issues));
    }

    private static ArtworkFetcher Fetcher(Source source, string path, Clock? clock = null)
    {
        var actualClock = clock ?? new Clock();
        return new(source, new(path, 1024, Path.Combine(path, "steam")), actualClock);
    }

    private sealed class Source(Uri? uri, string? hash = null) : IArtworkSource
    {
        public ArtworkSource? Resolve(ArtworkIdentity identity) => uri == null ? null : new(uri, hash);
    }

    private sealed class Clock : IArtworkClock
    {
        public Action? OnDelay { get; init; }
        public bool HoldDelay { get; init; }
        public TaskCompletionSource DelayStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            DelayStarted.TrySetResult();
            if (HoldDelay) await Task.Delay(Timeout.Infinite, cancellationToken);
            UtcNow += delay;
        }
    }

    private sealed class CacheDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playnite-artwork-" + Guid.NewGuid());
        public CacheDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class HttpStub : IAsyncDisposable
    {
        private readonly Clock? clock;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private readonly bool block;
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Url { get; }
        public int Requests { get; private set; }
        public List<DateTimeOffset> Times { get; } = [];
        public byte[] Body { get; set; }

        public HttpStub(byte[] body, int status = 200, long? declaredLength = null, bool omitLength = false, string extraHeaders = "", Clock? clock = null, bool block = false)
        {
            this.block = block;
            Body = body;
            this.clock = clock;
            listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/explicit-source");
            serving = ServeAsync(body, status, declaredLength, omitLength, extraHeaders);
        }

        private async Task ServeAsync(byte[] body, int status, long? length, bool omitLength, string extra)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                    Requests++;
                    Times.Add(clock?.UtcNow ?? DateTimeOffset.MinValue);
                    Received.TrySetResult();
                    if (block) await Task.Delay(Timeout.Infinite, stop.Token);
                    byte[] actualBody = Body;
                    string header = $"HTTP/1.1 {status} Stub\r\nConnection: close\r\n" + (omitLength ? "" : $"Content-Length: {length ?? actualBody.Length}\r\n") + extra + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), stop.Token);
                    await stream.WriteAsync(actualBody, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving;
            stop.Dispose();
        }
    }
}

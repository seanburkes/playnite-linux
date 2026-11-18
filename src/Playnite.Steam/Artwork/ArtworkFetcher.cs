using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Playnite.Steam.Artwork;

public sealed class ArtworkFetcher : IDisposable
{
    private readonly IArtworkSource sources;
    private readonly HttpClient client;
    private readonly ArtworkCache cache;
    private readonly IArtworkClock clock;
    private readonly int maxBodyBytes;
    private readonly TimeSpan interval;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest = DateTimeOffset.MinValue;

    // Own the handler so redirect policy cannot be weakened by an injected client.
    public ArtworkFetcher(IArtworkSource sources, ArtworkCache cache,
        IArtworkClock clock, int maxBodyBytes = 8 * 1024 * 1024, TimeSpan? interval = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBodyBytes);
        this.sources = sources;
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
        this.cache = cache;
        this.clock = clock;
        this.maxBodyBytes = maxBodyBytes;
        this.interval = interval ?? TimeSpan.FromSeconds(1);
        if (this.interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public async Task<ArtworkResult> FetchAsync(ArtworkIdentity identity, bool offline = false, CancellationToken cancellationToken = default)
    {
        var issues = new List<ArtworkIssue>();
        ArtworkResult Fail(string reason)
        {
            issues.Add(new(identity.AppId, identity.Kind, reason));
            return new(null, issues.AsReadOnly());
        }
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ArtworkSource? source = sources.Resolve(identity);
            if (source == null) return Fail("source-unconfigured-or-title-excluded");
            if (source.ExpectedSha256 != null && string.IsNullOrWhiteSpace(source.ExpectedSha256)) return Fail("source-invalid");
            if (!source.Location.IsAbsoluteUri || (source.Location.Scheme != "http" && source.Location.Scheme != "https" && !source.Location.IsFile))
                return Fail("source-invalid");
            string key = ArtworkCache.SourceKey(identity, source);
            string? hit = await cache.FindAsync(key, cancellationToken).ConfigureAwait(false);
            // Offline HTTP hits are validated snapshots. Online and local sources are read anew:
            // URI, length and mtime alone cannot prove content has not changed.
            if (hit != null && offline && !source.Location.IsFile)
            {
                if (source.ExpectedSha256 != null)
                {
                    await using var cached = File.OpenRead(hit);
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(cached, cancellationToken).ConfigureAwait(false));
                    if (!hash.Equals(source.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) return Fail("source-hash-mismatch");
                }
                return new(hit, issues.AsReadOnly());
            }
            if (offline && !source.Location.IsFile) return Fail("offline-cache-miss");

            byte[] body;
            if (source.Location.IsFile)
            {
                await using var stream = File.OpenRead(source.Location.LocalPath);
                body = await ReadBoundedAsync(stream, stream.Length, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                while (nextRequest > clock.UtcNow)
                {
                    TimeSpan delay = nextRequest - clock.UtcNow;
                    gate.Release();
                    entered = false;
                    await clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                }
                cancellationToken.ThrowIfCancellationRequested();
                nextRequest = clock.UtcNow + interval;
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    DateTimeOffset maximum = clock.UtcNow + TimeSpan.FromMinutes(5);
                    TimeSpan requested = retry?.Delta ?? interval;
                    if (requested > TimeSpan.FromMinutes(5)) requested = TimeSpan.FromMinutes(5);
                    DateTimeOffset deadline = retry?.Date ?? (clock.UtcNow + requested);
                    if (deadline > maximum) deadline = maximum;
                    if (deadline > nextRequest) nextRequest = deadline;
                    return Fail("http-429-retry-after");
                }
                if (response.StatusCode != HttpStatusCode.OK) return Fail("http-" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                body = await ReadBoundedAsync(stream, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
            }
            if (!ValidContent(identity.Kind, body)) return Fail("invalid-content");
            if (source.ExpectedSha256 != null && !Convert.ToHexString(SHA256.HashData(body)).Equals(source.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return Fail("source-hash-mismatch");
            string path = await cache.StoreAsync(key, body, cancellationToken).ConfigureAwait(false);
            return new(path, issues.AsReadOnly());
        }
        catch (OperationCanceledException) { return Fail(cancellationToken.IsCancellationRequested ? "cancelled" : "http-timeout"); }
        catch (InvalidDataException ex) { return Fail(ex.Message); }
        catch (HttpIOException) { return Fail("http-transfer-failed-or-truncated"); }
        catch (HttpRequestException) { return Fail("http-transfer-failed-or-truncated"); }
        catch (UnauthorizedAccessException) { return Fail("source-or-cache-unwritable-or-unreadable"); }
        catch (IOException) { return Fail("source-or-cache-io-failed"); }
        finally { if (entered) gate.Release(); }
    }

    private async Task<byte[]> ReadBoundedAsync(Stream stream, long? length, CancellationToken cancellationToken)
    {
        long limit = Math.Min(maxBodyBytes, cache.MaxBytes);
        if (length > limit) throw new InvalidDataException("body-size-limit");
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - output.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("body-size-limit");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (length.HasValue && output.Length != length.Value) throw new InvalidDataException("body-truncated");
        if (output.Length == 0) throw new InvalidDataException("body-empty");
        return output.ToArray();
    }

    private static bool ValidContent(ArtworkKind kind, byte[] body)
    {
        if (kind == ArtworkKind.Achievements)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.ValueKind == JsonValueKind.Array;
            }
            catch (JsonException) { return false; }
        }
        if (kind == ArtworkKind.Logo) return body.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        return body.Length >= 4 && body[0] == 0xff && body[1] == 0xd8 && body[^2] == 0xff && body[^1] == 0xd9;
    }

    public void Dispose()
    {
        client.Dispose();
        gate.Dispose();
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Playnite.Core.Metadata;

/// <summary>Bounds and freshness for a metadata cache directory.</summary>
public sealed record MetadataCacheOptions
{
    /// <summary>Gets the maximum number of entries kept. Defaults to 256.</summary>
    public int MaxEntries { get; init; } = 256;

    /// <summary>Gets the maximum total bytes kept. Defaults to 8 MiB.</summary>
    public long MaxBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Gets how long an entry is fresh. Defaults to seven days.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);

    internal void Validate()
    {
        if (MaxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), MaxEntries, "At least one cache entry is required.");
        }

        if (MaxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "The cache byte bound must be positive.");
        }

        if (MaxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAge), MaxAge, "The cache age cannot be negative.");
        }
    }
}

/// <summary>The outcome of reading one cache entry.</summary>
internal sealed record MetadataCacheRead(
    MetadataDocument? Document,
    DateTimeOffset? FetchedAt,
    bool IsStale,
    string? Problem,
    string? Warning);

/// <summary>The outcome of storing one cache entry.</summary>
internal sealed record MetadataCacheWrite(bool Stored, string? Problem);

/// <summary>
/// A bounded, per-provider metadata cache. Entries are written to a sibling temporary file and
/// renamed into place, so a reader never observes a partially written entry. The cache is a
/// private directory owned by one process; the service serializes every operation.
/// </summary>
public sealed class MetadataCache : IDisposable
{
    private const int SchemaVersion = 1;
    private const string EntrySuffix = ".json";
    private const string TemporarySuffix = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string directory;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    /// <summary>Creates a cache rooted at an absolute, non-root directory.</summary>
    public MetadataCache(string directory, MetadataCacheOptions? options = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A cache directory is required.", nameof(directory));
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("An absolute cache directory is required.", nameof(directory));
        }

        string full = Path.GetFullPath(directory);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal))
        {
            throw new ArgumentException("The filesystem root cannot be used as a cache directory.", nameof(directory));
        }

        this.directory = Path.TrimEndingDirectorySeparator(full);
        Options = options ?? new MetadataCacheOptions();
        Options.Validate();
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the absolute cache directory.</summary>
    public string DirectoryPath => directory;

    /// <summary>Gets the bounds and freshness settings.</summary>
    public MetadataCacheOptions Options { get; }

    internal async Task<MetadataCacheRead> ReadAsync(
        string provider,
        string appId,
        CancellationToken cancellationToken)
    {
        string path = PathFor(provider, appId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!File.Exists(path))
            {
                return new MetadataCacheRead(null, null, false, null, null);
            }

            CacheDocument? entry;
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > Options.MaxBytes)
                {
                    TryDelete(path);
                    return new MetadataCacheRead(null, null, false, "cache-corrupt", null);
                }

                await using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    entry = await JsonSerializer.DeserializeAsync<CacheDocument>(
                        stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                TryDelete(path);
                return new MetadataCacheRead(null, null, false, "cache-corrupt", null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new MetadataCacheRead(null, null, false, "cache-read-failed", null);
            }

            MetadataDocument? document = ToDocument(entry, provider, appId, out string? problem);
            if (document is null || entry is null)
            {
                TryDelete(path);
                return new MetadataCacheRead(null, null, false, problem ?? "cache-corrupt", null);
            }

            bool stale = clock.GetUtcNow() - entry.FetchedAt >= Options.MaxAge;
            return new MetadataCacheRead(
                document,
                entry.FetchedAt,
                stale,
                null,
                Touch(path));
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<MetadataCacheWrite> StoreAsync(
        string provider,
        string appId,
        MetadataDocument document,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (fetchedAt == default)
        {
            throw new ArgumentException("A fetch timestamp is required.", nameof(fetchedAt));
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            ToCacheDocument(provider, appId, document, fetchedAt),
            JsonOptions);
        if (bytes.LongLength > Options.MaxBytes)
        {
            return new MetadataCacheWrite(false, "cache-entry-too-large");
        }

        string path = PathFor(provider, appId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            RemoveTemporaryFiles();

            var entries = Directory.GetFiles(directory)
                .Where(file => file.EndsWith(EntrySuffix, StringComparison.Ordinal))
                .Select(file => new FileInfo(file))
                .ToArray();
            long used = 0;
            int count = 0;
            foreach (FileInfo entry in entries)
            {
                used = checked(used + entry.Length);
                count++;
            }

            foreach (FileInfo existing in entries)
            {
                if (SamePath(existing.FullName, path))
                {
                    used -= existing.Length;
                    count--;
                }
            }

            var victims = entries
                .Where(entry => !SamePath(entry.FullName, path))
                .OrderBy(entry => entry.LastWriteTimeUtc)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();
            int victimIndex = 0;
            while ((count >= Options.MaxEntries || used + bytes.LongLength > Options.MaxBytes)
                && victimIndex < victims.Count)
            {
                FileInfo victim = victims[victimIndex++];
                File.Delete(victim.FullName);
                used -= victim.Length;
                count--;
            }

            if (count >= Options.MaxEntries || used + bytes.LongLength > Options.MaxBytes)
            {
                return new MetadataCacheWrite(false, "cache-size-limit");
            }

            string temporary = Path.Combine(directory, "." + Path.GetRandomFileName() + TemporarySuffix);
            try
            {
                await using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                    FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.SetLastWriteTimeUtc(temporary, fetchedAt.UtcDateTime);
                File.Move(temporary, path, overwrite: true);
                return new MetadataCacheWrite(true, null);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    TryDelete(temporary);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentOutOfRangeException or OverflowException)
        {
            return new MetadataCacheWrite(false, "cache-write-failed");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }

    private string PathFor(string provider, string appId)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("A provider identity is required.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("A provider app id is required.", nameof(appId));
        }

        string key = provider + "\n" + appId;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(directory, hash + EntrySuffix);
    }

    private static CacheDocument ToCacheDocument(
        string provider,
        string appId,
        MetadataDocument document,
        DateTimeOffset fetchedAt)
    {
        return new CacheDocument
        {
            Schema = SchemaVersion,
            AppId = appId,
            Provider = provider,
            FetchedAt = fetchedAt,
            Description = document.Description,
            Links = document.Links.Select(link => new CacheLink
            {
                Name = link.Name,
                Location = link.Location,
            }).ToList(),
            Artwork = document.Artwork.Select(artwork => new CacheArtwork
            {
                Kind = artwork.Kind.ToString(),
                Location = artwork.Location,
            }).ToList(),
        };
    }

    private static MetadataDocument? ToDocument(
        CacheDocument? entry,
        string provider,
        string appId,
        out string? problem)
    {
        problem = "cache-corrupt";
        if (entry is null
            || entry.Schema != SchemaVersion
            || !string.Equals(entry.AppId, appId, StringComparison.Ordinal)
            || !string.Equals(entry.Provider, provider, StringComparison.Ordinal)
            || entry.FetchedAt == default
            || entry.Links is null
            || entry.Artwork is null)
        {
            return null;
        }

        var links = new MetadataLink[entry.Links.Count];
        for (int index = 0; index < entry.Links.Count; index++)
        {
            CacheLink? link = entry.Links[index];
            if (link is null
                || string.IsNullOrWhiteSpace(link.Name)
                || string.IsNullOrWhiteSpace(link.Location))
            {
                return null;
            }

            links[index] = new MetadataLink(link.Name, link.Location);
        }

        var artwork = new MetadataArtwork[entry.Artwork.Count];
        for (int index = 0; index < entry.Artwork.Count; index++)
        {
            CacheArtwork? item = entry.Artwork[index];
            if (item is null
                || item.Kind is null
                || !Enum.TryParse(item.Kind, ignoreCase: false, out MetadataArtworkKind kind)
                || !Enum.IsDefined(kind)
                || !string.Equals(Enum.GetName(kind), item.Kind, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.Location))
            {
                return null;
            }

            artwork[index] = new MetadataArtwork(kind, item.Location);
        }

        problem = null;
        return new MetadataDocument(entry.Description, links, artwork);
    }

    private string? Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentOutOfRangeException)
        {
            return "cache-touch-failed";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
    }

    private void RemoveTemporaryFiles()
    {
        foreach (string file in Directory.GetFiles(directory)
            .Where(file => file.EndsWith(TemporarySuffix, StringComparison.Ordinal)))
        {
            TryDelete(file);
        }
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class CacheDocument
    {
        public int Schema { get; set; }

        public string? AppId { get; set; }

        public string? Provider { get; set; }

        public DateTimeOffset FetchedAt { get; set; }

        public string? Description { get; set; }

        public List<CacheLink>? Links { get; set; }

        public List<CacheArtwork>? Artwork { get; set; }
    }

    private sealed class CacheLink
    {
        public string? Name { get; set; }

        public string? Location { get; set; }
    }

    private sealed class CacheArtwork
    {
        public string? Kind { get; set; }

        public string? Location { get; set; }
    }
}

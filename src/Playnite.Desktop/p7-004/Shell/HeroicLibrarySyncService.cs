using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Platform;
using Playnite.Platform.Linux;

namespace Playnite.Desktop.Shell;

internal sealed record HeroicDiscoveredGame(
    Guid PluginId,
    string GameId,
    string Name,
    bool IsInstalled,
    string? InstallDirectory,
    string? Version,
    string? Description,
    string? CoverImage);

internal sealed record HeroicSyncResult(
    int Added,
    int Updated,
    int Uninstalled,
    int Unchanged,
    int Skipped,
    IReadOnlyList<string> Issues,
    bool Reconciled,
    bool RootFound)
{
    public int LinkedTitlesUpdated { get; init; }
}

internal interface ILibraryHeroicSync
{
    Task<HeroicSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken);
}

internal static class HeroicSyncIssues
{
    public const string RootMissing = "heroic-root-missing";
    public const string EpicParseError = "heroic-epic-parse-error";
    public const string GogParseError = "heroic-gog-parse-error";
    public const string GogMetadataFallbackError = "heroic-gog-metadata-fallback-error";
    public const string AmazonParseError = "heroic-amazon-parse-error";
}

/// <summary>
/// Bounded read-only Heroic library discovery and idempotent SQLite reconciliation off the UI thread.
/// Public GOG metadata is queried only for cache misses; no credential or token files are read.
/// </summary>
internal sealed class HeroicLibrarySyncRunner(
    IEnvironment? environment = null,
    TimeProvider? timeProvider = null,
    HttpClient? gogMetadataClient = null) : ILibraryHeroicSync
{
    internal const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxGogProductBytes = 2 * 1024 * 1024;
    private const int MaxGogArtworkBytes = 8 * 1024 * 1024;
    private const int MaxGogFallbackProductsPerSync = 24;
    private static readonly HttpClient SharedGogMetadataClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };
    private static readonly Guid HeroicNamespaceGuid = new("d2b5748e-5d73-4f88-a05c-391f8e76c041");
    private static readonly JsonDocumentOptions SafeJsonOptions = new()
    {
        MaxDepth = 32,
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IEnvironment environment = environment ?? new LinuxEnvironment();
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly HttpClient gogMetadataClient = gogMetadataClient ?? SharedGogMetadataClient;
    private readonly ConcurrentDictionary<string, DateTimeOffset> gogFallbackRetryAfter = new(StringComparer.Ordinal);
    private readonly LinuxBoundedFileReader boundedReader = new();

    public Task<HeroicSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(async () =>
        {
            var roots = DiscoverConfigRoots(environment);
            if (roots.Count == 0)
            {
                return new HeroicSyncResult(0, 0, 0, 0, 0, [HeroicSyncIssues.RootMissing], Reconciled: false, RootFound: false);
            }

            var discovery = DiscoverFromRoots(roots, boundedReader);
            if (discovery.Games.Count == 0 && discovery.AuthoritativeInstalledStores.Count == 0 && discovery.Issues.Count > 0)
            {
                return new HeroicSyncResult(0, 0, 0, 0, discovery.Skipped, discovery.Issues, Reconciled: false, RootFound: true);
            }

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var database = await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
            discovery = await EnrichGogMetadataAsync(database, databasePath, discovery, cancellationToken).ConfigureAwait(false);
            return await ReconcileAsync(database, discovery, timeProvider.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public static string FormatSummary(HeroicSyncResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.RootFound)
        {
            return "Heroic Games Launcher library not found on this machine.";
        }

        if (!result.Reconciled && result.Issues.Count > 0)
        {
            var reasons = string.Join(", ", result.Issues.Distinct(StringComparer.Ordinal));
            return $"Heroic sync could not reconcile library ({reasons}).";
        }

        var summary = $"Heroic sync complete: {result.Added} added, {result.Updated} updated, {result.Unchanged} unchanged";
        if (result.LinkedTitlesUpdated > 0)
        {
            summary += $", {result.LinkedTitlesUpdated} linked title(s) refreshed";
        }

        if (result.Uninstalled > 0)
        {
            summary += $", {result.Uninstalled} uninstalled";
        }

        if (result.Skipped > 0)
        {
            summary += $", {result.Skipped} skipped";
        }

        if (result.Issues.Count > 0)
        {
            summary += $", {result.Issues.Count} issue(s)";
        }

        return summary + ".";
    }

    internal static IReadOnlyList<string> DiscoverConfigRoots(IEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var candidates = new List<string>();
        var xdgConfig = environment.GetVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig) && Path.IsPathFullyQualified(xdgConfig))
        {
            candidates.Add(Path.Combine(xdgConfig, "heroic"));
        }

        if (!string.IsNullOrWhiteSpace(environment.Home) && Path.IsPathFullyQualified(environment.Home))
        {
            candidates.Add(Path.Combine(environment.Home, ".config", "heroic"));
            candidates.Add(Path.Combine(environment.Home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"));
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var canonical = ResolveRealDirectory(candidate);
            if (!string.IsNullOrWhiteSpace(canonical) && Directory.Exists(canonical) && seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }

        return result;
    }

    private sealed record DiscoveryBatch(
        IReadOnlyDictionary<(Guid PluginId, string GameId), HeroicDiscoveredGame> Games,
        HashSet<Guid> AuthoritativeInstalledStores,
        HashSet<(Guid PluginId, string GameId)> SkippedInstalledKeys,
        int Skipped,
        IReadOnlyList<string> Issues);

    private sealed record GogProductMetadata(string? Title, string? ArtworkUri);

    private sealed record GogEnrichmentResult(HeroicDiscoveredGame Game, bool HadError);

    private static DiscoveryBatch DiscoverFromRoots(
        IReadOnlyList<string> roots,
        LinuxBoundedFileReader reader)
    {
        var games = new Dictionary<(Guid PluginId, string GameId), HeroicDiscoveredGame>();
        var authoritativeStores = new HashSet<Guid>();
        var failedInstalledStores = new HashSet<Guid>();
        var skippedInstalledKeys = new HashSet<(Guid PluginId, string GameId)>();
        var issues = new List<string>();
        var skipped = 0;

        foreach (var root in roots)
        {
            DiscoverEpic(root, reader, games, authoritativeStores, failedInstalledStores, skippedInstalledKeys, ref skipped, issues);
            DiscoverGog(root, reader, games, authoritativeStores, failedInstalledStores, skippedInstalledKeys, ref skipped, issues);
            DiscoverAmazon(root, reader, games, authoritativeStores, failedInstalledStores, skippedInstalledKeys, ref skipped, issues);
        }

        authoritativeStores.ExceptWith(failedInstalledStores);
        return new DiscoveryBatch(games, authoritativeStores, skippedInstalledKeys, skipped, issues);
    }

    private async Task<DiscoveryBatch> EnrichGogMetadataAsync(
        LibraryDatabase database,
        string databasePath,
        DiscoveryBatch discovery,
        CancellationToken cancellationToken)
    {
        var gogGames = discovery.Games.Values
            .Where(game => game.PluginId == StoreCatalog.GogPluginId)
            .ToArray();
        if (gogGames.Length == 0)
        {
            return discovery;
        }

        var existingByKey = await database.ReadAsync(async session =>
        {
            var existing = new Dictionary<string, (string? Cover, string? ProviderTitle)>(StringComparer.Ordinal);
            foreach (var stored in await session.Games.GetAllAsync().ConfigureAwait(false))
            {
                var game = stored.Value;
                if (game.PluginId == StoreCatalog.GogPluginId && !string.IsNullOrWhiteSpace(game.GameId)
                    && !existing.ContainsKey(game.GameId))
                {
                    var title = await session.Titles.GetAsync(game.Id).ConfigureAwait(false);
                    existing.Add(game.GameId, (game.CoverImage, title?.ProviderId == game.PluginId.ToString("D")
                        ? title.ProviderTitle : null));
                }
            }
            return existing;
        }, cancellationToken).ConfigureAwait(false);
        var games = new Dictionary<(Guid, string), HeroicDiscoveredGame>(discovery.Games);
        var candidates = new List<HeroicDiscoveredGame>();

        foreach (var game in gogGames)
        {
            existingByKey.TryGetValue(game.GameId, out var stored);
            var cachedTitle = !string.IsNullOrWhiteSpace(stored.ProviderTitle)
                && !HasFallbackName(game with { Name = stored.ProviderTitle });
            if ((string.IsNullOrWhiteSpace(game.CoverImage) && string.IsNullOrWhiteSpace(stored.Cover))
                || (HasFallbackName(game) && !cachedTitle))
            {
                candidates.Add(game with { CoverImage = game.CoverImage ?? stored.Cover });
            }
        }

        var candidateLimitExceeded = candidates.Count > MaxGogFallbackProductsPerSync;
        if (candidateLimitExceeded)
        {
            candidates = candidates.Take(MaxGogFallbackProductsPerSync).ToList();
        }

        using var concurrency = new SemaphoreSlim(4, 4);
        var results = await Task.WhenAll(candidates.Select(async game =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FetchGogFallbackAsync(game, databasePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);

        var hadError = candidateLimitExceeded;
        foreach (var result in results)
        {
            var game = result.Game;
            if (existingByKey.TryGetValue(game.GameId, out var stored) && game.CoverImage == stored.Cover)
            {
                game = game with { CoverImage = games[(game.PluginId, game.GameId)].CoverImage };
            }
            games[(game.PluginId, game.GameId)] = game;
            hadError |= result.HadError;
        }

        var issues = discovery.Issues;
        if (hadError && !issues.Contains(HeroicSyncIssues.GogMetadataFallbackError, StringComparer.Ordinal))
        {
            issues = [.. issues, HeroicSyncIssues.GogMetadataFallbackError];
        }

        return discovery with { Games = games, Issues = issues };
    }

    private async Task<GogEnrichmentResult> FetchGogFallbackAsync(
        HeroicDiscoveredGame game,
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (gogFallbackRetryAfter.TryGetValue(game.GameId, out var retryAfter)
            && retryAfter > timeProvider.GetUtcNow())
        {
            return new GogEnrichmentResult(game, HadError: true);
        }

        try
        {
            var metadata = await FetchGogProductMetadataAsync(game.GameId, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                gogFallbackRetryAfter[game.GameId] = timeProvider.GetUtcNow().AddHours(24);
                return new GogEnrichmentResult(game, HadError: false);
            }

            var name = HasFallbackName(game) && !string.IsNullOrWhiteSpace(metadata.Title)
                ? metadata.Title
                : game.Name;
            var cover = game.CoverImage;
            if (string.IsNullOrWhiteSpace(cover) && !string.IsNullOrWhiteSpace(metadata.ArtworkUri))
            {
                try
                {
                    cover = await ResolveOrDownloadGogArtworkAsync(
                        databasePath,
                        game.GameId,
                        metadata.ArtworkUri,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException
                    or UnauthorizedAccessException or TaskCanceledException)
                {
                    gogFallbackRetryAfter[game.GameId] = timeProvider.GetUtcNow().AddMinutes(15);
                    return new GogEnrichmentResult(game with { Name = name }, HadError: true);
                }
            }

            if (string.IsNullOrWhiteSpace(cover))
            {
                gogFallbackRetryAfter[game.GameId] = timeProvider.GetUtcNow().AddHours(24);
            }
            else
            {
                gogFallbackRetryAfter.TryRemove(game.GameId, out _);
            }

            return new GogEnrichmentResult(game with { Name = name, CoverImage = cover }, HadError: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            gogFallbackRetryAfter[game.GameId] = timeProvider.GetUtcNow().AddMinutes(15);
            return new GogEnrichmentResult(game, HadError: true);
        }
    }

    private async Task<GogProductMetadata?> FetchGogProductMetadataAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"https://api.gog.com/products/{Uri.EscapeDataString(gameId)}?locale=en-US");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await gogMetadataClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
        }

        var jsonBytes = await ReadBoundedResponseAsync(response, MaxGogProductBytes, cancellationToken).ConfigureAwait(false);
        if (jsonBytes is null)
        {
            throw new IOException("GOG product metadata exceeded its size limit.");
        }

        using var document = JsonDocument.Parse(jsonBytes, SafeJsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = GetString(root, "title");
        if (root.TryGetProperty("id", out var productId)
            && !string.Equals(productId.ToString(), gameId, StringComparison.Ordinal))
        {
            return null;
        }

        var artwork = GetString(root, "coverVertical")
            ?? GetString(root, "coverHorizontal")
            ?? GetString(root, "image");
        if (artwork is null && root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
        {
            artwork = GetString(images, "background") ?? GetString(images, "logo2x") ?? GetString(images, "logo");
        }

        if (artwork?.StartsWith("//", StringComparison.Ordinal) == true)
        {
            artwork = $"https:{artwork}";
        }

        if (artwork is not null && !IsAllowedGogArtworkUri(artwork, out _))
        {
            artwork = null;
        }

        return title is null && artwork is null ? null : new GogProductMetadata(title, artwork);
    }

    private async Task<string?> ResolveOrDownloadGogArtworkAsync(
        string databasePath,
        string gameId,
        string artworkUri,
        CancellationToken cancellationToken)
    {
        foreach (var root in DiscoverConfigRoots(environment))
        {
            if (ResolveOptionalSafeCover(root, artworkUri) is { } cached)
            {
                return cached;
            }
        }

        if (!IsAllowedGogArtworkUri(artworkUri, out var uri))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await gogMetadataClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var extension = mediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            _ => null,
        };
        if (extension is null)
        {
            return null;
        }

        var bytes = await ReadBoundedResponseAsync(response, MaxGogArtworkBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null || !HasImageSignature(bytes, extension))
        {
            return null;
        }

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var databaseDirectory = ResolveRealDirectory(Path.GetDirectoryName(fullDatabasePath)!);
        var filesDirectory = CreateContainedDirectory(databaseDirectory, "files");
        if (filesDirectory is null)
        {
            return null;
        }

        var artworkDirectory = CreateContainedDirectory(filesDirectory, "gog-artwork");
        if (artworkDirectory is null)
        {
            return null;
        }

        var targetPath = Path.Combine(artworkDirectory, $"{gameId}{extension}");
        var tempPath = Path.Combine(artworkDirectory, $"{gameId}.{Path.GetRandomFileName()}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<byte[]?> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    private static bool IsAllowedGogArtworkUri(string rawUri, out Uri uri)
    {
        if (Uri.TryCreate(rawUri, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.IsDefaultPort
            && parsed.UserInfo.Length == 0
            && IsGogImagesHost(parsed.IdnHost))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsGogImagesHost(string host)
    {
        const string suffix = ".gog-statics.com";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var shard = host[..^suffix.Length];
        return string.Equals(shard, "images", StringComparison.OrdinalIgnoreCase)
            || shard.StartsWith("images-", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(shard.AsSpan("images-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool HasFallbackName(HeroicDiscoveredGame game) =>
        string.Equals(game.Name, game.GameId, StringComparison.Ordinal)
        || string.Equals(game.Name, GetInstallDirectoryName(game.InstallDirectory), StringComparison.Ordinal);

    private static bool HasImageSignature(byte[] bytes, string extension) => extension switch
    {
        ".jpg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        _ => false,
    };

    private static string? CreateContainedDirectory(string parentDirectory, string name)
    {
        var candidate = Path.Combine(parentDirectory, name);
        var directory = new DirectoryInfo(candidate);
        if (directory.LinkTarget is not null)
        {
            return null;
        }

        if (!directory.Exists)
        {
            Directory.CreateDirectory(candidate);
        }

        directory.Refresh();
        if (directory.LinkTarget is not null || !directory.Exists)
        {
            return null;
        }

        var resolved = ResolveRealDirectory(candidate);
        return IsWithinDirectory(resolved, parentDirectory) ? resolved : null;
    }

    private static void DiscoverEpic(
        string root,
        LinuxBoundedFileReader reader,
        Dictionary<(Guid, string), HeroicDiscoveredGame> games,
        HashSet<Guid> authoritativeStores,
        HashSet<Guid> failedInstalledStores,
        HashSet<(Guid, string)> skippedInstalledKeys,
        ref int skipped,
        List<string> issues)
    {
        var installedOk = false;
        var installedDoc = TryReadContainedJson(
            root,
            Path.Combine(root, "legendaryConfig", "legendary", "installed.json"),
            reader,
            HeroicSyncIssues.EpicParseError,
            issues,
            out var installedExists);
        if (installedDoc is not null)
        {
            using (installedDoc)
            {
                if (installedDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    installedOk = true;
                    foreach (var prop in installedDoc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        var appName = GetString(prop.Value, "app_name") ?? prop.Name;
                        if (IsDlc(prop.Value) || !IsSafeGameId(StoreCatalog.EpicPluginId, appName))
                        {
                            if (!string.IsNullOrWhiteSpace(appName))
                            {
                                skippedInstalledKeys.Add((StoreCatalog.EpicPluginId, appName.Trim()));
                            }

                            skipped++;
                            continue;
                        }

                        var title = GetString(prop.Value, "title") ?? appName;
                        var installPath = ResolveOptionalSafeDir(GetString(prop.Value, "install_path"));
                        var version = GetString(prop.Value, "version");
                        MergeGame(games, new HeroicDiscoveredGame(
                            StoreCatalog.EpicPluginId,
                            appName,
                            title,
                            IsInstalled: true,
                            installPath,
                            version,
                            Description: null,
                            CoverImage: null));
                    }
                }
                else
                {
                    issues.Add(HeroicSyncIssues.EpicParseError);
                }
            }
        }

        if (installedExists && !installedOk)
        {
            failedInstalledStores.Add(StoreCatalog.EpicPluginId);
        }
        else if (installedOk)
        {
            authoritativeStores.Add(StoreCatalog.EpicPluginId);
        }

        var libDoc = TryReadContainedJson(
            root,
            Path.Combine(root, "store_cache", "legendary_library.json"),
            reader,
            HeroicSyncIssues.EpicParseError,
            issues,
            out _);
        if (libDoc is not null)
        {
            using (libDoc)
            {
                var items = ExtractArray(libDoc.RootElement, "library");
                if (items is { } array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        var appName = GetString(item, "app_name");
                        if (IsDlc(item) || !IsSafeGameId(StoreCatalog.EpicPluginId, appName))
                        {
                            skipped++;
                            continue;
                        }

                        var title = GetString(item, "title") ?? appName!;
                        string? description = null;
                        if (item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                        {
                            description = GetString(meta, "description") ?? GetString(meta, "longDescription");
                        }

                        var cover = ResolveOptionalSafeCover(root, GetString(item, "art_cover"))
                            ?? ResolveOptionalSafeCover(root, GetString(item, "art_square"));
                        MergeGame(games, new HeroicDiscoveredGame(
                            StoreCatalog.EpicPluginId,
                            appName!,
                            title,
                            IsInstalled: false,
                            InstallDirectory: null,
                            Version: null,
                            Description: description,
                            CoverImage: cover));
                    }
                }
                else
                {
                    issues.Add(HeroicSyncIssues.EpicParseError);
                }
            }
        }
    }

    private static void DiscoverGog(
        string root,
        LinuxBoundedFileReader reader,
        Dictionary<(Guid, string), HeroicDiscoveredGame> games,
        HashSet<Guid> authoritativeStores,
        HashSet<Guid> failedInstalledStores,
        HashSet<(Guid, string)> skippedInstalledKeys,
        ref int skipped,
        List<string> issues)
    {
        var installedOk = false;
        var installedDoc = TryReadContainedJson(
            root,
            Path.Combine(root, "gog_store", "installed.json"),
            reader,
            HeroicSyncIssues.GogParseError,
            issues,
            out var installedExists);
        if (installedDoc is not null)
        {
            using (installedDoc)
            {
                var items = ExtractArray(installedDoc.RootElement, "installed");
                if (items is { } array)
                {
                    installedOk = true;
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        var appName = GetString(item, "appName") ?? GetString(item, "app_name");
                        if (IsDlc(item) || !IsSafeGameId(StoreCatalog.GogPluginId, appName))
                        {
                            if (!string.IsNullOrWhiteSpace(appName))
                            {
                                skippedInstalledKeys.Add((StoreCatalog.GogPluginId, appName.Trim()));
                            }

                            skipped++;
                            continue;
                        }

                        var installPath = ResolveOptionalSafeDir(GetString(item, "install_path"));
                        var title = GetString(item, "title") ?? GetInstallDirectoryName(installPath) ?? appName!;
                        var version = GetString(item, "version");
                        MergeGame(games, new HeroicDiscoveredGame(
                            StoreCatalog.GogPluginId,
                            appName!,
                            title,
                            IsInstalled: true,
                            installPath,
                            version,
                            Description: null,
                            CoverImage: null));
                    }
                }
                else
                {
                    issues.Add(HeroicSyncIssues.GogParseError);
                }
            }
        }

        if (installedExists && !installedOk)
        {
            failedInstalledStores.Add(StoreCatalog.GogPluginId);
        }
        else if (installedOk)
        {
            authoritativeStores.Add(StoreCatalog.GogPluginId);
        }

        foreach (var libraryPath in new[]
        {
            Path.Combine(root, "store_cache", "gog_library.json"),
            Path.Combine(root, "gog_store", "library.json"),
        })
        {
            var libDoc = TryReadContainedJson(
                root,
                libraryPath,
                reader,
                HeroicSyncIssues.GogParseError,
                issues,
                out _);
            if (libDoc is null)
            {
                continue;
            }

            using (libDoc)
            {
                var items = ExtractArray(libDoc.RootElement, "games");
                if (items is { } array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        var appName = GetString(item, "app_name") ?? GetString(item, "appName");
                        if (IsDlc(item) || !IsSafeGameId(StoreCatalog.GogPluginId, appName))
                        {
                            skipped++;
                            continue;
                        }

                        var installPath = games.TryGetValue((StoreCatalog.GogPluginId, appName!), out var installedGame)
                            ? installedGame.InstallDirectory
                            : null;
                        var title = GetString(item, "title") ?? GetInstallDirectoryName(installPath) ?? appName!;
                        string? description = null;
                        if (item.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object
                            && extra.TryGetProperty("about", out var about) && about.ValueKind == JsonValueKind.Object)
                        {
                            description = GetString(about, "description") ?? GetString(about, "shortDescription");
                        }

                        var cover = ResolveOptionalSafeCover(root, GetString(item, "art_cover"))
                            ?? ResolveOptionalSafeCover(root, GetString(item, "art_square"));
                        MergeGame(games, new HeroicDiscoveredGame(
                            StoreCatalog.GogPluginId,
                            appName!,
                            title,
                            IsInstalled: false,
                            InstallDirectory: null,
                            Version: null,
                            Description: description,
                            CoverImage: cover));
                    }
                }
                else
                {
                    issues.Add(HeroicSyncIssues.GogParseError);
                }
            }
        }
    }

    private static void DiscoverAmazon(
        string root,
        LinuxBoundedFileReader reader,
        Dictionary<(Guid, string), HeroicDiscoveredGame> games,
        HashSet<Guid> authoritativeStores,
        HashSet<Guid> failedInstalledStores,
        HashSet<(Guid, string)> skippedInstalledKeys,
        ref int skipped,
        List<string> issues)
    {
        var installedOk = false;
        var installedDoc = TryReadContainedJson(
            root,
            Path.Combine(root, "nile_config", "nile", "installed.json"),
            reader,
            HeroicSyncIssues.AmazonParseError,
            issues,
            out var installedExists);
        if (installedDoc is not null)
        {
            using (installedDoc)
            {
                var items = ExtractArray(installedDoc.RootElement, "installed");
                if (items is { } array)
                {
                    installedOk = true;
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        var appId = GetString(item, "id") ?? GetString(item, "appName");
                        if (IsDlc(item) || !IsSafeGameId(StoreCatalog.AmazonPluginId, appId))
                        {
                            if (!string.IsNullOrWhiteSpace(appId))
                            {
                                skippedInstalledKeys.Add((StoreCatalog.AmazonPluginId, appId.Trim()));
                            }

                            skipped++;
                            continue;
                        }

                        var title = GetString(item, "title") ?? appId!;
                        var installPath = ResolveOptionalSafeDir(GetString(item, "path") ?? GetString(item, "install_path"));
                        var version = GetString(item, "version");
                        MergeGame(games, new HeroicDiscoveredGame(
                            StoreCatalog.AmazonPluginId,
                            appId!,
                            title,
                            IsInstalled: true,
                            installPath,
                            version,
                            Description: null,
                            CoverImage: null));
                    }
                }
                else
                {
                    issues.Add(HeroicSyncIssues.AmazonParseError);
                }
            }
        }

        if (installedExists && !installedOk)
        {
            failedInstalledStores.Add(StoreCatalog.AmazonPluginId);
        }
        else if (installedOk)
        {
            authoritativeStores.Add(StoreCatalog.AmazonPluginId);
        }

        var libDoc = TryReadContainedJson(
            root,
            Path.Combine(root, "nile_config", "nile", "library.json"),
            reader,
            HeroicSyncIssues.AmazonParseError,
            issues,
            out _);
        if (libDoc is not null)
        {
            using (libDoc)
            {
                var items = ExtractArray(libDoc.RootElement, "library")
                    ?? ExtractArray(libDoc.RootElement, "games");
                if (items is { } array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        JsonElement? product = item.TryGetProperty("product", out var prod) && prod.ValueKind == JsonValueKind.Object
                            ? prod
                            : null;
                        var appId = GetString(item, "id")
                            ?? (product.HasValue ? GetString(product.Value, "id") : null);
                        if (IsDlc(item) || !IsSafeGameId(StoreCatalog.AmazonPluginId, appId))
                        {
                            skipped++;
                            continue;
                        }

                        var title = (product.HasValue ? GetString(product.Value, "title") : null)
                            ?? GetString(item, "title")
                            ?? appId!;
                        string? description = null;
                        if (product.HasValue
                            && product.Value.TryGetProperty("productDetail", out var pd) && pd.ValueKind == JsonValueKind.Object
                            && pd.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object)
                        {
                            description = GetString(det, "shortDescription");
                        }

                        MergeGame(games, new HeroicDiscoveredGame(
                            StoreCatalog.AmazonPluginId,
                            appId!,
                            title,
                            IsInstalled: false,
                            InstallDirectory: null,
                            Version: null,
                            Description: description,
                            CoverImage: null));
                    }
                }
                else
                {
                    issues.Add(HeroicSyncIssues.AmazonParseError);
                }
            }
        }
    }

    private static async Task<HeroicSyncResult> ReconcileAsync(
        LibraryDatabase database,
        DiscoveryBatch discovery,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var uninstalled = 0;
        var unchanged = 0;
        var linkedTitlesUpdated = 0;

        await database.WriteAsync(async session =>
        {
            var titlePreference = await session.Titles.GetLibraryPreferenceAsync().ConfigureAwait(false);
            var observedTitles = new List<Guid>();
            var existingAll = await session.Games.GetAllAsync().ConfigureAwait(false);
            var existingByKey = new Dictionary<(Guid PluginId, string GameId), List<StoredGame>>();
            foreach (var stored in existingAll)
            {
                var g = stored.Value;
                if (StoreCatalog.IsHeroicStore(g.PluginId) && !string.IsNullOrWhiteSpace(g.GameId))
                {
                    var key = (g.PluginId, g.GameId);
                    if (!existingByKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        existingByKey[key] = list;
                    }

                    list.Add(stored);
                }
            }

            foreach (var item in discovery.Games.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (item.PluginId, item.GameId);
                if (!existingByKey.TryGetValue(key, out var matches))
                {
                    var created = new Game(GameIdFor(item.PluginId, item.GameId), item.GameId, item.Name)
                    {
                        PluginId = item.PluginId,
                        IsInstalled = item.IsInstalled,
                        InstallDirectory = item.InstallDirectory,
                        Version = item.Version,
                        Description = item.Description,
                        CoverImage = item.CoverImage,
                        Added = utcNow,
                        Modified = utcNow,
                    };
                    await session.Games.AddAsync(new StoredGame(created)).ConfigureAwait(false);
                    await session.Titles.ObserveAsync(created, item.Name, isNew: true, titlePreference).ConfigureAwait(false);
                    await session.StoreMetadata.ObserveAsync(created, item.Description, item.CoverImage, isNew: true).ConfigureAwait(false);
                    observedTitles.Add(created.Id);
                    added++;
                    continue;
                }

                foreach (var existing in matches)
                {
                    var game = existing.Value;
                    var changed = await session.Titles.ObserveAsync(game, item.Name, isNew: false, titlePreference,
                        isFallback: item.PluginId == StoreCatalog.GogPluginId && HasFallbackName(item)).ConfigureAwait(false);
                    observedTitles.Add(game.Id);
                    var becameUninstalled = false;

                    if (!game.OverrideInstallState)
                    {
                        var canMarkUninstalled = discovery.AuthoritativeInstalledStores.Contains(item.PluginId);
                        var targetInstalled = item.IsInstalled || (!canMarkUninstalled && game.IsInstalled);
                        if (game.IsInstalled != targetInstalled)
                        {
                            becameUninstalled = game.IsInstalled && !targetInstalled;
                            game.IsInstalled = targetInstalled;
                            changed = true;
                        }

                        if (item.IsInstalled)
                        {
                            if (item.InstallDirectory is not null
                                && !string.Equals(game.InstallDirectory, item.InstallDirectory, StringComparison.Ordinal))
                            {
                                game.InstallDirectory = item.InstallDirectory;
                                changed = true;
                            }

                            if (!string.Equals(game.Version, item.Version, StringComparison.Ordinal))
                            {
                                game.Version = item.Version;
                                changed = true;
                            }
                        }
                        else if (canMarkUninstalled)
                        {
                            if (game.InstallDirectory is not null)
                            {
                                game.InstallDirectory = null;
                                changed = true;
                            }

                            if (game.Version is not null)
                            {
                                game.Version = null;
                                changed = true;
                            }
                        }
                    }

                    changed |= await session.StoreMetadata.ObserveAsync(game, item.Description, item.CoverImage).ConfigureAwait(false);

                    if (changed)
                    {
                        game.Modified = utcNow;
                        await session.Games.UpdateAsync(new StoredGame(game, existing.LegacyJson)).ConfigureAwait(false);
                        if (becameUninstalled)
                        {
                            uninstalled++;
                        }
                        else
                        {
                            updated++;
                        }
                    }
                    else
                    {
                        unchanged++;
                    }
                }
            }

            foreach (var (key, matches) in existingByKey)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (discovery.Games.ContainsKey(key) || discovery.SkippedInstalledKeys.Contains(key))
                {
                    continue;
                }

                if (!discovery.AuthoritativeInstalledStores.Contains(key.PluginId))
                {
                    continue;
                }

                foreach (var existing in matches)
                {
                    var game = existing.Value;
                    if (game.OverrideInstallState || !game.IsInstalled)
                    {
                        continue;
                    }

                    game.IsInstalled = false;
                    game.InstallDirectory = null;
                    game.Version = null;
                    game.Modified = utcNow;
                    await session.Games.UpdateAsync(new StoredGame(game, existing.LegacyJson)).ConfigureAwait(false);
                    uninstalled++;
                }
            }

            linkedTitlesUpdated = await session.Titles.RefreshLinkedTitlesAsync(observedTitles, titlePreference, utcNow).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return new HeroicSyncResult(
            added,
            updated,
            uninstalled,
            unchanged,
            discovery.Skipped,
            discovery.Issues,
            Reconciled: true,
            RootFound: true) { LinkedTitlesUpdated = linkedTitlesUpdated };
    }

    internal static Guid GameIdFor(Guid pluginId, string gameId)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes($"{HeroicNamespaceGuid:N}:{pluginId:N}:{gameId}");
        var hash = System.Security.Cryptography.SHA256.HashData(payload);
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static void MergeGame(
        Dictionary<(Guid PluginId, string GameId), HeroicDiscoveredGame> target,
        HeroicDiscoveredGame incoming)
    {
        var key = (incoming.PluginId, incoming.GameId);
        if (!target.TryGetValue(key, out var current))
        {
            target[key] = incoming;
            return;
        }

        // Prefer installed state, install dir, and version while enriching title/description/cover.
        var isInstalled = current.IsInstalled || incoming.IsInstalled;
        var name = current.IsInstalled
            ? (string.IsNullOrWhiteSpace(incoming.Name) || string.Equals(incoming.Name, incoming.GameId, StringComparison.Ordinal)
                ? current.Name
                : incoming.Name)
            : (current.Name == current.GameId ? incoming.Name : current.Name);
        target[key] = new HeroicDiscoveredGame(
            incoming.PluginId,
            incoming.GameId,
            name,
            isInstalled,
            current.InstallDirectory ?? incoming.InstallDirectory,
            current.Version ?? incoming.Version,
            current.Description ?? incoming.Description,
            current.CoverImage ?? incoming.CoverImage);
    }

    private static JsonDocument? TryReadContainedJson(
        string allowedRoot,
        string candidatePath,
        LinuxBoundedFileReader reader,
        string issueCode,
        List<string> issues,
        out bool fileExisted)
    {
        var probeInfo = new FileInfo(candidatePath);
        fileExisted = probeInfo.Exists
            || probeInfo.LinkTarget is not null
            || (probeInfo.Attributes != (FileAttributes)(-1) && (probeInfo.Attributes & FileAttributes.ReparsePoint) != 0);
        if (!fileExisted)
        {
            return null;
        }

        try
        {
            var resolvedParent = ResolveRealDirectory(Path.GetDirectoryName(candidatePath)!);
            var canonical = Path.Combine(resolvedParent, Path.GetFileName(candidatePath));
            var info = new FileInfo(canonical);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
                if (finalTarget is null || !finalTarget.Exists)
                {
                    issues.Add(issueCode);
                    return null;
                }

                canonical = Path.Combine(
                    ResolveRealDirectory(Path.GetDirectoryName(finalTarget.FullName)!),
                    Path.GetFileName(finalTarget.FullName));
            }

            if (!IsWithinDirectory(canonical, allowedRoot))
            {
                issues.Add(issueCode);
                return null;
            }

            var read = reader.Read(canonical, MaxManifestBytes);
            if (read.Status != BoundedFileReadStatus.Ok || read.Bytes is null || read.Bytes.Length == 0)
            {
                if (read.Status != BoundedFileReadStatus.Missing)
                {
                    issues.Add(issueCode);
                }

                return null;
            }

            return JsonDocument.Parse(read.Bytes, SafeJsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            issues.Add(issueCode);
            return null;
        }
    }

    private static JsonElement? ExtractArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var child)
            && child.ValueKind == JsonValueKind.Array)
        {
            return child;
        }

        return null;
    }

    private static bool IsSafeGameId(Guid pluginId, string? gameId) =>
        StoreCatalog.TryBuildHeroicLaunchUri(pluginId, gameId, out _);

    private static bool IsDlc(JsonElement item) =>
        item.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private static string? ResolveOptionalSafeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir) || dir.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var trimmed = dir.Trim();
        return trimmed.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..")
            ? null
            : Path.GetFullPath(trimmed);
    }

    private static string? ResolveOptionalSafeCover(string heroicRoot, string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(rawPath, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPath))).ToLowerInvariant();
                rawPath = Path.Combine(heroicRoot, "images-cache", digest);
            }

            if (rawPath.Contains("://", StringComparison.Ordinal) || !Path.IsPathFullyQualified(rawPath))
            {
                return null;
            }

            var full = Path.GetFullPath(rawPath);
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent))
            {
                return null;
            }

            var canonicalParent = ResolveRealDirectory(parent);
            var candidate = Path.Combine(canonicalParent, Path.GetFileName(full));
            if (!File.Exists(candidate))
            {
                return null;
            }

            var finalTarget = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
            var resolvedFile = finalTarget is not null
                ? Path.Combine(
                    ResolveRealDirectory(Path.GetDirectoryName(finalTarget.FullName)!),
                    Path.GetFileName(finalTarget.FullName))
                : candidate;
            return IsWithinDirectory(resolvedFile, heroicRoot) && File.Exists(resolvedFile) ? resolvedFile : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetInstallDirectoryName(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        var name = Path.GetFileName(installDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? null : name.Replace('_', ' ');
    }

    private static bool IsWithinDirectory(string candidatePath, string rootDirectory)
    {
        var normalizedRoot = Path.EndsInDirectorySeparator(rootDirectory)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static string ResolveRealDirectory(string directory)
    {
        var path = Path.GetFullPath(directory);
        for (var pass = 0; pass < 16; pass++)
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var current = root;
            var redirected = false;
            var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                if (!Directory.Exists(current))
                {
                    continue;
                }

                var resolved = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (!string.IsNullOrEmpty(resolved)
                    && !string.Equals(Path.TrimEndingDirectorySeparator(resolved), Path.TrimEndingDirectorySeparator(current), StringComparison.Ordinal))
                {
                    path = i + 1 < segments.Length
                        ? Path.Combine([resolved, .. segments[(i + 1)..]])
                        : resolved;
                    redirected = true;
                    break;
                }
            }

            if (!redirected)
            {
                return root.Length > 0 && current == root ? root : Path.TrimEndingDirectorySeparator(current);
            }
        }

        return Path.TrimEndingDirectorySeparator(path);
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Playnite.Core;
using Playnite.Core.Addons;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;

namespace Playnite.Desktop.Plugins;

/// <summary>
/// Built-in community compatibility plugin that surfaces ProtonDB ratings, badges, details,
/// and bounded LRU-cached metadata for Steam games via declarative UI slots and opt-in HTTPS lookups.
/// </summary>
internal sealed class ProtonDbPlugin(
    HttpClient? httpClient = null,
    string? cacheDirectory = null,
    IRegularFileProbe? regularFileProbe = null)
    : IPlaynitePlugin, IUiSlotCapability, IMetadataCapability, IPluginSettingsCapability, ICommandCapability
{
    public const string PluginId = "playnite.builtin.protondb";
    public const string ProviderIdentifier = PluginId;
    public const string RefreshRatingCommandId = "protondb.refresh-rating";
    public const string ClearCacheCommandId = "protondb.clear-cache";
    public const string EnabledSettingKey = "enabled";
    public const string OnlineLookupSettingKey = "onlineLookup";
    public const string CacheFileName = "protondb-cache.json";
    public const int MaxCachedEntries = 512;
    public const int MaxCacheFileBytes = 192 * 1024;
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan ErrorBackoffDuration = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private static readonly IReadOnlyList<string> Slots =
    [
        PluginUiSlots.CardBadge,
        PluginUiSlots.DetailsBadges,
        PluginUiSlots.DetailsSection,
    ];

    private static readonly IReadOnlyList<PluginSettingField> SettingsSchema =
    [
        new(
            EnabledSettingKey,
            "Enable ProtonDB ratings",
            PluginSettingType.Boolean,
            DefaultValue: "true",
            Description: "Show ProtonDB compatibility badges and details for Steam games."),
        new(
            OnlineLookupSettingKey,
            "Fetch online ProtonDB summaries",
            PluginSettingType.Boolean,
            DefaultValue: "false",
            Description: "Query public ProtonDB summary JSON on selection cache misses and cache locally."),
    ];

    private static long cacheTempSequence;

    private readonly HttpClient client = httpClient ?? SharedClient;
    private readonly string? normalizedCacheDirectory = NormalizeCacheDirectory(cacheDirectory);
    private readonly IRegularFileProbe? fileProbe = regularFileProbe;
    private readonly Lock cacheGate = new();
    private readonly Lock cacheDiskGate = new();
    private readonly Dictionary<uint, LinkedListNode<(uint AppId, ProtonDbSummary Summary)>> lruIndex = new();
    private readonly LinkedList<(uint AppId, ProtonDbSummary Summary)> lruList = new();
    private readonly ConcurrentDictionary<uint, DateTimeOffset> retryAfter = new();
    private readonly ConcurrentDictionary<uint, Lazy<Task<ProtonDbSummary?>>> inflightFetches = new();
    private readonly ConcurrentDictionary<uint, long> appSuccessGen = new();

    private bool diskCacheLoaded;
    private long cacheEpoch;
    private PluginContext? hostContext;

    public PluginDescriptor Descriptor { get; } = new(
        PluginId,
        Guid.Empty,
        "ProtonDB Compatibility",
        new Version(1, 0, 0),
        PluginLoadKind.BuiltIn,
        PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        PluginCapabilityKind.UiSlots | PluginCapabilityKind.Metadata | PluginCapabilityKind.Settings | PluginCapabilityKind.Commands,
        new PluginEntryPoints());

    public IReadOnlyList<string> SubscribedSlots => Slots;

    public string ProviderId => ProviderIdentifier;

    public string DisplayName => "ProtonDB";

    public IReadOnlyList<PluginSettingField> Fields => SettingsSchema;

    public IReadOnlyList<PluginCommandDescriptor> Commands { get; } =
    [
        new PluginCommandDescriptor(
            RefreshRatingCommandId,
            PluginId,
            "Refresh ProtonDB Rating",
            PluginCommandPlacement.GameContext,
            StoreCatalog.SteamPluginId),
        new PluginCommandDescriptor(
            ClearCacheCommandId,
            PluginId,
            "Clear ProtonDB Cache",
            PluginCommandPlacement.LibraryToolbar),
    ];

    public PluginRegistration Setup(PluginContext context)
    {
        hostContext = context ?? throw new ArgumentNullException(nameof(context));
        return new PluginRegistration
        {
            UiSlotProviders = [this],
            MetadataProviders = [this],
            CommandProviders = [this],
            Settings = this,
        };
    }

    public async Task<PluginCommandResult> ExecuteAsync(
        string commandId,
        PluginCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(commandId, ClearCacheCommandId, StringComparison.Ordinal))
        {
            return ClearCache();
        }

        if (string.Equals(commandId, RefreshRatingCommandId, StringComparison.Ordinal))
        {
            if (!IsFeatureEnabled())
            {
                return new PluginCommandResult(false, "ProtonDB ratings are disabled in plugin settings.");
            }

            if (context.Game is null || !TryGetSteamAppId(context.Game, out var appId))
            {
                return new PluginCommandResult(false, "Select a Steam game to refresh its ProtonDB rating.");
            }

            if (!IsOnlineLookupEnabled())
            {
                return new PluginCommandResult(false, "Enable online ProtonDB lookups in Plugins to refresh ratings.");
            }

            RemoveCachedEntry(appId, persistToDisk: true);
            retryAfter.TryRemove(appId, out _);

            var refreshed = await FetchAndCacheAsync(appId, cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
            {
                return new PluginCommandResult(
                    false,
                    $"Could not refresh ProtonDB rating for app {appId}.",
                    RefreshSlots: true);
            }

            var tierLabel = FormatTierLabel(refreshed.Tier);
            return new PluginCommandResult(
                true,
                $"ProtonDB rating refreshed: {tierLabel}.",
                RefreshSlots: true);
        }

        return new PluginCommandResult(false, $"Unknown ProtonDB command '{commandId}'.");
    }

    public async Task<PluginUiSlotContribution?> ResolveSlotAsync(
        string slotId,
        PluginGameSnapshot game,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsFeatureEnabled() || !TryGetSteamAppId(game, out var appId))
        {
            return null;
        }

        // Card badges only read local/memory cache so scrolling a grid page never triggers HTTP storms.
        var allowOnlineFetch = !string.Equals(slotId, PluginUiSlots.CardBadge, StringComparison.Ordinal);
        var summary = await GetSummaryAsync(appId, allowOnlineFetch, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var tierLabel = FormatTierLabel(summary.Tier);
        var tone = MapTone(summary.Tier);
        var protonUrl = $"https://www.protondb.com/app/{appId}";

        return slotId switch
        {
            PluginUiSlots.CardBadge => new PluginUiSlotContribution(
                PluginId,
                slotId,
                Badges: [new PluginUiBadge($"protondb.{appId}", tierLabel, tone, $"ProtonDB: {tierLabel}")]),
            PluginUiSlots.DetailsBadges => new PluginUiSlotContribution(
                PluginId,
                slotId,
                Badges:
                [
                    new PluginUiBadge(
                        $"protondb.{appId}",
                        $"ProtonDB: {tierLabel}",
                        tone,
                        "View ProtonDB compatibility reports",
                        protonUrl),
                ]),
            PluginUiSlots.DetailsSection => new PluginUiSlotContribution(
                PluginId,
                slotId,
                Sections: [BuildSection(appId, summary, tierLabel, protonUrl)]),
            _ => null,
        };
    }

    public async Task<PluginGameMetadata?> FetchMetadataAsync(
        PluginGameSnapshot game,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsFeatureEnabled() || !TryGetSteamAppId(game, out var appId))
        {
            return null;
        }

        var summary = await GetSummaryAsync(appId, allowOnlineFetch: true, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tier"] = summary.Tier,
            ["url"] = $"https://www.protondb.com/app/{appId}",
        };
        if (!string.IsNullOrWhiteSpace(summary.BestReportedTier))
        {
            attributes["bestReportedTier"] = summary.BestReportedTier;
        }

        if (!string.IsNullOrWhiteSpace(summary.Confidence))
        {
            attributes["confidence"] = summary.Confidence;
        }

        if (summary.TotalReports is int total && total >= 0)
        {
            attributes["totalReports"] = total.ToString(CultureInfo.InvariantCulture);
        }

        return new PluginGameMetadata(
            PluginId,
            Summary: BuildSummaryLine(summary, FormatTierLabel(summary.Tier)),
            CommunityScore: null,
            Attributes: attributes);
    }

    internal void SeedCacheForTest(uint appId, string tier, string? bestReportedTier = null, string? confidence = null, int? total = null)
    {
        StoreCachedSummary(appId, new ProtonDbSummary(tier, bestReportedTier, confidence, total), persistToDisk: false);
    }

    private bool IsFeatureEnabled()
    {
        var raw = hostContext?.GetSetting(EnabledSettingKey);
        return raw is null || !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOnlineLookupEnabled()
    {
        var raw = hostContext?.GetSetting(OnlineLookupSettingKey);
        return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSteamAppId(PluginGameSnapshot game, out uint appId)
    {
        appId = 0;
        return game.PluginId == StoreCatalog.SteamPluginId
            && !string.IsNullOrWhiteSpace(game.GameId)
            && uint.TryParse(game.GameId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out appId)
            && appId > 0;
    }

    private async Task<ProtonDbSummary?> GetSummaryAsync(
        uint appId,
        bool allowOnlineFetch,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedSummary(appId, out var cached))
        {
            return cached;
        }

        if (!allowOnlineFetch || !IsOnlineLookupEnabled())
        {
            return null;
        }

        var now = (hostContext?.TimeProvider ?? TimeProvider.System).GetUtcNow();
        if (retryAfter.TryGetValue(appId, out var retryAt))
        {
            if (now < retryAt)
            {
                return null;
            }

            retryAfter.TryRemove(KeyValuePair.Create(appId, retryAt));
        }

        var lazy = inflightFetches.GetOrAdd(
            appId,
            static (id, self) => new Lazy<Task<ProtonDbSummary?>>(
                () => self.FetchAndCacheAsync(id),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);
        try
        {
            var task = lazy.Value;
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inflightFetches.TryRemove(KeyValuePair.Create(appId, lazy));
        }
    }

    private async Task<ProtonDbSummary?> FetchAndCacheAsync(uint appId, CancellationToken cancellationToken = default)
    {
        var startEpoch = Volatile.Read(ref cacheEpoch);
        var startSuccessGen = appSuccessGen.GetOrAdd(appId, 0);
        try
        {
            var timeProvider = hostContext?.TimeProvider ?? TimeProvider.System;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6), timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            var requestUri = $"https://www.protondb.com/api/v1/reports/summaries/{appId}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RecordBackoff(appId, startEpoch, startSuccessGen);
                return null;
            }

            if (response.Content.Headers.ContentLength is long contentLength
                && (contentLength < 0 || contentLength > MaxResponseBytes))
            {
                RecordBackoff(appId, startEpoch, startSuccessGen);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            var jsonBytes = await ReadBoundedBytesAsync(stream, MaxResponseBytes, linkedCts.Token).ConfigureAwait(false);
            if (jsonBytes is null || !TryParseSummaryUtf8(jsonBytes, out var parsed))
            {
                RecordBackoff(appId, startEpoch, startSuccessGen);
                return null;
            }

            appSuccessGen.AddOrUpdate(appId, 1, static (_, previous) => previous + 1);
            retryAfter.TryRemove(appId, out _);
            StoreCachedSummary(appId, parsed, persistToDisk: true, expectedCacheEpoch: startEpoch);
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            RecordBackoff(appId, startEpoch, startSuccessGen);
            hostContext?.Log($"ProtonDB lookup failed for app {appId}: {exception.Message}");
            return null;
        }
    }

    private void RecordBackoff(uint appId, long startEpoch, long startSuccessGen)
    {
        if (Volatile.Read(ref cacheEpoch) != startEpoch || appSuccessGen.GetOrAdd(appId, 0) != startSuccessGen)
        {
            return;
        }

        var now = (hostContext?.TimeProvider ?? TimeProvider.System).GetUtcNow();
        retryAfter[appId] = now + ErrorBackoffDuration;
        if (retryAfter.Count > 256)
        {
            foreach (var pair in retryAfter)
            {
                if (pair.Value <= now)
                {
                    retryAfter.TryRemove(KeyValuePair.Create(pair.Key, pair.Value));
                }
            }
        }
    }

    private bool TryGetCachedSummary(uint appId, out ProtonDbSummary summary)
    {
        EnsureDiskCacheLoaded();
        lock (cacheGate)
        {
            if (lruIndex.TryGetValue(appId, out var node))
            {
                lruList.Remove(node);
                lruList.AddFirst(node);
                summary = node.Value.Summary;
                return true;
            }
        }

        summary = default!;
        return false;
    }

    private void StoreCachedSummary(
        uint appId,
        ProtonDbSummary summary,
        bool persistToDisk,
        long? expectedCacheEpoch = null)
    {
        EnsureDiskCacheLoaded();
        if (persistToDisk && normalizedCacheDirectory is not null)
        {
            KeyValuePair<uint, ProtonDbSummary>[]? snapshotToWrite = null;
            lock (cacheDiskGate)
            {
                lock (cacheGate)
                {
                    if (expectedCacheEpoch is long expected && expected != cacheEpoch)
                    {
                        return;
                    }

                    UpsertLruLocked(appId, summary);
                    snapshotToWrite = lruList.Select(entry => KeyValuePair.Create(entry.AppId, entry.Summary)).ToArray();
                }

                if (snapshotToWrite is not null)
                {
                    PersistDiskCache(snapshotToWrite);
                }
            }

            return;
        }

        lock (cacheGate)
        {
            if (expectedCacheEpoch is long expected && expected != cacheEpoch)
            {
                return;
            }

            UpsertLruLocked(appId, summary);
        }
    }

    private void RemoveCachedEntry(uint appId, bool persistToDisk = false)
    {
        EnsureDiskCacheLoaded();
        if (persistToDisk && normalizedCacheDirectory is not null)
        {
            lock (cacheDiskGate)
            {
                KeyValuePair<uint, ProtonDbSummary>[]? snapshotToWrite = null;
                lock (cacheGate)
                {
                    if (lruIndex.TryGetValue(appId, out var node))
                    {
                        lruList.Remove(node);
                        lruIndex.Remove(appId);
                        snapshotToWrite = lruList.Select(entry => KeyValuePair.Create(entry.AppId, entry.Summary)).ToArray();
                    }
                }

                if (snapshotToWrite is not null)
                {
                    PersistDiskCache(snapshotToWrite);
                }
            }

            return;
        }

        lock (cacheGate)
        {
            if (lruIndex.TryGetValue(appId, out var node))
            {
                lruList.Remove(node);
                lruIndex.Remove(appId);
            }
        }
    }

    private PluginCommandResult ClearCache()
    {
        int removedCount;
        lock (cacheDiskGate)
        {
            EnsureDiskCacheLoadedUnderDiskGate();
            lock (cacheGate)
            {
                cacheEpoch++;
                removedCount = lruList.Count;
                lruIndex.Clear();
                lruList.Clear();
                diskCacheLoaded = true;
            }

            retryAfter.Clear();

            if (normalizedCacheDirectory is not null)
            {
                try
                {
                    if (IsCacheRootOrLeafUnsafe(out var offending, checkTargetExists: true))
                    {
                        return new PluginCommandResult(
                            false,
                            $"Cleared memory cache but refused symlinked ProtonDB cache path '{offending}'.",
                            RefreshSlots: true);
                    }

                    var targetPath = Path.Combine(normalizedCacheDirectory, CacheFileName);
                    if (File.Exists(targetPath))
                    {
                        if (fileProbe is not null && !fileProbe.IsRegularFile(targetPath))
                        {
                            return new PluginCommandResult(
                                false,
                                $"Cleared memory cache but refused non-regular ProtonDB cache file '{targetPath}'.",
                                RefreshSlots: true);
                        }

                        File.Delete(targetPath);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new PluginCommandResult(
                        false,
                        $"Could not delete ProtonDB cache file: {exception.Message}",
                        RefreshSlots: true);
                }
            }
        }

        return new PluginCommandResult(
            true,
            $"Cleared {removedCount} cached ProtonDB ratings.",
            RefreshSlots: true);
    }

    private void UpsertLruLocked(uint appId, ProtonDbSummary summary)
    {
        if (lruIndex.TryGetValue(appId, out var existing))
        {
            existing.Value = (appId, summary);
            lruList.Remove(existing);
            lruList.AddFirst(existing);
            return;
        }

        if (lruList.Count >= MaxCachedEntries && lruList.Last is { } oldest)
        {
            lruList.RemoveLast();
            lruIndex.Remove(oldest.Value.AppId);
        }

        var node = new LinkedListNode<(uint AppId, ProtonDbSummary Summary)>((appId, summary));
        lruList.AddFirst(node);
        lruIndex[appId] = node;
    }

    private void EnsureDiskCacheLoaded()
    {
        lock (cacheGate)
        {
            if (diskCacheLoaded)
            {
                return;
            }
        }

        lock (cacheDiskGate)
        {
            EnsureDiskCacheLoadedUnderDiskGate();
        }
    }

    private void EnsureDiskCacheLoadedUnderDiskGate()
    {
        lock (cacheGate)
        {
            if (diskCacheLoaded)
            {
                return;
            }
        }

        var loadedEntries = ReadDiskCacheEntries();
        lock (cacheGate)
        {
            if (diskCacheLoaded)
            {
                return;
            }

            foreach (var (appId, summary) in loadedEntries)
            {
                UpsertLruLocked(appId, summary);
            }

            diskCacheLoaded = true;
        }
    }

    private List<(uint AppId, ProtonDbSummary Summary)> ReadDiskCacheEntries()
    {
        var entries = new List<(uint AppId, ProtonDbSummary Summary)>();
        if (normalizedCacheDirectory is null || IsCacheRootOrLeafUnsafe(out _, checkTargetExists: false))
        {
            return entries;
        }

        var targetPath = Path.Combine(normalizedCacheDirectory, CacheFileName);
        if (!File.Exists(targetPath))
        {
            return entries;
        }

        if (fileProbe is not null && !fileProbe.IsRegularFile(targetPath))
        {
            return entries;
        }

        try
        {
            var bytes = ReadFileBoundedBytes(targetPath, MaxCacheFileBytes);
            if (bytes is null)
            {
                return entries;
            }

            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            foreach (var property in doc.RootElement.EnumerateObject().Reverse())
            {
                if (!uint.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                    || appId == 0
                    || property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryParseSummaryElement(property.Value, out var summary))
                {
                    entries.Add((appId, summary));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            hostContext?.Log($"Could not read ProtonDB cache '{targetPath}': {exception.Message}");
        }

        return entries;
    }

    private void PersistDiskCache(KeyValuePair<uint, ProtonDbSummary>[] snapshot)
    {
        if (normalizedCacheDirectory is null)
        {
            return;
        }

        try
        {
            if (IsCacheRootOrLeafUnsafe(out var offending, checkTargetExists: true))
            {
                hostContext?.Log($"Refusing symlinked ProtonDB cache path '{offending}'.");
                return;
            }

            Directory.CreateDirectory(normalizedCacheDirectory);
            if (IsCacheRootOrLeafUnsafe(out var postCreateOffending, checkTargetExists: true))
            {
                hostContext?.Log($"Refusing symlinked ProtonDB cache path '{postCreateOffending}'.");
                return;
            }

            var targetPath = Path.Combine(normalizedCacheDirectory, CacheFileName);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var (appId, summary) in snapshot)
                {
                    writer.WritePropertyName(appId.ToString(CultureInfo.InvariantCulture));
                    writer.WriteStartObject();
                    writer.WriteString("tier", summary.Tier);
                    if (!string.IsNullOrWhiteSpace(summary.BestReportedTier))
                    {
                        writer.WriteString("bestReportedTier", summary.BestReportedTier);
                    }

                    if (!string.IsNullOrWhiteSpace(summary.Confidence))
                    {
                        writer.WriteString("confidence", summary.Confidence);
                    }

                    if (summary.TotalReports is int total && total >= 0)
                    {
                        writer.WriteNumber("total", total);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            if (buffer.Length > MaxCacheFileBytes)
            {
                return;
            }

            var tempPath = Path.Combine(
                normalizedCacheDirectory,
                $".protondb-cache.{Environment.ProcessId}.{Interlocked.Increment(ref cacheTempSequence)}.tmp");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    buffer.Position = 0;
                    buffer.CopyTo(stream);
                }

                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch
            {
                TryDeleteTempFile(tempPath);
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            hostContext?.Log($"Could not persist ProtonDB cache: {exception.Message}");
        }
    }

    private bool IsCacheRootOrLeafUnsafe(out string offendingPath, bool checkTargetExists)
    {
        if (normalizedCacheDirectory is null)
        {
            offendingPath = string.Empty;
            return false;
        }

        var parent = Path.GetDirectoryName(normalizedCacheDirectory);
        if (!string.IsNullOrWhiteSpace(parent) && IsPathSymlinkOrReparsePoint(parent))
        {
            offendingPath = parent;
            return true;
        }

        if (IsPathSymlinkOrReparsePoint(normalizedCacheDirectory))
        {
            offendingPath = normalizedCacheDirectory;
            return true;
        }

        var targetPath = Path.Combine(normalizedCacheDirectory, CacheFileName);
        if (IsPathSymlinkOrReparsePoint(targetPath))
        {
            offendingPath = targetPath;
            return true;
        }

        if (checkTargetExists && File.Exists(targetPath) && fileProbe is not null && !fileProbe.IsRegularFile(targetPath))
        {
            offendingPath = targetPath;
            return true;
        }

        offendingPath = string.Empty;
        return false;
    }

    private static string? NormalizeCacheDirectory(string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory)
            || cacheDirectory.Contains('\0')
            || !Path.IsPathFullyQualified(cacheDirectory))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
    }

    private static bool IsPathSymlinkOrReparsePoint(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget is not null || (fileInfo.Exists && (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            return true;
        }

        var dirInfo = new DirectoryInfo(path);
        return dirInfo.LinkTarget is not null || (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static byte[]? ReadFileBoundedBytes(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBoundedBytesCore(stream, maxBytes);
    }

    private static byte[]? ReadBoundedBytesCore(Stream stream, int maxBytes)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = stream.Read(chunk, 0, Math.Min(chunk.Length, (maxBytes + 1) - total));
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            ms.Write(chunk, 0, read);
        }

        return StripUtf8Bom(ms.ToArray());
    }

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, (maxBytes + 1) - total)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            ms.Write(chunk, 0, read);
        }

        return StripUtf8Bom(ms.ToArray());
    }

    private static byte[] StripUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    private static bool TryParseSummaryUtf8(byte[] utf8Bytes, out ProtonDbSummary summary)
    {
        summary = default!;
        try
        {
            using var doc = JsonDocument.Parse(utf8Bytes, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && TryParseSummaryElement(doc.RootElement, out summary);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseSummaryElement(JsonElement root, out ProtonDbSummary summary)
    {
        summary = default!;
        var tier = ReadOptionalString(root, "tier")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(tier))
        {
            return false;
        }

        var best = ReadOptionalString(root, "bestReportedTier")?.ToLowerInvariant();
        var confidence = ReadOptionalString(root, "confidence")?.ToLowerInvariant();
        int? total = null;
        if (root.TryGetProperty("total", out var totalElement)
            && totalElement.ValueKind == JsonValueKind.Number
            && totalElement.TryGetInt32(out var totalValue)
            && totalValue >= 0)
        {
            total = totalValue;
        }

        summary = new ProtonDbSummary(tier, best, confidence, total);
        return true;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static PluginUiSection BuildSection(
        uint appId,
        ProtonDbSummary summary,
        string tierLabel,
        string protonUrl)
    {
        var fields = new List<PluginUiSectionField>
        {
            new("Tier", tierLabel),
        };
        if (!string.IsNullOrWhiteSpace(summary.BestReportedTier))
        {
            fields.Add(new PluginUiSectionField("Best reported", FormatTierLabel(summary.BestReportedTier)));
        }

        if (!string.IsNullOrWhiteSpace(summary.Confidence))
        {
            fields.Add(new PluginUiSectionField("Confidence", FormatTierLabel(summary.Confidence)));
        }

        if (summary.TotalReports is int total)
        {
            fields.Add(new PluginUiSectionField("Reports", total.ToString(CultureInfo.InvariantCulture)));
        }

        return new PluginUiSection(
            $"protondb.{appId}.summary",
            "ProtonDB Compatibility",
            BuildSummaryLine(summary, tierLabel),
            fields,
            LinkLabel: "View on ProtonDB",
            LinkUrl: protonUrl);
    }

    private static string BuildSummaryLine(ProtonDbSummary summary, string tierLabel)
    {
        var confidencePart = string.IsNullOrWhiteSpace(summary.Confidence) ? null : $"{summary.Confidence} confidence";
        var countPart = summary.TotalReports is int total ? $"{total.ToString(CultureInfo.InvariantCulture)} reports" : null;
        return (confidencePart, countPart) switch
        {
            (not null, not null) => $"Community rating: {tierLabel} ({confidencePart}, {countPart}).",
            (not null, null) => $"Community rating: {tierLabel} ({confidencePart}).",
            (null, not null) => $"Community rating: {tierLabel} ({countPart}).",
            _ => $"Community rating: {tierLabel}.",
        };
    }

    private static string FormatTierLabel(string rawTier) => rawTier.Trim().ToLowerInvariant() switch
    {
        "native" => "Native",
        "platinum" => "Platinum",
        "gold" => "Gold",
        "silver" => "Silver",
        "bronze" => "Bronze",
        "borked" => "Borked",
        "pending" => "Pending",
        "strong" => "Strong",
        "good" => "Good",
        "moderate" => "Moderate",
        "low" => "Low",
        var other when other.Length > 0 => char.ToUpperInvariant(other[0]) + other[1..],
        _ => "Unknown",
    };

    private static PluginBadgeTone MapTone(string rawTier) => rawTier.Trim().ToLowerInvariant() switch
    {
        "native" or "gold" => PluginBadgeTone.Positive,
        "platinum" => PluginBadgeTone.Accent,
        "silver" => PluginBadgeTone.Neutral,
        "bronze" => PluginBadgeTone.Warning,
        "borked" => PluginBadgeTone.Danger,
        "pending" => PluginBadgeTone.Muted,
        _ => PluginBadgeTone.Neutral,
    };

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete temporary ProtonDB cache file '{tempPath}': {exception.Message}");
        }
    }

    private sealed record ProtonDbSummary(
        string Tier,
        string? BestReportedTier,
        string? Confidence,
        int? TotalReports);
}

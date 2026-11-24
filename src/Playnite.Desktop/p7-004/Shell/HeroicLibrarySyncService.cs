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
    bool RootFound);

internal interface ILibraryHeroicSync
{
    Task<HeroicSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken);
}

internal static class HeroicSyncIssues
{
    public const string RootMissing = "heroic-root-missing";
    public const string EpicParseError = "heroic-epic-parse-error";
    public const string GogParseError = "heroic-gog-parse-error";
    public const string AmazonParseError = "heroic-amazon-parse-error";
}

/// <summary>
/// Bounded read-only local discovery of Heroic Games Launcher libraries (Epic/Legendary, GOG, Amazon/Nile)
/// and idempotent SQLite reconciliation off the UI thread. No network calls or token reads.
/// </summary>
internal sealed class HeroicLibrarySyncRunner(
    IEnvironment? environment = null,
    TimeProvider? timeProvider = null) : ILibraryHeroicSync
{
    internal const int MaxManifestBytes = 8 * 1024 * 1024;
    private static readonly Guid HeroicNamespaceGuid = new("d2b5748e-5d73-4f88-a05c-391f8e76c041");
    private static readonly JsonDocumentOptions SafeJsonOptions = new()
    {
        MaxDepth = 32,
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IEnvironment environment = environment ?? new LinuxEnvironment();
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
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

                        var title = GetString(item, "title") ?? appName!;
                        var installPath = ResolveOptionalSafeDir(GetString(item, "install_path"));
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

        var libDoc = TryReadContainedJson(
            root,
            Path.Combine(root, "gog_store", "library.json"),
            reader,
            HeroicSyncIssues.GogParseError,
            issues,
            out _);
        if (libDoc is not null)
        {
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

                        var title = GetString(item, "title") ?? appName!;
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

        await database.WriteAsync(async session =>
        {
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
                    added++;
                    continue;
                }

                foreach (var existing in matches)
                {
                    var game = existing.Value;
                    var changed = false;
                    var becameUninstalled = false;
                    if (!string.Equals(game.Name, item.Name, StringComparison.Ordinal))
                    {
                        game.Name = item.Name;
                        changed = true;
                    }

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

                    if (string.IsNullOrWhiteSpace(game.Description) && !string.IsNullOrWhiteSpace(item.Description))
                    {
                        game.Description = item.Description;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(game.CoverImage) && !string.IsNullOrWhiteSpace(item.CoverImage))
                    {
                        game.CoverImage = item.CoverImage;
                        changed = true;
                    }

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
            RootFound: true);
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
        if (string.IsNullOrWhiteSpace(rawPath)
            || rawPath.Contains("://", StringComparison.Ordinal)
            || rawPath.Contains('\0', StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(rawPath))
        {
            return null;
        }

        try
        {
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

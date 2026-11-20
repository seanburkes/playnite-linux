using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Playnite.Core.Database;
using Playnite.Core.Models;

namespace Playnite.Steam.Sync;

/// <summary>
/// Reconciles the discovered Steam library with the SQLite game library. It only writes provider
/// fields (<see cref="Game.Name"/>, <see cref="Game.InstallSize"/> and, unless
/// <see cref="Game.OverrideInstallState"/> is set, <see cref="Game.InstallDirectory"/> and
/// <see cref="Game.IsInstalled"/>), never deletes a record and never writes outside the supplied
/// <see cref="LibraryDatabase"/>. A second run over unchanged discovery is a no-op.
/// </summary>
public sealed class SteamLibrarySyncService
{
    /// <summary>The frozen v10 Steam provider identity, shared with the library importer.</summary>
    public const string SteamPluginId = "cb91dfc9-b977-43bf-8e70-55f46e410fab";

    private static readonly Guid SteamPlugin = new(SteamPluginId);
    private const string EmptyLegacyJson = "{}";

    private readonly ISteamSyncDiscovery discovery;
    private readonly TimeProvider time;

    public SteamLibrarySyncService(ISteamSyncDiscovery discovery, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        this.discovery = discovery;
        time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns the stable library identity used for a newly added Steam app.</summary>
    public static Guid GameIdFor(uint appId)
    {
        Span<byte> identity = stackalloc byte[20];
        SteamPlugin.TryWriteBytes(identity);
        BinaryPrimitives.WriteUInt32BigEndian(identity[16..], appId);
        var hash = SHA256.HashData(identity);
        // RFC 9562 name-based UUID layout: version 5, variant 10xx. .NET stores Data3
        // little-endian, so the version nibble lives in byte 7 of this buffer.
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Discovers once and reconciles the whole library in one write transaction. A skipped app is
    /// reported with its id and reason; a failed write rolls the whole run back.
    /// </summary>
    public async Task<SteamSyncResult> SyncAsync(LibraryDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = discovery.Discover(cancellationToken);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        // A run with nothing to observe or reconcile does not open the destination at all.
        if (snapshot.Apps.Count == 0 && snapshot.Skipped.Count == 0 && !snapshot.CanReconcile)
        {
            return new(0, 0, 0, 0, snapshot.Skipped, snapshot.Issues, false);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var skipped = new List<SteamSyncSkip>(snapshot.Skipped);
        var added = 0;
        var updated = 0;
        var removed = 0;
        var unchanged = 0;

        await database.WriteAsync(async session =>
        {
            var stored = await session.Games.GetAllAsync().ConfigureAwait(false);
            var byAppId = new Dictionary<uint, List<StoredGame>>();
            var byId = new Dictionary<Guid, StoredGame>();
            foreach (var game in stored)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byId.TryAdd(game.Value.Id, game);
                if (game.Value.PluginId == SteamPlugin && TryAppId(game.Value.GameId, out var appId))
                {
                    if (!byAppId.TryGetValue(appId, out var matches))
                    {
                        byAppId.Add(appId, matches = []);
                    }

                    matches.Add(game);
                }
            }

            var observed = new HashSet<uint>();
            foreach (var skip in snapshot.Skipped)
            {
                observed.Add(skip.AppId);
            }

            foreach (var app in snapshot.Apps.OrderBy(app => app.AppId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed.Add(app.AppId);

                if (!byAppId.TryGetValue(app.AppId, out var matches))
                {
                    var id = GameIdFor(app.AppId);
                    if (byId.ContainsKey(id))
                    {
                        skipped.Add(new(app.AppId, SteamSyncReasons.IdentityConflict));
                        continue;
                    }

                    var game = new Game(id, app.AppId.ToString(CultureInfo.InvariantCulture), app.Name)
                    {
                        PluginId = SteamPlugin,
                        InstallDirectory = app.InstallPath,
                        InstallSize = app.SizeOnDisk,
                        IsInstalled = IsInstalled(app.State),
                        Added = now,
                        Modified = now,
                    };
                    await session.Games.AddAsync(new StoredGame(game, EmptyLegacyJson)).ConfigureAwait(false);
                    added++;
                    continue;
                }

                // A duplicate provider identity is ambiguous. Update the lowest-id record and
                // report every extra so it is visible rather than silently coalesced.
                for (var index = 1; index < matches.Count; index++)
                {
                    skipped.Add(new(app.AppId, SteamSyncReasons.DuplicateLibraryRecord));
                }

                var item = matches[0];
                if (Apply(app, item.Value, now))
                {
                    await session.Games.UpdateAsync(item).ConfigureAwait(false);
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }

            if (snapshot.CanReconcile)
            {
                foreach (var (appId, matches) in byAppId.OrderBy(entry => entry.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (observed.Contains(appId))
                    {
                        continue;
                    }

                    foreach (var item in matches)
                    {
                        var game = item.Value;
                        if (game.OverrideInstallState || !game.IsInstalled)
                        {
                            unchanged++;
                            continue;
                        }

                        game.IsInstalled = false;
                        game.Modified = now;
                        await session.Games.UpdateAsync(item).ConfigureAwait(false);
                        removed++;
                    }
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);

        skipped.Sort((left, right) =>
        {
            var id = left.AppId.CompareTo(right.AppId);
            return id != 0 ? id : string.CompareOrdinal(left.Reason, right.Reason);
        });
        return new(added, updated, removed, unchanged, skipped.AsReadOnly(),
            snapshot.Issues.ToArray(), snapshot.CanReconcile);
    }

    private static bool Apply(SteamApp app, Game game, DateTime now)
    {
        var changed = false;
        if (!string.Equals(game.Name, app.Name, StringComparison.Ordinal))
        {
            game.Name = app.Name;
            changed = true;
        }

        if (app.SizeOnDisk is { } size && game.InstallSize != size)
        {
            game.InstallSize = size;
            changed = true;
        }

        // OverrideInstallState is the model's explicit user override: both install-state fields,
        // InstallDirectory and IsInstalled, are ignored while it is set, exactly as the library
        // plugin contract requires.
        if (!game.OverrideInstallState)
        {
            if (!string.Equals(game.InstallDirectory, app.InstallPath, StringComparison.Ordinal))
            {
                game.InstallDirectory = app.InstallPath;
                changed = true;
            }

            var installed = IsInstalled(app.State);
            if (game.IsInstalled != installed)
            {
                game.IsInstalled = installed;
                changed = true;
            }
        }

        if (changed)
        {
            game.Modified = now;
        }

        return changed;
    }

    private static bool IsInstalled(SteamAppState state) =>
        state is SteamAppState.Installed or SteamAppState.UpdatePending;

    private static bool TryAppId(string? text, out uint appId)
    {
        appId = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 10)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out appId);
    }
}

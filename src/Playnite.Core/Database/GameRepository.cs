using System.Collections.ObjectModel;
using Playnite.Core.Models;

namespace Playnite.Core.Database;

/// <summary>A v10 game and its uninterpreted, preserved unknown fields.</summary>
public sealed record StoredGame(Game Value, string LegacyJson = "{}");

/// <summary>A game action whose emulator identity is absent from the caller's emulator catalogue.</summary>
public sealed record DanglingEmulatorReference(Guid GameId, int Position, Guid EmulatorId);

/// <summary>CRUD for complete persistent v10 game graphs. No events or importer behavior.</summary>
public sealed class GameRepository
{
    private readonly LibrarySession session;

    internal GameRepository(LibrarySession session) => this.session = session;

    // Only these source-code identifiers enter SQL; all stored values are parameters.
    private sealed record Relation(string Table, Func<Game, List<Guid>?> Get, Action<Game, List<Guid>?> Set, Func<GameRow, bool> Present);
    private static readonly Relation[] Relations =
    [
        new("game_tag", g => g.TagIds, (g, ids) => g.TagIds = ids, r => r.TagIdsPresent),
        new("game_genre", g => g.GenreIds, (g, ids) => g.GenreIds = ids, r => r.GenreIdsPresent),
        new("game_platform", g => g.PlatformIds, (g, ids) => g.PlatformIds = ids, r => r.PlatformIdsPresent),
        new("game_publisher", g => g.PublisherIds, (g, ids) => g.PublisherIds = ids, r => r.PublisherIdsPresent),
        new("game_developer", g => g.DeveloperIds, (g, ids) => g.DeveloperIds = ids, r => r.DeveloperIdsPresent),
        new("game_category", g => g.CategoryIds, (g, ids) => g.CategoryIds = ids, r => r.CategoryIdsPresent),
        new("game_feature", g => g.FeatureIds, (g, ids) => g.FeatureIds = ids, r => r.FeatureIdsPresent),
        new("game_series", g => g.SeriesIds, (g, ids) => g.SeriesIds = ids, r => r.SeriesIdsPresent),
        new("game_age_rating", g => g.AgeRatingIds, (g, ids) => g.AgeRatingIds = ids, r => r.AgeRatingIdsPresent),
        new("game_region", g => g.RegionIds, (g, ids) => g.RegionIds = ids, r => r.RegionIdsPresent),
    ];

    /// <summary>Inserts a graph; duplicate identities or unresolved references fail explicitly.</summary>
    public Task AddAsync(StoredGame item) => session.AtomicAsync(async () =>
    {
        var row = GameRow.FromGame(item);
        await session.ExecuteAsync($"INSERT INTO game({GameRow.Columns}) VALUES ({GameRow.Parameters});", row).ConfigureAwait(false);
        await WriteChildrenAsync(item.Value).ConfigureAwait(false);
    });

    /// <summary>Replaces a complete graph atomically; missing identity is an error.</summary>
    public Task UpdateAsync(StoredGame item) => session.AtomicAsync(async () =>
    {
        var row = GameRow.FromGame(item);
        if (await session.ExecuteAsync($"UPDATE game SET ({GameRow.Columns}) = ({GameRow.Parameters}) WHERE Id=@Id;", row).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Game {item.Value.Id} was not found.");
        }
        foreach (var table in Relations.Select(r => r.Table).Concat(["game_link", "game_action", "game_rom"]))
        {
            await session.ExecuteAsync($"DELETE FROM {table} WHERE GameId=@Id;", new { row.Id }).ConfigureAwait(false);
        }
        await WriteChildrenAsync(item.Value).ConfigureAwait(false);
    });

    /// <summary>Reads a graph, or null if absent.</summary>
    public async Task<StoredGame?> GetAsync(Guid id)
    {
        var row = await session.QuerySingleOrDefaultAsync<GameRow>("SELECT * FROM game WHERE Id=@Id;", new { Id = id.ToString("D") }).ConfigureAwait(false);
        return row == null ? null : await ReadChildrenAsync(row).ConfigureAwait(false);
    }

    /// <summary>Reads graphs ordered by canonical identity.</summary>
    public async Task<IReadOnlyList<StoredGame>> GetAllAsync()
    {
        var rows = await session.QueryAsync<GameRow>("SELECT * FROM game ORDER BY Id;").ConfigureAwait(false);
        var result = new List<StoredGame>();
        foreach (var row in rows)
        {
            result.Add(await ReadChildrenAsync(row).ConfigureAwait(false));
        }
        return result;
    }

    /// <summary>Deletes the graph and dependent rows; returns false if absent.</summary>
    public async Task<bool> DeleteAsync(Guid id)
    {
        var exists = await session.QuerySingleOrDefaultAsync<int>(
            "SELECT 1 FROM game WHERE Id=@Id;", new { Id = id.ToString("D") }).ConfigureAwait(false) == 1;
        if (exists)
        {
            await session.AtomicAsync(() => session.Titles.DeleteSourceAsync(id, TimeProvider.System.GetUtcNow().UtcDateTime))
                .ConfigureAwait(false);
        }
        return exists;
    }

    /// <summary>
    /// Reports actions whose emulator identity is absent from <paramref name="knownEmulators"/>.
    /// v10 stores emulators in a separate collection that this destination does not hold, so no
    /// foreign key can express this reference; the importer must report it against the source.
    /// </summary>
    public async Task<IReadOnlyList<DanglingEmulatorReference>> FindDanglingEmulatorReferencesAsync(IReadOnlyCollection<Guid> knownEmulators)
    {
        ArgumentNullException.ThrowIfNull(knownEmulators);
        var known = knownEmulators as ISet<Guid> ?? new HashSet<Guid>(knownEmulators);
        var rows = await session.QueryAsync<EmulatorReferenceRow>(
            "SELECT GameId, Position, EmulatorId FROM game_action WHERE EmulatorId<>@NoEmulator ORDER BY GameId, Position;",
            new { NoEmulator = Guid.Empty.ToString("D") }).ConfigureAwait(false);
        return rows.Where(row => !known.Contains(Guid.Parse(row.EmulatorId)))
            .Select(row => new DanglingEmulatorReference(Guid.Parse(row.GameId), row.Position, Guid.Parse(row.EmulatorId)))
            .ToArray();
    }

    private async Task<StoredGame> ReadChildrenAsync(GameRow row)
    {
        var game = row.ToGame();
        var args = new { row.Id };
        foreach (var relation in Relations)
        {
            var ids = relation.Present(row)
                ? (await session.QueryAsync<string>($"SELECT ItemId FROM {relation.Table} WHERE GameId=@Id ORDER BY Position;", args).ConfigureAwait(false)).Select(Guid.Parse).ToList()
                : null;
            relation.Set(game, ids);
        }
        game.Links = row.LinksPresent
            ? new ObservableCollection<Link>(await session.QueryAsync<Link>("SELECT Name, Url FROM game_link WHERE GameId=@Id ORDER BY Position;", args).ConfigureAwait(false)) : null;
        game.Roms = row.RomsPresent
            ? new ObservableCollection<GameRom>(await session.QueryAsync<GameRom>("SELECT Name, Path FROM game_rom WHERE GameId=@Id ORDER BY Position;", args).ConfigureAwait(false)) : null;
        game.GameActions = row.GameActionsPresent
            ? new ObservableCollection<GameAction>((await session.QueryAsync<ActionRow>("SELECT * FROM game_action WHERE GameId=@Id ORDER BY Position;", args).ConfigureAwait(false)).Select(a => a.ToAction())) : null;
        return new StoredGame(game, row.LegacyJson);
    }

    private async Task WriteChildrenAsync(Game game)
    {
        var id = game.Id.ToString("D");
        foreach (var relation in Relations)
        {
            if (relation.Get(game) is not { } ids) continue;
            for (var i = 0; i < ids.Count; i++)
            {
                await session.ExecuteAsync($"INSERT INTO {relation.Table}(GameId, Position, ItemId) VALUES (@Id, @Position, @ItemId);",
                    new { Id = id, Position = i, ItemId = ids[i].ToString("D") }).ConfigureAwait(false);
            }
        }
        if (game.Links != null)
        {
            for (var i = 0; i < game.Links.Count; i++)
            {
                var link = game.Links[i] ?? throw new ArgumentException("A persisted link cannot be null.");
                await session.ExecuteAsync("INSERT INTO game_link(GameId, Position, Name, Url) VALUES (@Id, @Position, @Name, @Url);",
                    new { Id = id, Position = i, link.Name, link.Url }).ConfigureAwait(false);
            }
        }
        if (game.Roms != null)
        {
            for (var i = 0; i < game.Roms.Count; i++)
            {
                var rom = game.Roms[i] ?? throw new ArgumentException("A persisted ROM cannot be null.");
                await session.ExecuteAsync("INSERT INTO game_rom(GameId, Position, Name, Path) VALUES (@Id, @Position, @Name, @Path);",
                    new { Id = id, Position = i, rom.Name, rom.Path }).ConfigureAwait(false);
            }
        }
        if (game.GameActions != null)
        {
            for (var i = 0; i < game.GameActions.Count; i++)
            {
                var action = game.GameActions[i] ?? throw new ArgumentException("A persisted action cannot be null.");
                await session.ExecuteAsync("""
                    INSERT INTO game_action(GameId, Position, Type, Name, Path, Arguments, WorkingDir,
                        AdditionalArguments, OverrideDefaultArgs, IsPlayAction, EmulatorId, EmulatorProfileId,
                        TrackingMode, TrackingPath, Script, InitialTrackingDelay, TrackingFrequency)
                    VALUES (@Id, @Position, @Type, @Name, @Path, @Arguments, @WorkingDir,
                        @AdditionalArguments, @OverrideDefaultArgs, @IsPlayAction, @EmulatorId, @EmulatorProfileId,
                        @TrackingMode, @TrackingPath, @Script, @InitialTrackingDelay, @TrackingFrequency);
                    """, new
                    {
                        Id = id, Position = i, Type = (int)action.Type, action.Name, action.Path, action.Arguments,
                        action.WorkingDir, action.AdditionalArguments, action.OverrideDefaultArgs, action.IsPlayAction,
                        EmulatorId = action.EmulatorId.ToString("D"), action.EmulatorProfileId, TrackingMode = (int)action.TrackingMode,
                        action.TrackingPath, action.Script, action.InitialTrackingDelay, action.TrackingFrequency,
                    }).ConfigureAwait(false);
            }
        }
    }
}

using Playnite.Core.Models;

namespace Playnite.Core.Database;

public sealed record TitleProvenance(
    string ProviderId, string StoreGameId, string ProviderTitle, string? PersonalTitle,
    string? AppliedTitle, string? Preference);

public sealed record StoreTitleCandidate(string SourceGameId, string ProviderId, string StoreGameId, string ProviderTitle);

/// <summary>Tracks provider titles independently of the displayed library name.</summary>
public sealed class LibraryTitleRepository
{
    private static readonly string User = Guid.Empty.ToString("D");
    private readonly LibrarySession session;

    internal LibraryTitleRepository(LibrarySession session) => this.session = session;

    public Task<TitleProvenance?> GetAsync(Guid gameId) =>
        session.QuerySingleOrDefaultAsync<TitleProvenance>("""
            SELECT t.ProviderId, t.StoreGameId, t.ProviderTitle, t.PersonalTitle, t.AppliedTitle, t.Preference
            FROM game_title_provenance t JOIN game g ON g.Id=t.GameId AND g.PluginId=t.ProviderId AND g.GameId=t.StoreGameId
            WHERE g.Id=@Id;
            """, new { Id = gameId.ToString("D") });

    public Task<IEnumerable<StoreTitleCandidate>> GetLinkedTitlesAsync(Guid gameId) =>
        session.QueryAsync<StoreTitleCandidate>("""
            SELECT l.SourceGameId, t.ProviderId, t.StoreGameId, t.ProviderTitle
            FROM game_title_link l JOIN game_title_provenance t
                ON t.GameId=l.SourceGameId AND t.ProviderId=l.ProviderId AND t.StoreGameId=l.StoreGameId
            JOIN game g ON g.Id=t.GameId AND g.PluginId=t.ProviderId AND g.GameId=t.StoreGameId
            JOIN game target ON target.Id=l.GameId AND target.PluginId=l.TargetProviderId AND target.GameId=l.TargetStoreGameId
            WHERE l.GameId=@Id ORDER BY t.ProviderId;
            """, new { Id = gameId.ToString("D") });

    public Task<IEnumerable<StoreTitleCandidate>> FindLinkCandidatesAsync(Guid gameId, string search) =>
        session.QueryAsync<StoreTitleCandidate>("""
            SELECT g.Id AS SourceGameId, t.ProviderId, t.StoreGameId, t.ProviderTitle
            FROM game_title_provenance t JOIN game g
                ON g.Id=t.GameId AND g.PluginId=t.ProviderId AND g.GameId=t.StoreGameId
            JOIN game target ON target.Id=@Id AND target.PluginId<>g.PluginId
            WHERE instr(lower(t.ProviderTitle), lower(@Search)) > 0
            ORDER BY t.ProviderTitle COLLATE NOCASE, t.ProviderId, g.Id LIMIT 30;
            """, new { Id = gameId.ToString("D"), Search = search });

    public async Task<Guid> GetLibraryPreferenceAsync() =>
        Guid.Parse(await session.QuerySingleOrDefaultAsync<string>(
            "SELECT ProviderId FROM library_title_preference WHERE Id=1;").ConfigureAwait(false) ?? User);

    public async Task SetLibraryPreferenceAsync(Guid providerId, DateTime now)
    {
        await session.ExecuteAsync("""
            INSERT INTO library_title_preference VALUES (1, @ProviderId)
            ON CONFLICT(Id) DO UPDATE SET ProviderId=excluded.ProviderId WHERE ProviderId<>excluded.ProviderId;
            """, new { ProviderId = providerId.ToString("D") }).ConfigureAwait(false);
        var ids = await session.QueryAsync<string>(
            "SELECT GameId FROM game_title_provenance WHERE Preference IS NULL ORDER BY GameId;").ConfigureAwait(false);
        await ReapplyAsync(ids, providerId, now).ConfigureAwait(false);
    }

    public async Task SetGamePreferenceAsync(Guid gameId, Guid? providerId, DateTime now,
        string? personalTitle = null, Guid? linkSourceId = null)
    {
        var game = await ReadTitleGameAsync(gameId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Game {gameId} was not found.");
        var title = await GetAsync(gameId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sync this game's source to make its title available.");

        if (linkSourceId is Guid linkId)
        {
            var linked = await GetAsync(linkId).ConfigureAwait(false);
            if (linked is null || linkId == gameId || providerId?.ToString("D") != linked.ProviderId
                || linked.ProviderId == title.ProviderId)
            {
                throw new ArgumentException("The selected store entry is unavailable for linking.", nameof(linkSourceId));
            }

            await session.ExecuteAsync("""
                INSERT INTO game_title_link VALUES (@Id, @SourceGameId, @TargetProviderId, @TargetStoreGameId, @ProviderId, @StoreGameId)
                ON CONFLICT(GameId, ProviderId) DO UPDATE SET SourceGameId=excluded.SourceGameId, StoreGameId=excluded.StoreGameId,
                    TargetProviderId=excluded.TargetProviderId, TargetStoreGameId=excluded.TargetStoreGameId;
                """, new
                {
                    Id = gameId.ToString("D"), SourceGameId = linkId.ToString("D"),
                    TargetProviderId = title.ProviderId, TargetStoreGameId = title.StoreGameId,
                    linked.ProviderId, linked.StoreGameId,
                })
                .ConfigureAwait(false);
        }

        var libraryPreference = await GetLibraryPreferenceAsync().ConfigureAwait(false);
        var preference = providerId?.ToString("D");
        var linkedTitle = await ResolveLinkedTitleAsync(gameId, title.ProviderId, preference ?? libraryPreference.ToString("D")).ConfigureAwait(false);
        if (preference is not null && preference != User && preference != title.ProviderId && linkedTitle is null)
        {
            throw new ArgumentException("The selected title is unavailable for this game.", nameof(providerId));
        }

        title = title with
        {
            PersonalTitle = !string.IsNullOrWhiteSpace(personalTitle) ? personalTitle.Trim()
                : game.Name != title.AppliedTitle || (providerId == Guid.Empty && title.PersonalTitle is null)
                    ? game.Name : title.PersonalTitle,
            AppliedTitle = game.Name,
            Preference = preference,
        };
        var name = await ApplyAsync(gameId, game.Name, title, libraryPreference, linkedTitle).ConfigureAwait(false);
        await UpdateGameNameAsync(game, name, now).ConfigureAwait(false);
    }

    public async Task UnlinkAsync(Guid gameId, Guid providerId, DateTime now)
    {
        var provider = providerId.ToString("D");
        await session.ExecuteAsync("DELETE FROM game_title_link WHERE GameId=@Id AND ProviderId=@Provider;",
            new { Id = gameId.ToString("D"), Provider = provider }).ConfigureAwait(false);
        await ClearUnavailablePreferenceAsync(gameId.ToString("D"), provider).ConfigureAwait(false);
        await ReapplyAsync([gameId.ToString("D")],
            await GetLibraryPreferenceAsync().ConfigureAwait(false), now).ConfigureAwait(false);
    }

    internal async Task DeleteSourceAsync(Guid gameId, DateTime now)
    {
        var id = gameId.ToString("D");
        var links = await session.QueryAsync<DeletedLink>(
            "SELECT GameId, ProviderId FROM game_title_link WHERE SourceGameId=@Id;", new { Id = id }).ConfigureAwait(false);
        await session.ExecuteAsync("DELETE FROM game WHERE Id=@Id;", new { Id = id }).ConfigureAwait(false);
        foreach (var link in links)
        {
            await ClearUnavailablePreferenceAsync(link.GameId, link.ProviderId).ConfigureAwait(false);
        }
        if (links.Any())
        {
            await ReapplyAsync(links.Select(link => link.GameId),
                await GetLibraryPreferenceAsync().ConfigureAwait(false), now).ConfigureAwait(false);
        }
    }

    private sealed record DeletedLink(string GameId, string ProviderId);

    private Task ClearUnavailablePreferenceAsync(string gameId, string provider) =>
        session.ExecuteAsync("UPDATE game_title_provenance SET Preference=NULL WHERE GameId=@GameId AND Preference=@Provider;",
            new { GameId = gameId, Provider = provider });

    public async Task<bool> ObserveAsync(Game game, string providerTitle, bool isNew, Guid libraryPreference, bool isFallback = false)
    {
        var providerId = game.PluginId.ToString("D");
        var existing = await GetAsync(game.Id).ConfigureAwait(false);
        var title = existing is null
            ? new TitleProvenance(providerId, game.GameId, providerTitle, isNew ? null : game.Name, game.Name, null)
            : existing with { ProviderTitle = isFallback ? existing.ProviderTitle : providerTitle };
        var linkedTitle = await ResolveLinkedTitleAsync(game.Id, title.ProviderId,
            title.Preference ?? libraryPreference.ToString("D")).ConfigureAwait(false);
        var name = await ApplyAsync(game.Id, game.Name, title, libraryPreference, linkedTitle).ConfigureAwait(false);
        var changed = !string.Equals(game.Name, name, StringComparison.Ordinal);
        game.Name = name;
        return changed;
    }

    public async Task<int> RefreshLinkedTitlesAsync(IReadOnlyCollection<Guid> sourceIds, Guid libraryPreference, DateTime now)
    {
        var dependents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in sourceIds.Chunk(500))
        {
            var ids = await session.QueryAsync<string>(
                "SELECT DISTINCT GameId FROM game_title_link WHERE SourceGameId IN @Ids;",
                new { Ids = batch.Select(id => id.ToString("D")).ToArray() }).ConfigureAwait(false);
            dependents.UnionWith(ids);
        }
        return await ReapplyAsync(dependents, libraryPreference, now).ConfigureAwait(false);
    }

    private async Task<int> ReapplyAsync(IEnumerable<string> ids, Guid preference, DateTime now)
    {
        var changed = 0;
        foreach (var id in ids)
        {
            var gameId = Guid.Parse(id);
            var game = await ReadTitleGameAsync(gameId).ConfigureAwait(false);
            var title = await GetAsync(gameId).ConfigureAwait(false);
            if (game is null || title is null)
            {
                continue;
            }

            var linked = await ResolveLinkedTitleAsync(gameId, title.ProviderId,
                title.Preference ?? preference.ToString("D")).ConfigureAwait(false);
            var name = await ApplyAsync(gameId, game.Name, title, preference, linked).ConfigureAwait(false);
            if (await UpdateGameNameAsync(game, name, now).ConfigureAwait(false))
            {
                changed++;
            }
        }
        return changed;
    }

    private async Task<string?> ResolveLinkedTitleAsync(Guid gameId, string owner, string preference)
    {
        if (preference == owner || preference == User)
        {
            return null;
        }
        return (await GetLinkedTitlesAsync(gameId).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.ProviderId == preference)?.ProviderTitle;
    }

    private async Task<string> ApplyAsync(Guid gameId, string? currentName, TitleProvenance title, Guid libraryPreference, string? linkedTitle)
    {
        if (!string.Equals(currentName, title.AppliedTitle, StringComparison.Ordinal))
        {
            title = title with { PersonalTitle = currentName, Preference = User };
        }

        var preference = title.Preference ?? libraryPreference.ToString("D");
        var name = preference == title.ProviderId ? title.ProviderTitle
            : (preference != User ? linkedTitle : null)
                ?? (string.IsNullOrWhiteSpace(title.PersonalTitle) ? title.ProviderTitle : title.PersonalTitle);
        await session.ExecuteAsync("""
            INSERT INTO game_title_provenance VALUES (@GameId, @ProviderId, @StoreGameId, @ProviderTitle, @PersonalTitle, @AppliedTitle, @Preference)
            ON CONFLICT(GameId) DO UPDATE SET ProviderId=excluded.ProviderId, StoreGameId=excluded.StoreGameId,
                ProviderTitle=excluded.ProviderTitle, PersonalTitle=excluded.PersonalTitle,
                AppliedTitle=excluded.AppliedTitle, Preference=excluded.Preference
            WHERE ProviderId IS NOT excluded.ProviderId OR StoreGameId IS NOT excluded.StoreGameId
                OR ProviderTitle IS NOT excluded.ProviderTitle OR PersonalTitle IS NOT excluded.PersonalTitle
                OR AppliedTitle IS NOT excluded.AppliedTitle OR Preference IS NOT excluded.Preference;
            """, new
        {
            GameId = gameId.ToString("D"), title.ProviderId, title.StoreGameId, title.ProviderTitle,
            title.PersonalTitle, AppliedTitle = name, title.Preference,
        }).ConfigureAwait(false);
        return name;
    }

    private sealed record TitleGame(string Id, string? Name);

    private Task<TitleGame?> ReadTitleGameAsync(Guid id) =>
        session.QuerySingleOrDefaultAsync<TitleGame>("SELECT Id, Name FROM game WHERE Id=@Id;", new { Id = id.ToString("D") });

    private async Task<bool> UpdateGameNameAsync(TitleGame game, string name, DateTime now)
    {
        if (name == game.Name)
        {
            return false;
        }

        await session.ExecuteAsync("UPDATE game SET Name=@Name, Modified=@Modified WHERE Id=@Id;",
            new { game.Id, Name = name, Modified = StorageValues.Timestamp(now) }).ConfigureAwait(false);
        return true;
    }
}

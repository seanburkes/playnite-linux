using Playnite.Core.Models;

namespace Playnite.Core.Database;

public sealed record StoreMetadataProvenance(string ProviderId, string StoreGameId, string? ImportedDescription, string? ImportedCover);

/// <summary>Tracks store-seeded metadata so later syncs can respect edits and removals.</summary>
public sealed class StoreMetadataRepository
{
    private readonly LibrarySession session;

    internal StoreMetadataRepository(LibrarySession session) => this.session = session;

    public Task<StoreMetadataProvenance?> GetAsync(Guid gameId) =>
        session.QuerySingleOrDefaultAsync<StoreMetadataProvenance>(
            "SELECT m.ProviderId, m.StoreGameId, m.ImportedDescription, m.ImportedCover FROM game_store_metadata m JOIN game g ON m.GameId=g.Id AND m.ProviderId=g.PluginId AND m.StoreGameId=g.GameId WHERE g.Id=@GameId;",
            new { GameId = gameId.ToString("D") });

    public async Task<bool> ObserveAsync(Game game, string? description, string? cover, bool isNew = false)
    {
        var providerId = game.PluginId.ToString("D");
        var previous = await GetAsync(game.Id).ConfigureAwait(false);
        var importedDescription = previous?.ImportedDescription;
        var importedCover = previous?.ImportedCover;
        var changed = false;
        if (!string.IsNullOrWhiteSpace(description) && (isNew || CanApply(game.Description, importedDescription)))
        {
            changed |= game.Description != description;
            game.Description = importedDescription = description;
        }

        if (!string.IsNullOrWhiteSpace(cover) && (isNew || CanApply(game.CoverImage, importedCover)))
        {
            changed |= game.CoverImage != cover;
            game.CoverImage = importedCover = cover;
        }

        if (importedDescription != previous?.ImportedDescription || importedCover != previous?.ImportedCover)
        {
            await session.ExecuteAsync("""
                INSERT INTO game_store_metadata VALUES (@GameId, @ProviderId, @StoreGameId, @ImportedDescription, @ImportedCover)
                ON CONFLICT(GameId) DO UPDATE SET ProviderId=excluded.ProviderId, StoreGameId=excluded.StoreGameId, ImportedDescription=excluded.ImportedDescription, ImportedCover=excluded.ImportedCover;
                """, new
            {
                GameId = game.Id.ToString("D"), ProviderId = providerId, StoreGameId = game.GameId, ImportedDescription = importedDescription,
                ImportedCover = importedCover,
            }).ConfigureAwait(false);
        }

        return changed;
    }

    private static bool CanApply(string? current, string? imported) =>
        imported is null ? string.IsNullOrWhiteSpace(current) : string.Equals(current, imported, StringComparison.Ordinal);
}

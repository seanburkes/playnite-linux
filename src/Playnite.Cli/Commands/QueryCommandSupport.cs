using System.Text.Json;
using Playnite.Core.Database;
using Playnite.Core.Library;

namespace Playnite.Cli;

/// <summary>Exit codes shared by the query commands. Program keeps 0 and 1 for dispatch.</summary>
internal static class QueryExitCodes
{
    internal const int Success = 0;
    internal const int InvalidArguments = 2;
    internal const int GameNotFound = 3;
    internal const int ServiceError = 4;
    internal const int NoPlayAction = 5;
}

/// <summary>Opening a library and writing the documented one-record-per-line output.</summary>
internal static class QueryCommandSupport
{
    internal static async Task<LibraryDatabase> OpenLibraryAsync(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Library database '{full}' was not found.", full);
        }

        return await LibraryDatabase.OpenAsync(full);
    }

    internal static void WritePage(TextWriter output, LibraryPage page, QueryOptions options)
    {
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "page",
            totalCount = page.TotalCount,
            offset = options.Offset,
            limit = options.Limit,
            returned = page.Games.Count
        }));

        foreach (var game in page.Games)
        {
            WriteGame(output, game);
        }
    }

    private static void WriteGame(TextWriter output, StoredGame stored)
    {
        var game = stored.Value;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "game",
            id = game.Id,
            gameId = game.GameId,
            name = game.Name,
            installed = game.IsInstalled,
            hidden = game.Hidden,
            favorite = game.Favorite,
            playtimeSeconds = game.Playtime,
            playCount = game.PlayCount,
            releaseYear = game.ReleaseDate?.Year
        }));
    }
}

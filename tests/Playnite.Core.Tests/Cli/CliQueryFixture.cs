using System.Text.Json;
using Playnite.Cli;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Cli;

internal static class CliQueryFixture
{
    internal static async Task<string> CreateLibraryAsync(DatabaseTestDirectory directory, params Game[] games)
    {
        var path = directory.PathFor();
        var database = await LibraryDatabase.OpenAsync(path, TestContext.Current.CancellationToken);
        await database.WriteAsync(async session =>
        {
            foreach (var game in games)
            {
                await session.Games.AddAsync(new StoredGame(game));
            }

            return 0;
        }, TestContext.Current.CancellationToken);
        return path;
    }

    internal static JsonElement[] ReadRecords(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    internal static Game NewGame(string name, Guid? id = null)
        => new(id ?? Guid.NewGuid(), name.ToLowerInvariant().Replace(' ', '-'), name);
}

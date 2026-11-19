using System.Diagnostics;
using System.Reflection;
using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Core.Tests.Database;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Library;

public sealed class LibraryServiceImportTests
{
    [Fact]
    public async Task Imported_games_are_listed_and_resolved_by_persistent_identity()
    {
        using var temporary = new DatabaseTestDirectory();
        var service = await ImportAsync(temporary.PathFor(), TestContext.Current.CancellationToken);

        var page = await service.ListAsync(new(SortBy: SortOrder.Playtime, Descending: true, Limit: 1));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal("220", Assert.Single(page.Games).Value.GameId);

        var stored = Assert.IsType<StoredGame>(await service.GetAsync(new("10000000-0000-0000-0000-000000000001")));
        Assert.Equal(3600UL, stored.Value.Playtime);
        Assert.Equal(new Guid("20000000-0000-0000-0000-000000000003"), Assert.Single(stored.Value.GenreIds));
        Assert.Contains("Synthetic Steam Installed", stored.LegacyJson);
        stored.Value.Name = "Detached edit";
        Assert.Equal("Synthetic Steam Installed", (await service.GetAsync(stored.Value.Id))?.Value.Name);
        Assert.Null(await service.GetAsync(new("10000000-0000-0000-0000-000000000004")));
    }

    [Fact]
    public async Task Imported_unicode_and_missing_release_years_flow_through_the_query_pipeline()
    {
        using var temporary = new DatabaseTestDirectory();
        var service = await ImportAsync(temporary.PathFor(), TestContext.Current.CancellationToken);

        var grouped = await service.SearchAsync("synthetic steam", new(GroupBy: LibraryGroupBy.ReleaseYear, Limit: 2));
        Assert.Equal(3, grouped.TotalCount);
        Assert.Equal(["400", "220"], grouped.Games.Select(game => game.Value.GameId));
        Assert.Collection(grouped.Groups,
            group => Assert.Equal(2019, Assert.IsType<int>(group.Key)),
            group => Assert.Null(group.Key));

        var page = await service.SearchAsync("雪", new(Filter: LibraryFilter.Parse("""{"Hidden":true}""")));
        var game = Assert.Single(page.Games).Value;
        Assert.Equal("Synthetic Steam Unicode — 雪", game.Name);
        Assert.Equal(new ReleaseDate(2019, 7), game.ReleaseDate);
    }

    private static async Task<LibraryService> ImportAsync(string destination, CancellationToken cancellationToken)
    {
        var configuration = typeof(LibraryServiceImportTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string Product(string name) => RepositoryPaths.Combine("src", name, "bin", configuration, "net11.0", name + ".dll");

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { Product("Playnite.Cli"), "import-v10", Product("Playnite.LegacyImport"),
            RepositoryPaths.Combine("tests", "fixtures", "v10", "library"), destination })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the library importer.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        Assert.True(process.ExitCode == 0, await errors);
        Assert.Contains("\"ImportedSteam\":3", await output);
        return new(await LibraryDatabase.OpenAsync(destination, cancellationToken));
    }
}

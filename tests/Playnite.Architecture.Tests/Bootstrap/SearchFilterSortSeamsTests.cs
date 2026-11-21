using System.Diagnostics;
using System.Reflection;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Desktop.Tests;

/// <summary>
/// p7-004 query seams over the moved, instance-owned LibraryViewModel and the real p6-001 service.
/// The protected fixture is only ever imported into a separate temporary destination.
/// </summary>
public sealed class SearchFilterSortSeamsTests
{
    [Fact]
    public async Task A_new_library_starts_with_the_default_all_name_ascending_query()
    {
        using var temp = new TemporaryLibrary();
        var model = new LibraryViewModel(Path.Combine(temp.Root, "playnite", "library.sqlite"));
        await model.LoadAsync();

        Assert.Equal("empty", model.Status);
        Assert.Equal(string.Empty, model.SearchTerm);
        Assert.Equal(LibraryFilterMode.All, model.FilterMode);
        Assert.Equal(SortOrder.Name, model.SortBy);
        Assert.False(model.Descending);
        Assert.Equal("Search:  | Filter: All | Sort: Name | Direction: asc", model.QueryStatus);
    }

    [Fact]
    public async Task A_trimmed_v10_search_term_narrows_rows_and_a_blank_term_restores_them()
    {
        using var temp = await SeededLibraryAsync(
            new Seed("game-1", "Alpha Game"),
            new Seed("game-2", "Bravo Game"),
            new Seed("game-3", "Charlie Game"));
        var model = new LibraryViewModel(temp.LibraryPath);
        await model.LoadAsync();
        Assert.Equal(3, model.Rows.Count);

        await model.SetSearchTermAsync("  Bravo  ");
        Assert.Equal("Bravo", model.SearchTerm);
        Assert.Equal(["Bravo Game"], model.Rows.Select(row => row.Name));
        Assert.Equal(1, model.TotalCount);

        await model.SetSearchTermAsync("   ");
        Assert.Equal(string.Empty, model.SearchTerm);
        Assert.Equal(3, model.Rows.Count);
    }

    [Fact]
    public async Task Search_uses_the_v10_acronym_and_word_matchers()
    {
        using var temp = await SeededLibraryAsync(
            new Seed("game-1", "Bravo Game"),
            new Seed("game-2", "Charlie Game"));
        var model = new LibraryViewModel(temp.LibraryPath);
        await model.LoadAsync();

        await model.SetSearchTermAsync("BG");
        Assert.Equal(["Bravo Game"], model.Rows.Select(row => row.Name));

        await model.SetSearchTermAsync("game");
        Assert.Equal(["Bravo Game", "Charlie Game"], model.Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task Each_bounded_quick_filter_selects_its_own_category()
    {
        using var temp = await SeededLibraryAsync(
            new Seed("game-1", "Alpha Installed", Installed: true),
            new Seed("game-2", "Bravo Uninstalled"),
            new Seed("game-3", "Charlie Hidden", Hidden: true),
            new Seed("game-4", "Delta Favorite", Installed: true, Favorite: true));
        var model = new LibraryViewModel(temp.LibraryPath);
        await model.LoadAsync();

        await model.SetFilterAsync(LibraryFilterMode.Installed);
        Assert.Equal(["Alpha Installed", "Delta Favorite"], model.Rows.Select(row => row.Name));

        await model.SetFilterAsync(LibraryFilterMode.Uninstalled);
        Assert.Equal(["Bravo Uninstalled"], model.Rows.Select(row => row.Name));

        await model.SetFilterAsync(LibraryFilterMode.Hidden);
        Assert.Equal(["Charlie Hidden"], model.Rows.Select(row => row.Name));

        await model.SetFilterAsync(LibraryFilterMode.Favorite);
        Assert.Equal(["Delta Favorite"], model.Rows.Select(row => row.Name));

        await model.SetFilterAsync(LibraryFilterMode.All);
        Assert.Equal(4, model.Rows.Count);
    }

    [Fact]
    public async Task Selected_sort_key_and_direction_produce_stable_p6_012_orders()
    {
        using var temp = await SeededLibraryAsync(
            new Seed("game-1", "Low", Score: 10),
            new Seed("game-2", "High", Score: 30),
            new Seed("game-3", "Middle", Score: 20));
        var model = new LibraryViewModel(temp.LibraryPath);
        await model.LoadAsync();

        await model.SetSortAsync(SortOrder.UserScore, descending: false);
        Assert.Equal(["Low", "Middle", "High"], model.Rows.Select(row => row.Name));
        var ascending = model.Rows.Select(row => row.Id).ToArray();
        await model.ApplyQueryAsync();
        Assert.Equal(ascending, model.Rows.Select(row => row.Id).ToArray());

        await model.SetDescendingAsync(descending: true);
        Assert.Equal(["High", "Middle", "Low"], model.Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task Changing_a_query_resets_paging_to_one_bounded_stable_first_page()
    {
        using var temp = new TemporaryLibrary();
        var path = temp.LibraryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = await LibraryDatabase.OpenAsync(path);
        for (var number = 1; number <= 60; number++)
        {
            var id = new Guid($"10000000-0000-0000-0000-{number:X12}");
            var game = new Game(id, "game-" + number.ToString("D3"), $"Seeded Game {number:D3}")
            {
                UserScore = number,
            };
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(game));
                return true;
            });
        }

        var model = new LibraryViewModel(path);
        await model.LoadAsync();
        Assert.Equal("Page 1 / 2", model.PageIndicator);
        await model.NextPageAsync();
        Assert.Equal("Page 2 / 2", model.PageIndicator);
        var secondPageFirst = model.Rows[0].Name;

        await model.SetSearchTermAsync("Seeded");
        Assert.Equal("Page 1 / 2", model.PageIndicator);
        Assert.Equal(50, model.Rows.Count);
        Assert.Equal("Seeded Game 001", model.Rows[0].Name);
        Assert.DoesNotContain(model.Rows, row => row.Name == secondPageFirst);

        var firstPageIds = model.Rows.Select(row => row.Id).ToArray();
        await model.SetSortAsync(SortOrder.UserScore, descending: true);
        Assert.Equal("Page 1 / 2", model.PageIndicator);
        Assert.Equal(50, model.Rows.Count);
        Assert.Equal("Seeded Game 060", model.Rows[0].Name);
        Assert.DoesNotContain(model.Rows, row => row.Name == "Seeded Game 001");

        var sortedIds = model.Rows.Select(row => row.Id).ToArray();
        await model.ApplyQueryAsync();
        Assert.Equal(sortedIds, model.Rows.Select(row => row.Id).ToArray());
        Assert.NotEqual(firstPageIds, sortedIds);
    }

    [Fact]
    public void The_moved_view_model_is_instance_owned_without_a_service_locator()
    {
        var constructor = Assert.Single(typeof(LibraryViewModel).GetConstructors());
        Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(string), constructor.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IServiceProvider));
    }

    [Fact]
    public void The_moved_types_keep_the_shell_namespace_and_the_p7_003_control_names()
    {
        Assert.Equal("Playnite.Desktop.Shell", typeof(LibraryViewModel).Namespace);
        Assert.Equal("Playnite.Desktop.Shell", typeof(LibraryGridView).Namespace);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        var xaml = File.ReadAllText(Path.Combine(
            root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"),
            "src", "Playnite.Desktop", "p7-004", "Shell", "LibraryGridView.axaml"));
        Assert.Contains("x:Class=\"Playnite.Desktop.Shell.LibraryGridView\"", xaml, StringComparison.Ordinal);
        foreach (var name in new[] { "GameList", "DetailsName", "DetailsId", "PageText", "PreviousButton", "NextButton" })
        {
            Assert.Contains("x:Name=\"" + name + "\"", xaml, StringComparison.Ordinal);
        }
    }

    private static async Task<TemporaryLibrary> SeededLibraryAsync(params Seed[] seeds)
    {
        var temp = new TemporaryLibrary();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.LibraryPath)!);
        var database = await LibraryDatabase.OpenAsync(temp.LibraryPath);
        for (var index = 0; index < seeds.Length; index++)
        {
            var seed = seeds[index];
            var id = new Guid($"20000000-0000-0000-0000-{index + 1:X12}");
            var game = new Game(id, seed.GameId, seed.Name)
            {
                IsInstalled = seed.Installed,
                Hidden = seed.Hidden,
                Favorite = seed.Favorite,
                UserScore = seed.Score,
            };
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(game));
                return true;
            });
        }

        return temp;
    }

    private sealed record Seed(string GameId, string Name, bool Installed = false, bool Hidden = false,
        bool Favorite = false, int Score = 0);

    private sealed class TemporaryLibrary : IDisposable
    {
        internal TemporaryLibrary() => Root = Path.Combine(Path.GetTempPath(), "playnite-p7-004-" + Path.GetRandomFileName());

        internal string Root { get; }
        internal string LibraryPath => Path.Combine(Root, "playnite", "library.sqlite");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

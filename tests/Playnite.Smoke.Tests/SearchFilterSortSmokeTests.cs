using System.Diagnostics;
using System.Reflection;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence for the p7-004 search/filter/sort query bar over the real host.</summary>
public sealed class SearchFilterSortSmokeTests
{
    [Fact]
    public async Task Headless_query_controls_search_filter_sort_and_reset_pages_in_an_isolated_profile()
    {
        using var profile = new IsolatedProfile();
        await SeedSixGamesAsync(profile.LibraryPath);
        var result = await RunDesktopAsync(profile);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains("search-filter-sort: initial=rows:Alpha Game,Bravo Game,Charlie Game,Delta Game,Echo Hidden,Foxtrot Game;total:6;page:Page 1 / 1;state:Search:  | Filter: All | Sort: Name | Direction: asc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: search=rows:Bravo Game;total:1;page:Page 1 / 1;state:Search: Bravo | Filter: All | Sort: Name | Direction: asc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: filter=rows:Bravo Game,Delta Game;total:2;page:Page 1 / 1;state:Search:  | Filter: Favorite | Sort: Name | Direction: asc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: sort=rows:Delta Game,Bravo Game;total:2;page:Page 1 / 1;state:Search:  | Filter: Favorite | Sort: UserScore | Direction: asc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: sort-desc=rows:Bravo Game,Delta Game;total:2;page:Page 1 / 1;state:Search:  | Filter: Favorite | Sort: UserScore | Direction: desc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: reset=rows:Alpha Game,Bravo Game,Charlie Game,Delta Game,Echo Hidden,Foxtrot Game;total:6;page:Page 1 / 1;state:Search:  | Filter: All | Sort: Name | Direction: asc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: supersede=rows:Charlie Game;total:1;page:Page 1 / 1;state:Search: Alpha | Filter: All | Sort: Name | Direction: asc;semantic:True;valid:true", lines);
        Assert.DoesNotContain("query: failed=", result.Output);
        Assert.Contains("search-filter-sort: controls-loaded", lines);
        profile.AssertWritesStayIsolated();
    }

    [Fact]
    public async Task Headless_empty_library_still_exposes_the_query_controls()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains("search-filter-sort: initial=rows:;total:0;page:Page 1 / 1;state:Search:  | Filter: All | Sort: Name | Direction: asc;semantic:True;valid:true", lines);
        Assert.Contains("search-filter-sort: controls-loaded", lines);
        profile.AssertWritesStayIsolated();
    }

    private static void AssertHost((int ExitCode, string Output, string Error) result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("Window opened: Playnite", Lines(result.Output));
    }

    private static string[] Lines(string output) => output.Split(
        '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task SeedSixGamesAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = await LibraryDatabase.OpenAsync(path);
        var seeds = new (string Name, bool Installed, bool Hidden, bool Favorite, int Score)[]
        {
            ("Alpha Game", true, false, false, 10),
            ("Bravo Game", true, false, true, 80),
            ("Charlie Game", false, false, false, 30),
            ("Delta Game", false, false, true, 60),
            ("Echo Hidden", true, true, false, 50),
            ("Foxtrot Game", false, false, false, 90),
        };
        for (var index = 0; index < seeds.Length; index++)
        {
            var seed = seeds[index];
            var id = new Guid($"30000000-0000-0000-0000-{index + 1:X12}");
            var game = new Game(id, "game-" + (index + 1).ToString("D3"), seed.Name)
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
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDesktopAsync(IsolatedProfile profile)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(DesktopHostPath());
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--smoke");
        start.ArgumentList.Add("--search-filter-sort-smoke");

        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment["XDG_CONFIG_HOME"] = profile.Config;
        start.Environment["XDG_STATE_HOME"] = profile.State;
        start.Environment["XDG_DATA_HOME"] = profile.Data;
        start.Environment["XDG_CACHE_HOME"] = profile.Cache;
        start.Environment["XDG_RUNTIME_DIR"] = profile.Runtime;
        start.Environment["HOME"] = profile.Home;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"Desktop search/filter/sort smoke did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
        }

        return (process.ExitCode, await output, await error);
    }

    private static string DesktopHostPath() =>
        RepositoryPath("src", "Playnite.Desktop", "bin",
            typeof(SearchFilterSortSmokeTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "BuildConfiguration").Value!,
            "net11.0", "Playnite.Desktop.dll");

    private static string RepositoryPath(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        return Path.Combine([root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"), .. segments]);
    }

    private sealed class IsolatedProfile : IDisposable
    {
        internal IsolatedProfile()
        {
            Root = Path.Combine(Path.GetTempPath(), "playnite-p7-004-smoke-" + Path.GetRandomFileName());
            Config = Path.Combine(Root, "config");
            State = Path.Combine(Root, "state");
            Data = Path.Combine(Root, "data");
            Cache = Path.Combine(Root, "cache");
            Runtime = Path.Combine(Root, "runtime");
            Home = Path.Combine(Root, "home");
            LibraryPath = Path.Combine(Data, "playnite", "library.sqlite");
            foreach (var directory in new[] { Config, State, Data, Cache, Runtime, Home })
            {
                Directory.CreateDirectory(directory);
            }
        }

        internal string Root { get; }
        internal string Config { get; }
        internal string State { get; }
        internal string Data { get; }
        internal string Cache { get; }
        internal string Runtime { get; }
        internal string Home { get; }
        internal string LibraryPath { get; }

        internal void AssertWritesStayIsolated()
        {
            var written = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToArray();
            Assert.NotEmpty(written);
            Assert.All(written, path => Assert.StartsWith(Root + Path.DirectorySeparatorChar, Path.GetFullPath(path)));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

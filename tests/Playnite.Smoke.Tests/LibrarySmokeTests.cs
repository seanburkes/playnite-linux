using System.Diagnostics;
using System.Reflection;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence for the p7-003 library grid over the real host.</summary>
public sealed class LibrarySmokeTests
{
    private const string FirstFixtureGame = "Synthetic Steam Installed";
    private const string ThirdFixtureGame = "Synthetic Steam Uninstalled";

    [Fact]
    public async Task Headless_empty_library_shows_an_empty_grid_in_an_isolated_profile()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile, seed: null);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains("library: page1=count:0;total:0;page:Page 1 / 1;first:<missing>;prev:false;next:false;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: grid-loaded", lines);
        profile.AssertWritesStayIsolated();
    }

    [Fact]
    public async Task Headless_fixture_imported_library_selects_a_non_first_row_and_updates_details()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile, seed: ProfileSeed.Fixture);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains("library: page1=count:3;total:3;page:Page 1 / 1;first:" + FirstFixtureGame + ";prev:false;next:false;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: selection=index:2;row:" + ThirdFixtureGame + ";details:" + ThirdFixtureGame + "|570;semantic:" + ThirdFixtureGame + "|Details name|Details id;valid:true", lines);
        Assert.Contains("library: grid-loaded", lines);
        profile.AssertWritesStayIsolated();
    }

    [Fact]
    public async Task Headless_paged_library_navigates_selects_and_refuses_out_of_range_pages()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile, seed: ProfileSeed.SixtyGames);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains("library: page1=count:50;total:60;page:Page 1 / 2;first:Seeded Game 001;prev:false;next:true;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: selection=index:2;row:Seeded Game 003;details:Seeded Game 003|game-003;semantic:Seeded Game 003|Details name|Details id;valid:true", lines);
        Assert.Contains("library: next-result=count:10;total:60;page:Page 2 / 2;first:Seeded Game 051;prev:true;next:false;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: next-at-last=count:10;total:60;page:Page 2 / 2;first:Seeded Game 051;prev:true;next:false;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: back=count:50;total:60;page:Page 1 / 2;first:Seeded Game 001;prev:false;next:true;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: prev-at-first=count:50;total:60;page:Page 1 / 2;first:Seeded Game 001;prev:false;next:true;semantic:Page indicator;valid:true", lines);
        Assert.Contains("library: grid-loaded", lines);
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

    private static async Task<(int ExitCode, string Output, string Error)> RunDesktopAsync(
        IsolatedProfile profile, ProfileSeed? seed)
    {
        if (seed is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile.LibraryPath)!);
            switch (seed)
            {
                case ProfileSeed.Fixture:
                    await ImportFixtureAsync(profile.LibraryPath);
                    break;
                case ProfileSeed.SixtyGames:
                    await SeedGamesAsync(profile.LibraryPath, 60);
                    break;
            }
        }

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(DesktopHostPath());
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--smoke");
        start.ArgumentList.Add("--library-smoke");

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
                $"Desktop library smoke did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
        }

        return (process.ExitCode, await output, await error);
    }

    private static async Task SeedGamesAsync(string path, int count)
    {
        var database = await LibraryDatabase.OpenAsync(path);
        for (var number = 1; number <= count; number++)
        {
            var id = new Guid($"10000000-0000-0000-0000-{number:X12}");
            var game = new Game(id, "game-" + number.ToString("D3"), $"Seeded Game {number:D3}");
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(game));
                return true;
            });
        }
    }

    private static async Task ImportFixtureAsync(string destination)
    {
        var configuration = typeof(LibrarySmokeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BuildConfiguration").Value!;
        string Product(string name) => RepositoryPath("src", name, "bin", configuration, "net11.0", name + ".dll");

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     Product("Playnite.Cli"), "import-v10", Product("Playnite.LegacyImport"),
                     RepositoryPath("tests", "fixtures", "v10", "library"), destination,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start the library importer.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        Assert.True(process.ExitCode == 0, await errors);
        Assert.Contains("\"ImportedSteam\":3", await output);
    }

    private static string DesktopHostPath() =>
        RepositoryPath("src", "Playnite.Desktop", "bin",
            typeof(LibrarySmokeTests).Assembly
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

    private enum ProfileSeed
    {
        Fixture,
        SixtyGames,
    }

    private sealed class IsolatedProfile : IDisposable
    {
        internal IsolatedProfile()
        {
            Root = Path.Combine(Path.GetTempPath(), "playnite-p7-003-smoke-" + Path.GetRandomFileName());
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

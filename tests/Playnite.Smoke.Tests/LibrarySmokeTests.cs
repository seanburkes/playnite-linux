using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence for the p7-003 library grid over the real host.</summary>
public sealed class LibrarySmokeTests
{
    private const string FirstFixtureGame = "Synthetic Steam Installed";
    private const string FixtureGameId = "220";
    private const string EmptyMarker =
        "library: status=empty;count=0;total=0;first=<missing>;details=(none)|(none);"
        + "semantic=Library header|<missing>|Details name|Details id;valid=true";
    private const string LoadedMarkerPrefix =
        "library: status=loaded;count=3;total=3;first=" + FirstFixtureGame + ";details=";
    private const string LoadedSemantic =
        "semantic=Library header|Synthetic Steam Installed|Details name|Details id;valid=true";

    [Fact]
    public async Task Headless_empty_library_shows_an_empty_grid_in_an_isolated_profile()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile, seedFixture: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        var lines = Lines(result.Output);
        Assert.Contains("Window opened: Playnite", lines);
        Assert.Contains(EmptyMarker, lines);
        Assert.Contains("library: grid-loaded", lines);
        profile.AssertWritesStayIsolated();
    }

    [Fact]
    public async Task Headless_fixture_imported_library_shows_rows_details_and_stable_first_page()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile, seedFixture: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        var lines = Lines(result.Output);
        Assert.Contains("Window opened: Playnite", lines);
        Assert.Contains(lines, line => line.StartsWith(LoadedMarkerPrefix, StringComparison.Ordinal)
            && line.Contains(FirstFixtureGame + "|" + FixtureGameId, StringComparison.Ordinal)
            && line.EndsWith(LoadedSemantic, StringComparison.Ordinal));
        Assert.Contains("library: grid-loaded", lines);
        profile.AssertWritesStayIsolated();
    }

    private static string[] Lines(string output) => output.Split(
        '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<(int ExitCode, string Output, string Error)> RunDesktopAsync(
        IsolatedProfile profile, bool seedFixture)
    {
        if (seedFixture)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile.LibraryPath)!);
            await ImportFixtureAsync(profile.LibraryPath);
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

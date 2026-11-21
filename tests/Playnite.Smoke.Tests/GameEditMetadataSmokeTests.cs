using System.Diagnostics;
using System.Reflection;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence for the p7-005 game edit/metadata view over the real host.</summary>
public sealed class GameEditMetadataSmokeTests
{
    private static readonly Guid GameId = Guid.Parse("87654321-abcd-4abc-9def-123456789abc");

    [Fact]
    public async Task Headless_edit_controls_load_save_and_reread_in_an_isolated_profile()
    {
        using var profile = new IsolatedProfile();
        await SeedOneGameAsync(profile.LibraryPath);
        var result = await RunDesktopAsync(profile);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains(
            "game-edit: loaded=id:game-001;status:loaded;metadata:Release=2020; Platforms=0; Tags=0; Developers=0;semantic:true;valid:true",
            lines);
        Assert.Contains(
            "game-edit: saved=id:game-001;name:Edited Smoke Game;stored:Edited Smoke Game;score:77;detail:Edited Smoke Game;valid:true",
            lines);
        Assert.Contains("game-edit: controls-loaded", lines);
        Assert.DoesNotContain("game-edit: save-failed=", result.Output);
        Assert.DoesNotContain("game-edit: load-failed=", result.Output);
        profile.AssertWritesStayIsolated();
    }

    [Fact]
    public async Task Headless_empty_library_still_exposes_the_edit_controls()
    {
        using var profile = new IsolatedProfile();
        var result = await RunDesktopAsync(profile);

        AssertHost(result);
        var lines = Lines(result.Output);
        Assert.Contains("game-edit: empty=rows:0;status:empty;semantic:true;valid:true", lines);
        Assert.Contains("game-edit: controls-loaded", lines);
        Assert.DoesNotContain("game-edit: save-failed=", result.Output);
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

    private static async Task SeedOneGameAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = await LibraryDatabase.OpenAsync(path);
        var game = new Game(GameId, "game-001", "Alpha Game")
        {
            Description = "original description",
            Version = "1.0",
            InstallDirectory = "/games/alpha",
            IsInstalled = true,
            UserScore = 55,
            ReleaseDate = new ReleaseDate(2020),
        };
        await database.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new StoredGame(game));
            return true;
        });
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
        start.ArgumentList.Add("--game-edit-smoke");

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
                $"Desktop game-edit smoke did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
        }

        return (process.ExitCode, await output, await error);
    }

    private static string DesktopHostPath() =>
        RepositoryPath("src", "Playnite.Desktop", "bin",
            typeof(GameEditMetadataSmokeTests).Assembly
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
            Root = Path.Combine(Path.GetTempPath(), "playnite-p7-005-smoke-" + Path.GetRandomFileName());
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

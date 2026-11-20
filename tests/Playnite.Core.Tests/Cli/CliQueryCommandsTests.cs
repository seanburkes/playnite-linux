using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Playnite.Cli;
using Playnite.Core.Models;
using Playnite.Core.Tests.Database;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class CliQueryCommandsTests
{
    [Fact]
    public async Task Discovered_query_commands_run_end_to_end()
    {
        using var directory = new DatabaseTestDirectory();
        var executable = Path.Combine(directory.DirectoryPath, "game.sh");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\n");
        var alpha = CliQueryFixture.NewGame("Alpha");
        alpha.IsInstalled = true;
        alpha.GameActions = new ObservableCollection<GameAction>
        {
            new() { Name = "Play", Type = GameActionType.File, Path = executable, IsPlayAction = true }
        };
        var path = await CliQueryFixture.CreateLibraryAsync(directory, alpha, CliQueryFixture.NewGame("Beta"));

        var list = await RunCli("list", "--library", path, "--limit", "1");
        Assert.Equal(0, list.ExitCode);
        Assert.Equal("", list.Error);
        var listRecords = CliQueryFixture.ReadRecords(list.Output);
        Assert.Equal(2, listRecords.Length);
        Assert.Equal("page", listRecords[0].GetProperty("kind").GetString());
        Assert.Equal(2, listRecords[0].GetProperty("totalCount").GetInt32());
        Assert.Equal("Alpha", listRecords[1].GetProperty("name").GetString());

        var search = await RunCli("search", "beta", "--library", path);
        Assert.Equal(0, search.ExitCode);
        Assert.Equal("", search.Error);
        var searchRecords = CliQueryFixture.ReadRecords(search.Output);
        Assert.Equal(2, searchRecords.Length);
        Assert.Equal(1, searchRecords[0].GetProperty("totalCount").GetInt32());
        Assert.Equal("Beta", searchRecords[1].GetProperty("name").GetString());

        var launch = await RunCli("launch", "--dry-run", "--library", path, "--name", "Alpha");
        Assert.Equal(0, launch.ExitCode);
        Assert.Equal("", launch.Error);
        var launchRecord = Assert.Single(CliQueryFixture.ReadRecords(launch.Output));
        Assert.True(launchRecord.GetProperty("dryRun").GetBoolean());
        Assert.False(launchRecord.GetProperty("started").GetBoolean());
        Assert.Equal(executable, launchRecord.GetProperty("path").GetString());
        Assert.True(launchRecord.GetProperty("fileExists").GetBoolean());
    }

    [Fact]
    public async Task Unknown_verb_and_bad_arguments_keep_the_documented_exit_codes()
    {
        using var directory = new DatabaseTestDirectory();
        var alpha = CliQueryFixture.NewGame("Alpha");
        alpha.IncludeLibraryPluginAction = false;
        var path = await CliQueryFixture.CreateLibraryAsync(directory, alpha);

        var unknown = await RunCli("frobnicate");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Equal("", unknown.Output);
        Assert.Contains("Unknown command 'frobnicate'.", unknown.Error);

        var badLimit = await RunCli("list", "--library", path, "--limit", "0");
        Assert.Equal(2, badLimit.ExitCode);
        Assert.Equal("", badLimit.Output);
        Assert.Contains("--limit must be an integer from 1 to 10000", badLimit.Error);

        var missingTerm = await RunCli("search", "--library", path);
        Assert.Equal(2, missingTerm.ExitCode);
        Assert.Contains("Usage: Playnite.Cli search", missingTerm.Error);

        var missingDryRun = await RunCli("launch", "--library", path, "--name", "Alpha");
        Assert.Equal(2, missingDryRun.ExitCode);
        Assert.Contains("launch requires --dry-run", missingDryRun.Error);

        var unknownGame = await RunCli("launch", "--dry-run", "--library", path, "--name", "Absent");
        Assert.Equal(3, unknownGame.ExitCode);
        Assert.Contains("No game named 'Absent' was found", unknownGame.Error);

        var noPlayAction = await RunCli("launch", "--dry-run", "--library", path, "--name", "Alpha");
        Assert.Equal(5, noPlayAction.ExitCode);
        Assert.Contains("has no play action", noPlayAction.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(params string[] args)
    {
        var configuration = typeof(CliQueryCommandsTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(RepositoryPaths.Combine("src", "Playnite.Cli", "bin", configuration, "net11.0", "Playnite.Cli.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }
}

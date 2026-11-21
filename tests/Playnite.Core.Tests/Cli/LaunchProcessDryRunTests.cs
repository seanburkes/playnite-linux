using System.Collections.ObjectModel;
using System.Diagnostics;
using Playnite.Cli;
using Playnite.Core.Models;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class LaunchProcessDryRunTests
{
    [Fact]
    public async Task Launch_dry_run_resolves_by_id_out_of_process_without_starting_the_game()
    {
        using var directory = new DatabaseTestDirectory();
        var sentinel = Path.Combine(directory.DirectoryPath, "sentinel.txt");
        var executable = Path.Combine(directory.DirectoryPath, "game.sh");
        await File.WriteAllTextAsync(executable,
            $"#!/bin/sh\nprintf started > '{sentinel}'\nexit 0\n");

        var game = CliQueryFixture.NewGame("Sentinel Game");
        game.IsInstalled = true;
        game.GameActions = new ObservableCollection<GameAction>
        {
            new()
            {
                Name = "Play",
                Type = GameActionType.File,
                Path = executable,
                Arguments = "--fullscreen",
                WorkingDir = directory.DirectoryPath,
                IsPlayAction = true
            }
        };
        var library = await CliQueryFixture.CreateLibraryAsync(directory, game);

        var cliAssembly = typeof(LaunchDryRunCommand).Assembly.Location;
        var cliOutputDirectory = Path.GetDirectoryName(cliAssembly)!;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cliAssembly);
        start.ArgumentList.Add("launch");
        start.ArgumentList.Add("--dry-run");
        start.ArgumentList.Add("--library");
        start.ArgumentList.Add(library);
        start.ArgumentList.Add("--id");
        start.ArgumentList.Add(game.Id.ToString());

        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", error);
        var record = Assert.Single(CliQueryFixture.ReadRecords(output));
        Assert.Equal("launch", record.GetProperty("kind").GetString());
        Assert.True(record.GetProperty("dryRun").GetBoolean());
        Assert.False(record.GetProperty("started").GetBoolean());
        Assert.Equal("File", record.GetProperty("actionType").GetString());
        Assert.Equal(executable, record.GetProperty("path").GetString());
        Assert.Equal("--fullscreen", record.GetProperty("arguments").GetString());
        Assert.False(File.Exists(sentinel), "the dry run resolved the game but must not execute its play action");

        var uiAssemblies = Directory.EnumerateFiles(cliOutputDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(uiAssemblies);
    }
}

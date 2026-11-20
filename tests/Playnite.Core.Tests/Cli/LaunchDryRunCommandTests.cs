using System.Collections.ObjectModel;
using Playnite.Cli;
using Playnite.Core.Models;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class LaunchDryRunCommandTests
{
    [Fact]
    public async Task Launch_resolves_a_file_play_action_by_id_without_starting_it()
    {
        using var directory = new DatabaseTestDirectory();
        var executable = Path.Combine(directory.DirectoryPath, "game.sh");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\n");
        var game = GameWithAction("Play Me", new GameAction
        {
            Name = "Play",
            Type = GameActionType.File,
            Path = executable,
            Arguments = "--fullscreen",
            WorkingDir = directory.DirectoryPath,
            IsPlayAction = true
        });
        var path = await CliQueryFixture.CreateLibraryAsync(directory, game);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--id", game.Id.ToString()], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var record = Assert.Single(CliQueryFixture.ReadRecords(output.ToString()));
        Assert.Equal("launch", record.GetProperty("kind").GetString());
        Assert.True(record.GetProperty("dryRun").GetBoolean());
        Assert.False(record.GetProperty("started").GetBoolean());
        Assert.Equal("Play Me", record.GetProperty("name").GetString());
        Assert.Equal("File", record.GetProperty("actionType").GetString());
        Assert.Equal("Play", record.GetProperty("actionName").GetString());
        Assert.Equal(executable, record.GetProperty("path").GetString());
        Assert.Equal("--fullscreen", record.GetProperty("arguments").GetString());
        Assert.Equal(directory.DirectoryPath, record.GetProperty("workingDirectory").GetString());
        Assert.True(record.GetProperty("fileExists").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, record.GetProperty("script").ValueKind);
    }

    [Fact]
    public async Task Launch_resolves_an_exact_display_name()
    {
        using var directory = new DatabaseTestDirectory();
        var game = GameWithAction("Exact Name", new GameAction
        {
            Name = "Play",
            Type = GameActionType.URL,
            Path = "https://example.invalid/game",
            IsPlayAction = true
        });
        var path = await CliQueryFixture.CreateLibraryAsync(directory, game,
            GameWithAction("Exact Name Extra", new GameAction { Type = GameActionType.File, Path = "/tmp/other", IsPlayAction = true }));

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "exact name"], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var record = Assert.Single(CliQueryFixture.ReadRecords(output.ToString()));
        Assert.Equal("URL", record.GetProperty("actionType").GetString());
        Assert.Equal("https://example.invalid/game", record.GetProperty("path").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, record.GetProperty("fileExists").ValueKind);
    }

    [Fact]
    public async Task Launch_reports_a_missing_executable_without_failing()
    {
        using var directory = new DatabaseTestDirectory();
        var game = GameWithAction("Missing Exe", new GameAction
        {
            Type = GameActionType.File,
            Path = Path.Combine(directory.DirectoryPath, "absent.exe"),
            IsPlayAction = true
        });
        var path = await CliQueryFixture.CreateLibraryAsync(directory, game);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--id", game.Id.ToString()], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var record = Assert.Single(CliQueryFixture.ReadRecords(output.ToString()));
        Assert.False(record.GetProperty("fileExists").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task Launch_rejects_invalid_arguments(string[] args, string expected)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LaunchDryRunCommand.ExecuteAsync(args, output, error);

        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains(expected, error.ToString());
    }

    public static TheoryData<string[], string> BadArguments => new()
    {
        { ["--library", "/tmp/library.sqlite", "--name", "Game"], "launch requires --dry-run" },
        { ["--dry-run", "--library", "/tmp/library.sqlite"], "Choose exactly one of --id and --name" },
        { ["--dry-run", "--library", "/tmp/library.sqlite", "--id", Guid.Empty.ToString(), "--name", "Game"], "Choose exactly one of --id and --name" },
        { ["--dry-run", "--library", "/tmp/library.sqlite", "--id", "not-a-guid"], "--id must be a GUID" },
        { ["--dry-run", "--library", "/tmp/library.sqlite", "--id", Guid.Empty.ToString(), "--id", Guid.Empty.ToString()], "--id was specified more than once" },
        { ["--dry-run", "--library", "/tmp/library.sqlite", "--name", "Game", "--name", "Other"], "--name was specified more than once" },
        { ["--dry-run", "--library", "/tmp/library.sqlite", "--library", "/tmp/other.sqlite", "--name", "Game"], "--library was specified more than once" },
        { ["--dry-run", "--dry-run", "--library", "/tmp/library.sqlite", "--name", "Game"], "--dry-run was specified more than once" },
        { ["--dry-run", "--library", "/tmp/library.sqlite", "--name", "Game", "extra"], "Usage: Playnite.Cli launch" },
        { ["--dry-run"], "Usage: Playnite.Cli launch" }
    };

    [Fact]
    public async Task Launch_reports_an_unknown_game_or_id()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CliQueryFixture.CreateLibraryAsync(directory, CliQueryFixture.NewGame("Known"));

        var byIdError = new StringWriter();
        Assert.Equal(3, await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--id", Guid.NewGuid().ToString()], new StringWriter(), byIdError));
        Assert.Contains("No game with id", byIdError.ToString());

        var byNameError = new StringWriter();
        Assert.Equal(3, await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "Unknown"], new StringWriter(), byNameError));
        Assert.Contains("No game named 'Unknown' was found", byNameError.ToString());
    }

    [Fact]
    public async Task Launch_rejects_an_ambiguous_exact_name()
    {
        using var directory = new DatabaseTestDirectory();
        var first = GameWithAction("Same Name", new GameAction { Type = GameActionType.File, Path = "/tmp/first", IsPlayAction = true });
        var second = GameWithAction("Same Name", new GameAction { Type = GameActionType.File, Path = "/tmp/second", IsPlayAction = true });
        var path = await CliQueryFixture.CreateLibraryAsync(directory, first, second);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "Same Name"], output, error);

        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("matches 2 games; use --id", error.ToString());
    }

    [Fact]
    public async Task Launch_reports_a_game_without_a_supported_play_action()
    {
        using var directory = new DatabaseTestDirectory();
        var noAction = CliQueryFixture.NewGame("No Action");
        noAction.IncludeLibraryPluginAction = false;
        var pluginAction = CliQueryFixture.NewGame("Plugin Action");
        var emulatorOnly = GameWithAction("Emulated", new GameAction
        {
            Type = GameActionType.Emulator,
            EmulatorId = Guid.NewGuid(),
            IsPlayAction = true
        });
        var path = await CliQueryFixture.CreateLibraryAsync(directory, noAction, pluginAction, emulatorOnly);

        var missing = new StringWriter();
        Assert.Equal(5, await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "No Action"], new StringWriter(), missing));
        Assert.Contains("has no play action", missing.ToString());

        var plugin = new StringWriter();
        Assert.Equal(5, await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "Plugin Action"], new StringWriter(), plugin));
        Assert.Contains("library plugin action", plugin.ToString());

        var emulator = new StringWriter();
        Assert.Equal(5, await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "Emulated"], new StringWriter(), emulator));
        Assert.Contains("only Emulator play actions", emulator.ToString());
    }

    [Fact]
    public async Task Launch_reports_a_missing_library_as_a_service_error()
    {
        using var directory = new DatabaseTestDirectory();
        var path = directory.PathFor("absent.sqlite");

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await LaunchDryRunCommand.ExecuteAsync(
            ["--dry-run", "--library", path, "--name", "Game"], output, error);

        Assert.Equal(4, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("was not found", error.ToString());
    }

    private static Game GameWithAction(string name, GameAction action)
    {
        var game = CliQueryFixture.NewGame(name);
        game.IsInstalled = true;
        game.GameActions = new ObservableCollection<GameAction> { action };
        return game;
    }
}

using System.Reflection;
using System.Text.Json;
using Playnite.Cli;
using Playnite.Core.Database;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class ImportLibraryCommandTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "playnite-cli-import-" + Guid.NewGuid().ToString("N"));

    public ImportLibraryCommandTests() => Directory.CreateDirectory(temporary);

    [Fact]
    public async Task Import_command_publishes_a_fixture_and_prints_the_structured_result()
    {
        var destination = Path.Combine(temporary, "library.sqlite");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await ImportLibraryCommand.ExecuteAsync(
            [Reader(), Fixture, destination], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("Published", document.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("Report").GetProperty("ImportedSteam").GetInt32());
        var database = await LibraryDatabase.OpenAsync(destination);
        Assert.Equal(3, (await database.ReadAsync(session => session.Games.GetAllAsync())).Count);
    }

    [Fact]
    public async Task Dry_run_prints_Planned_and_usage_errors_return_two()
    {
        var destination = Path.Combine(temporary, "library.sqlite");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await ImportLibraryCommand.ExecuteAsync(
            [Reader(), Fixture, destination, "--dry-run"], output, error);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("Planned", document.RootElement.GetProperty("Outcome").GetString());
        Assert.False(File.Exists(destination));

        var invalid = new StringWriter();
        var usage = new StringWriter();
        Assert.Equal(2, await ImportLibraryCommand.ExecuteAsync([Reader()], invalid, usage));
        Assert.Equal("", invalid.ToString());
        Assert.Contains("Usage: Playnite.Cli import", usage.ToString());
    }

    [Fact]
    public async Task Reader_failure_returns_the_service_error_exit_code()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await ImportLibraryCommand.ExecuteAsync(
            [Reader(), Path.Combine(temporary, "absent"), Path.Combine(temporary, "library.sqlite")],
            output, error);

        Assert.Equal(4, exitCode);
        Assert.Equal("", output.ToString());
        Assert.Contains("legacy reader failed", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Fixture => RepositoryPaths.Combine("tests", "fixtures", "v10", "library");

    private static string Reader()
    {
        var configuration = typeof(ImportLibraryCommandTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        return RepositoryPaths.Combine("src", "Playnite.LegacyImport", "bin", configuration, "net11.0",
            "Playnite.LegacyImport.dll");
    }

    public void Dispose() => Directory.Delete(temporary, recursive: true);
}

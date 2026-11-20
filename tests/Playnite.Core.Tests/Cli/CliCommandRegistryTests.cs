using System.Diagnostics;
using Playnite.Cli;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class CliCommandRegistryTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Playnite.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    private static string Cli => Path.Combine(Root, "src", "Playnite.Cli", "bin",
        AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug", "net11.0", "Playnite.Cli.dll");

    [Fact]
    public void Import_command_is_discovered_from_its_own_file()
    {
        Assert.IsType<ImportCommand>(CliCommandRegistry.Find("import-v10"));
    }

    [Fact]
    public async Task Command_registered_from_a_new_file_is_reachable()
    {
        CliCommandRegistry.Register(new SyntheticCommand());

        Assert.IsType<SyntheticCommand>(CliCommandRegistry.Find("synthetic-probe"));
        Assert.Equal(23, await Program.RunAsync(["synthetic-probe", "one", "two"]));
        Assert.True(SyntheticCommand.Invoked);
        Assert.Equal(["one", "two"], SyntheticCommand.Arguments);
    }

    [Fact]
    public async Task Import_command_dispatches_with_its_own_usage_and_exit_code()
    {
        var result = await RunCli("import-v10");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.StartsWith("Usage: Playnite.Cli import-v10 ", result.Error);
    }

    [Fact]
    public async Task Unknown_command_reports_a_clear_error_and_exit_code_one()
    {
        var result = await RunCli("frobnicate");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal(["Unknown command 'frobnicate'.", "Usage: Playnite.Cli [--version]"],
            result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Cli);
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

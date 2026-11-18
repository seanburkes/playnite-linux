using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Playnite.Platform.DesktopIntegration;

[assembly: InternalsVisibleTo("Playnite.Core.Tests")]

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>Starts executables from an argument list. Output is captured, and a timeout kills the tree.</summary>
public sealed class LinuxExternalCommandRunner : IExternalCommandRunner
{
    public async Task<ExternalCommandResult> RunAsync(ExternalCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return ExternalCommandResult.NotStarted("the process did not start");
            }
        }
        catch (Win32Exception exception)
        {
            return ExternalCommandResult.NotStarted(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ExternalCommandResult.NotStarted(exception.Message);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return new ExternalCommandResult(
                true,
                -1,
                await AwaitOutputAsync(standardOutput).ConfigureAwait(false),
                await AwaitOutputAsync(standardError).ConfigureAwait(false),
                $"timed out after {command.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s");
        }

        return new ExternalCommandResult(
            true,
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false),
            null);
    }

    private static async Task<string> AwaitOutputAsync(Task<string> output)
    {
        try
        {
            return await output.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception)
        {
            return;
        }
    }
}

internal static class CommandDiagnostics
{
    private const int MaximumDetailLength = 200;

    public static string Summarize(ExternalCommandResult result)
    {
        if (!result.Started)
        {
            return "not started: " + FirstLine(result.Failure ?? "unknown failure");
        }

        if (!string.IsNullOrWhiteSpace(result.Failure))
        {
            return FirstLine(result.Failure);
        }

        var error = FirstLine(result.StandardError);
        return error.Length == 0
            ? $"exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            : $"exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: {error}";
    }

    public static string FirstLine(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var lineEnd = trimmed.IndexOfAny(['\r', '\n']);
        var line = lineEnd < 0 ? trimmed : trimmed[..lineEnd];
        return line.Length <= MaximumDetailLength ? line : line[..MaximumDetailLength];
    }
}

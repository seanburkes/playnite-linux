using DiagnosticsProcess = System.Diagnostics.Process;
using DiagnosticsProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Playnite.Platform.Linux.Sessions;

/// <summary>Signals a whole process group; success means the signal command was accepted.</summary>
public interface IProcessGroupKiller
{
    Task KillAsync(int processGroupId, CancellationToken cancellationToken);
}

public sealed class LinuxProcessGroupKiller : IProcessGroupKiller
{
    private readonly string killPath;

    public LinuxProcessGroupKiller(string? killPath = null)
    {
        this.killPath = killPath
            ?? (File.Exists("/usr/bin/kill") ? "/usr/bin/kill"
                : File.Exists("/bin/kill") ? "/bin/kill"
                : throw new InvalidOperationException("A kill binary is required for process-group containment."));
    }

    public async Task KillAsync(int processGroupId, CancellationToken cancellationToken)
    {
        if (processGroupId <= 0)
        {
            return;
        }

        var startInfo = new DiagnosticsProcessStartInfo
        {
            FileName = killPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-KILL");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add($"-{processGroupId}");
        using var process = DiagnosticsProcess.Start(startInfo)
            ?? throw new InvalidOperationException("The kill command could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"kill -KILL -- -{processGroupId} exited {process.ExitCode}; the group may already be empty.");
        }
    }
}

using System.Diagnostics;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;

namespace Playnite.Platform.Linux;

/// <summary>Same-namespace host adapter. Does not read environments or command lines.</summary>
public sealed class LinuxExternalProcessHost : IExternalProcessHost
{
    private readonly LinuxProcessObserver observer = new();
    private readonly LinuxProcStatReader stats = new();

    public async Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var sample = await observer.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ExternalProcessEntry>();
        var complete = sample.IsComplete && sample.Unreadable.Count == 0;
        foreach (var entry in sample.Processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? executable = null;
            if (entry.IsRunning)
            {
                try
                {
                    executable = File.ResolveLinkTarget($"/proc/{entry.Identity.ProcessId}/exe", true)?.FullName;
                    if (executable is not null)
                    {
                        executable = File.ResolveLinkTarget(executable, true)?.FullName ?? executable;
                        if (!File.Exists(executable))
                        {
                            executable = null;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }

            // Bind executable evidence to the exact stat identity, not a reused PID.
            ProcStat? current;
            try
            {
                current = await stats.ReadAsync(entry.Identity.ProcessId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                complete = false;
                continue;
            }
            if (current is { } row && row.StartTimeTicks == entry.Identity.StartTimeTicks
                && row.ProcessGroupId == entry.Identity.ProcessGroupId)
            {
                entries.Add(new(entry with { IsRunning = row.IsRunning }, executable));
            }
        }

        return new(entries, complete);
    }

    public async Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(request.Executable) { UseShellExecute = false };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.WorkingDirectory is not null)
        {
            info.WorkingDirectory = request.WorkingDirectory;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("Steam request could not be started.");
        // Cancellation releases our watcher/handle, not the command: a cold-start request may
        // become the long-lived client. Only attributed game identities may ever be signalled.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = await stats.ReadAsync(identity.ProcessId, cancellationToken).ConfigureAwait(false);
        if (row is not { } current || !current.IsRunning || current.StartTimeTicks != identity.StartTimeTicks
            || current.ProcessGroupId != identity.ProcessGroupId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            process.Kill(); // Only this identity; never an unobserved child or an unrelated group.
        }
        catch (ArgumentException)
        {
            // Exit between the checked stat and opening the process handle.
            return;
        }
        catch (InvalidOperationException)
        {
            // Exit between opening the handle and signalling.
            return;
        }
    }
}

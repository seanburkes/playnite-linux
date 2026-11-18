using System.Diagnostics;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;

namespace Playnite.Platform.Linux;

/// <summary>Same-namespace host adapter. Does not read environments or command lines.</summary>
public sealed class LinuxExternalProcessHost : IExternalProcessHost
{
    private readonly LinuxProcessObserver observer;
    private readonly IProcStatReader stats;
    private readonly IProcessSignalHandles signals;
    private readonly Func<int, string?> readExecutable;

    public LinuxExternalProcessHost(IProcStatReader? statReader = null, IProcessSignalHandles? signalHandles = null,
        Func<int, string?>? executableReader = null)
    {
        stats = statReader ?? new LinuxProcStatReader();
        observer = new LinuxProcessObserver(stats);
        signals = signalHandles ?? new LinuxPidFd();
        readExecutable = executableReader ?? ReadExecutable;
    }

    public async Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var sample = await observer.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ExternalProcessEntry>();
        var moved = new List<ProcessIdentity>();
        var complete = sample.IsComplete && sample.Unreadable.Count == 0;
        foreach (var entry in sample.Processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? executable = null;
            if (entry.IsRunning)
            {
                try
                {
                    executable = readExecutable(entry.Identity.ProcessId);
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

            if (current is not { } row)
            {
                // Vanished between the two reads: exit evidence, not a row.
                continue;
            }

            if (row.StartTimeTicks != entry.Identity.StartTimeTicks)
            {
                // A different process reused the PID: never report it as the sampled identity.
                continue;
            }

            if (row.ProcessGroupId != entry.Identity.ProcessGroupId)
            {
                // Same PID and start ticks in a changed group: the process moved (setsid) between
                // reads. Retain the re-read identity so an attributed identity's escape stays
                // visible, and report it as an anomaly rather than degrading the whole sample:
                // an unrelated move must not abort a launch or a stop.
                var movedIdentity = new ProcessIdentity(entry.Identity.ProcessId, row.StartTimeTicks,
                    row.ProcessGroupId);
                moved.Add(movedIdentity);
                entries.Add(new(entry with { Identity = movedIdentity, IsRunning = row.IsRunning },
                    executable));
                continue;
            }

            entries.Add(new(entry with { IsRunning = row.IsRunning }, executable));
        }

        return new(entries, complete, moved);
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

    public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        SignalCoreAsync(identity, null, cancellationToken);

    public Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken cancellationToken) =>
        SignalCoreAsync(identity, expectedExecutable, cancellationToken);

    private async Task SignalCoreAsync(ProcessIdentity identity, string? expectedExecutable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = signals.Open(identity.ProcessId);
        if (handle is null)
        {
            return;
        }

        // Pin first, then reverify. The identity check runs before the executable re-read
        // because execve preserves pid/start ticks/PGID: an exec during the stat read is
        // caught by the later read, and a subsequent PID recycle cannot redirect the signal.
        var row = await stats.ReadAsync(identity.ProcessId, cancellationToken).ConfigureAwait(false);
        if (row is not { } current || current.ProcessId != identity.ProcessId || !current.IsRunning
            || current.StartTimeTicks != identity.StartTimeTicks
            || current.ProcessGroupId != identity.ProcessGroupId)
        {
            return;
        }

        if (expectedExecutable is not null)
        {
            try
            {
                if (readExecutable(identity.ProcessId) != expectedExecutable)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        handle.Terminate();
    }

    private static string? ReadExecutable(int pid)
    {
        var executable = File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName;
        executable = executable is null ? null : File.ResolveLinkTarget(executable, true)?.FullName ?? executable;
        return File.Exists(executable) ? executable : null;
    }
}

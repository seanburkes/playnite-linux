using Playnite.Platform.Sessions;

namespace Playnite.Platform.Linux.Sessions;

/// <summary>
/// Reads every numeric <c>/proc</c> entry. A row that vanishes between enumeration and read is
/// absent; any other read or parse failure makes the sample incomplete, which callers must treat
/// as unknown rather than stopped.
/// </summary>
public sealed class LinuxProcessObserver : IProcessObserver
{
    private readonly IProcStatReader statReader;
    private readonly string procRoot;

    public LinuxProcessObserver(IProcStatReader? statReader = null, string procRoot = "/proc")
    {
        ArgumentException.ThrowIfNullOrEmpty(procRoot);
        if (!Path.IsPathFullyQualified(procRoot))
        {
            throw new ArgumentException("The proc root must be an absolute path.", nameof(procRoot));
        }

        this.statReader = statReader ?? new LinuxProcStatReader(procRoot);
        this.procRoot = procRoot;
    }

    public async Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<ProcessEntry>();
        string? reason = null;
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(procRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ProcessSnapshot(entries, false, "the /proc table could not be enumerated");
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileName(directory), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                var stat = await statReader.ReadAsync(pid, cancellationToken).ConfigureAwait(false);
                if (stat is not { } row)
                {
                    continue;
                }

                if (row.ProcessId != pid)
                {
                    reason ??= $"process {pid} was replaced by process {row.ProcessId}";
                    continue;
                }

                entries.Add(new ProcessEntry(
                    new ProcessIdentity(pid, row.StartTimeTicks), row.ParentProcessId, row.IsRunning));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                reason ??= $"process {pid} could not be read";
            }
        }

        return new ProcessSnapshot(entries, reason is null, reason);
    }
}

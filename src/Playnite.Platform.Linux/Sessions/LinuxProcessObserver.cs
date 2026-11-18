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
        var unreadable = new List<int>();
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
                    unreadable.Add(pid);
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
                unreadable.Add(pid);
            }
        }

        return new ProcessSnapshot(entries, true, null, unreadable.Count == 0 ? null : unreadable);
    }
}

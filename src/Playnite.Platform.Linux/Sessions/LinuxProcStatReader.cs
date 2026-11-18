namespace Playnite.Platform.Linux.Sessions;

/// <summary>Reads one process's stat row from <c>/proc</c>.</summary>
public sealed class LinuxProcStatReader : IProcStatReader
{
    // A process that exits between the /proc enumeration and this read makes the kernel
    // return ESRCH; .NET surfaces it as an IOException whose HResult is the raw errno.
    private const int NoSuchProcess = 3;

    private readonly string procRoot;

    public LinuxProcStatReader(string procRoot = "/proc")
    {
        ArgumentException.ThrowIfNullOrEmpty(procRoot);
        if (!Path.IsPathFullyQualified(procRoot))
        {
            throw new ArgumentException("The proc root must be an absolute path.", nameof(procRoot));
        }

        this.procRoot = procRoot;
    }

    public async Task<ProcStat?> ReadAsync(int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(procRoot, processId.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat");
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException exception) when (IsNoSuchProcess(exception))
        {
            return null;
        }

        return LinuxProcStat.Parse(text);
    }

    /// <summary>
    /// True when the I/O failure is ESRCH: the process vanished after its <c>/proc</c> entry was
    /// seen. Other I/O failures stay errors so callers treat the sample as unknown coverage
    /// rather than a stopped process.
    /// </summary>
    internal static bool IsNoSuchProcess(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.HResult == NoSuchProcess;
    }
}

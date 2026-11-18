namespace Playnite.Platform.Linux.Sessions;

/// <summary>Reads <c>/proc/&lt;pid&gt;/stat</c> without collecting command lines or environments.</summary>
public sealed class LinuxProcStatReader : IProcStatReader
{
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

        return LinuxProcStat.Parse(text);
    }
}

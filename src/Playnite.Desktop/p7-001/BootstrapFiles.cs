using Playnite.Core;

namespace Playnite.Desktop.Bootstrap;

/// <summary>
/// Nonblocking entry-type checks for startup files. A FIFO or device at a config/log path must
/// never be opened for reading or append: the managed file APIs would block waiting for a writer.
/// The Linux probe uses O_PATH|O_NOFOLLOW|O_NONBLOCK plus statx, so classification itself cannot
/// block or follow a planted symlink.
/// </summary>
public static class BootstrapFiles
{
    public static string IsRegular(string path, IRegularFileProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var fullPath = Path.GetFullPath(path);
        // A dangling symlink is not reported by File.Exists, so it would pass a naive existence
        // check and FileMode.Append would then follow it and create the link target. Reject the
        // final symlink first, whether or not its target currently exists.
        if (new FileInfo(fullPath).LinkTarget is not null)
        {
            throw new InvalidDataException($"{fullPath} is a symbolic link.");
        }

        if (File.Exists(fullPath) && !probe.IsRegularFile(fullPath))
        {
            throw new InvalidDataException($"{fullPath} is not a regular file.");
        }

        return fullPath;
    }

    public static string? ExistingRegular(string? path, IRegularFileProbe probe)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) && probe.IsRegularFile(fullPath) ? fullPath : null;
    }
}

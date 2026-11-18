using Playnite.Core;
using Playnite.Platform;

namespace Playnite.Steam;

/// <summary>Outcome of a bounded read of a library file. Only <see cref="Ok"/> carries bytes.</summary>
public enum SteamFileReadStatus { Ok, Missing, NotRegularFile, TooLarge }

/// <summary>A bounded file read. <see cref="Bytes"/> is null unless <see cref="Status"/> is <see cref="SteamFileReadStatus.Ok"/>.</summary>
public sealed record SteamFileRead(SteamFileReadStatus Status, byte[]? Bytes);

/// <summary>
/// Read-only filesystem surface used by discovery. The interface has no mutating member, so a
/// discovery call cannot create, modify or repair anything under a Steam tree.
/// </summary>
public interface ISteamFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);

    /// <summary>
    /// True when <paramref name="path"/> names a directory entry, including a symbolic link whose
    /// target does not exist. <see cref="GetRealPath"/> returns <c>null</c> for an absent entry and
    /// for a dangling link alike; this separates the two without opening anything.
    /// </summary>
    bool EntryExists(string path);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from one handle opened without following links
    /// in any canonical-path component. Type and length checks apply to that same handle.
    /// </summary>
    SteamFileRead ReadFile(string path, int maxBytes);

    /// <summary>
    /// Returns the absolute canonical path with every symbolic link resolved, or <c>null</c> when
    /// the path does not exist (including a dangling link). Throws <see cref="IOException"/> when
    /// the link chain exceeds the resolution limit, and <see cref="UnauthorizedAccessException"/>
    /// when a component cannot be inspected.
    /// </summary>
    string? GetRealPath(string path);

    /// <summary>True when the canonical path is a filesystem mount point.</summary>
    bool IsMountPoint(string path);
}

/// <summary>The host filesystem. Every member is a read; nothing is opened for writing.</summary>
public sealed class SteamFileSystem(IBoundedFileReader reader) : ISteamFileSystem
{
    private const int MaxLinks = 40;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool EntryExists(string path) => EntryExistsCore(path);

    private static bool EntryExistsCore(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (parent is null || name.Length == 0)
        {
            return false;
        }

        var comparison = PathComparison;
        try
        {
            return Directory.EnumerateFileSystemEntries(parent)
                .Any(entry => string.Equals(Path.GetFileName(entry), name, comparison));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public SteamFileRead ReadFile(string path, int maxBytes)
    {
        var read = reader.Read(path, maxBytes);
        var status = read.Status switch
        {
            BoundedFileReadStatus.Ok => SteamFileReadStatus.Ok,
            BoundedFileReadStatus.Missing => SteamFileReadStatus.Missing,
            BoundedFileReadStatus.NotRegularFile => SteamFileReadStatus.NotRegularFile,
            BoundedFileReadStatus.TooLarge => SteamFileReadStatus.TooLarge,
            _ => throw new IOException("Unknown bounded read status."),
        };
        return new(status, read.Bytes);
    }

    public bool IsMountPoint(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (string.Equals(Path.GetFullPath(drive.RootDirectory.FullName), full, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    public string? GetRealPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute path is required.", nameof(path));
        }

        var hops = 0;
        return Resolve(path, ref hops);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // realpath(3) semantics: components are resolved left to right, "." is ignored, ".." pops the
    // already-resolved parent (after any link at that component), and a link restarts resolution
    // from its target. The input is never lexically normalised first, because that would collapse
    // "link/../x" before the link is followed.
    private static string? Resolve(string path, ref int hops)
    {
        var root = Path.GetPathRoot(path) ?? throw new ArgumentException("An absolute path is required.", nameof(path));
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                // realpath must traverse this component before walking back out of it.
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    return null;
                }

                current = Path.GetDirectoryName(current) ?? root;
                continue;
            }

            var next = Path.Combine(current, part);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinks)
            {
                throw new IOException("Too many levels of symbolic links.");
            }

            var combined = Path.IsPathFullyQualified(target) ? target : current + Path.DirectorySeparatorChar + target;
            for (var rest = index + 1; rest < parts.Length; rest++)
            {
                combined += Path.DirectorySeparatorChar + parts[rest];
            }

            return Resolve(combined, ref hops);
        }

        if (Directory.Exists(current) || File.Exists(current))
        {
            return current;
        }

        // A permission failure makes Exists false, so distinguish it from a genuinely missing path.
        try
        {
            _ = File.GetAttributes(current);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException)
        {
            // File.GetAttributes cannot describe every existing entry type (a socket is one), so
            // fall back to the directory entry itself: realpath(3) succeeds for any existing name.
            return EntryExistsCore(current) ? current : null;
        }

        return current;
    }
}

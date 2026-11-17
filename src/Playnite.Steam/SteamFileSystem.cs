namespace Playnite.Steam;

/// <summary>
/// Read-only filesystem surface used by discovery. The interface has no mutating member, so a
/// discovery call cannot create, modify or repair anything under a Steam tree.
/// </summary>
public interface ISteamFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    long GetFileLength(string path);
    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Returns the absolute canonical path with every symbolic link resolved, or <c>null</c> when
    /// the path does not exist (including a dangling link). Throws <see cref="IOException"/> when
    /// the link chain exceeds the resolution limit.
    /// </summary>
    string? GetRealPath(string path);
}

/// <summary>The host filesystem. Every member is a read; nothing is opened for writing.</summary>
public sealed class SteamFileSystem : ISteamFileSystem
{
    private const int MaxLinks = 40;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public string? GetRealPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute path is required.", nameof(path));
        }

        var hops = 0;
        return Resolve(Path.GetFullPath(path), ref hops);
    }

    // realpath(3) semantics: re-resolve from the root after every link so a target with
    // symlinked ancestors is canonical too. The hop budget is shared across the whole walk.
    private static string? Resolve(string path, ref int hops)
    {
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var parent = current;
            var next = Path.Combine(parent, parts[index]);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinks)
            {
                throw new IOException($"Too many levels of symbolic links in '{path}'.");
            }

            var combined = Path.GetFullPath(target, parent);
            for (var rest = index + 1; rest < parts.Length; rest++)
            {
                combined = Path.Combine(combined, parts[rest]);
            }

            return Resolve(combined, ref hops);
        }

        return Directory.Exists(current) || File.Exists(current) ? current : null;
    }
}

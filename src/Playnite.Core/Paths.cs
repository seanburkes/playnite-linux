using System.Text;
using System.Text.RegularExpressions;

namespace Playnite.Core;

/// <summary>Adapted from v10 Playnite/Common/Paths.cs; POSIX paths are case-sensitive.</summary>
public static class Paths
{
    public static string Combine(params string[] paths) => Path.Combine(paths);

    // v10 collapsed duplicate separators. A backslash is a filename character on Linux.
    public static string? FixSeparators(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var result = new StringBuilder(path.Length);
        char previous = default;
        foreach (var current in path)
        {
            if (previous != current || current != Path.DirectorySeparatorChar)
            {
                result.Append(current);
            }

            previous = current;
        }

        return result.ToString();
    }

    /// <summary>Lexical normalization only, not symlink resolution or a containment check.</summary>
    public static string Normalize(string path, string currentDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, currentDirectory));

    public static bool AreEqual(string? path1, string? path2, string currentDirectory)
    {
        // v10 deliberately treats even two empty paths as unequal.
        if (string.IsNullOrEmpty(path1) || string.IsNullOrEmpty(path2))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(path1, currentDirectory), Normalize(path2, currentDirectory), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsFullPath(string? path) => !string.IsNullOrWhiteSpace(path)
        && !ContainsInvalidPathChars(path) && Path.IsPathFullyQualified(path);

    public static bool ContainsInvalidPathChars(string path) => path.Intersect(Path.GetInvalidPathChars()).Any();

    public static string GetSafePathName(string filename)
    {
        var path = string.Join(" ", filename.Split(Path.GetInvalidFileNameChars()));
        return Regex.Replace(path, @"\s+", " ").Trim();
    }

    public static bool IsValidFilePath(string? path, IFileSystem fileSystem)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || ContainsInvalidPathChars(path) || string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                return false;
            }

            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) || fileSystem.DirectoryExists(root);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string GetCommonDirectory(string[] paths)
    {
        int length = paths[0].Length;
        for (int i = 1; i < paths.Length; i++)
        {
            length = Math.Min(length, paths[i].Length);
            for (int j = 0; j < length; j++)
            {
                if (paths[i][j] != paths[0][j])
                {
                    length = j;
                    break;
                }
            }
        }

        var common = paths[0][..length];
        return common.EndsWith(Path.DirectorySeparatorChar)
            ? common
            : common[..(common.LastIndexOf(Path.DirectorySeparatorChar) + 1)];
    }
}

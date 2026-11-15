using Playnite.Core;

namespace Playnite.Core.Tests.TestDoubles;

/// <summary>Case-sensitive, absolute-path filesystem with no host filesystem access.</summary>
public sealed class MemoryFileSystem : IFileSystem
{
    private readonly HashSet<string> directories = new(StringComparer.Ordinal) { "/" };
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

    public bool FileExists(string path) => files.ContainsKey(Key(path));
    public bool DirectoryExists(string path) => directories.Contains(Key(path));

    public void CreateDirectory(string path)
    {
        var key = Key(path);
        if (files.ContainsKey(key))
        {
            throw new IOException("A file occupies the directory path.");
        }

        if (directories.Contains(key))
        {
            return;
        }

        CreateDirectory(Path.GetDirectoryName(key)!);
        directories.Add(key);
    }

    public void AddFile(string path, string content)
    {
        var key = Key(path);
        if (directories.Contains(key))
        {
            throw new IOException("A directory occupies the file path.");
        }

        CreateDirectory(Path.GetDirectoryName(key)!);
        files[key] = content;
    }

    public string ReadAllText(string path) => files.TryGetValue(Key(path), out var text)
        ? text : throw new FileNotFoundException("File not found.", path);

    private static string Key(string path)
    {
        if (!Paths.IsFullPath(path))
        {
            throw new ArgumentException("The test filesystem requires an absolute path.", nameof(path));
        }

        return Paths.Normalize(path, "/");
    }
}

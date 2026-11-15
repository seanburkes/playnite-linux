using Playnite.Core;

namespace Playnite.Platform.Linux;

public sealed class LinuxFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
}

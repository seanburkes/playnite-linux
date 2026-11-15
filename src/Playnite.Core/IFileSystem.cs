namespace Playnite.Core;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    string ReadAllText(string path);
}

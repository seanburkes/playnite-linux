using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class FileSystemTests
{
    [Fact]
    public void MemoryDirectoriesAreIdempotentAndNeverUseHostFilesystem()
    {
        Exercise(new MemoryFileSystem(), "/virtual-parent/child");
    }

    [Fact]
    public void LinuxDirectoriesAreIdempotentInAnIsolatedTemporaryDirectory()
    {
        var temporary = Directory.CreateTempSubdirectory("playnite-paths-");
        try
        {
            var fileSystem = new LinuxFileSystem();
            var path = Path.Combine(temporary.FullName, "parent", "child");
            Exercise(fileSystem, path);
            var file = Path.Combine(path, "test.txt");
            File.WriteAllText(file, "これはユニコード");
            Assert.True(fileSystem.FileExists(file));
            Assert.Equal("これはユニコード", fileSystem.ReadAllText(file));
            Assert.Throws<IOException>(() => fileSystem.CreateDirectory(file));
            Assert.Throws<FileNotFoundException>(() => fileSystem.ReadAllText(Path.Combine(path, "missing.txt")));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    [Fact]
    public void MemoryFilesystemPreservesCaseAndReportsConflictsAndMissingFiles()
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.AddFile("/a/file", "content");
        Assert.True(fileSystem.FileExists("/a/./file"));
        Assert.False(fileSystem.FileExists("/a/File"));
        Assert.Equal("content", fileSystem.ReadAllText("/a/file"));
        Assert.Throws<FileNotFoundException>(() => fileSystem.ReadAllText("/missing"));
        Assert.Throws<IOException>(() => fileSystem.CreateDirectory("/a/file/child"));
        Assert.Throws<IOException>(() => fileSystem.AddFile("/a", "content"));
    }

    private static void Exercise(IFileSystem fileSystem, string path)
    {
        Assert.False(fileSystem.DirectoryExists(path));
        Assert.False(fileSystem.FileExists(path));
        fileSystem.CreateDirectory(path);
        fileSystem.CreateDirectory(path + "/./");
        Assert.True(fileSystem.DirectoryExists(path));
        Assert.True(fileSystem.DirectoryExists(Path.GetDirectoryName(path)!));
        Assert.False(fileSystem.FileExists(path));
    }
}

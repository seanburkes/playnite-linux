using System.IO.Compression;
using System.Text;
using Xunit;

namespace Playnite.Core.Tests;

internal static class ArchiveTestData
{
    internal static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "archives-v10", name);

    internal static string Golden(string name) =>
        Path.Combine(AppContext.BaseDirectory, "golden-archives", name);

    internal static string NewDirectory(string prefix = "playnite-archive-") =>
        Directory.CreateTempSubdirectory(prefix).FullName;

    internal static void Write(string root, string relative, string content) =>
        Write(root, relative, Encoding.UTF8.GetBytes(content));

    internal static void Write(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    internal static SortedDictionary<string, byte[]> ReadTree(string root)
    {
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }

        return result;
    }

    internal static void AssertTree(IReadOnlyDictionary<string, byte[]> expected, string actualRoot)
    {
        var actual = ReadTree(actualRoot);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys);
        foreach (var (name, bytes) in expected)
        {
            Assert.Equal(bytes, actual[name]);
        }
    }

    internal static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            if (name.EndsWith('/'))
            {
                zip.CreateEntry(name);
                continue;
            }

            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    internal static void AddZipEntry(string path, string name, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static byte[] EntryBytes(string archivePath, string name)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        using var stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    internal static byte[] Truncated(byte[] bytes, double fraction)
    {
        var result = new byte[(int)(bytes.Length * fraction)];
        Array.Copy(bytes, result, result.Length);
        return result;
    }
}

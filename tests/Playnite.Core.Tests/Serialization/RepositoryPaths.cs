using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Playnite.Core.Tests.Serialization;

internal static class RepositoryPaths
{
    internal static string Root { get; } = FindRoot();

    internal static string Combine(params string[] parts)
    {
        return Path.Combine([Root, .. parts]);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Playnite.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Cannot find the repository root from {AppContext.BaseDirectory}; Playnite.slnx is missing.");
    }

    internal static string Sha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Playnite.Fuzz;

/// <summary>A committed corpus seed and its format.</summary>
/// <param name="Format">Reader entry point: json, yaml, toml or ini.</param>
/// <param name="RelativePath">Path relative to the corpus root, with forward slashes.</param>
/// <param name="Bytes">Exact seed bytes.</param>
public sealed record FuzzSeed(string Format, string RelativePath, byte[] Bytes);

/// <summary>
/// Loads and verifies the committed, hash-pinned fuzz corpus.
/// </summary>
/// <remarks>
/// <c>manifest.sha256</c> is the provenance record: every seed's SHA-256 is checked before
/// mutation, and a missing, extra, edited or replaced seed fails the target instead of
/// silently changing what CI fuzzes. Seeds copied from frozen v10 data are byte copies; their
/// source is recorded in the corpus README, never rewritten in place.
/// </remarks>
public static class FuzzCorpus
{
    /// <summary>Loads every seed and verifies the manifest.</summary>
    /// <param name="root">Corpus directory containing one subdirectory per format.</param>
    /// <returns>Seeds ordered by relative path.</returns>
    /// <exception cref="InvalidOperationException">The corpus or manifest is invalid.</exception>
    public static IReadOnlyList<FuzzSeed> Load(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            throw new InvalidOperationException($"Fuzz corpus directory '{root}' does not exist.");
        }

        var manifestPath = Path.Combine(directory.FullName, "manifest.sha256");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Fuzz corpus '{root}' has no manifest.sha256.");
        }

        var manifest = ReadManifest(manifestPath);
        var seeds = new List<FuzzSeed>();
        foreach (var format in FuzzParsers.Formats)
        {
            var formatDirectory = Path.Combine(directory.FullName, format);
            if (!Directory.Exists(formatDirectory))
            {
                throw new InvalidOperationException(
                    $"Fuzz corpus '{root}' has no '{format}' directory.");
            }

            var paths = Directory.EnumerateFiles(formatDirectory)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Fuzz corpus '{root}/{format}' contains no seeds.");
            }

            foreach (var path in paths)
            {
                var relative = Path.GetRelativePath(directory.FullName, path).Replace('\\', '/');
                var bytes = File.ReadAllBytes(path);
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!manifest.TryGetValue(relative, out var recorded))
                {
                    throw new InvalidOperationException(
                        $"Fuzz seed '{relative}' is not recorded in manifest.sha256.");
                }

                if (!string.Equals(recorded, digest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Fuzz seed '{relative}' has SHA-256 {digest}, but manifest.sha256 records {recorded}.");
                }

                seeds.Add(new FuzzSeed(format, relative, bytes));
            }
        }

        var onDisk = seeds.Select(seed => seed.RelativePath).Order(StringComparer.Ordinal).ToArray();
        var recordedPaths = manifest.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!onDisk.SequenceEqual(recordedPaths, StringComparer.Ordinal))
        {
            var missing = recordedPaths.Except(onDisk, StringComparer.Ordinal).ToArray();
            throw new InvalidOperationException(
                "manifest.sha256 and the corpus files disagree." +
                (missing.Length == 0 ? string.Empty : $" Missing: {string.Join(", ", missing)}."));
        }

        return seeds;
    }

    /// <summary>Computes a stable digest over every seed path and byte sequence.</summary>
    /// <param name="seeds">Loaded seeds.</param>
    /// <returns>Lowercase SHA-256 of the corpus contents.</returns>
    public static string Digest(IReadOnlyList<FuzzSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var seed in seeds)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(seed.RelativePath));
            hash.AppendData([0]);
            hash.AppendData(seed.Bytes);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Dictionary<string, string> ReadManifest(string path)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || parts[1].Length == 0)
            {
                throw new InvalidOperationException(
                    $"manifest.sha256 line '{line}' is not '<sha256>  <relative path>'.");
            }

            entries.Add(parts[1].Replace('\\', '/'), parts[0].ToLowerInvariant());
        }

        return entries;
    }
}

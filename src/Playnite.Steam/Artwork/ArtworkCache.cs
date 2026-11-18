using System.Security.Cryptography;
using System.Text;

namespace Playnite.Steam.Artwork;

// One owner per private cache directory; the fetcher serializes read/evict/publish operations.
public sealed class ArtworkCache
{
    private readonly string directory;
    private readonly string steamRoot;
    public long MaxBytes { get; }

    public ArtworkCache(string directory, long maxBytes, string steamRoot)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Absolute cache directory required.", nameof(directory));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        this.directory = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(steamRoot)) throw new ArgumentException("Absolute Steam root required.", nameof(steamRoot));
        this.steamRoot = ResolveDirectory(steamRoot);
        MaxBytes = maxBytes;
    }

    public static string SourceKey(ArtworkIdentity identity, ArtworkSource source)
    {
        string key = FormattableString.Invariant($"{identity.AppId}\n{identity.Kind}\n{source.Location.AbsoluteUri}\n{source.ExpectedSha256?.ToUpperInvariant()}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public async Task<string?> FindAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        CheckDirectory();
        if (!Directory.Exists(directory)) return null;
        foreach (string path in Directory.GetFiles(directory, key + "-*.blob").Order(StringComparer.Ordinal))
        {
            CheckFile(path);
            var info = new FileInfo(path);
            if (info.Length > MaxBytes || info.Length == 0) throw new InvalidDataException("cache-content-invalid");
            await using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!Path.GetFileName(path).Equals(key + "-" + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + hash + ".blob", StringComparison.Ordinal))
                throw new InvalidDataException("cache-hash-mismatch");
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    public async Task<string> StoreAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        if (content.Length == 0) throw new InvalidDataException("cache-content-empty");
        if (content.LongLength > MaxBytes) throw new InvalidDataException("cache-size-limit");
        CheckDirectory();
        Directory.CreateDirectory(directory);
        // Abandoned temporary files from interrupted writes are never hits and count toward the bound.
        foreach (string abandoned in Directory.GetFiles(directory, "*.tmp"))
        {
            CheckFile(abandoned);
            File.Delete(abandoned);
        }
        // Only the most recently acquired snapshot of an explicit source remains discoverable.
        foreach (string previous in Directory.GetFiles(directory, key + "-*.blob"))
        {
            CheckFile(previous);
            File.Delete(previous);
        }
        var files = Directory.GetFiles(directory, "*.blob").Select(p => new FileInfo(p))
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        long used = 0;
        foreach (var file in files)
        {
            CheckFile(file.FullName);
            used = checked(used + file.Length);
        }
        foreach (var file in files)
        {
            if (used <= MaxBytes - content.LongLength) break;
            File.Delete(file.FullName);
            used -= file.Length;
        }
        string hash = Convert.ToHexString(SHA256.HashData(content));
        string target = Path.Combine(directory, key + "-" + content.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + hash + ".blob");
        string temporary = Path.Combine(directory, key + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            return File.Exists(target) ? target : throw new IOException("cache-publish-missing");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void CheckDirectory()
    {
        if (directory == Path.GetPathRoot(directory) || directory == steamRoot || directory.StartsWith(steamRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("cache-directory-unsafe");
        for (DirectoryInfo? current = new(directory); current != null; current = current.Parent)
            if (current.LinkTarget != null) throw new IOException("cache-symlink");
    }

    private static string ResolveDirectory(string path)
    {
        string resolved = Path.GetPathRoot(path)!;
        foreach (string part in Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var info = new DirectoryInfo(resolved);
            if (info.LinkTarget != null)
                resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new IOException("steam-root-unresolvable");
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static void CheckFile(string path)
    {
        if (new FileInfo(path).LinkTarget != null) throw new IOException("cache-symlink");
    }

    private static void ValidateKey(string key)
    {
        if (key.Length is < 1 or > 64 || key.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Cache keys must be 1-64 ASCII alphanumeric characters.", nameof(key));
    }
}

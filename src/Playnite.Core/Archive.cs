using SharpCompress.Archives;

namespace Playnite;

/// <summary>Limits apply to declared sizes and to bytes actually decompressed.</summary>
public sealed record ArchiveLimits(int EntryCount = 100_000, long EntryBytes = 268_435_456,
    long TotalBytes = 4_294_967_296)
{
    internal void Validate()
    {
        if (EntryCount < 1 || EntryBytes < 1 || TotalBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ArchiveLimits));
        }
    }
}

/// <summary>v10 archive helpers, with bounded reads and portable, validated member names.</summary>
public static class Archive
{
    public static List<string> GetArchiveFiles(string archivePath, ArchiveLimits? limits = null)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        return ValidateEntries(archive, limits ?? new ArchiveLimits()).Select(e => e.Key!).ToList();
    }

    public static async Task<Stream?> GetEntryStreamAsync(string archivePath, string entryName,
        ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ArchiveFactory.Open(archivePath);
        var entries = ValidateEntries(archive, limits);
        var entry = entries.FirstOrDefault(e => e.Key == entryName);
        if (entry is null)
        {
            return null;
        }

        using var source = entry.OpenEntryStream();
        var result = new MemoryStream();
        try
        {
            await CopyBoundedAsync(source, result, Math.Min(limits.EntryBytes, limits.TotalBytes), cancellationToken).ConfigureAwait(false);
            if (result.Length != entry.Size)
            {
                throw new InvalidDataException("Archive member size mismatch.");
            }

            result.Position = 0;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static List<IArchiveEntry> ValidateEntries(IArchive archive, ArchiveLimits limits)
    {
        limits.Validate();
        var result = new List<IArchiveEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            if (++count > limits.EntryCount || entry.Size < 0 || entry.Size > limits.EntryBytes ||
                entry.Size > limits.TotalBytes - total)
            {
                throw new InvalidDataException("Archive exceeds decompression limits.");
            }

            total += entry.Size;
            var name = NormalizeName(entry.Key ?? "");
            if (!names.Add(name) || !string.IsNullOrEmpty(entry.LinkTarget))
            {
                throw new InvalidDataException("Duplicate or linked archive member.");
            }

            if (!entry.IsDirectory)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    internal static string NormalizeName(string name)
    {
        name = name.Replace('\\', '/');
        var parts = name.TrimEnd('/').Split('/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') ||
            name.Any(char.IsControl) || parts.Any(p => p is "" or "." or ".."))
        {
            throw new InvalidDataException("Unsafe archive member path.");
        }

        return string.Join('/', parts);
    }

    internal static async Task<long> CopyBoundedAsync(Stream source, Stream target, long maximum,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            if (read > maximum - total)
            {
                throw new InvalidDataException("Archive exceeds decompression limits.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
    }
}

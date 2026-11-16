using SharpCompress.Archives;

namespace Playnite;

/// <summary>
/// Bounds applied to an archive before and while it is decompressed. The defaults match the
/// v11 backup shape: 100k members, 256 MiB per member and 4 GiB in total.
/// </summary>
public sealed record ArchiveLimits(int EntryCount = 100_000, long EntryBytes = 268_435_456,
    long TotalBytes = 4_294_967_296)
{
    internal void Validate()
    {
        if (EntryCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(EntryCount));
        }

        if (EntryBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(EntryBytes));
        }

        if (TotalBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TotalBytes));
        }
    }
}

/// <summary>v10 archive helpers with portable member names and bounded decompression.</summary>
public static class Archive
{
    public static List<string> GetArchiveFiles(string archivePath, ArchiveLimits? limits = null)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        return ValidateEntries(archive, limits ?? new ArchiveLimits()).Select(e => e.Name).ToList();
    }

    public static async Task<Stream?> GetEntryStreamAsync(string archivePath, string entryName,
        ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        using var archive = ArchiveFactory.Open(archivePath);
        var entries = ValidateEntries(archive, limits);
        var entry = entries.FirstOrDefault(e => e.Name == entryName);
        if (entry.Entry is null)
        {
            return null;
        }

        // This call extracts one selected member, so its remaining budget is the full total.
        // Cap at the declared size first: a member that lies cannot reach the EntryBytes ceiling.
        var cap = Math.Min(entry.Entry.Size, limits.TotalBytes);
        using var source = entry.Entry.OpenEntryStream();
        var result = new MemoryStream();
        try
        {
            await CopyBoundedAsync(source, result, cap, cancellationToken).ConfigureAwait(false);
            if (result.Length != entry.Entry.Size)
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

    /// <summary>Normalize a member name and reject anything a restore could write outside its root.</summary>
    internal static string NormalizeName(string name)
    {
        name = name.Replace('\\', '/');
        var trimmed = name.TrimEnd('/');
        var parts = trimmed.Split('/');
        if (trimmed.Length == 0 || name.StartsWith('/') || name.Contains(':') ||
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
                throw new InvalidDataException($"Archive exceeds decompression limits: maximum {maximum} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
    }

    private static List<(string Name, IArchiveEntry? Entry)> ValidateEntries(IArchive archive, ArchiveLimits limits)
    {
        limits.Validate();
        var result = new List<(string, IArchiveEntry?)>();
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
                if ((entry.Key ?? "").EndsWith('/') || (entry.Key ?? "").EndsWith('\\'))
                {
                    throw new InvalidDataException("Unsafe archive member path.");
                }

                result.Add((name, entry));
            }
        }

        return result;
    }
}

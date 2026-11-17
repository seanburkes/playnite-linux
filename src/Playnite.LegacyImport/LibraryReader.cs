using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;

namespace Playnite.LegacyImport;

// Raw documents, not port model types: this boundary must never load Core or SQLite.
internal static class LibraryReader
{
    private sealed record CapturedFile(string Path, long? Size, string? Sha256, string Status, byte[]? Bytes);

    internal static object Read(string source)
    {
        for (var directory = new DirectoryInfo(source); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Source ancestors cannot be symbolic links.");
        var copies = new List<CapturedFile>();
        Capture(source, source, copies);
        using var marker = JsonDocument.Parse(copies.Single(c => c.Path == "database.json").Bytes ?? throw new IOException("Cannot read database.json."));
        if (marker.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate database.json marker keys are not supported.");
        if (marker.RootElement.GetProperty("Version").GetInt32() != 4)
        {
            throw new InvalidDataException("Only the v10 database format (Version 4) is supported.");
        }
        var files = new List<object>();
        var records = new List<object>();
        // Capture immutable copies before parsing. Opening originals uses only File.OpenRead.
        foreach (var captured in copies.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var path = captured.Path;
            files.Add(new { path, size = captured.Size, sha256 = captured.Sha256, status = captured.Status });
            if (Path.GetFileName(path) != path || !path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            using var stream = new MemoryStream(captured.Bytes ?? throw new IOException($"Cannot read database: {path}"), writable: false);
            using var database = new LiteEngine(stream, null);
            foreach (var collection in database.GetCollectionNames().Order(StringComparer.Ordinal))
            {
                foreach (var document in database.FindAll(collection))
                {
                    records.Add(new { file = path, collection, data = ConvertValue(document) });
                }
            }
        }
        var after = new List<CapturedFile>();
        Capture(source, source, after);
        if (after.Count != copies.Count || copies.Any(c => !after.Any(a => a.Path == c.Path && a.Size == c.Size && a.Sha256 == c.Sha256 && a.Status == c.Status)))
            throw new IOException("Source changed during read; refusing a mixed snapshot.");
        return new { format = "playnite-v10-library", version = 1, files, records };
    }

    private static void Capture(string root, string directory, List<CapturedFile> copies)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Source symbolic links are not supported: {entry.Name}");
            }
            if (entry is DirectoryInfo child)
            {
                Capture(root, child.FullName, copies);
            }
            else
            {
                var path = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                try
                {
                    using var source = File.OpenRead(entry.FullName);
                    // Only database/marker files need immutable in-memory copies for LiteDB parsing.
                    if (!path.Contains('/', StringComparison.Ordinal))
                    {
                        using var copy = new MemoryStream();
                        source.CopyTo(copy);
                        var bytes = copy.ToArray();
                        copies.Add(new(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), "readable", bytes));
                    }
                    else copies.Add(new(path, source.Length, Convert.ToHexStringLower(SHA256.HashData(source)), "readable", null));
                }
                catch (Exception exception) when (path.StartsWith("files/", StringComparison.Ordinal) && exception is IOException or UnauthorizedAccessException)
                {
                    copies.Add(new(path, null, null, "unreadable", null));
                }
            }
        }
    }

    private static object? ConvertValue(BsonValue value)
    {
        if (value.IsNull) return null;
        if (value.IsDocument) return value.AsDocument.ToDictionary(p => p.Key, p => ConvertValue(p.Value), StringComparer.Ordinal);
        if (value.IsArray) return value.AsArray.Select(ConvertValue).ToArray();
        if (value.IsGuid) return value.AsGuid.ToString("D");
        // LiteDB decodes an epoch instant as Local or Utc. Refuse Unspecified instead of inferring a timezone.
        if (value.IsDateTime)
        {
            var date = value.AsDateTime;
            if (date.Kind == DateTimeKind.Unspecified) throw new InvalidDataException("BSON datetime has no timezone kind.");
            return date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
        if (value.IsBinary) return new { bsonType = "binary", base64 = Convert.ToBase64String(value.AsBinary) };
        if (value.IsObjectId) return new { bsonType = "objectId", hex = value.AsObjectId.ToString() };
        if (value.IsString) return value.AsString;
        if (value.IsBoolean) return value.AsBoolean;
        if (value.IsInt32) return value.AsInt32;
        if (value.IsInt64) return value.AsInt64;
        if (value.IsDouble) return value.AsDouble;
        if (value.IsDecimal) return value.AsDecimal;
        throw new InvalidDataException($"Unsupported BSON type: {value.Type}");
    }
}

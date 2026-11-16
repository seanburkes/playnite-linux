using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;

namespace Playnite.LegacyImport;

// Raw documents, not port model types: this boundary must never load Core or SQLite.
internal static class LibraryReader
{
    internal static object Read(string source)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Source cannot be a symbolic link.");
        var copies = new List<(string Path, byte[] Bytes)>();
        Capture(source, source, copies);
        using var marker = JsonDocument.Parse(copies.Single(c => c.Path == "database.json").Bytes);
        if (marker.RootElement.GetProperty("Version").GetInt32() != 4)
        {
            throw new InvalidDataException("Only the v10 database format (Version 4) is supported.");
        }
        var files = new List<object>();
        var records = new List<object>();
        // Capture immutable copies before parsing. Opening originals uses only File.OpenRead.
        foreach (var (path, bytes) in copies.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            files.Add(new { path, size = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
            if (Path.GetFileName(path) != path || !path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            using var stream = new MemoryStream(bytes, writable: false);
            using var database = new LiteEngine(stream, null);
            foreach (var collection in database.GetCollectionNames().Order(StringComparer.Ordinal))
            {
                foreach (var document in database.FindAll(collection))
                {
                    records.Add(new { file = path, collection, data = ConvertValue(document) });
                }
            }
        }
        var after = new List<(string Path, byte[] Bytes)>();
        Capture(source, source, after);
        if (after.Count != copies.Count || copies.Any(c => !after.Any(a => a.Path == c.Path && a.Bytes.AsSpan().SequenceEqual(c.Bytes))))
            throw new IOException("Source changed during read; refusing a mixed snapshot.");
        return new { format = "playnite-v10-library", version = 1, files, records };
    }

    private static void Capture(string root, string directory, List<(string Path, byte[] Bytes)> copies)
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
                using var source = File.OpenRead(entry.FullName);
                using var copy = new MemoryStream();
                source.CopyTo(copy);
                copies.Add((Path.GetRelativePath(root, entry.FullName), copy.ToArray()));
            }
        }
    }

    private static object? ConvertValue(BsonValue value)
    {
        if (value.IsNull) return null;
        if (value.IsDocument) return value.AsDocument.ToDictionary(p => p.Key, p => ConvertValue(p.Value), StringComparer.Ordinal);
        if (value.IsArray) return value.AsArray.Select(ConvertValue).ToArray();
        if (value.IsGuid) return value.AsGuid.ToString("D");
        // LiteDB dates are milliseconds since the UTC epoch; never infer a host timezone.
        if (value.IsDateTime) return value.AsDateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
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

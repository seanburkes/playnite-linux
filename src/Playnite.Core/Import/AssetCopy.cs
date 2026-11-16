using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Playnite.Core.Import;

/// <summary>A v10 files-directory reference and its independently captured manifest hash.</summary>
public sealed record AssetReference(string File, string Collection, string Identity, string Field, string Path,
    string? Sha256, long? Size, string Status = "readable");

/// <summary>One result per reference; original filenames survive even when bytes share a blob.</summary>
public sealed record AssetResult(AssetReference Reference, string Outcome, string? Destination,
    int? Width, int? Height, string Dimensions);

/// <summary>Streams immutable source files into a SHA-256 store, publishing only complete verified blobs.</summary>
public static class AssetCopy
{
    public static async Task<IReadOnlyList<AssetResult>> RunAsync(string sourceDirectory, string destinationDirectory,
        IEnumerable<AssetReference> references, bool dryRun = false, Action<string>? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(sourceDirectory) || !Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("Asset source and destination must be absolute.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (source == destination || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Asset destination must be outside the source tree.");
        RejectLinks(source);
        RejectLinks(destination);
        var results = new List<AssetResult>();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = reference.Path.Replace('\\', '/');
            if (Uri.TryCreate(relative, UriKind.Absolute, out _))
            {
                results.Add(new(reference, "external", null, null, null, "unavailable"));
                continue;
            }
            if (relative.StartsWith('/') || relative.Contains(':', StringComparison.Ordinal) || relative.Split('/').Any(p => p is "" or "." or ".."))
                throw new InvalidDataException($"Unsafe asset reference: {reference.Identity}/{reference.Path}");
            var path = Path.Combine(source, "files", relative);
            RejectLinks(path);
            if (reference.Status == "unreadable")
            {
                results.Add(new(reference, "unreadable", null, null, null, "unavailable"));
                continue;
            }
            FileStream input;
            try { input = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (reference.Sha256 != null || reference.Size != null)
                    throw new InvalidDataException($"Source asset disappeared after reader snapshot: {reference.File}/{reference.Collection}/{reference.Identity}/{reference.Path}", exception);
                results.Add(new(reference, "missing", null, null, null, "unavailable"));
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(new(reference, "unreadable", null, null, null, "unavailable"));
                continue;
            }
            await using (input.ConfigureAwait(false))
            {
                if (input.Length == 0)
                {
                    if (reference.Size is > 0) throw new InvalidDataException($"Asset hash mismatch: {reference.Identity}/{reference.Path}");
                    results.Add(new(reference, "empty", null, null, null, "unavailable"));
                    continue;
                }
                if (reference.Sha256 is not { Length: 64 } hash || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToLowerInvariant())
                    throw new InvalidDataException($"Asset lacks a captured SHA-256: {reference.Identity}/{reference.Path}");
                var blob = Path.Combine(destination, "sha256", hash[..2], hash);
                RejectLinks(blob);
                var exists = File.Exists(blob);
                if (exists)
                {
                    await using var stored = new FileStream(blob, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                    if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stored, cancellationToken).ConfigureAwait(false)) != hash)
                        throw new InvalidDataException($"Stored asset hash mismatch; refusing overwrite: {blob}");
                }
                string? temporary = null;
                try
                {
                    if (!dryRun && !exists)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                        temporary = blob + "." + Path.GetRandomFileName() + ".tmp";
                    }
                    await using (var output = temporary == null ? null : new FileStream(temporary, System.IO.FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[65536];
                        long size = 0;
                        int read;
                        while ((read = await ReadSourceAsync(input, buffer, cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            digest.AppendData(buffer, 0, read);
                            size += read;
                            if (output != null)
                            {
                                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                checkpoint?.Invoke("AssetPartialCopy");
                            }
                        }
                        if (size != reference.Size || Convert.ToHexStringLower(digest.GetHashAndReset()) != hash)
                            throw new InvalidDataException($"Asset hash mismatch: {reference.Identity}/{reference.Path}");
                        if (output != null)
                        {
                            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                            output.Flush(flushToDisk: true);
                        }
                    }
                    input.Position = 0;
                    ImageDimensions.Result dimensions;
                    try { dimensions = await ImageDimensions.ReadAsync(input, cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new SourceReadException(exception);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (temporary != null) File.Move(temporary, blob, overwrite: false);
                    results.Add(new(reference, dryRun ? "planned" : exists ? "reused" : "copied",
                        Path.GetRelativePath(Path.GetDirectoryName(destination)!, blob).Replace('\\', '/'),
                        dimensions.Width, dimensions.Height, dimensions.Status));
                }
                catch (SourceReadException)
                {
                    results.Add(new(reference, "unreadable", null, null, null, "unavailable"));
                }
                finally
                {
                    if (temporary != null) File.Delete(temporary);
                }
            }
        }
        return results;
    }

    private sealed class SourceReadException(Exception inner) : IOException("Cannot read source asset.", inner);

    private static async Task<int> ReadSourceAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { return await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SourceReadException(exception);
        }
    }

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? entry = new FileInfo(path); entry != null; entry = new DirectoryInfo(Path.GetDirectoryName(entry.FullName)!))
        {
            if (entry.LinkTarget != null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException($"Symbolic-link asset paths are not supported: {path}");
            if (entry.FullName == Path.GetPathRoot(entry.FullName)) break;
        }
    }
}

internal static class ImageDimensions
{
    internal sealed record Result(int? Width, int? Height, string Status);

    // PNG IHDR, GIF logical screen, JPEG SOF only. No pixels, decompression, or UI assemblies.
    internal static async Task<Result> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[24];
        var length = await input.ReadAtLeastAsync(header, 24, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (length < 24 || !header.AsSpan(8, 8).SequenceEqual(new byte[] { 0, 0, 0, 13, 73, 72, 68, 82 })) return new(null, null, "invalid-header");
            return Size(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
        }
        if (length >= 6 && (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || header.AsSpan(0, 6).SequenceEqual("GIF89a"u8)))
            return length < 10 ? new(null, null, "invalid-header") : Size(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)));
        if (length < 2 || header[0] != 255 || header[1] != 216) return new(null, null, "unsupported");
        input.Position = 2;
        try
        {
            while (input.Position < input.Length)
            {
                await input.ReadExactlyAsync(header.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                if (header[0] != 255) return new(null, null, "invalid-header");
                while (header[1] == 255) await input.ReadExactlyAsync(header.AsMemory(1, 1), cancellationToken).ConfigureAwait(false);
                var marker = header[1];
                if (marker is 0xDA or 0xD9) return new(null, null, "invalid-header");
                if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
                await input.ReadExactlyAsync(header.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadUInt16BigEndian(header);
                if (size < 2 || input.Position + size - 2 > input.Length) return new(null, null, "invalid-header");
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    if (size < 8) return new(null, null, "invalid-header");
                    await input.ReadExactlyAsync(header.AsMemory(0, 5), cancellationToken).ConfigureAwait(false);
                    return Size(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3)), BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1)));
                }
                input.Seek(size - 2, SeekOrigin.Current);
            }
        }
        catch (EndOfStreamException) { return new(null, null, "invalid-header"); }
        return new(null, null, "invalid-header");
    }

    private static Result Size(uint width, uint height) => width is 0 or > int.MaxValue || height is 0 or > int.MaxValue
        ? new(null, null, "invalid-header") : new((int)width, (int)height, "decoded");
}

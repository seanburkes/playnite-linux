using System.Buffers.Binary;
using System.Globalization;
using Avalonia.Media.Imaging;
using Playnite.Platform;
using Playnite.Platform.Linux;
using Playnite.Steam;

namespace Playnite.Desktop.Shell;

internal enum ArtworkSlot
{
    Cover,
    Hero,
}

/// <summary>
/// Resolves local and Steam librarycache artwork safely and decodes images off the UI thread
/// with handle-bound Linux reads and a bounded in-memory LRU byte cache.
/// </summary>
internal sealed class LibraryArtworkLoader : IDisposable
{
    internal const long MaxImageFileBytes = 8 * 1024 * 1024;
    internal const int MaxImageDimension = 16384;
    internal const long MaxImagePixels = 64_000_000;

    private static readonly string[] CoverFileNames =
        ["header.jpg", "library_600x900.jpg", "library_capsule.jpg", "capsule_616x353.jpg"];
    private static readonly string[] HeroFileNames =
        ["library_hero.jpg", "header.jpg"];

    private readonly string databaseDirectory;
    private readonly IReadOnlyList<string>? steamRootsOverride;
    private readonly IReadOnlyList<string>? heroicRootsOverride;
    private readonly int maxCacheEntries;
    private readonly long maxCacheBytes;
    private readonly object cacheLock = new();
    private readonly object steamRootsLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> cacheIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> cacheLru = new();
    private readonly LinuxBoundedFileReader boundedReader = new();

    private IReadOnlyList<string>? discoveredSteamRoots;
    private IReadOnlyList<string>? discoveredHeroicRoots;
    private long cachedBytes;
    private volatile bool disposed;

    public LibraryArtworkLoader(
        string databasePath,
        IReadOnlyList<string>? steamRootsOverride = null,
        int maxCacheEntries = 64,
        long maxCacheBytes = 16 * 1024 * 1024,
        IReadOnlyList<string>? heroicRootsOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheBytes);

        var fullDb = Path.GetFullPath(databasePath);
        databaseDirectory = ResolveRealDirectory(Path.GetDirectoryName(fullDb) ?? Path.GetPathRoot(fullDb)!);
        this.steamRootsOverride = steamRootsOverride;
        this.heroicRootsOverride = heroicRootsOverride;
        this.maxCacheEntries = maxCacheEntries;
        this.maxCacheBytes = maxCacheBytes;
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (cacheLock)
            {
                return cacheIndex.Count;
            }
        }
    }

    internal long CachedBytes
    {
        get
        {
            lock (cacheLock)
            {
                return cachedBytes;
            }
        }
    }

    public string? ResolveArtworkPath(LibraryRow row, ArtworkSlot slot)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (disposed)
        {
            return null;
        }

        var explicitPath = slot == ArtworkSlot.Cover
            ? row.CoverImage
            : !string.IsNullOrWhiteSpace(row.BackgroundImage) ? row.BackgroundImage : row.CoverImage;

        if (!string.IsNullOrWhiteSpace(explicitPath)
            && ResolveExplicitPath(explicitPath) is { } resolvedExplicit)
        {
            return resolvedExplicit;
        }

        if (row.IsSteam
            && uint.TryParse(row.GameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
            && appId > 0)
        {
            return ResolveSteamCachePath(appId, slot);
        }

        return null;
    }

    public Task<Bitmap?> LoadBitmapAsync(
        LibraryRow row,
        ArtworkSlot slot,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeWidth);
        cancellationToken.ThrowIfCancellationRequested();

        if (disposed)
        {
            return Task.FromResult<Bitmap?>(null);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed)
            {
                return null;
            }

            var path = ResolveArtworkPath(row, slot);
            if (path is null || disposed)
            {
                return null;
            }

            try
            {
                var bytes = GetOrReadBytes(path, cancellationToken);
                if (bytes is null || bytes.Length == 0 || disposed)
                {
                    return null;
                }

                if (!ValidateImageDimensions(bytes))
                {
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return DecodeBitmap(bytes, decodeWidth);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsDecodeOrIoFailure(exception))
            {
                return null;
            }
        }, cancellationToken);
    }

    internal Task<byte[]?> GetOrReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => GetOrReadBytes(path, cancellationToken), cancellationToken);
    }

    private byte[]? GetOrReadBytes(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (cacheLock)
        {
            if (disposed)
            {
                return null;
            }

            if (cacheIndex.TryGetValue(path, out var existing))
            {
                cacheLru.Remove(existing);
                cacheLru.AddFirst(existing);
                return existing.Value.Bytes;
            }
        }

        var loaded = ReadValidatedBytes(path, cancellationToken);
        if (loaded is null || loaded.Length == 0 || loaded.Length > maxCacheBytes)
        {
            return loaded;
        }

        lock (cacheLock)
        {
            if (disposed)
            {
                return null;
            }

            if (!cacheIndex.TryGetValue(path, out _))
            {
                var entry = new CacheEntry(path, loaded);
                var node = cacheLru.AddFirst(entry);
                cacheIndex[path] = node;
                cachedBytes += loaded.Length;

                while (cacheIndex.Count > maxCacheEntries || cachedBytes > maxCacheBytes)
                {
                    var oldest = cacheLru.Last;
                    if (oldest is null)
                    {
                        break;
                    }

                    cacheLru.RemoveLast();
                    cacheIndex.Remove(oldest.Value.Key);
                    cachedBytes -= oldest.Value.Bytes.LongLength;
                }
            }
        }

        return loaded;
    }

    private byte[]? ReadValidatedBytes(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsLinux())
        {
            var result = boundedReader.Read(path, (int)MaxImageFileBytes);
            return result.Status == BoundedFileReadStatus.Ok && result.Bytes is { Length: > 0 }
                ? result.Bytes
                : null;
        }

        return null;
    }

    private static Bitmap? DecodeBitmap(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Bitmap.DecodeToWidth(stream, decodeWidth);
    }

    private static bool IsDecodeOrIoFailure(Exception exception)
    {
        if (exception is InvalidOperationException invalidOp
            && invalidOp.Message.Contains("IPlatformRenderInterface", StringComparison.Ordinal))
        {
            return false;
        }

        return exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException
            or ArithmeticException
            or BadImageFormatException;
    }

    internal static bool ValidateImageDimensions(ReadOnlySpan<byte> bytes)
    {
        if (!TryReadImageDimensions(bytes, out var width, out var height))
        {
            // Unknown headers are left to Avalonia/Skia; malformed data returns null during decode.
            return true;
        }

        return width > 0
            && height > 0
            && width <= MaxImageDimension
            && height <= MaxImageDimension
            && (long)width * height <= MaxImagePixels;
    }

    private static bool TryReadImageDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        // PNG signature + IHDR chunk.
        if (bytes.Length >= 24
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A
            && bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
        {
            var w = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
            var h = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
            width = w > int.MaxValue ? int.MaxValue : (int)w;
            height = h > int.MaxValue ? int.MaxValue : (int)h;
            return true;
        }

        // GIF87a / GIF89a logical screen descriptor.
        if (bytes.Length >= 10
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2));
            return true;
        }

        // BMP ("BM") BITMAPINFOHEADER+.
        if (bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            var rawW = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(18, 4));
            var rawH = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(22, 4));
            width = rawW <= 0 ? 0 : rawW;
            height = rawH == int.MinValue ? int.MaxValue : Math.Abs(rawH);
            return true;
        }

        // JPEG SOF0..SOF3, SOF5..SOF7, SOF9..SOF11, SOF13..SOF15.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset] != 0xFF)
                {
                    break;
                }

                while (offset < bytes.Length && bytes[offset] == 0xFF)
                {
                    offset++;
                }

                if (offset >= bytes.Length)
                {
                    break;
                }

                var marker = bytes[offset++];
                if (marker is 0x00 or 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7) or 0x01)
                {
                    continue;
                }

                if (offset + 2 > bytes.Length)
                {
                    break;
                }

                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (segmentLength < 2 || offset + segmentLength > bytes.Length)
                {
                    break;
                }

                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && segmentLength >= 7)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                    return true;
                }

                offset += segmentLength;
            }
        }

        return false;
    }

    private string? ResolveExplicitPath(string rawPath)
    {
        if (rawPath.Contains('\0', StringComparison.Ordinal)
            || rawPath.Contains("://", StringComparison.Ordinal)
            || Uri.TryCreate(rawPath, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile)
        {
            return null;
        }

        var normalized = rawPath.Replace('\\', '/');
        if (Path.IsPathRooted(rawPath))
        {
            if (ResolveSafeExistingFile(rawPath, databaseDirectory) is { } inDb)
            {
                return inDb;
            }

            foreach (var steamRoot in GetSteamRoots())
            {
                if (ResolveSafeExistingFile(rawPath, steamRoot) is { } inSteam)
                {
                    return inSteam;
                }
            }

            foreach (var heroicRoot in GetHeroicRoots())
            {
                if (ResolveSafeExistingFile(rawPath, heroicRoot) is { } inHeroic)
                {
                    return inHeroic;
                }
            }

            return null;
        }

        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        var directCandidate = Path.Combine(databaseDirectory, normalized);
        if (ResolveSafeExistingFile(directCandidate, databaseDirectory) is { } direct)
        {
            return direct;
        }

        var filesCandidate = Path.Combine(databaseDirectory, "files", normalized);
        return ResolveSafeExistingFile(filesCandidate, databaseDirectory);
    }

    private string? ResolveSteamCachePath(uint appId, ArtworkSlot slot)
    {
        var names = slot == ArtworkSlot.Cover ? CoverFileNames : HeroFileNames;
        var appDirFragment = Path.Combine("appcache", "librarycache", appId.ToString(CultureInfo.InvariantCulture));
        foreach (var root in GetSteamRoots())
        {
            var appCacheDir = Path.Combine(root, appDirFragment);
            if (!Directory.Exists(appCacheDir))
            {
                continue;
            }

            foreach (var fileName in names)
            {
                if (ResolveSafeExistingFile(Path.Combine(appCacheDir, fileName), root) is { } directHit)
                {
                    return directHit;
                }
            }

            // Steam 2024+ client cache layout places artwork under 40-char SHA1 subdirectories.
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(appCacheDir);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(subdirectories, StringComparer.Ordinal);
            foreach (var subdir in subdirectories)
            {
                var leaf = Path.GetFileName(subdir);
                if (!IsHex40(leaf))
                {
                    continue;
                }

                foreach (var fileName in names)
                {
                    if (ResolveSafeExistingFile(Path.Combine(subdir, fileName), root) is { } hashedHit)
                    {
                        return hashedHit;
                    }
                }
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetSteamRoots()
    {
        lock (steamRootsLock)
        {
            if (discoveredSteamRoots is not null)
            {
                return discoveredSteamRoots;
            }

            if (steamRootsOverride is not null)
            {
                discoveredSteamRoots = steamRootsOverride
                    .Where(Path.IsPathFullyQualified)
                    .Select(ResolveRealDirectory)
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return discoveredSteamRoots;
            }

            var env = new LinuxEnvironment();
            var paths = new LinuxPaths(env, new LinuxFileSystem());
            var discovery = SteamDiscovery.Discover(env, paths, new SteamFileSystem(boundedReader));
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(discovery.Root) && Directory.Exists(discovery.Root))
            {
                var resolvedRoot = ResolveRealDirectory(discovery.Root);
                if (!string.IsNullOrWhiteSpace(resolvedRoot) && seen.Add(resolvedRoot))
                {
                    roots.Add(resolvedRoot);
                }
            }

            foreach (var library in discovery.Libraries)
            {
                if (library.IsAvailable
                    && !string.IsNullOrWhiteSpace(library.ResolvedPath)
                    && Directory.Exists(library.ResolvedPath))
                {
                    var resolvedLib = ResolveRealDirectory(library.ResolvedPath);
                    if (!string.IsNullOrWhiteSpace(resolvedLib) && seen.Add(resolvedLib))
                    {
                        roots.Add(resolvedLib);
                    }
                }
            }

            discoveredSteamRoots = roots;
            return discoveredSteamRoots;
        }
    }

    private IReadOnlyList<string> GetHeroicRoots()
    {
        lock (steamRootsLock)
        {
            if (discoveredHeroicRoots is not null)
            {
                return discoveredHeroicRoots;
            }

            if (heroicRootsOverride is not null)
            {
                discoveredHeroicRoots = heroicRootsOverride
                    .Where(Path.IsPathFullyQualified)
                    .Select(ResolveRealDirectory)
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return discoveredHeroicRoots;
            }

            var resolved = HeroicLibrarySyncRunner.DiscoverConfigRoots(new LinuxEnvironment());
            if (resolved.Count > 0)
            {
                discoveredHeroicRoots = resolved;
            }

            return resolved;
        }
    }

    private static string? ResolveSafeExistingFile(string candidatePath, string allowedRoot)
    {
        try
        {
            var full = Path.GetFullPath(candidatePath);
            var candidateParent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(candidateParent))
            {
                return null;
            }

            var resolvedCandidateParent = ResolveRealDirectory(candidateParent);
            var candidateCanonical = Path.Combine(resolvedCandidateParent, Path.GetFileName(full));
            if (!IsWithinDirectory(candidateCanonical, allowedRoot))
            {
                return null;
            }

            if (!File.Exists(candidateCanonical))
            {
                return null;
            }

            var fileInfo = new FileInfo(candidateCanonical);
            if (fileInfo.LinkTarget is not null || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var resolvedTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (resolvedTarget is null || !resolvedTarget.Exists)
                {
                    return null;
                }

                full = resolvedTarget.FullName;
                var resolvedParent = ResolveRealDirectory(Path.GetDirectoryName(full)!);
                candidateCanonical = Path.Combine(resolvedParent, Path.GetFileName(full));
                if (!IsWithinDirectory(candidateCanonical, allowedRoot))
                {
                    return null;
                }

                fileInfo = new FileInfo(candidateCanonical);
            }

            if (fileInfo.Length <= 0 || fileInfo.Length > MaxImageFileBytes)
            {
                return null;
            }

            return candidateCanonical;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsWithinDirectory(string candidatePath, string rootDirectory)
    {
        var normalizedRoot = Path.EndsInDirectorySeparator(rootDirectory)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static string ResolveRealDirectory(string directory)
    {
        var path = Path.GetFullPath(directory);
        for (var pass = 0; pass < 16; pass++)
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var current = root;
            var redirected = false;
            var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                if (!Directory.Exists(current))
                {
                    continue;
                }

                var resolved = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (!string.IsNullOrEmpty(resolved)
                    && !string.Equals(Path.TrimEndingDirectorySeparator(resolved), Path.TrimEndingDirectorySeparator(current), StringComparison.Ordinal))
                {
                    path = i + 1 < segments.Length
                        ? Path.Combine([resolved, .. segments[(i + 1)..]])
                        : resolved;
                    redirected = true;
                    break;
                }
            }

            if (!redirected)
            {
                return root.Length > 0 && current == root ? root : Path.TrimEndingDirectorySeparator(current);
            }
        }

        return Path.TrimEndingDirectorySeparator(path);
    }

    private static bool IsHex40(string value)
    {
        if (value.Length != 40)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        lock (cacheLock)
        {
            disposed = true;
            cacheLru.Clear();
            cacheIndex.Clear();
            cachedBytes = 0;
        }
    }

    private sealed record CacheEntry(string Key, byte[] Bytes);
}

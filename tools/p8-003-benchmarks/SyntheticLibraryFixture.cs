using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Steam.Sync;

namespace Playnite.Benchmarks;

/// <summary>Deterministic synthetic SQLite libraries and PNG artwork payloads.</summary>
public static class SyntheticLibraryFixture
{
    private const int BatchSize = 250;
    private static readonly Guid SteamPluginId = new(SteamLibrarySyncService.SteamPluginId);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Guid DeterministicGuid(uint seed, int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes[..4], seed);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..8], index);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..16], 0x5038_3030_3033_0000UL ^ (uint)index);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static async Task CreateSeededLibraryAsync(
        string databasePath,
        int gameCount,
        uint seed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameCount);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var database = await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var random = new Random(unchecked((int)seed));
        var prefixes = new[] { "Synthetic Quest", "Steam Chronicle", "Linux Frontier", "Proton Odyssey", "Orbit Runner" };

        for (var start = 0; start < gameCount; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(gameCount, start + BatchSize);
            await database.WriteAsync(async session =>
            {
                for (var index = start; index < end; index++)
                {
                    var prefix = prefixes[index % prefixes.Length];
                    var game = new Game(
                        DeterministicGuid(seed, index),
                        (100_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        $"{prefix} {index + 1:D4}")
                    {
                        PluginId = SteamPluginId,
                        IsInstalled = (index % 3) != 0,
                        Favorite = (index % 7) == 0,
                        Hidden = (index % 19) == 0,
                        Playtime = (ulong)random.Next(0, 50_000),
                        PlayCount = (ulong)random.Next(0, 120),
                        UserScore = random.Next(40, 100),
                        CriticScore = random.Next(45, 99),
                        CommunityScore = random.Next(50, 98),
                        ReleaseDate = new ReleaseDate(2000 + (index % 27)),
                    };
                    await session.Games.AddAsync(new StoredGame(game)).ConfigureAwait(false);
                }

                return end - start;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Creates a valid 8-bit RGB PNG image whose dimensions and bytes are deterministic.</summary>
    public static byte[] CreateDeterministicPng(int width, int height, uint seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var rowStride = 1 + (width * 3);
        var raw = new byte[rowStride * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowStride;
            raw[rowStart] = 0; // Filter type None
            for (var x = 0; x < width; x++)
            {
                var pixel = rowStart + 1 + (x * 3);
                raw[pixel] = (byte)((x * 7 + y * 3 + seed) & 0xFF);
                raw[pixel + 1] = (byte)((x * 5 + y * 11 + (seed >> 8)) & 0xFF);
                raw[pixel + 2] = (byte)((x * 13 + y * 2 + (seed >> 16)) & 0xFF);
            }
        }

        byte[] compressed;
        using (var zlibBuffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(zlibBuffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = zlibBuffer.ToArray();
        }

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // RGB color type
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace

        using var output = new MemoryStream();
        output.Write(PngSignature);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], payload.Length);
        Encoding.ASCII.GetBytes(type, header[4..8]);
        output.Write(header);
        output.Write(payload);

        var crc = Crc32.Append(0, header[4..8]);
        crc = Crc32.Append(crc, payload);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }
}

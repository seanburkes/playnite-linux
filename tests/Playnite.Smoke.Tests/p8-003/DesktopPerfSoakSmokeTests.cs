using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class DesktopPerfSoakSmokeTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task Headless_library_viewmodel_open_browse_filter_soak_keeps_fds_and_rss_flat()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "playnite-vm-soak-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var dbPath = Path.Combine(tempRoot, "library.sqlite");
            await SeedGamesAsync(dbPath, 200);

            var fdCounts = new List<long>(15);
            var rssSamples = new List<long>(15);
            for (var cycle = 0; cycle < 15; cycle++)
            {
                var vm = new LibraryViewModel(dbPath);
                try
                {
                    await vm.LoadAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(200, vm.TotalCount);
                    Assert.Equal(50, vm.Rows.Count);

                    await vm.LoadPageAsync(50, TestContext.Current.CancellationToken);
                    Assert.Equal(2, vm.CurrentPage);

                    await vm.LoadPageAsync(100, TestContext.Current.CancellationToken);
                    Assert.Equal(3, vm.CurrentPage);

                    await vm.SetSearchTermAsync("Soak", TestContext.Current.CancellationToken);
                    await vm.SetFilterAsync(LibraryFilterMode.Installed, TestContext.Current.CancellationToken);
                    await vm.SetSortAsync(SortOrder.Playtime, descending: true, TestContext.Current.CancellationToken);
                    Assert.True(vm.TotalCount > 0);
                    Assert.True(vm.Rows.Count > 0);
                }
                finally
                {
                    vm.Dispose();
                }

                fdCounts.Add(CountOpenFileDescriptors());
                rssSamples.Add(ReadVmRssKiB());
            }

            AssertSoakFlattened("LibraryViewModel soak", fdCounts, rssSamples);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Avalonia_bitmap_decode_soak_decodes_synthetic_pngs_without_fd_or_rss_growth()
    {
        var pngBytes = CreateDeterministicPng(320, 180, 0x5038_0003);
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(() =>
        {
            var fdSamples = new List<long>(10);
            var rssSamples = new List<long>(10);
            for (var i = 0; i < 100; i++)
            {
                using var stream = new MemoryStream(pngBytes, writable: false);
                using var bitmap = new Bitmap(stream);
                Assert.Equal(320, bitmap.PixelSize.Width);
                Assert.Equal(180, bitmap.PixelSize.Height);

                if (i % 10 == 9)
                {
                    fdSamples.Add(CountOpenFileDescriptors());
                    rssSamples.Add(ReadVmRssKiB());
                }
            }

            AssertSoakFlattened("Avalonia Bitmap decode soak", fdSamples, rssSamples);
        }, TestContext.Current.CancellationToken);
    }

    private static void AssertSoakFlattened(string label, IReadOnlyList<long> fdSamples, IReadOnlyList<long> rssSamples)
    {
        // Exclude sample 0 warm-up; compare steady halves for both FD handles and native/managed RSS.
        var steadyCount = fdSamples.Count - 1;
        var firstHalfCount = Math.Max(1, steadyCount / 2);

        var firstFds = fdSamples.Skip(1).Take(firstHalfCount).ToArray();
        var secondFds = fdSamples.Skip(1 + firstHalfCount).ToArray();
        var fdDelta = secondFds.Max() - firstFds.Min();
        Assert.True(fdDelta <= 6, $"Open FD count grew during {label}: delta={fdDelta} (samples: {string.Join(",", fdSamples)})");

        var firstRss = rssSamples.Skip(1).Take(firstHalfCount).Select(v => (double)v).Order().ToArray();
        var secondRss = rssSamples.Skip(1 + firstHalfCount).Select(v => (double)v).Order().ToArray();
        var firstMedian = firstRss[firstRss.Length / 2];
        var secondMedian = secondRss[secondRss.Length / 2];
        var growthRatio = firstMedian <= 0 ? 0.0 : (secondMedian - firstMedian) / firstMedian;
        Assert.True(growthRatio <= 0.25, $"VmRSS grew during {label}: growth={growthRatio:P2} (samples: {string.Join(",", rssSamples)})");
    }

    private static async Task SeedGamesAsync(string dbPath, int count)
    {
        var db = await LibraryDatabase.OpenAsync(dbPath, TestContext.Current.CancellationToken);
        await db.WriteAsync(async session =>
        {
            for (var i = 0; i < count; i++)
            {
                var id = new Guid($"20000000-0000-4000-8000-{i + 1:X12}");
                var game = new Game(id, $"soak-{i + 1:D4}", $"Soak Title {i + 1:D4}")
                {
                    IsInstalled = i % 2 == 0,
                    Playtime = (ulong)(i * 15),
                };
                await session.Games.AddAsync(new StoredGame(game));
            }

            return count;
        }, TestContext.Current.CancellationToken);
    }

    private static long CountOpenFileDescriptors()
    {
        long count = 0;
        foreach (var _ in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            count++;
        }

        return count;
    }

    private static long ReadVmRssKiB()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line["VmRSS:".Length..].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib))
            {
                return kib;
            }
        }

        throw new InvalidDataException("Missing VmRSS entry in /proc/self/status.");
    }

    private static byte[] CreateDeterministicPng(int width, int height, uint seed)
    {
        var rowStride = 1 + (width * 3);
        var raw = new byte[rowStride * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowStride;
            raw[rowStart] = 0;
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
        ihdr[8] = 8;
        ihdr[9] = 2;

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

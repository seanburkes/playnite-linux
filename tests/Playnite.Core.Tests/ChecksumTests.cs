using System.Text;
using System.Text.Json;
using Playnite;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class ChecksumTests
{
    public static TheoryData<string> Cases
    {
        get
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "checksums-v10.json")));
            var data = new TheoryData<string>();
            foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                data.Add(item.GetRawText());
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Frozen_v10_checksum_expectations_match(string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        string actual;
        var directory = NewDirectory();
        try
        {
            actual = Run(item, directory);
        }
        catch (Exception exception)
        {
            actual = "!" + exception.GetType().Name;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Equal(item.GetProperty("expected").GetString(), actual);
    }

    [Fact]
    public void Crc32_matches_the_published_iso_hdlc_check_vector()
    {
        // CRC-32/ISO-HDLC check value: "123456789" -> 0xCBF43926 (reflected 0xEDB88320).
        Assert.Equal("CBF43926", Crc32.Compute("123456789"u8).ToString("X8"));
    }

    [Fact]
    public void Crc32_empty_input_is_zero_and_does_not_change_a_running_value()
    {
        Assert.Equal(0U, Crc32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0U, Crc32.Append(0, ReadOnlySpan<byte>.Empty));
        var running = Crc32.Compute("abc"u8);
        Assert.Equal(running, Crc32.Append(running, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc32_incremental_updates_compose_in_any_boundary()
    {
        var data = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
        var whole = Crc32.Compute(data);
        for (var split = 0; split <= data.Length; split++)
        {
            var crc = Crc32.Append(0, data.AsSpan(0, split));
            crc = Crc32.Append(crc, data.AsSpan(split));
            Assert.Equal(whole, crc);
        }
    }

    [Fact]
    public void Crc32_large_buffer_split_across_updates_matches_one_pass()
    {
        var data = Pattern(1048576, 31, 7);
        var whole = Crc32.Compute(data);
        foreach (var chunk in new[] { 1, 2, 4095, 4096, 4097, 65536, 100_000, data.Length })
        {
            var crc = 0U;
            for (var offset = 0; offset < data.Length; offset += chunk)
            {
                crc = Crc32.Append(crc, data.AsSpan(offset, Math.Min(chunk, data.Length - offset)));
            }

            Assert.Equal(whole, crc);
        }
    }

    [Fact]
    public void GetCRC32_stream_and_file_overloads_agree()
    {
        var data = Pattern(12000, 17, 3);
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "sample.bin");
            File.WriteAllBytes(path, data);
            var expected = Crc32.Compute(data).ToString("X8");
            Assert.Equal(expected, Checksums.GetCRC32(new MemoryStream(data)));
            Assert.Equal(expected, Checksums.GetCRC32(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Run(JsonElement item, string directory)
    {
        switch (item.GetProperty("op").GetString())
        {
            case "crc32-stream":
                return item.GetProperty("input").ValueKind == JsonValueKind.Null
                    ? Checksums.GetCRC32((Stream)null!)
                    : Checksums.GetCRC32(new MemoryStream(Bytes(item.GetProperty("input"))));
            case "crc32-file":
                return Checksums.GetCRC32(Write(directory, "crc", Bytes(item.GetProperty("input"))));
            case "md5-stream":
                return item.GetProperty("input").ValueKind == JsonValueKind.Null
                    ? Checksums.GetMD5((Stream)null!)
                    : Checksums.GetMD5(new MemoryStream(Bytes(item.GetProperty("input"))));
            case "md5-file":
                return Checksums.GetMD5(Write(directory, "md5", Bytes(item.GetProperty("input"))));
            case "sha256-file":
                return Checksums.GetSHA256(Write(directory, "sha", Bytes(item.GetProperty("input"))));
            case "crc32-append":
            {
                var crc = 0U;
                foreach (var chunk in item.GetProperty("chunks").EnumerateArray())
                {
                    crc = Crc32.Append(crc, Bytes(chunk));
                }

                return crc.ToString("X8");
            }
            case "crc32-append-split":
            {
                var data = Bytes(item.GetProperty("input"));
                var size = item.GetProperty("split").GetInt32();
                var crc = 0U;
                for (var offset = 0; offset < data.Length; offset += size)
                {
                    crc = Crc32.Append(crc, data.AsSpan(offset, Math.Min(size, data.Length - offset)));
                }

                return crc.ToString("X8");
            }
            case "equal":
            {
                var inputs = item.GetProperty("inputs").EnumerateArray().ToArray();
                return Checksums.AreFileContentsEqual(
                    Write(directory, "equal-a", Bytes(inputs[0])),
                    Write(directory, "equal-b", Bytes(inputs[1]))).ToString();
            }
            default:
                throw new ArgumentException("Unknown checksum golden op.");
        }
    }

    private static byte[] Bytes(JsonElement spec)
    {
        if (spec.TryGetProperty("hex", out var hex) && hex.ValueKind == JsonValueKind.String)
        {
            return Convert.FromHexString(hex.GetString()!);
        }

        var pattern = spec.GetProperty("pattern");
        return Pattern(pattern.GetProperty("length").GetInt32(), pattern.GetProperty("step").GetInt32(),
            pattern.GetProperty("offset").GetInt32());
    }

    private static byte[] Pattern(int length, int step, int offset)
    {
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)((index * step + offset) & 0xFF);
        }

        return data;
    }

    private static string Write(string directory, string name, byte[] data)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "playnite-checksums-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

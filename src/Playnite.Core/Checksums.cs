using System.Globalization;
using System.Security.Cryptography;

namespace Playnite;

/// <summary>
/// Adapted from <c>legacy/source/Playnite/Common/FileSystem_Checksum.cs</c>. The hex strings are
/// the exact uppercase, separator-free values v10 returned; MD5 is a legacy cache key, not a
/// security primitive.
/// </summary>
public static class Checksums
{
    public static string GetCRC32(Stream stream)
    {
        var crc = 0U;
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc = Crc32.Append(crc, buffer.AsSpan(0, bytesRead));
        }

        return crc.ToString("X8", CultureInfo.InvariantCulture);
    }

    public static string GetCRC32(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        return GetCRC32(stream);
    }

    public static string GetMD5(Stream stream)
    {
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(stream));
    }

    public static string GetMD5(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        return GetMD5(stream);
    }

    public static string GetSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    public static bool AreFileContentsEqual(string path1, string path2)
    {
        var info1 = new FileInfo(path1);
        var info2 = new FileInfo(path2);
        return info1.Length == info2.Length && GetMD5(path1) == GetMD5(path2);
    }
}

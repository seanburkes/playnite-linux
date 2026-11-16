namespace Playnite;

/// <summary>
/// CRC-32/ISO-HDLC, the reflected IEEE 802.3 polynomial v10 obtained from
/// <c>Force.Crc32.Crc32Algorithm</c> (<c>legacy/source/Playnite/Common/FileSystem_Checksum.cs</c>).
/// </summary>
public static class Crc32
{
    private const uint ReflectedPolynomial = 0xEDB88320U;

    private static readonly uint[] Table = CreateTable();

    /// <summary>
    /// Appends <paramref name="data"/> to a running CRC. <paramref name="crc"/> is the value
    /// returned for the bytes already processed, or zero when there are none, which is the
    /// convention v10's <c>Crc32Algorithm.Append</c> used.
    /// </summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var current = ~crc;
        foreach (var value in data)
        {
            current = Table[(current ^ value) & 0xFF] ^ (current >> 8);
        }

        return ~current;
    }

    /// <summary>The CRC of <paramref name="data"/> in one pass.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) == 0 ? 0U : ReflectedPolynomial);
            }

            table[index] = value;
        }

        return table;
    }
}

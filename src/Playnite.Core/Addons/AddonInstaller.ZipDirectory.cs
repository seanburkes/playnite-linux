using System.Buffers.Binary;

namespace Playnite.Core.Addons;

public sealed partial class AddonInstaller
{
    // ZipArchive materializes every central-directory entry before returning Entries.
    // Walk the fixed headers first, limiting the count even when the ZIP count field lies.
    private static async Task VerifyZipDirectoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int endHeaderLength = 22;
        var tail = new byte[(int)Math.Min(stream.Length, ushort.MaxValue + endHeaderLength)];
        stream.Position = stream.Length - tail.Length;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        for (var offset = tail.Length - endHeaderLength; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) != 0x06054b50
                || ReadZipWord(tail, offset + 20) != tail.Length - offset - endHeaderLength)
            {
                continue;
            }

            var directoryEnd = stream.Length - tail.Length + offset;
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset + 12));
            var directoryStart = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset + 16));
            var entryCount = ReadZipWord(tail, offset + 10);
            if (ReadZipWord(tail, offset + 4) != 0 || ReadZipWord(tail, offset + 6) != 0
                || ReadZipWord(tail, offset + 8) != entryCount
                || (long)directoryStart + directorySize != directoryEnd || entryCount > MaxPackageEntries)
            {
                throw new InvalidDataException("The ZIP directory is invalid or exceeds the package entry limit.");
            }

            stream.Position = directoryStart;
            var entryHeader = new byte[46];
            var count = 0;
            while (stream.Position < directoryEnd)
            {
                if (++count > entryCount || directoryEnd - stream.Position < entryHeader.Length)
                {
                    throw new InvalidDataException("The ZIP directory contains more entries than declared.");
                }
                await stream.ReadExactlyAsync(entryHeader, cancellationToken).ConfigureAwait(false);
                var variableSize = ReadZipWord(entryHeader, 28) + ReadZipWord(entryHeader, 30) + ReadZipWord(entryHeader, 32);
                if (BinaryPrimitives.ReadUInt32LittleEndian(entryHeader) != 0x02014b50
                    || directoryEnd - stream.Position < variableSize)
                {
                    throw new InvalidDataException("The ZIP directory entry is invalid.");
                }
                stream.Position += variableSize;
            }

            if (count != entryCount)
            {
                throw new InvalidDataException("The ZIP directory contains fewer entries than declared.");
            }
            stream.Position = 0;
            return;
        }

        throw new InvalidDataException("The package has no ZIP directory.");
    }

    private static ushort ReadZipWord(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
}

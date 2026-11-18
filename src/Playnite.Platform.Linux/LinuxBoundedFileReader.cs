using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Playnite.Core.Tests")]

namespace Playnite.Platform.Linux;

/// <summary>Linux openat/statx boundary. All descriptors are read-only and owned by SafeHandle.</summary>
public sealed class LinuxBoundedFileReader : IBoundedFileReader
{
    private const int NoFollow = 0x20000;
    private const int CloseOnExec = 0x80000;
    private const int DirectoryOnly = 0x10000;
    private const int PathOnly = 0x200000;
    private const int NonBlock = 0x800;
    private const int NoControllingTerminal = 0x100;

    public BoundedFileRead Read(string path, int maxBytes) => Read(path, maxBytes, null);

    internal BoundedFileRead Read(string path, int maxBytes, Action? afterStat)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Handle-bound reads require the Linux implementation.");
        }

        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("Linux handle-bound reads support x64 and arm64.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            throw new ArgumentException("A canonical absolute path is required.", nameof(path));
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("A canonical file path is required.", nameof(path));
        }

        var directory = Open(-100, "/", PathOnly | DirectoryOnly | NoFollow | CloseOnExec);
        try
        {
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = Open(directory.DangerousGetHandle().ToInt32(), parts[i],
                    PathOnly | DirectoryOnly | NoFollow | CloseOnExec);
                directory.Dispose();
                directory = next;
            }

            // NonBlock prevents a replacement FIFO from blocking even before the handle stat;
            // NoControllingTerminal keeps a device leaf (a TTY) from becoming this process's
            // controlling terminal.
            using var handle = Open(directory.DangerousGetHandle().ToInt32(), parts[^1],
                NoFollow | NonBlock | NoControllingTerminal | CloseOnExec);
            if (Statx(handle, "", 0x1000, 0x201, out var stat) != 0)
            {
                throw Error();
            }

            afterStat?.Invoke();
            if ((stat.Mask & 0x201) != 0x201 || (stat.Mode & 0xF000) != 0x8000 || stat.Size == 0)
            {
                return new(BoundedFileReadStatus.NotRegularFile, null);
            }

            if (stat.Size > (ulong)maxBytes)
            {
                return new(BoundedFileReadStatus.TooLarge, null);
            }

            var buffer = new byte[(int)stat.Size];
            var chunk = new byte[Math.Min(buffer.Length, 8192)];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = ReadNative(handle, chunk, (nuint)Math.Min(chunk.Length, buffer.Length - read));
                if (count < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 4)
                    {
                        continue;
                    }

                    throw Error();
                }

                if (count == 0)
                {
                    break;
                }

                chunk.AsSpan(0, (int)count).CopyTo(buffer.AsSpan(read));
                read += (int)count;
            }

            return new(BoundedFileReadStatus.Ok, read == buffer.Length ? buffer : buffer[..read]);
        }
        catch (FileNotFoundException)
        {
            return new(BoundedFileReadStatus.Missing, null);
        }
        finally
        {
            directory.Dispose();
        }
    }

    private static ReadHandle Open(int directory, string path, int flags)
    {
        var fd = OpenAt(directory, path, flags);
        return fd < 0 ? throw Error() : new ReadHandle(fd);
    }

    private static IOException Error()
    {
        var error = Marshal.GetLastPInvokeError();
        return error == 2 ? new FileNotFoundException("File component missing.")
            : new IOException($"Handle-bound file operation failed (errno {error}).");
    }

    // Linux statx has a fixed, architecture-independent 256-byte ABI (unlike struct stat).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(ReadHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxBuffer stat);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadNative(ReadHandle handle, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(nint handle);

    private sealed class ReadHandle : SafeHandle
    {
        public ReadHandle(int fd) : base(-1, ownsHandle: true) => SetHandle(fd);
        public override bool IsInvalid => handle == -1;
        protected override bool ReleaseHandle() => LinuxBoundedFileReader.Close(handle) == 0;
    }
}

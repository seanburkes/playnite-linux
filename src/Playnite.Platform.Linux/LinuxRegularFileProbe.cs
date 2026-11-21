using System.Runtime.InteropServices;
using Playnite.Core;

namespace Playnite.Platform.Linux;

/// <summary>
/// Linux entry-type probe over <c>openat</c> and <c>statx</c>. The entry is opened with
/// <c>O_PATH</c> so nothing in it is accessed, <c>O_NOFOLLOW</c> so a final symbolic link stays a
/// link, and <c>O_NONBLOCK</c> so a FIFO cannot block the open. The returned file type comes
/// from <c>statx</c>'s <c>stx_mode</c>, not from a managed <see cref="System.IO.FileInfo"/>,
/// which reports every non-directory Unix entry as a file.
/// </summary>
public sealed class LinuxRegularFileProbe : IRegularFileProbe
{
    private const int AtFdCwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const int OpenPath = 0x200000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenNonBlock = 0x800;
    private const int CloseOnExec = 0x80000;
    private const uint StatxType = 0x00000001;
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFile = 0x8000;

    /// <inheritdoc />
    public bool IsRegularFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Entry type classification requires the Linux implementation.");
        }

        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            throw new ArgumentException("A canonical absolute path is required.", nameof(path));
        }

        var fd = OpenAt(AtFdCwd, path, OpenPath | OpenNoFollow | OpenNonBlock | CloseOnExec);
        if (fd < 0)
        {
            return false;
        }

        using var handle = new ProbeHandle(fd);
        if (Statx(handle, string.Empty, AtEmptyPath, StatxType, out var stat) != 0)
        {
            return false;
        }

        return (stat.Mask & StatxType) != 0 && (stat.Mode & FileTypeMask) == RegularFile;
    }

    // Linux statx has a fixed, architecture-independent 256-byte ABI (unlike struct stat), the
    // same layout the reviewed LinuxBoundedFileReader uses.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(ProbeHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxBuffer stat);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(nint handle);

    private sealed class ProbeHandle : SafeHandle
    {
        public ProbeHandle(int fd) : base(-1, ownsHandle: true) => SetHandle(fd);

        public override bool IsInvalid => handle == -1;

        protected override bool ReleaseHandle() => LinuxRegularFileProbe.Close(handle) == 0;
    }
}

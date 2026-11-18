using System.Runtime.InteropServices;

namespace Playnite.Platform.Linux;

/// <summary>A pinned process, never a late lookup by reusable PID.</summary>
public interface IProcessSignalHandle : IDisposable
{
    void Terminate();
}

public interface IProcessSignalHandles
{
    IProcessSignalHandle? Open(int processId);
}

/// <summary>Linux pidfd signalling. SIGTERM only; unsupported kernels/libcs fail closed.</summary>
public sealed class LinuxPidFd : IProcessSignalHandles
{
    public IProcessSignalHandle? Open(int processId)
    {
        int fd;
        try
        {
            fd = PidFdOpen(processId, 0);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("pidfd signalling is required; no unsafe PID fallback is used.", exception);
        }

        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 3) // ESRCH: exited before the handle could be pinned.
            {
                return null;
            }

            throw new IOException($"Could not pin the game process (errno {error}); no signal sent.");
        }

        return new Handle(new PinnedHandle(fd));
    }

    private sealed class Handle(PinnedHandle fd) : IProcessSignalHandle
    {
        public void Terminate()
        {
            if (PidFdSendSignal(fd, 15, IntPtr.Zero, 0) != 0) // SIGTERM, never SIGKILL.
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 3)
                {
                    throw new IOException($"Could not gracefully terminate the pinned game process (errno {error}).");
                }
            }
        }

        public void Dispose() => fd.Dispose();
    }

    private sealed class PinnedHandle : SafeHandle
    {
        public PinnedHandle(int fd) : base(new IntPtr(-1), ownsHandle: true) => SetHandle((IntPtr)fd);
        public override bool IsInvalid => handle.ToInt64() < 0;
        protected override bool ReleaseHandle() => CloseDescriptor(handle.ToInt32()) == 0;
    }

    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int PidFdOpen(int processId, uint flags);

    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static extern int PidFdSendSignal(PinnedHandle fd, int signal, IntPtr info, uint flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseDescriptor(int fd);
}

using System.Diagnostics;

namespace Playnite.Platform.Linux.Sessions;

/// <summary>One owned OS process; the stop path signals before it waits.</summary>
public interface IProcessHandle : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    void KillEntireTree();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>The process-creation seam. The only start form takes a <see cref="LaunchRequest"/>.</summary>
public interface IProcessSystem
{
    IProcessHandle Start(Playnite.Platform.Sessions.LaunchRequest request);
}

/// <summary>Builds the only start form the launcher permits: an argv vector, never a command string.</summary>
public interface IProcessStartInfoFactory
{
    ProcessStartInfo Create(Playnite.Platform.Sessions.LaunchRequest request);
}

public sealed class LinuxProcessStartInfoFactory : IProcessStartInfoFactory
{
    private readonly string setsidPath;

    public LinuxProcessStartInfoFactory(string? setsidPath = null)
    {
        this.setsidPath = setsidPath
            ?? (File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid"
                : File.Exists("/bin/setsid") ? "/bin/setsid"
                : throw new InvalidOperationException("setsid is required for process-group containment."));
    }

    public ProcessStartInfo Create(Playnite.Platform.Sessions.LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = setsidPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        // setsid execs the target directly when it is not already a group leader, so the PID
        // captured after start is the game's PID and its PGID/SID equal that PID.
        startInfo.ArgumentList.Add(request.Executable);
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

public sealed class SystemProcessSystem : IProcessSystem
{
    private readonly IProcessStartInfoFactory factory;

    public SystemProcessSystem()
        : this(new LinuxProcessStartInfoFactory())
    {
    }

    public SystemProcessSystem(IProcessStartInfoFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public IProcessHandle Start(Playnite.Platform.Sessions.LaunchRequest request)
    {
        var startInfo = factory.Create(request);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The operating system did not start the process.");
        return new SystemProcessHandle(process);
    }

    private sealed class SystemProcessHandle : IProcessHandle
    {
        private readonly Process process;

        public SystemProcessHandle(Process process) => this.process = process;

        public int ProcessId => process.Id;

        public bool HasExited => process.HasExited;

        public void KillEntireTree()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}

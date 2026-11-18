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

public sealed class SystemProcessSystem : IProcessSystem
{
    public IProcessHandle Start(Playnite.Platform.Sessions.LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

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

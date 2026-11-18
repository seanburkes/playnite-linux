using System.Diagnostics;
using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// A child process whose lifetime is bound to our stdin pipe. We start the hold command with its
/// stdin redirected and never write to it; closing the pipe is EOF, so the child exits and the
/// inhibitor releases. Kernel process death closes the pipe too, which is why a crash (including
/// SIGKILL) cannot leak the inhibition: no managed cleanup is needed for the release to happen.
/// </summary>
internal sealed class StdinHold : ISleepInhibitorHandle
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(2);

    private readonly Process process;
    private readonly Task<string> standardError;
    private readonly EventHandler processExitHandler;
    private int released;

    private StdinHold(Process process, string mechanism, Task<string> standardError)
    {
        this.process = process;
        this.standardError = standardError;
        Mechanism = mechanism;
        processExitHandler = (_, _) => CloseLifeline();
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;
    }

    public string Mechanism { get; }

    public bool IsHeld => Volatile.Read(ref released) == 0 && !process.HasExited;

    internal bool HasExited => process.HasExited;

    internal bool Released => Volatile.Read(ref released) != 0;

    internal static async Task<StdinHold?> StartAsync(
        ProcessStartInfo startInfo,
        string mechanism,
        TimeSpan startupGrace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startupGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupGrace));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            process.Dispose();
            return null;
        }

        var standardError = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var graceTask = Task.Delay(startupGrace, cancellationToken);
        await Task.WhenAny(exitTask, graceTask).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            process.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }

        if (exitTask.IsCompleted)
        {
            await standardError.ConfigureAwait(false);
            process.Dispose();
            return null;
        }

        return new StdinHold(process, mechanism, standardError);
    }

    /// <summary>Closes the write end of the pipe, which is the entire release signal.</summary>
    internal void CloseLifeline()
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        CloseLifeline();

        try
        {
            await process.WaitForExitAsync().WaitAsync(ReleaseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
            return;
        }

        try
        {
            await standardError.WaitAsync(ReleaseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Dispose();
            return;
        }

        process.Dispose();
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(ReleaseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return;
        }
    }
}

/// <summary>
/// Holds a logind sleep block through <c>systemd-inhibit</c>. The hold command is <c>cat</c>: it
/// exits on stdin EOF, so the lock lives exactly as long as our pipe.
/// </summary>
public sealed class SystemdInhibitBackend : ISleepInhibitorBackend
{
    public const string ExecutableName = "systemd-inhibit";
    public const string HoldCommandName = "cat";

    private static readonly TimeSpan DefaultStartupGrace = TimeSpan.FromMilliseconds(250);

    private readonly IExecutableLocator locator;
    private readonly string executable;
    private readonly string holdCommand;
    private readonly TimeSpan startupGrace;

    public SystemdInhibitBackend(
        IExecutableLocator locator,
        string executable = ExecutableName,
        string holdCommand = HoldCommandName,
        TimeSpan? startupGrace = null)
    {
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.executable = executable;
        this.holdCommand = holdCommand;
        this.startupGrace = startupGrace ?? DefaultStartupGrace;
    }

    public string Mechanism => ExecutableName;

    public bool IsAvailable => locator.Exists(executable) && locator.Exists(holdCommand);

    public async Task<SleepInhibitorAcquireResult> TryAcquireAsync(
        SleepInhibitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable)
        {
            return new SleepInhibitorAcquireResult(null, "systemd-inhibit or the hold command is not installed");
        }

        var hold = await StdinHold
            .StartAsync(CreateStartInfo(request, executable, holdCommand), Mechanism, startupGrace, cancellationToken)
            .ConfigureAwait(false);
        return hold is null
            ? new SleepInhibitorAcquireResult(
                null,
                "systemd-inhibit exited during startup (logind unavailable or the block was refused)")
            : new SleepInhibitorAcquireResult(hold, "sleep inhibition held until the lease is released");
    }

    internal static ProcessStartInfo CreateStartInfo(
        SleepInhibitionRequest request,
        string executable,
        string holdCommand)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--what=sleep");
        startInfo.ArgumentList.Add("--who=" + request.Who);
        startInfo.ArgumentList.Add("--why=" + request.Why);
        startInfo.ArgumentList.Add("--mode=block");
        startInfo.ArgumentList.Add(holdCommand);
        return startInfo;
    }
}

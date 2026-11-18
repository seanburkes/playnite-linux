using Playnite.Platform.Sessions;

namespace Playnite.Platform.Linux.Sessions;

/// <summary>
/// Starts processes with an argv vector and owns every process it launched until it exits or is
/// stopped. Disposal is the application-shutdown path: it terminates all owned processes before
/// returning, so shutdown cannot orphan a game.
/// </summary>
public sealed class LinuxProcessLauncher : IProcessLauncher, IAsyncDisposable
{
    private readonly IProcessSystem processSystem;
    private readonly IProcStatReader statReader;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<ProcessIdentity, LinuxGameProcess> owned = [];
    private bool disposed;

    public LinuxProcessLauncher()
        : this(new SystemProcessSystem(), new LinuxProcStatReader(), TimeProvider.System)
    {
    }

    public LinuxProcessLauncher(IProcessSystem processSystem, IProcStatReader statReader, TimeProvider? timeProvider = null)
    {
        this.processSystem = processSystem ?? throw new ArgumentNullException(nameof(processSystem));
        this.statReader = statReader ?? throw new ArgumentNullException(nameof(statReader));
        time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        var handle = processSystem.Start(request);
        try
        {
            var stat = await statReader.ReadAsync(handle.ProcessId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The launched process exited before its identity could be captured.");
            var identity = new ProcessIdentity(handle.ProcessId, stat.StartTimeTicks);
            var process = new LinuxGameProcess(handle, identity, time.GetUtcNow(), statReader);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owned.Add(identity, process);
            }

            return process;
        }
        catch (OperationCanceledException)
        {
            await ReapFailedStartAsync(handle).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReapFailedStartAsync(handle).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken)
    {
        var game = Owned(process);
        return await game.IsRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(IGameProcess process, CancellationToken cancellationToken)
    {
        var game = Owned(process);
        await game.TerminateAsync(cancellationToken).ConfigureAwait(false);
        // The handle stays registered so a later IsRunningAsync reports false instead of
        // rejecting the process; launcher disposal owns the final handle cleanup.
    }

    public async ValueTask DisposeAsync()
    {
        LinuxGameProcess[] processes;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            processes = owned.Values.ToArray();
            owned.Clear();
        }

        List<Exception>? failures = null;
        foreach (var process in processes)
        {
            try
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            finally
            {
                process.DisposeHandle();
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more launched processes could not be reaped during shutdown.", failures);
        }
    }

    private LinuxGameProcess Owned(IGameProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (gate)
        {
            if (owned.TryGetValue(process.Identity, out var game) && ReferenceEquals(game, process))
            {
                return game;
            }
        }

        throw new ArgumentException("The process was not launched by this launcher.", nameof(process));
    }

    private static async Task ReapFailedStartAsync(IProcessHandle handle)
    {
        try
        {
            handle.KillEntireTree();
            await handle.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The handle is disposed below; a start that already failed has no caller to surface
            // a second failure to, and the original start exception is rethrown.
            _ = exception;
        }
        finally
        {
            handle.Dispose();
        }
    }
}

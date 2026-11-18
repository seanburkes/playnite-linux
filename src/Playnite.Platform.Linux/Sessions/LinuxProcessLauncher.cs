using Playnite.Platform.Sessions;
using DiagnosticsProcess = System.Diagnostics.Process;

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
    private readonly IProcessGroupKiller groupKiller;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<ProcessIdentity, LinuxGameProcess> owned = [];
    private bool disposed;

    public LinuxProcessLauncher()
        : this(new SystemProcessSystem(), new LinuxProcStatReader(), new LinuxProcessGroupKiller(), TimeProvider.System)
    {
    }

    public LinuxProcessLauncher(IProcessSystem processSystem, IProcStatReader statReader, TimeProvider? timeProvider = null)
        : this(processSystem, statReader, new LinuxProcessGroupKiller(), timeProvider)
    {
    }

    public LinuxProcessLauncher(
        IProcessSystem processSystem,
        IProcStatReader statReader,
        IProcessGroupKiller groupKiller,
        TimeProvider? timeProvider = null)
    {
        this.processSystem = processSystem ?? throw new ArgumentNullException(nameof(processSystem));
        this.statReader = statReader ?? throw new ArgumentNullException(nameof(statReader));
        this.groupKiller = groupKiller ?? throw new ArgumentNullException(nameof(groupKiller));
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
        var processId = handle.ProcessId;
        try
        {
            var stat = await WaitForGroupLeaderAsync(processId, cancellationToken).ConfigureAwait(false);
            var identity = new ProcessIdentity(processId, stat.StartTimeTicks, stat.ProcessGroupId);
            var process = new LinuxGameProcess(handle, identity, time.GetUtcNow(), statReader);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owned.Add(identity, process);
            }

            return process;
        }
        catch (Exception startFailure)
        {
            var reapFailure = await ReapFailedStartAsync(handle).ConfigureAwait(false);
            if (reapFailure is not null)
            {
                throw new ProcessOrphanedException(processId, startFailure, reapFailure);
            }

            throw;
        }
    }

    private async Task<ProcStat> WaitForGroupLeaderAsync(int processId, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = await statReader.ReadAsync(processId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The launched process exited before its identity could be captured.");
            if (stat.ProcessGroupId == processId)
            {
                return stat;
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw new InvalidOperationException("The launched process did not enter its own process group.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken)
    {
        var game = Owned(process);
        return await game.IsRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(IGameProcess process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        return StopAsync(process, [process.Identity], cancellationToken);
    }

    public async Task StopAsync(
        IGameProcess gameProcess,
        IReadOnlyCollection<ProcessIdentity> trackedProcesses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trackedProcesses);
        var game = Owned(gameProcess);
        var identities = trackedProcesses.Append(game.Identity).Distinct().ToArray();
        var rootGroup = game.Identity.ProcessGroupId;
        var extras = new List<DiagnosticsProcess>();
        var failures = new List<Exception>();
        var escaped = new List<ProcessIdentity>();

        foreach (var identity in identities)
        {
            ProcStat? stat;
            try
            {
                stat = await statReader.ReadAsync(identity.ProcessId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                failures.Add(exception);
                continue;
            }

            if (stat is not { } row
                || row.ProcessId != identity.ProcessId
                || row.StartTimeTicks != identity.StartTimeTicks
                || !row.IsRunning)
            {
                continue;
            }

            if (rootGroup > 0 && identity != game.Identity && row.ProcessGroupId != rootGroup)
            {
                // The child left the contained group (for example by calling setsid). Do not
                // silently fold it into a successful group stop; report it as a survivor.
                escaped.Add(new ProcessIdentity(identity.ProcessId, row.StartTimeTicks, row.ProcessGroupId));
                continue;
            }

            try
            {
                if (identity == game.Identity)
                {
                    game.KillEntireTree();
                }
                else
                {
                    var extra = DiagnosticsProcess.GetProcessById(identity.ProcessId);
                    if (!extra.HasExited)
                    {
                        extra.Kill(entireProcessTree: true);
                        extras.Add(extra);
                    }
                    else
                    {
                        extra.Dispose();
                    }
                }
            }
            catch (ArgumentException)
            {
                // The process vanished between the stat check and the signal.
                continue;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (rootGroup > 0 && rootGroup == game.Identity.ProcessId)
        {
            try
            {
                await groupKiller.KillAsync(rootGroup, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The group may already be empty; the scan below is the proof, not kill's exit.
                _ = exception;
            }
        }

        var cancelled = false;
        try
        {
            await WaitForAllAsync(game, extras, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            await WaitForAllAsync(game, extras, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var survivors = new List<ProcessIdentity>();
        if (rootGroup > 0 && rootGroup == game.Identity.ProcessId)
        {
            var scan = await ScanGroupAsync(rootGroup, CancellationToken.None).ConfigureAwait(false);
            if (!scan.Complete)
            {
                failures.Add(new InvalidOperationException("The process group could not be fully scanned; survivors are unknown."));
            }

            survivors.AddRange(scan.Running);
        }

        foreach (var identity in identities)
        {
            try
            {
                var stat = await statReader.ReadAsync(identity.ProcessId, CancellationToken.None).ConfigureAwait(false);
                if (stat is { } row
                    && row.ProcessId == identity.ProcessId
                    && row.StartTimeTicks == identity.StartTimeTicks
                    && row.IsRunning)
                {
                    var survivor = new ProcessIdentity(identity.ProcessId, row.StartTimeTicks, row.ProcessGroupId);
                    if (!survivors.Contains(survivor))
                    {
                        survivors.Add(survivor);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                failures.Add(exception);
            }
        }

        foreach (var escapedIdentity in escaped)
        {
            if (!survivors.Contains(escapedIdentity))
            {
                survivors.Add(escapedIdentity);
            }
        }

        foreach (var extra in extras)
        {
            extra.Dispose();
        }

        if (survivors.Count > 0)
        {
            throw new ProcessSurvivorException(
                "The stop left tracked processes outside the reaped group or survivors in it.",
                survivors);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Not every tracked process could be stopped and verified.", failures);
        }

        if (cancelled)
        {
            throw new OperationCanceledException("The stop was cancelled after the process was signalled.", cancellationToken);
        }
    }

    private async Task<(bool Complete, List<ProcessIdentity> Running)> ScanGroupAsync(
        int processGroupId,
        CancellationToken cancellationToken)
    {
        var running = new List<ProcessIdentity>();
        var complete = true;
        foreach (var process in DiagnosticsProcess.GetProcesses())
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stat = await statReader.ReadAsync(process.Id, CancellationToken.None).ConfigureAwait(false);
                if (stat is { } row && row.ProcessGroupId == processGroupId && row.IsRunning)
                {
                    running.Add(new ProcessIdentity(process.Id, row.StartTimeTicks, row.ProcessGroupId));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                complete = false;
                _ = exception;
            }
            finally
            {
                process.Dispose();
            }
        }

        return (complete, running);
    }

    private static async Task WaitForAllAsync(
        LinuxGameProcess game,
        IEnumerable<DiagnosticsProcess> extras,
        CancellationToken cancellationToken)
    {
        await game.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var extra in extras)
        {
            if (!extra.HasExited)
            {
                await extra.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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
        }

        List<Exception>? failures = null;
        foreach (var process in processes)
        {
            try
            {
                await StopAsync(process, [], CancellationToken.None).ConfigureAwait(false);
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

        lock (gate)
        {
            owned.Clear();
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more launched process groups could not be reaped during shutdown.", failures);
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

    private static async Task<Exception?> ReapFailedStartAsync(IProcessHandle handle)
    {
        Exception? failure = null;
        try
        {
            handle.KillEntireTree();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await handle.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        bool running;
        try
        {
            running = !handle.HasExited;
        }
        catch (Exception exception)
        {
            failure ??= exception;
            running = true;
        }

        handle.Dispose();
        return running
            ? failure ?? new InvalidOperationException("The failed start's process is still running.")
            : null;
    }
}

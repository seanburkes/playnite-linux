namespace Playnite.Platform.Sessions;

/// <summary>
/// Tracks one launched process against the <c>GameSession</c> formal model. The tracker owns
/// the process for the session's lifetime: every terminal path (stop, natural exit, cancel,
/// failure, dispose) calls <see cref="IProcessLauncher.StopAsync"/> before reporting the
/// terminal state, so a terminal session never keeps a process handle alive.
/// </summary>
public sealed class GameSessionTracker : IGameSessionTracker
{
    private const string ManualFallbackReason =
        "the platform cannot observe process exit; the session stays running until StopAsync is called";

    private readonly IProcessLauncher launcher;
    private readonly IProcessObserver observer;
    private readonly ISessionDelay sessionDelay;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim terminateGate = new(1, 1);
    private readonly HashSet<ProcessIdentity> known = [];
    private readonly TaskCompletionSource stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource trackFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IGameProcess? process;
    private SessionOptions options = new();
    private GameSessionState state = GameSessionState.Idle;
    private TimeSpan playtime = TimeSpan.Zero;
    private long runningSince;
    private bool observed;
    private bool degraded;
    private string? degradationReason;
    private bool trackingStarted;
    private bool disposed;

    public GameSessionTracker(IProcessLauncher launcher, IProcessObserver observer)
        : this(launcher, observer, new SystemSessionDelay(), TimeProvider.System)
    {
    }

    public GameSessionTracker(
        IProcessLauncher launcher,
        IProcessObserver observer,
        ISessionDelay sessionDelay,
        TimeProvider? timeProvider = null)
    {
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.sessionDelay = sessionDelay ?? throw new ArgumentNullException(nameof(sessionDelay));
        time = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<SessionStateChanged>? StateChanged;

    public GameSessionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool IsDegraded
    {
        get
        {
            lock (gate)
            {
                return degraded;
            }
        }
    }

    public TimeSpan Playtime
    {
        get
        {
            lock (gate)
            {
                return CurrentPlaytimeLocked();
            }
        }
    }

    public async Task TrackAsync(IGameProcess process, SessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (trackingStarted)
            {
                throw new InvalidOperationException("This tracker already owns an active session.");
            }

            if (state != GameSessionState.Idle)
            {
                throw new InvalidOperationException("This tracker has already completed a session.");
            }

            this.process = process;
            this.options = options;
            known.Clear();
            known.Add(process.Identity);
            observed = false;
            trackingStarted = true;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var token = linked.Token;
        try
        {
            Raise(TryTransition(GameSessionState.Starting, "launch accepted"));
            if (token.IsCancellationRequested)
            {
                await CancelAsync(cancellationToken.IsCancellationRequested).ConfigureAwait(false);
                return;
            }

            if (StoppingOrTerminal())
            {
                return;
            }

            Raise(TryTransition(GameSessionState.Running, "process observed"));
            if (!options.ExitObservationSupported)
            {
                NotifyDegraded(true, ManualFallbackReason);
                await stopSignal.Task.WaitAsync(token).ConfigureAwait(false);
                return;
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (StoppingOrTerminal())
                {
                    return;
                }

                var snapshot = await observer.SnapshotAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (StoppingOrTerminal())
                {
                    return;
                }

                ApplySnapshot(snapshot);
                if (State == GameSessionState.Stopped)
                {
                    await EnsureStoppedAsync().ConfigureAwait(false);
                }

                if (StoppingOrTerminal())
                {
                    return;
                }

                await sessionDelay.DelayAsync(options.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await CancelAsync(cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            await EnsureStoppedAsync().ConfigureAwait(false);
            Raise(TryTransition(GameSessionState.Failed, exception.Message));
            stopSignal.TrySetResult();
        }
        finally
        {
            trackFinished.TrySetResult();
        }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        IGameProcess? active;
        lock (gate)
        {
            if (state is not (GameSessionState.Starting or GameSessionState.Running))
            {
                return false;
            }

            active = process;
        }

        if (active is null)
        {
            return false;
        }

        Raise(TryTransition(GameSessionState.Stopping, "manual stop"));
        var cancelled = false;
        try
        {
            await launcher.StopAsync(active, cancellationToken)
                .WaitAsync(options.StopTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try
            {
                await launcher.StopAsync(active, CancellationToken.None)
                    .WaitAsync(options.StopTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await EnsureStoppedAsync().ConfigureAwait(false);
                Raise(TryTransition(GameSessionState.Failed, exception.Message));
                throw;
            }
        }
        catch (Exception exception)
        {
            await EnsureStoppedAsync().ConfigureAwait(false);
            Raise(TryTransition(GameSessionState.Failed, exception.Message));
            throw;
        }

        Raise(TryTransition(GameSessionState.Stopped, "manual stop"));
        stopSignal.TrySetResult();
        if (cancelled)
        {
            throw new OperationCanceledException("The stop was cancelled after the process was signalled.", cancellationToken);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        bool waitForTrack;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            waitForTrack = trackingStarted;
        }

        shutdown.Cancel();
        if (State is GameSessionState.Starting or GameSessionState.Running)
        {
            Raise(TryTransition(GameSessionState.Cancelled, "tracker disposed"));
        }

        await EnsureStoppedAsync().ConfigureAwait(false);
        stopSignal.TrySetResult();
        if (waitForTrack)
        {
            try
            {
                await trackFinished.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                // Disposal must not hang the host; the process was already signalled.
                _ = exception;
            }
        }
    }

    private async Task CancelAsync(bool rethrowCancellation)
    {
        if (State is GameSessionState.Starting or GameSessionState.Running)
        {
            Raise(TryTransition(GameSessionState.Cancelled, "session cancelled"));
        }

        await EnsureStoppedAsync().ConfigureAwait(false);
        stopSignal.TrySetResult();
        if (rethrowCancellation)
        {
            // The caller's token caused this; the terminal state and cleanup are already done.
            throw new OperationCanceledException();
        }
    }

    private async Task EnsureStoppedAsync()
    {
        IGameProcess? active;
        lock (gate)
        {
            active = process;
        }

        if (active is null)
        {
            return;
        }

        await terminateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await launcher.StopAsync(active, CancellationToken.None)
                    .WaitAsync(options.StopTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A terminal session is still reported; the launcher's own disposal retries and
                // the real implementation sends SIGKILL before waiting.
                _ = exception;
            }
        }
        finally
        {
            terminateGate.Release();
        }
    }

    private void ApplySnapshot(ProcessSnapshot snapshot)
    {
        if (!snapshot.IsComplete)
        {
            NotifyDegraded(true, snapshot.IncompleteReason ?? "process observation was incomplete; exit is unknown");
            return;
        }

        if (degraded)
        {
            NotifyDegraded(false, null);
        }

        var alive = snapshot.Processes.Where(entry => entry.IsRunning).ToArray();
        var related = new HashSet<ProcessIdentity>(known);
        related.IntersectWith(alive.Select(entry => entry.Identity));
        var relatedPids = related.Select(identity => identity.ProcessId).ToHashSet();
        while (true)
        {
            var added = false;
            foreach (var entry in alive)
            {
                if (relatedPids.Contains(entry.ParentProcessId) && related.Add(entry.Identity))
                {
                    relatedPids.Add(entry.Identity.ProcessId);
                    added = true;
                }
            }

            if (!added)
            {
                break;
            }
        }

        if (related.Count > 0)
        {
            known.Clear();
            known.UnionWith(related);
            observed = true;
            return;
        }

        known.Clear();
        if (!observed)
        {
            // A complete sample without the root means the launched process exited before the
            // first observation rather than that observation is unavailable.
            observed = true;
        }

        Raise(TryTransition(GameSessionState.Stopping, "process tree ended"));
        Raise(TryTransition(GameSessionState.Stopped, "process tree ended"));
        stopSignal.TrySetResult();
    }

    private void NotifyDegraded(bool value, string? reason)
    {
        var current = State;
        if (current.IsTerminal() || current == GameSessionState.Stopping)
        {
            return;
        }

        lock (gate)
        {
            if (degraded == value && string.Equals(degradationReason, reason, StringComparison.Ordinal))
            {
                return;
            }
        }

        Raise(TryTransition(current, reason, value));
    }

    private bool StoppingOrTerminal()
    {
        var current = State;
        return current == GameSessionState.Stopping || current.IsTerminal();
    }

    private SessionStateChanged? TryTransition(GameSessionState next, string? reason = null, bool? degradedValue = null)
    {
        lock (gate)
        {
            var previous = state;
            if (previous == next)
            {
                if (degradedValue is not { } value || (degraded == value && string.Equals(degradationReason, reason, StringComparison.Ordinal)))
                {
                    return null;
                }

                degraded = value;
                degradationReason = reason;
                return new SessionStateChanged(
                    previous, next, process!.Identity, CurrentPlaytimeLocked(), degraded, reason);
            }

            if (previous.IsTerminal() || !Allowed(previous, next))
            {
                return null;
            }

            if (next == GameSessionState.Running)
            {
                runningSince = time.GetTimestamp();
            }

            var currentPlaytime = CurrentPlaytimeLocked();
            if (next.IsTerminal())
            {
                playtime = currentPlaytime;
                runningSince = 0;
            }

            state = next;
            if (degradedValue is { } newDegraded)
            {
                degraded = newDegraded;
                degradationReason = reason;
            }

            return new SessionStateChanged(
                previous, next, process!.Identity, currentPlaytime, degraded, reason);
        }
    }

    private static bool Allowed(GameSessionState from, GameSessionState to) => (from, to) switch
    {
        (GameSessionState.Idle, GameSessionState.Starting) => true,
        (GameSessionState.Starting, GameSessionState.Running) => true,
        (GameSessionState.Starting, GameSessionState.Stopping) => true,
        (GameSessionState.Starting, GameSessionState.Stopped) => true,
        (GameSessionState.Starting, GameSessionState.Cancelled) => true,
        (GameSessionState.Starting, GameSessionState.Failed) => true,
        (GameSessionState.Running, GameSessionState.Stopping) => true,
        (GameSessionState.Running, GameSessionState.Cancelled) => true,
        (GameSessionState.Running, GameSessionState.Failed) => true,
        (GameSessionState.Stopping, GameSessionState.Stopped) => true,
        (GameSessionState.Stopping, GameSessionState.Failed) => true,
        _ => false,
    };

    private TimeSpan CurrentPlaytimeLocked()
    {
        if (runningSince != 0 && !state.IsTerminal())
        {
            return time.GetElapsedTime(runningSince);
        }

        return playtime;
    }

    private void Raise(SessionStateChanged? change)
    {
        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
    }
}

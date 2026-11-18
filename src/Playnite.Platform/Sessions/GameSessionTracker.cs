namespace Playnite.Platform.Sessions;

/// <summary>
/// Tracks one launched process against the <c>GameSession</c> formal model. The tracker owns
/// the process for the session's lifetime: every terminal transition is preceded by a stop
/// call that waits for exit and verifies the tracked identities are gone. A stop that cannot be
/// proven leaves the session non-terminal and explicitly degraded instead of fabricating a
/// terminal state.
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
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<ProcessIdentity> known = [];
    private readonly TaskCompletionSource stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource trackFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IGameProcess? process;
    private SessionOptions options = new();
    private GameSessionState state = GameSessionState.Idle;
    private TimeSpan playtime = TimeSpan.Zero;
    private long runningSince;
    private bool observedAny;
    private int emptySamples;
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
            observedAny = false;
            emptySamples = 0;
            trackingStarted = true;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var token = linked.Token;
        try
        {
            Raise(TryTransition(GameSessionState.Starting, "launch accepted"));
            if (token.IsCancellationRequested)
            {
                await StopCoreAsync(StopKind.Cancel, CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

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

                if (ApplySnapshot(snapshot) == SnapshotDecision.Ended)
                {
                    await StopCoreAsync(StopKind.Natural, CancellationToken.None).ConfigureAwait(false);
                    stopSignal.TrySetResult();
                    return;
                }

                await sessionDelay.DelayAsync(options.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await StopCoreAsync(StopKind.Cancel, CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            await StopCoreAsync(StopKind.Failure, CancellationToken.None, exception).ConfigureAwait(false);
            stopSignal.TrySetResult();
        }
        finally
        {
            trackFinished.TrySetResult();
        }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        var outcome = await StopCoreAsync(StopKind.Manual, cancellationToken).ConfigureAwait(false);
        if (!outcome.Attempted)
        {
            return false;
        }

        if (!outcome.Reaped)
        {
            throw outcome.Failure ?? new InvalidOperationException("The stop could not be proven complete; the process may still be running.");
        }

        if (outcome.Final == GameSessionState.Failed && outcome.Failure is not null)
        {
            throw outcome.Failure;
        }

        if (outcome.Cancelled)
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
        var outcome = await StopCoreAsync(StopKind.Dispose, CancellationToken.None).ConfigureAwait(false);
        stopSignal.TrySetResult();
        if (outcome.Attempted && !outcome.Reaped)
        {
            throw outcome.Failure ?? new InvalidOperationException("Tracker disposal could not prove the process was reaped.");
        }

        if (waitForTrack)
        {
            try
            {
                await trackFinished.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                // Disposal must not hang the host; the stop path already attempted a verified reap.
                _ = exception;
            }
        }
    }

    private enum StopKind
    {
        Manual,
        Natural,
        Cancel,
        Dispose,
        Failure,
    }

    private enum SnapshotDecision
    {
        Continue,
        Ended,
    }

    private sealed record ReapResult(bool Reaped, bool Cancelled, Exception? Failure);

    private sealed record StopOutcome(
        bool Attempted,
        bool Reaped,
        GameSessionState Final,
        Exception? Failure,
        bool Cancelled);

    private async Task<StopOutcome> StopCoreAsync(StopKind kind, CancellationToken cancellationToken, Exception? cause = null)
    {
        await stopGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            IGameProcess? active;
            lock (gate)
            {
                if (state.IsTerminal() || state == GameSessionState.Stopping)
                {
                    return new StopOutcome(false, state.IsTerminal(), state, null, false);
                }

                if (kind == StopKind.Manual && state is not (GameSessionState.Starting or GameSessionState.Running))
                {
                    return new StopOutcome(false, false, state, null, false);
                }

                active = process;
            }

            if (active is null)
            {
                return new StopOutcome(false, false, State, null, false);
            }

            var stoppingReason = kind switch
            {
                StopKind.Manual => "manual stop",
                StopKind.Natural => "process tree ended",
                StopKind.Cancel => "session cancelled",
                StopKind.Dispose => "tracker disposed",
                _ => cause?.Message ?? "session failed",
            };
            Raise(TryTransition(GameSessionState.Stopping, stoppingReason));

            var reap = kind == StopKind.Manual
                ? await ReapForManualAsync(active, cancellationToken).ConfigureAwait(false)
                : await ReapWithRetriesAsync(active, CancellationToken.None).ConfigureAwait(false);

            if (!reap.Reaped)
            {
                var reason = reap.Failure?.Message ?? cause?.Message
                    ?? "the process could not be reaped; the session stays running and degraded";
                Raise(TryTransition(GameSessionState.Running, reason, true));
                stopSignal.TrySetResult();
                return new StopOutcome(true, false, GameSessionState.Running, reap.Failure ?? cause, reap.Cancelled);
            }

            var final = kind switch
            {
                StopKind.Cancel or StopKind.Dispose => GameSessionState.Cancelled,
                StopKind.Failure => GameSessionState.Failed,
                _ => reap.Failure is null ? GameSessionState.Stopped : GameSessionState.Failed,
            };
            var finalReason = final switch
            {
                GameSessionState.Failed => reap.Failure?.Message ?? cause?.Message ?? "session failed",
                GameSessionState.Cancelled => kind == StopKind.Dispose ? "tracker disposed" : "session cancelled",
                _ => kind == StopKind.Natural ? "process tree ended" : "manual stop",
            };
            Raise(TryTransition(final, finalReason));
            stopSignal.TrySetResult();
            return new StopOutcome(true, true, final, reap.Failure ?? cause, reap.Cancelled);
        }
        finally
        {
            stopGate.Release();
        }
    }

    private async Task<ReapResult> ReapForManualAsync(IGameProcess active, CancellationToken cancellationToken)
    {
        var first = await AttemptStopAsync(active, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return new ReapResult(true, false, null);
        }

        if (first is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            var retry = await AttemptStopAsync(active, CancellationToken.None).ConfigureAwait(false);
            if (retry is null)
            {
                return new ReapResult(true, true, null);
            }

            var ensure = await AttemptStopAsync(active, CancellationToken.None).ConfigureAwait(false);
            return ensure is null
                ? new ReapResult(true, true, retry)
                : new ReapResult(false, true, ensure);
        }

        var second = await AttemptStopAsync(active, CancellationToken.None).ConfigureAwait(false);
        if (second is null)
        {
            return new ReapResult(true, false, first);
        }

        var final = await AttemptStopAsync(active, CancellationToken.None).ConfigureAwait(false);
        return final is null
            ? new ReapResult(true, false, second)
            : new ReapResult(false, false, final);
    }

    private async Task<ReapResult> ReapWithRetriesAsync(IGameProcess active, CancellationToken cancellationToken)
    {
        var first = await AttemptStopAsync(active, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return new ReapResult(true, false, null);
        }

        var second = await AttemptStopAsync(active, CancellationToken.None).ConfigureAwait(false);
        return second is null
            ? new ReapResult(true, false, first)
            : new ReapResult(false, false, second);
    }

    private async Task<Exception?> AttemptStopAsync(IGameProcess active, CancellationToken cancellationToken)
    {
        ProcessIdentity[] tracked;
        lock (gate)
        {
            tracked = known.ToArray();
        }

        try
        {
            await launcher.StopAsync(active, tracked, cancellationToken)
                .WaitAsync(options.StopTimeout, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private SnapshotDecision ApplySnapshot(ProcessSnapshot snapshot)
    {
        if (!snapshot.IsComplete)
        {
            NotifyDegraded(true, snapshot.IncompleteReason ?? "process observation was incomplete; exit is unknown");
            return SnapshotDecision.Continue;
        }

        int[] relevantPids;
        lock (gate)
        {
            relevantPids = [.. known.Select(identity => identity.ProcessId), process!.Identity.ProcessId];
        }

        if (snapshot.Unreadable.Any(pid => relevantPids.Contains(pid)))
        {
            NotifyDegraded(true, "a process relevant to this session could not be read; exit is unknown");
            return SnapshotDecision.Continue;
        }

        if (degraded)
        {
            NotifyDegraded(false, null);
        }

        var alive = snapshot.Processes.Where(entry => entry.IsRunning).ToArray();
        var root = process!.Identity;
        var rootSeen = snapshot.Processes.Any(entry => entry.Identity == root);
        var related = new HashSet<ProcessIdentity>(known);
        related.IntersectWith(alive.Select(entry => entry.Identity));
        // A child read before the root row disappeared may still name the root as its parent in
        // this same sample. Anchor on the root PID so that torn sample does not hide the child.
        var relatedPids = related.Select(identity => identity.ProcessId).ToHashSet();
        relatedPids.Add(root.ProcessId);
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
            lock (gate)
            {
                known.Clear();
                known.UnionWith(related);
                observedAny = true;
                emptySamples = 0;
            }

            return SnapshotDecision.Continue;
        }

        if (rootSeen)
        {
            lock (gate)
            {
                observedAny = true;
            }
        }

        bool unknown;
        int confirmations;
        lock (gate)
        {
            if (rootSeen)
            {
                observedAny = true;
            }

            unknown = !observedAny;
            if (unknown)
            {
                emptySamples = 0;
                confirmations = 0;
            }
            else
            {
                emptySamples++;
                confirmations = emptySamples;
            }
        }

        if (unknown)
        {
            NotifyDegraded(true, "the launched root has not been observed; exit is unknown");
            return SnapshotDecision.Continue;
        }

        return confirmations >= options.ExitConfirmationSamples
            ? SnapshotDecision.Ended
            : SnapshotDecision.Continue;
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
        (GameSessionState.Stopping, GameSessionState.Running) => true,
        (GameSessionState.Stopping, GameSessionState.Stopped) => true,
        (GameSessionState.Stopping, GameSessionState.Cancelled) => true,
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
        if (change is null)
        {
            return;
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<SessionStateChanged>)handler)(this, change);
            }
            catch (Exception exception)
            {
                // A subscriber cannot change session lifecycle or prevent the stop path.
                _ = exception;
            }
        }
    }
}

using System.Globalization;
using Playnite.Core.Database;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;

namespace Playnite.Desktop.Shell;

internal sealed record ActiveGameSession(
    Guid GameId,
    string GameName,
    GameSessionState State,
    bool IsDegraded,
    TimeSpan ElapsedPlaytime,
    string StatusMessage);

internal sealed record GameSessionCompleted(
    Guid GameId,
    string GameName,
    GameSessionState TerminalState,
    ulong AddedPlaytimeSeconds,
    bool PlaytimePersisted,
    bool IsDegraded,
    string StatusMessage);

/// <summary>
/// Coordinates native Linux process launching, /proc session tracking, sleep inhibition,
/// manual stop, shutdown teardown, and atomic SQLite playtime/playcount/last-played updates.
/// </summary>
internal sealed class DesktopGameSessionManager : IAsyncDisposable
{
    private readonly IProcessLauncher launcher;
    private readonly IProcessObserver observer;
    private readonly ISessionDelay? sessionDelay;
    private readonly TimeProvider timeProvider;
    private readonly SessionSleepInhibition sleepInhibition;
    private readonly SessionOptions sessionOptions;
    private readonly bool ownsLauncher;
    private readonly bool ownsSleepInhibition;
    private readonly object stateLock = new();

    private IGameSessionTracker? activeTracker;
    private CancellationTokenSource? activeCts;
    private Task<GameSessionCompleted>? activeRunTask;
    private ActiveGameSession? currentSession;
    private bool sessionBusy;
    private int disposedFlag;

    public DesktopGameSessionManager()
        : this(
            launcher: new LinuxProcessLauncher(),
            observer: new LinuxProcessObserver(),
            sessionDelay: null,
            timeProvider: TimeProvider.System,
            sleepInhibition: new SessionSleepInhibition(LinuxSleepInhibitionService.CreateDefault()),
            sessionOptions: null,
            ownsLauncher: true,
            ownsSleepInhibition: true)
    {
    }

    internal DesktopGameSessionManager(
        IProcessLauncher launcher,
        IProcessObserver observer,
        ISessionDelay? sessionDelay = null,
        TimeProvider? timeProvider = null,
        SessionSleepInhibition? sleepInhibition = null,
        SessionOptions? sessionOptions = null,
        bool ownsLauncher = false,
        bool ownsSleepInhibition = false)
    {
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.sessionDelay = sessionDelay;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.sleepInhibition = sleepInhibition
            ?? new SessionSleepInhibition(LinuxSleepInhibitionService.CreateDefault());
        this.sessionOptions = sessionOptions ?? new SessionOptions();
        this.ownsLauncher = ownsLauncher;
        this.ownsSleepInhibition = ownsSleepInhibition || sleepInhibition is null;
    }

    public event Action<ActiveGameSession>? SessionUpdated;

    public event Action<GameSessionCompleted>? SessionCompleted;

    public ActiveGameSession? CurrentSession
    {
        get
        {
            lock (stateLock)
            {
                return currentSession;
            }
        }
    }

    public bool HasActiveSession => CurrentSession is not null;

    internal bool IsDisposed => Volatile.Read(ref disposedFlag) != 0;

    public Task<GameSessionCompleted> RunSessionAsync(
        string databasePath,
        Guid gameId,
        string gameName,
        LaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        ArgumentNullException.ThrowIfNull(request);

        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposedFlag) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            if (sessionBusy)
            {
                throw new InvalidOperationException("Another game session is already active.");
            }

            sessionBusy = true;
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeCts = linkedCts;
            var run = RunSessionCoreLockedAsync(databasePath, gameId, gameName, request, linkedCts, cancellationToken);
            activeRunTask = run;
            return run;
        }
    }

    private async Task<GameSessionCompleted> RunSessionCoreLockedAsync(
        string databasePath,
        Guid gameId,
        string gameName,
        LaunchRequest request,
        CancellationTokenSource linkedCts,
        CancellationToken callerToken)
    {
        var sessionId = gameId.ToString("D") + "-" + Guid.NewGuid().ToString("N");
        var sessionToken = linkedCts.Token;

        IGameProcess? startedProcess = null;
        IGameSessionTracker? tracker = null;
        var inhibitionStarted = false;
        var reachedRunning = false;
        var reapDegraded = false;
        var sessionFinished = 0;
        GameSessionCompleted? completionToRaise = null;

        try
        {
            UpdateCurrentSession(new ActiveGameSession(
                gameId,
                gameName,
                GameSessionState.Starting,
                IsDegraded: false,
                TimeSpan.Zero,
                $"Starting {gameName}…"));

            try
            {
                await sleepInhibition.BeginSessionAsync(sessionId, sessionToken).ConfigureAwait(false);
                inhibitionStarted = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Sleep inhibition unavailable ({exception.GetType().Name}).");
            }

            startedProcess = await Task.Run(
                () => launcher.StartAsync(request, sessionToken),
                CancellationToken.None).ConfigureAwait(false);

            tracker = sessionDelay is null
                ? new GameSessionTracker(launcher, observer)
                : new GameSessionTracker(launcher, observer, sessionDelay, timeProvider);

            void OnStateChanged(object? _, SessionStateChanged change)
            {
                if (Volatile.Read(ref sessionFinished) != 0)
                {
                    return;
                }

                if (change.State == GameSessionState.Running)
                {
                    reachedRunning = true;
                }

                var message = FormatActiveMessage(gameName, change.State, change.IsDegraded, change.Reason);
                var next = new ActiveGameSession(
                    gameId,
                    gameName,
                    change.State,
                    change.IsDegraded,
                    change.Playtime,
                    message);
                lock (stateLock)
                {
                    if (Volatile.Read(ref sessionFinished) != 0)
                    {
                        return;
                    }

                    currentSession = next;
                }

                SessionUpdated?.Invoke(next);
            }

            tracker.StateChanged += OnStateChanged;
            lock (stateLock)
            {
                activeTracker = tracker;
            }

            var cancelledBeforeTrack = false;
            try
            {
                try
                {
                    // Do not pass sessionToken to Task.Run itself; TrackAsync owns process cancellation/teardown.
                    await Task.Run(
                        () => tracker.TrackAsync(startedProcess, sessionOptions, sessionToken),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (sessionToken.IsCancellationRequested)
                {
                    cancelledBeforeTrack = tracker.State == GameSessionState.Idle;
                    Console.Error.WriteLine($"Game session '{gameName}' interrupted ({exception.GetType().Name}).");
                }
            }
            finally
            {
                tracker.StateChanged -= OnStateChanged;
            }

            if (cancelledBeforeTrack && tracker.State == GameSessionState.Idle)
            {
                try
                {
                    await launcher.StopAsync(startedProcess, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stopEx)
                {
                    reapDegraded = true;
                    Console.Error.WriteLine($"Fallback stop failed ({stopEx.GetType().Name}).");
                }

                if (callerToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(callerToken);
                }
            }
            else if (tracker.State is GameSessionState.Running or GameSessionState.Starting or GameSessionState.Stopping)
            {
                try
                {
                    await tracker.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    reapDegraded = true;
                    Console.Error.WriteLine($"Session stop degraded ({disposeEx.GetType().Name}).");
                }
            }

            var terminalState = cancelledBeforeTrack ? GameSessionState.Cancelled : tracker.State;
            var isDegraded = tracker.IsDegraded || reapDegraded;
            var addedSeconds = reachedRunning
                ? (ulong)Math.Max(0L, (long)Math.Floor(tracker.Playtime.TotalSeconds))
                : 0UL;
            var persisted = false;

            if (reachedRunning)
            {
                persisted = await PersistSessionStatsAsync(
                    databasePath,
                    gameId,
                    addedSeconds,
                    timeProvider.GetUtcNow().UtcDateTime).ConfigureAwait(false);
            }

            var status = FormatCompletedMessage(
                gameName,
                terminalState,
                addedSeconds,
                reachedRunning,
                persisted,
                isDegraded);

            completionToRaise = new GameSessionCompleted(
                gameId,
                gameName,
                terminalState,
                addedSeconds,
                persisted,
                isDegraded,
                status);
            return completionToRaise;
        }
        catch (OperationCanceledException)
        {
            if (startedProcess is not null && (tracker is null || tracker.State == GameSessionState.Idle))
            {
                try
                {
                    await launcher.StopAsync(startedProcess, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stopEx)
                {
                    Console.Error.WriteLine($"Abort stop failed ({stopEx.GetType().Name}).");
                }
            }

            throw;
        }
        catch (Exception exception)
        {
            if (startedProcess is not null && (tracker is null || tracker.State == GameSessionState.Idle))
            {
                try
                {
                    await launcher.StopAsync(startedProcess, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stopEx)
                {
                    Console.Error.WriteLine($"Failure cleanup stop failed ({stopEx.GetType().Name}).");
                }
            }

            completionToRaise = new GameSessionCompleted(
                gameId,
                gameName,
                GameSessionState.Failed,
                0,
                PlaytimePersisted: false,
                IsDegraded: false,
                StatusMessage: $"Could not launch {gameName}: {exception.Message}");
            return completionToRaise;
        }
        finally
        {
            Interlocked.Exchange(ref sessionFinished, 1);

            if (tracker is not null)
            {
                try
                {
                    await tracker.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Tracker cleanup failed ({exception.GetType().Name}).");
                }
            }

            if (inhibitionStarted)
            {
                try
                {
                    await sleepInhibition.EndSessionAsync(sessionId).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Sleep inhibition release failed ({exception.GetType().Name}).");
                }
            }

            lock (stateLock)
            {
                currentSession = null;
                activeTracker = null;
                if (ReferenceEquals(activeCts, linkedCts))
                {
                    activeCts = null;
                }

                activeRunTask = null;
                sessionBusy = false;
            }

            linkedCts.Dispose();

            if (completionToRaise is not null)
            {
                SessionCompleted?.Invoke(completionToRaise);
            }
        }
    }

    public async Task<bool> StopActiveSessionAsync(CancellationToken cancellationToken)
    {
        IGameSessionTracker? tracker;
        lock (stateLock)
        {
            tracker = activeTracker;
        }

        if (tracker is null)
        {
            return false;
        }

        return await tracker.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAndWaitActiveSessionAsync()
    {
        CancellationTokenSource? cts;
        IGameSessionTracker? tracker;
        Task<GameSessionCompleted>? runningTask;
        lock (stateLock)
        {
            cts = activeCts;
            tracker = activeTracker;
            runningTask = activeRunTask;
        }

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException exception)
            {
                Console.Error.WriteLine($"Session cancellation already disposed ({exception.GetType().Name}).");
            }
        }

        if (tracker is not null)
        {
            try
            {
                await tracker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Session stop during teardown failed ({exception.GetType().Name}).");
            }
        }

        if (runningTask is not null)
        {
            try
            {
                await runningTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                Console.Error.WriteLine($"Session aborted during shutdown ({exception.GetType().Name}).");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Session wait failed during shutdown ({exception.GetType().Name}).");
            }
        }
    }

    private void UpdateCurrentSession(ActiveGameSession next)
    {
        lock (stateLock)
        {
            currentSession = next;
        }

        SessionUpdated?.Invoke(next);
    }

    private static async Task<bool> PersistSessionStatsAsync(
        string databasePath,
        Guid gameId,
        ulong addedSeconds,
        DateTime activityUtc)
    {
        try
        {
            var db = await LibraryDatabase.OpenAsync(databasePath, CancellationToken.None).ConfigureAwait(false);
            return await db.WriteAsync(async session =>
            {
                var existing = await session.Games.GetAsync(gameId).ConfigureAwait(false);
                if (existing is null)
                {
                    return false;
                }

                var game = existing.Value;
                game.Playtime = ulong.MaxValue - game.Playtime < addedSeconds
                    ? ulong.MaxValue
                    : game.Playtime + addedSeconds;
                game.PlayCount = game.PlayCount == ulong.MaxValue
                    ? ulong.MaxValue
                    : game.PlayCount + 1;

                game.LastActivity = activityUtc;
                await session.Games.UpdateAsync(new StoredGame(game, existing.LegacyJson)).ConfigureAwait(false);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Playtime persistence failed ({exception.GetType().Name}).");
            return false;
        }
    }

    private static string FormatActiveMessage(
        string gameName,
        GameSessionState state,
        bool isDegraded,
        string? reason) => state switch
    {
        GameSessionState.Starting => $"Starting {gameName}…",
        GameSessionState.Running when isDegraded =>
            $"Running {gameName} (exit tracking degraded; click Stop when finished).",
        GameSessionState.Running => $"Running {gameName}…",
        GameSessionState.Stopping => string.IsNullOrWhiteSpace(reason)
            ? $"Stopping {gameName}…"
            : $"Stopping {gameName} ({reason})…",
        _ => $"Session state for {gameName}: {state}",
    };

    private static string FormatCompletedMessage(
        string gameName,
        GameSessionState state,
        ulong addedSeconds,
        bool reachedRunning,
        bool persisted,
        bool isDegraded)
    {
        var durationText = addedSeconds > 0
            ? string.Create(CultureInfo.InvariantCulture, $" (+{addedSeconds}s)")
            : string.Empty;
        var baseText = state switch
        {
            GameSessionState.Stopped => $"{gameName} exited{durationText}.",
            GameSessionState.Cancelled => $"{gameName} stopped{durationText}.",
            GameSessionState.Failed => $"{gameName} session ended with an error{durationText}.",
            _ => $"{gameName} session {state}{durationText}.",
        };

        if (isDegraded)
        {
            baseText += " Process tracking was degraded.";
        }

        if (reachedRunning && !persisted)
        {
            baseText += " Playtime could not be saved.";
        }

        return baseText;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
        {
            return;
        }

        try
        {
            await CancelAndWaitActiveSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (ownsSleepInhibition)
                {
                    await sleepInhibition.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (ownsLauncher && launcher is IAsyncDisposable disposableLauncher)
                {
                    await disposableLauncher.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}

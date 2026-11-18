using Playnite.Platform.Sessions;

namespace Playnite.Steam;

/// <summary>
/// One Steam request and conservatively attributed game identities. Never owns Steam's group.
/// UNKNOWN requires owner intervention in Steam; it is not a running or successfully stopped game.
/// </summary>
public sealed class SteamProcessLauncher : IProcessLauncher, IProcessObserver
{
    private sealed record Game(ProcessIdentity Identity, DateTimeOffset StartedAt) : IGameProcess;

    private readonly IExternalProcessHost host;
    private readonly SteamLaunchTarget target;
    private readonly SteamLaunchOptions options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessIdentity, byte> known = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessIdentity, byte> descendants = new();
    private ExternalProcessSnapshot? baseline;
    private Game? game;
    private int started;
    private bool disposed;

    public SteamStopReport? StopReport { get; private set; }

    public IReadOnlyCollection<ProcessIdentity> AttributedIdentities => known.Keys.ToArray();
    public IReadOnlyCollection<ProcessIdentity> ObservedDescendants => descendants.Keys.ToArray();

    public SteamProcessLauncher(IExternalProcessHost host, SteamLaunchTarget target, SteamLaunchOptions options,
        TimeProvider? timeProvider = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        time = timeProvider ?? TimeProvider.System;
        if (options.AttributionWindow <= TimeSpan.Zero || options.PollInterval <= TimeSpan.Zero
            || options.ProgressInterval <= TimeSpan.Zero || options.StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (target.App.State is not (SteamAppState.Installed or SteamAppState.UpdatePending))
        {
            throw new ArgumentException("Only available installed games can be launched.", nameof(target));
        }

        ValidateOptionalPath(target.ProtonPrefix);
        ValidateOptionalPath(target.ExecutableOverride);
        if (target.ExecutableOverride is { } executable && !SteamProcessAttribution.Matches(target, executable))
        {
            throw new ArgumentException("An executable selector must be a non-helper game binary under the install directory or app prefix; shells and system binaries are forbidden.", nameof(target));
        }
        _ = CreateRequest();
    }

    public LaunchRequest CreateRequest()
    {
        var appId = target.App.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return options.SteamExecutable is { } steam
            ? new LaunchRequest(steam, ["-applaunch", appId])
            : new LaunchRequest(options.ProtocolOpener, [$"steam://rungameid/{appId}"]);
    }

    public async Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var expected = CreateRequest();
        if (request.Executable != expected.Executable || !request.Arguments.SequenceEqual(expected.Arguments)
            || request.WorkingDirectory is not null)
        {
            throw new ArgumentException("The request must be this target's Steam request.", nameof(request));
        }

        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("A Steam adapter owns only one launch attempt.");
        }

        try
        {
            return await StartCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            startFinished.TrySetResult();
        }
    }

    private async Task<IGameProcess> StartCoreAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        baseline = await host.SnapshotAsync(lifetime.Token).ConfigureAwait(false);
        if (!baseline.IsComplete)
        {
            throw new SteamAttributionUnknownException("UNKNOWN: baseline process coverage is incomplete; Steam was not launched.");
        }

        using var window = new CancellationTokenSource(options.AttributionWindow, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, window.Token);
        var began = time.GetTimestamp();
        var lastProgress = TimeSpan.MinValue;
        var lastWait = "no snapshot taken";
        void Report(string decision, bool terminal = false)
        {
            var elapsed = time.GetElapsedTime(began);
            if (terminal || lastProgress == TimeSpan.MinValue || elapsed - lastProgress >= options.ProgressInterval)
            {
                options.Progress?.Report(new(elapsed, options.AttributionWindow, decision));
                lastProgress = elapsed;
            }
        }

        try
        {
            Report("WAITING: Steam request completion; no game identity owned");
            var requestTask = host.RunAsync(request, linked.Token);
            try
            {
                while (!requestTask.IsCompleted)
                {
                    Report("WAITING: Steam request completion; no game identity owned");
                    await Task.Delay(options.PollInterval, time, linked.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                linked.Cancel();
                // Observe an abandoned request fault without swallowing it: nothing may infer a
                // game start from this outcome, and only attribution can authorize a signal.
                _ = requestTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw;
            }

            if (await requestTask.ConfigureAwait(false) != 0)
            {
                Report("UNKNOWN: Steam request failed; inspect Steam manually", true);
                throw new SteamAttributionUnknownException("UNKNOWN: Steam request failed; inspect Steam and stop manually if necessary.");
            }

            while (true)
            {
                var sample = await host.SnapshotAsync(linked.Token).ConfigureAwait(false);
                var decision = SteamProcessAttribution.Evaluate(target, baseline, sample);
                if (decision.Identity is { } match)
                {
                    game = new Game(match, time.GetUtcNow());
                    var games = GameMembers(sample).ToArray();
                    foreach (var row in games)
                    {
                        known.TryAdd(row.Process.Identity, 0);
                    }

                    ObserveDescendants(sample);

                    // Ownership is established before honoring cancellation: cleanup can now be safe.
                    if (linked.IsCancellationRequested)
                    {
                        await StopAsync(game, CancellationToken.None).ConfigureAwait(false);
                        linked.Token.ThrowIfCancellationRequested();
                    }

                    var tolerated = decision.ToleratedMembers.Count == 0 ? string.Empty
                        : $"; tolerated fresh unreadable member(s) {string.Join(", ", decision.ToleratedMembers)} never signalled";
                    Report($"ATTRIBUTED: unique fresh game pid={match.ProcessId} start_ticks={match.StartTimeTicks} pgid={match.ProcessGroupId} exe={decision.Candidates[0].Executable}; game identities={games.Length}{tolerated}", true);
                    return game;
                }

                lastWait = DescribeWait(decision);
                Report(lastWait);
                await Task.Delay(options.PollInterval, time, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            Report("UNKNOWN: attribution window expired; no unique safe game identity; use Steam manual Stop", true);
            throw new SteamAttributionUnknownException($"UNKNOWN: no unique safe game identity after {time.GetElapsedTime(began).TotalSeconds:F1}s (window {options.AttributionWindow.TotalSeconds:F0}s); last snapshot: {lastWait}; stop/attach manually in Steam.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Report("CANCELLED: attribution stopped; no unattributed process was signalled", true);
            throw;
        }
    }

    public async Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken)
    {
        EnsureOwned(process);
        var sample = await host.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!sample.IsComplete)
        {
            throw new SteamAttributionUnknownException("UNKNOWN: process coverage is incomplete.");
        }

        return sample.Processes.Any(row => row.Process.Identity == process.Identity && row.Process.IsRunning);
    }

    public async Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var active = game ?? throw new InvalidOperationException("No attributed Steam game.");
        var sample = await host.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        ObserveDescendants(sample);
        var members = GroupMembers(sample).ToArray();
        var games = GameMembers(sample).ToArray();

        // A tracked child leaving the group is a residual, not a clean terminal transition.
        var escaped = sample.Processes.Any(row => row.Process.IsRunning
            && known.Keys.Concat(descendants.Keys).Any(identity => SteamProcessAttribution.SameProcess(identity, row.Process.Identity))
            && row.Process.Identity.ProcessGroupId != active.Identity.ProcessGroupId);
        if (escaped)
        {
            return new([], false, "A tracked Steam child left its group; stop manually in Steam");
        }

        if (members.Length == 0)
        {
            return new([], true);
        }

        var tolerated = new List<string>();
        var rejection = SteamProcessAttribution.GroupRejection(target, baseline!, sample,
            active.Identity.ProcessGroupId, tolerated);
        var continuous = games.Any(row => known.ContainsKey(row.Process.Identity) || descendants.ContainsKey(row.Process.Identity));
        if (!sample.IsComplete || rejection is not null || (games.Length > 0 && !continuous))
        {
            return new([], false, $"Steam group is shared or unverifiable: {rejection ?? "new game identity has no observed continuity"}; stop manually in Steam");
        }

        foreach (var row in games)
        {
            known.TryAdd(row.Process.Identity, 0);
        }

        return new(games.Select(row => row.Process).ToArray(), true);
    }

    public Task StopAsync(IGameProcess process, CancellationToken cancellationToken) =>
        StopAsync(process, [process.Identity], cancellationToken);

    public async Task StopAsync(IGameProcess process, IReadOnlyCollection<ProcessIdentity> trackedProcesses,
        CancellationToken cancellationToken)
    {
        EnsureOwned(process);
        await stopGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Tracker input is not authority to signal arbitrary identities. Only policy-verified
            // games observed with continuity in our fresh group enter the allowlist below.
            StopReport = null;
            var deadline = time.GetTimestamp();
            while (true)
            {
                var sample = await host.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                if (!sample.IsComplete)
                {
                    throw new SteamAttributionUnknownException("UNKNOWN: cannot prove attributed game identities stopped; use Steam manual stop.");
                }

                ObserveDescendants(sample);
                var members = GroupMembers(sample).ToArray();
                var escaped = sample.Processes.Where(row => row.Process.IsRunning
                    && known.Keys.Concat(descendants.Keys).Any(identity => SteamProcessAttribution.SameProcess(identity, row.Process.Identity))
                    && row.Process.Identity.ProcessGroupId != process.Identity.ProcessGroupId).ToArray();
                if (escaped.Length > 0)
                {
                    throw new ProcessSurvivorException("Steam child escaped; no unrelated group was signalled.",
                        escaped.Select(row => row.Process.Identity).ToArray());
                }

                var games = GameMembers(sample).ToArray();
                var changed = sample.Processes.Where(row => row.Process.IsRunning && known.ContainsKey(row.Process.Identity)
                    && !SteamProcessAttribution.Matches(target, row.Executable)).ToArray();
                if (changed.Length > 0)
                {
                    throw new ProcessSurvivorException("Attributed identity changed executable; nothing was signalled.",
                        changed.Select(row => row.Process.Identity).ToArray());
                }

                if (games.Length == 0 && members.Length == 0)
                {
                    StopReport = new(false, "Attributed game identities gone; observed group empty", []);
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }

                var tolerated = new List<string>();
                var rejection = SteamProcessAttribution.GroupRejection(target, baseline!, sample,
                    process.Identity.ProcessGroupId, tolerated);
                // Require continuity with a previously observed identity, rejecting PGID reuse.
                if (rejection is not null
                    || (games.Length > 0 && !games.Any(row => known.ContainsKey(row.Process.Identity)
                        || descendants.ContainsKey(row.Process.Identity))))
                {
                    throw new SteamAttributionUnknownException(
                        $"UNKNOWN: {rejection ?? "game identities changed without observed continuity"}; nothing was signalled.");
                }

                if (games.Length == 0)
                {
                    var helpers = members.Select(row => row.Process.Identity).ToArray();
                    StopReport = new(true,
                        $"Attributed game identities gone; Steam-owned helper survivors left unsignalled (group not empty; tolerated unreadable members={tolerated.Count})",
                        helpers);
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }

                foreach (var row in games)
                {
                    known.TryAdd(row.Process.Identity, 0);
                    await host.SignalAsync(row.Process.Identity, row.Executable!, CancellationToken.None).ConfigureAwait(false);
                }

                if (time.GetElapsedTime(deadline) >= options.StopTimeout)
                {
                    throw new ProcessSurvivorException("Attributed game stop could not be proven within the deadline.",
                        games.Select(row => row.Process.Identity).ToArray());
                }

                await Task.Delay(options.PollInterval, time, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Cancel();
        if (Volatile.Read(ref started) != 0)
        {
            await startFinished.Task.ConfigureAwait(false);
        }

        if (game is { } active)
        {
            await StopAsync(active, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string DescribeWait(SteamAttributionDecision decision)
    {
        var seen = decision.Candidates.Count == 0
            ? "candidates=0"
            : $"candidates={decision.Candidates.Count} [{string.Join("; ", decision.Candidates.Select(candidate =>
                $"pid={candidate.Identity.ProcessId} exe={candidate.Executable ?? "<unreadable>"} pgid={candidate.Identity.ProcessGroupId}"))}]";
        if (decision.Rejection is null)
        {
            return $"WAITING: {seen}";
        }

        var tolerated = decision.ToleratedMembers.Count == 0 ? string.Empty
            : $"; tolerated fresh unreadable member(s) {string.Join(", ", decision.ToleratedMembers)} (never signalled)";
        return $"WAITING: {seen}; rejected: {decision.Rejection}{tolerated}; decision UNKNOWN";
    }

    private IEnumerable<ExternalProcessEntry> GroupMembers(ExternalProcessSnapshot sample) => sample.Processes
        .Where(row => row.Process.IsRunning && row.Process.Identity.ProcessGroupId == game!.Identity.ProcessGroupId);

    private IEnumerable<ExternalProcessEntry> GameMembers(ExternalProcessSnapshot sample) => GroupMembers(sample)
        .Where(row => SteamProcessAttribution.Matches(target, row.Executable));

    private void ObserveDescendants(ExternalProcessSnapshot sample)
    {
        var parents = sample.Processes.Where(row => row.Process.IsRunning
            && (known.ContainsKey(row.Process.Identity) || descendants.ContainsKey(row.Process.Identity)))
            .Select(row => row.Process.Identity.ProcessId).ToHashSet();
        while (true)
        {
            var added = false;
            foreach (var row in sample.Processes.Where(row => row.Process.IsRunning
                && parents.Contains(row.Process.ParentProcessId) && !SteamProcessAttribution.IsInfrastructure(row.Executable)))
            {
                descendants.TryAdd(row.Process.Identity, 0);
                added |= parents.Add(row.Process.Identity.ProcessId);
            }

            if (!added) { return; }
        }
    }

    private void EnsureOwned(IGameProcess process)
    {
        if (!ReferenceEquals(process, game))
        {
            throw new ArgumentException("Not this Steam session's attributed game.", nameof(process));
        }
    }

    private static void ValidateOptionalPath(string? path)
    {
        if (path is not null && (!Path.IsPathFullyQualified(path) || path.Contains('\0')))
        {
            throw new ArgumentException("Canonical absolute host paths are required.");
        }
    }
}

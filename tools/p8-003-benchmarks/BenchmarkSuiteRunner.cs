using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Playnite.Core.Database;
using Playnite.Core.Import;
using Playnite.Core.Library;
using Playnite.Core.Queries;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;
using Playnite.Steam.Artwork;

namespace Playnite.Benchmarks;

/// <summary>Executes the p8-003 benchmark suite and resource-steady-state soak loops.</summary>
public static class BenchmarkSuiteRunner
{
    private const int SchemaVersion = 1;
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(5);

    public static Task<BenchmarkReport> RunAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken = default) =>
        RunAsync(options, workspaceParent: null, onWorkspaceCreated: null, cancellationToken);

    internal static async Task<BenchmarkReport> RunAsync(
        BenchmarkOptions options,
        string? workspaceParent,
        Action<string>? onWorkspaceCreated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        BenchmarkReport? baseline = null;
        if (!string.IsNullOrWhiteSpace(options.BaselinePath))
        {
            var baselineJson = await File.ReadAllTextAsync(options.BaselinePath, cancellationToken).ConfigureAwait(false);
            baseline = JsonSerializer.Deserialize<BenchmarkReport>(baselineJson, BenchmarkJson.Options)
                ?? throw new InvalidDataException($"Could not deserialize baseline report from {options.BaselinePath}.");
            if (baseline.Metrics is null || baseline.Metrics.Count == 0)
            {
                throw new InvalidDataException($"Baseline report {options.BaselinePath} contains no metrics.");
            }
        }

        var parentDir = string.IsNullOrWhiteSpace(workspaceParent) ? Path.GetTempPath() : workspaceParent;
        var workspace = Path.Combine(parentDir, "playnite-p8-003-" + Path.GetRandomFileName());
        Directory.CreateDirectory(workspace);
        onWorkspaceCreated?.Invoke(workspace);

        try
        {
            var metrics = new List<MetricSummary>();
            var soakCurves = new List<SoakCurve>();

            if (options.Mode is BenchmarkMode.All or BenchmarkMode.Bench)
            {
                await RunBenchmarksAsync(workspace, options, metrics, cancellationToken).ConfigureAwait(false);
            }

            if (options.Mode is BenchmarkMode.All or BenchmarkMode.Soak)
            {
                await RunSoaksAsync(workspace, options, metrics, soakCurves, cancellationToken).ConfigureAwait(false);
            }

            ToleranceGateResult? gate = null;
            if (baseline is not null)
            {
                gate = ToleranceGate.Compare(
                    metrics,
                    baseline.Metrics,
                    options.WarnThresholdRatio,
                    options.FailThresholdRatio,
                    options.AbsoluteFloorMs);
            }

            var overallPassed = soakCurves.All(c => c.Flattened) && (gate?.Passed ?? true);
            return new BenchmarkReport(
                SchemaVersion,
                TimeProvider.System.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                options.Seed,
                options.LibrarySize,
                metrics,
                soakCurves,
                gate,
                overallPassed);
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private static async Task RunBenchmarksAsync(
        string workspace,
        BenchmarkOptions options,
        List<MetricSummary> metrics,
        CancellationToken cancellationToken)
    {
        var dbPath = Path.Combine(workspace, "bench", "library.sqlite");
        await SyntheticLibraryFixture.CreateSeededLibraryAsync(dbPath, options.LibrarySize, options.Seed, cancellationToken)
            .ConfigureAwait(false);

        var openSamples = new List<double>(options.QueryIterations);
        for (var i = 0; i <= options.QueryIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var db = await LibraryDatabase.OpenAsync(dbPath, cancellationToken).ConfigureAwait(false);
            var service = new LibraryService(db);
            var page = await service.ListAsync(new LibraryQuery(Offset: 0, Limit: 50), cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (page.TotalCount != options.LibrarySize || page.Games.Count != 50)
            {
                throw new InvalidOperationException($"Unexpected library open count: {page.TotalCount}/{page.Games.Count}.");
            }

            if (i > 0)
            {
                openSamples.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        metrics.Add(PercentileCalculator.Summarize("library_open_1000_ms", "ms", "median", openSamples));

        var queryDb = await LibraryDatabase.OpenAsync(dbPath, cancellationToken).ConfigureAwait(false);
        var queryService = new LibraryService(queryDb);
        var installedFilter = LibraryFilter.FromSettings(new FilterSettings { IsInstalled = true });
        var searchSamples = new List<double>(options.QueryIterations);
        for (var i = 0; i <= options.QueryIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var filtered = await queryService.SearchAsync(
                "Quest",
                new LibraryQuery(
                    Filter: installedFilter,
                    SortBy: SortOrder.Playtime,
                    Descending: true,
                    GroupBy: LibraryGroupBy.ReleaseYear,
                    Offset: 0,
                    Limit: 50),
                cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (filtered.TotalCount == 0)
            {
                throw new InvalidOperationException("Expected non-empty filtered search match in synthetic library.");
            }

            if (i > 0)
            {
                searchSamples.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        metrics.Add(PercentileCalculator.Summarize("filter_search_p95_ms", "ms", "p95", searchSamples));

        var artworkSamples = await MeasureArtworkAsync(workspace, options, cancellationToken).ConfigureAwait(false);
        metrics.Add(PercentileCalculator.Summarize("artwork_cache_probe_ms", "ms", "median", artworkSamples));

        var postBenchRss = new double[] { ProcessResourceProbe.ReadVmRssKiB() };
        metrics.Add(PercentileCalculator.Summarize("memory_post_bench_rss_kib", "KiB", "median", postBenchRss));
    }

    private static async Task<List<double>> MeasureArtworkAsync(
        string workspace,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var steamRoot = Path.Combine(workspace, "steam-root");
        var cacheDir = Path.Combine(workspace, "artwork-cache");
        var assetSource = Path.Combine(workspace, "asset-src");
        var assetDest = Path.Combine(workspace, "asset-dest");
        Directory.CreateDirectory(steamRoot);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(Path.Combine(assetSource, "files", "covers"));
        Directory.CreateDirectory(assetDest);

        var payloads = new byte[8][];
        var keys = new string[8];
        var references = new AssetReference[8];
        var cache = new ArtworkCache(cacheDir, maxBytes: 16 * 1024 * 1024, steamRoot);

        for (var i = 0; i < payloads.Length; i++)
        {
            var png = SyntheticLibraryFixture.CreateDeterministicPng(256, 120, options.Seed + (uint)i);
            payloads[i] = png;
            var shaHex = Convert.ToHexStringLower(SHA256.HashData(png));
            var identity = new ArtworkIdentity((uint)(400 + i), ArtworkKind.Header);
            var source = new ArtworkSource(new Uri($"https://example.invalid/artwork/{i}.png"), shaHex);
            keys[i] = ArtworkCache.SourceKey(identity, source);

            var relPath = $"covers/cover-{i}.png";
            await File.WriteAllBytesAsync(Path.Combine(assetSource, "files", relPath), png, cancellationToken).ConfigureAwait(false);
            references[i] = new AssetReference("games.db", "Games", identity.AppId.ToString(CultureInfo.InvariantCulture), "CoverImage", relPath, shaHex, png.LongLength);
        }

        var samples = new List<double>(options.ArtworkIterations);
        for (var i = 0; i <= options.ArtworkIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idx = i % payloads.Length;
            var sw = Stopwatch.StartNew();
            await cache.StoreAsync(keys[idx], payloads[idx], cancellationToken).ConfigureAwait(false);
            var hit = await cache.FindAsync(keys[idx], cancellationToken).ConfigureAwait(false);
            var copied = await AssetCopy.RunAsync(assetSource, assetDest, [references[idx]], dryRun: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();
            if (hit is null || copied.Count != 1 || copied[0].Width != 256 || copied[0].Height != 120)
            {
                throw new InvalidOperationException("Artwork cache/probe verification failed.");
            }

            if (i > 0)
            {
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        return samples;
    }

    private static async Task RunSoaksAsync(
        string workspace,
        BenchmarkOptions options,
        List<MetricSummary> metrics,
        List<SoakCurve> soakCurves,
        CancellationToken cancellationToken)
    {
        var importSamples = new List<SoakSample>(options.SoakCycles);
        var importGamesPerCycle = Math.Min(250, options.LibrarySize);
        for (var cycle = 0; cycle < options.SoakCycles; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycleDir = Path.Combine(workspace, "soak-import", $"cycle-{cycle}");
            var cycleDbPath = Path.Combine(cycleDir, "library.sqlite");
            var sw = Stopwatch.StartNew();
            var completed = false;
            try
            {
                await SyntheticLibraryFixture.CreateSeededLibraryAsync(
                    cycleDbPath,
                    importGamesPerCycle,
                    options.Seed + (uint)cycle,
                    cancellationToken).ConfigureAwait(false);
                var db = await LibraryDatabase.OpenAsync(cycleDbPath, cancellationToken).ConfigureAwait(false);
                var service = new LibraryService(db);
                for (var offset = 0; offset < importGamesPerCycle; offset += 50)
                {
                    _ = await service.ListAsync(new LibraryQuery(Offset: offset, Limit: 50), cancellationToken).ConfigureAwait(false);
                }

                _ = await service.SearchAsync("Quest", new LibraryQuery(Offset: 0, Limit: 50), cancellationToken).ConfigureAwait(false);
                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    TryDeleteDirectory(cycleDir);
                }
            }

            if (Directory.Exists(cycleDir))
            {
                Directory.Delete(cycleDir, recursive: true);
            }

            sw.Stop();
            importSamples.Add(CaptureSample(cycle, sw.Elapsed.TotalMilliseconds));
        }

        soakCurves.Add(ProcessResourceProbe.EvaluateSoakFlattening("soak_import_browse", importSamples));

        var sessionSamples = await RunSyntheticSessionSoakAsync(options.SessionCycles, cancellationToken).ConfigureAwait(false);
        soakCurves.Add(ProcessResourceProbe.EvaluateSoakFlattening("soak_session_lifecycle", sessionSamples));

        if (options.RealProcessCycles >= 3)
        {
            var realSamples = await RunRealProcessSoakAsync(options.RealProcessCycles, cancellationToken).ConfigureAwait(false);
            soakCurves.Add(ProcessResourceProbe.EvaluateSoakFlattening("soak_real_process_lifecycle", realSamples));
        }

        var postSoakRss = new double[] { ProcessResourceProbe.ReadVmRssKiB() };
        metrics.Add(PercentileCalculator.Summarize("memory_post_soak_rss_kib", "KiB", "median", postSoakRss));
    }

    private static async Task<List<SoakSample>> RunSyntheticSessionSoakAsync(
        int sessionCycles,
        CancellationToken cancellationToken)
    {
        var sampleInterval = sessionCycles <= 30 ? 1 : Math.Max(1, sessionCycles / 15);
        var samples = new List<SoakSample>();

        for (var cycle = 0; cycle < sessionCycles; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            await RunSyntheticSessionCycleAsync(cycle, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (cycle == 0 || cycle == sessionCycles - 1 || cycle % sampleInterval == 0)
            {
                samples.Add(CaptureSample(cycle, sw.Elapsed.TotalMilliseconds));
            }
        }

        return samples;
    }

    private static async Task<List<SoakSample>> RunRealProcessSoakAsync(
        int realProcessCycles,
        CancellationToken cancellationToken)
    {
        var sleepPath = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep"
            : File.Exists("/bin/sleep") ? "/bin/sleep"
            : throw new InvalidOperationException("Real process soak cycles require /usr/bin/sleep or /bin/sleep.");

        var sampleInterval = realProcessCycles <= 30 ? 1 : Math.Max(1, realProcessCycles / 15);
        var samples = new List<SoakSample>();

        for (var cycle = 0; cycle < realProcessCycles; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            await RunRealLinuxProcessCycleAsync(sleepPath, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (cycle == 0 || cycle == realProcessCycles - 1 || cycle % sampleInterval == 0)
            {
                samples.Add(CaptureSample(cycle, sw.Elapsed.TotalMilliseconds));
            }
        }

        return samples;
    }

    private static async Task RunSyntheticSessionCycleAsync(int cycle, CancellationToken cancellationToken)
    {
        var root = new ProcessIdentity(1000 + cycle, 50_000 + cycle, 1000 + cycle);
        var process = new SyntheticGameProcess(root);
        var launcher = new SyntheticProcessLauncher(process);
        var mode = cycle % 3;
        var observer = new SyntheticProcessObserver(root, mode == 0);
        await using var tracker = new GameSessionTracker(launcher, observer, new ImmediateSessionDelay());

        if (mode == 0)
        {
            await tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, cancellationToken)
                .ConfigureAwait(false);
            if (tracker.State != GameSessionState.Stopped || process.Running)
            {
                throw new InvalidOperationException("Synthetic natural-exit soak cycle did not stop cleanly.");
            }
        }
        else if (mode == 1)
        {
            var trackTask = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, cancellationToken);
            var stopped = await tracker.StopAsync(cancellationToken).ConfigureAwait(false);
            await trackTask.ConfigureAwait(false);
            if (!stopped || launcher.StopCalls < 1 || tracker.State != GameSessionState.Stopped || process.Running)
            {
                throw new InvalidOperationException("Synthetic manual-stop soak cycle did not stop cleanly.");
            }
        }
        else
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var trackTask = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, linked.Token);
            linked.Cancel();
            try
            {
                await trackTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Expected cancellation path being soak-tested.
            }

            if (tracker.State != GameSessionState.Cancelled || process.Running)
            {
                throw new InvalidOperationException("Synthetic cancel soak cycle left an active process.");
            }
        }
    }

    private static async Task RunRealLinuxProcessCycleAsync(string sleepPath, CancellationToken cancellationToken)
    {
        await using var launcher = new LinuxProcessLauncher();
        var observer = new LinuxProcessObserver();
        await using var tracker = new GameSessionTracker(launcher, observer);
        var process = await launcher.StartAsync(new LaunchRequest(sleepPath, ["30"]), cancellationToken).ConfigureAwait(false);
        var trackTask = tracker.TrackAsync(
            process,
            new SessionOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                StopTimeout = ProcessStopTimeout,
            },
            cancellationToken);

        try
        {
            var stopped = await tracker.StopAsync(cancellationToken).ConfigureAwait(false);
            await trackTask.ConfigureAwait(false);
            if (!stopped || tracker.State != GameSessionState.Stopped)
            {
                throw new InvalidOperationException($"Real process soak cycle ended in state {tracker.State} (stopped={stopped}).");
            }

            if (await launcher.IsRunningAsync(process, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Real process soak cycle left the child process running.");
            }
        }
        catch
        {
            try
            {
                using var cleanup = new CancellationTokenSource(ProcessStopTimeout);
                await launcher.StopAsync(process, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Best-effort cleanup before rethrowing original failure.
            }

            throw;
        }
    }

    private static SoakSample CaptureSample(int cycle, double elapsedMs) =>
        new(
            cycle,
            ProcessResourceProbe.ReadOpenFileDescriptorCount(),
            ProcessResourceProbe.ReadVmRssKiB(),
            ProcessResourceProbe.ReadManagedHeapBytes(),
            elapsedMs);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup failure must not mask the primary error.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cleanup failure must not mask the primary error.
        }
    }

    private sealed class SyntheticGameProcess(ProcessIdentity identity) : IGameProcess
    {
        public ProcessIdentity Identity { get; } = identity;
        public DateTimeOffset StartedAt { get; } = TimeProvider.System.GetUtcNow();
        public bool Running { get; set; } = true;
    }

    private sealed class SyntheticProcessLauncher(SyntheticGameProcess process) : IProcessLauncher
    {
        public int StopCalls { get; private set; }

        public Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IGameProcess>(process);

        public Task<bool> IsRunningAsync(IGameProcess target, CancellationToken cancellationToken) =>
            Task.FromResult(process.Running);

        public Task StopAsync(IGameProcess target, CancellationToken cancellationToken)
        {
            StopCalls++;
            process.Running = false;
            return Task.CompletedTask;
        }

        public Task StopAsync(
            IGameProcess target,
            IReadOnlyCollection<ProcessIdentity> trackedProcesses,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            process.Running = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            process.Running = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyntheticProcessObserver(ProcessIdentity identity, bool stopImmediately) : IProcessObserver
    {
        private int calls;

        public Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            calls++;
            if (stopImmediately && calls >= 2)
            {
                return Task.FromResult(new ProcessSnapshot([], true));
            }

            return Task.FromResult(new ProcessSnapshot([new ProcessEntry(identity, 1, true)], true));
        }
    }

    private sealed class ImmediateSessionDelay : ISessionDelay
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.Yield().AsTask();
    }
}

internal static class TaskYieldExtensions
{
    public static async Task AsTask(this System.Runtime.CompilerServices.YieldAwaitable yieldAwaitable) =>
        await yieldAwaitable;
}

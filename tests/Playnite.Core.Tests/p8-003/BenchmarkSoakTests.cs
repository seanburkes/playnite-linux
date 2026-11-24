using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Playnite.Benchmarks;
using Playnite.Core.Import;
using Xunit;

namespace Playnite.Core.Tests.Benchmarks;

public sealed class BenchmarkSoakTests
{
    [Fact]
    public void Options_reject_vacuous_or_invalid_budgets_and_accept_zero_real_process_cycles()
    {
        (new BenchmarkOptions { RealProcessCycles = 0 }).Validate();

        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { Mode = (BenchmarkMode)999 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { LibrarySize = 50 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { QueryIterations = 4 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { SoakCycles = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { SessionCycles = 4 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { RealProcessCycles = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { RealProcessCycles = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { ArtworkIterations = 9 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { WarnThresholdRatio = -0.01 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { WarnThresholdRatio = double.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { WarnThresholdRatio = 0.25, FailThresholdRatio = 0.20 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { FailThresholdRatio = double.NaN }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { FailThresholdRatio = double.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { AbsoluteFloorMs = -0.1 }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { AbsoluteFloorMs = double.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { BaselinePath = "   " }).Validate());
        Assert.Throws<ArgumentException>(() => (new BenchmarkOptions { OutputPath = string.Empty }).Validate());
    }

    [Fact]
    public void Percentile_calculator_computes_exact_median_p95_min_and_max()
    {
        var single = PercentileCalculator.Summarize("single_ms", "ms", "median", [4.5]);
        Assert.Equal(4.5, single.Median);
        Assert.Equal(4.5, single.P95);
        Assert.Equal(4.5, single.GateValue);

        var odd = PercentileCalculator.Summarize("odd_ms", "ms", "median", [9.0, 1.0, 5.0]);
        Assert.Equal(5.0, odd.Median);
        Assert.Equal(9.0, odd.P95);
        Assert.Equal(5.0, odd.GateValue);

        var samples = new double[20];
        for (var i = 0; i < 20; i++)
        {
            samples[i] = 20 - i;
        }

        var summary = PercentileCalculator.Summarize("test_metric_ms", "ms", "p95", samples);
        Assert.Equal("test_metric_ms", summary.Name);
        Assert.Equal("ms", summary.Unit);
        Assert.Equal("p95", summary.GateStatistic);
        Assert.Equal(1.0, summary.Min);
        Assert.Equal(20.0, summary.Max);
        Assert.Equal(10.5, summary.Median);
        Assert.Equal(19.0, summary.P95);
        Assert.Equal(19.0, summary.GateValue);
        Assert.Equal(20, summary.Samples);

        Assert.Throws<ArgumentException>(() => PercentileCalculator.Summarize("bad", "ms", "avg", samples));
        Assert.Throws<ArgumentException>(() => PercentileCalculator.Summarize("empty", "ms", "median", []));
        Assert.Throws<ArgumentException>(() => PercentileCalculator.Summarize("nan", "ms", "median", [1.0, double.NaN]));
        Assert.Throws<ArgumentException>(() => PercentileCalculator.Summarize("inf", "ms", "median", [1.0, double.PositiveInfinity]));
    }

    [Fact]
    public void Tolerance_gate_handles_boundaries_mismatches_missing_metrics_and_rejects_invalid_values()
    {
        var baseline = new[]
        {
            new MetricSummary("open_ms", "ms", "median", 10.0, 12.0, 9.0, 13.0, 10),
            new MetricSummary("search_p95_ms", "ms", "p95", 10.0, 20.0, 8.0, 22.0, 10),
            new MetricSummary("subms_jitter_ms", "ms", "median", 0.10, 0.15, 0.08, 0.20, 10),
            new MetricSummary("zero_base", "KiB", "median", 0.0, 0.0, 0.0, 0.0, 1),
            new MetricSummary("present_in_baseline_only", "KiB", "median", 1000, 1000, 1000, 1000, 1),
        };

        // Exact boundary equalities: +10% stays pass, +20% stays warn, sub-floor preserves actual deltaRatio.
        var boundary = new[]
        {
            new MetricSummary("open_ms", "ms", "median", 11.0, 13.0, 9.0, 14.0, 10),
            new MetricSummary("search_p95_ms", "ms", "p95", 10.0, 24.0, 8.0, 25.0, 10),
            new MetricSummary("subms_jitter_ms", "ms", "median", 0.20, 0.24, 0.10, 0.25, 10),
            new MetricSummary("zero_base", "KiB", "median", 0.0, 0.0, 0.0, 0.0, 1),
            new MetricSummary("present_in_baseline_only", "KiB", "median", 1000, 1000, 1000, 1000, 1),
        };
        var boundaryResult = ToleranceGate.Compare(boundary, baseline);
        Assert.True(boundaryResult.Passed);
        Assert.True(boundaryResult.HasWarnings);
        Assert.Equal("pass", boundaryResult.Comparisons.Single(c => c.Metric == "open_ms").Status);
        Assert.Equal("warn", boundaryResult.Comparisons.Single(c => c.Metric == "search_p95_ms").Status);
        var jitter = boundaryResult.Comparisons.Single(c => c.Metric == "subms_jitter_ms");
        Assert.Equal("pass", jitter.Status);
        Assert.Equal(1.0, jitter.DeltaRatio, precision: 6);

        // Missing, unit-mismatch, statistic-mismatch, and zero->nonzero failure.
        var mismatched = new[]
        {
            new MetricSummary("open_ms", "KiB", "median", 10.0, 12.0, 9.0, 13.0, 10),
            new MetricSummary("search_p95_ms", "ms", "median", 10.0, 20.0, 8.0, 22.0, 10),
            new MetricSummary("subms_jitter_ms", "ms", "median", 0.20, 0.24, 0.10, 0.25, 10),
            new MetricSummary("zero_base", "KiB", "median", 5.0, 5.0, 5.0, 5.0, 1),
        };
        var mismatchResult = ToleranceGate.Compare(mismatched, baseline);
        Assert.False(mismatchResult.Passed);
        Assert.Equal("unit-mismatch", mismatchResult.Comparisons.Single(c => c.Metric == "open_ms").Status);
        Assert.Equal("statistic-mismatch", mismatchResult.Comparisons.Single(c => c.Metric == "search_p95_ms").Status);
        Assert.Equal("fail", mismatchResult.Comparisons.Single(c => c.Metric == "zero_base").Status);
        Assert.Equal("missing-metric", mismatchResult.Comparisons.Single(c => c.Metric == "present_in_baseline_only").Status);

        // Reject empty, duplicate, negative, and non-finite metrics.
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare(boundary, []));
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare([boundary[0], boundary[0]], [baseline[0]]));
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare([boundary[0]], [baseline[0], baseline[0]]));
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare([boundary[0] with { Median = double.NaN }], [baseline[0]]));
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare([boundary[0]], [baseline[0] with { Median = double.PositiveInfinity }]));
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare([boundary[0] with { Median = -1.0 }], [baseline[0]]));
        Assert.Throws<ArgumentException>(() => ToleranceGate.Compare([boundary[0]], [baseline[0] with { Median = -5.0 }]));
    }

    [Fact]
    public void Soak_flattening_accepts_flat_curve_rejects_leaks_and_validates_inputs()
    {
        var flatSamples = new[]
        {
            new SoakSample(0, 18, 40_000, 2_000_000, 5.0),
            new SoakSample(1, 20, 44_000, 2_100_000, 4.0),
            new SoakSample(2, 21, 44_500, 2_150_000, 4.1),
            new SoakSample(3, 20, 44_600, 2_120_000, 4.0),
            new SoakSample(4, 21, 44_700, 2_130_000, 4.2),
        };
        var flat = ProcessResourceProbe.EvaluateSoakFlattening("flat_test", flatSamples);
        Assert.True(flat.Flattened);
        Assert.Equal("flat", flat.Verdict);

        var fdLeak = ProcessResourceProbe.EvaluateSoakFlattening("fd_leak", [
            new SoakSample(0, 18, 40_000, 2_000_000, 5.0),
            new SoakSample(1, 20, 44_000, 2_100_000, 4.0),
            new SoakSample(2, 21, 44_100, 2_100_000, 4.0),
            new SoakSample(3, 28, 44_200, 2_100_000, 4.0),
            new SoakSample(4, 34, 44_200, 2_100_000, 4.0),
        ]);
        Assert.False(fdLeak.Flattened);
        Assert.Equal("fd-growth", fdLeak.Verdict);

        var rssLeak = ProcessResourceProbe.EvaluateSoakFlattening("rss_leak", [
            new SoakSample(0, 18, 40_000, 2_000_000, 5.0),
            new SoakSample(1, 20, 40_000, 2_100_000, 4.0),
            new SoakSample(2, 20, 42_000, 2_100_000, 4.0),
            new SoakSample(3, 20, 60_000, 2_100_000, 4.0),
            new SoakSample(4, 20, 68_000, 2_100_000, 4.0),
        ]);
        Assert.False(rssLeak.Flattened);
        Assert.Equal("rss-growth", rssLeak.Verdict);

        var bothLeak = ProcessResourceProbe.EvaluateSoakFlattening("both_leak", [
            new SoakSample(0, 18, 40_000, 2_000_000, 5.0),
            new SoakSample(1, 20, 40_000, 2_100_000, 4.0),
            new SoakSample(2, 21, 40_500, 2_100_000, 4.0),
            new SoakSample(3, 30, 65_000, 2_100_000, 4.0),
            new SoakSample(4, 35, 70_000, 2_100_000, 4.0),
        ]);
        Assert.False(bothLeak.Flattened);
        Assert.Equal("fd-and-rss-growth", bothLeak.Verdict);

        Assert.Throws<ArgumentException>(() => ProcessResourceProbe.EvaluateSoakFlattening("too_short", flatSamples[..2]));
        Assert.Throws<ArgumentException>(() => ProcessResourceProbe.EvaluateSoakFlattening("neg_fd", [
            new SoakSample(0, -1, 40_000, 1, 1.0),
            new SoakSample(1, 10, 40_000, 1, 1.0),
            new SoakSample(2, 10, 40_000, 1, 1.0),
        ]));
    }

    [Fact]
    public void Proc_resource_probe_reads_live_stats_and_rejects_missing_or_malformed_proc()
    {
        var liveFds = ProcessResourceProbe.ReadOpenFileDescriptorCount();
        var liveRss = ProcessResourceProbe.ReadVmRssKiB();
        var liveHwm = ProcessResourceProbe.ReadVmHwmKiB();
        var liveHeap = ProcessResourceProbe.ReadManagedHeapBytes();
        Assert.True(liveFds > 0);
        Assert.True(liveRss > 0);
        Assert.True(liveHwm >= liveRss);
        Assert.True(liveHeap > 0);

        var tempProc = Path.Combine(Path.GetTempPath(), "playnite-proc-test-" + Path.GetRandomFileName());
        try
        {
            Assert.Throws<InvalidOperationException>(() => ProcessResourceProbe.ReadOpenFileDescriptorCount(tempProc));
            Assert.Throws<InvalidOperationException>(() => ProcessResourceProbe.ReadVmRssKiB(tempProc));

            Directory.CreateDirectory(Path.Combine(tempProc, "fd"));
            File.WriteAllText(Path.Combine(tempProc, "fd", "0"), string.Empty);
            File.WriteAllText(Path.Combine(tempProc, "fd", "1"), string.Empty);
            File.WriteAllText(Path.Combine(tempProc, "fd", "2"), string.Empty);
            File.WriteAllText(Path.Combine(tempProc, "status"), "Name:\tplaynite\nVmHWM:\t  65432 kB\nVmRSS:\t  54321 kB\n");

            Assert.Equal(3, ProcessResourceProbe.ReadOpenFileDescriptorCount(tempProc));
            Assert.Equal(54321, ProcessResourceProbe.ReadVmRssKiB(tempProc));
            Assert.Equal(65432, ProcessResourceProbe.ReadVmHwmKiB(tempProc));

            File.WriteAllText(Path.Combine(tempProc, "status"), "Name:\tplaynite\n");
            Assert.Throws<InvalidDataException>(() => ProcessResourceProbe.ReadVmRssKiB(tempProc));

            File.WriteAllText(Path.Combine(tempProc, "status"), "VmRSS:\tnot-a-number kB\n");
            Assert.Throws<InvalidDataException>(() => ProcessResourceProbe.ReadVmRssKiB(tempProc));
        }
        finally
        {
            if (Directory.Exists(tempProc))
            {
                Directory.Delete(tempProc, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Synthetic_png_is_valid_and_asset_copy_reads_exact_dimensions()
    {
        var png = SyntheticLibraryFixture.CreateDeterministicPng(256, 120, 42);
        var sha = Convert.ToHexStringLower(SHA256.HashData(png));

        var tempRoot = Path.Combine(Path.GetTempPath(), "playnite-png-test-" + Path.GetRandomFileName());
        try
        {
            var srcDir = Path.Combine(tempRoot, "src");
            var destDir = Path.Combine(tempRoot, "dest");
            Directory.CreateDirectory(Path.Combine(srcDir, "files"));
            Directory.CreateDirectory(destDir);
            await File.WriteAllBytesAsync(Path.Combine(srcDir, "files", "cover.png"), png, TestContext.Current.CancellationToken);

            var results = await AssetCopy.RunAsync(
                srcDir,
                destDir,
                [new AssetReference("games.db", "Games", "g1", "CoverImage", "cover.png", sha, png.LongLength)],
                dryRun: false,
                cancellationToken: TestContext.Current.CancellationToken);

            var item = Assert.Single(results);
            Assert.Equal(256, item.Width);
            Assert.Equal(120, item.Height);
            Assert.Equal("decoded", item.Dimensions);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Benchmark_harness_runs_1000_game_bench_and_soak_in_isolated_process_and_enforces_tolerance_gate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "playnite-bench-iso-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reportPath = Path.Combine(tempRoot, "report.json");
            var passRun = await RunHarnessProcessAsync(
                [
                    "--mode", "all",
                    "--library-size", "1000",
                    "--query-iterations", "5",
                    "--soak-cycles", "3",
                    "--session-cycles", "6",
                    "--real-process-cycles", "4",
                    "--artwork-iterations", "12",
                    "--output", reportPath,
                ],
                TestContext.Current.CancellationToken);

            Assert.True(passRun.ExitCode == 0, $"Expected exit 0, got {passRun.ExitCode}. stdout: {passRun.Stdout} stderr: {passRun.Stderr}");
            Assert.Contains("BENCHMARK COMPLETE", passRun.Stdout, StringComparison.Ordinal);
            Assert.Contains("SOAK COMPLETE", passRun.Stdout, StringComparison.Ordinal);

            var report = JsonSerializer.Deserialize<BenchmarkReport>(
                await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken),
                BenchmarkJson.Options);
            Assert.NotNull(report);
            Assert.True(report.OverallPassed);
            Assert.Equal(1000, report.LibrarySize);
            Assert.Contains(report.Metrics, m => m.Name == "library_open_1000_ms");
            Assert.Contains(report.Metrics, m => m.Name == "filter_search_p95_ms");
            Assert.Contains(report.Metrics, m => m.Name == "artwork_cache_probe_ms");
            Assert.Contains(report.Metrics, m => m.Name == "memory_post_bench_rss_kib");
            Assert.Contains(report.Metrics, m => m.Name == "memory_post_soak_rss_kib");
            Assert.Equal(3, report.SoakCurves.Count);
            Assert.All(report.SoakCurves, curve => Assert.True(curve.Flattened, $"{curve.Name}: {curve.Verdict}"));

            var strictBaselineMetrics = report.Metrics
                .Select(m => m.Unit == "ms"
                    ? m with { Median = m.Median * 0.1, P95 = m.P95 * 0.1 }
                    : m)
                .ToArray();
            var baselinePath = Path.Combine(tempRoot, "strict-baseline.json");
            await File.WriteAllTextAsync(
                baselinePath,
                JsonSerializer.Serialize(report with { Metrics = strictBaselineMetrics }, BenchmarkJson.Options),
                TestContext.Current.CancellationToken);

            var failRun = await RunHarnessProcessAsync(
                [
                    "--mode", "bench",
                    "--library-size", "1000",
                    "--query-iterations", "5",
                    "--artwork-iterations", "12",
                    "--baseline", baselinePath,
                    "--absolute-floor-ms", "0",
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(1, failRun.ExitCode);
            Assert.Contains("BENCHMARK REGRESSION", failRun.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Benchmark_suite_honors_mid_flight_cancellation_and_cleans_up_workspace_directory()
    {
        var tempParent = Path.Combine(Path.GetTempPath(), "playnite-cancel-parent-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempParent);
        try
        {
            string? createdWorkspace = null;
            using var cts = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BenchmarkSuiteRunner.RunAsync(
                    new BenchmarkOptions
                    {
                        Mode = BenchmarkMode.Bench,
                        LibrarySize = 100,
                        QueryIterations = 5,
                        ArtworkIterations = 10,
                    },
                    tempParent,
                    workspace =>
                    {
                        createdWorkspace = workspace;
                        Assert.True(Directory.Exists(workspace));
                        cts.Cancel();
                    },
                    cts.Token));

            Assert.NotNull(createdWorkspace);
            Assert.False(Directory.Exists(createdWorkspace), $"Workspace {createdWorkspace} leaked after mid-flight cancellation.");
            Assert.Empty(Directory.GetFileSystemEntries(tempParent));
        }
        finally
        {
            if (Directory.Exists(tempParent))
            {
                Directory.Delete(tempParent, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cli_returns_expected_exit_codes_for_help_invalid_args_and_error_paths()
    {
        using var outHelp = new StringWriter();
        using var errHelp = new StringWriter();
        var helpExit = await Program.RunAsync(["--help"], outHelp, errHelp, TestContext.Current.CancellationToken);
        Assert.Equal(0, helpExit);
        Assert.Contains("Usage: Playnite.Benchmarks", outHelp.ToString(), StringComparison.Ordinal);

        using var outBogusHelp = new StringWriter();
        using var errBogusHelp = new StringWriter();
        var bogusHelpExit = await Program.RunAsync(["--bogus", "--help"], outBogusHelp, errBogusHelp, TestContext.Current.CancellationToken);
        Assert.Equal(2, bogusHelpExit);
        Assert.Contains("Unknown option: --bogus", errBogusHelp.ToString(), StringComparison.Ordinal);

        using var outInvalid = new StringWriter();
        using var errInvalid = new StringWriter();
        var invalidExit = await Program.RunAsync(["--library-size", "10"], outInvalid, errInvalid, TestContext.Current.CancellationToken);
        Assert.Equal(2, invalidExit);
        Assert.Contains("BENCHMARK ERROR", errInvalid.ToString(), StringComparison.Ordinal);

        using var outEmptyOut = new StringWriter();
        using var errEmptyOut = new StringWriter();
        var emptyOutExit = await Program.RunAsync(["--output", ""], outEmptyOut, errEmptyOut, TestContext.Current.CancellationToken);
        Assert.Equal(2, emptyOutExit);

        var tempRoot = Path.Combine(Path.GetTempPath(), "playnite-bench-cli-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var outMissingBase = new StringWriter();
            using var errMissingBase = new StringWriter();
            var missingBaseExit = await Program.RunAsync(
                ["--mode", "bench", "--library-size", "100", "--query-iterations", "5", "--artwork-iterations", "10", "--baseline", Path.Combine(tempRoot, "missing.json")],
                outMissingBase,
                errMissingBase,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, missingBaseExit);
            Assert.Contains("BENCHMARK ERROR", errMissingBase.ToString(), StringComparison.Ordinal);

            var corruptBaselinePath = Path.Combine(tempRoot, "empty-metrics.json");
            await File.WriteAllTextAsync(corruptBaselinePath, "{}", TestContext.Current.CancellationToken);
            using var outCorruptBase = new StringWriter();
            using var errCorruptBase = new StringWriter();
            var corruptBaseExit = await Program.RunAsync(
                ["--mode", "bench", "--library-size", "100", "--query-iterations", "5", "--artwork-iterations", "10", "--baseline", corruptBaselinePath],
                outCorruptBase,
                errCorruptBase,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, corruptBaseExit);
            Assert.Contains("contains no metrics", errCorruptBase.ToString(), StringComparison.Ordinal);

            // Passing an existing directory as --output must fail cleanly with exit 1 instead of throwing.
            var existingDir = Path.Combine(tempRoot, "dir-as-output");
            Directory.CreateDirectory(existingDir);
            using var outBadWrite = new StringWriter();
            using var errBadWrite = new StringWriter();
            var badWriteExit = await Program.RunAsync(
                ["--mode", "bench", "--library-size", "100", "--query-iterations", "5", "--artwork-iterations", "10", "--output", existingDir],
                outBadWrite,
                errBadWrite,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, badWriteExit);
            Assert.Contains("BENCHMARK ERROR", errBadWrite.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHarnessProcessAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var harnessAssembly = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(harnessAssembly);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new IOException("Failed to launch Playnite.Benchmarks child process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("Playnite.Benchmarks child process exceeded 45s.");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

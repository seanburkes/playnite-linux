using System.Globalization;
using System.Text.Json;
using Playnite.Platform.Sessions;

namespace Playnite.Benchmarks;

/// <summary>Command-line entry point for the p8-003 benchmark and soak harness.</summary>
public static class Program
{
    private const string Usage =
        """
        Usage: Playnite.Benchmarks [options]

        Runs deterministic 1,000-game benchmarks, resource-steady-state soak loops,
        and optional baseline tolerance-gate comparisons (+10% warn, +20% fail defaults).

        Options:
          --mode <all|bench|soak>       Execution mode (default: all).
          --library-size <n>            Synthetic library game count (minimum 100, default 1000).
          --query-iterations <n>        Benchmark query iterations (minimum 5, default 25).
          --soak-cycles <n>             Import/browse soak cycles (minimum 3, default 10).
          --session-cycles <n>          Synthetic game session soak cycles (minimum 5, default 25).
          --real-process-cycles <n>     Real Linux process soak cycles (0 or >=3, default 6).
          --artwork-iterations <n>      Artwork cache/copy iterations (minimum 10, default 100).
          --baseline <path>             Optional baseline JSON report for tolerance checks.
          --warn-threshold <ratio>      Relative regression warning ratio (default 0.10).
          --fail-threshold <ratio>      Relative regression failure ratio (default 0.20).
          --absolute-floor-ms <ms>      Sub-floor ms jitter exemption (default 0.25).
          --output <path>               Optional JSON report output path.
          --seed <value>                Deterministic seed (decimal or 0x-hex, default 0x50380003).
          --help                        Show this help text.
        """;

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        var disposed = false;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!Volatile.Read(ref disposed))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Main is exiting concurrently with Ctrl-C.
                }
            }
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            Volatile.Write(ref disposed, true);
        }
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        BenchmarkOptions options;
        bool helpRequested;
        try
        {
            options = Parse(args, out helpRequested);
            if (helpRequested)
            {
                await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
                return 0;
            }

            options.Validate();
        }
        catch (ArgumentException exception)
        {
            await stderr.WriteLineAsync($"BENCHMARK ERROR: {exception.Message}").ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        BenchmarkReport report;
        try
        {
            report = await BenchmarkSuiteRunner.RunAsync(options, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var json = JsonSerializer.Serialize(report, BenchmarkJson.Options);
                await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("BENCHMARK CANCELLED").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
            or ArgumentException
            or ProcessOrphanedException
            or ProcessSurvivorException
            or AggregateException)
        {
            await stderr.WriteLineAsync($"BENCHMARK ERROR: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        foreach (var metric in report.Metrics)
        {
            await stdout.WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "METRIC {0}: median={1:F3} {2} p95={3:F3} {2} min={4:F3} max={5:F3} samples={6}",
                metric.Name,
                metric.Median,
                metric.Unit,
                metric.P95,
                metric.Min,
                metric.Max,
                metric.Samples)).ConfigureAwait(false);
        }

        foreach (var curve in report.SoakCurves)
        {
            await stdout.WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "SOAK {0}: verdict={1} flattened={2} fd_delta={3} rss_growth={4:P2} samples={5}",
                curve.Name,
                curve.Verdict,
                curve.Flattened,
                curve.SteadyFdDelta,
                curve.LateRssGrowthRatio,
                curve.Samples.Count)).ConfigureAwait(false);
        }

        if (report.ToleranceGate is not null)
        {
            foreach (var comparison in report.ToleranceGate.Comparisons)
            {
                await stdout.WriteLineAsync(string.Format(
                    CultureInfo.InvariantCulture,
                    "GATE {0} ({1}): baseline={2:F3} current={3:F3} delta={4:P2} status={5}",
                    comparison.Metric,
                    comparison.GateStatistic,
                    comparison.BaselineValue,
                    comparison.CurrentValue,
                    comparison.DeltaRatio,
                    comparison.Status)).ConfigureAwait(false);
            }
        }

        if (report.OverallPassed)
        {
            if (options.Mode is BenchmarkMode.All or BenchmarkMode.Bench)
            {
                await stdout.WriteLineAsync("BENCHMARK COMPLETE").ConfigureAwait(false);
            }

            if (options.Mode is BenchmarkMode.All or BenchmarkMode.Soak)
            {
                await stdout.WriteLineAsync("SOAK COMPLETE").ConfigureAwait(false);
            }

            return 0;
        }

        if (report.SoakCurves.Any(c => !c.Flattened))
        {
            await stderr.WriteLineAsync("SOAK LEAK DETECTED").ConfigureAwait(false);
        }

        if (report.ToleranceGate is { Passed: false })
        {
            await stderr.WriteLineAsync("BENCHMARK REGRESSION").ConfigureAwait(false);
        }

        return 1;
    }

    public static BenchmarkOptions Parse(string[] args)
        => Parse(args, out _);

    public static BenchmarkOptions Parse(string[] args, out bool helpRequested)
    {
        ArgumentNullException.ThrowIfNull(args);
        helpRequested = false;
        var options = new BenchmarkOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag is "--help" or "-h")
            {
                helpRequested = true;
                return options;
            }

            var value = RequireValue(args, ref i, flag);
            options = flag switch
            {
                "--mode" => options with { Mode = ParseMode(value) },
                "--library-size" => options with { LibrarySize = ParseInt(value, flag) },
                "--query-iterations" => options with { QueryIterations = ParseInt(value, flag) },
                "--soak-cycles" => options with { SoakCycles = ParseInt(value, flag) },
                "--session-cycles" => options with { SessionCycles = ParseInt(value, flag) },
                "--real-process-cycles" => options with { RealProcessCycles = ParseInt(value, flag) },
                "--artwork-iterations" => options with { ArtworkIterations = ParseInt(value, flag) },
                "--baseline" => options with { BaselinePath = value },
                "--warn-threshold" => options with { WarnThresholdRatio = ParseDouble(value, flag) },
                "--fail-threshold" => options with { FailThresholdRatio = ParseDouble(value, flag) },
                "--absolute-floor-ms" => options with { AbsoluteFloorMs = ParseDouble(value, flag) },
                "--output" => options with { OutputPath = value },
                "--seed" => options with { Seed = ParseSeed(value) },
                _ => throw new ArgumentException($"Unknown option: {flag}."),
            };
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {flag}.");
        }

        index++;
        return args[index];
    }

    private static BenchmarkMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "all" => BenchmarkMode.All,
        "bench" => BenchmarkMode.Bench,
        "soak" => BenchmarkMode.Soak,
        _ => throw new ArgumentException($"Invalid mode '{value}'; expected all, bench, or soak."),
    };

    private static int ParseInt(string value, string flag) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {flag}: '{value}'.");

    private static double ParseDouble(string value, string flag) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {flag}: '{value}'.");

    private static uint ParseSeed(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : throw new ArgumentException($"Invalid hex seed: '{value}'.");
        }

        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
            ? dec
            : throw new ArgumentException($"Invalid seed: '{value}'.");
    }
}

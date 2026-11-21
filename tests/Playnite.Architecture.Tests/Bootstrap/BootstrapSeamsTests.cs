using System.Diagnostics;
using Playnite.Core;
using Playnite.Desktop.Bootstrap;
using Playnite.Platform.Integration;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Desktop.Tests;

/// <summary>p7-001 bootstrap seams: each test states one startup behaviour.</summary>
public sealed class BootstrapSeamsTests
{
    private static readonly IRegularFileProbe Probe = new LinuxRegularFileProbe();

    [Fact]
    public void Service_registry_returns_the_same_singleton_for_repeated_resolution()
    {
        var registry = new ServiceRegistry();
        var constructions = 0;
        registry.AddSingleton<object>(_ =>
        {
            constructions++;
            return new object();
        });

        var first = registry.GetRequiredService<object>();
        var second = registry.GetRequiredService<object>();

        Assert.Same(first, second);
        Assert.Equal(1, constructions);
    }

    [Fact]
    public void Service_registry_rejects_unregistered_and_duplicate_services()
    {
        var registry = new ServiceRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.GetRequiredService<object>());

        registry.AddSingleton<object>(_ => new object());
        Assert.Throws<InvalidOperationException>(() => registry.AddSingleton<object>(_ => new object()));
    }

    [Fact]
    public void Log_entry_formats_a_stable_utc_line()
    {
        var entry = new LogEntry(
            new DateTimeOffset(2026, 11, 20, 12, 34, 56, TimeSpan.Zero), "INFO", "started");

        Assert.Equal("2026-11-20T12:34:56Z INFO started", entry.Format());
    }

    [Fact]
    public void File_logger_appends_both_levels_and_releases_the_handle()
    {
        var path = Path.Combine(Path.GetTempPath(), "playnite-p7-001-" + Path.GetRandomFileName() + ".log");
        try
        {
            using (var logger = new FileAppLogger(path, Probe))
            {
                logger.Info("first");
                logger.Error("second");
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(" INFO first", lines[0]);
            Assert.EndsWith(" ERROR second", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Theme_paths_resolve_a_named_theme_inside_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "playnite-themes");

        Assert.True(ThemePaths.TryResolve(root, "Neon", out var path, out var error));
        Assert.Equal(Path.Combine(root, "Neon", ThemePaths.ThemeFileName), path);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("bad/name")]
    [InlineData("trailing.")]
    public void Theme_paths_reject_names_that_could_escape_the_root(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "playnite-themes");

        Assert.False(ThemePaths.TryResolve(root, name, out var path, out var error));
        Assert.Equal(string.Empty, path);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Theme_file_reads_a_window_font_size_override()
    {
        var path = Path.Combine(Path.GetTempPath(), "playnite-theme-" + Path.GetRandomFileName() + ".axaml");
        try
        {
            File.WriteAllText(path, """
                <Styles xmlns="https://github.com/avaloniaui">
                  <Style Selector="Window"><Setter Property="FontSize" Value="21" /></Style>
                </Styles>
                """);

            Assert.Equal(21, ThemeFile.Read(path, Probe).FontSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("<Styles xmlns=\"https://github.com/avaloniaui\"><Style Selector=\"Button\"><Setter Property=\"FontSize\" Value=\"21\" /></Style></Styles>")]
    [InlineData("<Styles xmlns=\"https://github.com/avaloniaui\"><Style Selector=\"Window\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Style></Styles>")]
    [InlineData("<Styles xmlns=\"https://github.com/avaloniaui\"><Style Selector=\"Window\"><Setter Property=\"FontSize\" Value=\"0\" /></Style></Styles>")]
    public void Theme_file_rejects_unsupported_or_invalid_overrides(string document)
    {
        var path = Path.Combine(Path.GetTempPath(), "playnite-theme-" + Path.GetRandomFileName() + ".axaml");
        try
        {
            File.WriteAllText(path, document);

            Assert.Throws<FormatException>(() => ThemeFile.Read(path, Probe));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Localization_source_names_the_loaded_catalog()
    {
        Assert.Equal("embedded", BootstrapLocalization.Load(null, Probe).Source);

        var path = Path.Combine(Path.GetTempPath(), "playnite-loc-" + Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path, "{\"LOCAboutWindowTitle\":\"From catalog\"}");
            Assert.Equal(path, BootstrapLocalization.Load(path, Probe).Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Localization_resolves_frozen_english_and_marks_unknown_keys()
    {
        var localization = BootstrapLocalization.Load(null, Probe);

        Assert.Equal("About Playnite", localization.Resolve(LOC.AboutWindowTitle));
        Assert.Equal("<!LOCMissingKey!>", localization.Resolve("LOCMissingKey"));
    }

    [Fact]
    public void Localization_catalog_overrides_a_key_and_falls_back_for_the_rest()
    {
        var translation = new LocalizationKeySet(
            new[] { KeyValuePair.Create(LOC.AboutWindowTitle, "Über Playnite") });
        var localization = new BootstrapLocalization(translation);

        Assert.Equal("Über Playnite", localization.Resolve(LOC.AboutWindowTitle));
        Assert.Equal(LocalizationKeySet.English.Resolve(LOC.APIKey), localization.Resolve(LOC.APIKey));
    }

    [Theory]
    [InlineData(SingleInstanceStatus.Owner, StartupDecision.Continue)]
    [InlineData(SingleInstanceStatus.AlreadyRunning, StartupDecision.ExitAlreadyRunning)]
    [InlineData(SingleInstanceStatus.Forwarded, StartupDecision.ExitAlreadyRunning)]
    [InlineData(SingleInstanceStatus.Unavailable, StartupDecision.Continue)]
    public void Single_instance_status_maps_to_one_startup_decision(
        SingleInstanceStatus status, StartupDecision expected)
    {
        var logger = new RecordingLogger();
        var bootstrap = new SingleInstanceBootstrap(
            new FakeSingleInstance(new SingleInstanceResult(status, "diagnostic")), logger);

        Assert.Equal(expected, bootstrap.DecideStartup());
        Assert.Single(logger.Messages);
    }

    [Fact]
    public async Task Single_instance_bootstrap_disposes_the_platform_service()
    {
        var instance = new FakeSingleInstance(new SingleInstanceResult(SingleInstanceStatus.Owner, null));
        var bootstrap = new SingleInstanceBootstrap(instance, new RecordingLogger());

        await bootstrap.DisposeAsync();

        Assert.True(instance.Disposed);
    }

    [Fact]
    public async Task Fifo_theme_is_rejected_without_blocking_startup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = CreateFifo("theme");
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => Task.Run(() => ThemeFile.Read(path, Probe)))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Fifo_localisation_catalog_falls_back_without_blocking_startup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = CreateFifo("loc");
        try
        {
            var result = await Task.Run(() => BootstrapLocalization.Load(path, Probe))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("embedded", result.Source);
            Assert.Equal(LocalizationKeySet.English.Resolve(LOC.APIKey), result.Resolve(LOC.APIKey));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Fifo_log_path_fails_fast_without_blocking_startup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = CreateFifo("log");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => Task.Run(() => new FileAppLogger(path, Probe)))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateFifo(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "playnite-p7-001-" + name + "-" + Path.GetRandomFileName());
        var start = new ProcessStartInfo("mkfifo")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("mkfifo could not be started.");
        Assert.True(process.WaitForExit(5000), "mkfifo did not finish within five seconds.");
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Error(string message) => Messages.Add(message);

        public void Dispose()
        {
        }
    }

    private sealed class FakeSingleInstance(SingleInstanceResult result) : ISingleInstance
    {
        public SingleInstanceResult Result { get; } = result;

        public bool Disposed { get; private set; }

        public event EventHandler<ActivationRequest>? Activated
        {
            add { }
            remove { }
        }

        public Task<SingleInstanceResult> ActivateAsync(string uri, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

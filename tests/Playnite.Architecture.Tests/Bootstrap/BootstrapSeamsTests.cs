using Playnite.Desktop.Bootstrap;
using Playnite.Platform.Integration;
using Xunit;

namespace Playnite.Desktop.Tests;

/// <summary>p7-001 bootstrap seams: each test states one startup behaviour.</summary>
public sealed class BootstrapSeamsTests
{
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
            using (var logger = new FileAppLogger(path))
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
    public void Localization_resolves_frozen_english_and_marks_unknown_keys()
    {
        var localization = BootstrapLocalization.Load(null);

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

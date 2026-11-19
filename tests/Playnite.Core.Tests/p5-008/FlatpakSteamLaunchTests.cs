using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class FlatpakSteamLaunchTests
{
    private static readonly SteamLaunchTarget Native = new(new SteamApp(42, "Synthetic", "Game",
        "/library", SteamAppState.Installed, false, null, null, 4));

    [Fact]
    public void Flatpak_marker_selects_launch_only_mode()
    {
        using var marker = new TempFile();
        Assert.True(new LinuxSteamHostFormat(marker.Path).IsFlatpak);
        Assert.Equal(SteamLaunchMode.FlatpakLaunchOnly,
            SteamLaunchModes.Select(new LinuxSteamHostFormat(marker.Path)));
    }

    [Fact]
    public void Absent_flatpak_marker_keeps_the_tracked_mode()
    {
        var missing = Path.Combine(Path.GetTempPath(), "playnite-no-flatpak-" + Guid.NewGuid().ToString("N"));
        Assert.False(new LinuxSteamHostFormat(missing).IsFlatpak);
        Assert.Equal(SteamLaunchMode.Tracked, SteamLaunchModes.Select(new LinuxSteamHostFormat(missing)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("007")]
    [InlineData("4294967295")]
    public void Strict_decimal_uint_app_ids_are_accepted(string text)
    {
        Assert.True(FlatpakSteamLauncher.TryParseAppId(text, out var appId));
        Assert.Equal(uint.Parse(text, System.Globalization.CultureInfo.InvariantCulture), appId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 42")]
    [InlineData("42 ")]
    [InlineData("0x2A")]
    [InlineData("1e3")]
    [InlineData("1.0")]
    [InlineData("4294967296")]
    [InlineData("999999999999999999999999")]
    [InlineData("42; id")]
    [InlineData("42\n")]
    [InlineData("../../etc/passwd")]
    [InlineData("steam://rungameid/42")]
    public void Invalid_or_injected_app_ids_are_rejected(string text)
    {
        Assert.False(FlatpakSteamLauncher.TryParseAppId(text, out _));
    }

    [Fact]
    public void Null_app_id_is_rejected()
    {
        Assert.False(FlatpakSteamLauncher.TryParseAppId(null, out _));
    }

    [Theory]
    [InlineData(0u, "steam://rungameid/0")]
    [InlineData(42u, "steam://rungameid/42")]
    [InlineData(4294967295u, "steam://rungameid/4294967295")]
    public void Uri_is_exact_decimal_text(uint appId, string expected)
    {
        Assert.Equal(expected, FlatpakSteamLauncher.BuildUri(appId));
    }

    [Fact]
    public void Request_is_one_absolute_opener_and_one_uri_argument()
    {
        var launcher = new FlatpakSteamLauncher(new RecordingOpener());
        var request = launcher.CreateRequest(42);
        Assert.Equal("/usr/bin/xdg-open", request.Executable);
        Assert.Equal(["steam://rungameid/42"], request.Arguments);
        Assert.Null(request.WorkingDirectory);
        Assert.DoesNotContain(request.Arguments, argument => argument.Any(character =>
            character is ';' or '&' or '|' or '`' or '$' or '\n' or '\r'));
    }

    [Fact]
    public void A_shell_opener_is_rejected_when_the_request_is_built()
    {
        var shell = new FlatpakSteamLauncher(new RecordingOpener(), "/bin/sh");
        var rejection = Assert.Throws<ArgumentException>(() => shell.CreateRequest(42));
        Assert.Contains("shell", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => new FlatpakSteamLauncher(new RecordingOpener(), "xdg-open"));
    }

    [Fact]
    public async Task A_shell_opener_is_rejected_on_the_production_launch_path()
    {
        // The constructor accepts any absolute path, so the /bin/sh guard must come from the
        // production request path (CreateRequest -> LaunchRequest) and must run before the
        // opener is consulted. LaunchAsync is the path the packaged entry uses.
        var opener = new RecordingOpener();
        var shell = new FlatpakSteamLauncher(opener, "/bin/sh");

        var rejection = await Assert.ThrowsAsync<ArgumentException>(
            () => shell.LaunchAsync(42, CancellationToken.None));

        Assert.Contains("shell", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(opener.Request);
    }

    [Fact]
    public async Task Accepted_request_is_reported_as_unknown_tracking_with_manual_stop()
    {
        var opener = new RecordingOpener { ExitCode = 0 };
        var launcher = new FlatpakSteamLauncher(opener);
        var result = await launcher.LaunchAsync(42, CancellationToken.None);

        Assert.Equal(FlatpakLaunchStatus.RequestAccepted, result.Status);
        Assert.Equal(SteamLaunchTracking.Unknown, result.Tracking);
        Assert.Equal("steam://rungameid/42", result.Uri);
        Assert.Contains("UNKNOWN", result.Detail);
        Assert.Contains("Stop the game manually in Steam", result.Detail);
        Assert.Equal("/usr/bin/xdg-open", opener.Request!.Executable);
        Assert.Equal(["steam://rungameid/42"], opener.Request.Arguments);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(126)]
    [InlineData(255)]
    public async Task Nonzero_opener_exit_claims_no_launch(int exitCode)
    {
        var launcher = new FlatpakSteamLauncher(new RecordingOpener { ExitCode = exitCode });
        var result = await launcher.LaunchAsync(42, CancellationToken.None);

        Assert.Equal(FlatpakLaunchStatus.Rejected, result.Status);
        Assert.Equal(SteamLaunchTracking.Unknown, result.Tracking);
        Assert.Contains("no launch is claimed", result.Detail);
        Assert.Contains($"exited {exitCode}", result.Detail);
    }

    [Fact]
    public async Task Missing_opener_is_unavailable_not_a_launch()
    {
        var launcher = new FlatpakSteamLauncher(new RecordingOpener { Failure = new IOException("missing") });
        var result = await launcher.LaunchAsync(42, CancellationToken.None);

        Assert.Equal(FlatpakLaunchStatus.Unavailable, result.Status);
        Assert.Equal(SteamLaunchTracking.Unknown, result.Tracking);
        Assert.Contains("could not be started", result.Detail);
    }

    [Fact]
    public async Task Portal_start_failure_is_unavailable_not_a_launch()
    {
        var launcher = new FlatpakSteamLauncher(new RecordingOpener
        {
            Failure = new PathTooLongException("portal path"),
        });
        var result = await launcher.LaunchAsync(42, CancellationToken.None);

        Assert.Equal(FlatpakLaunchStatus.Unavailable, result.Status);
        Assert.Equal(SteamLaunchTracking.Unknown, result.Tracking);
    }

    [Fact]
    public async Task Cancellation_propagates_and_releases_no_ownership_claim()
    {
        using var cancellation = new CancellationTokenSource();
        var opener = new RecordingOpener { BlockUntilCancelled = true };
        var launcher = new FlatpakSteamLauncher(opener);
        var launching = launcher.LaunchAsync(42, cancellation.Token);
        await opener.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launching);
    }

    [Fact]
    public void Launch_only_type_exposes_no_stop_signal_or_playtime_surface()
    {
        Assert.DoesNotContain(typeof(FlatpakSteamLauncher).GetMethods(),
            method => method.Name.Contains("Stop", StringComparison.Ordinal)
                || method.Name.Contains("Signal", StringComparison.Ordinal)
                || method.Name.Contains("Track", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(FlatpakLaunchResult).GetProperties(),
            property => property.Name.Contains("Playtime", StringComparison.Ordinal)
                || property.Name.Contains("Stop", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_flatpak_tracked_request_is_unchanged()
    {
        Assert.Equal(SteamLaunchMode.Tracked, SteamLaunchModes.Select(new FakeHostFormat(false)));
        var launcher = new SteamProcessLauncher(new UnusedHost(), Native, new()
        {
            SteamExecutable = "/usr/bin/steam",
            ProtocolOpener = "/usr/bin/xdg-open",
        });
        var request = launcher.CreateRequest();
        Assert.Equal("/usr/bin/steam", request.Executable);
        Assert.Equal(["-applaunch", "42"], request.Arguments);
    }

    private sealed class FakeHostFormat(bool flatpak) : ISteamHostFormat
    {
        public bool IsFlatpak => flatpak;
    }

    private sealed class RecordingOpener : ISteamUriOpener
    {
        public LaunchRequest? Request { get; private set; }
        public int ExitCode { get; init; }
        public Exception? Failure { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Started.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return ExitCode;
        }
    }

    private sealed class UnusedHost : IExternalProcessHost
    {
        public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            string? expectedCommandLineArgument, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "playnite-flatpak-marker-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path, string.Empty);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // The test marker is disposable; a leftover temp file changes no assertion.
            }
        }
    }
}

using Playnite.Platform.Sessions;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamAcceptanceConsentTests
{
    [Fact]
    public async Task Default_acceptance_is_read_only_even_if_control_flow_attempts_a_launch_or_stop()
    {
        var inner = new RecordingHost();
        var host = new ConsentProcessHost(inner);
        Assert.True((await host.SnapshotAsync(CancellationToken.None)).IsComplete);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.RunAsync(new("/synthetic/steam", ["-applaunch", "42"]), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.SignalAsync(new(42, 1, 42), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.SignalAsync(new(42, 1, 42), "/games/game", CancellationToken.None));
        Assert.Equal(0, inner.Launches);
        Assert.Equal(0, inner.Signals);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Launch_and_stop_each_require_their_own_explicit_consent(bool launch, bool stop)
    {
        var inner = new RecordingHost();
        var host = new ConsentProcessHost(inner, launch, stop);
        var request = new LaunchRequest("/synthetic/steam", ["-applaunch", "42"]);
        if (launch)
        {
            Assert.Equal(0, await host.RunAsync(request, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.RunAsync(request, CancellationToken.None));
        }

        if (stop)
        {
            await host.SignalAsync(new(42, 1, 42), CancellationToken.None);
            await host.SignalAsync(new(42, 1, 42), "/games/game", CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.SignalAsync(new(42, 1, 42), CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.SignalAsync(new(42, 1, 42), "/games/game", CancellationToken.None));
        }

        Assert.Equal(launch ? 1 : 0, inner.Launches);
        Assert.Equal(stop ? 2 : 0, inner.Signals);
    }

    private sealed class RecordingHost : IExternalProcessHost
    {
        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken cancellationToken) =>
            SignalAsync(identity, cancellationToken);
        public int Launches { get; private set; }
        public int Signals { get; private set; }
        public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new ExternalProcessSnapshot([], true));
        public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Launches++;
            return Task.FromResult(0);
        }

        public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken)
        {
            Signals++;
            return Task.CompletedTask;
        }
    }
}

using System.Diagnostics;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class LinuxPidFdSignalTests
{
    [Fact]
    public async Task Pid_recycled_while_opening_is_rejected_by_final_identity_check()
    {
        var state = new FakeState();
        state.AfterOpen = () => state.Ticks = 2;
        var host = new LinuxExternalProcessHost(state, state);
        await host.SignalAsync(new(22, 1, 20), CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Pid_recycled_after_final_check_cannot_redirect_the_pinned_signal()
    {
        var state = new FakeState();
        state.AfterRead = () => state.Ticks = 2;
        var host = new LinuxExternalProcessHost(state, state);
        await host.SignalAsync(new(22, 1, 20), CancellationToken.None);
        Assert.Equal([1L], state.SignalledGenerations);
        Assert.Equal(2, state.Ticks);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Process_that_left_the_authorized_group_is_not_signalled()
    {
        var state = new FakeState { Group = 99 };
        var host = new LinuxExternalProcessHost(state, state);
        await host.SignalAsync(new(22, 1, 20), CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Cancellation_before_pinning_never_opens_a_handle_or_signals()
    {
        var state = new FakeState();
        var host = new LinuxExternalProcessHost(state, state);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SignalAsync(new(22, 1, 20), source.Token));
        Assert.Equal(0, state.Opens);
        Assert.Empty(state.SignalledGenerations);
    }

    [Fact]
    public async Task Cancellation_during_final_identity_read_closes_the_pinned_handle_without_signalling()
    {
        using var source = new CancellationTokenSource();
        var state = new FakeState { AfterOpen = () => source.Cancel() };
        var host = new LinuxExternalProcessHost(state, state);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SignalAsync(new(22, 1, 20), source.Token));
        Assert.Equal(1, state.Disposals);
        Assert.Empty(state.SignalledGenerations);
    }

    [Fact]
    public async Task Production_pidfd_gracefully_stops_only_the_owned_test_child()
    {
        using var child = Process.Start(new ProcessStartInfo("/usr/bin/sleep")
        {
            UseShellExecute = false, ArgumentList = { "30" },
        }) ?? throw new IOException("Test child did not start.");
        try
        {
            var stat = await new LinuxProcStatReader().ReadAsync(child.Id, CancellationToken.None);
            Assert.NotNull(stat);
            var row = stat.Value;
            await new LinuxExternalProcessHost().SignalAsync(new(child.Id, row.StartTimeTicks, row.ProcessGroupId), CancellationToken.None);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.Equal(143, child.ExitCode); // SIGTERM, not SIGKILL (137).
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(); // Cleanup only our artificial sleep child after a failed test.
                await child.WaitForExitAsync();
            }
        }
    }

    private sealed class FakeState : IProcStatReader, IProcessSignalHandles
    {
        public long Ticks { get; set; } = 1;
        public int Group { get; init; } = 20;
        public Action? AfterOpen { get; set; }
        public Action? AfterRead { get; set; }
        public int Opens { get; private set; }
        public int Disposals { get; private set; }
        public List<long> SignalledGenerations { get; } = [];

        public Task<ProcStat?> ReadAsync(int processId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcStat? snapshot = new(processId, 1, Group, 'S', Ticks);
            AfterRead?.Invoke();
            return Task.FromResult(snapshot);
        }

        public IProcessSignalHandle Open(int processId)
        {
            Assert.Equal(22, processId);
            Opens++;
            var handle = new Handle(this, Ticks);
            AfterOpen?.Invoke();
            return handle;
        }

        private sealed class Handle(FakeState state, long pinnedGeneration) : IProcessSignalHandle
        {
            public void Terminate() => state.SignalledGenerations.Add(pinnedGeneration);
            public void Dispose() => state.Disposals++;
        }
    }
}

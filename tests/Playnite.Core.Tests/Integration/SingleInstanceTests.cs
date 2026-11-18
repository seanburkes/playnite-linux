using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Integration;
using Playnite.Platform.Linux.Integration;
using Xunit;

namespace Playnite.Core.Tests.Integration;

public sealed class SingleInstanceTests
{
    private const string Id = "playnite-test";

    [Fact]
    public async Task OwnerTakesTheLockAndSecondInstanceIsRefusedClearly()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");

        await using var owner = SingleInstanceService.Start(Id, directories);

        Assert.Equal(SingleInstanceStatus.Owner, owner.Result.Status);
        Assert.True(File.Exists(Path.Combine(directories.Runtime, Id + ".lock")));
        Assert.True(File.Exists(Path.Combine(directories.Runtime, Id + ".sock")));

        await using var second = SingleInstanceService.Start(Id, directories);

        Assert.Equal(SingleInstanceStatus.AlreadyRunning, second.Result.Status);
        Assert.False(second.Result.IsOwner);
        Assert.Contains("Another Playnite instance", second.Result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(Id + ".lock", second.Result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivationIsForwardedToTheOwnerExactlyOnce()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        const string activation = "playnite://start/0f8fad5b-d9cb-469f-a165-70867728950e/1";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var owner = SingleInstanceService.Start(Id, directories);
        owner.Activated += (_, request) => received.TrySetResult(request.Uri);
        await using var second = SingleInstanceService.Start(Id, directories);

        var forwarded = await second.ActivateAsync(activation);

        Assert.True(forwarded.Status == SingleInstanceStatus.Forwarded, forwarded.Diagnostic);
        Assert.Equal(activation, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task DisposingASecondInstanceLeavesTheOwnerSocketAndForwardingIntact()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        var socketPath = Path.Combine(directories.Runtime, Id + ".sock");
        const string activation = "playnite://search/second";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var owner = SingleInstanceService.Start(Id, directories);
        owner.Activated += (_, request) => received.TrySetResult(request.Uri);

        var second = SingleInstanceService.Start(Id, directories);
        Assert.Equal(SingleInstanceStatus.AlreadyRunning, second.Result.Status);
        await second.DisposeAsync();

        Assert.True(File.Exists(socketPath));
        await using var third = SingleInstanceService.Start(Id, directories);
        Assert.Equal(SingleInstanceStatus.AlreadyRunning, third.Result.Status);
        var forwarded = await third.ActivateAsync(activation);

        Assert.True(forwarded.Status == SingleInstanceStatus.Forwarded, forwarded.Diagnostic);
        Assert.Equal(activation, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ActivationForwardedBeforeTheOwnerSubscribesIsDeliveredOnSubscription()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        const string activation = "playnite://search/queued";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var owner = SingleInstanceService.Start(Id, directories);
        await using var second = SingleInstanceService.Start(Id, directories);
        var forwarded = await second.ActivateAsync(activation);
        Assert.Equal(SingleInstanceStatus.Forwarded, forwarded.Status);

        owner.Activated += (_, request) => received.TrySetResult(request.Uri);

        Assert.Equal(activation, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AStalledClientDoesNotBlockLaterActivations()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        var socketPath = Path.Combine(directories.Runtime, Id + ".sock");

        await using var owner = SingleInstanceService.Start(Id, directories);
        using var stalled = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await stalled.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var second = SingleInstanceService.Start(Id, directories);

        var forwarded = await second.ActivateAsync("playnite://search/after-stall").WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(forwarded.Status == SingleInstanceStatus.Forwarded, forwarded.Diagnostic);
    }

    [Fact]
    public async Task MalformedActivationIsRejectedBeforeItReachesTheOwner()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        var activations = 0;

        await using var owner = SingleInstanceService.Start(Id, directories);
        owner.Activated += (_, _) => Interlocked.Increment(ref activations);
        await using var second = SingleInstanceService.Start(Id, directories);

        foreach (var value in new[] { "playnite://start/%ZZ", "playnite://start/a\nb", "http://example.test/start", "" })
        {
            var result = await second.ActivateAsync(value);
            Assert.Equal(SingleInstanceStatus.Unavailable, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
        }

        Assert.Equal(0, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task DisposalReleasesTheLockButLeavesTheHarmlessLockFile()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        var lockPath = Path.Combine(directories.Runtime, Id + ".lock");
        var socketPath = Path.Combine(directories.Runtime, Id + ".sock");

        await using (var first = SingleInstanceService.Start(Id, directories))
        {
            Assert.Equal(SingleInstanceStatus.Owner, first.Result.Status);
        }

        Assert.True(File.Exists(lockPath));
        Assert.False(File.Exists(socketPath));

        await using var replacement = SingleInstanceService.Start(Id, directories);

        Assert.Equal(SingleInstanceStatus.Owner, replacement.Result.Status);
    }

    [Fact]
    public async Task WritesStayInsideTheRuntimeDirectory()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");

        await using (SingleInstanceService.Start(Id, directories))
        {
            var entry = Assert.Single(Directory.GetFileSystemEntries(temporary.Path));
            Assert.Equal(temporary.Path + "/run", entry);
        }

        Assert.Empty(Directory.GetFileSystemEntries(directories.Runtime).Where(path => path.EndsWith(".sock", StringComparison.Ordinal)));
        Assert.All(Directory.GetFileSystemEntries(temporary.Path), path => Assert.StartsWith(temporary.Path + "/run", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedActivationIsRejectedBeforeTheOwnerReadsIt()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        var uri = "playnite://search/" + new string('文', 1000) + "/" + new string('文', 400);

        await using var owner = SingleInstanceService.Start(Id, directories);
        await using var second = SingleInstanceService.Start(Id, directories);

        var forwarded = await second.ActivateAsync(uri);

        Assert.Equal(SingleInstanceStatus.Unavailable, forwarded.Status);
        Assert.Contains("larger than", forwarded.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostCancellationIsObservedInsteadOfReportedAsUnavailability()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        await using var owner = SingleInstanceService.Start(Id, directories);
        await using var second = SingleInstanceService.Start(Id, directories);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => second.ActivateAsync("playnite://search/cancel", cancellation.Token));
    }

    [Fact]
    public async Task RuntimeLockAndSocketArePrivateToTheUser()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");

        await using var owner = SingleInstanceService.Start(Id, directories);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode privateDirectory = privateFile | UnixFileMode.UserExecute;
        Assert.Equal(privateDirectory, File.GetUnixFileMode(directories.Runtime));
        Assert.Equal(privateFile, File.GetUnixFileMode(Path.Combine(directories.Runtime, Id + ".lock")));
        Assert.Equal(privateFile, File.GetUnixFileMode(Path.Combine(directories.Runtime, Id + ".sock")));
    }

    [Fact]
    public async Task APreExistingLockFileIsNotTruncatedAndOwnershipStillSucceeds()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        Directory.CreateDirectory(directories.Runtime);
        var lockPath = Path.Combine(directories.Runtime, Id + ".lock");
        File.WriteAllText(lockPath, new string('x', 200));

        await using (var owner = SingleInstanceService.Start(Id, directories))
        {
            Assert.Equal(SingleInstanceStatus.Owner, owner.Result.Status);
        }

        var bytes = File.ReadAllBytes(lockPath);
        Assert.True(bytes.Length >= 200);
        Assert.StartsWith($"{Environment.ProcessId}\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task NonContentionLockFailureIsUnavailableRatherThanAnotherInstance()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        Directory.CreateDirectory(directories.Runtime);
        var lockPath = Path.Combine(directories.Runtime, Id + ".lock");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(lockPath, lockPath);
        await using var service = SingleInstanceService.Start(Id, directories);

        Assert.Equal(SingleInstanceStatus.Unavailable, service.Result.Status);
        Assert.Contains("Cannot open the single-instance lock", service.Result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Another Playnite instance", service.Result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnusableRuntimeDegradesToUnavailableWithoutThrowing()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var blocked = temporary.Path + "/blocked";
        File.WriteAllText(blocked, "not a directory");
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables["XDG_RUNTIME_DIR"] = blocked;

        await using var service = SingleInstanceService.Start(Id, XdgDirectories.Resolve(environment));

        Assert.Equal(SingleInstanceStatus.Unavailable, service.Result.Status);
        Assert.Contains("Cannot create the runtime directory", service.Result.Diagnostic, StringComparison.Ordinal);
        var activation = await service.ActivateAsync("playnite://search/test");
        Assert.Equal(SingleInstanceStatus.Unavailable, activation.Status);
    }

    [Fact]
    public async Task OverlongSocketPathKeepsOwnershipAndReportsForwardingUnavailable()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var deep = temporary.Path + "/" + new string('r', 90);
        var directories = Directories(temporary, deep[(temporary.Path.Length + 1)..]);

        await using var owner = SingleInstanceService.Start(Id, directories);

        Assert.True(owner.Result.Status == SingleInstanceStatus.Owner, owner.Result.Diagnostic);
        Assert.Contains("Activation forwarding unavailable", owner.Result.Diagnostic, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directories.Runtime, Id + ".lock")));
        Assert.False(File.Exists(Path.Combine(directories.Runtime, Id + ".sock")));

        await using var second = SingleInstanceService.Start(Id, directories);
        Assert.Equal(SingleInstanceStatus.AlreadyRunning, second.Result.Status);
        Assert.Equal(SingleInstanceStatus.Unavailable, (await second.ActivateAsync("playnite://search/test")).Status);
    }

    [Fact]
    public async Task KilledOwnerLeavesNoStaleLockAndTheNextProcessBecomesOwner()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var runtime = temporary.Path + "/run";
        var host = Path.Combine(AppContext.BaseDirectory, "Playnite.SingleInstance.Host.dll");
        Assert.True(File.Exists(host), $"Missing crash host at {host}");

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add(Id);
        start.ArgumentList.Add(runtime);

        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(File.Exists(Path.Combine(runtime, "playnite", Id + ".lock")));

            // Prove cross-process contention while the host is alive, not just after it dies.
            await using (var contender = SingleInstanceService.Start(Id, Directories(temporary, "run")))
            {
                Assert.Equal(SingleInstanceStatus.AlreadyRunning, contender.Result.Status);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.NotEqual(0, process.ExitCode);
        Assert.True(File.Exists(Path.Combine(runtime, "playnite", Id + ".lock")));

        var directories = Directories(temporary, "run");
        await using var replacement = SingleInstanceService.Start(Id, directories);

        Assert.Equal(SingleInstanceStatus.Owner, replacement.Result.Status);
        Assert.Null(replacement.Result.Diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("UPPER")]
    public async Task InvalidInstanceIdsAreProgrammerErrors(string id)
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var directories = Directories(temporary, "run");
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(SingleInstanceService.Start(id, directories)));
    }

    private static XdgDirectories Directories(IntegrationTemporaryDirectory temporary, string runtimeRelative)
    {
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables["XDG_RUNTIME_DIR"] = runtimeRelative.StartsWith('/')
            ? runtimeRelative
            : temporary.Path + "/" + runtimeRelative;
        return XdgDirectories.Resolve(environment);
    }
}

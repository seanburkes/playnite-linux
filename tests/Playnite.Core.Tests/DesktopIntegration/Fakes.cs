using Playnite.Platform.DesktopIntegration;

namespace Playnite.Core.Tests.DesktopIntegration;

internal sealed class FakeDesktopSessionProbe : IDesktopSessionProbe
{
    public DesktopSession Session { get; set; } = new(DesktopSessionKind.None, "test");

    public DesktopSession Probe() => Session;
}

internal sealed class FakeTrayHostProbe : ITrayHostProbe
{
    public TrayHostStatus Status { get; set; } = new(false, "test");

    public int Calls { get; private set; }

    public Task<TrayHostStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Status);
    }
}

internal sealed class FakeTrayIconPresenter : ITrayIconPresenter
{
    public TrayModel? Presented { get; private set; }

    public FakeAsyncHandle Handle { get; } = new();

    public Exception? Failure { get; set; }

    public ValueTask<IAsyncDisposable> PresentAsync(
        TrayModel model,
        CancellationToken cancellationToken = default)
    {
        Presented = model;
        if (Failure is not null)
        {
            throw Failure;
        }

        return ValueTask.FromResult<IAsyncDisposable>(Handle);
    }
}

internal sealed class FakeAsyncHandle : IAsyncDisposable
{
    public int DisposeCalls { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeNotificationBackend : IDesktopNotificationBackend
{
    public string Mechanism { get; init; } = "fake";

    public bool IsAvailable { get; set; } = true;

    public bool Sent { get; set; } = true;

    public Exception? Failure { get; set; }

    public TaskCompletionSource<bool>? Gate { get; set; }

    public List<NotificationRequest> Requests { get; } = [];

    public async Task<NotificationBackendResult> SendAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return new NotificationBackendResult(Sent, Sent ? "sent" : "refused");
    }
}

internal sealed class FakeSleepInhibitorBackend : ISleepInhibitorBackend
{
    public string Mechanism { get; init; } = "fake-inhibitor";

    public bool IsAvailable { get; set; } = true;

    public bool AcquireSucceeds { get; set; } = true;

    public Exception? AcquireFailure { get; set; }

    public int AcquireCalls { get; private set; }

    public List<string> Events { get; } = [];

    public FakeInhibitorHandle? Handle { get; private set; }

    public Task<SleepInhibitorAcquireResult> TryAcquireAsync(
        SleepInhibitionRequest request,
        CancellationToken cancellationToken = default)
    {
        AcquireCalls++;
        Events.Add("acquire");
        if (AcquireFailure is not null)
        {
            throw AcquireFailure;
        }

        if (!AcquireSucceeds)
        {
            return Task.FromResult(new SleepInhibitorAcquireResult(null, "refused"));
        }

        Handle = new FakeInhibitorHandle(Events);
        return Task.FromResult(new SleepInhibitorAcquireResult(Handle, "held"));
    }
}

internal sealed class FakeInhibitorHandle : ISleepInhibitorHandle
{
    private readonly List<string> events;
    private int released;

    public FakeInhibitorHandle(List<string> events) => this.events = events;

    public string Mechanism => "fake-inhibitor";

    public bool IsHeld => Volatile.Read(ref released) == 0;

    public TaskCompletionSource<bool>? ReleaseGate { get; init; }

    public Action? Released { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return;
        }

        if (ReleaseGate is not null)
        {
            await ReleaseGate.Task;
        }

        events.Add("release");
        Released?.Invoke();
    }
}

/// <summary>Service fake with optional gates to observe acquire/release ordering.</summary>
internal sealed class FakeSleepInhibitionService : ISleepInhibitionService
{
    public bool Available { get; set; } = true;

    public Exception? AcquireFailure { get; set; }

    public TaskCompletionSource<bool>? AcquireGate { get; set; }

    public TaskCompletionSource<bool>? ReleaseGate { get; set; }

    public List<string> Events { get; } = [];

    public int AcquireCalls { get; private set; }

    public int ActiveHandles { get; private set; }

    public async Task<SleepInhibitionLease> InhibitAsync(
        SleepInhibitionRequest request,
        CancellationToken cancellationToken = default)
    {
        AcquireCalls++;
        Events.Add("inhibit-acquire");
        if (AcquireGate is not null)
        {
            await AcquireGate.Task.WaitAsync(cancellationToken);
        }

        if (AcquireFailure is not null)
        {
            throw AcquireFailure;
        }

        if (!Available)
        {
            return SleepInhibitionLease.NotHeld(
                DesktopCapability.Unavailable(DesktopFacility.SleepInhibition, "test-unavailable"));
        }

        ActiveHandles++;
        return SleepInhibitionLease.Held(
            DesktopCapability.Available(DesktopFacility.SleepInhibition, "fake", "held"),
            new FakeInhibitorHandle(Events)
            {
                ReleaseGate = ReleaseGate,
                Released = () => ActiveHandles--
            });
    }
}

internal sealed class FakeExternalCommandRunner : IExternalCommandRunner
{
    public List<ExternalCommand> Commands { get; } = [];

    public Queue<ExternalCommandResult> Results { get; } = new();

    public Task<ExternalCommandResult> RunAsync(
        ExternalCommand command,
        CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(
            Results.Count == 0
                ? new ExternalCommandResult(true, 0, "", "", null)
                : Results.Dequeue());
    }
}

internal sealed class FakeExecutableLocator : IExecutableLocator
{
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    public bool Exists(string name) => Names.Contains(name);
}

internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

using Playnite.Platform.Integration;

namespace Playnite.Desktop.Bootstrap;

/// <summary>The composition root's response to the single-instance result.</summary>
public enum StartupDecision
{
    Continue,
    ExitAlreadyRunning
}

/// <summary>
/// Adapts the platform single-instance contract to a startup decision. The concrete Linux
/// service is supplied by the root; this type is testable against any ISingleInstance.
/// </summary>
public sealed class SingleInstanceBootstrap : IAsyncDisposable
{
    private readonly ISingleInstance instance;
    private readonly IAppLogger logger;

    public SingleInstanceBootstrap(ISingleInstance instance, IAppLogger logger)
    {
        this.instance = instance ?? throw new ArgumentNullException(nameof(instance));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Result = instance.Result ?? throw new ArgumentException("The instance has no result.", nameof(instance));
    }

    public SingleInstanceResult Result { get; }

    public StartupDecision DecideStartup()
    {
        switch (Result.Status)
        {
            case SingleInstanceStatus.Owner:
                logger.Info($"single-instance: owner ({Result.Diagnostic ?? "no diagnostic"})");
                return StartupDecision.Continue;

            case SingleInstanceStatus.AlreadyRunning:
                logger.Error($"single-instance: already running ({Result.Diagnostic ?? "no diagnostic"})");
                return StartupDecision.ExitAlreadyRunning;

            case SingleInstanceStatus.Forwarded:
                logger.Info("single-instance: activation forwarded to the owner; this instance exits.");
                return StartupDecision.ExitAlreadyRunning;

            case SingleInstanceStatus.Unavailable:
            default:
                logger.Info($"single-instance: unavailable; continuing without the guard ({Result.Diagnostic ?? "no diagnostic"})");
                return StartupDecision.Continue;
        }
    }

    public ValueTask DisposeAsync() => instance.DisposeAsync();
}

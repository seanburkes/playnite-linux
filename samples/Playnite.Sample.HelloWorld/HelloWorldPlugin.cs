using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace Playnite.Sample.HelloWorld;

/// <summary>
/// Minimal generic plugin: it records the host's start call so a load test can prove the
/// lifecycle extension point was invoked.
/// </summary>
public sealed class HelloWorldPlugin : GenericPlugin
{
    /// <summary>Stable extension id for this sample.</summary>
    public static readonly Guid ExtensionId = Guid.Parse("6f8a1c1e-0d6b-4f4e-9a3e-2f6b1c9d7a01");

    /// <inheritdoc/>
    public override Guid Id => ExtensionId;

    /// <inheritdoc/>
    public override string Name => "Hello World";

    /// <summary>Gets the number of times the host called <see cref="OnApplicationStarted"/>.</summary>
    public int StartedCount { get; private set; }

    /// <inheritdoc/>
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        StartedCount++;
    }
}

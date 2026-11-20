using Playnite.SDK.Events;

namespace Playnite.SDK.Plugins;

/// <summary>
/// Base type for a loadable Playnite extension.
/// </summary>
/// <remarks>
/// PREVIEW, NOT FROZEN. This type is part of the headless preview surface in
/// <c>Playnite.SDK</c>, added by unit p6-009. It deliberately carries no
/// <c>IPlayniteAPI</c> god object: the preview exposes only the lifecycle seam the samples use.
/// It is not covered by the <c>Playnite.SDK.Abstractions</c> ApiCompat freeze and its shape may
/// change before v1. UI extension points remain deferred to p8-007 (ADR-023).
/// </remarks>
public abstract class Plugin : IPlayniteExtension, IDisposable
{
    /// <inheritdoc/>
    public abstract Guid Id { get; }

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <summary>Called once after the host has loaded the extension.</summary>
    public virtual void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
    }
}

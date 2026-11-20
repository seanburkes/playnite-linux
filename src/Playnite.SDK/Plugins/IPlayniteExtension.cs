namespace Playnite.SDK.Plugins;

/// <summary>
/// Identifies a loadable Playnite extension.
/// </summary>
/// <remarks>
/// PREVIEW, NOT FROZEN. This interface is part of the headless preview surface in
/// <c>Playnite.SDK</c>, added by unit p6-009 so the sample plugins can be compiled and loaded.
/// It is not covered by the <c>Playnite.SDK.Abstractions</c> ApiCompat freeze and its shape may
/// change before v1. UI extension points remain deferred to p8-007 (ADR-023).
/// </remarks>
public interface IPlayniteExtension
{
    /// <summary>Gets the stable extension identity.</summary>
    Guid Id { get; }

    /// <summary>Gets the display name.</summary>
    string Name { get; }
}

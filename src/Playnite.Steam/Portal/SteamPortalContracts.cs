namespace Playnite.Steam;

/// <summary>Outcome of one user-driven directory selection through the desktop portal.</summary>
public enum SteamPortalSelectionStatus
{
    /// <summary>The user selected a directory and the portal returned a readable path.</summary>
    Selected,
    /// <summary>The user dismissed the chooser.</summary>
    Cancelled,
    /// <summary>The portal answered but the selection cannot be used (bad path or host-path mismatch).</summary>
    Rejected,
    /// <summary>No desktop portal is available in this session.</summary>
    Unavailable,
}

/// <summary>
/// One portal selection. <see cref="PortalPath"/> is the path the sandbox may read;
/// <see cref="HostPath"/> is the host path reported by the document portal and is used only to
/// associate the grant with a declared Steam library. <see cref="DocumentId"/> is null when the
/// caller is not sandboxed.
/// </summary>
public sealed record SteamPortalSelection(
    SteamPortalSelectionStatus Status,
    string? PortalPath = null,
    string? HostPath = null,
    string? DocumentId = null,
    string? Detail = null)
{
    public static SteamPortalSelection Selected(string portalPath, string? hostPath, string? documentId) =>
        new(SteamPortalSelectionStatus.Selected, portalPath, hostPath, documentId);

    public static SteamPortalSelection Cancelled() => new(SteamPortalSelectionStatus.Cancelled);

    public static SteamPortalSelection Rejected(string detail) =>
        new(SteamPortalSelectionStatus.Rejected, Detail: detail);

    public static SteamPortalSelection Unavailable(string detail) =>
        new(SteamPortalSelectionStatus.Unavailable, Detail: detail);
}

/// <summary>
/// The single portal operation the importer needs. Implementations own all D-Bus detail; tests
/// provide a scripted fake. A caller must not read a host Steam path before a selection succeeds.
/// </summary>
public interface ISteamLibraryPortal
{
    /// <summary>
    /// Ask the user to select one <c>steamapps</c> directory. The returned
    /// <see cref="SteamPortalSelection.PortalPath"/> is the only path the caller may read.
    /// </summary>
    Task<SteamPortalSelection> SelectSteamAppsDirectoryAsync(string title,
        CancellationToken cancellationToken = default);
}

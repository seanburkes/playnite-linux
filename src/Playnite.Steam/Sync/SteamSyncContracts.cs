namespace Playnite.Steam.Sync;

/// <summary>
/// Stable machine-readable reasons for an app that discovery or sync did not write. A skipped
/// app always keeps its Steam app id so a caller can report it instead of dropping it silently.
/// </summary>
public static class SteamSyncReasons
{
    public const string LibraryMissing = "library-missing";
    public const string LibraryNotDirectory = "library-not-directory";
    public const string LibrarySteamAppsMissing = "library-steamapps-missing";
    public const string LibrarySteamAppsOutside = "library-steamapps-outside";
    public const string LibraryStaleMountpoint = "library-stale-mountpoint";
    public const string LibraryDuplicate = "library-duplicate";
    public const string LibraryUnreadable = "library-unreadable";

    public const string ManifestMissing = "manifest-missing";
    public const string ManifestNotRegular = "manifest-not-regular";
    public const string ManifestTooLarge = "manifest-too-large";
    public const string ManifestUnreadable = "manifest-unreadable";
    public const string ManifestRejected = "manifest-rejected";
    public const string ManifestInvalidName = "manifest-invalid-name";

    public const string AppIdDuplicate = "appid-duplicate";
    public const string InvalidInstallPath = "invalid-install-path";

    public const string IdentityConflict = "identity-conflict";
    public const string DuplicateLibraryRecord = "duplicate-library-record";

    public const string SteamRootMissing = "steam-root-missing";
    public const string LibraryEnumerationFailed = "library-enumeration-failed";
}

/// <summary>One app that was not written. The id is always the app the caller can act on.</summary>
public sealed record SteamSyncSkip(uint AppId, string Reason);

/// <summary>A discovery- or sync-level diagnostic that has no single app id.</summary>
public sealed record SteamSyncIssue(string Reason, uint? AppId = null, string? Detail = null);

/// <summary>
/// One read-only discovery of the installed Steam library. <see cref="Skipped"/> covers every app
/// found in a library manifest list that could not be projected. <see cref="CanReconcile"/> is
/// false when the observation cannot prove which apps are absent (for example, no Steam root or
/// an unreadable library directory), so the sync must not mark anything uninstalled.
/// </summary>
public sealed record SteamSyncDiscovery(
    IReadOnlyList<SteamApp> Apps,
    IReadOnlyList<SteamSyncSkip> Skipped,
    IReadOnlyList<SteamSyncIssue> Issues,
    bool CanReconcile)
{
    public static SteamSyncDiscovery Empty(bool canReconcile = false, IReadOnlyList<SteamSyncIssue>? issues = null) =>
        new([], [], issues ?? [], canReconcile);
}

/// <summary>
/// Supplies installed apps from the Steam tree. It only reads; a fake host can supply a fixed
/// snapshot without touching a real Steam installation, the network or the UI.
/// </summary>
public interface ISteamSyncDiscovery
{
    SteamSyncDiscovery Discover(CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of one sync. <see cref="Added"/>, <see cref="Updated"/> and <see cref="Removed"/>
/// count writes; skipped apps are reported by id and reason and are never silently dropped.
/// </summary>
public sealed record SteamSyncResult(
    int Added,
    int Updated,
    int Removed,
    int Unchanged,
    IReadOnlyList<SteamSyncSkip> Skipped,
    IReadOnlyList<SteamSyncIssue> Issues,
    bool Reconciled)
{
    /// <summary>Number of skipped apps; each entry carries an app id and a reason.</summary>
    public int SkippedCount => Skipped.Count;

    /// <summary>Number of records this run changed. A repeat over unchanged input is zero.</summary>
    public int Changes => Added + Updated + Removed;
}

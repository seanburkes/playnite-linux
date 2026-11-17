namespace Playnite.Steam;

/// <summary>Where a root probe came from. Kinds follow the p3-001 candidate contract plus the v1 extras.</summary>
public enum SteamRootKind { Override, HomeSteam, HomeSteamRoot, XdgData, HomeLocalShare, Flatpak }

/// <summary>Why a root candidate did or did not become the discovered root.</summary>
public enum SteamRootState
{
    /// <summary>First candidate whose realpath exists and contains a usable <c>steamapps</c>.</summary>
    Accepted,
    /// <summary>Usable, but an earlier candidate already supplied the root.</summary>
    Shadowed,
    /// <summary>Another candidate already resolved to this realpath.</summary>
    DuplicateRealPath,
    Missing,
    NotDirectory,
    SteamAppsMissing,
    /// <summary><c>steamapps</c> resolves outside the candidate root; it is not followed.</summary>
    SteamAppsOutsideRoot,
    /// <summary>The candidate path is not absolute; the explicit override must be absolute.</summary>
    InvalidPath,
    /// <summary>The filesystem probe failed (for example a symlink loop).</summary>
    Unreadable,
}

/// <summary>A candidate request, before it is probed.</summary>
public sealed record SteamRootCandidate(SteamRootKind Kind, string Path);

/// <summary>What happened to one candidate. <see cref="RealPath"/> is null when it does not exist.</summary>
public sealed record SteamRootProbe(SteamRootKind Kind, string Path, string? RealPath, SteamRootState State);

/// <summary>The selected root realpath, if any, plus every candidate considered and its outcome.</summary>
public sealed record SteamRootDiscovery(string? Root, IReadOnlyList<SteamRootProbe> Candidates);

/// <summary>
/// Availability of a declared library entry. Everything other than <see cref="Available"/> is the
/// spec's <c>Unavailable</c> state with a stable reason.
/// </summary>
public enum SteamLibraryStatus
{
    Available,
    MissingPath,
    NotDirectory,
    SteamAppsMissing,
    /// <summary><c>steamapps</c> resolves outside the library root; it is not followed.</summary>
    SteamAppsOutsideLibrary,
    /// <summary>The path is an existing mount point with no <c>steamapps</c>: an unmounted drive.</summary>
    StaleMountpoint,
    /// <summary>An earlier library entry in file order resolved to the same realpath.</summary>
    DuplicateLibrary,
    Unreadable,
}

/// <summary>
/// One parsed library entry joined with a read-only filesystem observation. <see cref="Declared"/>
/// keeps the parser's apps map even when the library is unavailable, so unmounted libraries remain
/// visible; <see cref="ResolvedPath"/> is the canonical path when the entry exists.
/// </summary>
public sealed record SteamDiscoveredLibrary(
    string Index,
    SteamLibrary Declared,
    string ResolvedPath,
    SteamLibraryStatus Status)
{
    public bool IsAvailable => Status == SteamLibraryStatus.Available;
    public string DeclaredPath => Declared.Path;

    /// <summary>
    /// The declared apps map, or null when the file omitted the object. Presence follows the p5-001
    /// contract: absent is null, a present but empty map is an empty non-null map.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong>? Apps => Declared.Apps;
}

/// <summary>
/// Root discovery, the library file selected (if any), every library entry and every skip. Unknown
/// root fields are discarded: they are parser detail, not discovery state.
/// </summary>
public sealed record SteamDiscoveryResult(
    SteamRootDiscovery Roots,
    string? LibraryFilePath,
    IReadOnlyList<SteamDiscoveredLibrary> Libraries,
    IReadOnlyList<SteamParseIssue> Issues)
{
    /// <summary>Canonical root, or null when Steam is not installed.</summary>
    public string? Root => Roots.Root;
}

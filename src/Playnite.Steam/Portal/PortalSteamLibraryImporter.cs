using Playnite.Core;

namespace Playnite.Steam;

/// <summary>Outcome of one portable library import.</summary>
public enum SteamPortalImportStatus
{
    /// <summary>Every declared library was granted and its app set was read.</summary>
    Complete,
    /// <summary>Some declared libraries were skipped by the user, the rest were read.</summary>
    Partial,
    /// <summary>The user dismissed the primary chooser.</summary>
    Cancelled,
    /// <summary>The user selected something that is not the required <c>steamapps</c> directory.</summary>
    Rejected,
    /// <summary>No usable desktop portal is available in this session.</summary>
    Unavailable,
}

/// <summary>One granted library and its imported app set.</summary>
public sealed record SteamPortalLibrary(
    string HostLibraryPath,
    string SteamAppsHostPath,
    string PortalPath,
    IReadOnlyList<SteamAppEntry> Apps);

/// <summary>
/// Import result. <see cref="Grants"/> is the set that was persisted when the import completed;
/// a failed import persists nothing.
/// </summary>
public sealed record SteamPortalImportResult(
    SteamPortalImportStatus Status,
    IReadOnlyList<SteamPortalLibrary> Libraries,
    IReadOnlyList<SteamPortalGrant> Grants,
    IReadOnlyList<string> Issues,
    string? Detail = null);

/// <summary>
/// Imports the host Steam library through desktop-portal grants. The importer never opens a host
/// path: it reads only <see cref="SteamPortalGrant.PortalPath"/> after the user selected it. The
/// first run asks for the primary <c>steamapps</c> directory, then for every additional library
/// declared in <c>libraryfolders.vdf</c>; later runs reuse the persisted grants until one no longer
/// resolves (a revoked or deleted grant), which is dropped and asked for again.
/// </summary>
public sealed class PortalSteamLibraryImporter(
    ISteamLibraryPortal portal,
    ISteamPortalGrantStore grantStore,
    ISteamFileSystem fileSystem)
{
    private const string LibraryFile = "libraryfolders.vdf";

    public async Task<SteamPortalImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(grantStore);
        ArgumentNullException.ThrowIfNull(fileSystem);
        cancellationToken.ThrowIfCancellationRequested();

        var grants = grantStore.Load().Where(IsUsable).ToList();
        var primary = grants.FirstOrDefault(grant =>
            fileSystem.FileExists(Path.Combine(grant.PortalPath, LibraryFile)));
        if (primary is null)
        {
            var selection = await portal.SelectSteamAppsDirectoryAsync(
                "Select the primary Steam library's steamapps directory", cancellationToken).ConfigureAwait(false);
            if (Failure(selection) is { } failure)
            {
                return failure;
            }

            if (!fileSystem.FileExists(Path.Combine(selection.PortalPath!, LibraryFile)))
            {
                return Rejected("The selected directory does not contain libraryfolders.vdf; select a Steam steamapps directory.");
            }

            if (!TryGrant(selection, out primary))
            {
                return Rejected("The portal did not report a document id and usable host path for the selected directory.");
            }

            grants.RemoveAll(grant => Paths.AreEqual(grant.SteamAppsHostPath, primary.SteamAppsHostPath, "/"));
            grants.Add(primary);
        }

        var declared = ParseLibraries(primary);
        if (declared.Failure is { } parseFailure)
        {
            return parseFailure;
        }

        var libraries = new List<SteamPortalLibrary>();
        var issues = new List<string>();
        var skipped = 0;
        foreach (var library in declared.Libraries!)
        {
            var expected = SteamAppsPath(library.Path);
            var grant = grants.FirstOrDefault(candidate =>
                Paths.AreEqual(candidate.SteamAppsHostPath, expected, "/"));
            if (grant is null)
            {
                var selection = await portal.SelectSteamAppsDirectoryAsync(
                    $"Select the steamapps directory for {library.Path}", cancellationToken).ConfigureAwait(false);
                if (selection.Status == SteamPortalSelectionStatus.Cancelled)
                {
                    // A declared library can be an external drive that is not mounted. Dismissing
                    // its chooser means "not available now": keep the grants already obtained and
                    // import the other libraries. The primary library above is still mandatory.
                    skipped++;
                    issues.Add($"{expected}: skipped (selection cancelled)");
                    continue;
                }

                if (Failure(selection) is { } failure)
                {
                    return failure;
                }

                if (!string.Equals(Path.TrimEndingDirectorySeparator(selection.HostPath!), expected, StringComparison.Ordinal))
                {
                    return Rejected($"The selected directory is not the required {expected}.");
                }

                if (!TryGrant(selection, out grant))
                {
                    return Rejected("The portal did not report a document id and usable host path for the selected directory.");
                }

                grants.Add(grant);
            }

            var manifests = SteamManifestSet.Read(fileSystem, grant.PortalPath);
            issues.AddRange(manifests.Issues.Select(issue => $"{expected}: {issue}"));
            libraries.Add(new(library.Path, grant.SteamAppsHostPath, grant.PortalPath, manifests.Apps));
        }

        grantStore.Save(grants);
        return new(skipped == 0 ? SteamPortalImportStatus.Complete : SteamPortalImportStatus.Partial,
            libraries.AsReadOnly(), grants.AsReadOnly(), issues.AsReadOnly());
    }

    private bool IsUsable(SteamPortalGrant grant) =>
        grant.IsConsistent && fileSystem.DirectoryExists(grant.PortalPath);

    private static bool TryGrant(SteamPortalSelection selection, out SteamPortalGrant grant)
    {
        grant = new(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(selection.HostPath!)) ?? string.Empty,
            Path.TrimEndingDirectorySeparator(selection.HostPath!),
            selection.DocumentId ?? string.Empty,
            Path.TrimEndingDirectorySeparator(selection.PortalPath!));
        return grant.IsConsistent;
    }

    private (IReadOnlyList<SteamLibrary>? Libraries, SteamPortalImportResult? Failure) ParseLibraries(SteamPortalGrant primary)
    {
        var read = fileSystem.ReadFile(Path.Combine(primary.PortalPath, LibraryFile), KeyValues.DefaultMaxBytes);
        if (read.Status != SteamFileReadStatus.Ok || read.Bytes is null)
        {
            return (null, Rejected($"libraryfolders.vdf is {read.Status}."));
        }

        var parsed = SteamLibraryParser.Parse(read.Bytes, LibraryFile);
        if (parsed.Value is null)
        {
            return (null, Rejected("libraryfolders.vdf was rejected by the Steam parser."));
        }

        return (parsed.Value.Libraries, null);
    }

    private static SteamPortalImportResult? Failure(SteamPortalSelection selection) => selection.Status switch
    {
        SteamPortalSelectionStatus.Selected => null,
        SteamPortalSelectionStatus.Cancelled => new(SteamPortalImportStatus.Cancelled, [], [], [],
            "The directory selection was cancelled."),
        SteamPortalSelectionStatus.Unavailable => new(SteamPortalImportStatus.Unavailable, [], [], [],
            selection.Detail ?? "No desktop portal is available."),
        SteamPortalSelectionStatus.Rejected => Rejected(selection.Detail ?? "The portal rejected the selection."),
        _ => throw new InvalidOperationException("Unknown portal selection status."),
    };

    private static SteamPortalImportResult Rejected(string detail) =>
        new(SteamPortalImportStatus.Rejected, [], [], [], detail);

    private static string SteamAppsPath(string hostLibraryPath) =>
        Path.TrimEndingDirectorySeparator(Path.Combine(Path.TrimEndingDirectorySeparator(hostLibraryPath), "steamapps"));
}

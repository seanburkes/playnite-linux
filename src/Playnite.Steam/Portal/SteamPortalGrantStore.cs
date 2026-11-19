using System.Text.Json;
using Playnite.Core;
using System.Text.Json.Serialization;

namespace Playnite.Steam;

/// <summary>
/// A persisted portal grant: one host <c>steamapps</c> directory, the document id the desktop
/// portal issued for it, and the portal-visible path the sandbox may read. No other host path is
/// implied.
/// </summary>
public sealed record SteamPortalGrant(string HostLibraryPath, string SteamAppsHostPath, string DocumentId, string PortalPath)
{
    /// <summary><see cref="HostLibraryPath"/> is the Steam root; the grant is exactly its steamapps.</summary>
    public bool IsConsistent =>
        Paths.IsFullPath(HostLibraryPath) &&
        Paths.IsFullPath(SteamAppsHostPath) &&
        Paths.IsFullPath(PortalPath) &&
        !string.IsNullOrEmpty(DocumentId) &&
        string.Equals(Path.GetFileName(SteamAppsHostPath), "steamapps", StringComparison.Ordinal) &&
        string.Equals(Path.GetDirectoryName(SteamAppsHostPath), HostLibraryPath, StringComparison.Ordinal);
}

/// <summary>Reads and replaces the persisted grant set. A save is all-or-nothing.</summary>
public interface ISteamPortalGrantStore
{
    IReadOnlyList<SteamPortalGrant> Load();

    void Save(IReadOnlyList<SteamPortalGrant> grants);
}

/// <summary>
/// JSON grant store. The file is written beside its destination and renamed into place, with mode
/// 0600, so a crash cannot leave a partial grant file and another user cannot read the grant set.
/// A missing or malformed file loads as an empty set: the importer then asks for selection again.
/// </summary>
public sealed class JsonSteamPortalGrantStore(string path) : ISteamPortalGrantStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public IReadOnlyList<SteamPortalGrant> Load()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize<GrantFile>(File.ReadAllText(Path), Options);
            return document?.Grants?.Where(grant => grant is not null && grant.IsConsistent).ToArray() ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<SteamPortalGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        foreach (var grant in grants)
        {
            if (!grant.IsConsistent)
            {
                throw new ArgumentException("Every grant must name an absolute Steam root, its steamapps directory and a document id.", nameof(grants));
            }
        }

        var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new ArgumentException("Grant path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Environment.ProcessId}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(new GrantFile { Grants = grants }, Options);
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, streamOptions))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Write('\n');
            }

            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record GrantFile
    {
        public int Version { get; init; } = 1;

        [JsonPropertyName("grants")]
        public IReadOnlyList<SteamPortalGrant> Grants { get; init; } = [];
    }
}

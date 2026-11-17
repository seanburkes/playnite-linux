using System.Globalization;

namespace Playnite.Steam;

/// <summary>A value-free diagnostic. Identity is the filename appid or library index, not a user id.</summary>
public sealed record SteamParseIssue(string File, string Identity, string Reason, string? Field, int ByteOffset)
{
    public string File { get; init; } = Path.GetFileName(File);
}

/// <summary>A rejected file has a null value; valid library files can contain individually skipped records.</summary>
public sealed record SteamParseResult<T>(T? Value, IReadOnlyList<SteamParseIssue> Issues) where T : class;

public sealed record SteamLibrary(
    string Index,
    string Path,
    string? Label,
    ulong? ContentId,
    ulong? TotalSize,
    ulong? UpdateCleanBytesTally,
    DateTimeOffset? TimeLastUpdateVerified,
    IReadOnlyDictionary<uint, ulong> Apps,
    IReadOnlyList<KeyValue> UnknownFields);

public sealed record SteamLibraries(IReadOnlyList<SteamLibrary> Libraries, IReadOnlyList<KeyValue> UnknownFields);

public sealed record SteamDepot(ulong Manifest, ulong Size, uint? DlcAppId, IReadOnlyList<KeyValue> UnknownFields);

/// <summary>Decoded manifest data, not proof of filesystem availability or Proton selection.</summary>
public sealed record SteamManifest(
    uint AppId,
    string Name,
    string InstallDir,
    uint StateFlags,
    ulong? SizeOnDisk,
    DateTimeOffset? LastUpdated,
    IReadOnlyDictionary<string, ulong> OptionalNumbers,
    IReadOnlyDictionary<uint, SteamDepot>? InstalledDepots,
    IReadOnlyDictionary<uint, uint>? SharedDepots,
    IReadOnlyDictionary<string, string>? InstallScripts,
    IReadOnlyList<KeyValue>? UserConfig,
    IReadOnlyList<KeyValue>? MountedConfig,
    IReadOnlyList<KeyValue> UnknownFields)
{
    // Do not emit names, paths, play timestamps or other metadata through record formatting.
    public override string ToString() => $"SteamManifest {AppId}";

    /// <summary>Only caller-supplied observations decide availability/Proton; this method does no I/O.</summary>
    public SteamApp ToApp(string libraryPath, bool libraryAvailable, bool installDirectoryExists, bool isProton)
    {
        if (!System.IO.Path.IsPathFullyQualified(libraryPath))
        {
            throw new ArgumentException("An absolute library path is required.", nameof(libraryPath));
        }

        var state = !libraryAvailable ? SteamAppState.Unavailable
            : (StateFlags & 4) != 0 && !installDirectoryExists ? SteamAppState.Broken
            : (StateFlags & ~6u) != 0 ? SteamAppState.Unknown
            : (StateFlags & 4) == 0 ? SteamAppState.Unknown
            : (StateFlags & 2) != 0 ? SteamAppState.UpdatePending
            : SteamAppState.Installed;
        return new SteamApp(AppId, Name, InstallDir, System.IO.Path.GetFullPath(libraryPath), state,
            isProton, SizeOnDisk, LastUpdated, StateFlags);
    }
}

public enum SteamAppState { Installed, UpdatePending, Broken, Unavailable, Unknown }

/// <summary>Missing optional size stays null: it is not guessed to be zero.</summary>
public sealed record SteamApp(uint AppId, string Name, string InstallDir, string LibraryPath,
    SteamAppState State, bool IsProton, ulong? SizeOnDisk, DateTimeOffset? LastUpdated, uint StateFlags)
{
    public string InstallPath => Path.GetFullPath(Path.Combine(LibraryPath, "steamapps", "common", InstallDir));
    public override string ToString() => $"SteamApp {AppId} ({State})";
}

internal static class SteamFields
{
    internal static bool IsOwner(string key) => string.Equals(key, "LastOwner", StringComparison.OrdinalIgnoreCase);

    private static bool AsciiDigits(string? text) => !string.IsNullOrEmpty(text) && text.All(c => c is >= '0' and <= '9');

    internal static bool Decimal(string? text, out ulong value) =>
        ulong.TryParse(AsciiDigits(text) ? text : null, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    internal static bool Id(string? text, out uint value) =>
        uint.TryParse(AsciiDigits(text) ? text : null, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    internal static ulong? Number(KeyValuesObject obj, string key, List<SteamParseIssue> issues, string file, string identity)
    {
        var entry = obj.Find(key);
        if (entry is null)
        {
            return null;
        }

        if (Decimal(entry.Text, out var value))
        {
            return value;
        }

        issues.Add(new(file, identity, "invalid-optional-number", IsOwner(key) ? null : key, entry.ByteOffset));
        return null;
    }

    internal static DateTimeOffset? Timestamp(KeyValuesObject obj, string key, List<SteamParseIssue> issues, string file, string identity)
    {
        var value = Number(obj, key, issues, file, identity);
        if (value is null)
        {
            return null;
        }

        if (value <= (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)value);
        }

        issues.Add(new(file, identity, "invalid-optional-timestamp", key, obj.Find(key)!.ByteOffset));
        return null;
    }

    internal static IReadOnlyList<KeyValue> Bag(IEnumerable<KeyValue> entries) => entries
        .Where(e => !IsOwner(e.Key))
        .Select(e => e.Object is null ? e : e with { Object = new KeyValuesObject(Bag(e.Object.Entries).ToList()) })
        .ToList().AsReadOnly();

    internal static SteamParseIssue Syntax(KeyValuesException exception, string identity) =>
        new(exception.File, identity, exception.Reason, exception.Key, exception.ByteOffset);
}

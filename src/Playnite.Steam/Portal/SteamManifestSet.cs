namespace Playnite.Steam;

/// <summary>One imported Steam application: the manifest app id and its display name.</summary>
public sealed record SteamAppEntry(uint AppId, string Name);

/// <summary>The parsed manifests of one <c>steamapps</c> directory plus non-fatal read issues.</summary>
public sealed record SteamManifestSetResult(IReadOnlyList<SteamAppEntry> Apps, IReadOnlyList<string> Issues);

/// <summary>
/// Reads the installed-app manifests from one <c>steamapps</c> directory. Pure read: only
/// <c>appmanifest_&lt;appid&gt;.acf</c> names are opened, each through the bounded, link-refusing
/// reader. Used by both the direct (AppImage) and portal (Flatpak) paths so they produce the same
/// app set.
/// </summary>
public static class SteamManifestSet
{
    public static SteamManifestSetResult Read(ISteamFileSystem fileSystem, string steamAppsPath,
        int maxBytes = KeyValues.DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(steamAppsPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var apps = new List<SteamAppEntry>();
        var issues = new List<string>();
        string[] files;
        try
        {
            files = Directory.GetFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(apps.AsReadOnly(), [$"steamapps-unreadable: {exception.GetType().Name}"]);
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!TryAppId(name, out _))
            {
                continue;
            }

            var read = fileSystem.ReadFile(file, maxBytes);
            if (read.Status != SteamFileReadStatus.Ok || read.Bytes is null)
            {
                issues.Add($"{name}: {read.Status}");
                continue;
            }

            var parsed = SteamManifestParser.Parse(read.Bytes, name);
            if (parsed.Value is null)
            {
                issues.Add($"{name}: parse-rejected");
                continue;
            }

            apps.Add(new(parsed.Value.AppId, parsed.Value.Name));
        }

        apps.Sort((left, right) => left.AppId.CompareTo(right.AppId));
        return new(apps.AsReadOnly(), issues.AsReadOnly());
    }

    private static bool TryAppId(string file, out uint appId)
    {
        appId = 0;
        const string prefix = "appmanifest_";
        const string suffix = ".acf";
        if (!file.StartsWith(prefix, StringComparison.Ordinal) ||
            !file.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = file.AsSpan(prefix.Length, file.Length - prefix.Length - suffix.Length);
        return digits.Length is > 0 and <= 10 && uint.TryParse(digits, out appId);
    }
}

using System.Collections.ObjectModel;
using System.Globalization;

namespace Playnite.Steam;

/// <summary>Strict appmanifest model binding. Skip reports retain filename identity, never scalar values.</summary>
public static class SteamManifestParser
{
    private static readonly string[] OptionalNumbers =
    [
        "Universe", "StagingSize", "LastPlayed", "buildid", "DownloadType", "UpdateResult", "BytesToDownload",
        "BytesDownloaded", "BytesToStage", "BytesStaged", "BytesToVerify", "BytesVerified", "TargetBuildID",
        "AutoUpdateBehavior", "AllowOtherDownloadsWhileRunning", "ScheduledAutoUpdate",
    ];
    private static readonly HashSet<string> Known = new(OptionalNumbers.Concat(
        ["appid", "name", "installdir", "StateFlags", "SizeOnDisk", "lastupdated", "LastOwner", "InstalledDepots",
         "SharedDepots", "InstallScripts", "UserConfig", "MountedConfig"]), StringComparer.Ordinal);

    public static SteamParseResult<SteamManifest> Parse(ReadOnlySpan<byte> bytes, string file, int maxBytes = KeyValues.DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        var identity = Path.GetFileName(file);
        if (!identity.StartsWith("appmanifest_", StringComparison.Ordinal) || !identity.EndsWith(".acf", StringComparison.Ordinal)
            || !SteamFields.Id(identity[12..^4], out var appId))
        {
            return new(null, [new(file, identity, "invalid-manifest-filename", null, 0)]);
        }

        identity = appId.ToString(CultureInfo.InvariantCulture);
        KeyValue root;
        try
        {
            root = KeyValues.Parse(bytes, file, maxBytes);
        }
        catch (KeyValuesException exception)
        {
            return new(null, [SteamFields.Syntax(exception, identity)]);
        }

        if (root.Key != "AppState")
        {
            return new(null, [new(file, identity, "invalid-root", null, root.ByteOffset)]);
        }

        var fields = root.Object!;
        var issues = new List<SteamParseIssue>();
        void Invalid(string key, string reason) => issues.Add(new(file, identity, reason, key, fields.Find(key)?.ByteOffset ?? root.ByteOffset));
        if (!SteamFields.Id(fields.Text("appid"), out var declared))
        {
            Invalid("appid", "invalid-required-field");
        }
        else if (declared != appId)
        {
            Invalid("appid", "appid-mismatch");
        }

        var name = fields.Text("name");
        if (string.IsNullOrEmpty(name))
        {
            Invalid("name", "invalid-required-field");
        }

        var installDir = fields.Text("installdir");
        if (!SafeInstallDir(installDir))
        {
            Invalid("installdir", "invalid-required-field");
        }

        if (!SteamFields.Id(fields.Text("StateFlags"), out var flags))
        {
            Invalid("StateFlags", "invalid-required-field");
        }

        if (issues.Count != 0)
        {
            return new(null, issues.AsReadOnly());
        }

        var size = SteamFields.Number(fields, "SizeOnDisk", issues, file, identity);
        var updated = SteamFields.Timestamp(fields, "lastupdated", issues, file, identity);
        var numbers = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var key in OptionalNumbers)
        {
            var number = key is "LastPlayed" or "ScheduledAutoUpdate"
                ? (ulong?)SteamFields.Timestamp(fields, key, issues, file, identity)?.ToUnixTimeSeconds()
                : SteamFields.Number(fields, key, issues, file, identity);
            if (number is { } value)
            {
                numbers.Add(key, value);
            }
        }

        // Validate but deliberately discard owner identity, including from the unknown bag.
        _ = SteamFields.Number(fields, "LastOwner", issues, file, identity);
        KeyValuesObject? OptionalObject(string key)
        {
            var entry = fields.Find(key);
            if (entry is not null && entry.Object is null)
            {
                Invalid(key, "invalid-optional-object");
            }

            return entry?.Object;
        }

        var depots = new Dictionary<uint, SteamDepot>();
        if (OptionalObject("InstalledDepots") is { } installed)
        {
            foreach (var entry in installed.Entries)
            {
                var depot = entry.Object;
                if (!SteamFields.Id(entry.Key, out var id) || depot is null
                    || !SteamFields.Decimal(depot.Text("manifest"), out var manifest)
                    || !SteamFields.Decimal(depot.Text("size"), out var depotSize)
                    || depots.ContainsKey(id))
                {
                    Invalid("InstalledDepots", "invalid-depot");
                    continue;
                }

                uint? dlc = null;
                if (depot.Find("dlcappid") is { } dlcEntry)
                {
                    if (SteamFields.Id(dlcEntry.Text, out var dlcId))
                    {
                        dlc = dlcId;
                    }
                    else
                    {
                        Invalid("InstalledDepots", "invalid-optional-number");
                    }
                }

                depots.Add(id, new(manifest, depotSize, dlc,
                    SteamFields.Bag(depot.Entries.Where(e => e.Key is not ("manifest" or "size" or "dlcappid")))));
            }
        }

        var shared = new Dictionary<uint, uint>();
        if (OptionalObject("SharedDepots") is { } sharedObject)
        {
            foreach (var entry in sharedObject.Entries)
            {
                if (!SteamFields.Id(entry.Key, out var id) || !SteamFields.Id(entry.Text, out var owner) || !shared.TryAdd(id, owner))
                {
                    Invalid("SharedDepots", "invalid-shared-depot");
                }
            }
        }

        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OptionalObject("InstallScripts") is { } scriptsObject)
        {
            foreach (var entry in scriptsObject.Entries.Where(e => e.Key != "LastOwner"))
            {
                if (entry.Text is { } script)
                {
                    scripts.Add(entry.Key, script);
                }
                else
                {
                    Invalid("InstallScripts", "invalid-install-script");
                }
            }
        }

        var user = OptionalObject("UserConfig");
        var mounted = OptionalObject("MountedConfig");
        return new(new(appId, name!, installDir!, flags, size, updated,
            new ReadOnlyDictionary<string, ulong>(numbers), new ReadOnlyDictionary<uint, SteamDepot>(depots),
            new ReadOnlyDictionary<uint, uint>(shared), new ReadOnlyDictionary<string, string>(scripts),
            user is null ? null : SteamFields.Bag(user.Entries), mounted is null ? null : SteamFields.Bag(mounted.Entries),
            SteamFields.Bag(fields.Entries.Where(e => !Known.Contains(e.Key)))), issues.AsReadOnly());
    }

    private static bool SafeInstallDir(string? path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        // POSIX only: a backslash in Steam metadata is never a separator.
        var depth = 0;
        foreach (var component in path.Split('/'))
        {
            if (component == "..")
            {
                if (--depth < 0)
                {
                    return false;
                }
            }
            else if (component is not ("" or "."))
            {
                depth++;
            }
        }

        return depth > 0;
    }
}

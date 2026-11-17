namespace Playnite.Steam;

/// <summary>Parses library records only. Availability checks and mount management belong to discovery.</summary>
public static class SteamLibraryParser
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "path", "label", "contentid", "totalsize", "update_clean_bytes_tally", "time_last_update_verified", "apps",
    };

    public static SteamParseResult<SteamLibraries> Parse(ReadOnlySpan<byte> bytes, string file, int maxBytes = KeyValues.DefaultMaxBytes)
    {
        var issues = new List<SteamParseIssue>();
        KeyValue root;
        try
        {
            root = KeyValues.Parse(bytes, file, maxBytes);
        }
        catch (KeyValuesException exception)
        {
            return new(null, [SteamFields.Syntax(exception, "libraryfolders")]);
        }

        if (root.Key != "libraryfolders")
        {
            return new(null, [new(file, "libraryfolders", "invalid-root", null, root.ByteOffset)]);
        }

        var libraries = new List<SteamLibrary>();
        var unknown = new List<KeyValue>();
        var appIds = new HashSet<uint>();
        var indexes = new Dictionary<uint, KeyValue>();
        foreach (var entry in root.Object!.Entries)
        {
            if (SteamFields.Id(entry.Key, out var index) && !indexes.TryAdd(index, entry))
            {
                return new(null, [new(file, "libraryfolders", "duplicate-library-index",
                    indexes[index].Key + "/" + entry.Key, entry.ByteOffset)]);
            }
        }

        foreach (var entry in root.Object!.Entries)
        {
            if (!SteamFields.Id(entry.Key, out _))
            {
                unknown.Add(entry);
                continue;
            }

            var fields = entry.Object;
            var path = fields is null ? entry.Text : fields.Text("path");
            string fullPath;
            try
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                {
                    throw new ArgumentException("An absolute library path is required.");
                }

                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                issues.Add(new(file, entry.Key, "invalid-library-path", "path", fields?.Find("path")?.ByteOffset ?? entry.ByteOffset));
                continue;
            }

            var apps = new Dictionary<uint, ulong>();
            if (fields?.Find("apps") is { } appEntry)
            {
                var valid = appEntry.Object is not null;
                if (appEntry.Object is { } appObject)
                {
                    foreach (var app in appObject.Entries)
                    {
                        if (!SteamFields.Id(app.Key, out var id) || !SteamFields.Decimal(app.Text, out var size) || !apps.TryAdd(id, size))
                        {
                            valid = false;
                            break;
                        }
                    }
                }

                if (!valid)
                {
                    apps.Clear();
                    issues.Add(new(file, entry.Key, "invalid-apps-map", "apps", appEntry.ByteOffset));
                }
            }

            foreach (var id in apps.Keys.ToArray())
            {
                if (!appIds.Add(id))
                {
                    apps.Remove(id);
                    issues.Add(new(file, entry.Key, "duplicate-appid", id.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.ByteOffset));
                }
            }

            ulong? Number(string key) => fields is null ? null : SteamFields.Number(fields, key, issues, file, entry.Key);
            DateTimeOffset? verified = fields is null ? null
                : SteamFields.Timestamp(fields, "time_last_update_verified", issues, file, entry.Key);
            if (fields?.Find("label") is { Text: null } label)
            {
                issues.Add(new(file, entry.Key, "invalid-optional-string", "label", label.ByteOffset));
            }

            libraries.Add(new(entry.Key, fullPath, fields?.Text("label"), Number("contentid"),
                Number("totalsize"), Number("update_clean_bytes_tally"), verified,
                new System.Collections.ObjectModel.ReadOnlyDictionary<uint, ulong>(apps),
                fields is null ? [] : SteamFields.Bag(fields.Entries.Where(e => !Known.Contains(e.Key)))));
        }

        return new(new(libraries.AsReadOnly(), SteamFields.Bag(unknown)), issues.AsReadOnly());
    }
}

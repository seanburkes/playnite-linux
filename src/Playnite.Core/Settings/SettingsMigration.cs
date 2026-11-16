using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playnite.Settings;

public enum SettingsWriteStep
{
    BeforeWrite,
    Written,
    Flushed,
    Verified,
    Committed
}

public sealed record SettingsPathMapping(string WindowsRoot, string LinuxRoot);
public sealed record SettingsPathChange(string Property, string Source, string? Destination);
public sealed record SettingsMigrationReport(
    IReadOnlyList<SettingsPathChange> RewrittenPaths,
    IReadOnlyList<SettingsPathChange> UnmappedPaths,
    IReadOnlyList<string> PreservedUnknownProperties);
public sealed record SettingsMigrationResult(SettingsMigrationReport Report, bool AlreadyMigrated, bool DryRun);

/// <summary>Lossless v10 settings import. No defaults, scripts or path values are executed.</summary>
public static class SettingsMigration
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> PathProperties = new(StringComparer.Ordinal)
    {
        "DatabasePath", "AutoBackupDir"
    };

    public static async Task<SettingsMigrationResult> MigrateAsync(
        string sourceFile, string destinationFile, IReadOnlyList<SettingsPathMapping> mappings,
        bool dryRun = false, Action<SettingsWriteStep>? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceFile);
        var destination = Path.GetFullPath(destinationFile);
        RejectSymbolicLinks(source);
        RejectSymbolicLinks(destination);
        var sourceDirectory = Path.GetDirectoryName(source)!;
        if (sourceDirectory.Length <= 1)
        {
            throw new ArgumentException("A settings source directly under the filesystem root is not supported.", nameof(sourceFile));
        }

        if (destination.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The destination must be outside the source directory.", nameof(destinationFile));
        }

        var original = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        var (bytes, report) = Convert(original, mappings);
        if (File.Exists(destination))
        {
            var existing = await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new IOException("The destination already contains different settings; refusing to overwrite it.");
            }

            return new(report, true, dryRun);
        }

        if (dryRun)
        {
            return new(report, false, true);
        }

        // A fixed sibling staging name lets a restart find and discard the abandoned stage.
        // A stale file is unlinked under an exclusive handle, never truncated (it could be a
        // hard link). Partial staging is never adopted.
        var temporary = destination + ".settings-migration.tmp";
        RejectSymbolicLinks(temporary);
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke(SettingsWriteStep.BeforeWrite);
        using (var stream = OpenStage(temporary))
        {
            var committed = false;
            try
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                checkpoint?.Invoke(SettingsWriteStep.Written);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                checkpoint?.Invoke(SettingsWriteStep.Flushed);
                stream.Position = 0;
                var verified = new byte[bytes.Length];
                await stream.ReadExactlyAsync(verified, cancellationToken).ConfigureAwait(false);
                if (!verified.AsSpan().SequenceEqual(bytes))
                {
                    throw new IOException("Staged settings verification failed.");
                }

                _ = ExportV10(verified);
                checkpoint?.Invoke(SettingsWriteStep.Verified);
                var sourceNow = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
                if (!sourceNow.AsSpan().SequenceEqual(original))
                {
                    throw new IOException("Source settings changed during migration.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                committed = true;
                checkpoint?.Invoke(SettingsWriteStep.Committed);
            }
            finally
            {
                // Unlink while still owning the staging handle: another writer must not lose
                // its file between our close and cleanup. Process death leaves a partial stage
                // that the next attempt deletes; it is never resumed from.
                if (!committed)
                {
                    File.Delete(temporary);
                }
            }
        }

        return new(report, false, false);
    }

    /// <summary>Returns the exact migrated v10 bytes, including untouched value spelling/layout.</summary>
    public static byte[] ExportV10(byte[] migratedSettings)
    {
        using var document = JsonDocument.Parse(migratedSettings);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported settings migration schema.");
        }

        var bytes = System.Convert.FromBase64String(root.GetProperty("settingsUtf8").GetString()!);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var archived = ReadV10(Utf8.GetString(bytes, offset, bytes.Length - offset));
        var projected = ReadV10(root.GetProperty("settings").GetRawText());
        var legacy = ReadV10(root.GetProperty("legacy").GetRawText());
        foreach (var property in legacy.Properties())
        {
            if (projected.ContainsKey(property.Name))
            {
                throw new InvalidDataException("A setting occurs in both settings and legacy.");
            }
            projected.Add(property.Name, property.Value.DeepClone());
        }
        if (!JToken.DeepEquals(archived, projected))
        {
            throw new InvalidDataException("Settings and their lossless round-trip representation disagree.");
        }
        return bytes;
    }

    private static (byte[] Bytes, SettingsMigrationReport Report) Convert(
        byte[] source, IReadOnlyList<SettingsPathMapping> mappings)
    {
        var orderedMappings = mappings.Select(ValidateMapping)
            .OrderByDescending(mapping => mapping.WindowsRoot.Length)
            .ThenBy(mapping => mapping.WindowsRoot, StringComparer.OrdinalIgnoreCase).ToArray();
        if (orderedMappings.Select(mapping => mapping.WindowsRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count() != orderedMappings.Length)
        {
            throw new ArgumentException("Duplicate Windows path mappings.", nameof(mappings));
        }

        var offset = source.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var text = Utf8.GetString(source, offset, source.Length - offset);
        var config = ReadV10(text);

        var rewritten = new List<SettingsPathChange>();
        var unmapped = new List<SettingsPathChange>();
        var unknown = config.Properties().Where(property => !V10SettingsProperties.Names.Contains(property.Name))
            .Select(property => property.Name).ToArray();
        var legacy = new JObject();
        foreach (var property in unknown)
        {
            legacy[property] = config[property]!.DeepClone();
        }

        // The reader locates string byte spans only; Newtonsoft above owns v10 validation.
        // No numeric/date/collection value is reserialized on the compatibility boundary.
        var tokens = new Utf8JsonReader(source.AsSpan(offset));
        var edits = new List<(int Start, int Length, byte[] Replacement)>();
        using var strings = config.Descendants().OfType<JValue>().Where(value => value.Type == JTokenType.String).GetEnumerator();
        while (tokens.Read())
        {
            if (tokens.TokenType == JsonTokenType.String)
            {
                if (!strings.MoveNext())
                {
                    throw new InvalidDataException("Inconsistent settings string tokens.");
                }
                var token = strings.Current;
                var value = tokens.GetString()!;
                var property = token.Path;
                var typedPath = PathProperties.Contains(property);
                var looksWindows = IsWindowsPath(value) ||
                    (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') ||
                    value.StartsWith('\\') ||
                    (typedPath && value.Length > 0 && !value.StartsWith('/'));
                if (!looksWindows)
                {
                    continue;
                }

                var normalized = value.Replace('/', '\\');
                var mapping = IsWindowsPath(value) && tokens.CurrentDepth == 1 && typedPath
                    ? orderedMappings.FirstOrDefault(item => normalized.Equals(item.WindowsRoot, StringComparison.OrdinalIgnoreCase) ||
                        normalized.StartsWith(item.WindowsRoot + "\\", StringComparison.OrdinalIgnoreCase))
                    : null;
                var suffix = mapping is null ? null : normalized[mapping.WindowsRoot.Length..].TrimStart('\\');
                if (mapping is null ||
                    (suffix is { Length: > 0 } && HasUnsupportedWindowsNameCharacters(suffix)) ||
                    normalized.Split('\\').Any(segment => segment is "." or ".."))
                {
                    unmapped.Add(new(property, value, null));
                    // Keep the full owning root property in the bag, not just a leaf value.
                    var owner = token.Ancestors().OfType<JProperty>().Last();
                    legacy[owner.Name] = owner.Value.DeepClone();
                    continue;
                }

                var replacement = suffix!.Length == 0 ? mapping.LinuxRoot : mapping.LinuxRoot.TrimEnd('/') + "/" + suffix.Replace('\\', '/');
                rewritten.Add(new(property, value, replacement));
                var encoded = Utf8.GetBytes(JsonConvert.SerializeObject(replacement));
                edits.Add((checked((int)tokens.TokenStartIndex + offset), checked((int)(tokens.BytesConsumed - tokens.TokenStartIndex)), encoded));
            }
        }

        using var payload = new MemoryStream();
        var position = 0;
        foreach (var edit in edits)
        {
            payload.Write(source.AsSpan(position, edit.Start - position));
            payload.Write(edit.Replacement);
            position = edit.Start + edit.Length;
        }
        payload.Write(source.AsSpan(position));
        var migrated = payload.ToArray();
        var settings = ReadV10(Utf8.GetString(migrated, offset, migrated.Length - offset));
        foreach (var property in legacy.Properties())
        {
            settings.Remove(property.Name);
        }
        var report = new SettingsMigrationReport(rewritten.AsReadOnly(), unmapped.AsReadOnly(), Array.AsReadOnly(unknown));
        // Base64 is deliberate: JSON embedding cannot preserve a BOM, trailing whitespace or
        // original numeric/escape spellings through arbitrary schema readers and writers.
        var envelope = new JObject
        {
            ["schemaVersion"] = 1,
            ["settings"] = settings,
            ["settingsUtf8"] = System.Convert.ToBase64String(migrated),
            ["legacy"] = legacy,
            ["report"] = JObject.FromObject(new
            {
                rewrittenPaths = rewritten.Select(item => new { property = item.Property, source = item.Source, destination = item.Destination }),
                unmappedPaths = unmapped.Select(item => new { property = item.Property, source = item.Source }),
                preservedUnknownProperties = unknown
            })
        };
        return (Utf8.GetBytes(envelope.ToString(Formatting.None)), report);
    }

    private static SettingsPathMapping ValidateMapping(SettingsPathMapping mapping)
    {
        var windows = mapping.WindowsRoot.Replace('/', '\\').TrimEnd('\\');
        var windowsWithoutRoot = windows.Length >= 2 && windows[1] == ':' ? windows[2..]
            : windows.StartsWith("\\\\", StringComparison.Ordinal) ? windows[2..] : windows;
        if (!IsWindowsPath(mapping.WindowsRoot.Replace('/', '\\')) ||
            windows.Split('\\').Any(segment => segment is "." or "..") ||
            HasUnsupportedWindowsNameCharacters(windowsWithoutRoot) ||
            (windows.StartsWith("\\\\", StringComparison.Ordinal) && windows[2..].Split('\\').Length < 2) ||
            !Path.IsPathFullyQualified(mapping.LinuxRoot) ||
            mapping.LinuxRoot.StartsWith("//", StringComparison.Ordinal) ||
            mapping.LinuxRoot.Contains('\\') || mapping.LinuxRoot.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Mappings require an absolute Windows root and a normalized absolute Linux root.");
        }
        return new(windows, mapping.LinuxRoot.TrimEnd('/') is "" ? "/" : mapping.LinuxRoot.TrimEnd('/'));
    }

    // These characters cannot occur in a Windows file name. A value containing one in the
    // mapped suffix is not a real v10 path component, so it is reported, not rewritten.
    private static bool HasUnsupportedWindowsNameCharacters(string value) =>
        value.Any(character => character < ' ' || character is '<' or '>' or ':' or '"' or '|' or '?' or '*');

    private static bool IsWindowsPath(string value) =>
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/') ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal);

    private static JObject ReadV10(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
        var config = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read())
        {
            throw new InvalidDataException("Additional content after settings object.");
        }
        return config;
    }

    private static void RejectSymbolicLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null)
            {
                throw new IOException("Settings migration paths must not traverse symbolic links.");
            }
        }
    }

    private static FileStream OpenStage(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        }
        catch (IOException) when (File.Exists(path))
        {
            using var abandoned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            File.Delete(path);
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Playnite.Core.Addons;

namespace Playnite.Core.Plugins;

/// <summary>
/// Thread-safe persistent JSON key/value <see cref="IPluginSettingsStore"/> isolated per plugin ID
/// under a verified non-symlink directory using atomic temp-file writes.
/// </summary>
public sealed class FilePluginSettingsStore : IPluginSettingsStore
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxKeysPerPlugin = 256;
    public const int MaxValueLength = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
    };

    private static long tempSequence;

    [ThreadStatic]
    private static HashSet<FilePluginSettingsStore>? activeErrorNotifiers;

    private readonly Lock gate = new();
    private readonly string rootDirectory;
    private readonly IRegularFileProbe? regularFileProbe;
    private readonly Action<string, string>? onError;
    private readonly Dictionary<string, Dictionary<string, string>> cache = new(StringComparer.Ordinal);

    /// <summary>Creates a file-backed plugin settings store under <paramref name="rootDirectory"/>.</summary>
    public FilePluginSettingsStore(
        string rootDirectory,
        Action<string, string>? onError = null,
        IRegularFileProbe? regularFileProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (rootDirectory.Contains('\0') || !Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("Plugin settings root must be a non-null, fully qualified path.", nameof(rootDirectory));
        }

        this.rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        this.onError = onError;
        this.regularFileProbe = regularFileProbe;
    }

    /// <summary>Gets the resolved root directory where plugin setting JSON files are stored.</summary>
    public string RootDirectory => rootDirectory;

    /// <inheritdoc/>
    public string? Get(string pluginId, string key)
    {
        ValidatePluginAndKey(pluginId, key);
        string? pendingError;
        string? result;
        lock (gate)
        {
            var map = TryGetOrLoadLocked(pluginId, out _, out pendingError);
            result = map.TryGetValue(key, out var value) ? value : null;
        }

        NotifyError(pluginId, pendingError);
        return result;
    }

    /// <inheritdoc/>
    public void Set(string pluginId, string key, string? value)
    {
        ValidatePluginAndKey(pluginId, key);
        ValidateValueLength(value);
        SetMany(pluginId, new Dictionary<string, string?>(1, StringComparer.Ordinal) { [key] = value });
    }

    /// <inheritdoc/>
    public void SetMany(string pluginId, IReadOnlyDictionary<string, string?> values)
    {
        ValidatePluginId(pluginId);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            ValidatePluginAndKey(pluginId, key);
            ValidateValueLength(value);
        }

        string? pendingError = null;
        try
        {
            lock (gate)
            {
                var current = TryGetOrLoadLocked(pluginId, out var loadFailed, out pendingError);
                if (loadFailed)
                {
                    throw new IOException(pendingError ?? $"Existing settings for '{pluginId}' could not be read safely.");
                }

                var updated = new Dictionary<string, string>(current, StringComparer.Ordinal);
                var mutated = false;
                foreach (var (key, value) in values)
                {
                    if (value is null)
                    {
                        if (updated.Remove(key))
                        {
                            mutated = true;
                        }
                    }
                    else
                    {
                        if (!updated.ContainsKey(key) && updated.Count >= MaxKeysPerPlugin)
                        {
                            throw new InvalidOperationException(
                                $"Plugin '{pluginId}' exceeded the {MaxKeysPerPlugin}-key settings limit.");
                        }

                        if (!updated.TryGetValue(key, out var existingValue)
                            || !string.Equals(existingValue, value, StringComparison.Ordinal))
                        {
                            updated[key] = value;
                            mutated = true;
                        }
                    }
                }

                if (!mutated)
                {
                    return;
                }

                WritePluginMapLocked(pluginId, updated);
                cache[pluginId] = updated;
            }
        }
        finally
        {
            NotifyError(pluginId, pendingError);
        }
    }

    private static void ValidateValueLength(string? value)
    {
        if (value is not null && value.Length > MaxValueLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Setting value length ({value.Length}) exceeds the {MaxValueLength}-character limit.");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GetAll(string pluginId)
    {
        ValidatePluginId(pluginId);
        string? pendingError;
        Dictionary<string, string> snapshot;
        lock (gate)
        {
            var map = TryGetOrLoadLocked(pluginId, out _, out pendingError);
            snapshot = new Dictionary<string, string>(map, StringComparer.Ordinal);
        }

        NotifyError(pluginId, pendingError);
        return snapshot;
    }

    private void NotifyError(string pluginId, string? errorMessage)
    {
        if (errorMessage is null || onError is null)
        {
            return;
        }

        activeErrorNotifiers ??= new HashSet<FilePluginSettingsStore>(ReferenceEqualityComparer.Instance);
        if (!activeErrorNotifiers.Add(this))
        {
            return;
        }

        try
        {
            onError(pluginId, errorMessage);
        }
        finally
        {
            activeErrorNotifiers.Remove(this);
        }
    }

    private Dictionary<string, string> TryGetOrLoadLocked(
        string pluginId,
        out bool loadFailed,
        out string? errorMessage)
    {
        if (cache.TryGetValue(pluginId, out var existing))
        {
            loadFailed = false;
            errorMessage = null;
            return existing;
        }

        var loaded = LoadPluginMapFromDisk(pluginId, out loadFailed, out errorMessage);
        if (!loadFailed)
        {
            cache[pluginId] = loaded;
        }

        return loaded;
    }

    private Dictionary<string, string> LoadPluginMapFromDisk(
        string pluginId,
        out bool loadFailed,
        out string? errorMessage)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        loadFailed = false;
        errorMessage = null;

        if (IsRootOrParentSymlink(out var rootSymlinkPath))
        {
            loadFailed = true;
            errorMessage = $"Refusing symlinked plugin settings directory '{rootSymlinkPath}'.";
            return result;
        }

        if (!Directory.Exists(rootDirectory))
        {
            return result;
        }

        var targetPath = ResolvePluginPath(pluginId);
        if (IsPathSymlinkOrReparsePoint(targetPath))
        {
            loadFailed = true;
            errorMessage = $"Refusing symlinked plugin settings file '{targetPath}'.";
            return result;
        }

        if (!File.Exists(targetPath))
        {
            return result;
        }

        if (regularFileProbe is not null && !regularFileProbe.IsRegularFile(targetPath))
        {
            loadFailed = true;
            errorMessage = $"Refusing non-regular plugin settings file '{targetPath}'.";
            return result;
        }

        try
        {
            var bytes = ReadBoundedBytes(targetPath, MaxFileBytes, out var truncated);
            if (truncated)
            {
                loadFailed = true;
                errorMessage = $"Plugin settings file '{targetPath}' exceeds the {MaxFileBytes}-byte limit.";
                return result;
            }

            if (HasDuplicateOrInvalidObjectKeys(bytes, out var duplicateReason))
            {
                loadFailed = true;
                errorMessage = $"Plugin settings file '{targetPath}' is invalid JSON ({duplicateReason}).";
                return result;
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                loadFailed = true;
                errorMessage = $"Plugin settings file '{targetPath}' must contain a JSON object.";
                return result;
            }

            var partialIssues = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name)
                    || property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not string parsed
                    || parsed.Length > MaxValueLength
                    || (!result.ContainsKey(property.Name) && result.Count >= MaxKeysPerPlugin))
                {
                    partialIssues = true;
                    continue;
                }

                result[property.Name] = parsed;
            }

            if (partialIssues)
            {
                errorMessage = $"Ignored malformed or out-of-bounds entries in '{targetPath}'.";
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            loadFailed = true;
            errorMessage = $"Could not read plugin settings '{targetPath}': {exception.Message}";
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void WritePluginMapLocked(string pluginId, Dictionary<string, string> map)
    {
        if (IsRootOrParentSymlink(out var rootSymlinkPath))
        {
            throw new IOException($"Refusing symlinked plugin settings directory '{rootSymlinkPath}'.");
        }

        Directory.CreateDirectory(rootDirectory);
        var targetPath = ResolvePluginPath(pluginId);
        if (IsPathSymlinkOrReparsePoint(targetPath))
        {
            throw new IOException($"Refusing symlinked plugin settings file '{targetPath}'.");
        }

        if (File.Exists(targetPath) && regularFileProbe is not null && !regularFileProbe.IsRegularFile(targetPath))
        {
            throw new IOException($"Refusing non-regular plugin settings file '{targetPath}'.");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var pair in map.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
        }

        if (buffer.Length > MaxFileBytes)
        {
            throw new InvalidOperationException(
                $"Serialized plugin settings for '{pluginId}' ({buffer.Length} bytes) exceed the {MaxFileBytes}-byte limit.");
        }

        var safePrefix = pluginId.Length <= 80
            ? pluginId
            : $"{pluginId[..48]}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pluginId)))[..16]}";
        var tempName = $".{safePrefix}.{Environment.ProcessId}.{Interlocked.Increment(ref tempSequence)}.tmp";
        var tempPath = Path.Combine(rootDirectory, tempName);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                buffer.Position = 0;
                buffer.CopyTo(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private string ResolvePluginPath(string pluginId)
    {
        var full = Path.GetFullPath(Path.Combine(rootDirectory, $"{pluginId}.json"));
        if (!AddonInstallLayout.IsWithin(rootDirectory, full))
        {
            throw new ArgumentException($"Plugin identifier '{pluginId}' escapes settings directory.", nameof(pluginId));
        }

        return full;
    }

    private bool IsRootOrParentSymlink(out string offendingPath)
    {
        var parent = Path.GetDirectoryName(rootDirectory);
        if (!string.IsNullOrWhiteSpace(parent) && IsPathSymlinkOrReparsePoint(parent))
        {
            offendingPath = parent;
            return true;
        }

        if (IsPathSymlinkOrReparsePoint(rootDirectory))
        {
            offendingPath = rootDirectory;
            return true;
        }

        offendingPath = string.Empty;
        return false;
    }

    private static bool IsPathSymlinkOrReparsePoint(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget is not null || (fileInfo.Exists && (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            return true;
        }

        var dirInfo = new DirectoryInfo(path);
        return dirInfo.LinkTarget is not null || (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static byte[] ReadBoundedBytes(string path, int maxBytes, out bool truncated)
    {
        truncated = false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ms = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = stream.Read(chunk, 0, Math.Min(chunk.Length, (maxBytes + 1) - total));
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                truncated = true;
                return [];
            }

            ms.Write(chunk, 0, read);
        }

        var raw = ms.ToArray();
        return raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
            ? raw[3..]
            : raw;
    }

    private static bool HasDuplicateOrInvalidObjectKeys(ReadOnlySpan<byte> utf8Bytes, out string reason)
    {
        reason = string.Empty;
        var seenStack = new Stack<HashSet<string>>();
        var reader = new Utf8JsonReader(utf8Bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        seenStack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (seenStack.Count > 0)
                        {
                            seenStack.Pop();
                        }
                        break;
                    case JsonTokenType.PropertyName:
                        var propertyName = reader.GetString() ?? string.Empty;
                        if (seenStack.Count > 0 && !seenStack.Peek().Add(propertyName))
                        {
                            reason = $"duplicate property '{propertyName}'";
                            return true;
                        }
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            reason = exception.Message;
            return true;
        }

        return false;
    }

    private static void ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (!AddonPathSafety.IsSafeSegment(pluginId))
        {
            throw new ArgumentException($"Plugin identifier '{pluginId}' must be one safe filename segment.", nameof(pluginId));
        }
    }

    private static void ValidatePluginAndKey(string pluginId, string key)
    {
        ValidatePluginId(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete temporary plugin settings file '{tempPath}': {exception.Message}");
        }
    }
}

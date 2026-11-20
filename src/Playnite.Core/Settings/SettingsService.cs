using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playnite.Settings;

/// <summary>Why a settings operation failed. The category is stable; the message is diagnostic.</summary>
public enum SettingsFailure
{
    /// <summary>The settings file or its directory could not be read.</summary>
    Unreadable,

    /// <summary>The file is not a valid, internally consistent settings envelope.</summary>
    Corrupt,

    /// <summary>The file declares a settings schema this version does not support.</summary>
    UnsupportedSchema,

    /// <summary>The file is a raw v10 config; migration must be requested explicitly.</summary>
    LegacyShape,

    /// <summary>A typed value is absent, unknown or has the wrong JSON type.</summary>
    InvalidValue,

    /// <summary>The settings store already exists; migration never replaces it.</summary>
    AlreadyExists,

    /// <summary>The settings file or its config directory could not be written.</summary>
    WriteUnavailable,
}

/// <summary>A settings failure carrying the stable <see cref="Failure"/> category.</summary>
public sealed class SettingsException : Exception
{
    public SettingsException(SettingsFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Gets the failure category, independent of the message.</summary>
    public SettingsFailure Failure { get; }
}

/// <summary>One typed change. Unknown v10 names are rejected so they stay in the legacy bag.</summary>
public sealed record SettingsUpdate(SettingsKey Key, object? Value)
{
    /// <summary>Creates a typed update for a key.</summary>
    public static SettingsUpdate Set<T>(SettingsKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new SettingsUpdate(key, value);
    }
}

/// <summary>Raised after a successful write. Subscribers run on the writing thread.</summary>
public sealed class SettingsChangedEventArgs(SettingsSnapshot snapshot, IReadOnlyList<string> changedProperties)
    : EventArgs
{
    /// <summary>Gets the complete snapshot that is now on disk.</summary>
    public SettingsSnapshot Snapshot { get; } = snapshot;

    /// <summary>Gets the root property names this write changed, in request order.</summary>
    public IReadOnlyList<string> ChangedProperties { get; } = changedProperties;
}

/// <summary>
/// An immutable view of one settings document. Values are resolved from <c>settings</c> first
/// and then <c>legacy</c>; the raw objects preserve every root property, including properties
/// this version does not model.
/// </summary>
public sealed class SettingsSnapshot
{
    private readonly JObject envelope;
    private readonly JObject settings;
    private readonly JObject legacy;
    private readonly JObject report;
    private readonly byte[]? sourceBytes;

    private SettingsSnapshot(bool exists, JObject envelope, JObject settings, JObject legacy, JObject report,
        byte[]? sourceBytes)
    {
        Exists = exists;
        this.envelope = envelope;
        this.settings = settings;
        this.legacy = legacy;
        this.report = report;
        this.sourceBytes = sourceBytes;
    }

    /// <summary>Gets whether a store file was present when this snapshot was read.</summary>
    public bool Exists { get; }

    /// <summary>Gets the migration-envelope schema version.</summary>
    public int SchemaVersion => 1;

    /// <summary>Gets a copy of the normalized known-settings object.</summary>
    public JObject Settings => (JObject)settings.DeepClone();

    /// <summary>Gets a copy of the preserved unknown and unmapped legacy object.</summary>
    public JObject Legacy => (JObject)legacy.DeepClone();

    /// <summary>Gets a copy of the preserved migration report.</summary>
    public JObject Report => (JObject)report.DeepClone();

    /// <summary>Returns a typed value, or the key's documented default when it is absent.</summary>
    public T Get<T>(SettingsKey<T> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var token = settings.Property(key.Name, StringComparison.Ordinal)?.Value
            ?? legacy.Property(key.Name, StringComparison.Ordinal)?.Value;
        if (token is null)
        {
            return key.DefaultValue;
        }

        key.ValidateToken(token);
        return (T)key.FromToken(token)!;
    }

    internal static SettingsSnapshot Missing()
    {
        var empty = new JObject();
        return new SettingsSnapshot(false, new JObject(), empty, (JObject)empty.DeepClone(),
            PlayniteSettingsService.EmptyReport(), null);
    }

    internal static SettingsSnapshot FromEnvelope(JObject envelope, JObject settings, JObject legacy, JObject report,
        byte[] sourceBytes)
        => new(true, envelope, settings, legacy, report, sourceBytes);

    internal JObject Envelope => envelope;

    internal JObject RawSettings => settings;

    internal JObject RawLegacy => legacy;

    internal JObject RawReport => report;

    internal byte[]? SourceBytes => sourceBytes;
}

/// <summary>
/// UI-free settings service over the p4-004 migration envelope. A read validates the envelope
/// and every modeled value; an update rewrites the envelope atomically from a fresh read, so a
/// corrupt or concurrently changed file is never overwritten silently. Unknown properties stay
/// in <c>legacy</c>, and the compatibility <c>settingsUtf8</c> bytes are regenerated so
/// <see cref="SettingsMigration.ExportV10"/> remains the authoritative round-trip check.
/// </summary>
public sealed class PlayniteSettingsService : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Utf8Bom = [0xef, 0xbb, 0xbf];
    private readonly ISettingsStorage storage;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    /// <summary>Creates a service over a settings file path.</summary>
    public PlayniteSettingsService(string path)
        : this(new SettingsFileStorage(path))
    {
    }

    /// <summary>Creates a service over a storage implementation.</summary>
    public PlayniteSettingsService(ISettingsStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        this.storage = storage;
    }

    /// <summary>Gets the storage location named by the service.</summary>
    public string Location => storage.Location;

    /// <summary>Gets the most recent snapshot, or <c>null</c> before a load or update.</summary>
    public SettingsSnapshot? Current { get; private set; }

    /// <summary>Raised after a successful write completes.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>
    /// Reads and validates the envelope. A missing file returns the documented defaults without
    /// creating anything. A raw v10 config is refused as <see cref="SettingsFailure.LegacyShape"/>.
    /// </summary>
    public async Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = Deserialize(bytes, Location);
            Current = snapshot;
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies typed changes from a fresh read and writes the complete envelope atomically.
    /// A no-op or an empty request writes nothing and raises no event.
    /// </summary>
    public async Task<SettingsSnapshot> UpdateAsync(IEnumerable<SettingsUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(updates);
        var requested = updates.ToArray();
        foreach (var update in requested)
        {
            ArgumentNullException.ThrowIfNull(update);
            ArgumentNullException.ThrowIfNull(update.Key);
            _ = update.Key.ToToken(update.Value);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SettingsChangedEventArgs? notification = null;
        SettingsSnapshot result;
        try
        {
            var bytes = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
            var current = Deserialize(bytes, Location);
            var (replacement, changed) = BuildReplacement(current, requested);
            if (replacement is null)
            {
                Current = current;
                result = current;
            }
            else
            {
                await storage.WriteAtomicAsync(replacement, cancellationToken).ConfigureAwait(false);
                var next = Deserialize(replacement, Location);
                Current = next;
                result = next;
                notification = new SettingsChangedEventArgs(next, changed);
            }
        }
        finally
        {
            gate.Release();
        }

        // Subscribers run after the gate is released so a handler may read the service again.
        if (notification is not null)
        {
            Changed?.Invoke(this, notification);
        }

        return result;
    }

    /// <summary>
    /// Explicitly migrates a raw v10 config through <see cref="SettingsMigration"/> and writes
    /// the result as the native store. An existing store is never replaced.
    /// </summary>
    public async Task<SettingsSnapshot> MigrateFromV10Async(string sourceFile,
        IReadOnlyList<SettingsPathMapping> mappings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentNullException.ThrowIfNull(mappings);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(Path.GetTempPath(), "playnite-settings-" + Path.GetRandomFileName());
        SettingsChangedEventArgs? notification = null;
        SettingsSnapshot result;
        try
        {
            if (await storage.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new SettingsException(SettingsFailure.AlreadyExists,
                    "The settings store already exists; migration never overwrites it.");
            }

            Directory.CreateDirectory(staging);
            var staged = Path.Combine(staging, "settings.json");
            await SettingsMigration.MigrateAsync(Path.GetFullPath(sourceFile), staged, mappings,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(staged, cancellationToken).ConfigureAwait(false);
            var snapshot = Deserialize(bytes, Location);
            await storage.WriteAtomicAsync(bytes, cancellationToken).ConfigureAwait(false);
            Current = snapshot;
            result = snapshot;
            notification = new SettingsChangedEventArgs(snapshot, []);
        }
        catch (SettingsException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or Newtonsoft.Json.JsonException)
        {
            throw new SettingsException(SettingsFailure.Corrupt,
                $"The v10 settings file could not be migrated: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            throw new SettingsException(SettingsFailure.Unreadable,
                $"The v10 settings file could not be migrated: {exception.Message}", exception);
        }
        catch (ArgumentException exception)
        {
            throw new SettingsException(SettingsFailure.InvalidValue, exception.Message, exception);
        }
        finally
        {
            DeleteDirectory(staging);
            gate.Release();
        }

        // Subscribers run after the gate is released so a handler may read the service again.
        if (notification is not null)
        {
            Changed?.Invoke(this, notification);
        }

        return result;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }

    internal static JObject EmptyReport() => new()
    {
        ["rewrittenPaths"] = new JArray(),
        ["unmappedPaths"] = new JArray(),
        ["preservedUnknownProperties"] = new JArray(),
    };

    private static SettingsSnapshot Deserialize(byte[]? bytes, string location)
    {
        if (bytes is null)
        {
            return SettingsSnapshot.Missing();
        }

        JObject envelope;
        try
        {
            var text = Utf8.GetString(bytes).TrimStart('\uFEFF');
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            envelope = JObject.Load(reader,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read())
            {
                throw new JsonReaderException("Additional content after the settings object.");
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new SettingsException(SettingsFailure.Corrupt,
                $"The settings file at '{location}' is not valid JSON: {exception.Message}", exception);
        }

        if (envelope.Property("schemaVersion", StringComparison.Ordinal) is not { } version)
        {
            if (envelope.Property("Version", StringComparison.Ordinal) is not null)
            {
                throw new SettingsException(SettingsFailure.LegacyShape,
                    "The settings file is a raw v10 config. Run SettingsMigration explicitly before loading a native store.");
            }

            throw new SettingsException(SettingsFailure.Corrupt,
                "The settings file has no schemaVersion and is not a v10 config.");
        }

        if (version.Value.Type != JTokenType.Integer)
        {
            throw new SettingsException(SettingsFailure.Corrupt,
                "The settings schemaVersion is not an integer.");
        }

        if (!SettingsKey.TryGetInteger(version.Value, out var schemaVersion) || schemaVersion != 1)
        {
            throw new SettingsException(SettingsFailure.UnsupportedSchema,
                $"Unsupported settings schema version {version.Value}.");
        }

        if (envelope.Property("settings", StringComparison.Ordinal)?.Value is not JObject settings)
        {
            throw new SettingsException(SettingsFailure.Corrupt, "The settings envelope has no settings object.");
        }

        if (envelope.Property("legacy", StringComparison.Ordinal)?.Value is not JObject legacy)
        {
            throw new SettingsException(SettingsFailure.Corrupt, "The settings envelope has no legacy object.");
        }

        if (envelope.Property("settingsUtf8", StringComparison.Ordinal)?.Value is not { Type: JTokenType.String })
        {
            throw new SettingsException(SettingsFailure.Corrupt, "The settings envelope has no settingsUtf8 string.");
        }

        if (envelope.Property("report", StringComparison.Ordinal)?.Value is not JObject report)
        {
            throw new SettingsException(SettingsFailure.Corrupt, "The settings envelope has no report object.");
        }

        EnsureEnvelopeRoundTrips(bytes);

        foreach (var key in PlayniteSettingsKeys.All)
        {
            var token = settings.Property(key.Name, StringComparison.Ordinal)?.Value
                ?? legacy.Property(key.Name, StringComparison.Ordinal)?.Value;
            if (token is not null)
            {
                key.ValidateToken(token);
            }
        }

        return SettingsSnapshot.FromEnvelope(envelope, settings, legacy, report, bytes);
    }

    private static (byte[]? Bytes, IReadOnlyList<string> Changed) BuildReplacement(
        SettingsSnapshot snapshot, IReadOnlyList<SettingsUpdate> updates)
    {
        var archivedBytes = snapshot.SourceBytes is null
            ? null
            : SettingsMigration.ExportV10(WithoutBom(snapshot.SourceBytes));
        var archived = archivedBytes is null ? new JObject() : ReadArchived(archivedBytes);
        var legacy = (JObject)snapshot.RawLegacy.DeepClone();
        var changed = new List<string>();
        foreach (var update in updates)
        {
            var name = update.Key.Name;
            if (!V10SettingsProperties.Names.Contains(name))
            {
                throw new SettingsException(SettingsFailure.InvalidValue,
                    $"'{name}' is not a known v10 settings property; unknown properties stay in legacy.");
            }

            var token = update.Key.ToToken(update.Value);
            var existing = archived.Property(name, StringComparison.Ordinal)?.Value;
            if (existing is not null && JToken.DeepEquals(existing, token))
            {
                continue;
            }

            archived[name] = token;
            legacy.Remove(name);
            changed.Add(name);
        }

        if (changed.Count == 0)
        {
            return (null, []);
        }

        var newArchived = Utf8.GetBytes(archived.ToString(Formatting.None));
        var settings = (JObject)archived.DeepClone();
        foreach (var property in legacy.Properties())
        {
            settings.Remove(property.Name);
        }

        var envelope = (JObject)snapshot.Envelope.DeepClone();
        envelope["schemaVersion"] = 1;
        envelope["settings"] = settings;
        envelope["settingsUtf8"] = Convert.ToBase64String(newArchived);
        envelope["legacy"] = legacy;
        envelope["report"] = snapshot.RawReport.DeepClone();
        var bytes = Utf8.GetBytes(envelope.ToString(Formatting.None));
        EnsureEnvelopeRoundTrips(bytes);
        return (bytes, changed.AsReadOnly());
    }

    private static void EnsureEnvelopeRoundTrips(byte[] bytes)
    {
        try
        {
            _ = SettingsMigration.ExportV10(WithoutBom(bytes));
        }
        catch (Exception exception) when (exception is InvalidDataException
            or Newtonsoft.Json.JsonException
            or System.Text.Json.JsonException
            or FormatException
            or ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            throw new SettingsException(SettingsFailure.Corrupt,
                $"The settings envelope is inconsistent: {exception.Message}", exception);
        }
    }

    private static byte[] WithoutBom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Utf8Bom) ? bytes[3..] : bytes;

    private static JObject ReadArchived(byte[] bytes)
    {
        var text = Utf8.GetString(bytes).TrimStart('\uFEFF');
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
        var archived = JObject.Load(reader,
            new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read())
        {
            throw new SettingsException(SettingsFailure.Corrupt,
                "The archived v10 settings contain additional content.");
        }

        return archived;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception.HResult;
        }
    }
}

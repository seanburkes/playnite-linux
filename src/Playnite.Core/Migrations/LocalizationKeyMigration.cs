using System.Collections.ObjectModel;
using System.Text.Json;

namespace Playnite;

/// <summary>Explicit ordinal v10 resource-key migration; text is never normalized or resolved.</summary>
public sealed class LocalizationKeyMigration
{
    public static LocalizationKeyMigration English { get; } = LoadEnglish();

    public IReadOnlyDictionary<string, string?> Mapping { get; }

    public LocalizationKeyMigration(IEnumerable<KeyValuePair<string, string?>> mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var entries = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (source, target) in mapping)
        {
            ArgumentException.ThrowIfNullOrEmpty(source);
            if (target is not null)
            {
                ArgumentException.ThrowIfNullOrEmpty(target);
                if (!targets.Add(target))
                {
                    throw new ArgumentException($"Multiple keys map to {target}.", nameof(mapping));
                }
            }

            entries.Add(source, target);
        }

        // A destination must be a fixed point, never another rename or an obsolete key.
        foreach (var target in targets)
        {
            if (entries.TryGetValue(target, out var next) && next != target)
            {
                throw new ArgumentException($"Mapping is not idempotent at {target}.", nameof(mapping));
            }
        }

        Mapping = new ReadOnlyDictionary<string, string?>(entries);
    }

    /// <summary>Unknown and obsolete entries retain their text in separate diagnostic sets.</summary>
    public LocalizationMigrationResult Apply(LocalizationKeySet source, LocalizationKeySet target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var migrated = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unmapped = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var obsolete = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var destinations = Mapping.Values.Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in source.Entries)
        {
            if (!Mapping.TryGetValue(key, out var destination))
            {
                // An already migrated name is a fixed point, not an unknown input.
                destination = destinations.Contains(key) ? key : null;
                if (destination is null)
                {
                    unmapped.Add(key, value);
                    continue;
                }
            }

            if (destination is null)
            {
                obsolete.Add(key, value);
            }
            else if (!target.Entries.ContainsKey(destination))
            {
                unmapped.Add(key, value);
            }
            else
            {
                // Add, not assignment: conflicting old/new input keys must not lose text.
                migrated.Add(destination, value);
            }
        }

        return new LocalizationMigrationResult(
            new LocalizationKeySet(migrated), new LocalizationKeySet(unmapped), new LocalizationKeySet(obsolete),
            Array.AsReadOnly(target.Entries.Keys.Except(migrated.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(Mapping.Where(pair => pair.Value is not null && !target.Entries.ContainsKey(pair.Value))
                .Select(pair => pair.Key).ToArray()));
    }

    private static LocalizationKeyMigration LoadEnglish()
    {
        using var stream = typeof(LocalizationKeyMigration).Assembly.GetManifestResourceStream("Playnite.Migrations.Localization.v10.json")
            ?? throw new InvalidOperationException("English localisation migration is missing.");
        using var document = JsonDocument.Parse(stream);
        return new LocalizationKeyMigration(document.RootElement.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("mapping").EnumerateObject())
            .Select(entry => KeyValuePair.Create(entry.Name, entry.Value.GetString())));
    }
}

public sealed record LocalizationMigrationResult(
    LocalizationKeySet Migrated,
    LocalizationKeySet Unmapped,
    LocalizationKeySet Obsolete,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> OrphanMappings)
{
    public int MappedCount => Migrated.Entries.Count;
    public bool IsComplete => Unmapped.Entries.Count == 0 && Obsolete.Entries.Count == 0
        && MissingKeys.Count == 0 && OrphanMappings.Count == 0;
}

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Playnite;

/// <summary>Ordinal resource keys and their source text, independent of a UI resource loader.</summary>
public sealed class LocalizationKeySet
{
    public IReadOnlyDictionary<string, string> Entries { get; }

    public static LocalizationKeySet English { get; } = LoadEnglish();

    public LocalizationKeySet(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(value);
            values.Add(key, value);
        }

        Entries = new ReadOnlyDictionary<string, string>(values);
    }

    /// <summary>Extracts keyed, nonempty string declarations without executing XAML.</summary>
    public static LocalizationKeySet Extract(TextReader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace system = "clr-namespace:System;assembly=mscorlib";
        if (document.Root?.Name != presentation + "ResourceDictionary")
        {
            throw new FormatException("Expected a localisation ResourceDictionary.");
        }

        return new LocalizationKeySet(document.Root.Elements(system + "String")
            .Where(element => !string.IsNullOrEmpty((string?)element.Attribute(xaml + "Key"))
                && !string.IsNullOrEmpty(element.Value))
            .Select(element => KeyValuePair.Create((string)element.Attribute(xaml + "Key")!, element.Value)));
    }

    /// <summary>The v10 generator removes only the leading, case-sensitive LOC prefix.</summary>
    public static string GetConstantName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith("LOC", StringComparison.Ordinal) ? key[3..] : key;
    }

    /// <summary>Empty translations fall back to English; unknown keys retain v10's marker.</summary>
    public string Resolve(string key, LocalizationKeySet? translation = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (translation is not null && translation.Entries.TryGetValue(key, out var translated)
            && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }

        return Entries.TryGetValue(key, out var value) ? value : $"<!{key}!>";
    }

    /// <summary>Compares this (before) set with the supplied after set, in ordinal key order.</summary>
    public LocalizationKeyDiff Diff(LocalizationKeySet after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return new LocalizationKeyDiff(
            Array.AsReadOnly(after.Entries.Keys.Except(Entries.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(Entries.Keys.Except(after.Entries.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(Entries.Keys.Intersect(after.Entries.Keys, StringComparer.Ordinal)
                .Where(key => !string.Equals(Entries[key], after.Entries[key], StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray()));
    }

    private static LocalizationKeySet LoadEnglish()
    {
        using var stream = typeof(LocalizationKeySet).Assembly.GetManifestResourceStream("Playnite.Localization.English.json")
            ?? throw new InvalidOperationException("English localisation catalog is missing.");
        return new LocalizationKeySet(JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("English localisation catalog is empty."));
    }
}

public sealed record LocalizationKeyDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

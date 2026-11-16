using System.Collections.ObjectModel;
using System.Xml;
using System.Xml.Linq;

namespace Playnite;

/// <summary>A localized string whose format placeholders differ from the English source.</summary>
public sealed record LocalizationPlaceholderMismatch(
    string Key,
    IReadOnlyList<string> EnglishPlaceholders,
    IReadOnlyList<string> TranslatedPlaceholders);

/// <summary>Per-language coverage of the English resource keys.</summary>
public sealed record LocalizationLanguageCoverage(
    string Language,
    string File,
    int Present,
    int Missing,
    int Extra,
    int Untranslated,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> ExtraKeys,
    IReadOnlyList<LocalizationPlaceholderMismatch> PlaceholderMismatches);

/// <summary>
/// Compares frozen legacy language declarations with the English source without executing XAML.
/// Empty translations are untranslated rather than absent, matching v10's English fallback.
/// </summary>
public static class LocalizationCoverage
{
    /// <summary>Extracts every keyed string declaration, including an empty value.</summary>
    public static IReadOnlyDictionary<string, string> ExtractDeclarations(TextReader source)
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

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in document.Root.Elements(system + "String"))
        {
            var key = (string?)element.Attribute(xaml + "Key");
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!values.TryAdd(key, element.Value))
            {
                throw new FormatException($"Duplicate localisation key: {key}");
            }
        }

        return new ReadOnlyDictionary<string, string>(values);
    }

    /// <summary>Reports missing, extra and untranslated keys plus format-placeholder defects.</summary>
    public static LocalizationLanguageCoverage Analyze(
        string language,
        string file,
        LocalizationKeySet english,
        IReadOnlyDictionary<string, string> translated)
    {
        ArgumentException.ThrowIfNullOrEmpty(language);
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(translated);

        var missingKeys = new List<string>();
        var untranslated = 0;
        var present = 0;
        var mismatches = new List<LocalizationPlaceholderMismatch>();
        foreach (var (key, englishText) in english.Entries)
        {
            if (!translated.TryGetValue(key, out var translatedText))
            {
                missingKeys.Add(key);
                continue;
            }

            if (translatedText.Length == 0)
            {
                untranslated++;
                continue;
            }

            present++;
            var englishPlaceholders = ExtractPlaceholders(englishText);
            var translatedPlaceholders = ExtractPlaceholders(translatedText);
            if (!englishPlaceholders.SequenceEqual(translatedPlaceholders, StringComparer.Ordinal))
            {
                mismatches.Add(new LocalizationPlaceholderMismatch(
                    key,
                    englishPlaceholders,
                    translatedPlaceholders));
            }
        }

        var extraKeys = translated.Keys
            .Where(key => !english.Entries.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        mismatches.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return new LocalizationLanguageCoverage(
            language,
            file,
            present,
            missingKeys.Count,
            extraKeys.Length,
            untranslated,
            missingKeys.AsReadOnly(),
            Array.AsReadOnly(extraKeys),
            mismatches.AsReadOnly());
    }

    /// <summary>
    /// Extracts unescaped .NET composite-format placeholders, ordinal sorted, so a changed
    /// index or format is a mismatch. Doubled braces are literals and are not placeholders.
    /// </summary>
    public static IReadOnlyList<string> ExtractPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var placeholders = new List<string>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                if (index + 1 < text.Length && text[index + 1] == '{')
                {
                    index++;
                    continue;
                }

                var close = text.IndexOf('}', index + 1);
                if (close > index + 1 && IsPlaceholderBody(text.AsSpan(index + 1, close - index - 1)))
                {
                    placeholders.Add(text[index..(close + 1)]);
                    index = close;
                }
            }
            else if (text[index] == '}' && index + 1 < text.Length && text[index + 1] == '}')
            {
                index++;
            }
        }

        placeholders.Sort(StringComparer.Ordinal);
        return placeholders.AsReadOnly();
    }

    private static bool IsPlaceholderBody(ReadOnlySpan<char> body)
    {
        var position = 0;
        if (position >= body.Length || !char.IsAsciiDigit(body[position]))
        {
            return false;
        }

        while (position < body.Length && char.IsAsciiDigit(body[position]))
        {
            position++;
        }

        if (position < body.Length && body[position] == ',')
        {
            position++;
            if (position < body.Length && body[position] == '-')
            {
                position++;
            }

            var alignmentStart = position;
            while (position < body.Length && char.IsAsciiDigit(body[position]))
            {
                position++;
            }

            if (position == alignmentStart)
            {
                return false;
            }
        }

        if (position < body.Length && body[position] == ':')
        {
            return body[(position + 1)..].IndexOfAny('{', '}') < 0;
        }

        return position == body.Length;
    }
}

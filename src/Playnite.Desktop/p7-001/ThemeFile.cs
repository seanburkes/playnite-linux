using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Playnite.Core;

namespace Playnite.Desktop.Bootstrap;

/// <summary>
/// One scalar override from a functional theme file. v1 supports a deliberately small,
/// Avalonia-shaped subset: a Window style with FontSize setters. Unknown selectors or
/// properties are rejected rather than guessed at.
/// </summary>
public sealed record ThemeOverride(double FontSize);

/// <summary>Reads the constrained on-disk theme format without executing XAML.</summary>
public static class ThemeFile
{
    private const int MaxDocumentBytes = 64 * 1024;
    private const double MinFontSize = 1;
    private const double MaxFontSize = 384;

    public static ThemeOverride Read(string path, IRegularFileProbe probe)
    {
        var fullPath = BootstrapFiles.ExistingRegular(path, probe)
            ?? throw new FileNotFoundException($"Theme {path} is not a regular file.", path);
        var file = new FileInfo(fullPath);
        if (file.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException($"Theme {fullPath} exceeds {MaxDocumentBytes} bytes.");
        }

        XNamespace avalonia = "https://github.com/avaloniaui";
        using var stream = File.OpenRead(fullPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name != avalonia + "Styles")
        {
            throw new FormatException("Expected an Avalonia Styles root element.");
        }

        var values = new List<double>();
        foreach (var style in document.Root.Elements(avalonia + "Style"))
        {
            var selector = (string?)style.Attribute("Selector");
            if (!string.Equals(selector, "Window", StringComparison.Ordinal))
            {
                throw new FormatException($"Unsupported theme selector '{selector ?? "<missing>"}'.");
            }

            foreach (var setter in style.Elements(avalonia + "Setter"))
            {
                var property = (string?)setter.Attribute("Property");
                if (!string.Equals(property, "FontSize", StringComparison.Ordinal))
                {
                    throw new FormatException($"Unsupported theme property '{property ?? "<missing>"}'.");
                }

                var raw = (string?)setter.Attribute("Value");
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
                    || double.IsFinite(fontSize) is false
                    || fontSize < MinFontSize || fontSize > MaxFontSize)
                {
                    throw new FormatException($"Invalid FontSize value '{raw ?? "<missing>"}'.");
                }

                values.Add(fontSize);
            }
        }

        if (document.Root.Elements().Any(element => element.Name != avalonia + "Style")
            || values.Count == 0)
        {
            throw new FormatException("The theme must contain at least one Window FontSize setter and nothing else.");
        }

        return new ThemeOverride(values[^1]);
    }
}

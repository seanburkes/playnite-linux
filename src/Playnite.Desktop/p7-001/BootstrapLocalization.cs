using System.Text.Json;

namespace Playnite.Desktop.Bootstrap;

/// <summary>
/// Localisation seam over the frozen English key set. An optional translation catalog only
/// overrides known keys; <see cref="LocalizationKeySet.Resolve"/> keeps the v10 fallback and
/// the unknown-key marker. No Core type is changed.
/// </summary>
public sealed class BootstrapLocalization
{
    private const int MaxCatalogBytes = 1024 * 1024;

    private readonly LocalizationKeySet catalog;

    /// <summary>The loaded catalog path, or <c>embedded</c> when no file was read.</summary>
    public string Source { get; }

    public BootstrapLocalization(LocalizationKeySet? catalog = null, string? source = null)
    {
        this.catalog = catalog ?? LocalizationKeySet.English;
        Source = source ?? "embedded";
    }

    public string Resolve(string key) => LocalizationKeySet.English.Resolve(key, catalog);

    public static BootstrapLocalization Load(string? catalogPath)
    {
        if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
        {
            return new BootstrapLocalization();
        }

        var file = new FileInfo(catalogPath);
        if (file.Length > MaxCatalogBytes)
        {
            throw new InvalidDataException($"Localisation catalog {catalogPath} exceeds {MaxCatalogBytes} bytes.");
        }

        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(catalogPath))
            ?? throw new InvalidDataException($"Localisation catalog {catalogPath} is empty.");
        return new BootstrapLocalization(new LocalizationKeySet(entries), Path.GetFullPath(catalogPath));
    }
}

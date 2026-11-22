using Playnite.Desktop.Bootstrap;
using Playnite.Platform.Linux;

namespace Playnite.Desktop.Shell;

internal sealed class SettingsCatalogs(string config)
{
    private const string English = "english";
    private readonly string directory = Path.Combine(config, "Localization");
    private readonly LinuxRegularFileProbe probe = new();

    internal IReadOnlyList<string> List()
    {
        if (!Directory.Exists(directory))
        {
            return [English];
        }

        return [English, .. Directory.EnumerateFiles(directory, "*.json")
            .Where(probe.IsRegularFile)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(id => id is not ("en" or English) && IsCatalogId(id))
            .Order(StringComparer.Ordinal)];
    }

    internal BootstrapLocalization Load(string language)
    {
        if (language != English && IsCatalogId(language))
        {
            var selected = BootstrapLocalization.Load(Path.Combine(directory, language + ".json"), probe);
            if (selected.Source != "embedded")
            {
                return selected;
            }
        }

        return BootstrapLocalization.Load(Path.Combine(directory, "en.json"), probe);
    }

    private static bool IsCatalogId(string id) => id.Length > 0 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

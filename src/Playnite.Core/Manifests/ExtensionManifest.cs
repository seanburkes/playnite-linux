using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace Playnite.Core.Manifests;

// Wire shapes adapted from legacy/source/Playnite/Manifests/ExtensionManifest.cs.
public enum ExtensionType { GenericPlugin, GameLibrary, Script, MetadataProvider }
public enum ApplicationMode { Desktop, Fullscreen }

public class ManifestLink
{
    public string? Name { get; set; }
    public string? Url { get; set; }
}

public class BaseExtensionManifest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public List<ManifestLink>? Links { get; set; }

    [YamlIgnore, JsonIgnore]
    public string? DirectoryPath { get; set; }
    [YamlIgnore, JsonIgnore]
    public string? DirectoryName { get; set; }
    [YamlIgnore, JsonIgnore]
    public string? DescriptionPath { get; set; }
}

public class ExtensionManifest : BaseExtensionManifest
{
    public string? Module { get; set; }
    public string? Icon { get; set; }
    public ExtensionType Type { get; set; }
}

// ThemeManager compatibility and built-in-theme discovery belong to the consuming layer.
public class ThemeManifest : BaseExtensionManifest
{
    public string? ThemeApiVersion { get; set; }
    public ApplicationMode Mode { get; set; }
    public bool ForceLTRLayout { get; set; }
}

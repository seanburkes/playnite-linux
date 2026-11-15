namespace Playnite.Core.Manifests;

// legacy/source/Playnite/App/UpdateManifest.cs; no updater/network side effects.
public class UpdateManifest
{
    public const string ServerManifestFileName = "update.json";
    public Version? Version { get; set; }
    public Version? SdkVersion { get; set; }
    public Version? FullscreenThemeVersion { get; set; }
    public Version? DesktopThemeVersion { get; set; }
    public string? Sha256 { get; set; }
    public List<string>? PackageUrls { get; set; }
    public List<Version>? VersionHistory { get; set; }
}

public class ReleaseNoteData
{
    public Version? Version { get; set; }
    public string? Note { get; set; }
}

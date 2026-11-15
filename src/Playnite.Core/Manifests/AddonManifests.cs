namespace Playnite.Core.Manifests;

// Runtime download/install/UI members of the legacy classes are deliberately not wire data.
public class AddonManifest : AddonManifestBase;

public class AddonInstallerManifest : AddonInstallerManifestBase
{
    public AddonType AddonType { get; set; }

    // legacy/source/Playnite/Manifests/AddonManifests.cs:39-49.
    public List<AddonInstallerPackage>? GetCompatiblePackages(Version apiVersion)
    {
        if (Packages is null || Packages.Count == 0)
        {
            return null;
        }

        return Packages.Where(a => a.RequiredApiVersion is not null
                && a.RequiredApiVersion.Major == apiVersion.Major
                && a.RequiredApiVersion <= apiVersion)
            .OrderByDescending(a => a.Version).ToList();
    }
}

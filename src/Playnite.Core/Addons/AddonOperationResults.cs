using Playnite.Core.Manifests;

namespace Playnite.Core.Addons;

/// <summary>Outcome of installing one add-on directory.</summary>
public sealed class AddonInstallResult
{
    internal AddonInstallResult(
        bool installed,
        BaseExtensionManifest? manifest,
        string? directory,
        string? manifestPath,
        IReadOnlyList<AddonDiagnostic> diagnostics)
    {
        Installed = installed;
        Manifest = manifest;
        Directory = directory;
        ManifestPath = manifestPath;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets whether the add-on files were copied into the stable directory.</summary>
    public bool Installed { get; }

    /// <summary>Gets the parsed manifest that was installed.</summary>
    public BaseExtensionManifest? Manifest { get; }

    /// <summary>Gets the stable <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;</c> directory.</summary>
    public string? Directory { get; }

    /// <summary>Gets the installed <c>extension.yaml</c> path.</summary>
    public string? ManifestPath { get; }

    /// <summary>Gets every validation, conflict or filesystem failure.</summary>
    public IReadOnlyList<AddonDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether the install completed without a failure.</summary>
    public bool Succeeded => Installed && Diagnostics.Count == 0;

    /// <summary>Gets the first failure, or <see langword="null"/> on success.</summary>
    public AddonDiagnostic? Diagnostic => Diagnostics.Count == 0 ? null : Diagnostics[0];
}

/// <summary>Outcome of removing one add-on directory.</summary>
public sealed class AddonUninstallResult
{
    internal AddonUninstallResult(
        bool removed,
        string? directory,
        IReadOnlyList<AddonDiagnostic> diagnostics)
    {
        Removed = removed;
        Directory = directory;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets whether the add-on directory was removed.</summary>
    public bool Removed { get; }

    /// <summary>Gets the removed <c>&lt;root&gt;/&lt;id&gt;</c> directory.</summary>
    public string? Directory { get; }

    /// <summary>Gets every validation or filesystem failure.</summary>
    public IReadOnlyList<AddonDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether the uninstall completed without a failure.</summary>
    public bool Succeeded => Removed && Diagnostics.Count == 0;

    /// <summary>Gets the first failure, or <see langword="null"/> on success.</summary>
    public AddonDiagnostic? Diagnostic => Diagnostics.Count == 0 ? null : Diagnostics[0];
}

namespace Playnite.Core.Addons;

/// <summary>Opens a regular package file without following symbolic links in its path.</summary>
public interface IAddonPackageFileReader
{
    /// <summary>Returns an owned read-only stream whose opened handle was verified.</summary>
    Stream OpenPackageFile(string path);
}

using System.ComponentModel;

namespace Playnite.SDK.Models;

/// <summary>Represents game installation status.</summary>
public enum InstallationStatus
{
    [Description("LOCGameIsInstalledTitle")]
    Installed = 0,

    [Description("LOCGameIsUnInstalledTitle")]
    Uninstalled = 1
}

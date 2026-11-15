namespace Playnite.Core.Manifests;

// Document members from legacy/source/PlayniteSDK/Models/Emulator.cs:15-217.
// These are Core wire DTOs, not a replacement SDK public model surface.
public class EmulatorDefinitionProfile
{
    public string? Name { get; set; }
    public List<string>? Platforms { get; set; }
    public List<string>? ImageExtensions { get; set; }
    public List<string>? ProfileFiles { get; set; }
    public string? InstallationFile { get; set; }
    public string? StartupArguments { get; set; }
    public string? StartupExecutable { get; set; }
    public bool ScriptStartup { get; set; }
    public bool ScriptGameImport { get; set; }
}

public class EmulatorDefinition
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Website { get; set; }
    public List<EmulatorDefinitionProfile>? Profiles { get; set; }
}

public class EmulatedRegion
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool DefaultImport { get; set; }
    public ulong IgdbId { get; set; }
    public List<string>? Codes { get; set; }
}

public class EmulatedPlatform
{
    public ulong IgdbId { get; set; }
    public string? Name { get; set; }
    public string? Id { get; set; }
    public List<string>? Databases { get; set; }
    public List<string>? Emulators { get; set; }
}

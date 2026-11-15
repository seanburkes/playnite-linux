using System.Collections;
using Playnite.Core;

namespace Playnite.Platform.Linux;

/// <summary>Capture ambient values once, before constructing services.</summary>
public sealed class LinuxEnvironment : IEnvironment
{
    private readonly Dictionary<string, string?> variables;
    public string Home { get; }
    public string CurrentDirectory { get; }
    public string ProgramDirectory { get; }

    public LinuxEnvironment()
    {
        variables = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        // Only an unset/empty HOME reaches the passwd entry; .NET returns any other HOME verbatim,
        // so a relative HOME is kept as captured and rejected by LinuxPaths.
        var home = GetVariable("HOME");
        Home = string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
        CurrentDirectory = Environment.CurrentDirectory;
        ProgramDirectory = AppContext.BaseDirectory;
    }

    public string? GetVariable(string name) => variables.GetValueOrDefault(name);
}

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
        var home = GetVariable("HOME");
        Home = Paths.IsFullPath(home) ? home! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        CurrentDirectory = Environment.CurrentDirectory;
        ProgramDirectory = AppContext.BaseDirectory;
    }

    public string? GetVariable(string name) => variables.GetValueOrDefault(name);
}

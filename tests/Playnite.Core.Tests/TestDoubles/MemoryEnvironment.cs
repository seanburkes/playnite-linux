using Playnite.Core;

namespace Playnite.Core.Tests.TestDoubles;

public sealed class MemoryEnvironment : IEnvironment
{
    public string Home { get; init; } = "/home/test-user";
    public string CurrentDirectory { get; init; } = "/working";
    public string ProgramDirectory { get; init; } = "/opt/playnite";
    public Dictionary<string, string?> Variables { get; } = new(StringComparer.Ordinal);
    public string? GetVariable(string name) => Variables.GetValueOrDefault(name);
}

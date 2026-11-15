namespace Playnite.Core;

public interface IEnvironment
{
    string Home { get; }
    string CurrentDirectory { get; }
    string ProgramDirectory { get; }
    string? GetVariable(string name);
}

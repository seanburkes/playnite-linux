namespace Playnite.Cli;

/// <summary>
/// A top-level CLI command. Every concrete implementation in this assembly is discovered
/// automatically, so a new command is a new file: no shared list or dispatch edit is needed.
/// Implementations must have a public parameterless constructor.
/// </summary>
internal interface ICliCommand
{
    /// <summary>First command-line argument that selects this command.</summary>
    string Name { get; }

    /// <summary>Runs the command with every argument after <see cref="Name"/>.</summary>
    Task<int> RunAsync(string[] args);
}

using Playnite.Cli;

namespace Playnite.Core.Tests.Cli;

// A synthetic command in its own file, registered by the tests to prove that a command
// outside Program.cs is reachable through the shared registry without a dispatch edit.
internal sealed class SyntheticCommand : ICliCommand
{
    internal static bool Invoked { get; private set; }

    internal static string[] Arguments { get; private set; } = [];

    public string Name => "synthetic-probe";

    public Task<int> RunAsync(string[] args)
    {
        Invoked = true;
        Arguments = args;
        return Task.FromResult(23);
    }
}

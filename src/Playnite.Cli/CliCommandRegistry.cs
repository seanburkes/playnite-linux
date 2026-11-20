namespace Playnite.Cli;

/// <summary>
/// The command table used by <see cref="Program"/>. Commands are discovered from this
/// assembly once, so adding <c>MyCommand.cs</c> is the whole extension step.
/// </summary>
internal static class CliCommandRegistry
{
    private static readonly Dictionary<string, ICliCommand> Commands = Discover();

    internal static IEnumerable<string> Names => Commands.Keys;

    internal static ICliCommand? Find(string name) => Commands.GetValueOrDefault(name);

    internal static void Register(ICliCommand command) => Add(Commands, command);

    private static Dictionary<string, ICliCommand> Discover()
    {
        var commands = new Dictionary<string, ICliCommand>(StringComparer.Ordinal);
        foreach (var type in typeof(CliCommandRegistry).Assembly.GetTypes())
        {
            if (type is not { IsClass: true, IsAbstract: false } || !typeof(ICliCommand).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new InvalidOperationException($"CLI command {type.FullName} needs a public parameterless constructor.");
            }

            Add(commands, (ICliCommand)Activator.CreateInstance(type)!);
        }

        return commands;
    }

    private static void Add(Dictionary<string, ICliCommand> commands, ICliCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ArgumentException($"CLI command {command.GetType().FullName} has no name.", nameof(command));
        }

        if (!commands.TryAdd(command.Name, command))
        {
            throw new InvalidOperationException($"CLI command name '{command.Name}' is registered more than once.");
        }
    }
}

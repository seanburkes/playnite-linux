namespace Playnite.Cli;

internal static class Program
{
    private const string Usage = "Usage: Playnite.Cli [--version]";

    private static Task<int> Main(string[] args) => RunAsync(args);

    // Internal so tests can exercise dispatch in-process; Main stays the only entry point.
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString());
            return 0;
        }

        if (CliCommandRegistry.Find(args[0]) is { } command)
        {
            return await command.RunAsync(args[1..]);
        }

        // Keep option-shaped mistakes on the documented usage-only path.
        if (!args[0].StartsWith('-'))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        }

        Console.Error.WriteLine(Usage);
        return 1;
    }
}

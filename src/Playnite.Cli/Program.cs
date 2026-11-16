namespace Playnite.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "import-v10")
        {
            return await ImportCommand.RunAsync(args[1..]);
        }
        if (args.Length != 0 && (args.Length != 1 || args[0] != "--version"))
        {
            Console.Error.WriteLine("Usage: Playnite.Cli [--version]");
            return 1;
        }

        Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString());
        return 0;
    }
}

namespace Playnite.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 1 || args[0] != "--version"))
        {
            Console.Error.WriteLine("Usage: Playnite.Cli [--version]");
            return 1;
        }

        Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString());
        return 0;
    }
}

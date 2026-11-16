namespace Playnite.LegacyImport;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: Playnite.LegacyImport <v10-library-directory> (JSON v1 to stdout; read-only)");
                return 1;
            }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(LibraryReader.Read(Path.GetFullPath(args[0]))));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

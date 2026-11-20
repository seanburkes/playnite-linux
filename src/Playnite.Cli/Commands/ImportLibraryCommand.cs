using System.Text.Json;
using Playnite.Core.Import;

namespace Playnite.Cli;

/// <summary>Imports a v10 library through the Core import service.</summary>
internal sealed class ImportLibraryCommand : ICliCommand
{
    private const string Usage =
        "Usage: Playnite.Cli import <reader.dll> <v10-library> <destination.sqlite> [--dry-run] [--replace-existing]";

    private const int InvalidArguments = 2;
    private const int ServiceError = 4;

    public string Name => "import";

    public Task<int> RunAsync(string[] args) => ExecuteAsync(args, Console.Out, Console.Error);

    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length < 3 ||
                args.Skip(3).Any(argument => argument is not ("--dry-run" or "--replace-existing")))
            {
                throw new ArgumentException(Usage);
            }

            var reader = new LegacyLibraryReaderProcess(Path.GetFullPath(args[0]));
            var request = new LibraryImportRequest(
                Path.GetFullPath(args[1]),
                Path.GetFullPath(args[2]),
                args.Skip(3).Contains("--dry-run", StringComparer.Ordinal),
                args.Skip(3).Contains("--replace-existing", StringComparer.Ordinal));
            var result = await new LibraryImportService(reader).ImportAsync(request);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Outcome = result.Outcome.ToString(),
                result.Report,
            }));
            return 0;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            return InvalidArguments;
        }
        catch (LibraryImportException exception)
        {
            error.WriteLine(exception.Message);
            return ServiceError;
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return ServiceError;
        }
    }
}

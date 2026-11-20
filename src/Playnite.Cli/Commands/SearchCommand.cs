using Playnite.Core.Library;

namespace Playnite.Cli;

/// <summary>Searches display names with the v10 matcher and lists one bounded page.</summary>
internal sealed class SearchCommand : ICliCommand
{
    private const string Usage =
        "Usage: Playnite.Cli search <term> --library <library.sqlite> [--offset <0-1000000>] [--limit <1-10000>] " +
        "[--sort <key>] [--descending]";

    public string Name => "search";

    public Task<int> RunAsync(string[] args) => ExecuteAsync(args, Console.Out, Console.Error);

    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length == 0 || args[0].StartsWith('-'))
            {
                throw new UsageException(Usage);
            }

            var term = args[0];
            var options = QueryOptions.Parse(args[1..], Usage);
            var database = await QueryCommandSupport.OpenLibraryAsync(options.LibraryPath);
            var page = await new LibraryService(database).SearchAsync(term, options.ToQuery());
            QueryCommandSupport.WritePage(output, page, options);
            return QueryExitCodes.Success;
        }
        catch (UsageException exception)
        {
            error.WriteLine(exception.Message);
            return QueryExitCodes.InvalidArguments;
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return QueryExitCodes.ServiceError;
        }
    }
}

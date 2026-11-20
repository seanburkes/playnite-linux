using Playnite.Core.Library;

namespace Playnite.Cli;

/// <summary>Lists one bounded page of a library in ascending name order by default.</summary>
internal sealed class ListCommand : ICliCommand
{
    private const string Usage =
        "Usage: Playnite.Cli list --library <library.sqlite> [--offset <0-1000000>] [--limit <1-10000>] " +
        "[--sort <key>] [--descending]";

    public string Name => "list";

    public Task<int> RunAsync(string[] args) => ExecuteAsync(args, Console.Out, Console.Error);

    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = QueryOptions.Parse(args, Usage);
            var database = await QueryCommandSupport.OpenLibraryAsync(options.LibraryPath);
            var page = await new LibraryService(database).ListAsync(options.ToQuery());
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

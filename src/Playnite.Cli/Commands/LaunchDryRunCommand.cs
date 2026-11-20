using System.Text.Json;
using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;

namespace Playnite.Cli;

/// <summary>
/// Resolves a stored game and its first supported play action, then prints the launch plan.
/// This command never starts a process or opens a URL.
/// </summary>
internal sealed class LaunchDryRunCommand : ICliCommand
{
    private const string Usage =
        "Usage: Playnite.Cli launch --dry-run --library <library.sqlite> " +
        "(--id <game-guid> | --name <display-name>)";

    public string Name => "launch";

    public Task<int> RunAsync(string[] args) => ExecuteAsync(args, Console.Out, Console.Error);

    internal static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = Options.Parse(args, Usage);
            var database = await QueryCommandSupport.OpenLibraryAsync(options.LibraryPath);
            var service = new LibraryService(database);
            var game = await ResolveAsync(service, options);
            var (action, reason) = ResolvePlayAction(game.Value);
            if (action is null)
            {
                error.WriteLine(reason);
                return QueryExitCodes.NoPlayAction;
            }

            WriteLaunch(output, game, action);
            return QueryExitCodes.Success;
        }
        catch (UsageException exception)
        {
            error.WriteLine(exception.Message);
            return QueryExitCodes.InvalidArguments;
        }
        catch (GameNotFoundException exception)
        {
            error.WriteLine(exception.Message);
            return QueryExitCodes.GameNotFound;
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return QueryExitCodes.ServiceError;
        }
    }

    private static async Task<StoredGame> ResolveAsync(LibraryService service, Options options)
    {
        if (options.Id is Guid id)
        {
            return await service.GetAsync(id) ??
                throw new GameNotFoundException($"No game with id '{id}' was found.");
        }

        var name = options.Name!;
        var matches = (await service.SearchAsync(name)).Games
            .Where(game => string.Equals(game.Value.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new GameNotFoundException($"No game named '{name}' was found."),
            _ => throw new UsageException($"'{name}' matches {matches.Length} games; use --id.")
        };
    }

    private static (GameAction? Action, string? Reason) ResolvePlayAction(Game game)
    {
        var playActions = game.GameActions?.Where(action => action.IsPlayAction).ToArray() ?? [];
        if (playActions.Length == 0)
        {
            return game.IncludeLibraryPluginAction
                ? (null, $"'{game.Name}' is started by a library plugin action, not a stored play action.")
                : (null, $"'{game.Name}' has no play action.");
        }

        var supported = playActions
            .Where(action => action.Type is GameActionType.File or GameActionType.URL or GameActionType.Script)
            .ToArray();
        if (supported.Length == 0)
        {
            return (null, $"'{game.Name}' has only Emulator play actions; launch --dry-run does not resolve emulator profiles.");
        }

        var action = supported[0];
        if ((action.Type is GameActionType.File or GameActionType.URL) && string.IsNullOrWhiteSpace(action.Path))
        {
            return (null, $"The {action.Type} play action for '{game.Name}' has no path.");
        }

        if (action.Type is GameActionType.Script &&
            string.IsNullOrWhiteSpace(action.Script) && string.IsNullOrWhiteSpace(action.Path))
        {
            return (null, $"The Script play action for '{game.Name}' has no script.");
        }

        return (action, null);
    }

    private static void WriteLaunch(TextWriter output, StoredGame stored, GameAction action)
    {
        var game = stored.Value;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "launch",
            dryRun = true,
            started = false,
            id = game.Id,
            gameId = game.GameId,
            name = game.Name,
            installed = game.IsInstalled,
            actionType = action.Type.ToString(),
            actionName = action.Name,
            path = action.Path,
            arguments = action.Arguments,
            workingDirectory = action.WorkingDir,
            script = action.Script,
            fileExists = action.Type == GameActionType.File ? File.Exists(action.Path) : (bool?)null
        }));
    }

    private sealed record Options(string LibraryPath, Guid? Id, string? Name)
    {
        internal static Options Parse(string[] args, string usage)
        {
            string? library = null;
            Guid? id = null;
            string? name = null;
            var dryRun = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--library":
                        OptionGuard.Once(seen, "--library", usage);
                        library = Value(args, ref index, "--library", usage);
                        break;
                    case "--id":
                        OptionGuard.Once(seen, "--id", usage);
                        var value = Value(args, ref index, "--id", usage);
                        if (!Guid.TryParse(value, out var parsed))
                        {
                            throw new UsageException($"--id must be a GUID, not '{value}'.{Environment.NewLine}{usage}");
                        }

                        id = parsed;
                        break;
                    case "--name":
                        OptionGuard.Once(seen, "--name", usage);
                        name = Value(args, ref index, "--name", usage);
                        break;
                    case "--dry-run":
                        OptionGuard.Once(seen, "--dry-run", usage);
                        dryRun = true;
                        break;
                    default:
                        throw new UsageException(usage);
                }
            }

            if (!dryRun)
            {
                throw new UsageException($"launch requires --dry-run; this build never starts a game.{Environment.NewLine}{usage}");
            }

            if (library is null)
            {
                throw new UsageException(usage);
            }

            if ((id is null) == (name is null))
            {
                throw new UsageException($"Choose exactly one of --id and --name.{Environment.NewLine}{usage}");
            }

            return new Options(library, id, name);
        }

        private static string Value(string[] args, ref int index, string name, string usage)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"{name} needs a value.{Environment.NewLine}{usage}");
            }

            return args[++index];
        }
    }
}

/// <summary>The requested game identity is absent from the library, or a name matched none.</summary>
internal sealed class GameNotFoundException(string message) : Exception(message);

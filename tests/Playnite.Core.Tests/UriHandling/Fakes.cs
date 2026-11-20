using Playnite.Core.UriHandling;
using Playnite.SDK;

namespace Playnite.Core.Tests.UriHandling;

internal sealed class UriCommandCall
{
    public required string Kind { get; init; }
    public Guid? GameId { get; init; }
    public int? ActionIndex { get; init; }
    public string? Term { get; init; }
    public PlayniteViewTarget? View { get; init; }
    public PlayniteFilterTarget? Filter { get; init; }
}

internal sealed class FakeUriCommandHost : IUriCommandHost
{
    public List<UriCommandCall> Calls { get; } = new();

    public Func<UriCommandResult> Result { get; set; } = UriCommandResult.Completed;

    public Func<CancellationToken, ValueTask<UriCommandResult>>? StartHandler { get; set; }

    public ValueTask<UriCommandResult> StartGameAsync(
        Guid gameId,
        int? actionIndex,
        CancellationToken cancellationToken)
    {
        Calls.Add(new UriCommandCall
        {
            Kind = "start",
            GameId = gameId,
            ActionIndex = actionIndex,
        });
        return StartHandler is null
            ? ValueTask.FromResult(Result())
            : StartHandler(cancellationToken);
    }

    public ValueTask<UriCommandResult> ShowGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        Calls.Add(new UriCommandCall { Kind = "showgame", GameId = gameId });
        return ValueTask.FromResult(Result());
    }

    public ValueTask<UriCommandResult> SearchAsync(string? term, CancellationToken cancellationToken)
    {
        Calls.Add(new UriCommandCall { Kind = "search", Term = term });
        return ValueTask.FromResult(Result());
    }

    public ValueTask<UriCommandResult> ShowViewAsync(
        PlayniteViewTarget view,
        CancellationToken cancellationToken)
    {
        Calls.Add(new UriCommandCall { Kind = "view", View = view });
        return ValueTask.FromResult(Result());
    }

    public ValueTask<UriCommandResult> ApplyFilterAsync(
        PlayniteFilterTarget filter,
        CancellationToken cancellationToken)
    {
        Calls.Add(new UriCommandCall { Kind = "filter", Filter = filter });
        return ValueTask.FromResult(Result());
    }
}

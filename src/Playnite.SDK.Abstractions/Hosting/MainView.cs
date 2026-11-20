using Playnite.SDK.Models;

namespace Playnite.SDK;

/// <summary>Currently active desktop view. Values follow the v10 SDK contract.</summary>
public enum DesktopView
{
    /// <summary>Details view.</summary>
    Details = 0,

    /// <summary>Grid view.</summary>
    Grid = 1,

    /// <summary>List view.</summary>
    List = 2,
}

/// <summary>Currently active fullscreen view. Values follow the v10 SDK contract.</summary>
public enum FullscreenView
{
    /// <summary>List view.</summary>
    List = 0,

    /// <summary>Details view.</summary>
    Details = 1,
}

/// <summary>The shell state a host reports for the main-view query surface.</summary>
public sealed record MainViewState
{
    /// <summary>Creates a state snapshot, copying the game lists.</summary>
    public MainViewState(
        DesktopView activeDesktopView = DesktopView.Grid,
        FullscreenView activeFullscreenView = FullscreenView.List,
        SortOrder sortOrder = SortOrder.Name,
        bool sortDescending = false,
        LibraryGroupBy grouping = LibraryGroupBy.None,
        IEnumerable<IGameView>? selectedGames = null,
        IEnumerable<IGameView>? filteredGames = null)
    {
        if (!Enum.IsDefined(activeDesktopView))
        {
            throw new ArgumentOutOfRangeException(nameof(activeDesktopView), activeDesktopView, "Unknown desktop view.");
        }

        if (!Enum.IsDefined(activeFullscreenView))
        {
            throw new ArgumentOutOfRangeException(nameof(activeFullscreenView), activeFullscreenView, "Unknown fullscreen view.");
        }

        if (!Enum.IsDefined(sortOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder, "Unknown sort order.");
        }

        if (!Enum.IsDefined(grouping))
        {
            throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "Unknown grouping.");
        }

        ActiveDesktopView = activeDesktopView;
        ActiveFullscreenView = activeFullscreenView;
        SortOrder = sortOrder;
        SortDescending = sortDescending;
        Grouping = grouping;
        SelectedGames = (selectedGames ?? Array.Empty<IGameView>()).ToArray();
        FilteredGames = (filteredGames ?? Array.Empty<IGameView>()).ToArray();
    }

    /// <summary>Active desktop view.</summary>
    public DesktopView ActiveDesktopView { get; }

    /// <summary>Active fullscreen view.</summary>
    public FullscreenView ActiveFullscreenView { get; }

    /// <summary>Active sort key.</summary>
    public SortOrder SortOrder { get; }

    /// <summary>True when the active sort is descending.</summary>
    public bool SortDescending { get; }

    /// <summary>Active grouping key.</summary>
    public LibraryGroupBy Grouping { get; }

    /// <summary>Selected games in selection order.</summary>
    public IReadOnlyList<IGameView> SelectedGames { get; }

    /// <summary>Games currently visible in the library view.</summary>
    public IReadOnlyList<IGameView> FilteredGames { get; }
}

/// <summary>The main-view state plus the host outcome.</summary>
public sealed record MainViewResponse(HostOperationStatus Status, MainViewState? State = null, string? Diagnostic = null)
{
    /// <summary>True when the host answered the request.</summary>
    public bool IsCompleted => Status == HostOperationStatus.Completed;
}

/// <summary>A list of games plus the host outcome.</summary>
public sealed record MainViewGamesResponse
{
    /// <summary>Creates a response, copying the game list.</summary>
    public MainViewGamesResponse(HostOperationStatus status, IEnumerable<IGameView>? games = null, string? diagnostic = null)
    {
        Status = status;
        Games = (games ?? Array.Empty<IGameView>()).ToArray();
        Diagnostic = diagnostic;
    }

    /// <summary>Host outcome.</summary>
    public HostOperationStatus Status { get; }

    /// <summary>Games in host order; empty when unavailable.</summary>
    public IReadOnlyList<IGameView> Games { get; }

    /// <summary>Diagnostic for an unavailable or failed request.</summary>
    public string? Diagnostic { get; }

    /// <summary>True when the host answered the request.</summary>
    public bool IsCompleted => Status == HostOperationStatus.Completed;
}

/// <summary>
/// The query seam supplied by the UI shell. The CLI may register a headless host or leave it
/// unregistered; the API then reports Unavailable with a diagnostic instead of throwing.
/// </summary>
public interface IMainViewHost
{
    /// <summary>Returns the current shell state.</summary>
    Task<MainViewState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current selection.</summary>
    Task<IReadOnlyList<IGameView>> GetSelectedGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the games currently visible in the library view.</summary>
    Task<IReadOnlyList<IGameView>> GetFilteredGamesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The v10-named main-view query surface: the data the shell needs without any UI toolkit type.
/// </summary>
public interface IMainViewAPI
{
    /// <summary>Returns the current shell state.</summary>
    Task<MainViewResponse> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current selection.</summary>
    Task<MainViewGamesResponse> GetSelectedGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the games currently visible in the library view.</summary>
    Task<MainViewGamesResponse> GetFilteredGamesAsync(CancellationToken cancellationToken = default);
}

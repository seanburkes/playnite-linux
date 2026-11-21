using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Queries;

namespace Playnite.Desktop.Shell;

/// <summary>One displayed library row, detached from the database session.</summary>
public sealed record LibraryRow(Guid Id, string Name, string GameId);

/// <summary>
/// Instance-owned library state for the ContentHost pilot. The database destination is the new
/// app-owned XDG data file; the frozen v10 fixture is only ever read by tests, never opened here.
/// Bounded p6-012 windows come from <see cref="LibraryService.ListAsync"/> in name order; search,
/// filter and sort controls remain p7-004 work.
/// </summary>
public sealed class LibraryViewModel
{
    public const int PageSize = 50;

    private readonly string databasePath;
    private LibraryDatabase? database;
    private LibraryService? service;
    private int pageOffset;

    public LibraryViewModel(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
    }

    public IReadOnlyList<LibraryRow> Rows { get; private set; } = [];

    public LibraryRow? Selected { get; private set; }

    public int TotalCount { get; private set; }

    public string Status { get; private set; } = "unloaded";

    public int PageCount => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

    public int CurrentPage => pageOffset / PageSize + 1;

    public bool CanGoPrevious => CurrentPage > 1;

    public bool CanGoNext => CurrentPage < PageCount;

    public string PageIndicator => $"Page {CurrentPage} / {PageCount}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new ArgumentException("The library path must have a parent directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        database = await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        service = new LibraryService(database);
        await LoadPageAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadPageAsync(int offset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var service = this.service ?? throw new InvalidOperationException("The library has not been opened.");
        cancellationToken.ThrowIfCancellationRequested();
        var page = await service.ListAsync(
            new LibraryQuery(Offset: offset, Limit: PageSize, SortBy: SortOrder.Name),
            cancellationToken).ConfigureAwait(false);

        pageOffset = offset;
        TotalCount = page.TotalCount;
        Rows = page.Games
            .Select(game => new LibraryRow(game.Value.Id, game.Value.Name ?? "(unnamed)", game.Value.GameId))
            .ToArray();
        var first = Rows.Count > 0 ? Rows[0] : null;
        Selected = first is null ? null : await LoadDetailsAsync(first, cancellationToken).ConfigureAwait(false);
        Status = TotalCount == 0 ? "empty" : "loaded";
    }

    /// <summary>Loads the selected row's details through the service; used by the real row control.</summary>
    public async Task SelectAsync(LibraryRow? row, CancellationToken cancellationToken = default)
    {
        if (row is null)
        {
            Selected = null;
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Selected = await LoadDetailsAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoNext)
        {
            return;
        }

        await LoadPageAsync(CurrentPage * PageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoPrevious)
        {
            return;
        }

        await LoadPageAsync((CurrentPage - 2) * PageSize, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LibraryRow> LoadDetailsAsync(LibraryRow row, CancellationToken cancellationToken)
    {
        var stored = await service!.GetAsync(row.Id, cancellationToken).ConfigureAwait(false);
        return stored is null
            ? row
            : row with { Name = stored.Value.Name ?? "(unnamed)", GameId = stored.Value.GameId };
    }

    /// <summary>Releases the instance-owned references; the host view calls this on unload.</summary>
    public void Dispose()
    {
        Rows = [];
        Selected = null;
        service = null;
        database = null;
        Status = "disposed";
    }
}

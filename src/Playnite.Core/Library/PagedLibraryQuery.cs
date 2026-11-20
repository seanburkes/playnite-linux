using Playnite.Core.Queries;
using Playnite.SDK.Models;

namespace Playnite.Core.Library;

/// <summary>Bounds for one ordered page of the stored library.</summary>
/// <remarks>The limit is required: this reader never returns the remainder of an unbounded set.</remarks>
public sealed record PagedLibraryQuery(
    SortOrder SortBy = SortOrder.Name,
    bool Descending = false,
    int Offset = 0,
    int Limit = PagedLibraryQuery.DefaultLimit)
{
    /// <summary>Page size used when the caller does not name one.</summary>
    public const int DefaultLimit = 100;
}

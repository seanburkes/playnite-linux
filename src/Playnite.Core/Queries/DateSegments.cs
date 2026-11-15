using Playnite.Core.Models;

namespace Playnite.Core.Queries;

public interface IQueryClock
{
    DateTime Now { get; }
}

public interface IDateSegmentCalculator
{
    PastTimeSegment GetSegment(DateTime? value, DateTime now);
}

// PlayniteSDK/Models/Game.cs:1531-1565; injected local time replaces ambient
// Today/Now reads, retaining calendar-day checks before elapsed-time buckets.
public sealed class DateSegmentCalculator : IDateSegmentCalculator
{
    public PastTimeSegment GetSegment(DateTime? value, DateTime now)
    {
        if (value == null)
        {
            return PastTimeSegment.Never;
        }

        if (value.Value.Date == now.Date)
        {
            return PastTimeSegment.Today;
        }

        if (value.Value.Date.AddDays(1) == now.Date)
        {
            return PastTimeSegment.Yesterday;
        }

        var diff = now - value.Value;
        if (diff.TotalDays < 7)
        {
            return PastTimeSegment.PastWeek;
        }

        if (diff.TotalDays < 31)
        {
            return PastTimeSegment.PastMonth;
        }

        if (diff.TotalDays < 365)
        {
            return PastTimeSegment.PastYear;
        }

        return PastTimeSegment.MoreThenYear;
    }
}

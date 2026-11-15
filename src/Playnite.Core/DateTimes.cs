using System.Globalization;

namespace Playnite;

// Pure branches of legacy/source/Playnite/DateTimes.cs and NullableDateToStringConverter.cs.
public static class DateTimes
{
    public static string ToDisplayString(this DateTime date, CultureInfo culture, DateTime today,
        Func<string, string> localize, string dateUiFormat, string? format = null, bool pastWeekRelativeFormat = false)
    {
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            if (pastWeekRelativeFormat)
            {
                var dayDiff = (today - date.Date).TotalDays;
                if (dayDiff == 0)
                {
                    return localize("LOCToday");
                }

                if (dayDiff == 1)
                {
                    return localize("LOCYesterday");
                }

                if (dayDiff > 1 && dayDiff < 7)
                {
                    return localize("LOC" + date.DayOfWeek);
                }
            }

            return date.ToString(format ?? dateUiFormat, culture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "unsupported";
        }
    }

    // true + null is an empty field; false replaces the WPF UnsetValue result.
    public static bool TryParseDisplayDate(string? value, string dateUiFormat, out DateTime? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (DateTime.TryParseExact(value, dateUiFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }
}

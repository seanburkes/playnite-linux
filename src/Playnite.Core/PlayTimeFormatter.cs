using System.Globalization;

namespace Playnite;

// legacy/source/Playnite/Converters/PlayTimeToStringConverter.cs:39-75, without WPF/cache.
public static class PlayTimeFormatter
{
    public static string ToDisplayString(ulong? value, bool formatToDays, CultureInfo culture, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (value == null || value == 0)
        {
            return localize("LOCPlayedNone");
        }

        var seconds = value.Value;
        if (seconds < 60)
        {
            return string.Format(culture, localize("LOCPlayedSeconds"), seconds);
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return string.Format(culture, localize("LOCPlayedMinutes"), minutes);
        }

        var hours = minutes / 60;
        if (formatToDays && hours >= 24)
        {
            return string.Format(culture, localize("LOCPlayedDays"), hours / 24, hours % 24, minutes % 60);
        }

        return string.Format(culture, localize("LOCPlayedHours"), hours, minutes - hours * 60);
    }
}

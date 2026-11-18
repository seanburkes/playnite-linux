namespace Playnite.Platform.Linux.Sessions;

/// <summary>
/// Parses the fixed fields after the parenthesised comm in <c>/proc/&lt;pid&gt;/stat</c>. The
/// comm may contain spaces and ')' characters, so only the final ')' delimits it.
/// </summary>
public static class LinuxProcStat
{
    public static ProcStat Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var opening = text.IndexOf('(');
        var closing = text.LastIndexOf(')');
        if (opening < 0 || closing <= opening)
        {
            throw new FormatException("The stat row has no parenthesised command name.");
        }

        if (!int.TryParse(text.AsSpan(0, opening).Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            throw new FormatException("The stat row has no numeric PID.");
        }

        var fields = text[(closing + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After comm: field 3 state, field 4 ppid, ..., field 22 starttime.
        if (fields.Length < 20 || fields[0].Length != 1)
        {
            throw new FormatException("The stat row is too short or has no state field.");
        }

        if (!int.TryParse(fields[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parent))
        {
            throw new FormatException("The stat row has no numeric parent PID.");
        }

        if (!long.TryParse(fields[19], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var startTime))
        {
            throw new FormatException("The stat row has no numeric start time.");
        }

        return new ProcStat(pid, parent, fields[0][0], startTime);
    }
}

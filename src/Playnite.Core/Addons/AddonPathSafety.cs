namespace Playnite.Core.Addons;

/// <summary>
/// Path rules for add-on ids, versions and package entries. Install paths are built from
/// manifest text, so a value is accepted only when it is one portable directory name
/// (<see cref="IsSafeSegment"/>) or a portable relative path made of those names
/// (<see cref="IsSafeRelativePath"/>). Values are rejected, never silently rewritten: a
/// sanitised id could collide with another add-on and make the install target ambiguous.
/// </summary>
public static class AddonPathSafety
{
    private const string WindowsInvalidChars = "<>:\"|?*";

    /// <summary>Gets whether the value is one safe directory name.</summary>
    public static bool IsSafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Leading or trailing whitespace makes an id visually collide with another id.
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (value is "." or "..")
        {
            return false;
        }

        if (value.Length > 255 || Path.IsPathRooted(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }

            // Separators, Windows invalid name characters, and platform invalid characters
            // are all rejected so the same manifest is safe on every supported host.
            if (character is '/' or '\\'
                || WindowsInvalidChars.Contains(character)
                || Path.GetInvalidFileNameChars().Contains(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets whether the value is a relative path whose every segment is safe.</summary>
    public static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        // Backslash is a separator on the Windows v10 host even when this process runs on Linux.
        if (value.Contains('\\'))
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length > 0 && segments.All(IsSafeSegment);
    }
}

using System.ComponentModel;

namespace Playnite;

// legacy/source/Playnite/Common/Extensions/Enums.cs:23-47.
public static class EnumExtensions
{
    public static string GetDescription(this Enum source, Func<string, string> localize)
    {
        var field = source.GetType().GetField(source.ToString());
        if (field == null)
        {
            return string.Empty;
        }

        var attributes = (DescriptionAttribute[])field.GetCustomAttributes(typeof(DescriptionAttribute), false);
        if (attributes.Length > 0)
        {
            var desc = attributes[0].Description;
            return desc.StartsWith("LOC", StringComparison.Ordinal) ? localize(desc) : desc;
        }

        return source.ToString();
    }
}

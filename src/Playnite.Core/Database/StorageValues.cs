using System.Globalization;

namespace Playnite.Core.Database;

internal static class StorageValues
{
    internal static void RequireIdentity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An empty entity identity cannot be persisted.", nameof(id));
        }
    }

    internal static string? Timestamp(DateTime? value)
    {
        if (value == null) return null;
        if (value.Value.Kind == DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Unspecified timestamps require an explicit source timezone before persistence.", nameof(value));
        }
        return new DateTimeOffset(value.Value.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);
    }

    internal static DateTime? Timestamp(string? value) => value == null ? null
        : DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture).UtcDateTime;

    // SQLite INTEGER is signed; fixed-width decimal TEXT preserves the complete v10 UInt64 domain and sort order.
    internal static string Unsigned(ulong value) => value.ToString("D20", CultureInfo.InvariantCulture);
    internal static ulong Unsigned(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    internal static string? Reference(Guid value) => value == Guid.Empty ? null : value.ToString("D");
    internal static Guid Reference(string? value) => value == null ? Guid.Empty : Guid.Parse(value);
}

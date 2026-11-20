using System.Numerics;
using Newtonsoft.Json.Linq;

namespace Playnite.Settings;

/// <summary>v10 automatic backup frequency values, persisted as their v10 numbers.</summary>
public enum AutoBackupFrequency
{
    OnceADay = 1,
    OnceAWeek = 2,
}

/// <summary>v10 update-check frequency values, persisted as their v10 numbers.</summary>
public enum UpdateCheckFrequency
{
    OnEveryStartup = 0,
    OnceADay = 1,
    OnceAWeek = 2,
    Manually = 3,
}

/// <summary>
/// A typed settings name. Values are read from <c>settings</c> and then <c>legacy</c>; a
/// missing value reads as <see cref="DefaultValue"/>. Defaults are not written until an
/// explicit update, so a missing store and a migrated store agree on their defaults.
/// </summary>
public abstract class SettingsKey
{
    private protected SettingsKey(string name, Type valueType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A settings name is required.", nameof(name));
        }

        Name = name;
        ValueType = valueType;
    }

    /// <summary>Gets the v10 root property name.</summary>
    public string Name { get; }

    /// <summary>Gets the CLR type of the value.</summary>
    public Type ValueType { get; }

    internal abstract bool AllowsNull { get; }

    internal abstract JToken ToToken(object? value);

    internal abstract object? FromToken(JToken token);

    internal abstract void ValidateToken(JToken token);

    /// <summary>
    /// Reads an integer token without narrowing past its JSON representation. A value that
    /// Json.NET parsed as <see cref="BigInteger"/> stays exact so callers can range-check it.
    /// </summary>
    internal static bool TryGetInteger(JToken token, out BigInteger value)
    {
        if (token is JValue { Type: JTokenType.Integer, Value: { } raw })
        {
            switch (raw)
            {
                case BigInteger integer:
                    value = integer;
                    return true;
                case long integer:
                    value = integer;
                    return true;
                case ulong integer:
                    value = integer;
                    return true;
                case int integer:
                    value = integer;
                    return true;
                case uint integer:
                    value = integer;
                    return true;
                case short integer:
                    value = integer;
                    return true;
                case ushort integer:
                    value = integer;
                    return true;
                case byte integer:
                    value = integer;
                    return true;
                case sbyte integer:
                    value = integer;
                    return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>A typed settings name with a documented v10 default.</summary>
public sealed class SettingsKey<T> : SettingsKey
{
    /// <summary>Creates a key. The name must be a root property of the v10 PlayniteSettings object.</summary>
    public SettingsKey(string name, T defaultValue)
        : base(name, typeof(T))
    {
        DefaultValue = defaultValue;
    }

    /// <summary>Gets the value returned when the property is absent.</summary>
    public T DefaultValue { get; }

    internal override bool AllowsNull =>
        !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null;

    internal override JToken ToToken(object? value)
    {
        if (value is null)
        {
            if (!AllowsNull)
            {
                throw new SettingsException(SettingsFailure.InvalidValue,
                    $"Setting '{Name}' does not accept null.");
            }

            return JValue.CreateNull();
        }

        if (value is not T typed)
        {
            throw new SettingsException(SettingsFailure.InvalidValue,
                $"Setting '{Name}' requires a {typeof(T).Name} value, not {value.GetType().Name}.");
        }

        return JToken.FromObject(typed);
    }

    internal override object? FromToken(JToken token) => token.ToObject<T>();

    internal override void ValidateToken(JToken token)
    {
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (token.Type == JTokenType.Null)
        {
            if (!AllowsNull)
            {
                throw InvalidType(token);
            }

            return;
        }

        if (type == typeof(bool))
        {
            if (token.Type != JTokenType.Boolean)
            {
                throw InvalidType(token);
            }

            return;
        }

        if (type == typeof(string))
        {
            if (token.Type != JTokenType.String)
            {
                throw InvalidType(token);
            }

            return;
        }

        if (type == typeof(int) || type == typeof(long) || type.IsEnum)
        {
            ValidateInteger(type, token);
            return;
        }

        throw InvalidType(token);
    }

    private void ValidateInteger(Type type, JToken token)
    {
        if (token.Type != JTokenType.Integer)
        {
            throw InvalidType(token);
        }

        // A token beyond long is still an integer; it is out of every supported CLR range.
        if (!TryGetInteger(token, out var value))
        {
            throw type.IsEnum ? UndefinedEnum(type, token) : OutOfRange(type, token);
        }

        if (type == typeof(int))
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw OutOfRange(type, token);
            }

            return;
        }

        if (type == typeof(long))
        {
            if (value < long.MinValue || value > long.MaxValue)
            {
                throw OutOfRange(type, token);
            }

            return;
        }

        if (value < int.MinValue || value > int.MaxValue || !Enum.IsDefined(type, (int)value))
        {
            throw UndefinedEnum(type, token);
        }
    }

    private SettingsException InvalidType(JToken token) =>
        new(SettingsFailure.InvalidValue,
            $"Setting '{Name}' must be a {Nullable.GetUnderlyingType(typeof(T))?.Name ?? typeof(T).Name}, not {token.Type}.");

    private SettingsException OutOfRange(Type type, JToken token) =>
        new(SettingsFailure.InvalidValue,
            $"Setting '{Name}' value {token} is outside the {type.Name} range.");

    private SettingsException UndefinedEnum(Type type, JToken token) =>
        new(SettingsFailure.InvalidValue,
            $"Setting '{Name}' value {token} is not a defined {type.Name} value.");
}

/// <summary>
/// The typed settings contract. Every default matches the v10 <c>PlayniteSettings</c> field
/// initializer; a value absent from both <c>settings</c> and <c>legacy</c> reads as that
/// default. Unlisted v10 properties stay available through the raw snapshot objects.
/// </summary>
public static class PlayniteSettingsKeys
{
    /// <summary>v10 settings object version. Default <c>7</c>.</summary>
    public static SettingsKey<int> Version { get; } = new("Version", 7);

    /// <summary>Start the desktop client fullscreen. Default <c>false</c>.</summary>
    public static SettingsKey<bool> StartInFullscreen { get; } = new("StartInFullscreen", false);

    /// <summary>Start minimized to the tray. Default <c>false</c>.</summary>
    public static SettingsKey<bool> StartMinimized { get; } = new("StartMinimized", false);

    /// <summary>Minimize to the tray. Default <c>false</c>.</summary>
    public static SettingsKey<bool> MinimizeToTray { get; } = new("MinimizeToTray", false);

    /// <summary>Close to the tray. Default <c>true</c>.</summary>
    public static SettingsKey<bool> CloseToTray { get; } = new("CloseToTray", true);

    /// <summary>Show the tray icon. Default <c>true</c>.</summary>
    public static SettingsKey<bool> EnableTray { get; } = new("EnableTray", true);

    /// <summary>Show the sidebar. Default <c>true</c>.</summary>
    public static SettingsKey<bool> ShowSidebar { get; } = new("ShowSidebar", true);

    /// <summary>The first-run wizard has completed. Default <c>false</c>.</summary>
    public static SettingsKey<bool> FirstTimeWizardComplete { get; } = new("FirstTimeWizardComplete", false);

    /// <summary>UI language id. Default <c>"english"</c>.</summary>
    public static SettingsKey<string?> Language { get; } = new("Language", "english");

    /// <summary>Desktop theme id. Default <c>"Playnite_builtin_DefaultDesktop"</c>.</summary>
    public static SettingsKey<string?> Theme { get; } = new("Theme", "Playnite_builtin_DefaultDesktop");

    /// <summary>Library database path after migration. Default <c>null</c>.</summary>
    public static SettingsKey<string?> DatabasePath { get; } = new("DatabasePath", null);

    /// <summary>Installation instance id; v10 leaves it unset. Default <c>null</c>.</summary>
    public static SettingsKey<string?> InstallInstanceId { get; } = new("InstallInstanceId", null);

    /// <summary>Automatic backups are enabled. Default <c>false</c>.</summary>
    public static SettingsKey<bool> AutoBackupEnabled { get; } = new("AutoBackupEnabled", false);

    /// <summary>Automatic backup frequency. Default <see cref="AutoBackupFrequency.OnceAWeek"/>.</summary>
    public static SettingsKey<AutoBackupFrequency> AutoBackupFrequency { get; } =
        new("AutoBackupFrequency", Playnite.Settings.AutoBackupFrequency.OnceAWeek);

    /// <summary>Program update check frequency. Default <see cref="UpdateCheckFrequency.OnEveryStartup"/>.</summary>
    public static SettingsKey<UpdateCheckFrequency> CheckForProgramUpdates { get; } =
        new("CheckForProgramUpdates", Playnite.Settings.UpdateCheckFrequency.OnEveryStartup);

    internal static IReadOnlyList<SettingsKey> All { get; } =
    [
        Version, StartInFullscreen, StartMinimized, MinimizeToTray, CloseToTray, EnableTray,
        ShowSidebar, FirstTimeWizardComplete, Language, Theme, DatabasePath, InstallInstanceId,
        AutoBackupEnabled, AutoBackupFrequency, CheckForProgramUpdates,
    ];
}

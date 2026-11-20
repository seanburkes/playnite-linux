using System.Globalization;
using System.Text;

namespace Playnite.Core.UriHandling;

/// <summary>The application command a <c>playnite://</c> URI targets.</summary>
public enum PlayniteUriTarget
{
    /// <summary>A registered plugin source owns the URI.</summary>
    CustomSource = 0,

    /// <summary>Launch a game, optionally through one of its play actions.</summary>
    StartGame,

    /// <summary>Select a game in the main view.</summary>
    ShowGame,

    /// <summary>Open the search view with an optional term.</summary>
    Search,

    /// <summary>Switch the main view between desktop and fullscreen.</summary>
    ShowView,

    /// <summary>Apply one stored-field library filter.</summary>
    Filter,
}

/// <summary>A validated view target for <c>playnite://playnite/view/...</c>.</summary>
public enum PlayniteViewTarget
{
    /// <summary>Desktop mode.</summary>
    Desktop = 0,

    /// <summary>Fullscreen mode.</summary>
    Fullscreen,
}

/// <summary>Stored-field filter names accepted by <c>playnite://playnite/filter</c>.</summary>
public enum PlayniteFilterField
{
    /// <summary>Favourite flag; value is <c>true</c> or <c>false</c>.</summary>
    Favorite = 0,

    /// <summary>Installed flag; value is <c>true</c> or <c>false</c>.</summary>
    Installed,

    /// <summary>Uninstalled flag; value is <c>true</c> or <c>false</c>.</summary>
    Uninstalled,

    /// <summary>Hidden flag; value is <c>true</c> or <c>false</c>.</summary>
    Hidden,

    /// <summary>Genre id; value is a GUID.</summary>
    Genre,

    /// <summary>Platform id; value is a GUID.</summary>
    Platform,

    /// <summary>Publisher id; value is a GUID.</summary>
    Publisher,

    /// <summary>Developer id; value is a GUID.</summary>
    Developer,

    /// <summary>Category id; value is a GUID.</summary>
    Category,

    /// <summary>Tag id; value is a GUID.</summary>
    Tag,

    /// <summary>Series id; value is a GUID.</summary>
    Series,

    /// <summary>Region id; value is a GUID.</summary>
    Region,

    /// <summary>Source id; value is a GUID.</summary>
    Source,

    /// <summary>Age-rating id; value is a GUID.</summary>
    AgeRating,

    /// <summary>Library id; value is a GUID.</summary>
    Library,

    /// <summary>Completion-status id; value is a GUID.</summary>
    CompletionStatus,

    /// <summary>Feature id; value is a GUID.</summary>
    Feature,

    /// <summary>Release year; value is four ASCII digits.</summary>
    ReleaseYear,

    /// <summary>Game name text; value is bounded text.</summary>
    Name,
}

/// <summary>One validated filter target.</summary>
public sealed record PlayniteFilterTarget(PlayniteFilterField Field, string Value);

/// <summary>
/// Parsed and validated form of an external <c>playnite://</c> URI.
/// </summary>
/// <remarks>
/// This is the headless Core parser for the app routes. The platform layer validates the raw
/// URI before it crosses a process boundary (<c>Playnite.Platform.Linux.Integration.PlayniteUri</c>);
/// Core cannot reference that project, so the same strict rules are applied again here and the
/// route is then narrowed to a known command. Parsing never executes anything and never touches
/// a shell. Unknown or unsupported commands are rejected rather than ignored.
/// </remarks>
public sealed record PlayniteUriRoute
{
    /// <summary>The reserved URI scheme.</summary>
    public const string Scheme = "playnite";

    /// <summary>The source that carries application commands.</summary>
    public const string AppSource = "playnite";

    /// <summary>Largest accepted URI length in characters.</summary>
    public const int MaxLength = 2048;

    /// <summary>Largest accepted number of URI arguments.</summary>
    public const int MaxArguments = 32;

    /// <summary>Largest accepted decoded argument length in characters.</summary>
    public const int MaxArgumentLength = 1024;

    /// <summary>Largest accepted source-name length in characters.</summary>
    public const int MaxSourceLength = 64;

    /// <summary>Largest accepted search term length in characters.</summary>
    public const int MaxSearchTermLength = 256;

    /// <summary>Largest accepted filter value length in characters.</summary>
    public const int MaxFilterValueLength = 256;

    /// <summary>Largest accepted play-action index.</summary>
    public const int MaxActionIndex = 999;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<string, PlayniteFilterField> FilterFields =
        new(StringComparer.Ordinal)
        {
            ["favorite"] = PlayniteFilterField.Favorite,
            ["installed"] = PlayniteFilterField.Installed,
            ["uninstalled"] = PlayniteFilterField.Uninstalled,
            ["hidden"] = PlayniteFilterField.Hidden,
            ["genre"] = PlayniteFilterField.Genre,
            ["platform"] = PlayniteFilterField.Platform,
            ["publisher"] = PlayniteFilterField.Publisher,
            ["developer"] = PlayniteFilterField.Developer,
            ["category"] = PlayniteFilterField.Category,
            ["tag"] = PlayniteFilterField.Tag,
            ["series"] = PlayniteFilterField.Series,
            ["region"] = PlayniteFilterField.Region,
            ["source"] = PlayniteFilterField.Source,
            ["age-rating"] = PlayniteFilterField.AgeRating,
            ["library"] = PlayniteFilterField.Library,
            ["completion-status"] = PlayniteFilterField.CompletionStatus,
            ["feature"] = PlayniteFilterField.Feature,
            ["release-year"] = PlayniteFilterField.ReleaseYear,
            ["name"] = PlayniteFilterField.Name,
        };

    private PlayniteUriRoute(
        string source,
        IReadOnlyList<string> arguments,
        PlayniteUriTarget target,
        Guid? gameId = null,
        int? actionIndex = null,
        string? searchTerm = null,
        PlayniteViewTarget? view = null,
        PlayniteFilterTarget? filter = null)
    {
        Source = source;
        Arguments = arguments;
        Target = target;
        GameId = gameId;
        ActionIndex = actionIndex;
        SearchTerm = searchTerm;
        View = view;
        Filter = filter;
    }

    /// <summary>Gets the source segment, for example <c>playnite</c> or a plugin source.</summary>
    public string Source { get; }

    /// <summary>Gets every decoded argument after the source.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the validated application target, or <see cref="PlayniteUriTarget.CustomSource"/>.</summary>
    public PlayniteUriTarget Target { get; }

    /// <summary>Gets the game id for <see cref="PlayniteUriTarget.StartGame"/> and <see cref="PlayniteUriTarget.ShowGame"/>.</summary>
    public Guid? GameId { get; }

    /// <summary>Gets the optional play-action index for <see cref="PlayniteUriTarget.StartGame"/>.</summary>
    public int? ActionIndex { get; }

    /// <summary>Gets the optional search term for <see cref="PlayniteUriTarget.Search"/>.</summary>
    public string? SearchTerm { get; }

    /// <summary>Gets the view target for <see cref="PlayniteUriTarget.ShowView"/>.</summary>
    public PlayniteViewTarget? View { get; }

    /// <summary>Gets the filter target for <see cref="PlayniteUriTarget.Filter"/>.</summary>
    public PlayniteFilterTarget? Filter { get; }

    /// <summary>
    /// Parses and validates an external URI. Malformed, oversized, traversal, control-character
    /// and unknown-command input is rejected without executing anything.
    /// </summary>
    public static bool TryParse(string? value, out PlayniteUriRoute? route, out string? error)
    {
        route = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The URI is empty.";
            return false;
        }

        if (value.Length > MaxLength)
        {
            error = $"The URI exceeds {MaxLength} characters.";
            return false;
        }

        foreach (var character in value)
        {
            if (character < ' ' || character == '\u007f' || (character >= '\u0080' && character <= '\u009f'))
            {
                error = "The URI contains a control character.";
                return false;
            }

            if (char.IsWhiteSpace(character))
            {
                error = "The URI contains unescaped whitespace.";
                return false;
            }
        }

        var prefix = Scheme + "://";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The URI scheme must be {Scheme}://.";
            return false;
        }

        if (value.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            error = "The URI must not contain a query, fragment or backslash.";
            return false;
        }

        var segments = value[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 1)
        {
            error = "The URI is missing a source.";
            return false;
        }

        var source = segments[0];
        if (!IsValidSourceName(source))
        {
            error = $"The URI source must be 1-{MaxSourceLength} letters, digits, '.', '_', '~' or '-'.";
            return false;
        }

        var arguments = segments.Skip(1).ToArray();
        if (arguments.Length > MaxArguments)
        {
            error = $"The URI carries more than {MaxArguments} arguments.";
            return false;
        }

        var decoded = new string[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Length > MaxArgumentLength)
            {
                error = $"Argument {index + 1} exceeds {MaxArgumentLength} characters.";
                return false;
            }

            if (!TryDecode(arguments[index], out var argument, out error))
            {
                return false;
            }

            decoded[index] = argument;
        }

        if (source.Equals(AppSource, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildAppRoute(decoded, out route, out error))
            {
                return false;
            }

            return true;
        }

        route = new PlayniteUriRoute(
            source,
            Array.AsReadOnly(decoded),
            PlayniteUriTarget.CustomSource);
        return true;
    }

    /// <summary>True when <paramref name="source"/> is a legal source name for a plugin handler.</summary>
    public static bool IsValidSourceName(string? source)
    {
        if (string.IsNullOrEmpty(source) || source.Length > MaxSourceLength || source is "." or "..")
        {
            return false;
        }

        return source.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-');
    }

    private static bool TryBuildAppRoute(
        string[] arguments,
        out PlayniteUriRoute? route,
        out string? error)
    {
        route = null;
        error = null;

        if (arguments.Length == 0)
        {
            error = "The URI is missing an application command.";
            return false;
        }

        var command = arguments[0];
        switch (command)
        {
            case "start":
                return TryBuildStart(arguments, out route, out error);

            case "showgame":
                if (arguments.Length != 2)
                {
                    error = "The showgame command requires exactly one game id.";
                    return false;
                }

                if (!Guid.TryParse(arguments[1], out var showGameId))
                {
                    error = "The showgame command requires a valid game id.";
                    return false;
                }

                route = new PlayniteUriRoute(
                    AppSource,
                    Array.AsReadOnly(arguments),
                    PlayniteUriTarget.ShowGame,
                    gameId: showGameId);
                return true;

            case "search":
                if (arguments.Length > 2)
                {
                    error = "The search command accepts at most one term.";
                    return false;
                }

                var term = arguments.Length == 2 ? arguments[1] : null;
                if (term is not null && !IsBoundedText(term, MaxSearchTermLength))
                {
                    error = $"The search term must be at most {MaxSearchTermLength} characters without control characters.";
                    return false;
                }

                route = new PlayniteUriRoute(
                    AppSource,
                    Array.AsReadOnly(arguments),
                    PlayniteUriTarget.Search,
                    searchTerm: term);
                return true;

            case "view":
                if (arguments.Length != 2)
                {
                    error = "The view command requires exactly one target.";
                    return false;
                }

                var view = arguments[1] switch
                {
                    "desktop" => PlayniteViewTarget.Desktop,
                    "fullscreen" => PlayniteViewTarget.Fullscreen,
                    _ => (PlayniteViewTarget?)null,
                };
                if (view is null)
                {
                    error = "The view target must be 'desktop' or 'fullscreen'.";
                    return false;
                }

                route = new PlayniteUriRoute(
                    AppSource,
                    Array.AsReadOnly(arguments),
                    PlayniteUriTarget.ShowView,
                    view: view);
                return true;

            case "filter":
                return TryBuildFilter(arguments, out route, out error);

            case "creatediag":
                error = "The creatediag command is not supported in v1.";
                return false;

            case "installaddon":
                error = "The installaddon command is not supported in v1.";
                return false;

            default:
                error = $"The application command '{command}' is not supported.";
                return false;
        }
    }

    private static bool TryBuildStart(
        string[] arguments,
        out PlayniteUriRoute? route,
        out string? error)
    {
        route = null;
        error = null;

        if (arguments.Length is not (2 or 3))
        {
            error = "The start command requires a game id and at most one action index.";
            return false;
        }

        if (!Guid.TryParse(arguments[1], out var gameId))
        {
            error = "The start command requires a valid game id.";
            return false;
        }

        int? actionIndex = null;
        if (arguments.Length == 3)
        {
            if (!int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed > MaxActionIndex)
            {
                error = $"The action index must be an integer between 0 and {MaxActionIndex}.";
                return false;
            }

            actionIndex = parsed;
        }

        route = new PlayniteUriRoute(
            AppSource,
            Array.AsReadOnly(arguments),
            PlayniteUriTarget.StartGame,
            gameId: gameId,
            actionIndex: actionIndex);
        return true;
    }

    private static bool TryBuildFilter(
        string[] arguments,
        out PlayniteUriRoute? route,
        out string? error)
    {
        route = null;
        error = null;

        if (arguments.Length != 3)
        {
            error = "The filter command requires a field and exactly one value.";
            return false;
        }

        if (!FilterFields.TryGetValue(arguments[1], out var field))
        {
            error = $"The filter field '{arguments[1]}' is not supported.";
            return false;
        }

        var value = arguments[2];
        if (!TryNormalizeFilterValue(field, value, out var normalized, out error))
        {
            return false;
        }

        route = new PlayniteUriRoute(
            AppSource,
            Array.AsReadOnly(arguments),
            PlayniteUriTarget.Filter,
            filter: new PlayniteFilterTarget(field, normalized));
        return true;
    }

    private static bool TryNormalizeFilterValue(
        PlayniteFilterField field,
        string value,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (field is PlayniteFilterField.Favorite or PlayniteFilterField.Installed
            or PlayniteFilterField.Uninstalled or PlayniteFilterField.Hidden)
        {
            if (value is not ("true" or "false"))
            {
                error = $"The filter field '{field}' requires 'true' or 'false'.";
                return false;
            }

            normalized = value;
            return true;
        }

        if (field == PlayniteFilterField.ReleaseYear)
        {
            if (value.Length != 4 || !value.All(char.IsAsciiDigit) || value[0] == '0')
            {
                error = "The release-year filter requires four ASCII digits.";
                return false;
            }

            normalized = value;
            return true;
        }

        if (field == PlayniteFilterField.Name)
        {
            if (!IsBoundedText(value, MaxFilterValueLength))
            {
                error = $"The name filter requires 1-{MaxFilterValueLength} characters without control characters.";
                return false;
            }

            normalized = value;
            return true;
        }

        if (!Guid.TryParse(value, out var id))
        {
            error = $"The filter field '{field}' requires a GUID value.";
            return false;
        }

        normalized = id.ToString("D");
        return true;
    }

    private static bool IsBoundedText(string value, int maximum) =>
        value.Length > 0
        && value.Length <= maximum
        && !value.Any(character =>
            character < ' ' || character == '\u007f' || (character >= '\u0080' && character <= '\u009f'));

    private static bool TryDecode(string value, out string decoded, out string? error)
    {
        decoded = string.Empty;
        error = null;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && (index + 2 >= value.Length
                || !char.IsAsciiHexDigit(value[index + 1])
                || !char.IsAsciiHexDigit(value[index + 2])))
            {
                error = "The URI contains an invalid percent escape.";
                return false;
            }
        }

        try
        {
            // Percent escapes are decoded as bytes so malformed UTF-8 fails instead of becoming
            // a replacement character; '+' keeps the v10 HttpUtility.UrlDecode space meaning.
            var bytes = new List<byte>(value.Length);
            Span<byte> encoded = stackalloc byte[4];
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '%')
                {
                    bytes.Add((byte)((HexValue(value[index + 1]) << 4) | HexValue(value[index + 2])));
                    index += 2;
                }
                else if (value[index] == '+')
                {
                    bytes.Add((byte)' ');
                }
                else
                {
                    var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed);
                    if (status != System.Buffers.OperationStatus.Done)
                    {
                        error = "The URI contains an unpaired surrogate.";
                        return false;
                    }

                    var count = rune.EncodeToUtf8(encoded);
                    bytes.AddRange(encoded[..count].ToArray());
                    index += consumed - 1;
                }
            }

            decoded = StrictUtf8.GetString(bytes.ToArray());
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException or EncoderFallbackException or ArgumentException)
        {
            error = "The URI contains invalid UTF-8 or an invalid escape.";
            return false;
        }

        if (decoded.Any(character =>
            character < ' ' || character == '\u007f' || (character >= '\u0080' && character <= '\u009f')))
        {
            error = "The URI argument decodes to a control character.";
            return false;
        }

        return true;
    }

    private static int HexValue(char character) => character <= '9'
        ? character - '0'
        : (character | 0x20) - 'a' + 10;
}

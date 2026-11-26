using Playnite.Steam.Sync;

namespace Playnite.Desktop.Shell;

/// <summary>Store source quick-filter options surfaced in the p7-004 query bar.</summary>
public enum LibrarySourceFilter
{
    All = 0,
    Steam = 1,
    Epic = 2,
    Gog = 3,
    Amazon = 4,
    Custom = 5,
    Luna = 6,
}

/// <summary>
/// Canonical Playnite v10 store plugin identifiers and safe URI/label helpers.
/// Matches BuiltInExtensions in PlayniteSDK without taking a runtime store plugin dependency.
/// </summary>
internal static class StoreCatalog
{
    public static readonly Guid SteamPluginId = new(SteamLibrarySyncService.SteamPluginId);
    public static readonly Guid EpicPluginId = Guid.Parse("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
    public static readonly Guid GogPluginId = Guid.Parse("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");
    public static readonly Guid AmazonPluginId = Guid.Parse("402674cd-4af6-4886-b6ec-0e695bfa0688");
    public static readonly Guid ItchioPluginId = Guid.Parse("00000001-ebb2-4eec-abcb-7c89937a42bb");
    public static readonly Guid BattleNetPluginId = Guid.Parse("e3c26a3d-d695-4cb7-a769-5ff7612c7edd");
    public static readonly Guid HumblePluginId = Guid.Parse("96e8c4bc-ec5c-4c8b-87e7-18ee5a690626");

    public static readonly Guid LunaPluginId = new("885cec14-5e05-432e-96ff-23bf5b1bff31");

    private const int MaxHeroicAppIdLength = 128;

    public static string GetDisplayName(Guid pluginId)
    {
        if (pluginId == SteamPluginId)
        {
            return "Steam";
        }

        if (pluginId == EpicPluginId)
        {
            return "Epic";
        }

        if (pluginId == GogPluginId)
        {
            return "GOG";
        }

        if (pluginId == AmazonPluginId)
        {
            return "Amazon";
        }

        if (pluginId == ItchioPluginId)
        {
            return "itch.io";
        }

        if (pluginId == BattleNetPluginId)
        {
            return "Battle.net";
        }

        if (pluginId == HumblePluginId)
        {
            return "Humble";
        }

        return pluginId == LunaPluginId ? "Amazon Luna" : "Library";
    }

    public static string GetFilterLabel(LibrarySourceFilter filter) => filter switch
    {
        LibrarySourceFilter.All => "All stores",
        LibrarySourceFilter.Steam => "Steam",
        LibrarySourceFilter.Epic => "Epic",
        LibrarySourceFilter.Gog => "GOG",
        LibrarySourceFilter.Amazon => "Amazon",
        LibrarySourceFilter.Luna => "Amazon Luna",
        LibrarySourceFilter.Custom => "Custom",
        _ => "All stores",
    };

    public static bool TryGetFilterPluginId(LibrarySourceFilter filter, out Guid pluginId)
    {
        switch (filter)
        {
            case LibrarySourceFilter.Steam:
                pluginId = SteamPluginId;
                return true;
            case LibrarySourceFilter.Epic:
                pluginId = EpicPluginId;
                return true;
            case LibrarySourceFilter.Gog:
                pluginId = GogPluginId;
                return true;
            case LibrarySourceFilter.Amazon:
                pluginId = AmazonPluginId;
                return true;
            case LibrarySourceFilter.Luna:
                pluginId = LunaPluginId;
                return true;
            case LibrarySourceFilter.Custom:
                pluginId = Guid.Empty;
                return true;
            default:
                pluginId = Guid.Empty;
                return false;
        }
    }

    public static bool IsHeroicStore(Guid pluginId) =>
        pluginId == EpicPluginId || pluginId == GogPluginId || pluginId == AmazonPluginId;

    public static bool TryGetHeroicRunner(Guid pluginId, out string runner)
    {
        if (pluginId == EpicPluginId)
        {
            runner = "legendary";
            return true;
        }

        if (pluginId == GogPluginId)
        {
            runner = "gog";
            return true;
        }

        if (pluginId == AmazonPluginId)
        {
            runner = "nile";
            return true;
        }

        runner = string.Empty;
        return false;
    }

    public static bool TryBuildHeroicLaunchUri(Guid pluginId, string? gameId, out string uri)
    {
        uri = string.Empty;
        if (!TryGetHeroicRunner(pluginId, out var runner) || string.IsNullOrEmpty(gameId))
        {
            return false;
        }

        if (gameId.Length > MaxHeroicAppIdLength || gameId is "." or "..")
        {
            return false;
        }

        foreach (var ch in gameId)
        {
            var allowed = ch is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '.'
                or '_'
                or '-';
            if (!allowed)
            {
                return false;
            }
        }

        uri = $"heroic://launch/{runner}/{gameId}";
        return true;
    }
}

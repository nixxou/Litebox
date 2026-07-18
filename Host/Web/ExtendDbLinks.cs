// Single source of truth for "where should a game-DB link go?" — LiteBox port of the plugin's
// Web/Backend/ExtendDbLinks.cs. The Extended DB assigns non-LaunchBox games synthetic DatabaseIDs in
// reserved RANGES, so the source site and its native id are recoverable from the id alone — no DB
// lookup needed:
//     [0, 1e6)        LaunchBox     → gamesdb.launchbox-app.com/games/dbid/{id}
//     [1e6, 2e6)      ScreenScraper → screenscraper.fr/gameinfos.php?gameid={id-1e6}
//     [2e6, 1e7)      VNDB          → vndb.org/v{id-2e6}
//     [1e7, 1e9)      Steam         → store.steampowered.com/app/{id-1e7}
//     >= 1e9          (IGDB formula; never a real game origin) → no link
//
// Consumers: the desktop Related-card click (open-site vs modal decision + the modal's link row) and
// anything else that needs to route a DatabaseID to a site. Keep every caller on these helpers so the
// range map never forks (the plugin's web theme JS mirrors the same map client-side).

#nullable enable

using LbApiHost.Host.Media;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Web;

internal static class ExtendDbLinks
{
    public const int ScreenScraperBase = 1_000_000;
    public const int VndbBase = 2_000_000;
    public const int SteamBase = 10_000_000;
    public const int IgdbBase = 1_000_000_000;

    /// <summary>A LaunchBox-origin id (real LaunchBox Games-DB id).</summary>
    public static bool IsLaunchBoxId(int dbId) => dbId > 0 && dbId < ScreenScraperBase;

    /// <summary>The correct EXTERNAL site URL for a DatabaseID, by range.
    /// Null for id &lt;= 0 or the &gt;= 1e9 range (never a real origin).</summary>
    public static string? ExternalUrl(int dbId)
    {
        if (dbId <= 0) return null;
        if (dbId < ScreenScraperBase) return $"https://gamesdb.launchbox-app.com/games/dbid/{dbId}";
        if (dbId < VndbBase) return $"https://screenscraper.fr/gameinfos.php?gameid={dbId - ScreenScraperBase}";
        if (dbId < SteamBase) return $"https://vndb.org/v{dbId - VndbBase}";
        if (dbId < IgdbBase) return $"https://store.steampowered.com/app/{dbId - SteamBase}";
        return null;
    }

    /// <summary>Display name of the site <see cref="ExternalUrl"/> points at, or null.</summary>
    public static string? SiteName(int dbId)
    {
        if (dbId <= 0) return null;
        if (dbId < ScreenScraperBase) return "LaunchBox DB";
        if (dbId < VndbBase) return "ScreenScraper";
        if (dbId < SteamBase) return "VNDB";
        if (dbId < IgdbBase) return "Steam";
        return null;
    }

    /// <summary>Can the ACTIVE site database describe <paramref name="dbId"/>? Extended DB active as
    /// main (Base module on + UseAsMainDb + file present) → any id; otherwise the native LaunchBox DB
    /// only carries LaunchBox-range ids. This is the same resolution DbRepository applies, evaluated
    /// without opening the DB — it drives the "rich modal vs straight-to-site" decision.</summary>
    public static bool ActiveDbCovers(int dbId)
    {
        if (dbId <= 0) return false;
        try
        {
            if (MediaApiBridge.ModuleActive && MetadataDb.UseExtendedAsMain) return true;
        }
        catch { }
        return IsLaunchBoxId(dbId);
    }

    /// <summary>Can the LOCAL embedded web-db page serve <paramref name="dbId"/> right now?
    /// Database site enabled + Web module on + server bound + the active DB covers the id.</summary>
    public static bool LocalWebDbCanServe(int dbId)
    {
        try
        {
            if (dbId <= 0) return false;
            if (!LbModules.On(LbModule.Web)) return false;
            if (!WebConfig.EnableDatabaseSite) return false;
            if (EmbeddedWebServer.CurrentPort <= 0) return false;
            return ActiveDbCovers(dbId);
        }
        catch { return false; }
    }

    /// <summary>The local database-site page for a game (only meaningful when
    /// <see cref="LocalWebDbCanServe"/> is true).</summary>
    public static string LocalWebDbUrl(int dbId)
        => $"http://127.0.0.1:{EmbeddedWebServer.CurrentPort}/games/{dbId}.html";
}

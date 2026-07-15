// Per-game Steam media → WebImage stand-ins (origin="steam"), so they flow through the same tile / fetch /
// download paths as the database and EmuMovies sources.
//
// Sources, combined:
//   • CONSTRUCTED from the appid (the "baked" library assets appdetails doesn't carry):
//       library_600x900_2x.jpg → Box - Front,  logo.png → Clear Logo,  library_hero.jpg → Fanart - Background
//   • appdetails (SteamApi): header_image → Banner,  background → Fanart - Background,
//       screenshots.path_full → Screenshot - Gameplay,  each movie → Video (a reconstructed direct mp4).
//
// Only for games that HAVE a Steam appid — either Source=="Steam" (steam://rungameid/{appid} in ApplicationPath)
// OR a SteamAppId that LaunchBox's metadata carries for the game's DatabaseId (so a plain "Windows" import that
// matches a Steam title in the DB qualifies too; see AppIdOf / MetadataDb.SteamAppIdForGame). No CRC/size is
// known, so Steam stand-ins aren't deduped against owned files — they always show while the source is on
// (best-effort; re-downloading just adds a numbered file).

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Integrations;

internal static class SteamCatalog
{
    /// <summary>The game's Steam appid from its ApplicationPath ("steam://rungameid/{appid}"), or null. Use the
    /// <see cref="AppIdOf(string?,int)"/> overload where a DatabaseId is available — it also honours the SteamAppId
    /// that LaunchBox's metadata carries for games that aren't launched via a steam:// URI.</summary>
    public static string? AppIdOf(string? applicationPath) => StoreSupport.SteamAppId(applicationPath);

    /// <summary>The game's Steam appid: the steam://rungameid/ appid from its ApplicationPath, else the SteamAppId
    /// LaunchBox's metadata (extended DB preferred) associates with its DatabaseId. So a non-Steam-source game that
    /// merely matches a Steam title in the DB (e.g. a plain "Windows" import) still gets the Steam source.</summary>
    public static string? AppIdOf(string? applicationPath, int dbId)
        => StoreSupport.SteamAppId(applicationPath) ?? (dbId > 0 ? MetadataDb.SteamAppIdForGame(dbId) : null);

    /// <summary>Steam media for a game as WebImage stand-ins (origin="steam"). Empty when it has no appid or the
    /// Store API returns nothing.</summary>
    public static async Task<List<MetadataDb.WebImage>> ResolveForGameAsync(int dbId, string? applicationPath, CancellationToken ct = default)
    {
        var result = new List<MetadataDb.WebImage>();
        var appid = AppIdOf(applicationPath, dbId);
        if (string.IsNullOrEmpty(appid)) return result;

        // Constructed library assets (appdetails doesn't carry the portrait box art or the logo).
        Add(result, dbId, $"{SteamApi.CdnBase}{appid}/library_600x900_2x.jpg", "Box - Front");
        Add(result, dbId, $"{SteamApi.CdnBase}{appid}/logo.png", "Clear Logo");
        Add(result, dbId, $"{SteamApi.CdnBase}{appid}/library_hero.jpg", "Fanart - Background");

        var media = await SteamApi.GetAppMediaAsync(appid, ct).ConfigureAwait(false);
        if (media != null)
        {
            if (!string.IsNullOrEmpty(media.Header)) Add(result, dbId, media.Header!, "Banner");
            if (!string.IsNullOrEmpty(media.Background)) Add(result, dbId, media.Background!, "Fanart - Background");
            foreach (var shot in media.Screenshots) Add(result, dbId, shot, "Screenshot - Gameplay");
            // Reconstruct a direct, downloadable mp4 from each movie id (the appdetails HLS is stream-only).
            foreach (var mid in media.MovieIds) Add(result, dbId, $"{SteamApi.TrailerBase}{mid}/movie_max.mp4", "Video");
        }
        return result;
    }

    private static void Add(List<MetadataDb.WebImage> list, int dbId, string url, string lbType)
    {
        string ext = ImageFileType.Extract(url);
        list.Add(new MetadataDb.WebImage(dbId, url, lbType, "World", 0, "steam", 0, ext, 0));
    }
}

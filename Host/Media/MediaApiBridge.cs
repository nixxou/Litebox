// The seam the editor / video code calls to download or enumerate a game's web media "the smart way" (the
// per-origin URL chain: Launchbox CDN, Screenscraper with credentials, Steam CDN, the extenddb mirror, …)
// instead of a naive CDN GET that only works for launchbox-origin rows.
//
// This used to bridge (by reflection) into the ExtendDB plugin's MediaApi.FetchForWizard. LiteBox now owns that
// logic natively (Host/Media/MediaFetch), so the bridge is a thin native wrapper — no plugin, no reflection.
//
// The gate is the Extended-database MODULE: the same "module OFF → LiteBox's native way, module ON → the
// ExtendDB way" contract the user asked for.
//   • Base module OFF → Available/UseWizardPath are false: callers show and naively GET only launchbox rows,
//     exactly like a LaunchBox without ExtendDB.
//   • Base module ON  → the per-origin native fetch (MediaFetch) is used for every origin. The richer merged
//     database (non-launchbox rows) is only read when it has actually been downloaded (ModuleActive), which is
//     what MetadataDb.WebDbPath guards with ExtendedDbPath != null.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Media;

internal static class MediaApiBridge
{
    /// <summary>The native per-origin fetch is enabled — i.e. the Extended-database module is on. When false,
    /// callers restrict themselves to launchbox-origin rows they can GET directly.</summary>
    public static bool Available => LbModules.On(LbModule.Base);

    /// <summary>The Extended database is genuinely in play: the module is on AND the extended DB has been
    /// downloaded (so its non-launchbox rows exist). Guards reading the merged DB over base LaunchBox's.</summary>
    public static bool ModuleActive => LbModules.On(LbModule.Base) && MetadataDb.ExtendedDbPath != null;

    /// <summary>True when downloads/previews go through the per-origin path (same meaning as the old wizard path).
    /// Equivalent to <see cref="Available"/> today; kept as a distinct name for the call sites that read it.</summary>
    public static bool UseWizardPath => LbModules.On(LbModule.Base);

    /// <summary>
    /// Fetches one row's bytes via the per-origin chain (MediaFetch.FetchBytes). Returns null on any failure.
    /// Synchronous/blocking — call it off the UI thread.
    /// </summary>
    public static byte[]? FetchBytes(MetadataDb.WebImage w, string platform)
        => MediaFetch.FetchBytes(w, platform);

    /// <summary>One playable upstream: the URL to open and the Referer the CDN gates on (null when it doesn't).</summary>
    public readonly record struct UrlCandidate(string Kind, string Url, string? Referer);

    /// <summary>
    /// The ordered upstream URLs for a row WITHOUT fetching — used to PLAY a web video instead of downloading it
    /// (e.g. a Steam trailer stored as a fake "…/movie480.m3u8.mp4" is rewritten to the real HLS manifest).
    /// Empty when the Base module is off.
    /// </summary>
    public static List<UrlCandidate> ListUrls(MetadataDb.WebImage w)
    {
        if (!Available) return new List<UrlCandidate>();
        return MediaFetch.ListUrls(w)
                         .Select(c => new UrlCandidate(c.Kind, c.Url, c.Referer))
                         .ToList();
    }
}

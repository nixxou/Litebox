// The LaunchBox → RomM library projection: what a RomM client sees when it lists this install.
//
// Reads the SAME live library the theme surfaces read (PluginHelper.DataManager — LiteBox's in-process
// HostDataManagerXml), keyed through the RommIdMap ledger so every id survives restarts. One LaunchBox
// game = one RomM rom; its playable variants become files[] later (S4) — for now each game exports its
// main ROM as the single file, under a file id S4 will keep.
//
// Visibility: "Hide in LaunchBox" games are excluded unless [RommServer] ExposeHiddenGames; the parental
// state applies per request exactly as on the theme surfaces (a RomM client never carries the unlock
// cookie, so a locked install shows a phone the same censored library the TV shows) unless
// [RommServer] IgnoreParental. An unmapped platform is not exported at all.
//
// Sorting is cached per (platform, order, dir) for a short TTL: the source arrays live in RAM, but
// re-sorting 40k titles on every 50-item page would be waste in the exact place clients hammer.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One exported platform, resolved and counted.</summary>
internal sealed class RommPlatform
{
    public int Id;
    public string LbName = "";
    public string Slug = "";
    public int RomCount;
}

internal static class RommLibrary
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    // ── Platforms ─────────────────────────────────────────────────────────────

    /// <summary>Every mapped LaunchBox platform with its visible-game count. Unmapped platforms are
    /// absent by design (a wrong slug launches the wrong emulator on the client).</summary>
    /// <param name="ignorePins">Count the games as the library HAS them, not as a client sees them.
    /// The index pass must pass true: it counts through GamesOf, whose advertisability filter answers
    /// from the index — so on a cold index every platform would count zero, be judged empty, and the
    /// index could never fill. Measured: 4 platforms out of 7 walked, 58 games out of 3058.</param>
    public static List<RommPlatform> Platforms(WebParentalState? st, int? tokenId = null,
                                               bool ignorePins = false)
    {
        var result = new List<RommPlatform>();
        IPlatform[] all;
        try { all = PluginHelper.DataManager.GetAllPlatforms() ?? Array.Empty<IPlatform>(); }
        catch (Exception ex) { LbLog.Warn("romm", "GetAllPlatforms: " + ex.Message); return result; }

        foreach (var p in all)
        {
            string name;
            try { name = p?.Name ?? ""; } catch { continue; }
            if (name.Length == 0) continue;
            if (!RommConfig.PlatformIncluded(name)) continue;
            if (st != null && st.IsHidden(name)) continue;

            var slug = RommPlatformMap.SlugFor(name);
            if (slug == null) continue;

            int count = GamesOf(name, st, tokenId, ignorePins).Count;
            if (count == 0) continue;

            result.Add(new RommPlatform
            {
                Id = RommIdMap.PlatformId(name),
                LbName = name,
                Slug = slug,
                RomCount = count,
            });
        }
        result.Sort((a, b) => string.Compare(a.LbName, b.LbName, OIC));
        return result;
    }

    /// <summary>The platform slugs the heartbeat advertises as FS_PLATFORMS.</summary>
    public static string[] PlatformSlugs()
    {
        try
        {
            var st = RommConfig.IgnoreParental ? null : WebParentalState.From(null);
            return Platforms(st).Select(p => p.Slug).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static RommPlatform? PlatformById(int id, WebParentalState? st)
        => Platforms(st).FirstOrDefault(p => p.Id == id);

    // ── Games ─────────────────────────────────────────────────────────────────

    /// <summary>The visible games of one LB platform, unsorted. Hide/broken/parental rules applied.</summary>
    /// <param name="ignorePins">Skip the "is this archive settled for this client" filter. TRUE for the
    /// two callers that must see the library as it really is: the assignment screen, and the pass that
    /// settles a newly paired client — which would otherwise be filtered out of the very games it is
    /// there to settle.</param>
    public static List<IGame> GamesOf(string lbPlatformName, WebParentalState? st, int? tokenId = null,
                                      bool ignorePins = false)
    {
        if (!RommConfig.PlatformIncluded(lbPlatformName)) return new List<IGame>();
        IPlatform? platform = null;
        try
        {
            foreach (var p in PluginHelper.DataManager.GetAllPlatforms())
                if (string.Equals(p?.Name, lbPlatformName, OIC)) { platform = p; break; }
        }
        catch (Exception ex) { LbLog.Warn("romm", "GetAllPlatforms: " + ex.Message); }
        if (platform == null) return new List<IGame>();

        IEnumerable<IGame> games;
        try { games = platform.GetAllGames(RommConfig.ExposeHiddenGames, true) ?? Enumerable.Empty<IGame>(); }
        catch (Exception ex) { LbLog.Warn("romm", "GetAllGames(" + lbPlatformName + "): " + ex.Message); return new List<IGame>(); }

        return games.Where(g => IsAllowed(g, st, tokenId, ignorePins)).ToList();
    }

    private static bool IsAllowed(IGame? g, WebParentalState? st, int? tokenId = null,
                                  bool ignorePins = false)
    {
        if (g == null) return false;

        // An archive we could not settle for this client cannot be named truthfully, and a client caches
        // the name it is given. Not advertising it is the only answer that does not lie. Memory only —
        // this runs for every row of every listing.
        if (!ignorePins)
            try { if (!RommFiles.Advertisable(g, tokenId)) return false; } catch { }

        if (st == null) return true;
        try
        {
            if (st.IsHidden(g.Platform)) return false;
            if (!st.IsRatingAllowed(g.Rating)) return false;
        }
        catch { }
        return true;
    }

    /// <summary>The game a rom_id belongs to. A rom_id names a game AND a file — this answers only the
    /// first half; RommRoms.Resolve gives both when the file matters.</summary>
    public static IGame? GameByRomId(int romId)
    {
        var row = RommIndexer.RowOf(romId);
        if (row == null) return null;
        IGame? g;
        try { g = PluginHelper.DataManager?.GetGameById(row.GuidLb); }
        catch { return null; }
        // A row minted while the platform was included stays in romm.db (assignments and pins with it),
        // but as long as the platform is out, the rom does not answer.
        return g != null && RommConfig.PlatformIncluded(PlatformOf(g)) ? g : null;
    }

    // ── Sorted-list cache ─────────────────────────────────────────────────────

    private sealed class SortedEntry
    {
        public List<IGame> Games = new();
        public DateTime BuiltUtc;
    }

    private static readonly ConcurrentDictionary<string, SortedEntry> _sorted = new(StringComparer.Ordinal);
    private static readonly TimeSpan SortTtl = TimeSpan.FromSeconds(30);

    /// <summary>The ordered result set for a roms query: all visible games (optionally one platform),
    /// filtered by search term, sorted. Cached briefly — clients page through this in 50-row bites.</summary>
    public static List<IGame> Query(int? platformId, string? searchTerm, string orderBy, string orderDir,
                                    WebParentalState? st, int? tokenId = null, bool ignorePins = false)
    {
        string platformName = "";
        if (platformId != null)
        {
            platformName = RommIdMap.PlatformNameOf(platformId.Value) ?? " none";
        }

        var key = string.Join("", platformName, searchTerm ?? "", orderBy, orderDir,
                              st == null ? "np" : (st.IsLocked ? "locked" : "open"),
                              RommConfig.ExposeHiddenGames ? "h1" : "h0",
                              // The CLIENT is part of the key: what a listing contains now depends on
                              // which archives are settled for it, so one client's result set must
                              // never be served to another.
                              ignorePins ? "all" : "c" + (tokenId?.ToString() ?? "-"));

        if (_sorted.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.BuiltUtc < SortTtl)
            return hit.Games;

        var games = new List<IGame>();
        if (platformId == null)
        {
            foreach (var p in Platforms(st))
                games.AddRange(GamesOf(p.LbName, st, tokenId, ignorePins));
        }
        else if (platformName != " none")
        {
            // Only a mapped platform is exported; an id we never allocated yields the empty list.
            if (RommPlatformMap.SlugFor(platformName) != null)
                games = GamesOf(platformName, st, tokenId, ignorePins);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var needle = searchTerm!.Trim();
            games = games.Where(g =>
            {
                try { return (g.Title ?? "").IndexOf(needle, OIC) >= 0; }
                catch { return false; }
            }).ToList();
        }

        Sort(games, orderBy, orderDir);

        _sorted[key] = new SortedEntry { Games = games, BuiltUtc = DateTime.UtcNow };
        // The cache only ever holds a handful of shapes; sweep the stale ones so a burst of odd queries
        // cannot pin dead lists.
        foreach (var k in _sorted.Where(kv => DateTime.UtcNow - kv.Value.BuiltUtc > SortTtl).Select(kv => kv.Key).ToList())
            _sorted.TryRemove(k, out _);

        return games;
    }

    private static void Sort(List<IGame> games, string orderBy, string orderDir)
    {
        Comparison<IGame> cmp = orderBy switch
        {
            "fs_size_bytes" => (a, b) => SizeOf(a).CompareTo(SizeOf(b)),
            "first_release_date" => (a, b) => Nullable.Compare(ReleaseOf(a), ReleaseOf(b)),
            "created_at" => (a, b) => AddedOf(a).CompareTo(AddedOf(b)),
            "last_played" => (a, b) => Nullable.Compare(LastPlayedOf(a), LastPlayedOf(b)),
            _ => (a, b) => string.Compare(SortNameOf(a), SortNameOf(b), OIC),
        };
        games.Sort(cmp);
        if (string.Equals(orderDir, "desc", OIC)) games.Reverse();
    }

    // ── Safe field readers (an IGame getter can throw on a torn library) ──────

    public static string TitleOf(IGame g) { try { return g.Title ?? ""; } catch { return ""; } }
    public static string SortNameOf(IGame g) { try { return g.SortTitleOrTitle ?? ""; } catch { return TitleOf(g); } }
    public static string IdOf(IGame g) { try { return g.Id ?? ""; } catch { return ""; } }
    public static string PlatformOf(IGame g) { try { return g.Platform ?? ""; } catch { return ""; } }
    public static string AppPathOf(IGame g) { try { return g.ApplicationPath ?? ""; } catch { return ""; } }
    public static DateTime AddedOf(IGame g) { try { return g.DateAdded; } catch { return DateTime.MinValue; } }
    public static DateTime ModifiedOf(IGame g) { try { return g.DateModified; } catch { return DateTime.MinValue; } }
    public static DateTime? LastPlayedOf(IGame g) { try { return g.LastPlayedDate; } catch { return null; } }
    public static DateTime? ReleaseOf(IGame g) { try { return g.ReleaseDate; } catch { return null; } }
    public static bool FavoriteOf(IGame g) { try { return g.Favorite; } catch { return false; } }
    public static bool HiddenOf(IGame g) { try { return g.Hide; } catch { return false; } }
    public static float RatingOf(IGame g) { try { return g.CommunityOrLocalStarRating; } catch { return 0f; } }
    // Via la doublure des themes web : pendant qu'un jeu tourne, la tranche Notes est larguee du
    // store, et DescriptionOf sert alors la description de LaunchBox.Extended.Metadata.db a la
    // place - un client qui synchronise en pleine partie ne doit pas encaisser un summary vide.
    public static string NotesOf(IGame g)
    { try { return LbApiHost.Host.Web.OwnedDataProvider.DescriptionOf(g) ?? ""; } catch { return ""; } }
    public static string RegionOf(IGame g) { try { return g.Region ?? ""; } catch { return ""; } }
    public static string VersionOf(IGame g) { try { return g.Version ?? ""; } catch { return ""; } }
    public static string GenresOf(IGame g) { try { return g.GenresString ?? ""; } catch { return ""; } }
    public static string DeveloperOf(IGame g) { try { return g.Developer ?? ""; } catch { return ""; } }
    public static string PublisherOf(IGame g) { try { return g.Publisher ?? ""; } catch { return ""; } }
    public static string PlayModeOf(IGame g) { try { return g.PlayMode ?? ""; } catch { return ""; } }
    public static string EsrbOf(IGame g) { try { return g.Rating ?? ""; } catch { return ""; } }
    public static int PlayCountOf(IGame g) { try { return g.PlayCount; } catch { return 0; } }
    public static string FrontImageOf(IGame g) { try { return g.FrontImagePath ?? ""; } catch { return ""; } }

    /// <summary>The main ROM's absolute path, resolved against the LB root when relative.</summary>
    public static string? RomAbsPath(IGame g)
    {
        var p = AppPathOf(g);
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            if (!Path.IsPathRooted(p))
                p = Path.GetFullPath(Path.Combine(Media.MediaResolver.LbRoot ?? AppContext.BaseDirectory, p));
            return p;
        }
        catch { return null; }
    }

    public static long SizeOf(IGame g)
    {
        var p = RomAbsPath(g);
        if (p == null) return 0;
        try { var fi = new FileInfo(p); return fi.Exists ? fi.Length : 0; } catch { return 0; }
    }

    /// <summary>The parental view for a request: null (everything visible) when the module option says to
    /// ignore the lock, else the same per-request state the theme surfaces compute.</summary>
    public static WebParentalState? Parental(HttpRequest? req)
    {
        if (RommConfig.IgnoreParental) return null;
        try { return WebParentalState.From(req); } catch { return null; }
    }
}

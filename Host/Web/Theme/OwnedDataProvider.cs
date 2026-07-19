// The data source for both theme surfaces (BigBox Web + LiteBox Web): builds the JSON contract from the user's
// REAL library, read live from PluginHelper.DataManager (LiteBox's own in-process HostDataManagerXml) + the
// in-memory GameCache for media. This is exactly what a LaunchBox/BigBox host browses — the SDK types (IGame /
// IPlatform / IPlaylist / IPlatformCategory) are real in-process, so the data reads compile as-is.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Theme/OwnedDataProvider.cs, with the plugin seams cut:
//   • media proxy URLs  → Host/Web/MediaProxy.BuildProxyUrl (was MediaResolver.BuildProxyUrl)
//   • GameCache         → Host/Gc/GameCache (was ExtendDB.GameCache; identical type/member names)
//   • store install     → Host/Web/WebStoreState over Host/StoreInstallStateSync (was StoreInstallState)
//   • parental          → Host/Web/WebParentalState (was ExtendDB WebParentalState)
//   • config            → Host/Web/WebConfig / DbRepository (was ExtendDBConfigManager)
//   • last-launch       → HostDataManagerXml.GetLastLaunch tuple (was LaunchHistoryDb)
//   • logging           → Host/Diag/LbLog
//
// DEVIATIONS from the source (documented gaps for later slices):
//   • Related / RelatedOverviews return empty — the Similar-Games engine is NOT ported to LiteBox (out of S4).
//   • launchOptions is null and the on-select RA resolve is omitted — RA/launch-menu wiring is S5/S6; Play
//     goes through HostLaunch.Launch("web", …) with the game's default emulator (see BigBoxMutationApi).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Gc;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class OwnedDataProvider
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    // BigBox semantics: hide "Hide in BigBox" games, keep broken ones.
    private const bool IncludeHidden = false;
    private const bool IncludeBroken = true;

    // ── data/cattree.json ──────────────────────────────────────────

    public static object CatTree(WebParentalState st)
    {
        var tree = new List<object>();
        var seenPlaylists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in SafeRoots())
        {
            var o = BuildNode(node, seenPlaylists, st);
            if (o != null) tree.Add(o);
        }
        var pls = BuildPlaylistsRoot(seenPlaylists, st);
        if (pls != null) tree.Add(pls);
        return tree;
    }

    private static IList<IPlatform> SafeRoots()
    {
        try { return PluginHelper.DataManager.GetRootPlatformsCategoriesPlaylists() ?? new List<IPlatform>(); }
        catch (Exception ex) { Log("GetRootPlatformsCategoriesPlaylists: " + ex.Message); return new List<IPlatform>(); }
    }

    private static object BuildNode(IPlatform node, HashSet<string> seenPlaylists, WebParentalState st)
    {
        if (node == null) return null;
        if (st != null && st.IsHidden(node.Name)) return null;

        if (node is IPlatformCategory)
        {
            var kids = new List<object>();
            IList<IPlatform> children = null;
            try { children = node.GetChildren(); } catch (Exception ex) { Log("GetChildren: " + ex.Message); }
            if (children != null)
                foreach (var c in children) { var co = BuildNode(c, seenPlaylists, st); if (co != null) kids.Add(co); }
            if (kids.Count == 0) return null;
            var catSlug = PlatformSlug.For(node.Name);
            return new
            {
                name = node.Name,
                kind = "platform",
                count = SafeCount(node),
                sub = new[] { "", "" },
                desc = ThemeFormat.OverviewHtml(SafeNotes(node)),
                media = ThemeFormat.Gradient(node.Name),
                recent = Array.Empty<string>(),
                slug = catSlug,
                path = "categories/" + catSlug,
                children = kids.ToArray(),
            };
        }

        if (node is IPlaylist plNode)
        {
            int plCount = CountAllowed(SafePlaylistGames(plNode), st);
            if (plCount <= 0) return null;
            var plSlug = PlatformSlug.For(node.Name);
            seenPlaylists.Add(node.Name);
            return new
            {
                name = node.Name,
                kind = "playlist",
                count = plCount,
                stats = new[] { "Total Games: " + plCount },
                media = ThemeFormat.Gradient(node.Name),
                recent = Array.Empty<string>(),
                slug = plSlug,
                path = "playlists/" + plSlug,
            };
        }

        int count = (st != null && st.IsLocked) ? CountAllowed(SafeGames(node), st) : SafeCount(node);
        if (count <= 0) return null;
        var slug = PlatformSlug.For(node.Name);

        string year = ""; try { var rd = node.ReleaseDate; if (rd.HasValue) year = rd.Value.Year.ToString(); } catch { }
        string manu = ""; try { manu = node.Manufacturer ?? ""; } catch { }
        return new
        {
            name = node.Name,
            kind = "platform",
            count,
            sub = new[] { year, manu },
            desc = ThemeFormat.OverviewHtml(SafeNotes(node)),
            media = ThemeFormat.Gradient(node.Name),
            recent = Array.Empty<string>(),
            slug,
            path = "platforms/" + slug,
        };
    }

    // ── data/platforms/<slug>/games.json + data/playlists/<slug>/games.json ──

    public static object PlatformGames(string slug, WebParentalState st)
    {
        var plat = ResolvePlatform(slug);
        if (plat == null) return EmptyGamesPayload();
        if (st != null && st.IsHidden(plat.Name)) return EmptyGamesPayload();
        return GamesPayload(plat, SafeGames(plat), st);
    }

    public static object PlaylistGames(string slug, WebParentalState st)
    {
        var pl = ResolvePlaylist(slug);
        if (pl == null) return EmptyGamesPayload();
        if (st != null && st.IsHidden(pl.Name)) return EmptyGamesPayload();
        return GamesPayload(pl as IPlatform, SafePlaylistGames(pl), st);
    }

    // ── data/{platforms|playlists|categories}/<slug>/recent.json ──────────

    private const int RecentCount = 8;

    public static object PlatformRecent(string slug, WebParentalState st)
    {
        var plat = ResolvePlatform(slug);
        if (plat == null) return EmptyRecent();
        if (st != null && st.IsHidden(plat.Name)) return EmptyRecent();
        return RecentPayload(SafeGames(plat), st);
    }

    public static object PlaylistRecent(string slug, WebParentalState st)
    {
        var pl = ResolvePlaylist(slug);
        if (pl == null) return EmptyRecent();
        if (st != null && st.IsHidden(pl.Name)) return EmptyRecent();
        return RecentPayload(SafePlaylistGames(pl), st);
    }

    public static object CategoryRecent(string slug, WebParentalState st)
    {
        var cat = ResolveCategory(slug);
        if (cat == null) return EmptyRecent();
        if (st != null && st.IsHidden(cat.Name)) return EmptyRecent();
        return RecentPayload(CategoryGames(cat, st), st);
    }

    public static object EmptyRecent() => new { recent = Array.Empty<object>() };

    private static object RecentPayload(IEnumerable<IGame> games, WebParentalState st)
    {
        WebStoreState.EnsureFresh();
        var allowed = games.Where(g => g != null && Allowed(g, st)).ToList();
        var picked = allowed.Where(g => SafeLastPlayed(g) != null)
                            .OrderByDescending(g => SafeLastPlayed(g).Value)
                            .Take(RecentCount).ToList();
        if (picked.Count < RecentCount)
        {
            var have = new HashSet<string>(picked.Select(g => Safe(() => g.Id)), StringComparer.OrdinalIgnoreCase);
            picked.AddRange(allowed.Where(g => !have.Contains(Safe(() => g.Id)))
                                   .OrderByDescending(SafeDateAdded)
                                   .Take(RecentCount - picked.Count));
        }
        return new { recent = picked.Select(LightItem).ToArray() };
    }

    private static DateTime? SafeLastPlayed(IGame g) { try { return g.LastPlayedDate; } catch { return null; } }
    private static DateTime SafeDateAdded(IGame g) { try { return g.DateAdded; } catch { return DateTime.MinValue; } }

    // ── data/{platforms|playlists|categories}/<slug>/catmedia.json ─────────

    private static readonly string[] DefaultCatMediaOrder =
        { "platformvideo", "platformbackground", "randomgamevideo", "randomgamebackground" };

    public static object EmptyCatMedia() => new { };

    public static object PlatformCatMedia(string slug, string order, WebParentalState st)
    {
        var p = ResolvePlatform(slug);
        if (p == null || (st != null && st.IsHidden(p.Name))) return EmptyCatMedia();
        return ResolveCatMedia(p, SafeGames(p), order, st);
    }

    public static object PlaylistCatMedia(string slug, string order, WebParentalState st)
    {
        var pl = ResolvePlaylist(slug);
        if (pl == null || (st != null && st.IsHidden(pl.Name))) return EmptyCatMedia();
        return ResolveCatMedia(pl as IPlatform, SafePlaylistGames(pl), order, st);
    }

    public static object CategoryCatMedia(string slug, string order, WebParentalState st)
    {
        var c = ResolveCategory(slug);
        if (c == null || (st != null && st.IsHidden(c.Name))) return EmptyCatMedia();
        return ResolveCatMedia(c, CategoryGames(c, st), order, st);
    }

    private static object ResolveCatMedia(IPlatform plat, IEnumerable<IGame> games, string order, WebParentalState st)
    {
        var tiers = string.IsNullOrEmpty(order)
            ? DefaultCatMediaOrder
            : order.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToArray();

        List<IGame> allowed = null;
        foreach (var tier in tiers)
        {
            string url;
            switch (tier)
            {
                case "platformvideo":
                    var pv = SafePlatformVideo(plat);
                    if (FileOk(pv)) return CatMedia("video", pv, "Video");
                    break;
                case "platformbackground":
                    var pb = SafePlatformBg(plat);
                    if (FileOk(pb)) return CatMedia("image", pb, "Background");
                    break;
                case "randomgamevideo":
                    allowed ??= games.Where(g => g != null && Allowed(g, st)).ToList();
                    url = RandomGameMedia(allowed, wantVideo: true);
                    if (url != null) return new { type = "video", url };
                    break;
                case "randomgamebackground":
                    allowed ??= games.Where(g => g != null && Allowed(g, st)).ToList();
                    url = RandomGameMedia(allowed, wantVideo: false);
                    if (url != null) return new { type = "image", url };
                    break;
            }
        }
        return EmptyCatMedia();
    }

    private static object CatMedia(string type, string path, string label)
        => new { type, url = MediaProxy.BuildProxyUrl(path, null, 0, ExtOf(path), "local", label) };

    private static string SafePlatformVideo(IPlatform p) { try { return p?.GetPlatformVideoPath(false, false); } catch { return null; } }
    private static string SafePlatformBg(IPlatform p) { try { return p?.BackgroundImagePath; } catch { return null; } }
    private static bool FileOk(string path) { try { return !string.IsNullOrEmpty(path) && File.Exists(path); } catch { return false; } }

    private static string RandomGameMedia(List<IGame> allowed, bool wantVideo)
    {
        if (allowed == null || allowed.Count == 0) return null;
        var hits = new List<GameCacheGame>();
        foreach (var g in allowed)
        {
            var cg = ResolveCacheGame(g);
            if (cg == null) continue;
            if (wantVideo) { if (cg.HasAnyVideo()) hits.Add(cg); }
            else { GameCacheImageRef r = null; try { r = cg.GetBestImageTypeFirst("Background"); } catch { } if (r != null && !string.IsNullOrEmpty(r.FullPath)) hits.Add(cg); }
        }
        if (hits.Count == 0) return null;
        var cgPick = hits[new Random().Next(hits.Count)];
        string path = null;
        if (wantVideo) { List<GameCacheVideoRef> v = null; try { v = cgPick.FindAllVideos(); } catch { } if (v != null && v.Count > 0) path = v[0].FullPath; }
        else { GameCacheImageRef r = null; try { r = cgPick.GetBestImageTypeFirst("Background"); } catch { } if (r != null) path = r.FullPath; }
        if (string.IsNullOrEmpty(path)) return null;
        return MediaProxy.BuildProxyUrl(path, null, 0, ExtOf(path), "local", wantVideo ? "Video" : "Background");
    }

    private static object BuildPlaylistsRoot(HashSet<string> seenPlaylists, WebParentalState st)
    {
        IEnumerable<IPlaylist> pls;
        try { pls = PluginHelper.DataManager.GetAllPlaylists() ?? Enumerable.Empty<IPlaylist>(); }
        catch (Exception ex) { Log("GetAllPlaylists: " + ex.Message); return null; }

        var nodes = new List<object>();
        int total = 0;
        foreach (var pl in pls)
        {
            if (pl?.Name == null) continue;
            if (seenPlaylists.Contains(pl.Name)) continue;
            if (st != null && st.IsHidden(pl.Name)) continue;
            int count = CountAllowed(SafePlaylistGames(pl), st);
            if (count <= 0) continue;
            total += count;
            nodes.Add(new
            {
                name = pl.Name,
                kind = "playlist",
                count,
                stats = new[] { "Total Games: " + count },
                media = ThemeFormat.Gradient(pl.Name),
                recent = Array.Empty<string>(),
                path = "playlists/" + PlatformSlug.For(pl.Name),
            });
        }
        if (nodes.Count == 0) return null;
        return new
        {
            name = "Playlists",
            kind = "platform",
            count = total,
            sub = new[] { "", "" },
            desc = "",
            media = ThemeFormat.Gradient("Playlists"),
            recent = Array.Empty<string>(),
            children = nodes.ToArray(),
        };
    }

    private static IEnumerable<IGame> SafePlaylistGames(IPlaylist pl)
    {
        try { return pl.GetAllGames(true) ?? Enumerable.Empty<IGame>(); }
        catch (Exception ex) { Log("Playlist.GetAllGames(" + pl?.Name + "): " + ex.Message); return Enumerable.Empty<IGame>(); }
    }

    private static object GamesPayload(IPlatform plat, IEnumerable<IGame> games, WebParentalState st)
    {
        WebStoreState.EnsureFresh();
        var items = games.Where(g => g != null && Allowed(g, st)).Select(LightItem).ToArray();
        var name = (plat != null) ? plat.Name : "";
        return new { platform = name, platformLogo = name, platformLogoImg = PlatformLogoUrl(plat), platformTotal = items.Length, games = items };
    }

    private static readonly ConcurrentDictionary<string, string> _clearLogoPaths = new();

    private static string PlatformLogoUrl(IPlatform p)
    {
        var name = p?.Name;
        if (string.IsNullOrEmpty(name)) return null;
        var path = _clearLogoPaths.GetOrAdd(name, _ =>
        {
            var cl = SafeClearLogo(p);
            return FileOk(cl) ? cl : "";
        });
        if (string.IsNullOrEmpty(path)) return null;
        return MediaProxy.BuildProxyUrl(path, null, 0, ExtOf(path), "local", "Clear Logo");
    }
    private static string SafeClearLogo(IPlatform p) { try { return p?.ClearLogoImagePath; } catch { return null; } }

    private static bool Allowed(IGame g, WebParentalState st)
        => st == null || st.IsRatingAllowed(Safe(() => g.Rating));

    private static int CountAllowed(IEnumerable<IGame> games, WebParentalState st)
    {
        int n = 0;
        foreach (var g in games) if (g != null && Allowed(g, st)) n++;
        return n;
    }

    private static object EmptyGamesPayload()
        => new { platform = "", platformLogo = "", platformLogoImg = (string)null, platformTotal = 0, games = Array.Empty<object>() };

    private static object LightItem(IGame gm)
    {
        var cg = ResolveCacheGame(gm);
        return new
        {
            id = gm.Id,
            t = gm.Title,
            y = ThemeFormat.YearStr(EffYear(gm)),
            dev = Safe(() => gm.Developer),
            pub = Safe(() => gm.Publisher),
            g = Safe(() => gm.GenresString),
            r = ThemeFormat.RatingStr(SafeRating(gm)),
            ur = SafeEffRating(gm),
            lp = SafeLastPlayedMs(gm),
            esrb = Safe(() => gm.Rating),
            rt = Safe(() => gm.ReleaseType),
            file = SafeFileName(gm),
            box = ThemeFormat.BoxLines(gm.Title),
            dbId = SafeIntN(() => gm.LaunchBoxDbId),
            fav = SafeBool(() => gm.Favorite),
            broken = SafeBool(() => gm.Broken),
            completed = SafeBool(() => gm.Completed),
            installed = WebStoreState.IsInstalledOrPresent(gm),
            store = WebStoreState.StoreLabel(gm),
            portable = SafeBool(() => gm.Portable),
            thumb = ThumbProxy(cg, "Front"),
            shotThumb = ThumbProxy(cg, "Screenshots", "Background"),
            logo = LogoProxy(cg),
        };
    }

    // ── data/games/<id>/installstate.json ──────────────────────────
    public static object InstallStateOf(string id)
    {
        WebStoreState.EnsureFresh();
        var game = SafeGetGame(id);
        if (game == null) return null;
        return new
        {
            id = game.Id,
            store = WebStoreState.StoreLabel(game),
            installed = WebStoreState.IsInstalledOrPresent(game),
            epoch = WebStoreState.Epoch,
        };
    }

    // ── data/games/<id>/detail.json ────────────────────────────────

    public static object GameDetail(string id, WebParentalState st, bool extraShots = false)
    {
        WebStoreState.EnsureFresh();
        var game = SafeGetGame(id);
        if (game == null) return null;
        if (st != null && !Allowed(game, st)) return null;

        var cg = ResolveCacheGame(game);
        int votes = 0; try { votes = game.CommunityStarRatingTotalVotes; } catch { }

        int lbId = SafeInt(() => game.LaunchBoxDbId);
        bool webdbOn = false;
        try { webdbOn = WebConfig.EnableDatabaseSite; } catch { }
        bool extOn = false;
        try { extOn = DbRepository.ExtendedDbReady(); } catch { }

        return new
        {
            id = game.Id,
            lbdbid = lbId,
            webdb = webdbOn,
            extdb = extOn,
            store = WebStoreState.StoreLabel(game),
            installed = WebStoreState.IsInstalledOrPresent(game),
            t = game.Title,
            y = ThemeFormat.YearStr(EffYear(game)),
            dev = Safe(() => game.Developer),
            pub = Safe(() => game.Publisher),
            g = Safe(() => game.GenresString),
            r = ThemeFormat.RatingStr(SafeRating(game)),
            esrb = Safe(() => game.Rating),
            box = ThemeFormat.BoxLines(game.Title),
            d = ThemeFormat.OverviewHtml(Safe(() => game.Notes)),
            boxImg = WithFull(ImageProxy(cg, "Front")),
            shotImg = WithFull(ImageProxy(cg, "Screenshots", "Background")),
            video = WithFull(VideoProxy(cg)),
            shots = extraShots ? ExtraScreenshotThumbs(cg, 12) : ScreenshotThumbs(cg, 8),
            fanart = FanartList(cg, 12),
            vndb = VndbTags(game),
            votes,
            // S6: alt-emulator / multi-disc / Select-ROM launch menu — versions/emulators/archive flags.
            launchOptions = WebLaunchOptions.Build(game),
            lastLaunch = LastLaunchDto(game),
            // S5: RetroAchievements progress + per-achievement badges from Host/Ra (null when RA is
            // unconfigured / the game has no raid / nothing cached — theme then shows no RA panel).
            ra = WebRa.Block(game),
        };
    }

    /// <summary>Last-launch memory (emulator + additional-app the game was last played with), or null.
    /// Sourced from LiteBox's own launch journal via the data manager (was ExtendDB's LaunchHistoryDb).</summary>
    private static object LastLaunchDto(IGame game)
    {
        try
        {
            var id = Safe(() => game.Id);
            if (string.IsNullOrEmpty(id)) return null;
            if (PluginHelper.DataManager is HostDataManagerXml hdm)
            {
                var last = hdm.GetLastLaunchFull(id);
                if (last == null) return null;
                return new
                {
                    appId = last.Value.additionalAppId,
                    emulatorId = last.Value.emulatorId,
                    // In-archive entry that was launched → pre-selects the ROM button (null when the last launch wasn't an archive launch).
                    archiveEntry = string.IsNullOrEmpty(last.Value.extractedRomPath) ? (string)null : last.Value.extractedRomPath,
                    ts = (long?)null,
                };
            }
            return null;
        }
        catch { return null; }
    }

    private static object VndbTags(IGame game)
    {
        var genres = Safe(() => game.GenresString);
        if (string.IsNullOrEmpty(genres)) return null;
        var cont = new List<string>(); var tech = new List<string>(); var ero = new List<string>();
        foreach (var part in genres.Split(';'))
        {
            var s = part.Trim();
            if (s.StartsWith("vndb-cont", OIC)) cont.Add(s.Substring(9).Trim().TrimStart('/').Trim());
            else if (s.StartsWith("vndb-tech", OIC)) tech.Add(s.Substring(9).Trim().TrimStart('/').Trim());
            else if (s.StartsWith("vndb-ero", OIC)) ero.Add(s.Substring(8).Trim().TrimStart('/').Trim());
        }
        if (cont.Count == 0 && tech.Count == 0 && ero.Count == 0) return null;
        return new { cont = cont.ToArray(), tech = tech.ToArray(), ero = ero.ToArray() };
    }

    // ── data/games/<id>/related.json ───────────────────────────────
    // Drives the native LiteBox suggester engine (Host/Similar/GameSuggester) via RelatedProvider.

    public static object Related(string id, WebParentalState st, int limit) => RelatedProvider.Related(id, st, limit);

    // ── Resolution helpers (SDK data manager) ──────────────────────

    public static string PlatformNameForSlug(string slug) => ResolvePlatform(slug)?.Name;

    private static IPlatform ResolvePlatform(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        try
        {
            foreach (var p in PluginHelper.DataManager.GetAllPlatforms())
                if (p?.Name != null && string.Equals(PlatformSlug.For(p.Name), slug, OIC)) return p;
        }
        catch (Exception ex) { Log("GetAllPlatforms: " + ex.Message); }
        return null;
    }

    private static IPlaylist ResolvePlaylist(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        try
        {
            foreach (var p in PluginHelper.DataManager.GetAllPlaylists())
                if (p?.Name != null && string.Equals(PlatformSlug.For(p.Name), slug, OIC)) return p;
        }
        catch (Exception ex) { Log("GetAllPlaylists: " + ex.Message); }
        return null;
    }

    private static IPlatform ResolveCategory(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        try
        {
            foreach (var c in PluginHelper.DataManager.GetAllPlatformCategories())
                if (c?.Name != null && string.Equals(PlatformSlug.For(c.Name), slug, OIC)) return c as IPlatform;
        }
        catch (Exception ex) { Log("GetAllPlatformCategories: " + ex.Message); }
        return null;
    }

    private static IEnumerable<IGame> CategoryGames(IPlatform cat, WebParentalState st)
    {
        var acc = new List<IGame>();
        CollectGames(cat, st, acc, 0);
        return acc;
    }

    private static void CollectGames(IPlatform node, WebParentalState st, List<IGame> acc, int depth)
    {
        if (node == null || depth > 6) return;
        if (st != null && st.IsHidden(node.Name)) return;
        if (node is IPlatformCategory)
        {
            IList<IPlatform> children = null;
            try { children = node.GetChildren(); } catch (Exception ex) { Log("CollectGames.GetChildren: " + ex.Message); }
            if (children != null) foreach (var c in children) CollectGames(c, st, acc, depth + 1);
        }
        else if (node is IPlaylist pl) { acc.AddRange(SafePlaylistGames(pl)); }
        else { acc.AddRange(SafeGames(node)); }
    }

    private static IGame SafeGetGame(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try { return PluginHelper.DataManager.GetGameById(id); }
        catch (Exception ex) { Log("GetGameById(" + id + "): " + ex.Message); return null; }
    }

    private static IEnumerable<IGame> SafeGames(IPlatform p)
    {
        try { return p.GetAllGames(IncludeHidden, IncludeBroken) ?? Enumerable.Empty<IGame>(); }
        catch (Exception ex) { Log("GetAllGames(" + (p?.Name) + "): " + ex.Message); return Enumerable.Empty<IGame>(); }
    }

    private static int SafeCount(IPlatform node)
    {
        try { return node.GetGameCount(IncludeHidden, IncludeBroken); }
        catch { return 0; }
    }

    private static string SafeNotes(IPlatform node)
    {
        try { return node.Notes; } catch { return null; }
    }

    // ── Media (GameCache → signed proxy URL) ───────────────────────

    private static GameCacheGame ResolveCacheGame(IGame game)
    {
        if (game == null || !GameCache.IsGlobalReady) return null;
        string plat; try { plat = game.Platform; } catch { return null; }
        if (string.IsNullOrEmpty(plat)) return null;
        if (GameCache.Platforms == null || !GameCache.Platforms.TryGetValue(plat, out var p) || p == null) return null;
        if (!Guid.TryParse(game.Id, out var gid)) return null;
        try { p.GamesByUUID.TryGetValue(gid, out var cg); return cg; }
        catch { return null; }
    }

    /// <summary>Related-cards helper: the disk-cache-aware thumb proxy URL for an OWNED game's box front
    /// (`?q=thumb` signed token — the same pipeline the grid cards use), or null when GameCache doesn't
    /// know the game (platform mismatch / cache not rebuilt yet). RelatedProvider falls back to the
    /// numeric /api/media/{dbid}.jpg endpoint in that case, exactly like the plugin did.</summary>
    internal static string RelatedLocalThumb(IGame game) => ThumbProxy(ResolveCacheGame(game), "Front");

    private static string ThumbProxy(GameCacheGame cg, params string[] regroupements)
    {
        if (cg == null) return null;
        foreach (var rg in regroupements)
        {
            GameCacheImageRef r = null;
            try { r = cg.GetBestImageTypeFirst(rg); } catch { }
            if (r != null && !string.IsNullOrEmpty(r.FullPath))
            {
                long size = 0; try { size = r.GetFileSize(); } catch { }
                var url = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", rg);
                if (url != null) return url + "?q=thumb&v=" + size;
            }
        }
        return null;
    }

    private static string LogoProxy(GameCacheGame cg)
    {
        if (cg == null) return null;
        GameCacheImageRef r = null;
        try { r = cg.GetBestImageTypeFirst("ClearLogo"); } catch { }
        if (r == null || string.IsNullOrEmpty(r.FullPath)) return null;
        long size = 0; try { size = r.GetFileSize(); } catch { }
        var url = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", "Clear Logo");
        return url != null ? url + "?q=logo&v=" + size : null;
    }

    private static string WithFull(string url) => string.IsNullOrEmpty(url) ? url : url + "?q=full";

    private static string[] ScreenshotThumbs(GameCacheGame cg, int max)
    {
        if (cg == null) return Array.Empty<string>();
        List<GameCacheImageRef> imgs;
        try { imgs = cg.GetAllImagesTypeFirst("Screenshots", max); }
        catch (Exception ex) { Log("GetAllImagesTypeFirst: " + ex.Message); return Array.Empty<string>(); }
        if (imgs == null || imgs.Count == 0) return Array.Empty<string>();

        var urls = new List<string>(imgs.Count);
        foreach (var r in imgs)
        {
            var u = ThumbUrlOf(r, "Screenshots");
            if (u != null) urls.Add(u);
        }
        return urls.ToArray();
    }

    private static string ThumbUrlOf(GameCacheImageRef r, string label)
    {
        if (r == null || string.IsNullOrEmpty(r.FullPath)) return null;
        long size = 0; try { size = r.GetFileSize(); } catch { }
        var url = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", label);
        return url != null ? url + "?q=thumb&v=" + size : null;
    }

    private const int ExtraShotTarget = 6;

    private static string[] ExtraScreenshotThumbs(GameCacheGame cg, int max)
    {
        if (cg == null) return Array.Empty<string>();
        List<GameCacheImageRef> shots;
        try { shots = cg.GetAllImagesTypeFirst("Screenshots", max); }
        catch (Exception ex) { Log("Extra GetAllImagesTypeFirst: " + ex.Message); shots = null; }
        shots ??= new List<GameCacheImageRef>();

        var ordered = new List<GameCacheImageRef>();
        if (shots.Count > 0) ordered.Add(shots[0]);
        GameCacheImageRef title = null, box = null;
        try { title = cg.GetBestImageOfType("Screenshot - Game Title"); } catch { }
        if (title != null) ordered.Add(title);
        try { box = cg.GetBestImageOfType("Box - Front") ?? cg.GetBestImageOfType("Fanart - Box - Front"); } catch { }
        if (box != null) ordered.Add(box);
        for (int i = 1; i < shots.Count; i++) ordered.Add(shots[i]);

        var urls = new List<string>(ordered.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(GameCacheImageRef r)
        {
            if (r == null || string.IsNullOrEmpty(r.FullPath) || !seen.Add(r.FullPath)) return;
            var u = ThumbUrlOf(r, "Screenshots");
            if (u != null) urls.Add(u);
        }
        foreach (var r in ordered) { Add(r); if (urls.Count >= max) break; }

        if (urls.Count < ExtraShotTarget)
        {
            List<GameCacheImageRef> more = null;
            try { more = cg.GetAllImagesOfType("Screenshot - Gameplay", ExtraShotTarget + ordered.Count); }
            catch (Exception ex) { Log("Extra gameplay top-up: " + ex.Message); }
            if (more != null)
                foreach (var r in more) { if (urls.Count >= ExtraShotTarget) break; Add(r); }
        }
        return urls.ToArray();
    }

    private static string[] FanartList(GameCacheGame cg, int max)
    {
        if (cg == null) return Array.Empty<string>();
        List<GameCacheImageRef> imgs;
        try { imgs = cg.GetAllImagesTypeFirst("Background", max); }
        catch (Exception ex) { Log("Fanart GetAllImagesTypeFirst: " + ex.Message); return Array.Empty<string>(); }
        if (imgs == null || imgs.Count == 0) return Array.Empty<string>();

        var urls = new List<string>(imgs.Count);
        foreach (var r in imgs)
        {
            if (r == null || string.IsNullOrEmpty(r.FullPath)) continue;
            var url = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", "Background");
            if (url != null) urls.Add(WithFull(url));
        }
        return urls.ToArray();
    }

    private static string ImageProxy(GameCacheGame cg, params string[] regroupements)
    {
        if (cg == null) return null;
        foreach (var rg in regroupements)
        {
            GameCacheImageRef r = null;
            try { r = cg.GetBestImageTypeFirst(rg); } catch { }
            if (r != null && !string.IsNullOrEmpty(r.FullPath))
                return MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", rg);
        }
        return null;
    }

    private static string VideoProxy(GameCacheGame cg)
    {
        if (cg == null) return null;
        List<GameCacheVideoRef> vids = null;
        try { vids = cg.FindAllVideos(); } catch { }
        if (vids == null || vids.Count == 0) return null;
        var v = vids[0];
        if (string.IsNullOrEmpty(v.FullPath)) return null;
        return MediaProxy.BuildProxyUrl(v.FullPath, null, 0, ExtOf(v.FullPath), "local", "Video");
    }

    private static string ExtOf(string path)
    {
        var e = Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(e) ? "jpg" : e;
    }

    // ── Field helpers ──────────────────────────────────────────────

    private static int? EffYear(IGame g)
    {
        try { if (g.ReleaseYear.HasValue && g.ReleaseYear.Value > 1950 && g.ReleaseYear.Value < 2100) return g.ReleaseYear; } catch { }
        try { var d = g.ReleaseDate; if (d.HasValue && d.Value.Year > 1950 && d.Value.Year < 2100) return d.Value.Year; } catch { }
        return null;
    }

    private static double? SafeRating(IGame g)
    {
        try { double v = g.CommunityStarRating; return v > 0 ? v : (double?)null; }
        catch { return null; }
    }

    private static double SafeEffRating(IGame g)
    {
        try { double u = g.StarRatingFloat; if (u > 0) return u; } catch { }
        try { int ui = g.StarRating; if (ui > 0) return (double)ui; } catch { }
        return 0;
    }

    private static readonly DateTime _epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static double SafeLastPlayedMs(IGame g)
    {
        var d = SafeLastPlayed(g);
        if (d == null) return 0;
        try { return (d.Value.ToUniversalTime() - _epoch).TotalMilliseconds; } catch { return 0; }
    }

    private static string SafeFileName(IGame g)
    {
        try { var p = g.ApplicationPath; return string.IsNullOrEmpty(p) ? "" : Path.GetFileName(p); }
        catch { return ""; }
    }

    private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static int SafeInt(Func<int?> f) { try { return f() ?? 0; } catch { return 0; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static int? SafeIntN(Func<int?> f) { try { return f(); } catch { return null; } }

    private static void Log(string msg) => LbLog.Info("web", "[theme] " + msg);
}

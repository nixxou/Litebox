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
    // Changes at every LiteBox process start. Web clients use it to scope their
    // browser-local Arrange By state to this execution instead of persisting it.
    private static readonly string SortSessionId = Guid.NewGuid().ToString("N");
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
                name = Data.HostPlatformCategory.NodeDisplayName(node),
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

        // name = ce qu'on LIT (nom imbriqué quand il existe) ; slug, media et path restent bâtis sur
        // le nom UNIQUE, qui est l'identité — un slug tiré du nom court ne résoudrait plus rien.
        if (node is IPlaylist plNode)
        {
            int plCount = CountAllowed(SafePlaylistGames(plNode), st);
            if (plCount <= 0) return null;
            var plSlug = PlatformSlug.For(node.Name);
            seenPlaylists.Add(node.Name);
            return new
            {
                name = Data.HostPlatformCategory.NodeDisplayName(node),
                kind = "playlist",
                count = plCount,
                stats = new[] { "Total Games: " + plCount },
                media = ThemeFormat.Gradient(node.Name),
                recent = Array.Empty<string>(),
                slug = plSlug,
                path = "playlists/" + plSlug,
            };
        }

        // BigBox rule (faithful): when at least ONE PLAYLIST is nested under a platform, the platform
        // renders AS A CATEGORY in bb-web — its children (playlists + nested categories), NOT its games.
        // lb-web keeps "click the platform = its FULL games" (path stays platforms/<slug>; its front
        // renders the children as indented sub-rows with an expand chevron).
        IList<IPlatform> platKids = null;
        try { platKids = node.GetChildren(); } catch (Exception ex) { Log("Platform.GetChildren: " + ex.Message); }
        bool hasPlaylistChild = false;
        if (platKids != null)
            foreach (var c in platKids) { if (c is IPlaylist) { hasPlaylistChild = true; break; } }
        if (hasPlaylistChild)
        {
            var kids = new List<object>();
            foreach (var c in platKids) { var co = BuildNode(c, seenPlaylists, st); if (co != null) kids.Add(co); }
            if (kids.Count == 0) return null;
            var pSlug = PlatformSlug.For(node.Name);
            return new
            {
                name = Data.HostPlatformCategory.NodeDisplayName(node),
                kind = "platform",
                count = SafeCount(node),
                sub = new[] { "", "" },
                desc = ThemeFormat.OverviewHtml(SafeNotes(node)),
                media = ThemeFormat.Gradient(node.Name),
                recent = Array.Empty<string>(),
                slug = pSlug,
                path = "platforms/" + pSlug,
                children = kids.ToArray(),
            };
        }

        int count = (st != null && st.IsLocked) ? CountAllowed(SafeGames(node), st) : SafeCount(node);
        if (count <= 0) return null;
        var slug = PlatformSlug.For(node.Name);

        string year = ""; try { var rd = node.ReleaseDate; if (rd.HasValue) year = rd.Value.Year.ToString(); } catch { }
        string manu = ""; try { manu = node.Manufacturer ?? ""; } catch { }
        return new
        {
            name = Data.HostPlatformCategory.NodeDisplayName(node),
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
        return GamesPayload(plat, SafeGames(plat), st, null);
    }

    public static object PlaylistGames(string slug, WebParentalState st)
    {
        var pl = ResolvePlaylist(slug);
        if (pl == null) return EmptyGamesPayload();
        if (st != null && st.IsHidden(pl.Name)) return EmptyGamesPayload();
        return GamesPayload(null, SafePlaylistGames(pl), st, pl);
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
        var titleSortMode = TitleSortNormalizer.ConfiguredMode();
        return new { recent = picked.Select(g => LightItem(g, titleSortMode)).ToArray() };
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
                name = Data.HostPlatformCategory.NodeDisplayName(pl),
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

    private static object GamesPayload(IPlatform plat, IEnumerable<IGame> games, WebParentalState st, IPlaylist playlist)
    {
        WebStoreState.EnsureFresh();
        ResetPadNames();   // le catalogue de manettes est relu une fois par payload, pas par jeu
        var titleSortMode = TitleSortNormalizer.ConfiguredMode();
        var gameArray = games.Where(g => g != null && Allowed(g, st)).ToArray();
        // DENSE ranks, not the raw ManualOrder: LaunchBox writes <ManualOrder>0</ManualOrder> on
        // every row of a manual playlist and lets the document order carry the real sequence.
        // ManualRanks resolves that the same way the desktop does, so both surfaces get unique,
        // directly comparable keys instead of a wall of zeroes to break ties over.
        var manual = playlist != null
            ? GameSortCatalog.ManualRanks(SafeValue(() => playlist.GetAllPlaylistGames()))
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = gameArray.Select(g => LightItem(g, titleSortMode,
            manual.TryGetValue(Safe(() => g.Id) ?? "", out var order) ? order : (int?)null)).ToArray();
        var name = playlist != null ? Safe(() => playlist.Name) ?? "" : (plat != null ? plat.Name : "");
        bool autoPopulate = playlist != null && SafeValue(() => playlist.AutoPopulate);
        string sortBy = playlist == null ? "Default" : (Safe(() => playlist.SortBy) ?? "Default");
        return new
        {
            platform = name,
            platformLogo = name,
            platformLogoImg = PlatformLogoUrl(plat),
            platformTotal = items.Length,
            nodeKind = playlist == null ? "platform" : "playlist",
            // One SortBy for the three surfaces: the desktop, LB-Web and BB-Web all read this and
            // all apply it ascending. No BigBox-specific override — see HostPlaylist.Modeled.
            sortBy,
            autoPopulate,
            manualAvailable = playlist != null && !autoPopulate,
            sortSessionId = SortSessionId,
            // Keep Arrange By complete and stable while browsing between nodes.
            customSorts = GameSortCatalog.CustomFieldNames(
                SafeValue(() => PluginHelper.DataManager.GetAllGames()) ?? Array.Empty<IGame>()),
            games = items,
        };
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
    {
        if (st == null) return true;
        // Per-game "requires parental rights" flag — hidden from a locked client, on top of the rating rules.
        if (st.IsLocked && LbApiHost.Host.Parental.ParentalGameFlag.IsBlocked(Safe(() => g.Id))) return false;
        // Hide not-installed games (Installed=false) from a locked client when the option is on (default).
        if (st.IsLocked && LbApiHost.Host.Parental.ParentalConfig.Instance.HideUninstalled && SafeBoolN(() => g.Installed) == false) return false;
        return st.IsRatingAllowed(Safe(() => g.Rating));
    }

    private static int CountAllowed(IEnumerable<IGame> games, WebParentalState st)
    {
        int n = 0;
        foreach (var g in games) if (g != null && Allowed(g, st)) n++;
        return n;
    }

    private static object EmptyGamesPayload()
        => new
        {
            platform = "", platformLogo = "", platformLogoImg = (string)null, platformTotal = 0,
            nodeKind = "platform", sortBy = "Default", autoPopulate = false,
            manualAvailable = false, sortSessionId = SortSessionId,
            customSorts = Array.Empty<string>(), games = Array.Empty<object>(),
        };

    private static object LightItem(IGame gm, TitleSortNormalization titleSortMode, int? manualOrder = null)
    {
        var cg = ResolveCacheGame(gm);
        return new
        {
            id = gm.Id,
            t = gm.Title,
            cn = TitleSortNormalizer.Normalize(gm, titleSortMode),
            y = ThemeFormat.YearStr(EffYear(gm)),
            dev = Safe(() => gm.Developer),
            pub = Safe(() => gm.Publisher),
            g = Safe(() => gm.GenresString),
            platform = Safe(() => gm.Platform),
            r = ThemeFormat.RatingStr(SafeRating(gm)),
            ur = SafeEffRating(gm),
            community = SafeValue(() => gm.CommunityStarRating),
            votes = SafeInt(() => gm.CommunityStarRatingTotalVotes),
            lp = SafeLastPlayedMs(gm),
            // ── Arrange By keys ──────────────────────────────────────────────────────────────
            // Kept SEPARATE from the display fields above, which carry web-only semantics the
            // desktop does not share: ur is user-only, y is a formatted string, installed is
            // store-aware. Sorting on those is what made the two surfaces disagree. Every key
            // below is the value GameSortCatalog.Getter feeds the desktop list, and null means
            // "no value" — it ranks last ascending (game-sort.js :: sorted).
            sr = SafeDoubleN(() => gm.CommunityOrLocalStarRating),
            ry = GameSortCatalog.EffectiveYear(gm),
            inst = SafeBoolN(() => gm.Installed),
            da = SafeDateMs(() => gm.DateAdded),
            dm = SafeDateMs(() => gm.DateModified),
            esrb = Safe(() => gm.Rating),
            rt = Safe(() => gm.ReleaseType),
            rd = SafeNullableDateMs(() => gm.ReleaseDate),
            region = Safe(() => gm.Region),
            playMode = Safe(() => gm.PlayMode),
            playCount = SafeInt(() => gm.PlayCount),
            playTime = SafeInt(() => gm.PlayTime),
            maxPlayers = SafeIntN(() => gm.MaxPlayers),
            progress = Safe(() => gm.Progress),
            series = Safe(() => gm.Series),
            source = Safe(() => gm.Source),
            status = Safe(() => gm.Status),
            version = Safe(() => gm.Version),
            mameHs = SafeBool(() => GameSortCatalog.MameHighScoresSupported(gm)),
            // Advanced-filter dimensions the SDK surface does not carry: controller support and saves
            // live in per-game sub-entities. Same discriminators as the desktop filter — SupportLevel 0
            // is "Not Supported", a numeric Slot means a save STATE (see FilterCriteria's predicates).
            pads = SafePads(gm),
            hasSave = SafeHasSave(gm, wantState: false),
            hasState = SafeHasSave(gm, wantState: true),
            mo = manualOrder,
            cf = SafeCustomFields(gm),
            file = SafeFileName(gm),
            box = ThemeFormat.BoxLines(gm.Title),
            dbId = SafeIntN(() => gm.LaunchBoxDbId),
            fav = SafeBool(() => gm.Favorite),
            broken = SafeBool(() => gm.Broken),
            completed = SafeBool(() => gm.Completed),
            installed = WebStoreState.IsInstalledOrPresent(gm),
            store = WebStoreState.StoreLabel(gm),
            portable = SafeBool(() => gm.Portable),
            appPath = Safe(() => gm.ApplicationPath),
            raHash = gm is HostGame hostGame ? hostGame.RetroAchievementsHash : "",
            thumb = ThumbProxy(gm, cg, "Front"),
            shotThumb = ThumbProxy(gm, cg, "Screenshots", "Background"),
            logo = LogoProxy(gm, cg),
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
            d = ThemeFormat.OverviewHtml(DescriptionOf(game)),
            boxImg = WithFull(ImageProxy(game, cg, "Front")),
            shotImg = WithFull(ImageProxy(game, cg, "Screenshots", "Background")),
            video = WithFull(VideoProxy(game, cg)),
            shots = extraShots ? ExtraScreenshotThumbs(game, cg, 12) : ScreenshotThumbs(game, cg, 8),
            fanart = FanartList(game, cg, 12),
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


    /// <summary>The game's own Notes, and — only while the optional tier is freed for a running game — the
    /// metadata DB's description as a stand-in. Gated on OptionalDropped rather than on "Notes are empty",
    /// so what is served in normal operation does not change: a game that genuinely has no description still
    /// reports none. See MetadataDb.OverviewForGame for why the substitute is acceptable.</summary>
    private static string DescriptionOf(IGame game)
    {
        var notes = Safe(() => game.Notes);
        if (!string.IsNullOrWhiteSpace(notes) || !GameStore.OptionalDropped) return notes;
        try
        {
            int dbId = SafeInt(() => game.LaunchBoxDbId);
            return Media.MetadataDb.OverviewForGame(dbId) ?? notes;
        }
        catch { return notes; }
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
    internal static string RelatedLocalThumb(IGame game) => ThumbProxy(game, ResolveCacheGame(game), "Front");

    // Cache absent -> disk, and the SAME answer either way.
    //
    // Every media URL below came from the GameCache and nowhere else, so the moment it is emptied — which is
    // what a game launch does, ClearForMemory() swapping the platform dictionary for a fresh one — the web
    // frontends stopped producing images at all. Not degraded: absent.
    //
    // The fallback goes through the game image property, which is MediaResolver.Image over one regroupement
    // type chain. That function walks the SAME chain whether it reads a ready cache or the folders, so the
    // file it names does not depend on whether the cache happens to be loaded. That identity is the point:
    // a URL served now must not name a different file than the one served a minute later, or a browser would
    // be holding a cached answer that was never true.
    //
    // Which is also why the desktop route is NOT used here despite being right there: CacheSourceFor adds
    // cross-type chains — a missing front falls to a 3D box, then to a screenshot — so it would answer
    // differently with the cache gone. Generous, and inconsistent; here consistency wins.
    private static string DiskPath(IGame game, string regroupement)
    {
        if (game == null) return null;
        string p = null;
        try
        {
            p = regroupement switch
            {
                "Front"       => game.FrontImagePath,
                "Screenshots" => game.ScreenshotImagePath,
                "Background"  => game.BackgroundImagePath,
                "ClearLogo"   => game.ClearLogoImagePath,
                _             => null,   // BoxSpine / BoxFull have no property — cache-only, as before
            };
        }
        catch { }
        return FileOk(p) ? p : null;
    }

    private static long SizeOf(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    private static string ThumbProxy(IGame game, GameCacheGame cg, params string[] regroupements)
    {
        foreach (var rg in regroupements)
        {
            GameCacheImageRef r = null;
            if (cg != null) try { r = cg.GetBestImageTypeFirst(rg); } catch { }
            if (r != null && !string.IsNullOrEmpty(r.FullPath))
            {
                long size = 0; try { size = r.GetFileSize(); } catch { }
                var url = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", rg);
                if (url != null) return url + "?q=thumb&v=" + size;
            }
        }
        foreach (var rg in regroupements)
        {
            var p = DiskPath(game, rg);
            if (p == null) continue;
            var url = MediaProxy.BuildProxyUrl(p, null, 0, ExtOf(p), "local", rg);
            if (url != null) return url + "?q=thumb&v=" + SizeOf(p);
        }
        return null;
    }

    private static string LogoProxy(IGame game, GameCacheGame cg)
    {
        GameCacheImageRef r = null;
        if (cg != null) try { r = cg.GetBestImageTypeFirst("ClearLogo"); } catch { }
        if (r != null && !string.IsNullOrEmpty(r.FullPath))
        {
            long size = 0; try { size = r.GetFileSize(); } catch { }
            var hit = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", "Clear Logo");
            if (hit != null) return hit + "?q=logo&v=" + size;
        }
        var p = DiskPath(game, "ClearLogo");
        if (p == null) return null;
        var url = MediaProxy.BuildProxyUrl(p, null, 0, ExtOf(p), "local", "Clear Logo");
        return url != null ? url + "?q=logo&v=" + SizeOf(p) : null;
    }

    private static string WithFull(string url) => string.IsNullOrEmpty(url) ? url : url + "?q=full";


    // ── The LIST fields, from disk ──────────────────────────────────────────────────────────────────
    // shots and fanart are galleries, not single picks: the screenshot strip and the fanart rotation. No
    // IGame property returns a LIST, so the single-image fallback above cannot serve them — this walks
    // MediaResolver.AllImageFiles instead, the same enumeration the image editor uses, and keeps the
    // regroupement type chain in order so the first entry is still a gameplay shot.
    //
    // It IS an approximation: the cache orders by the user region priorities, this walks root files then
    // region sub-folders alphabetically. That is acceptable here and nowhere else, because a response
    // built in this state is marked X-LiteBox-Degraded and no cache — browser or frontend — keeps it. The
    // moment the real data is back, the next fetch replaces it.
    //
    // TODO: this whole family is due to be replaced. The extra-screenshots flag exists because one region
    // often has a single shot and the others have more, so the strip is padded from elsewhere — a rule
    // hard-coded here. It belongs in LiteBox's options as an ordered list of what to send, the same shape
    // as the post-load image list in Options → Display → Right panel. Until then, this mirrors the flag.
    private static string[] DiskImageList(IGame game, string[] typeChain, string label, int max)
    {
        if (game == null || max <= 0) return Array.Empty<string>();
        string plat, title, idStr;
        try { plat = game.Platform ?? ""; title = game.Title ?? ""; idStr = game.Id ?? ""; }
        catch { return Array.Empty<string>(); }
        if (plat.Length == 0) return Array.Empty<string>();
        Guid.TryParse(idStr, out var gid);

        List<(string path, string type, string region)> all;
        try { all = Media.MediaResolver.AllImageFiles(plat, gid, title); }
        catch (Exception ex) { Log("DiskImageList: " + ex.Message); return Array.Empty<string>(); }
        if (all == null || all.Count == 0) return Array.Empty<string>();

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in typeChain)
        {
            foreach (var f in all)
            {
                if (urls.Count >= max) return urls.ToArray();
                if (!string.Equals(f.type, type, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(f.path)) continue;
                var u = MediaProxy.BuildProxyUrl(f.path, null, 0, ExtOf(f.path), "local", label);
                if (u != null) urls.Add(u + "?q=thumb&v=" + SizeOf(f.path));
            }
        }
        return urls.ToArray();
    }

    private static string[] ScreenshotThumbs(IGame game, GameCacheGame cg, int max)
    {
        if (cg == null) return DiskImageList(game, Media.MediaResolver.Screenshot, "Screenshots", max);
        List<GameCacheImageRef> imgs;
        try { imgs = cg.GetAllImagesTypeFirst("Screenshots", max); }
        catch (Exception ex) { Log("GetAllImagesTypeFirst: " + ex.Message); imgs = null; }
        if (imgs == null || imgs.Count == 0) return DiskImageList(game, Media.MediaResolver.Screenshot, "Screenshots", max);

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

    private static string[] ExtraScreenshotThumbs(IGame game, GameCacheGame cg, int max)
    {
        // Cache gone: the padding rule below has nothing to read, so fall back on the plain enumeration.
        if (cg == null) return DiskImageList(game, Media.MediaResolver.Screenshot, "Screenshots", max);
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
        if (urls.Count == 0) return DiskImageList(game, Media.MediaResolver.Screenshot, "Screenshots", max);

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

    private static string[] FanartList(IGame game, GameCacheGame cg, int max)
    {
        if (cg == null) return DiskImageList(game, Media.MediaResolver.Background, "Background", max);
        List<GameCacheImageRef> imgs;
        try { imgs = cg.GetAllImagesTypeFirst("Background", max); }
        catch (Exception ex) { Log("Fanart GetAllImagesTypeFirst: " + ex.Message); imgs = null; }
        if (imgs == null || imgs.Count == 0) return DiskImageList(game, Media.MediaResolver.Background, "Background", max);

        var urls = new List<string>(imgs.Count);
        foreach (var r in imgs)
        {
            if (r == null || string.IsNullOrEmpty(r.FullPath)) continue;
            var url = MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", "Background");
            if (url != null) urls.Add(WithFull(url));
        }
        return urls.ToArray();
    }

    private static string ImageProxy(IGame game, GameCacheGame cg, params string[] regroupements)
    {
        foreach (var rg in regroupements)
        {
            GameCacheImageRef r = null;
            if (cg != null) try { r = cg.GetBestImageTypeFirst(rg); } catch { }
            if (r != null && !string.IsNullOrEmpty(r.FullPath))
                return MediaProxy.BuildProxyUrl(r.FullPath, null, 0, ExtOf(r.FullPath), "local", rg);
        }
        foreach (var rg in regroupements)
        {
            var p = DiskPath(game, rg);
            if (p != null) return MediaProxy.BuildProxyUrl(p, null, 0, ExtOf(p), "local", rg);
        }
        return null;
    }

    private static string VideoProxy(IGame game, GameCacheGame cg)
    {
        List<GameCacheVideoRef> vids = null;
        if (cg != null) try { vids = cg.FindAllVideos(); } catch { }
        if (vids != null && vids.Count > 0 && !string.IsNullOrEmpty(vids[0].FullPath))
            return MediaProxy.BuildProxyUrl(vids[0].FullPath, null, 0, ExtOf(vids[0].FullPath), "local", "Video");

        string path = null;
        try { path = game?.GetVideoPath(false); } catch { }
        if (!FileOk(path)) return null;
        return MediaProxy.BuildProxyUrl(path, null, 0, ExtOf(path), "local", "Video");
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
    private static double SafeDateMs(Func<DateTime> read)
    {
        try
        {
            var d = read();
            return d == default ? 0 : (d.ToUniversalTime() - _epoch).TotalMilliseconds;
        }
        catch { return 0; }
    }

    private static double SafeNullableDateMs(Func<DateTime?> read)
    {
        try
        {
            var d = read();
            return !d.HasValue ? 0 : (d.Value.ToUniversalTime() - _epoch).TotalMilliseconds;
        }
        catch { return 0; }
    }

    private static Dictionary<string, string> SafeCustomFields(IGame game)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var field in game.GetAllCustomFields() ?? Array.Empty<ICustomField>())
            {
                var name = Safe(() => field.Name).Trim();
                if (name.Length > 0) result[name] = Safe(() => field.Value);
            }
        }
        catch { }
        return result;
    }

    private static double SafeLastPlayedMs(IGame g)
    {
        var d = SafeLastPlayed(g);
        if (d == null) return 0;
        try { return (d.Value.ToUniversalTime() - _epoch).TotalMilliseconds; } catch { return 0; }
    }

    // ── Advanced-filter sub-entity reads (same predicates as the desktop filter) ──

    // Id → nom du catalogue Data\GameControllers.xml : résolu une fois par PAYLOAD (le champ statique
    // est remis à null à chaque construction de liste via ResetPadNames) — jamais par jeu, le catalogue
    // ne change pas au milieu d'une sérialisation.
    private static Dictionary<string, string> _padNames;
    internal static void ResetPadNames() => _padNames = null;
    private static Dictionary<string, string> PadNames()
    {
        var map = _padNames;
        if (map != null) return map;
        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in Host.ControllerCatalogStore.All())
                if (!string.IsNullOrEmpty(r.Id) && !string.IsNullOrEmpty(r.Name)) map[r.Id] = r.Name;
        }
        catch { }
        return _padNames = map;
    }

    /// <summary>Les NOMS de manettes que ce jeu supporte (SupportLevel ≠ 0). Null quand aucune —
    /// le JSON reste léger, et le client teste la présence.</summary>
    private static List<string> SafePads(IGame g)
    {
        if (g is not ILiteBoxGame lb) return null;
        try
        {
            List<string> pads = null;
            var byId = PadNames();
            foreach (var row in lb.GetSubEntities("GameControllerSupport"))
            {
                if (!Search.FilterCriteria.RowSupportsController(row)) continue;
                if (!row.TryGetValue("ControllerId", out var id) || string.IsNullOrEmpty(id)) continue;
                if (byId.TryGetValue(id, out var name)) (pads ??= new List<string>()).Add(name);
            }
            return pads;
        }
        catch { return null; }
    }

    private static bool SafeHasSave(IGame g, bool wantState)
    {
        if (g is not ILiteBoxGame lb) return false;
        try
        {
            foreach (var row in lb.GetSubEntities("GameSave"))
                if (Search.FilterCriteria.RowIsState(row) == wantState) return true;
        }
        catch { }
        return false;
    }

    private static string SafeFileName(IGame g)
    {
        try { var p = g.ApplicationPath; return string.IsNullOrEmpty(p) ? "" : Path.GetFileName(p); }
        catch { return ""; }
    }

    private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static T SafeValue<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static int SafeInt(Func<int?> f) { try { return f() ?? 0; } catch { return 0; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static int? SafeIntN(Func<int?> f) { try { return f(); } catch { return null; } }
    /// <summary>Tri-state flag: null stays null (unset is a hole, not a "false").</summary>
    private static bool? SafeBoolN(Func<bool?> f) { try { return f(); } catch { return null; } }
    /// <summary>Sortable score: null (not 0) when there is no rating at all, so it ranks last.</summary>
    private static double? SafeDoubleN(Func<double> f) { try { var v = f(); return v > 0 ? v : (double?)null; } catch { return null; } }

    private static void Log(string msg) => LbLog.Info("web", "[theme] " + msg);
}

// Lifecycle owner of LiteBox's theme/database HTTP surface.
//
// Owns the route table and the per-site enable flags; the listener, the accept loop and the keep-alive
// request loop live in HttpHost, which the RomM API surface reuses on its own port. Binds 127.0.0.1:{port}
// (loopback only) by default — no auth, no TLS. LAN access is opt-in: when [Web] AllowedIps lists wildcard
// IP patterns the bind flips to 0.0.0.0 and only matching remotes are admitted (loopback is ALWAYS allowed).
// Start is idempotent while running; the route table is rebuilt on every Start so the per-site enable flags
// take effect on a restart. Gated as a whole on LbModule.Web.

using System;
using LbApiHost.Host;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Web;

internal static class EmbeddedWebServer
{
    private static readonly Router _router = new();

    // One static-file handler per mount. Roots resolve lazily each request (LiteBoxPaths.Web creates on demand).
    private static readonly StaticFileHandler _vendor  = new(() => LiteBoxPaths.Web("vendor"),   "vendor");
    private static readonly StaticFileHandler _bigbox  = new(() => LiteBoxPaths.Web("bigbox"),   "bigbox");
    private static readonly StaticFileHandler _litebox = new(() => LiteBoxPaths.Web("litebox"),  "litebox");
    private static readonly StaticFileHandler _database = new(() => LiteBoxPaths.Web("database"), "database");

    // Listener + connection loop (shared with the other HTTP surfaces — see HttpHost).
    private static readonly HttpHost _host = new("web", _router)
    {
        AllowedIpsProvider = () => WebConfig.AllowedIps,
        Observe = req => WebSelectionBridge.Observe(req),   // a kiosk browsing IS a selection
        DegradedProbe = IsDegraded,
        Decorate = MarkIfDegraded,
    };

    public static bool IsRunning => _host.IsRunning;
    public static int CurrentPort => _host.CurrentPort;

    public static void Start(int port)
    {
        // Mechanism gate: the web module owns the embedded server. When off, refuse to start regardless of
        // caller so no server ever runs while the module is disabled.
        if (!LbModules.On(LbModule.Web))
        {
            LbLog.Once("web", "embedded web server start refused (web module off)");
            return;
        }
        if (_host.IsRunning) return;
        try
        {
            // Refresh the [Web] snapshot (enable flags, gzip, allowed IPs) and rebuild the table so a restart
            // reflects the current per-site enable flags.
            WebConfig.Reload();
            RegisterRoutes();
            Media.MediaPolicyStore.Bootstrap();   // warm the remote media-policy slots (no-op when URLs blank)

            _host.Start(port);
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", "start error: " + ex);
            Stop();
        }
    }

    public static void Stop() => _host.Stop();

    // ── Route table (S1: static serving only) ─────────────────────────────────

    private static void RegisterRoutes()
    {
        _router.Clear();

        // Crawlers off — always available.
        _router.Add(@"/robots\.txt", RobotsHandler.Handle);

        // Shared vendor assets (both themes reference ../vendor/… → /vendor/<file> at the server root).
        // Require a non-empty sub-path (matches the source's `.+`).
        _router.Add(@"/vendor/(?<path>.+)", _vendor.Handle);

        // ── S2: media proxy + thumbs + badges + recent-epoch + parental ───────────
        // Theme-agnostic → registered unconditionally, BEFORE the database catch-all. Order within the
        // /api/media/ family: the id form (single dot, digits) precedes the signed-token form (two dots) — the
        // two regexes are disjoint, but registering id-first keeps intent obvious.
        _router.Add(@"/thumbs/(?<id>\d+)\.jpg", ThumbHandler.Handle);
        _router.Add(@"/api/media/(?<id>\d+)\.(?<ext>[a-z0-9]{1,6})", MediaProxy.HandleThumbById);
        _router.Add(@"/api/media/(?<token>[A-Z0-9]+)\.(?<sig>[A-Z0-9]+)\.(?<ext>[a-z0-9]{1,6})", MediaProxy.Handle);
        _router.Add(@"/api/badges/(?<name>[^/]+)\.png", BadgeApi.Handle);
        // S5: RA achievement badge PNGs referenced by the `ra` block in detail.json (RaBadges disk cache).
        _router.Add(@"/api/ra/badge/(?<name>[^/]+)\.png", WebRa.BadgeHandle);
        _router.Add(@"/api/recent/epoch", RecentEpochApi.Handle);
        _router.Add(@"/api/kiosk/selection", WebSelectionBridge.Ping);   // a kiosk that served a view from its own cache
        // Monitor Profiles — registered only when its module AND its own option are on, so the feature
        // leaves no trace on a server whose owner did not ask for it.
        if (MonitorsApi.Enabled)
        {
            _router.Add(@"/api/monitors", MonitorsApi.List);
            _router.Add(@"/api/monitors/apply", MonitorsApi.Apply);
            _router.Add(@"/api/monitors/restore", MonitorsApi.Restore);
        }

        _router.Add(@"/api/parental/state", ParentalApi.HandleState);
        _router.Add(@"/api/parental/unlock", ParentalApi.HandleUnlock);
        _router.Add(@"/api/parental/lock", ParentalApi.HandleLock);

        // BigBox Web theme → web\bigbox\. Bare "/bigbox" 301s so relative fetches resolve against the dir.
        if (WebConfig.EnableBigBoxWeb)
        {
            _router.Add(@"/bigbox", _ => HttpResponse.Redirect("/bigbox/", 301));

            // ── S4: theme data + api. BEFORE the static catch-all so JSON/api routes match first. ──
            _router.Add(@"/bigbox/data/cattree\.json", BigBoxThemeApi.CatTree);
            _router.Add(@"/bigbox/data/detailmenu\.json", BigBoxThemeApi.DetailMenu);
            _router.Add(@"/bigbox/data/system\.json", BigBoxThemeApi.SystemMenu);
            _router.Add(@"/bigbox/data/platforms/(?<slug>[^/]+)/games\.json", BigBoxThemeApi.PlatformGames);
            _router.Add(@"/bigbox/data/platforms/(?<slug>[^/]+)/stars\.json", BigBoxThemeApi.PlatformStars);
            _router.Add(@"/bigbox/data/playlists/(?<slug>[^/]+)/games\.json", BigBoxThemeApi.PlaylistGames);
            _router.Add(@"/bigbox/data/platforms/(?<slug>[^/]+)/recent\.json", BigBoxThemeApi.PlatformRecent);
            _router.Add(@"/bigbox/data/playlists/(?<slug>[^/]+)/recent\.json", BigBoxThemeApi.PlaylistRecent);
            _router.Add(@"/bigbox/data/categories/(?<slug>[^/]+)/recent\.json", BigBoxThemeApi.CategoryRecent);
            _router.Add(@"/bigbox/data/platforms/(?<slug>[^/]+)/catmedia\.json", BigBoxThemeApi.PlatformCatMedia);
            _router.Add(@"/bigbox/data/playlists/(?<slug>[^/]+)/catmedia\.json", BigBoxThemeApi.PlaylistCatMedia);
            _router.Add(@"/bigbox/data/categories/(?<slug>[^/]+)/catmedia\.json", BigBoxThemeApi.CategoryCatMedia);
            // Batch-overviews (literal) BEFORE the per-game /games/{id}/… routes so it can't be swallowed.
            _router.Add(@"/bigbox/data/games/related/overviews\.json", BigBoxThemeApi.RelatedOverviews);
            _router.Add(@"/bigbox/data/games/(?<id>[^/]+)/detail\.json", BigBoxThemeApi.GameDetail);
            _router.Add(@"/bigbox/data/games/(?<id>[^/]+)/installstate\.json", BigBoxThemeApi.InstallState);
            _router.Add(@"/bigbox/data/games/(?<id>[^/]+)/related\.json", BigBoxThemeApi.Related);
            // R5: /bigbox/api/games/{id}/archive-{entries,favorite,metadata} — the Select-ROM sub-menu.
            // Registered BEFORE the {kind} mutation route: the dashed archive-* verbs don't match its
            // [a-z]+ capture anyway, but keeping them first makes the intent explicit. Each handler gates
            // on RomExtractor.Available (LbModule.Rom) → 404 when the module is off, so the theme hides
            // Select-ROM. Reuses RomExtractor's single listing/scoring impl (no re-list, no re-score).
            _router.Add(@"/bigbox/api/games/(?<id>[^/]+)/archive-entries", ArchiveListingApi.Handle);
            _router.Add(@"/bigbox/api/games/(?<id>[^/]+)/archive-favorite", ArchiveListingApi.HandleFavorite);
            _router.Add(@"/bigbox/api/games/(?<id>[^/]+)/archive-metadata", ArchiveMetadataApi.Handle);
            // S7: in-browser play (non-kiosk). The dashed/keyworded routes are disjoint from the [a-z]+
            // {kind} mutation capture; registered before it to keep intent readable.
            _router.Add(@"/bigbox/api/games/(?<id>[^/]+)/(?<kind>[a-z]+)", BigBoxMutationApi.Handle);
            _router.Add(@"/bigbox/api/keybinds", WebKeyBindsApi.Handle);

            // Static catch-all LAST within the site so data/api routes above win.
            _router.Add(@"/bigbox/(?<path>.*)", _bigbox.Handle);
        }

        // LiteBox Web theme (was "LaunchBox Web") → web\litebox\. KEEP the /launchbox/ URL mount (the shipped
        // theme JS hardcodes relative data/… and ../vendor/… paths); only the served folder is renamed.
        if (WebConfig.EnableLiteBoxWeb)
        {
            _router.Add(@"/launchbox", _ => HttpResponse.Redirect("/launchbox/", 301));

            // ── S4: theme data + api. BEFORE the static catch-all. Same data contract as /bigbox/. ──
            _router.Add(@"/launchbox/data/cattree\.json", LaunchBoxDataApi.CatTree);
            _router.Add(@"/launchbox/data/platforms/(?<slug>[^/]+)/games\.json", LaunchBoxDataApi.PlatformGames);
            _router.Add(@"/launchbox/data/playlists/(?<slug>[^/]+)/games\.json", LaunchBoxDataApi.PlaylistGames);
            _router.Add(@"/launchbox/data/platforms/(?<slug>[^/]+)/stars\.json", LaunchBoxDataApi.Stars);
            _router.Add(@"/launchbox/data/games/(?<id>[^/]+)/detail\.json", LaunchBoxDataApi.GameDetail);
            _router.Add(@"/launchbox/data/games/(?<id>[^/]+)/installstate\.json", LaunchBoxDataApi.InstallState);
            // Batch-overviews (literal) BEFORE the per-game /games/{id}/related route so it can't be swallowed.
            _router.Add(@"/launchbox/data/games/related/overviews\.json", LaunchBoxDataApi.RelatedOverviews);
            _router.Add(@"/launchbox/data/games/(?<id>[^/]+)/related\.json", LaunchBoxDataApi.Related);
            // Combined recent / catmedia driven by the {kind} (platforms|playlists|categories) capture.
            _router.Add(@"/launchbox/data/(?<kind>[^/]+)/(?<slug>[^/]+)/recent\.json", LaunchBoxDataApi.Recent);
            _router.Add(@"/launchbox/data/(?<kind>[^/]+)/(?<slug>[^/]+)/catmedia\.json", LaunchBoxDataApi.CatMedia);
            // Server-root API endpoints for the LiteBox web theme (registered here so they precede the
            // database site's "/" catch-all).
            _router.Add(@"/api/launchbox/icons/(?<name>[^/]+)\.(?<ext>[a-z0-9]{1,4})", LaunchBoxIconsApi.Handle);
            _router.Add(@"/api/launchbox/platforms/stats", LaunchBoxStatsApi.Handle);
            // Notifications: the event poll vendor\notify.js runs, + the write-back verbs. Scoped to THIS
            // surface deliberately, unlike the theme-agnostic /api/media|badges|recent above: only the
            // LiteBox theme loads notify.js (BigBox sends notifications to the bell instead), and the
            // action route EXECUTES a plugin-supplied callback rather than serving data. With this
            // surface off, those routes would answer nobody — so they don't exist.
            // Ids are Guid "N" (32 lowercase hex). action/<n> registered before the verb route: disjoint
            // regexes, but explicit order keeps intent obvious (same rationale as the /api/media family).
            _router.Add(@"/api/notifications/events", NotificationsApi.HandleEvents);
            _router.Add(@"/api/notifications/test", NotificationsApi.HandleTest);
            _router.Add(@"/api/notifications/(?<id>[0-9a-f]{32})/action/(?<index>\d+)", NotificationsApi.HandleAction);
            _router.Add(@"/api/notifications/(?<id>[0-9a-f]{32})/(?<verb>read|unread|dismiss|remove)", NotificationsApi.HandleVerb);
            // R5: /launchbox/api/games/{id}/archive-{entries,favorite} — Select-ROM (the LiteBox-Web theme
            // uses a modal table, not the per-entry overlay, so it has no archive-metadata route). Same
            // handlers + gate as /bigbox/. Registered before the [a-z]+ {kind} mutation route.
            _router.Add(@"/launchbox/api/games/(?<id>[^/]+)/archive-entries", ArchiveListingApi.Handle);
            _router.Add(@"/launchbox/api/games/(?<id>[^/]+)/archive-favorite", ArchiveListingApi.HandleFavorite);
            _router.Add(@"/launchbox/api/games/(?<id>[^/]+)/(?<kind>[a-z]+)", LaunchBoxMutationApi.Handle);
            _router.Add(@"/launchbox/api/keybinds", WebKeyBindsApi.HandleLaunchBox);

            // Static catch-all LAST within the site so data/api routes above win.
            _router.Add(@"/launchbox/(?<path>.*)", _litebox.Handle);
        }

        // Database site → mounted at "/". S3 renders it server-side from the Extended DB (DbRepository); the
        // page + API routes register BEFORE the static catch-all so "/" is the platform grid, not a placeholder.
        if (WebConfig.EnableDatabaseSite)
        {
            // Server-rendered pages.
            _router.Add(@"/", HomeHandler.Handle);
            _router.Add(@"/index\.html", HomeHandler.Handle);
            _router.Add(@"/platforms\.html", PlatformsListHandler.Handle);
            _router.Add(@"/platforms/(?<slug>[^/]+)\.html", PlatformDetailHandler.Handle);
            _router.Add(@"/games/(?<id>\d+)\.html", GameDetailHandler.Handle);

            // JSON API. ([^/]+ excludes '/', so the bare /api/platforms/{slug} route can't swallow the
            // /games and facet sub-routes even though it's registered first — the $-anchor makes them disjoint.)
            _router.Add(@"/api/platforms", PlatformsApi.Handle);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)", PlatformDetailApi.Handle);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)/games", PlatformGamesApi.Handle);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)/genres", PlatformFiltersApi.HandleGenres);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)/developers", PlatformFiltersApi.HandleDevelopers);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)/publishers", PlatformFiltersApi.HandlePublishers);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)/release-types", PlatformFiltersApi.HandleReleaseTypes);
            _router.Add(@"/api/platforms/(?<slug>[^/]+)/origins", PlatformFiltersApi.HandleOrigins);
            _router.Add(@"/api/games/(?<id>\d+)", GameDetailApi.Handle);
            _router.Add(@"/api/search", SearchApi.Handle);

            // Static catch-all LAST: web\database\ statics (favicon / overrides) + the built-in placeholder.
            _router.Add(@"/(?<path>.*)", DatabaseSite);
        }
    }

    // "/" and "/{path}" → static from web\database\. When the requested index has no shipped index.html,
    // fall back to a tiny built-in page rather than a bare 404.
    private static HttpResponse DatabaseSite(RouteContext ctx)
    {
        var rel = ctx.GetRoute("path") ?? "";
        var resp = _database.Serve(rel, ctx.Request);
        if (resp.StatusCode == 404 && IsIndexRequest(rel))
            return HttpResponse.Html(DatabasePlaceholderHtml);
        return resp;
    }

    private static bool IsIndexRequest(string rel)
    {
        rel = (rel ?? "").Replace('\\', '/').Trim('/');
        return rel.Length == 0 || rel.Equals("index.html", StringComparison.OrdinalIgnoreCase);
    }

    private const string DatabasePlaceholderHtml =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<title>LiteBox Web</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;background:#0f0f12;color:#e6e6e6;" +
        "display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0\">" +
        "<main style=\"text-align:center\"><h1 style=\"font-weight:600\">LiteBox Web</h1>" +
        "<p style=\"opacity:.7\">database site coming soon</p></main></body></html>";

    // ── Degraded mode: served, but never kept ───────────────────────────────────────────────────────
    // A game launch frees the optional tier and empties the media cache, so a data payload built during
    // one is an APPROXIMATION — a game with no description, a list with no controller flags. Serving that
    // is fine and better than serving nothing; letting anybody KEEP it is not, because the browser and the
    // frontends' own memory caches would hold the thin answer long after the real one came back.
    //
    // So it is labelled at the door rather than at each of the dozen handlers: no-store for the browser,
    // and a header the frontends read to skip their own memoisation. Only the data JSONs — media URLs and
    // static assets are the same in both states and stay cacheable.
    /// <summary>Is the data we can serve right now an APPROXIMATION of the real thing?
    ///
    /// Two very different situations look alike from here and must not be confused. A game launch frees the
    /// optional tier and empties the media cache: that is TRANSIENT, the real answer is coming back, and
    /// nothing built meanwhile may be kept. But an install that never builds the host cache at all — the
    /// user turned UseGameCache off, or ExtendDB supplies one this surface cannot read — is a STABLE
    /// configuration whose normal answer IS the disk walk. Marking those degraded forever would leave every
    /// frontend cache permanently disabled and repeat the enumeration on every navigation.
    ///
    /// So an absent cache only counts when the cache was supposed to be there.</summary>
    private static bool IsDegraded()
    {
        try { return Data.GameStore.OptionalDropped || (Gc.HostGameCache.Enabled && !Gc.GameCache.IsGlobalReady); }
        catch { return false; }
    }

    private static void MarkIfDegraded(HttpRequest req, HttpResponse resp, bool degradedBefore)
    {
        try
        {
            if (resp == null || req?.Path == null) return;
            if (req.Path.IndexOf("/data/", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!degradedBefore && !IsDegraded()) return;
            resp.Headers["X-LiteBox-Degraded"] = "1";
            resp.Headers["Cache-Control"] = "no-store";   // already the default; stated so the intent reads
        }
        catch { }
    }
}

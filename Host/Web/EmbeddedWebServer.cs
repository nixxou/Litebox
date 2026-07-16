// Lifecycle owner of LiteBox's local HTTP server.
//
// Binds 127.0.0.1:{port} (loopback only) by default — no auth, no TLS. LAN access is opt-in: when [Web]
// AllowedIps lists wildcard IP patterns, the server binds 0.0.0.0 and the accept loop admits a connection only
// if its remote IP matches a pattern (loopback is ALWAYS allowed; the allow-list is the only gate). One task
// per accepted connection, a keep-alive loop per socket, and a per-request concurrency cap (~20) so bursts
// don't spin up unbounded work. Start is idempotent while running; the route table is rebuilt on every Start
// so the per-site enable flags take effect on a restart. Gated as a whole on LbModule.Web.
//
// Slice S1 registers ONLY the static-serving routes (robots + vendor + the three site mounts); the data/API
// routes land in later slices.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Web;

internal static class EmbeddedWebServer
{
    private static TcpListener _listener;
    private static CancellationTokenSource _cts;
    private static Task _acceptLoop;
    private static readonly Router _router = new();
    private static int _currentPort;

    // Compiled remote-IP allow-list (wildcard patterns from config). Empty ⇒ loopback-only bind; non-empty
    // ⇒ 0.0.0.0 bind + per-connection filtering. Loopback is always allowed regardless.
    private static List<Regex> _allowedPatterns = new();

    // Cap on requests processed concurrently. Acquired per-request inside the keep-alive loop (not per
    // connection) so idle keep-alive sockets don't hold a slot.
    private static readonly SemaphoreSlim _concurrencyGate = new(20);

    // One static-file handler per mount. Roots resolve lazily each request (LiteBoxPaths.Web creates on demand).
    private static readonly StaticFileHandler _vendor  = new(() => LiteBoxPaths.Web("vendor"),   "vendor");
    private static readonly StaticFileHandler _bigbox  = new(() => LiteBoxPaths.Web("bigbox"),   "bigbox");
    private static readonly StaticFileHandler _litebox = new(() => LiteBoxPaths.Web("litebox"),  "litebox");
    private static readonly StaticFileHandler _database = new(() => LiteBoxPaths.Web("database"), "database");

    public static bool IsRunning => _listener != null;
    public static int CurrentPort => _currentPort;

    public static void Start(int port)
    {
        // Mechanism gate: the web module owns the embedded server. When off, refuse to start regardless of
        // caller so no server ever runs while the module is disabled.
        if (!LbModules.On(LbModule.Web))
        {
            LbLog.Once("web", "embedded web server start refused (web module off)");
            return;
        }
        if (_listener != null) return;
        try
        {
            // Refresh the [Web] snapshot (enable flags, gzip, allowed IPs) and rebuild the table so a restart
            // reflects the current per-site enable flags.
            WebConfig.Reload();
            RegisterRoutes();

            _cts = new CancellationTokenSource();

            // LAN access is opt-in: any configured IP pattern flips the bind from loopback to all interfaces;
            // the accept loop then filters.
            _allowedPatterns = ParseAllowedIpPatterns(WebConfig.AllowedIps);
            var bindAddr = _allowedPatterns.Count > 0 ? IPAddress.Any : IPAddress.Loopback;

            _listener = new TcpListener(bindAddr, port);
            _listener.Start();
            _currentPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            LbLog.Info("web", _allowedPatterns.Count > 0
                ? $"listening on http://0.0.0.0:{_currentPort}/ (LAN access: {_allowedPatterns.Count} pattern(s) + loopback)"
                : $"listening on http://127.0.0.1:{_currentPort}/ (loopback only)");

            _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", "start error: " + ex);
            Stop();
        }
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        _acceptLoop = null;
        LbLog.Info("web", "stopped");
    }

    private static async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                LbLog.Warn("web", "accept error: " + ex.Message);
                continue;
            }

            // Reject connections outside the allow-list before doing any work (loopback is always allowed;
            // an empty list ⇒ loopback bind, so every connection here is already loopback).
            if (!IsRemoteAllowed(client))
            {
                try { client.Close(); } catch { }
                continue;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private static void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 10000;   // idle keep-alive ceiling
                stream.WriteTimeout = 30000;  // guard against a wedged client mid-write

                while (true)
                {
                    HttpRequest req;
                    try { req = HttpRequest.TryRead(stream); }
                    catch (IOException) { return; } // peer disconnect / read timeout
                    if (req == null)
                    {
                        // On a kept-alive socket "null" is usually EOF (browser closed) — exit quietly.
                        try { HttpResponse.BadRequest("Malformed request").Write(stream); } catch { }
                        return;
                    }

                    // HTTP/1.1 default is keep-alive unless the client sent "Connection: close".
                    var connHdr = req.GetHeader("Connection") ?? "";
                    bool keepAlive = !connHdr.Equals("close", StringComparison.OrdinalIgnoreCase);

                    // Per-request concurrency gate (not per connection) so idle keep-alive sockets hold no slot.
                    _concurrencyGate.Wait();
                    HttpResponse resp;
                    try
                    {
                        try
                        {
                            if (req.Method != "GET" && req.Method != "HEAD" && req.Method != "POST")
                                resp = HttpResponse.PlainText("Method not allowed", 405);
                            else
                                resp = _router.Dispatch(req) ?? HttpResponse.NotFound($"No route for {req.Path}");
                        }
                        catch (Exception ex)
                        {
                            resp = HttpResponse.ServerError($"Dispatch error: {ex.Message}");
                        }
                    }
                    finally { _concurrencyGate.Release(); }

                    resp.Headers["Connection"] = keepAlive ? "keep-alive" : "close";
                    if (keepAlive)
                        resp.Headers["Keep-Alive"] = "timeout=10";

                    // Forward the client's gzip preference (HEAD has no body — skip).
                    if (req.Method != "HEAD")
                    {
                        var ae = req.GetHeader("Accept-Encoding");
                        resp.AcceptsGzip = ae != null
                            && ae.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    try
                    {
                        if (req.Method == "HEAD") resp.Body = Array.Empty<byte>();
                        resp.Write(stream);
                    }
                    catch (IOException) { return; /* client disconnected */ }

                    if (!keepAlive) return;
                }
            }
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", "client error: " + ex.Message);
        }
    }

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
        _router.Add(@"/api/recent/epoch", RecentEpochApi.Handle);
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
            // S6: /bigbox/api/games/{id}/archive-{favorite,entries,metadata} — Select-ROM, deferred (the
            // {kind} pattern below is [a-z]+ so the dashed archive-* verbs don't match it either).
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
            // Combined recent / catmedia driven by the {kind} (platforms|playlists|categories) capture.
            _router.Add(@"/launchbox/data/(?<kind>[^/]+)/(?<slug>[^/]+)/recent\.json", LaunchBoxDataApi.Recent);
            _router.Add(@"/launchbox/data/(?<kind>[^/]+)/(?<slug>[^/]+)/catmedia\.json", LaunchBoxDataApi.CatMedia);
            // Server-root API endpoints for the LiteBox web theme (registered here so they precede the
            // database site's "/" catch-all).
            _router.Add(@"/api/launchbox/icons/(?<name>[^/]+)\.(?<ext>[a-z0-9]{1,4})", LaunchBoxIconsApi.Handle);
            _router.Add(@"/api/launchbox/platforms/stats", LaunchBoxStatsApi.Handle);
            // S6: /launchbox/api/games/{id}/archive-* — Select-ROM, deferred ([a-z]+ excludes the dashed verbs).
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
        var resp = _database.Serve(rel);
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

    // ── IP allow-list ─────────────────────────────────────────────────────────

    /// <summary>Parses the config string (comma/semicolon/whitespace-separated wildcard patterns, <c>*</c> =
    /// any run) into anchored regexes. Empty / null → empty list (⇒ loopback-only bind).</summary>
    private static List<Regex> ParseAllowedIpPatterns(string raw)
    {
        var list = new List<Regex>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var tok in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                                      StringSplitOptions.RemoveEmptyEntries))
        {
            var p = tok.Trim();
            if (p.Length == 0) continue;
            try
            {
                var rx = "^" + Regex.Escape(p).Replace("\\*", ".*") + "$";
                list.Add(new Regex(rx, RegexOptions.Compiled | RegexOptions.IgnoreCase));
            }
            catch (Exception ex) { LbLog.Warn("web", $"bad IP pattern '{p}': {ex.Message}"); }
        }
        return list;
    }

    /// <summary>True if the client's remote IP is loopback (always) or matches an allow-list pattern.
    /// IPv4-mapped IPv6 remotes are normalised to IPv4 first.</summary>
    private static bool IsRemoteAllowed(TcpClient client)
    {
        IPAddress addr;
        try { addr = (client.Client.RemoteEndPoint as IPEndPoint)?.Address; }
        catch { return false; }
        if (addr == null) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (IPAddress.IsLoopback(addr)) return true;          // 127.0.0.1 / ::1 always
        var s = addr.ToString();
        foreach (var rx in _allowedPatterns) if (rx.IsMatch(s)) return true;
        return false;
    }
}

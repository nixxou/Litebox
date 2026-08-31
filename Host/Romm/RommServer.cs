// Lifecycle owner of the RomM-compatible API surface.
//
// Its own TcpListener on its own port (default 8998), because the theme server's database site already
// mounts /api/platforms, /api/games/{id} and /api/search at its root — and every RomM client expects the
// API at the base URL it was given. The listener, accept loop and keep-alive loop are HttpHost's; this
// class owns the route table and the module gate.
//
// Binding follows the same opt-in rule as the theme server: empty [RommServer] AllowedIps ⇒ loopback only.
// Unlike that surface, this one is meant to be reached from a phone or a handheld, so the options page
// says in plain words what opening it up means. There is no TLS: the password is the only wall.

#nullable enable

using System;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

internal static class RommServer
{
    private static readonly Router _router = new();

    private static readonly HttpHost _host = new("romm", _router)
    {
        AllowedIpsProvider = () => RommConfig.AllowedIps,
        Decorate = Decorate,
        Intercept = req => string.Equals(req.Method, "OPTIONS", StringComparison.Ordinal) ? RommApi.Preflight() : null,
    };

    public static bool IsRunning => _host.IsRunning;
    public static int CurrentPort => _host.CurrentPort;

    public static void Start(int port)
    {
        if (!LbModules.On(LbModule.RommServer))
        {
            LbLog.Once("romm", "server start refused (module off)");
            return;
        }
        if (_host.IsRunning) return;
        try
        {
            RommConfig.Reload();
            RegisterRoutes(_router);

            if (!RommConfig.HasPassword)
                LbLog.Warn("romm", "no password set — every request will be refused until one is (Options → Modules → RomM server)");

            _host.Start(port);
        }
        catch (Exception ex)
        {
            LbLog.Warn("romm", "start error: " + ex);
            Stop();
        }
    }

    public static void Stop()
    {
        _host.Stop();
        RommIdMap.Flush();   // ids allocated by the last requests reach disk before shutdown
    }

    /// <summary>Stop + Start on the current config, so a port or allow-list change takes effect on Apply.</summary>
    public static void Restart()
    {
        Stop();
        RommConfig.Reload();
        if (LbModules.On(LbModule.RommServer)) Start(RommConfig.Port);
    }

    // ── Route table ───────────────────────────────────────────────────────────

    /// <summary>Fills <paramref name="router"/> with the surface's routes. Takes the table rather than
    /// using the field so the self-test can drive the same registration on its own host.</summary>
    internal static void RegisterRoutes(Router router)
    {
        router.Clear();

        // Discovery: what every client asks first, and the only route that answers before authentication —
        // a client must be able to tell "wrong address" from "wrong password".
        router.Add(@"/api/heartbeat", RommApi.Heartbeat);
        router.Add(@"/api/heartbeat/metadata/(?<source>[^/]+)", RommApi.MetadataHeartbeat);
        router.Add(@"/api/config", RommApi.Config);

        // Auth. The pair routes come before the {id} ones: "pair" is not an integer, so the regexes are
        // disjoint, but registering them first keeps the intent obvious.
        router.Add(@"/api/login", RommAuthApi.Login);
        router.Add(@"/api/logout", RommAuthApi.Logout);
        router.Add(@"/api/token", RommAuthApi.Token);

        router.Add(@"/api/users", RommAuthApi.Users);
        router.Add(@"/api/users/me", RommAuthApi.Me);
        router.Add(@"/api/users/identifiers", RommAuthApi.UserIdentifiers);
        router.Add(@"/api/users/(?<id>\d+)", RommAuthApi.UserById);

        // Device pairing (RFC 8628-style) — what Argosy runs the moment you type the server address.
        // init/token are open by necessity (the device has no credential yet); the approval in between
        // is the security of the whole flow.
        router.Add(@"/api/auth/device/init", RommDeviceAuthApi.Init);
        router.Add(@"/api/auth/device/token", RommDeviceAuthApi.Token);
        router.Add(@"/api/auth/device/approve", RommDeviceAuthApi.Approve);
        router.Add(@"/api/auth/device/deny", RommDeviceAuthApi.Deny);
        router.Add(@"/api/auth/device/pending", RommDeviceAuthApi.ListPending);
        router.Add(@"/api/auth/device/pending/(?<user_code>[A-Za-z0-9\- ]+)", RommDeviceAuthApi.GetPending);

        router.Add(@"/api/client-tokens", RommAuthApi.TokensCollection);
        router.Add(@"/api/client-tokens/exchange", RommAuthApi.Exchange);
        router.Add(@"/api/client-tokens/pair/(?<code>[A-Za-z0-9]+)/status", RommAuthApi.PairStatus);
        router.Add(@"/api/client-tokens/(?<id>\d+)", RommAuthApi.TokenById);
        router.Add(@"/api/client-tokens/(?<id>\d+)/regenerate", RommAuthApi.Regenerate);
        router.Add(@"/api/client-tokens/(?<id>\d+)/pair", RommAuthApi.Pair);

        // Library. Literal sub-paths before the numeric {id} routes (disjoint regexes, explicit order).
        router.Add(@"/api/platforms", RommLibraryApi.Platforms);
        router.Add(@"/api/platforms/identifiers", RommLibraryApi.PlatformIdentifiers);
        router.Add(@"/api/platforms/supported", RommLibraryApi.PlatformsSupported);
        router.Add(@"/api/platforms/(?<id>\d+)", RommLibraryApi.PlatformById);
        router.Add(@"/api/roms", RommLibraryApi.Roms);
        router.Add(@"/api/roms/identifiers", RommLibraryApi.RomIdentifiers);
        router.Add(@"/api/roms/(?<id>\d+)", RommLibraryApi.RomById);
        router.Add(@"/api/roms/(?<id>\d+)/content/(?<file_name>[^/]+)", RommDownloadApi.Content);
        router.Add(@"/api/roms/(?<id>\d+)/user", RommUserApi.UpdateRomUser);
        // Same handler: clients in the wild use "props" for this write. Freegosy does, and answering
        // 501 there is what put "An unexpected error occurred" on its status picker.
        router.Add(@"/api/roms/(?<id>\d+)/props", RommUserApi.UpdateRomUser);

        // Collections: LB playlists read-only + the writable Favorites (IGame.Favorite is its membership).
        router.Add(@"/api/collections", RommUserApi.Collections);
        router.Add(@"/api/collections/(?<id>\d+)", RommUserApi.CollectionById);
        router.Add(@"/api/collections/(?<id>\d+)/roms", RommUserApi.AddToCollection);
        router.Add(@"/api/collections/(?<id>\d+)/roms/(?<rom_id>\d+)", RommUserApi.RemoveFromCollection);
        router.Add(@"/api/stats", RommLibraryApi.Stats);

        // Assets: saves + states share one implementation; screenshots are simpler. Literal verbs
        // (delete/identifiers) before the numeric {id} routes, sub-routes of {id} after it — the regexes
        // are anchored and disjoint, order just keeps intent readable.
        router.Add(@"/api/saves", RommAssetsApi.SavesCollection);
        router.Add(@"/api/saves/delete", RommAssetsApi.DeleteSaves);
        router.Add(@"/api/saves/(?<id>\d+)", RommAssetsApi.SaveById);
        router.Add(@"/api/saves/(?<id>\d+)/content", RommAssetsApi.Content);
        router.Add(@"/api/saves/(?<id>\d+)/downloaded", RommAssetsApi.ConfirmDownloaded);
        router.Add(@"/api/saves/(?<id>\d+)/track", RommAssetsApi.Track);
        router.Add(@"/api/saves/(?<id>\d+)/untrack", RommAssetsApi.Untrack);
        router.Add(@"/api/states", RommAssetsApi.StatesCollection);
        router.Add(@"/api/states/delete", RommAssetsApi.DeleteStates);
        router.Add(@"/api/states/(?<id>\d+)", RommAssetsApi.StateById);
        router.Add(@"/api/states/(?<id>\d+)/content", RommAssetsApi.Content);
        router.Add(@"/api/screenshots", RommAssetsApi.ScreenshotsCollection);
        router.Add(@"/api/screenshots/(?<id>\d+)", RommAssetsApi.ScreenshotById);
        router.Add(@"/api/screenshots/(?<id>\d+)/content", RommAssetsApi.Content);

        // Devices (the Grout sync contract).
        router.Add(@"/api/devices", RommDevicesApi.Collection);
        router.Add(@"/api/devices/(?<id>[0-9a-fA-F-]{36})", RommDevicesApi.ById);

        // Covers ride the same signed media proxy as the theme surfaces (the HMAC is the access control,
        // so no auth gate here — image loaders often drop the Authorization header).
        router.Add(@"/api/media/(?<token>[A-Z0-9]+)\.(?<sig>[A-Z0-9]+)\.(?<ext>[a-z0-9]{1,6})", MediaProxy.Handle);

        // Everything else under /api answers in RomM's own error shape rather than a bare 404, so a client
        // logs something actionable instead of "unexpected response".
        router.Add(@"/api/(?<rest>.*)", RommApi.NotImplemented);

        // The approval surface RomM serves from its Vue UI. Argosy points its QR at this path on the
        // origin it was given, so it has to live on THIS listener.
        router.Add(@"/pair/device", RommDeviceAuthApi.PairPage);

        // A human who opens the port in a browser deserves an explanation, not a blank 404.
        router.Add(@"/", RommApi.Landing);
        // Logged like the /api 501s: a client fetching a path we never serve — a cover URL shaped
        // differently than we emit it, say — is otherwise completely invisible from this side.
        router.Add(@"/(?<path>.*)", ctx =>
        {
            LbLog.Info("romm", $"unmatched: {ctx.Request?.Method} {ctx.Request?.Path}");
            return RommApi.Error(404, "Not found");
        });
    }

    // Native clients need no CORS, but a browser-based one does, and the cost is two headers. Credentials
    // are never cookies here — auth is always an Authorization header — so a wildcard origin is safe.
    private static void Decorate(HttpRequest req, HttpResponse resp, bool degraded)
    {
        if (resp == null) return;
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, X-Requested-With";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS";
        resp.Headers["Access-Control-Expose-Headers"] = "Content-Disposition, Content-Range, Accept-Ranges";

        // Last thing on the way out, so the line carries the real status and length.
        RommTrace.Request(req, resp);
    }
}

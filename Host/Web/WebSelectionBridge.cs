// What the KIOSK is looking at, told to the plugins as a selection.
//
// A plugin that follows the selection — a second-screen marquee, say — has one question it can ask:
// IStateManager's selected platform and selected games. Those answer for the desktop window, which is right
// until the user drives LiteBox through one of its web frontends instead. Then the marquee keeps showing
// whatever the desktop list happens to be pointing at, and follows nothing.
//
// KIOSK ONLY, and that restriction is the whole design. A kiosk IS the frontend: fullscreen, on this
// machine, one at a time — so what it shows is what "selected" means, exactly as the desktop window would.
// An ordinary browser is a guest: there can be several at once, on other machines, and letting any of them
// move the marquee on this one would be wrong. The two are told apart by the User-Agent marker the kiosk
// sets (WebParentalState.IsKioskRequest), which also covers ExtendDB's kiosk, not just ours.
//
// Nothing is asked of the frontends. They already fetch a game's detail.json when it becomes current, and a
// platform's games.json on entering it — the requests ARE the selection, so they are read as they pass
// through the server rather than adding an endpoint the pages would have to remember to call. The cost is
// that a page which prefetched aggressively would look like browsing; the last-key check below absorbs the
// repeats, and the frontends fetch on navigation.
//
// Desktop and kiosk take turns rather than merging: whoever moved last is the one being watched, so a click
// in the desktop window takes the answer straight back (MainWindow calls DesktopTookOver).

#nullable enable

using System;
using System.Text.RegularExpressions;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class WebSelectionBridge
{
    // /bigbox/data/games/<id>/detail.json  ·  /launchbox/data/games/<id>/detail.json
    private static readonly Regex GameRx = new(
        @"^/(?:bigbox|launchbox)/data/games/(?<id>[^/]+)/detail\.json$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // …/data/(platforms|playlists|categories)/<slug>/games.json
    private static readonly Regex EntityRx = new(
        @"^/(?:bigbox|launchbox)/data/(?<kind>platforms|playlists|categories)/(?<slug>[^/]+)/games\.json$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly object Lock = new();
    private static string _lastKey = "";
    private static IGame? _game;
    private static IPlatform? _platform;

    /// <summary>True while the kiosk is the surface that moved last — the desktop takes it back by selecting.</summary>
    public static bool Active { get; private set; }

    public static IGame[] Games { get { lock (Lock) return _game != null ? new[] { _game } : Array.Empty<IGame>(); } }
    public static IPlatform? Platform { get { lock (Lock) return _platform; } }

    /// <summary>The desktop window changed selection: it is the surface being watched again.</summary>
    public static void DesktopTookOver() => Active = false;

    /// <summary>Read one request on its way to a handler. Cheap and silent for everything that is not a
    /// kiosk asking for a game or a list — two anchored regexes and, most of the time, no match at all.</summary>
    public static void Observe(HttpRequest? req) => Match(req, req?.Path);

    /// <summary>The ping a frontend sends when it served a view from ITS OWN cache, so no request went out
    /// for us to read. It carries the path it would have fetched — the one parser below then applies, so the
    /// two routes in cannot drift apart. Answers 204 and costs a header round-trip; the frontends only send
    /// it on a cache hit, and only from a kiosk.</summary>
    public static HttpResponse Ping(RouteContext ctx)
    {
        Match(ctx.Request, ctx.Request?.GetQuery("p"));
        return new HttpResponse { StatusCode = 204, StatusText = "No Content" };
    }

    private static void Match(HttpRequest? req, string? rawPath)
    {
        if (req == null || string.IsNullOrEmpty(rawPath)) return;
        try
        {
            if (!WebParentalState.IsKioskRequest(req)) return;
            string path = rawPath;
            int q = path.IndexOf('?');           // a ping carries the query too; the router strips it, we do not
            if (q >= 0) path = path.Substring(0, q);

            var m = GameRx.Match(path);
            if (m.Success) { Adopt("g|" + m.Groups["id"].Value, () => ArchiveListingApi.ResolveGame(m.Groups["id"].Value), null); return; }

            m = EntityRx.Match(path);
            if (!m.Success) return;
            string kind = m.Groups["kind"].Value, slug = m.Groups["slug"].Value;
            // Entering a list means the list is what is selected, not the game left over from the previous
            // one — the same rule the desktop follows when a tree node is clicked.
            Adopt(kind + "|" + slug, null, () => ResolveEntity(kind, slug));
        }
        catch { }   // a selection is never worth failing a page request over
    }

    private static void Adopt(string key, Func<IGame?>? game, Func<IPlatform?>? platform)
    {
        lock (Lock)
        {
            if (key == _lastKey && Active) return;   // same view again (a repeat, a prefetch) — not a move
            _lastKey = key;
            if (game != null) { _game = game(); }
            else { _game = null; _platform = platform?.Invoke(); }
            if (game != null && _platform == null && _game != null)
                _platform = SafePlatformOf(_game);
            Active = true;
        }
        EventBus.FireNamed("SelectionChanged");
    }

    private static IPlatform? SafePlatformOf(IGame g)
    {
        try
        {
            string name = g.Platform ?? "";
            return name.Length == 0 ? null : PluginHelper.DataManager?.GetPlatformByName(name);
        }
        catch { return null; }
    }

    /// <summary>A platform by slug, or — for a playlist or a category, which are not platforms — the
    /// stand-in MainWindow uses for the same reason: the question only has an IPlatform-shaped answer.</summary>
    private static IPlatform? ResolveEntity(string kind, string slug)
    {
        string name = "";
        try { name = OwnedDataProvider.PlatformNameForSlug(slug) ?? ""; } catch { }
        if (name.Length == 0) name = Uri.UnescapeDataString(slug ?? "").Replace('-', ' ');
        if (name.Length == 0) return null;

        if (string.Equals(kind, "platforms", StringComparison.OrdinalIgnoreCase))
        {
            try { if (PluginHelper.DataManager?.GetPlatformByName(name) is { } p) return p; } catch { }
        }
        return MainWindow.EntityPlatformFor(
            string.Equals(kind, "playlists", StringComparison.OrdinalIgnoreCase) ? "Playlists" : "Platform Categories",
            name);
    }
}

// The web frontends' window onto the notification center — so a notification raised while the kiosk (or a
// browser on the litebox/bigbox site) covers the screen is SEEN there, not lost behind it.
//
// Transport matches the rest of the web module: no push channel, the page POLLS. vendor\notify.js calls
// GET /api/notifications/events?since=<seq> every ~2.5 s (the recent-epoch cadence); the server keeps a
// small ring of {seq, type, id} events plus the live notification objects, and the page replays whatever
// happened since its last seq — raised → show a toast, updated → retext it, dismissed/removed → drop it.
// A first call passes since=-1 and gets only the current seq: no backlog replay, a freshly opened page
// starts clean (older notifications are the NATIVE bell's job — the web shows what arrives while it looks).
//
// Only the LITEBOX theme loads notify.js; the BigBox (couch) surface deliberately doesn't — a notification
// raised while its kiosk is up goes straight to the native bell. Native-popup suppression is keyed on the
// HOST KIOSK WINDOW alone (NotificationUi.ShowOnUi): a plain browser on this API never silences the
// desktop.
//
// Buttons work from the web too: POST /api/notifications/<id>/action/<n> runs the plugin callback ON THE
// UI THREAD (plugins expect it — the native toast runs them there as well).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Forms;
using LbApiHost.Host.Notifications;
using LiteBox.Notifications;

namespace LbApiHost.Host.Web;

internal static class NotificationsApi
{
    private static readonly object _lock = new();
    private static readonly List<(long Seq, string Type, string Id)> _events = new();
    private static readonly Dictionary<string, LiteBoxNotification> _byId = new();
    private static long _seq;
    private static bool _wired;
    private const int MaxEvents = 200;

    /// <summary>Sequence number when the current kiosk opened, or -1 when none is open.
    ///
    /// Closes a real gap: the host suppresses native popups the moment the kiosk WINDOW exists, but the
    /// kiosk's page only starts polling once WebView2 has booted and loaded it — several seconds later.
    /// A notification raised in between would be shown by nobody (suppressed natively, and dropped by the
    /// page, whose first poll deliberately takes a baseline instead of replaying history). So the KIOSK's
    /// first poll replays from this floor instead: exactly the notifications its own window silenced.
    /// A plain browser still gets a clean baseline — it silences nothing, so it has nothing to catch up.</summary>
    private static long _kioskFloor = -1;

    /// <summary>Called when a kiosk window opens/closes (see WebKioskWindow). Wiring the center here too:
    /// without it, a notification raised before ANY page has polled is never recorded, so there would be
    /// nothing to replay for the very case this exists to cover.</summary>
    public static void KioskOpened()
    {
        EnsureWired();
        lock (_lock) _kioskFloor = _seq;
    }

    public static void KioskClosed()
    {
        lock (_lock) _kioskFloor = -1;
    }

    // ── Center subscription ──────────────────────────────────────────────────────────────────────────

    private static void EnsureWired()
    {
        lock (_lock)
        {
            if (_wired) return;
            _wired = true;
        }
        NotificationCenter.Raised += n => Push("raised", n);
        NotificationCenter.Updated += n => Push("updated", n);
        NotificationCenter.Read += n => Push("read", n);
        NotificationCenter.Unread += n => Push("unread", n);
        NotificationCenter.Dismissed += n => Push("dismissed", n);
        NotificationCenter.Removed += n => Push("removed", n);
    }

    private static void Push(string type, LiteBoxNotification n)
    {
        try
        {
            lock (_lock)
            {
                _events.Add((++_seq, type, n.Id));
                _byId[n.Id] = n;
                if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
                // The id map only exists to serialize events still in the ring — prune what fell out.
                if (_byId.Count > MaxEvents * 2)
                {
                    var live = new HashSet<string>(_events.Select(e => e.Id));
                    foreach (var dead in _byId.Keys.Where(k => !live.Contains(k)).ToArray()) _byId.Remove(dead);
                }
            }
        }
        catch { /* never break the center's event fan-out */ }
    }

    // ── GET /api/notifications/events?since=N ────────────────────────────────────────────────────────

    public static HttpResponse HandleEvents(RouteContext ctx)
    {
        EnsureWired();
        long since = long.TryParse(ctx.Request.GetQuery("since"), out var s) ? s : -1;

        // First poll (since < 0): a page normally starts clean — history belongs to the bell. The one
        // exception is the KIOSK, whose window has been silencing native popups since before its page
        // existed; it replays from that moment so nothing falls between the two displays.
        if (since < 0 && WebParentalState.IsKioskRequest(ctx.Request))
        {
            lock (_lock) since = _kioskFloor;
        }

        (long Seq, string Type, string Id)[] evs;
        LiteBoxNotification[] items;
        lock (_lock)
        {
            evs = since < 0
                ? Array.Empty<(long, string, string)>()
                : _events.Where(e => e.Seq > since).ToArray();
            var ids = new HashSet<string>(evs.Select(e => e.Id));
            items = ids.Select(id => _byId.TryGetValue(id, out var n) ? n : null)
                       .Where(n => n != null).Select(n => n!).ToArray();
            since = _seq;
        }

        return HttpResponse.Json(JsonSerializer.Serialize(new
        {
            seq = since,
            unread = NotificationCenter.UnreadCount,
            events = evs.Select(e => new { seq = e.Seq, type = e.Type, id = e.Id }),
            items = items.Select(Item),
        }));
    }

    private static object Item(LiteBoxNotification n) => new
    {
        id = n.Id,
        message = n.Message,
        error = n.IsError,
        progress = n.IsInProgress,
        read = n.IsRead,
        raisedMs = new DateTimeOffset(n.DateRaised).ToUnixTimeMilliseconds(),
        // The countdown the native popup would have used; <= 0 means sticky (progress / has actions).
        lifeSpan = NotificationSettings.EffectiveSeconds(n),
        actions = n.Actions.Select(a => a.Label).ToArray(),
    };

    // ── POST /api/notifications/test ─────────────────────────────────────────────────────────────────

    /// <summary>Raises one notification of each interactive shape — the web-side twin of Options ▸
    /// "Send a test notification", and the only way to exercise the kiosk path (the native Options window
    /// is unreachable behind a fullscreen kiosk). Loopback-only like the rest of the server by default.</summary>
    public static HttpResponse HandleTest(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.BadRequest("POST only");
        EnsureWired();
        NotificationCenter.Info("This is a LiteBox notification.");
        NotificationCenter.Input("Notification with actions.", new[]
        {
            new KeyValuePair<string, Action>("Say hi", () => NotificationCenter.Info("Hi.")),
            new KeyValuePair<string, Action>("Raise an error", () => NotificationCenter.Error("This is an error notification.")),
        });
        return HttpResponse.Json("{\"ok\":true}");
    }

    // ── POST /api/notifications/<id>/read|unread|dismiss|remove ──────────────────────────────────────

    public static HttpResponse HandleVerb(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.BadRequest("POST only");
        var n = Find(ctx.GetRoute("id"));
        if (n == null) return HttpResponse.NotFound("no such notification");
        switch (ctx.GetRoute("verb"))
        {
            case "read": NotificationCenter.MarkRead(n); break;
            case "unread": NotificationCenter.MarkUnread(n); break;
            case "dismiss": NotificationCenter.Dismiss(n); break;
            case "remove": NotificationCenter.Remove(n); break;
            default: return HttpResponse.BadRequest("unknown verb");
        }
        return HttpResponse.Json("{\"ok\":true}");
    }

    // ── POST /api/notifications/<id>/action/<index> ──────────────────────────────────────────────────

    public static HttpResponse HandleAction(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.BadRequest("POST only");
        var n = Find(ctx.GetRoute("id"));
        if (n == null) return HttpResponse.NotFound("no such notification");
        var act = n.Actions.ElementAtOrDefault(ctx.GetRouteInt("index", -1));
        if (act == null) return HttpResponse.NotFound("no such action");

        // Same sequence as a native button click, marshalled to the UI thread: the callback belongs to
        // whoever raised the notification (often a plugin) and must neither run on a web worker thread
        // nor take the server down with it.
        RunOnUiThread(() =>
        {
            NotificationCenter.MarkRead(n);
            if (act.DismissOnClick) NotificationCenter.Dismiss(n);
            try { act.Run(); }
            catch (Exception ex) { Console.WriteLine("[notify] web action '" + act.Label + "' threw: " + ex); }
        });
        return HttpResponse.Json("{\"ok\":true}");
    }

    private static LiteBoxNotification? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : NotificationCenter.All.FirstOrDefault(n => n.Id == id);

    private static void RunOnUiThread(Action a)
    {
        try
        {
            var f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x is LbApiHost.Host.MainWindow)
                 ?? Application.OpenForms.Cast<Form>().FirstOrDefault();
            if (f is { IsHandleCreated: true, IsDisposed: false }) { f.BeginInvoke(a); return; }
        }
        catch { }
        try { a(); } catch { }   // headless run: no UI thread to owe anyone
    }
}

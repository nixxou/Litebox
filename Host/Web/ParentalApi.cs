// The web-side parental endpoints:
//
//   GET  /api/parental/state   → { active, locked, canUnlock }
//   POST /api/parental/unlock   body {"pin":"…"} → { success } / { success:false, reason } + sets the cookie
//   POST /api/parental/lock     → { success } + clears the cookie
//
// Thin handlers over WebParentalState (LiteBox adapter) + the configured PIN (Host/Data/BigBoxPin). unlock
// compares the posted PIN to BigBoxPin.Current() and, on a match, sets the signed per-client unlock cookie
// (12 h). lock clears it. With no PIN configured the web is treated as unlocked, so unlock is a no-op there.
//
// Clean-room LiteBox rewrite of ExtendDB's ParentalApi — no plugin lockout mirror, no kiosk in-memory flag;
// the cookie is the whole state.

#nullable enable

using System;
using System.Text.Json;

namespace LbApiHost.Host.Web;

internal static class ParentalApi
{
    /// <summary>How long a web unlock survives without a re-PIN. 12 hours.</summary>
    private const int UnlockCookieMaxAgeSeconds = 12 * 60 * 60;

    public static HttpResponse HandleState(RouteContext ctx)
    {
        var s = WebParentalState.From(ctx.Request);
        return HttpResponse.Json(JsonSerializer.Serialize(new
        {
            active = s.IsActive,
            locked = s.IsLocked,
            canUnlock = s.CanUnlock,
        }));
    }

    public static HttpResponse HandleUnlock(RouteContext ctx)
    {
        if (!IsPost(ctx)) return HttpResponse.PlainText("POST only", 405);

        var s = WebParentalState.From(ctx.Request);
        if (!s.CanUnlock)
            // No PIN configured (or parental off) → nothing to unlock against.
            return HttpResponse.Json(JsonSerializer.Serialize(new { success = false, reason = "not-allowed" }));

        var pin = ExtractPin(ctx.Request?.Body);
        if (string.IsNullOrEmpty(pin))
            return HttpResponse.Json(JsonSerializer.Serialize(new { success = false, reason = "no-pin" }));

        var expected = WebParentalState.ConfiguredPin();
        if (!string.Equals(pin, expected, StringComparison.Ordinal))
            return HttpResponse.Json(JsonSerializer.Serialize(new { success = false, reason = "wrong-pin" }));

        var ok = HttpResponse.Json(JsonSerializer.Serialize(new { success = true }));
        ok.SetCookie(WebParentalState.UnlockCookie, WebParentalState.UnlockCookieValue(),
                     UnlockCookieMaxAgeSeconds, httpOnly: true);
        return ok;
    }

    public static HttpResponse HandleLock(RouteContext ctx)
    {
        if (!IsPost(ctx)) return HttpResponse.PlainText("POST only", 405);
        var r = HttpResponse.Json(JsonSerializer.Serialize(new { success = true }));
        r.ClearCookie(WebParentalState.UnlockCookie);
        return r;
    }

    private static bool IsPost(RouteContext ctx)
        => string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pulls "pin" out of a tiny {"pin":"1234"} body without a full deserializer. Null when absent.</summary>
    private static string? ExtractPin(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        int k = body!.IndexOf("\"pin\"", StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int col = body.IndexOf(':', k);
        if (col < 0) return null;
        int qStart = body.IndexOf('"', col + 1);
        if (qStart < 0) return null;
        int qEnd = body.IndexOf('"', qStart + 1);
        if (qEnd < 0) return null;
        return body.Substring(qStart + 1, qEnd - qStart - 1);
    }
}

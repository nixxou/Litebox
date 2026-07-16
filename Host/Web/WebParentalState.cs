// Per-request snapshot of the embedded server's parental-control state — the LiteBox adapter over
// Host/Media/ParentalBridge + the configured PIN (Host/Data/BigBoxPin), plus a per-CLIENT unlock cookie.
//
// LiteBox is a "LaunchBox" host to ExtendDB (never BigBox), so the web uses the cookie + PIN unlock path, not
// the BigBox global-lock mirror. The decision matrix:
//   • parental not configured (ParentalBridge not Enabled) → IsActive=false: no filtering, no lock UI.
//   • configured but NO PIN set                            → IsActive=true, CanUnlock=false, IsLocked=FALSE.
//        (Deliberate LiteBox deviation from the plugin's "no PIN ⇒ forced-locked": with no PIN there is
//         nothing to unlock against, and BigBoxPin is the only credential the web owns — so treat as unlocked
//         rather than permanently locking the web with no way out.)
//   • configured AND PIN set                               → IsActive=true, CanUnlock=true, IsLocked from cookie.
//
// The unlock cookie value is an unforgeable per-install signed marker (MediaTokenSecret HMAC key): a child
// can't reproduce it without the DPAPI-protected key, so it can't self-unlock by hand-writing the cookie.

#nullable enable

using LbApiHost.Host.Data;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal sealed class WebParentalState
{
    /// <summary>Cookie name remembering a client's unlock decision.</summary>
    public const string UnlockCookie = "litebox_unlocked";

    // Domain-separated marker payload. Constant, NOT a secret — the HMAC key is what makes the cookie unforgeable.
    private const string UnlockPurpose = "litebox-web-parental-unlock-v1";

    /// <summary>Parental control affects this request at all (configured).</summary>
    public bool IsActive { get; }
    /// <summary>This client should currently see filtered content.</summary>
    public bool IsLocked { get; }
    /// <summary>The in-page PIN-unlock flow is wired (a PIN is configured).</summary>
    public bool CanUnlock { get; }

    private WebParentalState(bool active, bool locked, bool canUnlock)
    {
        IsActive = active; IsLocked = locked; CanUnlock = canUnlock;
    }

    /// <summary>The signed cookie value a successful unlock sets (and that <see cref="From"/> checks for).</summary>
    public static string UnlockCookieValue() => MediaTokenSecret.SignedMarker(UnlockPurpose);

    /// <summary>The configured parental PIN (BigBox's own, read/managed by LiteBox), or "" when none is set.</summary>
    public static string ConfiguredPin() => BigBoxPin.Current();

    /// <summary>Builds the effective state from the parental config + this request's unlock cookie.</summary>
    public static WebParentalState From(HttpRequest? req)
    {
        // Not configured → nothing to enforce on any surface.
        if (!ParentalBridge.Enabled)
            return new WebParentalState(false, false, false);

        bool hasPin = !string.IsNullOrEmpty(ConfiguredPin());
        if (!hasPin)
            return new WebParentalState(active: true, locked: false, canUnlock: false);

        // PIN configured: the per-client cookie decides. Absent / invalid → locked.
        var cookie = req?.GetCookie(UnlockCookie);
        bool unlocked = MediaTokenSecret.VerifyMarker(UnlockPurpose, cookie);
        return new WebParentalState(active: true, locked: !unlocked, canUnlock: true);
    }

    // ── Filtering helpers (consumed by the page/data slices; media proxy is NOT gated, matching the source) ──

    /// <summary>Adult-mode value to actually apply: forced 0 (no NSFW / no blur) while locked.</summary>
    public int EffectiveAdult(int userAdult) => IsLocked ? 0 : userAdult;

    /// <summary>A game with this ESRB/age rating should be visible. Allow-all when unlocked; delegate to the
    /// shared rule engine when locked so the web matches the rest of LiteBox.</summary>
    public bool IsRatingAllowed(string rating) => !IsLocked || ParentalBridge.IsRatingAllowed(rating);

    /// <summary>A platform / category / playlist with this name must be hidden from the tree (locked only).</summary>
    public bool IsHidden(string name) => IsLocked && ParentalBridge.IsNameHidden(name);
}

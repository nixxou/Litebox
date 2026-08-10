// Per-request snapshot of the embedded server's parental-control state — the LiteBox adapter over
// Host/Media/ParentalBridge + the configured PIN (Host/Data/BigBoxPin), plus a per-CLIENT unlock cookie.
//
// LiteBox is a "LaunchBox" host to ExtendDB (never BigBox), so the web uses the cookie + PIN unlock path, not
// the BigBox global-lock mirror. The decision matrix:
//   • parental not configured (ParentalBridge not Enabled) → IsActive=false: no filtering, no lock UI.
//   • configured but NO PIN set                            → IsActive=true, CanUnlock=false, IsLocked=TRUE.
//        (FAIL-CLOSED, plugin parity: the state is an edge — the panel forces a PIN on first enable —
//         so if the PIN vanished externally the web stays safe rather than open.)
//   • configured AND PIN set                               → IsActive=true, CanUnlock=true, IsLocked from
//        the client's unlock state.
//
// Per-client unlock state comes in TWO flavours:
//   • Browser: the signed cookie (12 h) — unforgeable per-install marker (MediaTokenSecret HMAC key).
//   • KIOSK (the embedded WebKioskWindow, detected by a User-Agent marker the window sets): SHARES the
//     desktop's runtime lock (Host/Parental/ParentalFilter.Locked) — it's the same user on the same machine,
//     so the kiosk web and the host GUI lock/unlock together. Unlocking from the kiosk unlocks the desktop
//     and vice-versa; closing the kiosk does NOT re-lock (a window close is not a "lock" gesture).
//
// ForceWebHideAll: when configured, a LOCKED web client gets the block-all treatment (rating checks all
// fail, adult forced 0, the rating SQL short-circuits to match nothing).

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

    /// <summary>Installing a store game must be PIN-gated for THIS client: active, locked (per-client — cookie
    /// for a browser, the shared desktop lock for the kiosk) AND "block install while locked" is configured.
    /// Mirrors the rest of the web gating (per-client lock), unlike the desktop ParentalFilter.InstallNeedsUnlock.</summary>
    public bool InstallNeedsUnlock
        => IsActive && IsLocked && Parental.ParentalConfig.Instance.BlockInstallWhenLocked;

    private WebParentalState(bool active, bool locked, bool canUnlock)
    {
        IsActive = active; IsLocked = locked; CanUnlock = canUnlock;
    }

    /// <summary>The signed cookie value a successful unlock sets (and that <see cref="From"/> checks for).</summary>
    public static string UnlockCookieValue() => MediaTokenSecret.SignedMarker(UnlockPurpose);

    /// <summary>The configured parental PIN (BigBox's own, read/managed by LiteBox), or "" when none is set.</summary>
    public static string ConfiguredPin() => BigBoxPin.Current();

    // ── Kiosk (embedded WebView2 window) ────────────────────────────────────────
    // The kiosk window appends this marker to its WebView2 User-Agent; kiosk clients share the desktop's
    // runtime lock (Host/Parental/ParentalFilter) instead of the browser cookie — same user, same machine.

    public const string KioskUaMarker = "LiteBoxKiosk";

    public static bool IsKioskRequest(HttpRequest? req)
    {
        try { return (req?.GetHeader("User-Agent") ?? "").Contains(KioskUaMarker, System.StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>Builds the effective state from the parental config + this request's unlock state.</summary>
    public static WebParentalState From(HttpRequest? req)
    {
        // Not configured → nothing to enforce on any surface.
        if (!ParentalBridge.Enabled)
            return new WebParentalState(false, false, false);

        bool hasPin = !string.IsNullOrEmpty(ConfiguredPin());
        if (!hasPin)
            return new WebParentalState(active: true, locked: true, canUnlock: false);   // fail-closed

        // Kiosk shares the desktop runtime lock; a normal browser uses its signed cookie.
        bool unlocked = IsKioskRequest(req)
            ? !Parental.ParentalFilter.Locked
            : MediaTokenSecret.VerifyMarker(UnlockPurpose, req?.GetCookie(UnlockCookie));
        return new WebParentalState(active: true, locked: !unlocked, canUnlock: true);
    }

    // ── Filtering helpers (consumed by the page/data slices; media proxy is NOT gated, matching the source) ──

    /// <summary>Force-web block-all applies to this client (locked + configured): hide EVERYTHING.</summary>
    public bool ForceAllWeb => IsLocked && ParentalBridge.ForceAllConfigured;

    /// <summary>Adult-mode value to actually apply: forced 0 (no NSFW / no blur) while locked.</summary>
    public int EffectiveAdult(int userAdult) => IsLocked ? 0 : userAdult;

    /// <summary>A game with this ESRB/age rating should be visible. Allow-all when unlocked; delegate to the
    /// shared rule engine when locked so the web matches the rest of LiteBox.</summary>
    public bool IsRatingAllowed(string rating)
        => !IsLocked || (!ForceAllWeb && ParentalBridge.IsRatingAllowed(rating));

    /// <summary>A platform / category / playlist with this name must be hidden from THIS client. The list is
    /// chosen by the CLIENT's lock state (cookie for a browser, the shared desktop lock for the kiosk) — NOT
    /// the desktop runtime lock, so a locked browser hides the LOCKED list even while the desktop is unlocked.
    /// Both lists apply: hide-when-LOCKED while locked, hide-when-UNLOCKED while unlocked (unlike the vanilla
    /// LB/BB native filter, which enforces the locked list only — see the panel note).</summary>
    public bool IsHidden(string name)
    {
        if (!IsActive || string.IsNullOrEmpty(name)) return false;
        var cfg = Parental.ParentalConfig.Instance;
        var list = IsLocked ? cfg.HiddenPlatformsBigBoxOn : cfg.HiddenPlatformsBigBoxOff;
        foreach (var n in list)
            if (string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>SQL fragment enforcing the rating RULES on a Games query (column "g".ESRB), so lists,
    /// counts and paging all match — plugin BuildEsrbSqlFilter parity. Null = allow-all (unlocked);
    /// "0" = match nothing (force-all, or whitelist with zero rules); else a LIKE chain
    /// (wildcards * ? → % _, ESCAPE '\'). Safe to inline: rules are escaped, no user input.</summary>
    public string? EsrbSqlFilter()
    {
        if (!IsLocked) return null;
        if (ForceAllWeb) return "0";
        var cfg = Parental.ParentalConfig.Instance;
        var pieces = new System.Collections.Generic.List<string>();
        foreach (var rule in cfg.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule)) continue;
            var like = rule.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")
                           .Replace('*', '%').Replace('?', '_')
                           .Replace("'", "''");
            pieces.Add($"COALESCE(g.ESRB,'') LIKE '{like}' ESCAPE '\\'");
        }
        bool whitelist = cfg.Mode == Parental.ParentalMode.Whitelist;
        if (pieces.Count == 0) return whitelist ? "0" : null;   // whitelist+no rules = show nothing
        string ors = "(" + string.Join(" OR ", pieces) + ")";
        return whitelist ? ors : "NOT " + ors;
    }
}

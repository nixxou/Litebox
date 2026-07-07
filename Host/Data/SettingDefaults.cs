// The default value of a global setting when NEITHER LaunchBox's XML NOR LiteBox's DB
// carries it (fresh library, or a key the user never touched). ONE source of truth so the
// runtime (GameplaySettings), the options page (LbGlobalOptions) and the routed store all
// agree — otherwise a value read here and displayed there could silently differ.
//
// These mirror LaunchBox's own factory defaults so a library behaves identically whether the
// key is read from shared XML (13.28) or routed to our DB (13.27). Where LB changed a default
// between versions, key the entry on LbVersion (none needed today — the display-screen
// defaults are stable across 13.27/13.28).

#nullable enable

using System.Collections.Generic;

namespace LbApiHost.Host.Data;

internal static class SettingDefaults
{
    // key → default (string form, "true"/"false" for booleans). Absent key ⇒ caller's own
    // fallback still applies, so this is additive, never a hard requirement.
    private static readonly Dictionary<string, string> _d = new(System.StringComparer.Ordinal)
    {
        ["UseStartupScreen"]                    = "true",
        ["StartupScreenPostLaunchDisplayTime"]  = "1000",
        ["ShutdownScreenPostReadyDisplayTime"]  = "1000",
        ["HideMouseCursorOnStartupScreens"]     = "true",
        ["ForceFrontendFocusOnShutdown"]        = "true",
        ["UsePauseScreen"]                      = "true",
        ["PauseScreenFading"]                   = "true",
        ["PauseScreenMuting"]                   = "true",
    };

    public static string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;

    public static string Get(string key, string fallback) => _d.TryGetValue(key, out var v) ? v : fallback;

    public static bool GetBool(string key, bool fallback = false)
        => _d.TryGetValue(key, out var v) ? string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase) : fallback;

    public static int GetInt(string key, int fallback)
        => _d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
}

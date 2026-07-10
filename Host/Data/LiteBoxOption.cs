// Reusable resolution + storage for LiteBox-OWN options that LaunchBox has no field for
// (StartupStayOnTop, per-emulator ScreenCaptureKey, and whatever we add next). These live in
// litebox-options.db, scope by entity — a per-entity row OVERRIDES the global value, its
// absence INHERITS it. Tri-state by design: no row = inherit, a row = an explicit override
// (a bool "true"/"false", or a string incl. an explicit "disabled" sentinel).
//
// The resolution order mirrors the LB-native tier (game < emulator < global): a per-GAME override
// wins over a per-emulator one, which wins over the global. One place so every consumer resolves
// identically. Consumers that have a game in hand MUST pass its id, or the per-game overrides the
// Edit Game LiteBox tabs write are silently ignored.

#nullable enable

namespace LbApiHost.Host.Data;

internal static class LiteBoxOption
{
    public const string ScopeEmulator = "emulator";
    public const string ScopeGame     = "game";
    public const string ScopePlatform = "platform";

    /// <summary>The raw per-entity override, or null = inherit (no row). Drives the tri-state UI.</summary>
    public static string? GetOverride(string scope, string entityId, string key)
        => string.IsNullOrEmpty(entityId) ? null : LiteBoxOptionsDb.Get(scope, entityId, key);

    /// <summary>Set (non-empty) or clear (null/empty ⇒ back to inherit) a per-entity override.</summary>
    public static void SetOverride(string scope, string entityId, string key, string? value)
        => LiteBoxOptionsDb.Set(scope, entityId, key, value);

    /// <summary>Effective bool resolved game → emulator → global. Pass <paramref name="gameId"/> when a
    /// game is in hand so its per-game override (Edit Game LiteBox tabs) is honoured.</summary>
    public static bool ResolveBool(string key, string? emulatorId, bool globalValue, string? gameId = null)
    {
        var g = GetOverride(ScopeGame, gameId ?? "", key);
        if (!string.IsNullOrEmpty(g)) return string.Equals(g, "true", System.StringComparison.OrdinalIgnoreCase);
        var ov = GetOverride(ScopeEmulator, emulatorId ?? "", key);
        return string.IsNullOrEmpty(ov) ? globalValue
                                        : string.Equals(ov, "true", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Effective string resolved game → emulator → global. An override of the DISABLED sentinel
    /// resolves to empty (feature off). Pass <paramref name="gameId"/> to honour the per-game override.</summary>
    public static string ResolveString(string key, string? emulatorId, string globalValue, string? gameId = null)
    {
        var g = GetOverride(ScopeGame, gameId ?? "", key);
        if (!string.IsNullOrEmpty(g)) return g == Disabled ? "" : g;
        var ov = GetOverride(ScopeEmulator, emulatorId ?? "", key);
        if (string.IsNullOrEmpty(ov)) return globalValue;
        return ov == Disabled ? "" : ov;
    }

    /// <summary>Sentinel stored for a string option the user EXPLICITLY turned off for this entity
    /// (distinct from "no row = inherit"). ScreenCapture already treats "None" as disabled.</summary>
    public const string Disabled = "None";
}

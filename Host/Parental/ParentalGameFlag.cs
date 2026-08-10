// ─────────────────────────────────────────────────────────────────────────────
// Per-game "requires parental rights" flag.
// ─────────────────────────────────────────────────────────────────────────────
//
// A game an editor marks in Edit Game (next to Broken / Hide / Favorite) as requiring
// parental rights. It is NOT a LaunchBox IGame property, so it lives in LiteBox's own
// Options DB (Game scope, key "ParentalBlocked"; see Host/Data/OptionKeys) — LiteBox is
// the sole writer, and the row is swept automatically when its game leaves the library.
//
// Two consumers read it:
//   • the native LiteBox surfaces — a blocked game is hidden while parental is Active,
//     ON TOP OF the rating rules (Host/Parental/ParentalFilter.IsGameAllowed);
//   • the native ASI — it cannot read this SQLite store, so the whole set of blocked IDs
//     is exported to a flat sidecar it consumes (WS7 / Host/Parental config export).
//
// The key is Hot (read on the visibility path while locked); AllBlockedIds() is served
// from the hot cache.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Parental;

internal static class ParentalGameFlag
{
    internal const string Key = "ParentalBlocked";

    /// <summary>True when this game is flagged as requiring parental rights.</summary>
    public static bool IsBlocked(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        try { return LiteBoxOptionsDb.GetBool(LiteBoxOption.ScopeGame, gameId!, Key) == true; }
        catch { return false; }
    }

    /// <summary>Set or clear the flag. Clearing removes the row (= false / inherit-nothing).</summary>
    public static void SetBlocked(string? gameId, bool blocked)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        try { LiteBoxOptionsDb.SetBool(LiteBoxOption.ScopeGame, gameId!, Key, blocked ? true : (bool?)null); }
        catch { }
    }

    /// <summary>Every game ID currently flagged blocked — the flat export to the ASI, and any
    /// bulk native check. Empty when the store is unavailable.</summary>
    public static IReadOnlyCollection<string> AllBlockedIds()
    {
        try
        {
            return LiteBoxOptionsDb.AllOf(LiteBoxOption.ScopeGame, Key)
                .Where(kv => string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}

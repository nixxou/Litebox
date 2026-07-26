// Pending in-archive ROM selection, persisted per (game, version) — the host-side equivalent of
// LaunchBox-Web's localStorage (lbw.selectedRoms + lbw.romForce). It survives leaving the detail
// pane and restarting LiteBox, so a "Clear" (force-priority) or an explicit ROM pick sticks exactly
// like the web (a persisted force suppresses the re-seed from launch history).
//
// This is the DESKTOP CLIENT's pending pick, deliberately separate from the plugin's launch HISTORY
// (launch-history.db owns what was actually launched) AND from the web clients' picks (each browser
// keeps its own in localStorage — per-client semantics, decided with the user).
//
// STORAGE — options-db row (scope='game', key='RomSelection', value = JSON {verKey:{rom,force}});
// verKey = additional-app id or "__default__". Declared HOT in OptionKeys: read at detail display
// (Play-button seeding).

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LbApiHost.Host;

internal static class RomSelectionStore
{
    private sealed class Entry { public string? rom { get; set; } public bool force { get; set; } }

    private const string OptionKey = "RomSelection";
    private static readonly object _gate = new();

    private static string VerKey(string? appId) => string.IsNullOrEmpty(appId) ? "__default__" : appId!;

    private static Dictionary<string, Entry>? Read(string gameId)
        => Data.LiteBoxOptionsDb.GetJson<Dictionary<string, Entry>>("game", gameId, OptionKey);

    private static void Write(string gameId, Dictionary<string, Entry>? map)
        => Data.LiteBoxOptionsDb.Set("game", gameId, OptionKey,
                                     map == null || map.Count == 0 ? null : JsonSerializer.Serialize(map));

    /// <summary>The persisted pending pick for (game, version), or null when none — caller then seeds
    /// from launch history. rom == null with force == true is a "Clear".</summary>
    public static (string? rom, bool force)? Get(string gameId, string? appId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;
        lock (_gate)
        {
            var map = Read(gameId);
            if (map != null && map.TryGetValue(VerKey(appId), out var e) && e != null)
                return (e.rom, e.force);
            return null;
        }
    }

    /// <summary>Drops ALL pending picks for the game (every version) — the reset-to-default button
    /// restores pure default seeding across the board. No-op when nothing was persisted.</summary>
    public static void ClearGame(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        lock (_gate)
        {
            Write(gameId, null);
        }
    }

    /// <summary>Persist the pending pick. rom set → explicit pick; rom empty + force → "Clear";
    /// rom empty + !force → remove the slot (revert to history seeding), mirroring the web's
    /// setSelectedRomFor(null) / setRomForce(false).</summary>
    public static void Set(string gameId, string? appId, string? rom, bool force)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        lock (_gate)
        {
            var map = Read(gameId) ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
            var key = VerKey(appId);
            bool empty = string.IsNullOrEmpty(rom) && !force;
            if (empty) { if (!map.Remove(key)) return; }
            else map[key] = new Entry { rom = string.IsNullOrEmpty(rom) ? null : rom, force = force };
            Write(gameId, map);
        }
    }
}

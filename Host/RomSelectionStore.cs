// Pending in-archive ROM selection, persisted per (game, version) — the host-side equivalent of
// LaunchBox-Web's localStorage (lbw.selectedRoms + lbw.romForce). It survives leaving the detail
// pane and restarting LiteBox, so a "Clear" (force-priority) or an explicit ROM pick sticks exactly
// like the web (see app.js seeding at launchbox/app.js:2702 — a persisted force suppresses the
// re-seed from launch history).
//
// This is the CLIENT's pending pick, deliberately separate from the plugin's launch HISTORY
// (launch-history.db owns what was actually launched). The web keeps it per-browser; LiteBox is one
// client, so it keeps it in one JSON file next to its other state (Core\rom-selection.json).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LbApiHost.Host;

internal static class RomSelectionStore
{
    private sealed class Entry { public string? rom { get; set; } public bool force { get; set; } }

    private static readonly object _gate = new();
    private static Dictionary<string, Dictionary<string, Entry>>? _map;   // gameId → verKey → entry

    private static string FilePath => LiteBoxPaths.File("rom-selection.json");
    private static string VerKey(string? appId) => string.IsNullOrEmpty(appId) ? "__default__" : appId!;

    private static void EnsureLoaded()
    {
        if (_map != null) return;
        try
        {
            if (File.Exists(FilePath))
                _map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(FilePath));
        }
        catch { /* corrupt / unreadable → start fresh */ }
        _map ??= new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_map)); }
        catch (Exception ex) { Console.WriteLine("[rom-selection] save failed: " + ex.Message); }
    }

    /// <summary>The persisted pending pick for (game, version), or null when none — caller then seeds
    /// from launch history. rom == null with force == true is a "Clear".</summary>
    public static (string? rom, bool force)? Get(string gameId, string? appId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;
        lock (_gate)
        {
            EnsureLoaded();
            if (_map!.TryGetValue(gameId, out var pg) && pg.TryGetValue(VerKey(appId), out var e))
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
            EnsureLoaded();
            if (_map!.Remove(gameId)) Save();
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
            EnsureLoaded();
            var key = VerKey(appId);
            bool empty = string.IsNullOrEmpty(rom) && !force;
            if (empty)
            {
                if (_map!.TryGetValue(gameId, out var pg))
                {
                    pg.Remove(key);
                    if (pg.Count == 0) _map.Remove(gameId);
                }
            }
            else
            {
                if (!_map!.TryGetValue(gameId, out var pg)) { pg = new Dictionary<string, Entry>(StringComparer.Ordinal); _map[gameId] = pg; }
                pg[key] = new Entry { rom = string.IsNullOrEmpty(rom) ? null : rom, force = force };
            }
            Save();
        }
    }
}

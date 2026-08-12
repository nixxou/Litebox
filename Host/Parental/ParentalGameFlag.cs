// ─────────────────────────────────────────────────────────────────────────────
// Per-game "requires parental rights" flag.
// ─────────────────────────────────────────────────────────────────────────────
//
// A game an editor marks in Edit Game (next to Broken / Hide / Favorite) as requiring
// parental rights. It is NOT a LaunchBox IGame property, so LiteBox keeps it itself —
// now in the SHARED Core\litebox-parental.dat (BlockedId= lines), the single file the
// native .bin and the standalone plugin also read. No SQLite anymore.
//
// Runtime shape (the user's decision): the blocked-ID set is read ONCE at boot into a
// small in-memory HashSet (only the blocked IDs — a handful, not one row per game), and
// each game's answer is ALSO stamped onto a bool on the extended game class
// (HostGame.ParentalBlocked, a bit in GameRow) so the hot visibility path is a direct
// field read with no lookup. The file is NEVER re-read on a platform reload.
//
//   • the HashSet is the authoritative persistent mirror — it backs IsBlocked(id) for the
//     string-id callers (Edit Game, the web owned-view) and AllBlockedIds() for the .dat
//     write, and survives even before the game store is built (so a boot-time export never
//     wipes the BlockedId lines);
//   • the HostGame bool is the fast read the desktop list filter uses.
//
// SetBlocked flips BOTH (set + row bit) and rewrites the shared .dat through
// ParentalNativeExport.Write() — last-writer-wins with the plugin, atomic tmp+move.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Unbroken.LaunchBox.Plugins;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Parental;

internal static class ParentalGameFlag
{
    /// <summary>Legacy Options DB key — kept only for the one-shot import in <see cref="EnsureLoaded"/> of
    /// installs that flagged games before the move to the shared .dat.</summary>
    internal const string Key = "ParentalBlocked";

    // Authoritative in-memory mirror of the .dat's BlockedId= set (case-insensitive; GUIDs). Loaded once.
    private static readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static readonly object _gate = new();

    /// <summary>Boot entry point: load the blocked-ID set from the shared .dat (once) and stamp the runtime
    /// bit on every game row. Call after MediaResolver.LbRoot + the Options DB are up, BEFORE the first
    /// export. Safe to call more than once.</summary>
    public static void Init(GameStore? store)
    {
        EnsureLoaded();
        if (store != null) StampStore(store);
    }

    /// <summary>Populate <see cref="_blocked"/> from the shared .dat exactly once. Falls back to a one-shot
    /// import from the retired Options DB when the .dat carries no BlockedId yet (so an existing install's
    /// manual flags aren't silently lost by the storage move — imported IDs are persisted on the next write).</summary>
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            bool migrated = false;
            try
            {
                var d = ParentalNativeExport.Read();
                if (d != null)
                    foreach (var id in d.BlockedIds)
                        if (!string.IsNullOrWhiteSpace(id)) _blocked.Add(id.Trim());

                // Migration: a pre-.dat install kept these in the SQLite Options DB. Import them ONCE — only
                // when the .dat had none (so we never fight a newer .dat) — and persist below.
                if (_blocked.Count == 0)
                {
                    try
                    {
                        var legacy = LiteBoxOptionsDb.AllOf(LiteBoxOption.ScopeGame, Key)
                            .Where(kv => string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase))
                            .Select(kv => kv.Key)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToList();
                        if (legacy.Count > 0)
                        {
                            foreach (var id in legacy) _blocked.Add(id.Trim());
                            migrated = true;
                            Log($"imported {legacy.Count} manual block(s) from the legacy Options DB → shared .dat");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log("load failed: " + ex.Message); }
            _loaded = true;   // set BEFORE any Write() — its AllBlockedIds() re-enters EnsureLoaded.
            // Persist the imported set (re-entrant-safe now that _loaded is true).
            if (migrated) { try { ParentalNativeExport.Write(); } catch { } }
        }
    }

    /// <summary>Stamp <see cref="HostGame.ParentalBlocked"/> (the GameRow bit) on every game from the loaded
    /// set. No-op when nothing is blocked (the bits already default false).</summary>
    private static void StampStore(GameStore store)
    {
        try
        {
            if (_blocked.Count == 0) return;
            for (int i = 0; i < store.Count; i++)
                store.Rows[i].ParentalBlocked = _blocked.Contains(store.Rows[i].Id.ToString());
        }
        catch (Exception ex) { Log("stamp failed: " + ex.Message); }
    }

    /// <summary>True when this game is flagged as requiring parental rights.</summary>
    public static bool IsBlocked(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        EnsureLoaded();
        lock (_gate) return _blocked.Contains(gameId!);
    }

    /// <summary>Set or clear the flag: update the in-memory set, the game's runtime bit, and rewrite the
    /// shared .dat. No-op when the state is unchanged.</summary>
    public static void SetBlocked(string? gameId, bool blocked)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        EnsureLoaded();
        bool changed;
        lock (_gate) { changed = blocked ? _blocked.Add(gameId!) : _blocked.Remove(gameId!); }
        // Reflect on the extended game object so the desktop hot path + any open list repaint immediately.
        try { if (PluginHelper.DataManager?.GetGameById(gameId) is HostGame hg) hg.ParentalBlocked = blocked; }
        catch { }
        if (changed) { try { ParentalNativeExport.Write(); } catch { } }
    }

    /// <summary>Every game ID currently flagged blocked — the source of the .dat's BlockedId= lines and any
    /// bulk check. A snapshot (safe to enumerate while another thread edits).</summary>
    public static IReadOnlyCollection<string> AllBlockedIds()
    {
        EnsureLoaded();
        lock (_gate) return _blocked.ToList();
    }

    private static void Log(string msg) => LbLog.Info("parental", "gameflag: " + msg);
}

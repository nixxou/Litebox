// The persistent identity ledger: LaunchBox GUIDs / names → the integer ids RomM clients see.
//
// RomM keys everything by int, and clients PERSIST those ints (Grout remembers which rom ids it holds,
// Argosy attributes saves to them). So an id must survive restarts and never be reused: it is allocated
// on first sight, monotonically, and a departed entity keeps its number reserved forever — a re-added
// game must not inherit another game's history on somebody's handheld.
//
// Storage is its own JSON file (Core\litebox\romm-ids.json, the saves-vault.json precedent) rather than
// the options DB: a 40k-game library means 40k+ rows, which is not what the EAV store is for. Writes are
// deferred — allocations arrive in one burst on the first listing, so the file is saved once shortly
// after the burst instead of once per id.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Romm;

internal static class RommIdMap
{
    private sealed class Ledger
    {
        public int NextPlatform { get; set; } = 1;
        public int NextRom { get; set; } = 1;
        public int NextFile { get; set; } = 1;
        public int NextAsset { get; set; } = 1;
        public int NextCollection { get; set; } = 1;
        public Dictionary<string, int> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Roms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Collections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object _lock = new();
    private static Ledger? _ledger;
    private static bool _dirty;
    private static System.Threading.Timer? _flushTimer;
    private static string? _pathOverride;

    private static string StorePath => _pathOverride ?? LiteBoxPaths.File("romm-ids.json");

    /// <summary>Redirects the ledger to a scratch file — the self-test's hook. Resets the loaded state.</summary>
    internal static void UseStore(string? path)
    {
        lock (_lock) { Flush(); _pathOverride = path; _ledger = null; _dirty = false; }
    }

    private static Ledger Load()
    {
        if (_ledger != null) return _ledger;
        try
        {
            if (File.Exists(StorePath))
                _ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(StorePath));
        }
        catch (Exception ex) { LbLog.Warn("romm", "id ledger load failed (starting fresh): " + ex.Message); }
        _ledger ??= new Ledger();
        return _ledger;
    }

    // Deferred save: the first listing of a fresh library allocates thousands of ids in one burst, and one
    // file write two seconds later beats thousands of rewrites.
    private static void MarkDirty()
    {
        _dirty = true;
        _flushTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try { _flushTimer.Change(2000, Timeout.Infinite); } catch { }
    }

    /// <summary>Writes the ledger out if anything changed. Called by the timer and on server stop.</summary>
    public static void Flush()
    {
        lock (_lock)
        {
            if (!_dirty || _ledger == null) return;
            try
            {
                var tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_ledger));
                File.Move(tmp, StorePath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) { LbLog.Warn("romm", "id ledger save failed: " + ex.Message); }
        }
    }

    // The map and the counter are selected INSIDE the lock, off the same loaded ledger — selecting them
    // at the call site would read _ledger unlocked and could race a UseStore reset.
    private static int Resolve(string key, Func<Ledger, Dictionary<string, int>> mapOf, Func<Ledger, int> take)
    {
        lock (_lock)
        {
            var l = Load();
            var map = mapOf(l);
            if (map.TryGetValue(key, out var id)) return id;
            id = take(l);
            map[key] = id;
            MarkDirty();
            return id;
        }
    }

    /// <summary>Platform id for a LaunchBox platform NAME (the platform's identity in LB).</summary>
    public static int PlatformId(string lbPlatformName)
        => Resolve(lbPlatformName ?? "", l => l.Platforms, l => l.NextPlatform++);

    /// <summary>Rom id for a LaunchBox game GUID.</summary>
    public static int RomId(string gameId)
        => Resolve(gameId ?? "", l => l.Roms, l => l.NextRom++);

    /// <summary>File id for one playable entry of a game: the main ROM ("main"), an additional-app disc
    /// ("app:{appId}") or an archive member ("entry:{path}"). The key is scoped by the game GUID.</summary>
    public static int FileId(string gameId, string entryKey)
        => Resolve((gameId ?? "") + "|" + (entryKey ?? ""), l => l.Files, l => l.NextFile++);

    /// <summary>Asset id for a save / state / screenshot, keyed by its vault identity.</summary>
    public static int AssetId(string vaultKey)
        => Resolve(vaultKey ?? "", l => l.Assets, l => l.NextAsset++);

    /// <summary>Collection id for a LaunchBox playlist name (or the synthetic "Favorites").</summary>
    public static int CollectionId(string name)
        => Resolve(name ?? "", l => l.Collections, l => l.NextCollection++);

    // ── Reverse lookups (a client hands the int back) ─────────────────────────

    public static string? PlatformNameOf(int id) => ReverseOf(l => l.Platforms, id);
    public static string? GameIdOf(int romId) => ReverseOf(l => l.Roms, romId);
    public static string? FileKeyOf(int fileId) => ReverseOf(l => l.Files, fileId);
    public static string? AssetKeyOf(int assetId) => ReverseOf(l => l.Assets, assetId);
    public static string? CollectionNameOf(int id) => ReverseOf(l => l.Collections, id);

    private static string? ReverseOf(Func<Ledger, Dictionary<string, int>> mapOf, int id)
    {
        lock (_lock)
        {
            // Linear over an in-memory dictionary: reverse lookups happen once per request on maps that
            // fit in cache, and a second index would double what has to stay consistent.
            foreach (var kv in mapOf(Load()))
                if (kv.Value == id) return kv.Key;
            return null;
        }
    }
}

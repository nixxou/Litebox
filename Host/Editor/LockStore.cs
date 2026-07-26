// Native per-game metadata-field LOCK registry for LiteBox's built-in Edit Game window.
//
// The USER can flag individual metadata fields (Title, Genre, Rating, …) of a game as "locked", meaning
// "this is the value I want — do NOT let a metadata refresh overwrite it". Each lock stores BOTH the fact
// that it is locked AND the value to preserve.
//
// STORAGE — LiteBox's own options DB (LiteBoxOptionsDb, Core\litebox\litebox-options.db):
//     options(scope='game', entity_id=<gameId>, key='FieldLocks', value=compact JSON {"title":"Chrono
//     Trigger","genre":""})
// (key = the shared lock-field key set, identical to ExtendDB LockStorage.AllColumns; value = the
// preserved string — empty string IS a valid locked value, hence one JSON blob, not one row per field.)
// The options DB is the right home for LiteBox-own per-entity data: guid-keyed, survives REAL LaunchBox
// sessions (LB's fixed-schema XML rewrite would strip foreign <Game> elements), and is the store the
// ExtendDB plugin (which only runs under real LaunchBox) uses for its locks too when it detects a LiteBox
// install — one registry, locks set in either editor are honoured by the plugin's scrape protection.
// (Image locks are a SEPARATE system on purpose: the ADS ":lock" marker travels with the file across
// LB's renumber cascades, which a per-game path list would not — see ImageLockBridge.)
//
// MIGRATION — the previous store was Core\litebox\metadata-locks.json; on first use its field locks are
// copied into the options DB (only where the game has no row yet) and the file is renamed *.migrated.
// Its image-lock lists had no consumer and are dropped.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Editor;

/// <summary>Per-game locked metadata fields, stored in the LiteBox options DB (scope 'game', key
/// 'FieldLocks' — the shared contract with ExtendDB's LockStorage).</summary>
internal static class LockStore
{
    /// <summary>The options-DB key (game scope) — shared contract with ExtendDB LockStorage.</summary>
    public const string OptionKey = "FieldLocks";

    private static readonly object _gate = new();
    private static bool _migrated;

    private static Dictionary<string, string> Parse(string? json)
    {
        if (!string.IsNullOrEmpty(json))
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json!);
                if (d != null) return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { LbLog.Info("editor", "LockStore parse failed: " + ex.Message); }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> Read(string gameId)
        => Parse(LiteBoxOptionsDb.Get("game", gameId, OptionKey));

    private static void Write(string gameId, Dictionary<string, string> locks)
        => LiteBoxOptionsDb.Set("game", gameId, OptionKey,
                                locks.Count == 0 ? null : JsonSerializer.Serialize(locks));

    // ── Public API (unchanged shape — EditGameWindowLocks + future refresh hooks) ──

    /// <summary>True if <paramref name="fieldKey"/> is locked for the given game.</summary>
    public static bool IsFieldLocked(string? gameId, string? fieldKey)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(fieldKey)) return false;
        lock (_gate) { EnsureMigrated(); return Read(gameId!).ContainsKey(fieldKey!); }
    }

    /// <summary>The preserved value of a locked field, or null when the field is unlocked.</summary>
    public static string? GetLockedValue(string? gameId, string? fieldKey)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(fieldKey)) return null;
        lock (_gate) { EnsureMigrated(); return Read(gameId!).TryGetValue(fieldKey!, out var v) ? v : null; }
    }

    /// <summary>Every locked (fieldKey → preserved value) pair for a game. Empty when nothing is locked.</summary>
    public static IReadOnlyDictionary<string, string> GetLockedFields(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate) { EnsureMigrated(); return Read(gameId!); }
    }

    /// <summary>Locks or unlocks one field for one game. When locking, <paramref name="value"/> (may be
    /// empty, never treated as null) is stored as the value to preserve. Persists immediately.</summary>
    public static void SetFieldLock(string? gameId, string? fieldKey, bool locked, string? value)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(fieldKey)) return;
        lock (_gate)
        {
            EnsureMigrated();
            var locks = Read(gameId!);
            if (locked) locks[fieldKey!] = value ?? "";
            else if (!locks.Remove(fieldKey!)) return;   // nothing to do
            Write(gameId!, locks);
        }
    }

    /// <summary>Drops every field lock for a game.</summary>
    public static void ClearGame(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        lock (_gate) { EnsureMigrated(); Write(gameId!, new(StringComparer.OrdinalIgnoreCase)); }
    }

    /// <summary>True if the game has at least one locked field.</summary>
    public static bool HasAnyLock(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        lock (_gate) { EnsureMigrated(); return Read(gameId!).Count > 0; }
    }

    // ── One-shot migration from the previous JSON store (caller holds _gate) ──

    private static void EnsureMigrated()
    {
        if (_migrated) return;
        _migrated = true;   // one attempt per session — a failed parse shouldn't retry forever
        string path = LiteBoxPaths.File("metadata-locks.json");
        try
        {
            if (!File.Exists(path) || !LiteBoxOptionsDb.Enabled) return;
            int games = 0, locks = 0;
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                if (doc.RootElement.TryGetProperty("Games", out var g))
                    foreach (var entry in g.EnumerateObject())
                    {
                        if (!entry.Value.TryGetProperty("Fields", out var flds)) continue;
                        if (Read(entry.Name).Count > 0) continue;   // row already present → don't clobber
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in flds.EnumerateObject()) map[p.Name] = p.Value.GetString() ?? "";
                        if (map.Count == 0) continue;
                        Write(entry.Name, map);
                        games++; locks += map.Count;
                    }
            }
            File.Move(path, path + ".migrated", overwrite: true);
            LbLog.Info("editor", $"LockStore migrated metadata-locks.json → options DB ({games} game(s), {locks} lock(s))");
        }
        catch (Exception ex) { LbLog.Info("editor", "LockStore migration failed: " + ex.Message); }
    }
}

// Native per-game field / image LOCK registry for LiteBox's built-in Edit Game window.
//
// Clean-room re-home of ExtendDB's LockStorage idea onto the LiteBox data layer. The USER can flag
// individual metadata fields (Title, Genre, Rating, …) or individual image files of a game as
// "locked", meaning "this is the value I want — do NOT let a metadata refresh overwrite it". Each
// lock remembers BOTH the fact that it is locked AND the value to preserve, so a future refresh
// pipeline can re-apply the stored value if the field has drifted.
//
// Storage
//   A single JSON sidecar, <LB>\Core\litebox\metadata-locks.json (via LiteBoxPaths), owned entirely
//   by LiteBox — no SQLite, no reflection, no dependency on the ExtendDB plugin being present. The
//   file is USER data: it is built organically as fields get locked and is never shipped.
//
//   Shape:
//     {
//       "Version": 1,
//       "Games": {
//         "<gameId>": {
//           "Fields": { "title": "Chrono Trigger", "genre": "RPG" },   // key -> preserved value
//           "Images": [ "C:\\LaunchBox\\Images\\...\\front.png" ]        // locked image paths
//         }
//       }
//     }
//
//   A field key maps 1:1 onto an Edit Game control (see EditGameWindowLocks.cs). Presence of a key
//   means "locked"; the string is the value to enforce (empty string is a valid locked value). A game
//   entry with no locked fields and no locked images is pruned on write, so the file stays clean after
//   a user unlocks everything.
//
// Honoring locks on refresh
//   LiteBox has NO native metadata-scrape / refresh pipeline of its own (the Edit Game "Search for
//   Metadata" button is inert — scraping is the ExtendDB plugin's job). So this store PERSISTS locks
//   and exposes GetLockedFields(...) for a future refresh hook to consult, but nothing in LiteBox
//   currently calls back into it to re-apply values. That is the known gap: the lock survives and is
//   visible in the editor, but there is no in-host refresh to protect against yet.
//
// Thread-safety
//   All access is guarded by a single monitor; the in-memory model is loaded once (lazily) and every
//   mutation writes the whole file back immediately (the data set is tiny). Safe to call from the UI
//   thread; callers never block on I/O for long.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Editor;

/// <summary>JSON-backed registry of per-game locked metadata fields and locked images. Static
/// singleton; persisted in <c>Core\litebox\metadata-locks.json</c>. Fully native to LiteBox.</summary>
internal static class LockStore
{
    // ── On-disk model ──────────────────────────────────────────────────────

    /// <summary>Per-game lock record. <see cref="Fields"/> maps a field key to the preserved value;
    /// <see cref="Images"/> is the set of locked image file paths.</summary>
    private sealed class GameLocks
    {
        public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Images { get; set; } = new();
    }

    /// <summary>Root document persisted to metadata-locks.json.</summary>
    private sealed class Doc
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, GameLocks> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ── State ──────────────────────────────────────────────────────────────

    private static readonly object _gate = new();
    private static Doc? _doc;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FilePath => LiteBoxPaths.File("metadata-locks.json");

    /// <summary>Force a reload from disk on next access.</summary>
    public static void Invalidate() { lock (_gate) _doc = null; }

    // ── Field locks ────────────────────────────────────────────────────────

    /// <summary>True if <paramref name="fieldKey"/> is locked for the given game.</summary>
    public static bool IsFieldLocked(string? gameId, string? fieldKey)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(fieldKey)) return false;
        lock (_gate)
            return Load().Games.TryGetValue(gameId!, out var g) && g.Fields.ContainsKey(fieldKey!);
    }

    /// <summary>The preserved value of a locked field, or null when the field is unlocked.</summary>
    public static string? GetLockedValue(string? gameId, string? fieldKey)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(fieldKey)) return null;
        lock (_gate)
            return Load().Games.TryGetValue(gameId!, out var g) && g.Fields.TryGetValue(fieldKey!, out var v)
                ? v : null;
    }

    /// <summary>Every locked (fieldKey → preserved value) pair for a game. Empty when nothing is locked.
    /// Intended for a future metadata-refresh hook to consult and re-apply.</summary>
    public static IReadOnlyDictionary<string, string> GetLockedFields(string? gameId)
    {
        if (!string.IsNullOrEmpty(gameId))
            lock (_gate)
                if (Load().Games.TryGetValue(gameId!, out var g))
                    return new Dictionary<string, string>(g.Fields, StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Locks or unlocks one field for one game. When locking, <paramref name="value"/> (may be
    /// empty, never treated as null) is stored as the value to preserve. Persists immediately.</summary>
    public static void SetFieldLock(string? gameId, string? fieldKey, bool locked, string? value)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(fieldKey)) return;
        lock (_gate)
        {
            var doc = Load();
            if (locked)
            {
                if (!doc.Games.TryGetValue(gameId!, out var g)) doc.Games[gameId!] = g = new GameLocks();
                g.Fields[fieldKey!] = value ?? "";
            }
            else if (doc.Games.TryGetValue(gameId!, out var g))
            {
                g.Fields.Remove(fieldKey!);
                PruneIfEmpty(doc, gameId!, g);
            }
            else return;   // nothing to do
            Save(doc);
        }
    }

    // ── Image locks (native fallback; not yet wired into the Images page) ───

    /// <summary>True if the given image path is locked for the game.</summary>
    public static bool IsImageLocked(string? gameId, string? path)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(path)) return false;
        lock (_gate)
            return Load().Games.TryGetValue(gameId!, out var g)
                && g.Images.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Locks / unlocks a single image path for a game. Persists immediately.</summary>
    public static void SetImageLock(string? gameId, string? path, bool locked)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(path)) return;
        lock (_gate)
        {
            var doc = Load();
            if (locked)
            {
                if (!doc.Games.TryGetValue(gameId!, out var g)) doc.Games[gameId!] = g = new GameLocks();
                if (!g.Images.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                    g.Images.Add(path!);
            }
            else if (doc.Games.TryGetValue(gameId!, out var g))
            {
                g.Images.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                PruneIfEmpty(doc, gameId!, g);
            }
            else return;
            Save(doc);
        }
    }

    /// <summary>Every locked image path for a game (empty when none).</summary>
    public static IReadOnlyList<string> GetLockedImages(string? gameId)
    {
        if (!string.IsNullOrEmpty(gameId))
            lock (_gate)
                if (Load().Games.TryGetValue(gameId!, out var g))
                    return g.Images.ToArray();
        return Array.Empty<string>();
    }

    // ── Whole-game ─────────────────────────────────────────────────────────

    /// <summary>Drops every lock (fields + images) for a game.</summary>
    public static void ClearGame(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        lock (_gate)
        {
            var doc = Load();
            if (doc.Games.Remove(gameId!)) Save(doc);
        }
    }

    /// <summary>True if the game has at least one locked field or image.</summary>
    public static bool HasAnyLock(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        lock (_gate)
            return Load().Games.TryGetValue(gameId!, out var g) && (g.Fields.Count > 0 || g.Images.Count > 0);
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private static void PruneIfEmpty(Doc doc, string gameId, GameLocks g)
    {
        if (g.Fields.Count == 0 && g.Images.Count == 0) doc.Games.Remove(gameId);
    }

    // Caller holds _gate.
    private static Doc Load()
    {
        if (_doc != null) return _doc;
        var doc = new Doc();
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path), JsonOpts);
                if (parsed != null)
                {
                    doc = parsed;
                    doc.Games ??= new(StringComparer.OrdinalIgnoreCase);
                    // Rebuild dictionaries with the case-insensitive comparer (deserialization uses the default).
                    doc.Games = new Dictionary<string, GameLocks>(doc.Games, StringComparer.OrdinalIgnoreCase);
                    foreach (var g in doc.Games.Values)
                    {
                        g.Fields = new Dictionary<string, string>(g.Fields ?? new(), StringComparer.OrdinalIgnoreCase);
                        g.Images ??= new List<string>();
                    }
                }
            }
        }
        catch (Exception ex) { LbLog.Info("editor", "LockStore load failed: " + ex.Message); }
        return _doc = doc;
    }

    // Caller holds _gate.
    private static void Save(Doc doc)
    {
        _doc = doc;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(doc, JsonOpts)); }
        catch (Exception ex) { LbLog.Info("editor", "LockStore save failed: " + ex.Message); }
    }
}

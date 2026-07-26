// Per-game IMAGE-POOL signature — a short, stable fingerprint of the set of image files LiteBox knows for a
// game. It answers one question cheaply: "has this game's stored images changed since last time?" — the
// foundation for the anti-duplicate media cache (which must rebuild its dedup data when, and only when, the
// pool moves).
//
// Definition (deterministic, restart-stable):
//   sig(game) = first 8 hex chars of MD5( "path|size\n" for every IMAGE file, each path lower-cased,
//               the lines sorted ordinally, concatenated ).
//   • Images only — videos are excluded (they don't participate in image dedup).
//   • size = file size in bytes; NO mtime (a touch that doesn't change bytes must not move the signature).
//   • Same pool → same 8 hex, on every boot and on any machine.
//
// Zero-IO in the normal case: the (path, size) pairs come straight from the in-memory GameCache (ExtendDB's
// reflected cache, else the host port) where sizes ride the build. Only when no cache can answer (very early
// boot, cache disabled) do we fall back to a disk walk.
//
// Persistence: (platform, guid) → sig8 is memoised in-process for the session AND written to
// Core\litebox\image-signatures.json so a value computed once survives restarts and downstream code can diff
// against it. Entries are dropped when a platform's pool is invalidated (GameCacheBridge.RebuildPlatform).

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class MediaSignature
{
    // In-process memo: "platform\0guidN" → sig8. Authoritative for the session; a superset is persisted.
    private static readonly ConcurrentDictionary<string, string> _memo = new(StringComparer.Ordinal);
    private static int _loaded;   // 0 = json not yet merged into _memo
    private static int _dirty;    // pending unsaved changes
    private static System.Threading.Timer? _flush;
    private static readonly object _saveLock = new();

    private static string StorePath => LiteBoxPaths.File("image-signatures.json");

    private static string Key(string platform, Guid id) => platform + "\0" + id.ToString("N");

    /// <summary>Signature of a game's image pool (8 lower-case hex), or "" if the game can't be identified.</summary>
    public static string For(IGame? g)
    {
        if (g == null) return "";
        string plat, gid, title;
        try { plat = g.Platform ?? ""; gid = g.Id ?? ""; title = g.Title ?? ""; }
        catch { return ""; }
        if (!Guid.TryParse(gid, out var id) || string.IsNullOrEmpty(plat)) return "";
        return For(plat, id, title);
    }

    /// <summary>Signature of a game's image pool (8 lower-case hex). Recomputes live from the cache when it can
    /// answer (zero-IO), else serves the last persisted value, else walks the disk once.</summary>
    public static string For(string platform, Guid id, string title)
    {
        if (string.IsNullOrEmpty(platform)) return "";
        EnsureLoaded();
        string key = Key(platform, id);

        // Prefer a live computation from the cache — cheap (in-memory) and always current.
        var pairs = GameCacheBridge.ImagePairs(platform, id);
        if (pairs == null)   // no cache is authoritative yet → last-known value, else a one-off disk walk
        {
            if (_memo.TryGetValue(key, out var known)) return known;
            pairs = DiskPairs(platform, id, title);
        }

        string sig = Hash(pairs);
        if (_memo.TryGetValue(key, out var prev) && prev == sig) return sig;   // unchanged → no write
        _memo[key] = sig;
        MarkDirty();
        return sig;
    }

    /// <summary>Drop every cached signature for a platform (its image pool changed on disk). They recompute
    /// lazily on next request against the rebuilt cache.</summary>
    public static void Invalidate(string? platform)
    {
        if (string.IsNullOrEmpty(platform)) return;
        EnsureLoaded();
        string prefix = platform + "\0";
        bool any = false;
        foreach (var k in _memo.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && _memo.TryRemove(k, out _)) any = true;
        if (any) MarkDirty();
    }

    // ── Computation ───────────────────────────────────────────────────────────

    /// <summary>The 8-hex fingerprint of a (path, size) set: lower-case each path, one "path|size" line per
    /// file, sort ordinally (so ordering is irrelevant), MD5, keep the first 8 hex chars.</summary>
    private static string Hash(List<(string path, long size)> pairs)
    {
        if (pairs == null || pairs.Count == 0) return "00000000";
        var lines = new List<string>(pairs.Count);
        foreach (var (path, size) in pairs)
            if (!string.IsNullOrEmpty(path)) lines.Add(path.ToLowerInvariant() + "|" + size);
        if (lines.Count == 0) return "00000000";
        lines.Sort(StringComparer.Ordinal);
        var blob = string.Join("\n", lines);
        var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(blob))).ToLowerInvariant();
        return md5.Substring(0, 8);
    }

    /// <summary>Disk-walk fallback (with IO) for when no cache is loaded: enumerate the game's image files and
    /// stat each for its size.</summary>
    private static List<(string path, long size)> DiskPairs(string platform, Guid id, string title)
    {
        var res = new List<(string, long)>();
        try
        {
            foreach (var (path, _, _) in MediaResolver.AllImageFiles(platform, id, title))
            {
                long size = -1;
                try { var fi = new FileInfo(path); if (fi.Exists) size = fi.Length; } catch { }
                res.Add((path, size));
            }
        }
        catch { }
        return res;
    }

    // ── Persistence (Core\litebox\image-signatures.json) ──────────────────────

    private static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1) return;
        try
        {
            var p = StorePath;
            if (File.Exists(p))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p));
                if (map != null)
                    foreach (var kv in map)
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) _memo[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    private static void MarkDirty()
    {
        Interlocked.Exchange(ref _dirty, 1);
        // Coalesce bursts of updates (e.g. scrolling a list) into one write ~2s later.
        try
        {
            _flush ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _flush.Change(2000, Timeout.Infinite);
        }
        catch { Flush(); }
    }

    private static void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        lock (_saveLock)
        {
            try
            {
                // Snapshot to a plain dictionary for stable, key-sorted JSON.
                var map = _memo.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
                var tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map));
                if (File.Exists(StorePath)) File.Replace(tmp, StorePath, null); else File.Move(tmp, StorePath);
            }
            catch { }
        }
    }
}

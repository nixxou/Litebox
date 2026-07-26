// Per-game/per-view duplicate filter applied while BUILDING the post-load media list. Snapshot of the
// three cache keys (sort / pool / par — see DupCheckAds) + the engine settings, taken once per build:
//   • cached ADS result under the current triplet → reuse, zero decode;
//   • else evaluate via DedupEngine (candidate vs the images already ACCEPTED into the list) and persist;
//   • engine can't answer (missing natives, decode error) → fail OPEN (keep the image), persist nothing.
// `force` (the Caches → "update duplicates" pass) recomputes and rewrites even valid cached results.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace LbApiHost.Host.Media;

internal sealed class MediaDupFilter
{
    private readonly bool _poster;
    private readonly bool _force;
    private readonly string _sort, _pool, _par;
    private readonly Dedup.DupEngineMode _mode;
    private readonly double _threshold;
    private readonly bool _gpu;

    private MediaDupFilter(bool poster, bool force, string sort, string pool, string par,
                           Dedup.DupEngineMode mode, double threshold, bool gpu)
    { _poster = poster; _force = force; _sort = sort; _pool = pool; _par = par; _mode = mode; _threshold = threshold; _gpu = gpu; }

    /// <summary>The filter for one game+view build, or null when disabled / the game has no usable
    /// pool signature (no keys → no caching → filtering would recompute forever).</summary>
    public static MediaDupFilter? For(MediaLayout ml, bool poster, string plat, Guid id, string title, bool force = false)
    {
        try
        {
            if (ml == null || !ml.PreventDuplicates) return null;
            string pool = MediaSignature.For(plat, id, title);
            if (string.IsNullOrEmpty(pool)) return null;
            return new MediaDupFilter(
                poster, force,
                ml.PostLoadHash(poster).Substring(0, 8), pool, ml.DupParamHash8(),
                Dedup.DedupEngine.ParseMode(ml.DupEngine), ml.EffectiveDupThreshold(), ml.DupGpu);
        }
        catch { return null; }
    }

    // ── Exact-twin guard (byte-identical files under two names) ───────────────
    // Runs BEFORE the cached records and regardless of them. Rationale: records can be poisoned by a
    // mid-rename build (Edit Game renumber/move enumerates the folder with one twin briefly absent), and
    // byte-identical twins have IDENTICAL SIZES — the pool signature cannot see such an edit, so the
    // poisoned dup=0 records stay "valid" forever. Same bytes must never display twice, records or not.
    // Cost: one memoized stat per accepted image; MD5 only on a size collision (rare), memoized too.
    private readonly Dictionary<string, long> _sizeMemo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _md5Memo = new(StringComparer.OrdinalIgnoreCase);

    private long SizeOf(string p)
    {
        if (_sizeMemo.TryGetValue(p, out var s)) return s;
        try { s = new FileInfo(p).Length; } catch { s = -1; }
        _sizeMemo[p] = s;
        return s;
    }

    private static string? Md5Of(string p, long size)
    {
        string key = p + "|" + size;   // size in the key: a replaced file re-hashes instead of serving stale
        if (_md5Memo.TryGetValue(key, out var m)) return m;
        try { m = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(p))); }
        catch { return null; }
        _md5Memo[key] = m;
        return m;
    }

    private bool IsExactTwin(string path, IReadOnlyList<string> accepted)
    {
        long sz = SizeOf(path);
        if (sz <= 0) return false;
        foreach (var a in accepted)
        {
            if (SizeOf(a) != sz) continue;
            if (Md5Of(path, sz) is not string mc || Md5Of(a, sz) is not string ma || mc != ma) continue;
            if (Dedup.DedupEngine.Verbose)
                Console.WriteLine($"[dedup] exact-twin (size+md5) => skip: {Dedup.DedupEngine.Short(path)} == {Dedup.DedupEngine.Short(a)}");
            return true;
        }
        return false;
    }

    /// <summary>True → skip this candidate (it duplicates an image already accepted into the list).</summary>
    public bool IsDup(string path, IReadOnlyList<string> accepted)
    {
        try
        {
            if (IsExactTwin(path, accepted)) return true;
            if (!_force && DupCheckAds.TryGetResult(path, _poster, _sort, _pool, _par, out bool cached))
            {
                if (Dedup.DedupEngine.Verbose)
                    Console.WriteLine($"[dedup] cache {(_poster ? "poster" : "list")}: dup={(cached ? 1 : 0)}  {Dedup.DedupEngine.Short(path)}");
                return cached;
            }
            var (r, score) = Dedup.DedupEngine.Evaluate(_mode, _threshold, _gpu, path, accepted);
            if (r == null)
            {
                if (Dedup.DedupEngine.Verbose)
                    Console.WriteLine($"[dedup] no-verdict (fail-open, kept): {Dedup.DedupEngine.Short(path)}");
                return false;   // can't evaluate → keep the image, don't persist
            }
            DupCheckAds.Write(path, _poster, _sort, _pool, _par, r.Value, score);
            return r.Value;
        }
        catch { return false; }
    }
}

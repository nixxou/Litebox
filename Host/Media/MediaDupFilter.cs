// Per-game/per-view duplicate filter applied while BUILDING the post-load media list. Per candidate:
//   • compute the EVALUATION-CONTEXT key (ctx = md5-8 of the ordered "path|size" of the images accepted
//     before it + the candidate's own "path|size" — sizes from the build's own stat memo, so key and
//     evaluation come from the same on-disk snapshot);
//   • cached ADS result under the current (ctx,par) → reuse, zero decode;
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
    private readonly string _par;
    private readonly Dedup.DupEngineMode _mode;
    private readonly double _threshold;
    private readonly bool _gpu;

    private MediaDupFilter(bool poster, bool force, string par,
                           Dedup.DupEngineMode mode, double threshold, bool gpu)
    { _poster = poster; _force = force; _par = par; _mode = mode; _threshold = threshold; _gpu = gpu; }

    /// <summary>The filter for one game+view build, or null when disabled. The ctx key is per-candidate
    /// (computed in <see cref="IsDup"/> from the build's own accepted list) — no game-level key needed.</summary>
    public static MediaDupFilter? For(MediaLayout ml, bool poster, string plat, Guid id, string title, bool force = false)
    {
        try
        {
            if (ml == null || !ml.PreventDuplicates) return null;
            return new MediaDupFilter(
                poster, force, ml.DupParamHash8(),
                Dedup.DedupEngine.ParseMode(ml.DupEngine), ml.EffectiveDupThreshold(), ml.DupGpu);
        }
        catch { return null; }
    }

    /// <summary>The evaluation-context key: md5-8 of the ordered "path|size" lines of the images accepted
    /// BEFORE the candidate, a separator, then the candidate's own "path|size" (lower-cased paths). The
    /// candidate being part of its OWN key is what forces a recompute when the file is renumbered — a
    /// record travelling with a rename (ADS follows File.Move) can never match its new name.</summary>
    private string CtxOf(string path, IReadOnlyList<string> accepted)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in accepted) sb.Append(a.ToLowerInvariant()).Append('|').Append(SizeOf(a)).Append('\n');
        sb.Append("--\n").Append(path.ToLowerInvariant()).Append('|').Append(SizeOf(path));
        var md5 = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return md5.Substring(0, 8);
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
            string ctx = CtxOf(path, accepted);
            // Cache first: with the ctx key a matching record is trustworthy by construction (the poisoning
            // that once forced the twin guard to run before everything is structurally gone), and the warm
            // path then costs zero file reads.
            if (!_force && DupCheckAds.TryGetResult(path, _poster, ctx, _par, out bool cached))
            {
                if (Dedup.DedupEngine.Verbose)
                    Console.WriteLine($"[dedup] cache {(_poster ? "poster" : "list")} (ctx={ctx}): dup={(cached ? 1 : 0)}  {Dedup.DedupEngine.Short(path)}");
                return cached;
            }
            // Twin guard on cache miss: byte-identical files are dup by definition — no decode, and it also
            // covers the fail-open case (engine unavailable). PERSISTED like an engine verdict, with the
            // engine's perfect-match score, so the ADS reflects it and the next visit is a cache hit.
            if (IsExactTwin(path, accepted))
            {
                DupCheckAds.Write(path, _poster, ctx, _par, true, _mode == Dedup.DupEngineMode.Cnn ? 1.0 : 0);
                return true;
            }
            // Game running → the CNN session is released (Suspend) and must not re-create mid-play. Serve
            // the last STORED verdict as a best-effort HINT (keys deliberately ignored — the list rarely
            // changes while a game runs), else keep the image. Nothing is persisted; the first post-game
            // build re-evaluates properly under the real keys. Hash engines are pure CPU and stay live.
            if (_mode == Dedup.DupEngineMode.Cnn && Dedup.DedupEngine.Suspended)
            {
                var dto = DupCheckAds.Peek(path);
                var rec = (_poster ? dto?.Poster : dto?.List) ?? (_poster ? dto?.List : dto?.Poster);
                bool hint = rec != null && rec.Dup != 0;
                if (Dedup.DedupEngine.Verbose)
                    Console.WriteLine($"[dedup] suspended → stored-verdict hint dup={(hint ? 1 : 0)} (keys ignored): {Dedup.DedupEngine.Short(path)}");
                return hint;
            }
            var (r, score) = Dedup.DedupEngine.Evaluate(_mode, _threshold, _gpu, path, accepted);
            if (r == null)
            {
                if (Dedup.DedupEngine.Verbose)
                    Console.WriteLine($"[dedup] no-verdict (fail-open, kept): {Dedup.DedupEngine.Short(path)}");
                return false;   // can't evaluate → keep the image, don't persist
            }
            DupCheckAds.Write(path, _poster, ctx, _par, r.Value, score);
            return r.Value;
        }
        catch { return false; }
    }
}

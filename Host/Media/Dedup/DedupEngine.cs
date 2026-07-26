// Facade over the duplicate-detection engines (dhash / phash / cnn) for the post-load media list's
// "prevent duplicates" filter. Owns:
//   • per-session FEATURE memos (path → 64-bit hash / L2-normalized embedding) so an accepted image is
//     decoded/embedded once per session no matter how many candidates compare against it;
//   • the lazy CNN session (created on first use, GPU→CPU auto-fallback);
//   • the decision rule: hash → duplicate when Hamming distance ≤ threshold;
//                        cnn  → duplicate when cosine similarity ≥ threshold.
// IsDuplicate is TRI-STATE: null = could not evaluate (engine unavailable / decode error) — the caller
// fails OPEN (keeps the image) and does NOT persist a result.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LbApiHost.Host.Media.Dedup;

internal enum DupEngineMode { DHash, PHash, Cnn }

internal static class DedupEngine
{
    /// <summary>Bump when the fingerprint pipeline changes (decoder, preprocessing, model) — salts the
    /// dup-param hash so every persisted ADS result is invalidated at once.</summary>
    public const int Version = 1;

    public static DupEngineMode ParseMode(string? s) => s?.ToLowerInvariant() switch
    {
        "dhash" => DupEngineMode.DHash,
        "cnn" => DupEngineMode.Cnn,
        _ => DupEngineMode.PHash,
    };

    /// <summary>Engine default threshold: max Hamming distance (hashes) / min cosine similarity (cnn).</summary>
    public static double DefaultThreshold(DupEngineMode m) => m == DupEngineMode.Cnn ? 0.90 : 10;

    // ── Session feature memos ────────────────────────────────────────────────
    // Keys are full paths (ordinal-ignore-case normalized). Entries are only appended; a config change
    // doesn't invalidate them (features don't depend on thresholds). Soft-capped to bound memory: the
    // CNN vectors are 1024 floats = 4 KB each.
    private static readonly ConcurrentDictionary<string, ulong> _dhash = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ulong> _phash = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, float[]> _emb = new(StringComparer.OrdinalIgnoreCase);
    private const int EmbCap = 8192;   // ~32 MB worst case

    private static CnnEmbedder? _cnn;
    private static bool _cnnFailed;
    private static readonly object _cnnLock = new();

    /// <summary>The engine can evaluate right now (natives/model deployed for cnn; always for hashes).</summary>
    public static bool IsAvailable(DupEngineMode mode)
        => mode != DupEngineMode.Cnn || (!_cnnFailed && CnnEmbedder.IsAvailable());

    /// <summary>Evaluates <paramref name="candidate"/> against ALL of <paramref name="accepted"/> and
    /// returns (dup, bestScore): dup = duplicates at least one; bestScore = the CLOSEST similarity found,
    /// in the engine's native scale (cnn: max cosine, rounded 4 decimals; hashes: MIN Hamming distance) —
    /// stored in the ADS record purely for visual/manual debugging (the decision only uses dup).
    /// dup = null when the engine can't answer (missing natives, decode failure) — caller fails open.
    /// No early-out on the first match: the full scan is what makes bestScore the true best, and the
    /// per-image feature memos make the extra comparisons (dot products / popcounts) negligible.</summary>
    public static (bool? dup, double? score) Evaluate(DupEngineMode mode, double threshold, bool gpu,
                                                      string candidate, IReadOnlyList<string> accepted)
    {
        if (accepted == null || accepted.Count == 0) return (false, null);
        try
        {
            if (mode == DupEngineMode.Cnn)
            {
                var emb = EmbeddingOf(candidate, gpu);
                if (emb == null) return (null, null);
                double best = double.NegativeInfinity;
                foreach (var a in accepted)
                {
                    var ea = EmbeddingOf(a, gpu);
                    if (ea == null) continue;   // unreadable reference → skip it, not fatal
                    double c = CnnEmbedder.Cosine(emb, ea);
                    if (c > best) best = c;
                }
                if (double.IsNegativeInfinity(best)) return (false, null);   // no readable reference
                return (best >= threshold, Math.Round(best, 4));
            }
            else
            {
                ulong h = HashOf(mode, candidate);
                int best = int.MaxValue;
                foreach (var a in accepted)
                {
                    int d = DedupHash.Hamming(h, HashOf(mode, a));
                    if (d < best) best = d;
                }
                return (best <= (int)Math.Round(threshold), best);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[dedup] evaluate failed (" + candidate + "): " + ex.Message);
            return (null, null);
        }
    }

    private static ulong HashOf(DupEngineMode mode, string path)
    {
        var memo = mode == DupEngineMode.DHash ? _dhash : _phash;
        if (memo.TryGetValue(path, out var cached)) return cached;
        ulong h = mode == DupEngineMode.DHash
            ? DedupHash.DHash(DedupPreprocess.LoadGrayResized(path, 9, 8))
            : DedupHash.PHash(DedupPreprocess.LoadGrayResized(path, 32, 32));
        memo[path] = h;
        return h;
    }

    private static float[]? EmbeddingOf(string path, bool gpu)
    {
        if (_emb.TryGetValue(path, out var cached)) return cached;
        var cnn = Session(gpu);
        if (cnn == null) return null;
        float[] emb;
        try { emb = cnn.Embed(DedupPreprocess.LoadCnnInput(path)); }
        catch (Exception ex) { Console.WriteLine("[dedup] embed failed (" + path + "): " + ex.Message); return null; }
        if (_emb.Count >= EmbCap) _emb.Clear();   // crude but bounded; features recompute on demand
        _emb[path] = emb;
        return emb;
    }

    private static bool _cnnGpuPref;
    private static CnnEmbedder? Session(bool gpu)
    {
        var cur = _cnn;
        if (cur != null && _cnnGpuPref == gpu) return cur;
        if (_cnnFailed || !CnnEmbedder.IsAvailable()) return null;
        lock (_cnnLock)
        {
            if (_cnnFailed) return null;
            if (_cnn != null && _cnnGpuPref == gpu) return _cnn;
            // GPU preference flipped (or first use): recreate the session AND drop memoized embeddings —
            // CPU/GPU floats differ in low-order bits, and the dup-param hash labels results by preference.
            try { _cnn?.Dispose(); } catch { }
            _cnn = null;
            _emb.Clear();
            try { _cnn = new CnnEmbedder(gpu); _cnnGpuPref = gpu; }
            catch (Exception ex)
            {
                _cnnFailed = true;   // don't retry a broken runtime every image
                Console.WriteLine("[dedup] CNN session failed: " + ex.Message);
            }
            return _cnn;
        }
    }
}

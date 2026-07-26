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
    /// <summary>Bump when the fingerprint pipeline OR the record keying changes (decoder, preprocessing,
    /// model, key schema) — salts the dup-param hash so every persisted ADS result is invalidated at once.
    /// v2 = ctx-based keying (evaluation-context hash replaced the sort+pool pair).</summary>
    public const int Version = 2;

    /// <summary>Per-comparison trace ([dedup] lines: every file-vs-file score, cache hits, verdicts).
    /// Set at boot ONLY in debug mode (--debug / DebugLog=true) — stays false in normal use so the
    /// hot path doesn't even pay the string formatting.</summary>
    public static bool Verbose;

    /// <summary>Last 3 path segments ("Type\Region\file.jpg") — full paths would drown the trace, but a
    /// bare filename is ambiguous (the SAME name in two region folders is precisely the dup case).</summary>
    internal static string Short(string p)
    {
        try { var parts = p.Split('\\', '/'); return string.Join("\\", parts[Math.Max(0, parts.Length - 3)..]); }
        catch { return p; }
    }

    public static DupEngineMode ParseMode(string? s) => s?.ToLowerInvariant() switch
    {
        "dhash" => DupEngineMode.DHash,
        "cnn" => DupEngineMode.Cnn,
        _ => DupEngineMode.PHash,
    };

    /// <summary>Engine default threshold: max Hamming distance (hashes) / min cosine similarity (cnn).
    /// cnn 0.85 (not imagededup's 0.90): validated on a real library — regional box variants of the same
    /// art land around 0.89, which 0.90 would keep as "different".</summary>
    public static double DefaultThreshold(DupEngineMode m) => m == DupEngineMode.Cnn ? 0.85 : 10;

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

    /// <summary>Evaluates <paramref name="candidate"/> against <paramref name="accepted"/> and returns
    /// (dup, score). EARLY-OUT on the first match (zero perf cost vs a plain yes/no):
    ///   • dup=1 → score = the similarity of the FIRST reference that crossed the threshold (the image
    ///     that got the candidate filtered) — not necessarily the global best;
    ///   • dup=0 → score = the TRUE closest similarity: concluding "not a dup" required comparing against
    ///     every reference anyway, so the best is free. This is the debug case that matters ("this image
    ///     displays — how close was it to being filtered?").
    /// Scale is the engine's native one (cnn: cosine, 4 decimals; hashes: Hamming distance). The decision
    /// only uses dup; score is stored in the ADS record purely for visual/manual debugging.
    /// dup = null when the engine can't answer (missing natives, decode failure) — caller fails open.</summary>
    public static (bool? dup, double? score) Evaluate(DupEngineMode mode, double threshold, bool gpu,
                                                      string candidate, IReadOnlyList<string> accepted)
    {
        if (accepted == null || accepted.Count == 0) return (false, null);
        try
        {
            if (Verbose) Console.WriteLine($"[dedup] eval {Short(candidate)}  ({mode.ToString().ToLowerInvariant()} thr={threshold:0.###}, {accepted.Count} ref)");
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
                    if (Verbose) Console.WriteLine($"[dedup]   cos={c:0.0000}  vs {Short(a)}");
                    if (c > best) best = c;
                    if (c >= threshold)   // early-out: the filtering match
                    {
                        if (Verbose) Console.WriteLine($"[dedup]   => dup=1 (early-out, score={Math.Round(c, 4)})");
                        return (true, Math.Round(c, 4));
                    }
                }
                if (double.IsNegativeInfinity(best)) return (false, null);   // no readable reference
                if (Verbose) Console.WriteLine($"[dedup]   => dup=0 (best={Math.Round(best, 4)})");
                return (false, Math.Round(best, 4));
            }
            else
            {
                ulong h = HashOf(mode, candidate);
                int max = (int)Math.Round(threshold);
                int best = int.MaxValue;
                foreach (var a in accepted)
                {
                    int d = DedupHash.Hamming(h, HashOf(mode, a));
                    if (Verbose) Console.WriteLine($"[dedup]   ham={d}  vs {Short(a)}");
                    if (d < best) best = d;
                    if (d <= max)   // early-out: the filtering match
                    {
                        if (Verbose) Console.WriteLine($"[dedup]   => dup=1 (early-out, score={d})");
                        return (true, d);
                    }
                }
                if (Verbose) Console.WriteLine($"[dedup]   => dup=0 (best={best})");
                return (false, best);
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
        // Decode/preprocess OUTSIDE the session lock (no session needed, and it's the slow part).
        float[] chw;
        try { chw = DedupPreprocess.LoadCnnInput(path); }
        catch (Exception ex) { Console.WriteLine("[dedup] decode failed (" + path + "): " + ex.Message); return null; }
        float[] emb;
        // Inference UNDER the lock: the idle sweep and Suspend take the same lock, so a dispose can never
        // hit a run in flight — a caller either lands before (fresh _lastUse → the sweep skips) or after
        // (session gone → clean ~0.5 s re-create). Worst case is a re-init, never an error.
        lock (_cnnLock)
        {
            var cnn = SessionUnderLock(gpu);
            if (cnn == null) return null;
            _lastUse = DateTime.UtcNow;
            try { emb = cnn.Embed(chw); }
            catch (Exception ex) { Console.WriteLine("[dedup] embed failed (" + path + "): " + ex.Message); return null; }
        }
        if (_emb.Count >= EmbCap) _emb.Clear();   // crude but bounded; features recompute on demand
        _emb[path] = emb;
        return emb;
    }

    // ── Game-launch lifecycle (RAM-at-launch policy, like HostGameCache / VlcService) ────────────────
    // A live CNN session costs ~230 MB working set (DirectML device + arenas + model) AND holds a D3D12
    // device with VRAM allocations — exactly what a running game wants back. Suspend() drops it (and the
    // embedding memo); while suspended the CNN never re-creates, and MediaDupFilter serves cached ADS
    // verdicts (best-effort hint on a key miss). Resume() just lifts the flag — the session lazily
    // re-creates (~0.5 s) on the next media-list build that needs it.
    private static bool _suspended;

    /// <summary>True while a game runs (CNN session released — see Suspend).</summary>
    public static bool Suspended => _suspended;

    /// <summary>Release the CNN session + embedding memo (game launch). Cheap no-op when none is live.</summary>
    public static void Suspend()
    {
        _suspended = true;
        lock (_cnnLock)
        {
            if (_cnn != null) Console.WriteLine("[dedup] CNN session released for game launch");
            try { _cnn?.Dispose(); } catch { }
            _cnn = null;
            _emb.Clear();
        }
    }

    /// <summary>Allow the CNN session again (game exit). Lazy — nothing is created until needed.</summary>
    public static void Resume() => _suspended = false;

    // ── Idle auto-release ────────────────────────────────────────────────────
    // The session is created OPPORTUNISTICALLY (first cache miss) and, once idle for IdleReleaseMinutes,
    // released again (~230 MB RAM + the DirectML device back) by a 60 s sweep. The embedding memo is KEPT
    // on an idle release (a few MB at most; still valid — same gpu pref on re-create) so a later burst
    // only pays the ~0.5 s session init, not the re-embeds.
    private const int IdleReleaseMinutes = 5;
    private static DateTime _lastUse;              // written under _cnnLock
    private static System.Threading.Timer? _idleTimer;

    private static void IdleSweep()
    {
        lock (_cnnLock)
        {
            if (_cnn == null) return;
            if (DateTime.UtcNow - _lastUse < TimeSpan.FromMinutes(IdleReleaseMinutes)) return;
            Console.WriteLine($"[dedup] CNN session released after {IdleReleaseMinutes} min idle");
            try { _cnn.Dispose(); } catch { }
            _cnn = null;
        }
    }

    private static bool _cnnGpuPref;
    /// <summary>Get-or-create the CNN session. MUST be called holding <see cref="_cnnLock"/> — that lock is
    /// what makes the idle sweep / Suspend unable to dispose a session mid-inference.</summary>
    private static CnnEmbedder? SessionUnderLock(bool gpu)
    {
        if (_suspended || _cnnFailed) return null;   // game running / broken runtime → no session
        if (_cnn != null && _cnnGpuPref == gpu) return _cnn;
        if (!CnnEmbedder.IsAvailable()) return null;
        // GPU preference flipped (or first use): recreate the session AND drop memoized embeddings —
        // CPU/GPU floats differ in low-order bits, and the dup-param hash labels results by preference.
        try { _cnn?.Dispose(); } catch { }
        _cnn = null;
        if (_cnnGpuPref != gpu) _emb.Clear();
        try
        {
            _cnn = new CnnEmbedder(gpu);
            _cnnGpuPref = gpu;
            _idleTimer ??= new System.Threading.Timer(_ => IdleSweep(), null, 60_000, 60_000);
        }
        catch (Exception ex)
        {
            _cnnFailed = true;   // don't retry a broken runtime every image
            Console.WriteLine("[dedup] CNN session failed: " + ex.Message);
        }
        return _cnn;
    }
}

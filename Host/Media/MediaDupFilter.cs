// Per-game/per-view duplicate filter applied while BUILDING the post-load media list. Snapshot of the
// three cache keys (sort / pool / par — see DupCheckAds) + the engine settings, taken once per build:
//   • cached ADS result under the current triplet → reuse, zero decode;
//   • else evaluate via DedupEngine (candidate vs the images already ACCEPTED into the list) and persist;
//   • engine can't answer (missing natives, decode error) → fail OPEN (keep the image), persist nothing.
// `force` (the Caches → "update duplicates" pass) recomputes and rewrites even valid cached results.

#nullable enable

using System;
using System.Collections.Generic;

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

    /// <summary>True → skip this candidate (it duplicates an image already accepted into the list).</summary>
    public bool IsDup(string path, IReadOnlyList<string> accepted)
    {
        try
        {
            if (!_force && DupCheckAds.TryGetResult(path, _poster, _sort, _pool, _par, out bool cached))
                return cached;
            var (r, score) = Dedup.DedupEngine.Evaluate(_mode, _threshold, _gpu, path, accepted);
            if (r == null) return false;   // can't evaluate → keep the image, don't persist
            DupCheckAds.Write(path, _poster, _sort, _pool, _par, r.Value, score);
            return r.Value;
        }
        catch { return false; }
    }
}

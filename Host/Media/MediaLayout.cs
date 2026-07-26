// User-configurable right-detail-pane image layout (Options → Display → Right panel).
//
//   • Immediate image family PER VIEW (list vs poster) — the box shown the instant a game is selected,
//     before the detail-load delay. Defaults to "Front" for both (the previous hard-wired behaviour).
//   • Post-load ordered image LIST — replaces the hard-coded BuildMediaList. Each entry names a FAMILY
//     (regroupement, e.g. "Screenshots") OR an EXACT LaunchBox image type (e.g. "Screenshot - Game Title")
//     and a Count (max images to take from it). The list is ordered = priority; entry[0]'s first image is
//     the main box that the delay upgrades to full-res. Default reproduces the old list.
//
// Selection within an entry is "auto" (LaunchBox's type→region→numeric algorithm) for now; the Mode/Weights
// fields are reserved for a future weighted picker (region / type / numeric / aspect-ratio) — the config
// round-trips them so the UI and storage are ready, but the resolver currently ignores them.
//
// Stored as JSON at Core\litebox\media-layout.json (LiteBox-own, like every other config).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LbApiHost.Host.Media;

internal sealed class MediaEntry
{
    /// <summary>Family regroupement key (Front, Screenshots, …) or an exact LB image type when <see cref="ExactType"/>.</summary>
    public string Sel { get; set; } = "Front";
    /// <summary>True → <see cref="Sel"/> is an exact LB image type ("Screenshot - Game Title"); false → a family.</summary>
    public bool ExactType { get; set; }
    /// <summary>Max images to take from this entry (1 = a single best image). When <see cref="Cumulative"/>,
    /// this is a TARGET TOTAL counting the images already contributed by the <see cref="CumulativeDepth"/>
    /// entries above — this entry only tops up to reach it.</summary>
    public int Count { get; set; } = 99;
    /// <summary>Count cumulatively: the target includes the images from the N entries above.</summary>
    public bool Cumulative { get; set; }
    /// <summary>How many entries directly above to include in the cumulative total (1 = just the one above).</summary>
    public int CumulativeDepth { get; set; } = 1;
    /// <summary>"auto" (LB algorithm) or "weighted" (reserved — not yet resolved).</summary>
    public string Mode { get; set; } = "auto";
    /// <summary>Reserved weighted-picker weights (region / type / numeric / aspect). Unused for now.</summary>
    public int WRegion { get; set; } = 1;
    public int WType { get; set; } = 1;
    public int WNumeric { get; set; } = 1;
    public int WAspect { get; set; }
    /// <summary>Region pick for THIS entry: false (DEFAULT) = LaunchBox-identical — the GAME's own region(s)
    /// FIRST, then the user's region priorities + LB fallback; true = ignore the game's region and use only
    /// the global region priority list.</summary>
    public bool IgnoreGameRegion { get; set; }
    /// <summary>false (DEFAULT) = take images from the BEST region only (the first region in priority order
    /// that has one) — like LaunchBox, and avoids the same art appearing once per region; true = take from
    /// ALL regions (⚠ can produce visual duplicates of the same image that exists in several region folders).</summary>
    public bool AllRegions { get; set; }

    public MediaEntry Clone() => (MediaEntry)MemberwiseClone();
    public string Label()
        => (ExactType ? "🎞 " : "") + Sel
         + (Count < 99 ? $"  ×{Count}" : "")
         + (Cumulative ? $"  (Σ{CumulativeDepth}↑)" : "")
         + (IgnoreGameRegion ? "  ⊘region" : "")
         + (AllRegions ? "  ∗all-regions" : "");
}

internal sealed class MediaLayout
{
    public string ImmediateList { get; set; } = "Front";     // family shown instantly when selecting in LIST view
    public string ImmediatePoster { get; set; } = "Front";   // …in POSTER view
    public List<MediaEntry> PostLoad { get; set; } = new();        // post-load list for LIST view (and Poster when !PosterIndependent)
    public List<MediaEntry> PostLoadPoster { get; set; } = new();  // Poster view's OWN post-load list (used only when PosterIndependent)
    public bool PosterIndependent { get; set; }                    // false (default) = Poster reuses the List post-load list

    /// <summary>The post-load list for the active view. Poster reuses List's unless it has its own
    /// (<see cref="PosterIndependent"/> + a non-empty <see cref="PostLoadPoster"/>).</summary>
    public List<MediaEntry> PostLoadFor(bool poster)
        => (poster && PosterIndependent && PostLoadPoster.Count > 0) ? PostLoadPoster : PostLoad;

    // ── Config fingerprint (dev/diagnostic only) ──────────────────────────────
    // Per-view MD5 of the EFFECTIVE post-load config, dumped by the --media-hash dev flag to diff two
    // machines'/moments' configs at a glance. HISTORY: this was the v1 anti-dup "sort" key (persisted in
    // LiteBox.ini); the ctx-based keying (DupCheckAds — evaluation-context hash) subsumed it, and the ini
    // keys are scrubbed by the Options → Display apply.

    /// <summary>Canonical JSON of a view's effective post-load list — ONLY the resolution-affecting fields,
    /// in order, so it is stable (identical config → identical JSON → identical MD5).</summary>
    public string PostLoadJson(bool poster)
        => JsonSerializer.Serialize(PostLoadFor(poster).Select(e => new
        {
            e.Sel, e.ExactType, e.Count, e.Cumulative, e.CumulativeDepth, e.IgnoreGameRegion, e.AllRegions,
        }), Json);

    /// <summary>Lower-case hex MD5 of <see cref="PostLoadJson"/> — the config fingerprint (--media-hash).</summary>
    public string PostLoadHash(bool poster)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(PostLoadJson(poster)))).ToLowerInvariant();

    // ── 3D case block ─────────────────────────────────────────────────────────
    // Independent of the post-load image list (own row under the hero, own GLB cache) — deliberately NOT
    // part of PostLoadJson/PostLoadHash nor of the dup params.

    /// <summary>Stored INVERTED (default-off booleans survive the WhenWritingDefault serializer; a
    /// default-ON one would lose the user's "off" on reload). Use <see cref="Show3dBox"/>.</summary>
    public bool Hide3dBox { get; set; }

    /// <summary>Show the 3D case model under the hero image (right detail panel). Default on.</summary>
    [JsonIgnore]
    public bool Show3dBox { get => !Hide3dBox; set => Hide3dBox = !value; }

    // ── Duplicate prevention (post-load filter) ───────────────────────────────
    // These settings are deliberately NOT part of PostLoadJson/PostLoadHash: toggling the dup filter must
    // not shift the per-view config fingerprints. They are fingerprinted SEPARATELY (DupParamHash8) — the
    // third key of each per-image cached result (see DupCheckAds).

    /// <summary>Skip a post-load image when it duplicates one already accepted before it (per view).</summary>
    public bool PreventDuplicates { get; set; }
    /// <summary>Engine: "cnn" (deep embeddings — default), "phash" or "dhash".</summary>
    public string DupEngine { get; set; } = "cnn";
    /// <summary>Decision threshold; &lt; 0 = engine default (Hamming ≤ 10 for hashes, cosine ≥ 0.90 for cnn).</summary>
    public double DupThreshold { get; set; } = -1;
    /// <summary>cnn only: prefer the DirectML GPU session (auto CPU fallback).</summary>
    public bool DupGpu { get; set; } = true;

    public double EffectiveDupThreshold()
        => DupThreshold >= 0 ? DupThreshold : Dedup.DedupEngine.DefaultThreshold(Dedup.DedupEngine.ParseMode(DupEngine));

    /// <summary>Canonical JSON of the dup-check params (incl. the engine Version salt).</summary>
    public string DupParamJson()
        => JsonSerializer.Serialize(new { v = Dedup.DedupEngine.Version, e = DupEngine, t = EffectiveDupThreshold(), g = DupGpu });

    /// <summary>First 8 hex of MD5(<see cref="DupParamJson"/>) — the "par" key of cached ADS results.</summary>
    public string DupParamHash8()
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(DupParamJson()))).ToLowerInvariant().Substring(0, 8);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private static string Path => LiteBoxPaths.File("media-layout.json");

    /// <summary>The default layout — the curated set: box front as the main box, ONE marquee (global
    /// region priority — marquees rarely exist per-region), then up to 5 screenshots and 5 backgrounds.
    /// (The original default mirrored the old hard-coded BuildMediaList: Front ×1 + every game-title/
    /// gameplay screenshot + every fanart, which flooded the strip on well-scraped libraries.)</summary>
    public static MediaLayout Default() => new()
    {
        ImmediateList = "Front", ImmediatePoster = "Front",
        PostLoad = new()
        {
            new MediaEntry { Sel = "Front", ExactType = false, Count = 1 },
            new MediaEntry { Sel = "Marquee", ExactType = false, Count = 1, IgnoreGameRegion = true },
            new MediaEntry { Sel = "Screenshots", ExactType = false, Count = 5 },
            new MediaEntry { Sel = "Background", ExactType = false, Count = 5 },
        },
    };

    private static MediaLayout? _cached;
    public static MediaLayout Current => _cached ??= Load();

    public static MediaLayout Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var l = JsonSerializer.Deserialize<MediaLayout>(File.ReadAllText(Path));
                if (l != null && l.PostLoad.Count > 0) return l;
            }
        }
        catch { }
        return Default();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Json)); _cached = this; }
        catch { }
    }

    public MediaLayout Clone() => new()
    {
        ImmediateList = ImmediateList, ImmediatePoster = ImmediatePoster,
        PostLoad = PostLoad.Select(e => e.Clone()).ToList(),
        PostLoadPoster = PostLoadPoster.Select(e => e.Clone()).ToList(),
        PosterIndependent = PosterIndependent, Hide3dBox = Hide3dBox,
        PreventDuplicates = PreventDuplicates, DupEngine = DupEngine, DupThreshold = DupThreshold, DupGpu = DupGpu,
    };

    // ── Catalogs for the config UI ────────────────────────────────────────────
    /// <summary>Family regroupements (Key = stored value, Title = display).</summary>
    public static (string Key, string Title)[] Families => Host.MainWindow.CacheRegroupements;

    /// <summary>Every known exact LB image type (for "specific media" entries).</summary>
    public static string[] ExactTypes()
    {
        try { return MediaResolver.ImageTypeNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return Array.Empty<string>(); }
    }
}

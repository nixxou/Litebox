// Persistent per-image duplicate-check results, stored in ONE NTFS alternate data stream per image:
// <image>:lb.dupcheck. A single consolidated stream (instead of one per key) keeps the MFT record
// resident alongside ExtendDB's :info/:crc32 (each named stream costs its own ~100-byte attribute header).
//
// Content = compact JSON with one record per VIEW:
//   {"list":{"sort":"9f3ab2c1","pool":"4e21d0aa","par":"7c19e3b2","dup":0,"score":0.6411},"poster":{...}}
//     sort = 8-hex of the view's post-load config hash (MediaLayout.PostLoadHash)
//     pool = the game's image-pool signature (MediaSignature sig8)
//     par  = 8-hex of the dup-check params (engine/threshold/gpu/version — MediaLayout.DupParamHash8)
//     dup  = 1 when this image duplicates one accepted BEFORE it in that view's list
// A record is valid iff its (sort,pool,par) triplet matches the current one; a stale record is simply
// overwritten (the OTHER view's record is preserved). Cross-view reuse: when the other view's stored
// triplet equals the CURRENT one (e.g. poster reuses the list config), its result is used directly.
//
// Writes go through a session memo first (one persistent read per image per session). Volumes WITHOUT
// named-stream support (exFAT, network shares) fall back to a JSON sidecar — same ADS-else-sidecar
// strategy as FileMetaStore, but a DEDICATED file (<dir>\.ads\<name>.dupcheck.json): the shared .ads
// sidecar has ExtendDB's fixed {crc32,info,lock} shape, and ExtendDB would drop any extra field when it
// rewrites that file. Backend choice reuses FileMetaStore's cached per-drive capability probe.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LbApiHost.Host.Media;

internal static class DupCheckAds
{
    private const string StreamSuffix = ":lb.dupcheck";

    internal sealed class Rec
    {
        [JsonPropertyName("sort")] public string Sort { get; set; } = "";
        [JsonPropertyName("pool")] public string Pool { get; set; } = "";
        [JsonPropertyName("par")] public string Par { get; set; } = "";
        [JsonPropertyName("dup")] public int Dup { get; set; }
        /// <summary>Best similarity found vs the images accepted before this one, in the engine's native
        /// scale (cnn: max cosine; hashes: min Hamming). DEBUG-ONLY — the decision only reads Dup; this
        /// just makes the Info box / a manual ADS dump interpretable. Null on old records or when there
        /// was nothing to compare against (first image of the list).</summary>
        [JsonPropertyName("score")] public double? Score { get; set; }

        public bool Matches(string sort, string pool, string par)
            => Sort == sort && Pool == pool && Par == par;
    }

    internal sealed class Dto
    {
        [JsonPropertyName("list")] public Rec? List { get; set; }
        [JsonPropertyName("poster")] public Rec? Poster { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Session memo: image path → parsed record (null = known-absent). Mirrors the ADS and IS the store
    // on volumes where the ADS write fails.
    private static readonly ConcurrentDictionary<string, Dto?> _memo = new(StringComparer.OrdinalIgnoreCase);

    // ── Persistent backend: ADS on capable volumes, dedicated sidecar elsewhere ──
    private const string SidecarSuffix = ".dupcheck.json";

    private static string? SidecarPathOf(string imgPath)
    {
        string? dir = FileMetaStore.SidecarDirOf(imgPath);
        return dir == null ? null : Path.Combine(dir, Path.GetFileName(imgPath) + SidecarSuffix);
    }

    private static Dto? Load(string imgPath)
    {
        if (_memo.TryGetValue(imgPath, out var cached)) return cached;
        Dto? dto = null;
        try
        {
            string? p = FileMetaStore.VolumeSupportsAds(imgPath) ? imgPath + StreamSuffix : SidecarPathOf(imgPath);
            if (p != null && File.Exists(p))
            {
                string raw = File.ReadAllText(p);
                if (!string.IsNullOrWhiteSpace(raw)) dto = JsonSerializer.Deserialize<Dto>(raw);
            }
        }
        catch { }   // absent / malformed → treated as no data
        _memo[imgPath] = dto;
        return dto;
    }

    /// <summary>The raw stored records (both views) for display/diagnostics (the Edit-Game image Info box),
    /// or null when the image has no :lb.dupcheck data. Served through the session memo like every read.</summary>
    public static Dto? Peek(string imgPath) => Load(imgPath);

    /// <summary>Cached dup result for this image under the CURRENT (sort,pool,par) triplet — own view
    /// first, then the other view when its stored triplet matches (identical effective config). False
    /// return = no valid cached result, compute it.</summary>
    public static bool TryGetResult(string imgPath, bool poster, string sort, string pool, string par, out bool dup)
    {
        dup = false;
        var dto = Load(imgPath);
        if (dto == null) return false;
        var own = poster ? dto.Poster : dto.List;
        if (own != null && own.Matches(sort, pool, par)) { dup = own.Dup != 0; return true; }
        var other = poster ? dto.List : dto.Poster;
        if (other != null && other.Matches(sort, pool, par)) { dup = other.Dup != 0; return true; }
        return false;
    }

    /// <summary>Store this view's result (the other view's record is preserved). Memo always updated;
    /// the persistent write is best-effort — ADS on capable volumes, else the dedicated sidecar (hidden
    /// .ads folder, created on demand like FileMetaStore's).</summary>
    public static void Write(string imgPath, bool poster, string sort, string pool, string par, bool dup, double? score = null)
    {
        var dto = Load(imgPath) ?? new Dto();
        var rec = new Rec { Sort = sort, Pool = pool, Par = par, Dup = dup ? 1 : 0, Score = score };
        if (poster) dto.Poster = rec; else dto.List = rec;
        _memo[imgPath] = dto;
        try
        {
            string json = JsonSerializer.Serialize(dto, Json);
            if (FileMetaStore.VolumeSupportsAds(imgPath))
            {
                File.WriteAllText(imgPath + StreamSuffix, json);
            }
            else
            {
                string? p = SidecarPathOf(imgPath);
                if (p == null) return;
                string? folder = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    var di = Directory.CreateDirectory(folder);
                    try { di.Attributes |= FileAttributes.Hidden | FileAttributes.System; } catch { }
                }
                File.WriteAllText(p, json);
            }
        }
        catch { }   // persistence unavailable → session memo only
    }
}

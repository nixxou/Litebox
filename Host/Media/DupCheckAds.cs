// Persistent per-image duplicate-check results, stored in ONE NTFS alternate data stream per image:
// <image>:lb.dupcheck. A single consolidated stream (instead of one per key) keeps the MFT record
// resident alongside ExtendDB's :info/:crc32 (each named stream costs its own ~100-byte attribute header).
//
// Content = compact JSON with one record per VIEW:
//   {"list":{"sort":"9f3ab2c1","pool":"4e21d0aa","par":"7c19e3b2","dup":0},"poster":{...}}
//     sort = 8-hex of the view's post-load config hash (MediaLayout.PostLoadHash)
//     pool = the game's image-pool signature (MediaSignature sig8)
//     par  = 8-hex of the dup-check params (engine/threshold/gpu/version — MediaLayout.DupParamHash8)
//     dup  = 1 when this image duplicates one accepted BEFORE it in that view's list
// A record is valid iff its (sort,pool,par) triplet matches the current one; a stale record is simply
// overwritten (the OTHER view's record is preserved). Cross-view reuse: when the other view's stored
// triplet equals the CURRENT one (e.g. poster reuses the list config), its result is used directly.
//
// Writes go through a session memo first (one ADS read per image per session); volumes without ADS
// support (exFAT, network shares) degrade to the memo only — results just aren't persisted there.

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

    private static Dto? Load(string imgPath)
    {
        if (_memo.TryGetValue(imgPath, out var cached)) return cached;
        Dto? dto = null;
        try
        {
            string raw = File.ReadAllText(imgPath + StreamSuffix);
            if (!string.IsNullOrWhiteSpace(raw)) dto = JsonSerializer.Deserialize<Dto>(raw);
        }
        catch { }   // stream absent / non-NTFS / malformed → treated as no data
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
    /// the ADS write is best-effort (non-NTFS volumes keep the session memo only).</summary>
    public static void Write(string imgPath, bool poster, string sort, string pool, string par, bool dup)
    {
        var dto = Load(imgPath) ?? new Dto();
        var rec = new Rec { Sort = sort, Pool = pool, Par = par, Dup = dup ? 1 : 0 };
        if (poster) dto.Poster = rec; else dto.List = rec;
        _memo[imgPath] = dto;
        try { File.WriteAllText(imgPath + StreamSuffix, JsonSerializer.Serialize(dto, Json)); }
        catch { }   // ADS unsupported here → session memo only
    }
}

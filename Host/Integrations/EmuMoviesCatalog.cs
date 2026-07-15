// Per-game EmuMovies resolver — the SIMPLE matcher. There is no per-game API, so we pull the whole platform
// dict (EmuMoviesApi, cached) and match this game against its keys locally. Deliberately lightweight (the user
// asked for simple, misses are acceptable — the extended DB / purple source covers the rest):
//
//   • title match: sanitize the LB title and each EmuMovies key (tags stripped) to a CompareName, compare.
//   • rom-stem match: EmuMovies arcade keys ARE the MAME short name → match the ROM basename against the key.
//
// No accent-fold fallback, no alternate-title/year/piggyback passes (that's the offline scraper's job). If more
// than one EmuMovies entry matches (different regions), we keep them all. Region comes from the key's
// parenthetical tags (EmuMoviesMaps.Region), media type from EmuMoviesMaps.LbType (null / system-only skipped).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LbApiHost.Host.Integrations;

internal static class EmuMoviesCatalog
{
    /// <summary>One resolved EmuMovies asset for a game: ready to stream (Url) or download.</summary>
    internal readonly record struct EmuMedia(
        string LbType, string Region, string Url, string Ext, long Crc, long FileSize, string MediaType);

    /// <summary>True when this LB platform maps to an EmuMovies one (else nothing to resolve).</summary>
    public static bool SupportsPlatform(string lbPlatform)
        => !string.IsNullOrEmpty(lbPlatform) && EmuMoviesMaps.Platform.ContainsKey(lbPlatform);

    /// <summary>
    /// EmuMovies assets for a game, best-effort. Empty when the platform isn't mapped, credentials are missing,
    /// or nothing matched. <paramref name="romPath"/> feeds the MAME rom-stem match (may be null).
    /// </summary>
    public static async Task<List<EmuMedia>> ResolveForGameAsync(
        EmuMoviesApi api, string title, string? romPath, string lbPlatform, CancellationToken ct = default)
    {
        if (api == null || !api.HasCredentials) return new List<EmuMedia>();
        if (!EmuMoviesMaps.Platform.TryGetValue(lbPlatform ?? "", out var emuPlatforms) || emuPlatforms.Length == 0)
            return new List<EmuMedia>();

        string compare = Sanitize(title);
        string romStem = RomStem(romPath);

        // Try the mapped EmuMovies platforms IN ORDER; stop at the first that yields any media for this game.
        // e.g. LB "Arcade" tries "Arcade" then "ArcadePC". Each dict is cached (per-instance + disk), so a second
        // platform costs a lookup, not a re-fetch, once warmed.
        foreach (var emuPlatform in emuPlatforms)
        {
            if (ct.IsCancellationRequested) break;
            var dict = await api.GetMediaForPlatformAsync(emuPlatform, ct).ConfigureAwait(false);
            if (dict.Count == 0) continue;
            var media = MatchIn(dict, emuPlatform, compare, romStem);
            if (media.Count > 0) return media;   // first platform that matches wins
        }
        return new List<EmuMedia>();
    }

    /// <summary>Match a game (by sanitized title or MAME rom-stem) against one EmuMovies platform dict and build
    /// the resolved assets. Empty when the game isn't in this platform.</summary>
    private static List<EmuMedia> MatchIn(
        Dictionary<string, List<EmuMoviesApi.MediaItem>> dict, string emuPlatform, string compare, string romStem)
    {
        var result = new List<EmuMedia>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, items) in dict)
        {
            if (IsParasiticKey(key)) continue;
            bool titleHit = compare.Length > 0 && Sanitize(StripTags(key)) == compare;
            bool romHit = romStem.Length > 0 && KeyStem(key) == romStem;
            if (!titleHit && !romHit) continue;

            string region = RegionOf(key);
            foreach (var it in items)
            {
                if (EmuMoviesMaps.SystemOnly.Contains(it.MediaType)) continue;
                if (!EmuMoviesMaps.LbType.TryGetValue(it.MediaType, out var lbType) || lbType == null) continue;

                string ext = string.IsNullOrEmpty(it.FileExtension) ? "" : it.FileExtension;
                string url = BuildUrl(emuPlatform, it.MediaType, key, ext);
                if (!seen.Add($"{lbType}|{region}|{url}")) continue;
                result.Add(new EmuMedia(lbType, region, url, ext.TrimStart('.'), it.Crc, it.FileSize, it.MediaType));
            }
        }
        return result;
    }

    /// <summary>media.emumovies.com/<Platform>/<MediaType>/<gameNameOriginal(url-encoded)><ext></summary>
    public static string BuildUrl(string platform, string mediaType, string gameNameOriginal, string ext)
        => EmuMoviesApi.MediaBase + platform + "/" + mediaType + "/" + Uri.EscapeDataString(gameNameOriginal) + ext;

    // ── Matching helpers (pure) ───────────────────────────────────────────────

    /// <summary>Name-compare sanitize: strip () [] {} groups, punctuation → space, roman numerals → digits,
    /// drop leading articles, collapse, uppercase. A single pass — the simple matcher.</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = name;

        s = Regex.Replace(s, @"\([^)]*\)", " ");
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\{[^}]*\}", " ");

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if ("-:&!,/\\?".IndexOf(c) >= 0) sb.Append(' ');
            else if ("'.\"".IndexOf(c) >= 0) { /* drop */ }
            else sb.Append(c);
        }
        s = Regex.Replace(sb.ToString(), " {2,}", " ").Trim();

        s = Regex.Replace(s, @"(?<=^|\s)VIII(?=\s|$)", "8");
        s = Regex.Replace(s, @"(?<=^|\s)VII(?=\s|$)", "7");
        s = Regex.Replace(s, @"(?<=^|\s)VI(?=\s|$)", "6");
        s = Regex.Replace(s, @"(?<=^|\s)IV(?=\s|$)", "4");
        s = Regex.Replace(s, @"(?<=^|\s)III(?=\s|$)", "3");
        s = Regex.Replace(s, @"(?<=^|\s)II(?=\s|$)", "2");
        s = Regex.Replace(s, @"(?<=^|\s)V(?=\s|$)", "5");

        s = Regex.Replace(s, @"(?<=^|\s)the(?=\s|$)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=^|\s)and(?=\s|$)", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=^|\s)an(?=\s|$)", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=^|\s)a(?=\s|$)", " ", RegexOptions.IgnoreCase);

        return Regex.Replace(s, " {2,}", " ").Trim().ToUpperInvariant();
    }

    /// <summary>Everything before the first '(' — the bare name for title comparison.</summary>
    private static string StripTags(string key)
    {
        int p = key.IndexOf('(');
        return p >= 0 ? key.Substring(0, p) : key;
    }

    /// <summary>ROM basename, lowercased, before the first '.' — the MAME-short-name match key.</summary>
    public static string RomStem(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath)) return "";
        string name;
        try { name = Path.GetFileName(romPath) ?? ""; } catch { name = romPath; }
        int dot = name.IndexOf('.');
        return (dot >= 0 ? name.Substring(0, dot) : name).Trim().ToLowerInvariant();
    }

    /// <summary>The EmuMovies key seen as a rom name (arcade keys are the MAME short name, maybe with a tag).</summary>
    private static string KeyStem(string key)
    {
        string k = StripTags(key).Trim();
        int dot = k.IndexOf('.');
        return (dot >= 0 ? k.Substring(0, dot) : k).Trim().ToLowerInvariant();
    }

    /// <summary>Region from a key's parenthetical tags: one known region → it, several → World, none → World.</summary>
    private static string RegionOf(string key)
    {
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(key, @"\(([^)]+)\)"))
            foreach (var part in m.Groups[1].Value.Split(','))
                if (EmuMoviesMaps.Region.TryGetValue(part.Trim(), out var r)) regions.Add(r);
        return regions.Count == 1 ? regions.First() : "World";
    }

    /// <summary>Junk keys GetMediaForPlatform returns (bare years, [n Players], keys ending in a media ext).</summary>
    public static bool IsParasiticKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        if (Regex.IsMatch(key, @"^\d{4}$")) return true;
        if (key.StartsWith('[') && key.EndsWith(']')) return true;
        if (Regex.IsMatch(key, @"\.(mp4|avi|mkv|mp3|wav)$", RegexOptions.IgnoreCase)) return true;
        return false;
    }
}

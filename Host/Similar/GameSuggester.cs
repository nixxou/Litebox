// Native LiteBox "Similar Games" rule ENGINE — the runtime half of the suggester feature whose CONFIG
// surface already lives in Host/Similar/GameSuggesterConfig.cs (SuggesterResolver / SuggesterStore /
// SuggesterConfig / CriteriaRecord / ComparisonType / FilterScope / SuggesterCategory).
//
// Clean-room LiteBox rewrite of ExtendDB's SimilarGames/{CandidateGame,SuggesterEngine,SuggesterRunner}.cs.
// NO plugin/reflection/Harmony, NO SQLite read-intercept dance: the DB-only candidate pool is read straight
// off the Extended DB via MetadataDb.ExtendedDbPath (Microsoft.Data.Sqlite, read-only) — the same door
// Host/Web/Db/DbRepository already uses.
//
// What it does
//   Given a target IGame + a resolved SuggesterConfig (from SuggesterResolver.Resolve, which honours the
//   double-registration: mirror LaunchBox's Settings.xml rules unless a category's switch says "use our own"),
//   score every candidate game and return the ranked matches. Candidates come from two pools, unioned:
//     • LOCAL : PluginHelper.DataManager.GetAllGames() — the user's library.
//     • DB    : the Extended DB Games table — every cloud game (only when a config opts in via AllowDbGames).
//
//   Three categories (Similar / Recommended / PossiblePorts) are evaluated in one shot over a shared pool by
//   GameSuggester.RunAll — the public entry point the web "Related" routes and a future desktop viewer call.
//
// Scoring model (ported verbatim from ExtendDB so results match LaunchBox/BigBox)
//   • Hard filters (null Weight) must all pass or the candidate is rejected outright.
//   • Scoring criteria (positive Weight) each add their weight when they match.
//   • Genre EqualTo gets a GRADED score (subset 100% / ≥half 50% / else 0, + a VNDB-tag bonus) when the
//     global GradedGenreScoring toggle is on; otherwise a LaunchBox-faithful binary subset match.
//   • IsSimilarTo = Sørensen–Dice over stop-word-filtered title/series token sets, threshold from Bonuses.
//   • Owned games get a small boost (round(score × 20%), capped at Bonuses.LocalLibraryBonusMax) — never
//     resurrecting a zero-score candidate.
//   • A candidate is INCLUDED iff it clears every hard filter AND score ≥ cfg.MinimumScore.
//
// DB-only pool cache — 3 layers, parity with ExtendDB's SimilarGames/CandidatePoolCache.cs:
//   L1 in-memory : the FULL pool, keyed (ExtendedDbPath + file mtime + shared SQL pre-filter tag).
//       CORRECTNESS invalidation is the mtime key alone (in LiteBox nothing writes the extended DB in
//       place — the downloader swaps the file). The plugin's two other triggers are about RAM, and are
//       kept: a 5-min idle TTL (reaper timer — ~200k normalised candidates are hundreds of MB) and an
//       immediate drop on game launch (MainWindow.OnGameStarted → ReleaseMemory — the mirror of the
//       plugin's GameLaunchHook.InvalidateInMemory) so the RAM goes to the game. The local-library dedupe
//       is applied per call, OUTSIDE the cache key, so the pool survives library changes.
//   L2 disk snapshot : Core\litebox\cache\suggester-dbpool.zst — zstd-compressed length-prefixed BINARY
//       (BinaryWriter) of the RAW rows, key embedded; a request after boot or after a TTL/launch drop
//       skips the SQL scan when the DB hasn't changed. Binary, not JSON, for decode speed — the plugin
//       used msgpack for the same reason; BinaryWriter matches it without the extra dependency. One file,
//       overwritten on rebuild (the plugin's per-key msgpack files were never swept — this can't
//       accumulate). RAW-only + renormalise-on-load, the same size/CPU trade the plugin chose.
//   L3 SQL rebuild : single SELECT over the Extended DB Games table with the shared ReleaseType pushdown
//       (a hard filter is pushed to SQL only when every AllowDbGames config carries the identical one).
//
// Everything here is assembly-internal (the whole LiteBox host is one internal assembly); "public API" in the
// task sense = the clean GameSuggester.RunAll / RunCategory entry points any in-assembly caller uses.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LbApiHost.Host.Media;
using Microsoft.Data.Sqlite;
using ZstdSharp;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Similar;

// ── Candidate POCO ──────────────────────────────────────────────────────────────────────────────────

/// <summary>Flat, pre-normalised representation of a game (local OR cloud-DB-only) that the engine scores.
/// Built once per run; read (candidate × criteria) times — field access must stay cheap, hence the
/// pre-lowercased copies + pre-tokenised title/series sets.</summary>
internal sealed class CandidateGame
{
    // Identity
    public string Id;         // IGame.Id for local; "db-{DatabaseID}" for DB-only.
    public int LbDbId;        // LaunchBox cloud-DB id; dedupes local↔DB and keys the media proxy.
    public bool IsLocal;

    // Raw (display + case-preserving)
    public string Title, Series, Developer, Publisher, Platform, PlayMode, Rating, ReleaseType, Notes, Storefront;
    public List<string> AlternateNames = new();
    public List<string> Genres = new();
    public List<string> VndbTags = new();
    public List<string> PlayModes = new();
    public int? MaxPlayers;
    public int? Year;
    public double? StarRating;           // user's
    public double? CommunityStarRating;  // global

    // Normalised (lower + trim)
    public string TitleNorm, SeriesNorm, DeveloperNorm, PublisherNorm, PlatformNorm,
                  PlayModeNorm, RatingNorm, ReleaseTypeNorm, NotesNorm, StorefrontNorm;
    public List<string> AlternateNamesNorm = new();
    public List<string> GenresNorm = new();
    public List<string> VndbTagsNorm = new();
    public List<string> PlayModesNorm = new();

    // Tokenised (for IsSimilarTo)
    public HashSet<string> TitleTokens;
    public HashSet<string> SeriesTokens;
}

// ── Public result DTOs ──────────────────────────────────────────────────────────────────────────────

/// <summary>One ranked suggestion.</summary>
internal sealed class SuggestionEntry
{
    public CandidateGame Cand;
    public int Score;
    public int Pct;   // score as 0..100 % of the config's max scoring weight (boost can clamp to 100).
}

/// <summary>Per-category ranked results.</summary>
internal sealed class CategorySuggestions
{
    public SuggesterCategory Category;
    public List<SuggestionEntry> Top = new();
}

// ── Public entry point ──────────────────────────────────────────────────────────────────────────────

/// <summary>The suggester's runtime facade. <see cref="RunAll"/> evaluates all three categories against a
/// target game over a shared candidate pool and returns the top matches per category — consumed by the web
/// "Related" routes (via Host/Web/Theme/RelatedProvider) and a future desktop "Similar Games" viewer.</summary>
internal static class GameSuggester
{
    /// <summary>Evaluate all three suggester categories against <paramref name="target"/> and return the top
    /// <paramref name="limit"/> matches for each. <paramref name="filter"/> (optional) is applied to BOTH pools
    /// before scoring — callers use it to gate parental rating / hidden platforms.</summary>
    public static List<CategorySuggestions> RunAll(IGame target, int limit,
        Func<CandidateGame, bool> filter = null)
    {
        var result = new List<CategorySuggestions>(3);
        if (target == null) return result;

        var tgt = CandidateProvider.FromIGame(target);
        if (tgt == null) return result;

        var configs = new[]
        {
            SuggesterResolver.Resolve(SuggesterCategory.SimilarGames),
            SuggesterResolver.Resolve(SuggesterCategory.RecommendedGames),
            SuggesterResolver.Resolve(SuggesterCategory.PossiblePorts),
        };

        // Max scoring weight per config (for the Pct normalisation).
        var maxWeights = new int[configs.Length];
        for (int i = 0; i < configs.Length; i++)
        {
            int m = 0;
            foreach (var c in configs[i].Criteria)
                if (!c.IsHardFilter && c.Weight.HasValue) m += c.Weight.Value;
            maxWeights[i] = m > 0 ? m : 1;
        }

        // Local pool — self-excluded by Id, filter applied immediately.
        var local = new List<CandidateGame>();
        var localLbDbIds = new HashSet<int>();
        IGame[] allLocal;
        try { allLocal = PluginHelper.DataManager.GetAllGames() ?? Array.Empty<IGame>(); }
        catch { allLocal = Array.Empty<IGame>(); }
        foreach (var g in allLocal)
        {
            if (g == null) continue;
            if (string.Equals(g.Id, tgt.Id, StringComparison.Ordinal)) continue;
            var c = CandidateProvider.FromIGame(g);
            if (c == null) continue;
            if (filter != null && !filter(c)) continue;
            local.Add(c);
            if (c.LbDbId > 0) localLbDbIds.Add(c.LbDbId);
        }

        // DB-only pool — built (and cached) only when at least one config opts in. Shared across the 3.
        bool needDb = false;
        foreach (var c in configs) if (c.AllowDbGames) { needDb = true; break; }
        List<CandidateGame> dbPool = null;
        if (needDb)
        {
            dbPool = CandidateProvider.GetDbOnlyPool(configs, localLbDbIds);
            if (filter != null && dbPool != null)
            {
                var f = new List<CandidateGame>(dbPool.Count);
                foreach (var c in dbPool) if (filter(c)) f.Add(c);
                dbPool = f;
            }
        }

        for (int i = 0; i < configs.Length; i++)
        {
            var cfg = configs[i];
            int maxW = maxWeights[i];
            var cat = new CategorySuggestions { Category = cfg.Category };

            var all = new List<(CandidateGame c, int score)>(local.Count + (dbPool?.Count ?? 0));
            foreach (var cand in local)
            {
                var r = SuggesterEngine.Evaluate(tgt, cand, cfg);
                if (!r.rejected && r.score >= cfg.MinimumScore) all.Add((cand, r.score));
            }
            if (cfg.AllowDbGames && dbPool != null)
                foreach (var cand in dbPool)
                {
                    var r = SuggesterEngine.Evaluate(tgt, cand, cfg);
                    if (!r.rejected && r.score >= cfg.MinimumScore) all.Add((cand, r.score));
                }

            all.Sort((a, b) =>
            {
                int c = b.score.CompareTo(a.score);
                return c != 0 ? c : string.Compare(a.c.Title, b.c.Title, StringComparison.OrdinalIgnoreCase);
            });

            int take = Math.Min(Math.Max(1, limit), all.Count);
            for (int k = 0; k < take; k++)
            {
                var (cand, score) = all[k];
                int pct = (int)Math.Round(100.0 * score / maxW, MidpointRounding.ToZero);
                if (pct > 100) pct = 100; if (pct < 0) pct = 0;
                cat.Top.Add(new SuggestionEntry { Cand = cand, Score = score, Pct = pct });
            }
            result.Add(cat);
        }
        return result;
    }

    /// <summary>Convenience single-category run (desktop viewer tabs). Reuses <see cref="RunAll"/> and picks
    /// out the requested category.</summary>
    public static CategorySuggestions RunCategory(IGame target, SuggesterCategory category, int limit,
        Func<CandidateGame, bool> filter = null)
    {
        foreach (var r in RunAll(target, limit, filter))
            if (r.Category == category) return r;
        return new CategorySuggestions { Category = category };
    }
}

// ── Engine ──────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Stateless comparison logic. <see cref="Evaluate"/> returns whether a hard filter rejected the
/// candidate and the summed scoring weight. Ported from ExtendDB's SuggesterEngine (trace/report path
/// dropped — the runtime gap only needs the score).</summary>
internal static class SuggesterEngine
{
    private const double DefaultSimilarityThreshold = 0.4;
    private const double LocalBoostPct = 0.20;

    public static (bool rejected, int score) Evaluate(CandidateGame target, CandidateGame cand, SuggesterConfig cfg)
    {
        var bonus = SuggesterStore.Instance.Bonuses;
        bool rejected = false;
        int score = 0;

        foreach (var crit in cfg.Criteria)
        {
            if (crit.FilterType == FilterScope.LocalGamesOnly && !cand.IsLocal) continue;
            if (crit.FilterType == FilterScope.DatabaseGamesOnly && cand.IsLocal) continue;

            // Graded Genre EqualTo scoring path.
            if (bonus.GradedGenreScoring && !crit.IsHardFilter
                && crit.ComparisonTypeKey == ComparisonType.EqualTo && crit.FieldKey == "Genre")
            {
                score += ComputeGenreScore(target, cand, crit);
                continue;
            }

            bool passed = EvaluateCriterion(target, cand, crit);
            if (crit.IsHardFilter)
            {
                if (!passed) { rejected = true; return (true, 0); } // fail-fast
            }
            else if (passed)
            {
                score += crit.Weight ?? 0;
            }
        }

        // Owned-games boost.
        int localCap = bonus.LocalLibraryBonusEnabled ? bonus.LocalLibraryBonusMax : 0;
        if (!rejected && cand.IsLocal && score > 0 && localCap > 0)
        {
            int boost = (int)Math.Round(score * LocalBoostPct, MidpointRounding.AwayFromZero);
            if (boost > localCap) boost = localCap;
            if (boost > 0) score += boost;
        }

        return (rejected, score);
    }

    // ── Genre graded scoring (subset + partial + VNDB bonus) ──
    private static int ComputeGenreScore(CandidateGame target, CandidateGame cand, CriteriaRecord crit)
    {
        int weight = crit.Weight ?? 0;
        var tGenres = target.GenresNorm;
        var cGenres = cand.GenresNorm;

        int targetInCand = 0;
        if (tGenres != null && cGenres != null)
            foreach (var g in tGenres) if (cGenres.Contains(g)) targetInCand++;

        int tCount = tGenres?.Count ?? 0;
        double genrePct;
        if (tCount == 0 || targetInCand == 0)      genrePct = 0.0;
        else if (targetInCand == tCount)           genrePct = 1.0;
        else if (targetInCand * 2 >= tCount)       genrePct = 0.5;
        else                                       genrePct = 0.0;

        int vndbShared = 0;
        if (target.VndbTagsNorm != null && cand.VndbTagsNorm != null)
            foreach (var v in target.VndbTagsNorm) if (cand.VndbTagsNorm.Contains(v)) vndbShared++;

        double vndbPct = vndbShared >= 3 ? 1.0 : (vndbShared >= 1 ? 0.5 : 0.0);
        return (int)Math.Round((genrePct + vndbPct) * weight, MidpointRounding.ToZero);
    }

    // ── Per-criterion dispatch ──
    private static bool EvaluateCriterion(CandidateGame target, CandidateGame cand, CriteriaRecord crit)
    {
        var op = crit.ComparisonTypeKey;
        if (op == ComparisonType.Unknown) return false;

        if (op >= ComparisonType.IsAmazon && op <= ComparisonType.IsMicrosoft)
            return cand.StorefrontNorm == StorefrontFromOp(op);

        var candVal = GetField(cand, crit.FieldKey);
        var cmpVal = crit.UseGameValue ? GetField(target, crit.FieldKey)
                                       : FieldValue.OfString(crit.ComparisonValue);
        if (candVal.Kind == FieldKind.Missing || cmpVal.Kind == FieldKind.Missing) return false;

        return ApplyOp(op, candVal, cmpVal, cand, target, crit);
    }

    // ── Field accessor ──
    private enum FieldKind { Missing, String, StringList, Number }

    private readonly struct FieldValue
    {
        public readonly FieldKind Kind;
        public readonly string Str;          // normalised
        public readonly List<string> List;   // normalised list
        public readonly double Number;

        private FieldValue(FieldKind k, string s, List<string> l, double n) { Kind = k; Str = s; List = l; Number = n; }

        public static FieldValue Missing => new(FieldKind.Missing, "", null, 0);
        public static FieldValue OfString(string raw) => new(FieldKind.String, (raw ?? "").Trim().ToLowerInvariant(), null, 0);
        public static FieldValue OfStr(string norm) => new(FieldKind.String, norm ?? "", null, 0);
        public static FieldValue OfList(List<string> norm) => new(FieldKind.StringList, "", norm ?? new(), 0);
        public static FieldValue OfNum(double n) => new(FieldKind.Number, "", null, n);
    }

    private static FieldValue GetField(CandidateGame c, string key)
    {
        switch (key)
        {
            case "Title":         return FieldValue.OfStr(c.TitleNorm);
            case "AlternateName": return FieldValue.OfList(c.AlternateNamesNorm);
            case "Series":        return FieldValue.OfStr(c.SeriesNorm);
            case "Genre":         return FieldValue.OfList(c.GenresNorm);
            case "PlayMode":      return FieldValue.OfList(c.PlayModesNorm);
            case "MaxPlayers":    return c.MaxPlayers.HasValue ? FieldValue.OfNum(c.MaxPlayers.Value) : FieldValue.OfStr("");
            case "Platform":      return FieldValue.OfStr(c.PlatformNorm);
            case "Rating":        return FieldValue.OfStr(c.RatingNorm);
            case "Developer":     return FieldValue.OfStr(c.DeveloperNorm);
            case "Publisher":     return FieldValue.OfStr(c.PublisherNorm);
            case "Notes":         return FieldValue.OfStr(c.NotesNorm);
            case "ReleaseType":   return FieldValue.OfStr(c.ReleaseTypeNorm);
            case "Storefront":    return FieldValue.OfStr(c.StorefrontNorm);
            case "StarRating":
            {
                var v = c.StarRating ?? c.CommunityStarRating;
                return v.HasValue ? FieldValue.OfNum(v.Value) : FieldValue.OfStr("");
            }
            default: return FieldValue.Missing;
        }
    }

    // ── Comparison ops ──
    private static bool ApplyOp(ComparisonType op, FieldValue cand, FieldValue cmp,
        CandidateGame candGame, CandidateGame target, CriteriaRecord crit)
    {
        if (cand.Kind == FieldKind.StringList || cmp.Kind == FieldKind.StringList)
            return ApplyOpList(op, cand, cmp, candGame, target, crit);

        switch (op)
        {
            case ComparisonType.EqualTo:
                if (cand.Kind == FieldKind.Number || cmp.Kind == FieldKind.Number) return CompareNumber(cand, cmp) == 0;
                return !string.IsNullOrEmpty(cand.Str) && cand.Str == cmp.Str;
            case ComparisonType.NotEqualTo:
                if (cand.Kind == FieldKind.Number || cmp.Kind == FieldKind.Number) return CompareNumber(cand, cmp) != 0;
                return cand.Str != cmp.Str;
            case ComparisonType.Contains:
                return !string.IsNullOrEmpty(cand.Str) && !string.IsNullOrEmpty(cmp.Str) && cand.Str.Contains(cmp.Str, StringComparison.Ordinal);
            case ComparisonType.NotContains:
                return string.IsNullOrEmpty(cmp.Str) || !cand.Str.Contains(cmp.Str, StringComparison.Ordinal);
            case ComparisonType.IsEmpty:
                return string.IsNullOrEmpty(cand.Str) && cand.Kind != FieldKind.Number;
            case ComparisonType.IsNotEmpty:
                return cand.Kind == FieldKind.Number || !string.IsNullOrEmpty(cand.Str);
            case ComparisonType.StartsWith:
                return !string.IsNullOrEmpty(cand.Str) && cand.Str.StartsWith(cmp.Str, StringComparison.Ordinal);
            case ComparisonType.StartsWithNone:
                return string.IsNullOrEmpty(cmp.Str) || !cand.Str.StartsWith(cmp.Str, StringComparison.Ordinal);
            case ComparisonType.IsSimilarTo:    return JaccardSimilar(target, candGame, crit.FieldKey);
            case ComparisonType.IsNotSimilarTo: return !JaccardSimilar(target, candGame, crit.FieldKey);
            case ComparisonType.GreaterThan:    return CompareNumber(cand, cmp) > 0;
            case ComparisonType.LessThan:       return CompareNumber(cand, cmp) < 0;
            case ComparisonType.AtLeastOneOf:
            case ComparisonType.ContainsAnyValue: return AnyOf(cand.Str, cmp.Str);
            case ComparisonType.NoneOf:
            case ComparisonType.ContainsNoValue:  return !AnyOf(cand.Str, cmp.Str);
            default: return false;
        }
    }

    private static bool ApplyOpList(ComparisonType op, FieldValue cand, FieldValue cmp,
        CandidateGame candGame, CandidateGame target, CriteriaRecord crit)
    {
        List<string> cmpList = cmp.Kind == FieldKind.StringList ? cmp.List
            : (string.IsNullOrEmpty(cmp.Str) ? new List<string>() : new List<string> { cmp.Str });
        List<string> candList = cand.Kind == FieldKind.StringList ? cand.List
            : (string.IsNullOrEmpty(cand.Str) ? new List<string>() : new List<string> { cand.Str });

        switch (op)
        {
            case ComparisonType.IsEmpty:    return candList.Count == 0;
            case ComparisonType.IsNotEmpty: return candList.Count > 0;

            case ComparisonType.EqualTo:
                if (crit.FieldKey == "Genre")
                {
                    if (cmpList.Count == 0) return false;
                    foreach (var b in cmpList)
                    {
                        bool found = false;
                        foreach (var a in candList) if (a == b) { found = true; break; }
                        if (!found) return false;
                    }
                    return true;
                }
                foreach (var a in candList) foreach (var b in cmpList) if (a == b) return true;
                return false;

            case ComparisonType.NotEqualTo:
                foreach (var a in candList) foreach (var b in cmpList) if (a == b) return false;
                return true;

            case ComparisonType.Contains:
                foreach (var a in candList) foreach (var b in cmpList)
                    if (!string.IsNullOrEmpty(b) && a.Contains(b, StringComparison.Ordinal)) return true;
                return false;

            case ComparisonType.NotContains:
                foreach (var a in candList) foreach (var b in cmpList)
                    if (!string.IsNullOrEmpty(b) && a.Contains(b, StringComparison.Ordinal)) return false;
                return true;

            case ComparisonType.IsSimilarTo:    return JaccardSimilarList(target, candGame, crit.FieldKey);
            case ComparisonType.IsNotSimilarTo: return !JaccardSimilarList(target, candGame, crit.FieldKey);
            default: return false;
        }
    }

    private static int CompareNumber(FieldValue a, FieldValue b)
    {
        if (a.Kind != FieldKind.Number && !double.TryParse(a.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return -1;
        if (b.Kind != FieldKind.Number && !double.TryParse(b.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return 1;
        double na = a.Kind == FieldKind.Number ? a.Number : double.Parse(a.Str, NumberStyles.Float, CultureInfo.InvariantCulture);
        double nb = b.Kind == FieldKind.Number ? b.Number : double.Parse(b.Str, NumberStyles.Float, CultureInfo.InvariantCulture);
        return na.CompareTo(nb);
    }

    // ── Similarity ──
    private static bool JaccardSimilar(CandidateGame target, CandidateGame cand, string fieldKey)
    {
        var (a, b) = TokensForField(target, cand, fieldKey);
        return DiceSimilar(a, b);
    }

    private static bool DiceSimilar(HashSet<string> a, HashSet<string> b)
    {
        if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;
        int intersect = 0;
        foreach (var t in a) if (b.Contains(t)) intersect++;
        if (intersect == 0) return false;
        double threshold = SuggesterStore.Instance.Bonuses.SimilarityThreshold;
        if (threshold <= 0.0 || threshold > 1.0) threshold = DefaultSimilarityThreshold;
        return (2.0 * intersect) / (a.Count + b.Count) >= threshold;
    }

    private static bool JaccardSimilarList(CandidateGame target, CandidateGame cand, string fieldKey)
    {
        if (fieldKey == "AlternateName")
        {
            var candBags = new List<HashSet<string>>(cand.AlternateNames.Count + 1);
            if (cand.TitleTokens.Count > 0) candBags.Add(cand.TitleTokens);
            foreach (var an in cand.AlternateNames) { var t = CandidateProvider.Tokens(an); if (t.Count > 0) candBags.Add(t); }
            var targetBags = new List<HashSet<string>>(target.AlternateNames.Count + 1);
            if (target.TitleTokens.Count > 0) targetBags.Add(target.TitleTokens);
            foreach (var an in target.AlternateNames) { var t = CandidateProvider.Tokens(an); if (t.Count > 0) targetBags.Add(t); }
            foreach (var a in targetBags) foreach (var b in candBags) if (DiceSimilar(a, b)) return true;
            return false;
        }
        return JaccardSimilar(target, cand, fieldKey);
    }

    private static (HashSet<string>, HashSet<string>) TokensForField(CandidateGame target, CandidateGame cand, string fieldKey)
        => fieldKey switch
        {
            "Title"  => (target.TitleTokens,  cand.TitleTokens),
            "Series" => (target.SeriesTokens, cand.SeriesTokens),
            _        => (null, null),
        };

    private static bool AnyOf(string candNorm, string cmpNorm)
    {
        if (string.IsNullOrEmpty(candNorm) || string.IsNullOrEmpty(cmpNorm)) return false;
        foreach (var tok in cmpNorm.Split(','))
        {
            var t = tok.Trim();
            if (t.Length > 0 && candNorm == t) return true;
        }
        return false;
    }

    private static string StorefrontFromOp(ComparisonType op) => op switch
    {
        ComparisonType.IsAmazon    => "amazon",
        ComparisonType.IsSteam     => "steam",
        ComparisonType.IsGog       => "gog",
        ComparisonType.IsEpic      => "epic",
        ComparisonType.IsEa        => "ea",
        ComparisonType.IsUbisoft   => "ubisoft",
        ComparisonType.IsMicrosoft => "microsoft",
        _ => "",
    };
}

// ── Candidate provider (local IGame → POCO ; Extended DB → POCO pool) ─────────────────────────────────

internal static class CandidateProvider
{
    private static readonly Dictionary<string, string> StorefrontMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Amazon Games", "Amazon" }, { "Amazon", "Amazon" },
        { "Steam", "Steam" },
        { "GOG", "GOG" }, { "GOG.com", "GOG" },
        { "Epic Games Store", "Epic" }, { "Epic Games", "Epic" }, { "Epic", "Epic" },
        { "EA", "EA" }, { "EA App", "EA" }, { "EA Play", "EA" }, { "Origin", "EA" },
        { "Uplay", "Ubisoft" }, { "Ubisoft Connect", "Ubisoft" }, { "Ubisoft", "Ubisoft" },
        { "Microsoft Store", "Microsoft" }, { "Xbox", "Microsoft" }, { "Xbox Game Pass", "Microsoft" },
    };

    public static CandidateGame FromIGame(IGame g)
    {
        if (g == null) return null;
        try
        {
            var c = new CandidateGame
            {
                Id          = Safe(() => g.Id) ?? "",
                LbDbId      = Safe(() => g.LaunchBoxDbId) ?? 0,
                IsLocal     = true,
                Title       = Safe(() => g.Title) ?? "",
                Series      = Safe(() => g.Series) ?? "",
                Developer   = Safe(() => g.Developer) ?? "",
                Publisher   = Safe(() => g.Publisher) ?? "",
                Platform    = Safe(() => g.Platform) ?? "",
                PlayMode    = Safe(() => g.PlayMode) ?? "",
                Rating      = Safe(() => g.Rating) ?? "",
                ReleaseType = Safe(() => g.ReleaseType) ?? "",
                Notes       = Safe(() => g.Notes) ?? "",
                Storefront  = MapStorefront(Safe(() => g.Source)),
                MaxPlayers  = PosInt(Safe(() => (int?)g.MaxPlayers)),
                StarRating  = PosDouble(Safe(() => (double?)g.StarRating)),
                CommunityStarRating = PosDouble(Safe(() => (double?)g.CommunityStarRating)),
                Year        = EffYear(g),
            };

            c.AlternateNames = new List<string>();
            var ans = Safe(() => g.GetAllAlternateNames());
            if (ans != null)
                foreach (var an in ans)
                {
                    var n = Safe(() => an?.Name);
                    if (!string.IsNullOrWhiteSpace(n)) c.AlternateNames.Add(n);
                }

            c.Genres = SplitList(Safe(() => g.GenresString));

            Normalise(c);
            return c;
        }
        catch { return null; }
    }

    // ── DB-only pool (3 layers: in-memory ⇄ disk snapshot ⇄ SQL rebuild — see file header) ──

    private static readonly object _dbLock = new();
    private static string _dbCacheKey;
    private static List<CandidateGame> _dbCachePool;
    private static DateTime _dbCacheLastHit;                 // guarded by _dbLock
    private static System.Threading.Timer _dbCacheReaper;    // idle-TTL sweep (armed while a pool is held)
    private static readonly TimeSpan InMemTtl = TimeSpan.FromMinutes(5);

    /// <summary>Drop the in-memory pool NOW and hand the RAM back (game launch, or the idle reaper).
    /// Purely a memory measure — the disk snapshot makes the next request cheap.</summary>
    public static void ReleaseMemory()
    {
        lock (_dbLock)
        {
            _dbCachePool = null;
            _dbCacheKey = null;
            try { _dbCacheReaper?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
        }
    }

    /// <summary>(Re)arm the idle sweep — runs once a minute while a pool is cached.</summary>
    private static void ArmReaper()
    {
        _dbCacheLastHit = DateTime.UtcNow;
        if (_dbCacheReaper == null)
            _dbCacheReaper = new System.Threading.Timer(_ =>
            {
                lock (_dbLock)
                {
                    if (_dbCachePool != null && DateTime.UtcNow - _dbCacheLastHit > InMemTtl)
                    {
                        _dbCachePool = null;
                        _dbCacheKey = null;
                        try { _dbCacheReaper.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                    }
                }
            }, null, 60_000, 60_000);
        else
            try { _dbCacheReaper.Change(60_000, 60_000); } catch { }
    }

    /// <summary>Extended-DB candidate pool (cloud games), deduped against the local library's cloud ids.
    /// The FULL pool is cached (keyed on DB path + mtime + the shared SQL pre-filter); the caller-specific
    /// dedupe is applied per call so the cache survives library changes.</summary>
    public static List<CandidateGame> GetDbOnlyPool(SuggesterConfig[] configs, HashSet<int> excludeLbDbIds)
    {
        string where = SharedReleaseTypeFilter(configs, out string filterTag);
        List<CandidateGame> full;
        lock (_dbLock)
        {
            string path = MetadataDb.ExtendedDbPath;
            // The overview expression is part of the SELECT (HasOverview flag), so it's part of the key.
            // On the defaultOverview-column path a change rebuilds the DB (→ new mtime) anyway; but on the
            // dynamic-COALESCE path a [Base] OverviewSources reorder changes the expression WITHOUT touching
            // the file — without this, the snapshot would serve stale HasOverview under an unchanged key.
            // (The source DB itself needs no extra discriminant: the FULL path is in the key, and the pool
            // only ever reads the Extended DB — a future native-DB fallback would differ by path too.)
            string ovSig = path != null ? Data.OverviewCache.ReadExpression(path) : "";
            string key = BuildCacheKey(path, filterTag + "|" + ovSig);
            if (key != null && key == _dbCacheKey && _dbCachePool != null)
            {
                full = _dbCachePool;
            }
            else
            {
                var rows = key != null ? LoadPoolSnapshot(key) : null;   // L2: warm start off disk
                if (rows == null)
                {
                    rows = LoadDbOnlyRows(where);                        // L3: SQL rebuild
                    if (key != null && rows.Count > 0) SavePoolSnapshot(key, rows);
                }
                full = Materialize(rows);
                _dbCacheKey = key;
                _dbCachePool = full;
            }
            ArmReaper();   // stamp the hit + keep the idle sweep alive while the pool is held
        }

        if (excludeLbDbIds == null || excludeLbDbIds.Count == 0) return full;
        var outp = new List<CandidateGame>(full.Count);
        foreach (var c in full) if (c.LbDbId <= 0 || !excludeLbDbIds.Contains(c.LbDbId)) outp.Add(c);
        return outp;
    }

    private static string BuildCacheKey(string path, string filterTag)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + filterTag; }
        catch { return null; }
    }

    /// <summary>If every AllowDbGames config carries the SAME DB-scoped hard filter
    /// <c>ReleaseType EqualTo &lt;literal&gt;</c>, push it down to SQL to shrink the pool (the LB-default
    /// configs all filter ReleaseType='Released'). Otherwise no WHERE.</summary>
    private static string SharedReleaseTypeFilter(SuggesterConfig[] configs, out string tag)
    {
        tag = "";
        string common = null;
        foreach (var cfg in configs)
        {
            if (!cfg.AllowDbGames) continue;
            string here = null;
            foreach (var crit in cfg.Criteria)
            {
                if (crit.IsHardFilter && !crit.UseGameValue
                    && crit.ComparisonTypeKey == ComparisonType.EqualTo
                    && crit.FieldKey == "ReleaseType"
                    && crit.FilterType != FilterScope.LocalGamesOnly
                    && !string.IsNullOrEmpty(crit.ComparisonValue))
                { here = crit.ComparisonValue; break; }
            }
            if (here == null) return null;                 // a DB config without the filter → can't push down
            if (common == null) common = here;
            else if (!string.Equals(common, here, StringComparison.OrdinalIgnoreCase)) return null;
        }
        if (common == null) return null;
        tag = "rt=" + common;
        return common;
    }

    // ── Snapshot rows (L2/L3 interchange format) ──
    // RAW row exactly as read from SQL, before normalisation. Normalise() runs at Materialize time in
    // both paths, so a snapshot load and an SQL rebuild produce identical pools.

    private sealed class PoolRow
    {
        public int Id;              // DatabaseID
        public string Ti;           // Name
        public string Pl;           // Platform
        public List<string> Ge;     // Genres (split, pre-normalise)
        public string Es;           // ESRB
        public int? Mx;             // MaxPlayers
        public string Rt;           // ReleaseType
        public string De;           // Developer
        public string Pu;           // Publisher
        public double? Cr;          // CommunityRating
        public int? Yr;             // ReleaseYear
        public bool Ov;             // has a non-empty resolved overview
    }

    // Length-prefixed binary under zstd: magic + version + key + count + rows. BinaryWriter strings are
    // UTF-8 length-prefixed; nullables are a presence byte + value. Version bump = format change; a
    // mismatched magic/version/key just falls back to the SQL rebuild.
    private const uint SnapshotMagic = 0x4C425350;   // "PSBL" little-endian — LiteBox suggester pool
    private const byte SnapshotVersion = 1;
    private const int SnapshotMaxRows = 1_000_000;   // sanity bound against a corrupt count

    private static string SnapshotPath => Path.Combine(LiteBoxPaths.Dir("cache"), "suggester-dbpool.zst");

    /// <summary>The disk snapshot's rows when its embedded key matches <paramref name="key"/>, else null
    /// (missing, stale — the DB was swapped or the pre-filter changed — or unreadable/corrupt).</summary>
    private static List<PoolRow> LoadPoolSnapshot(string key)
    {
        try
        {
            string path = SnapshotPath;
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var z = new DecompressionStream(fs);
            using var r = new BinaryReader(z, Encoding.UTF8);

            if (r.ReadUInt32() != SnapshotMagic || r.ReadByte() != SnapshotVersion) return null;
            if (!string.Equals(r.ReadString(), key, StringComparison.Ordinal)) return null;

            int count = r.ReadInt32();
            if (count < 0 || count > SnapshotMaxRows) return null;

            var rows = new List<PoolRow>(count);
            for (int i = 0; i < count; i++)
            {
                var w = new PoolRow
                {
                    Id = r.ReadInt32(),
                    Ti = r.ReadString(),
                    Pl = r.ReadString(),
                    Es = r.ReadString(),
                    Rt = r.ReadString(),
                    De = r.ReadString(),
                    Pu = r.ReadString(),
                };
                int ng = r.ReadInt32();
                if (ng < 0 || ng > 512) return null;
                w.Ge = new List<string>(ng);
                for (int j = 0; j < ng; j++) w.Ge.Add(r.ReadString());
                w.Mx = r.ReadBoolean() ? r.ReadInt32() : (int?)null;
                w.Cr = r.ReadBoolean() ? r.ReadDouble() : (double?)null;
                w.Yr = r.ReadBoolean() ? r.ReadInt32() : (int?)null;
                w.Ov = r.ReadBoolean();
                rows.Add(w);
            }
            return rows;
        }
        catch { return null; }
    }

    /// <summary>Persist the raw rows for the next process (temp + move so a crash can't leave a torn file).</summary>
    private static void SavePoolSnapshot(string key, List<PoolRow> rows)
    {
        string tmp = SnapshotPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var z = new CompressionStream(fs))
            using (var w = new BinaryWriter(z, Encoding.UTF8))
            {
                w.Write(SnapshotMagic);
                w.Write(SnapshotVersion);
                w.Write(key);
                w.Write(rows.Count);
                foreach (var row in rows)
                {
                    w.Write(row.Id);
                    w.Write(row.Ti ?? "");
                    w.Write(row.Pl ?? "");
                    w.Write(row.Es ?? "");
                    w.Write(row.Rt ?? "");
                    w.Write(row.De ?? "");
                    w.Write(row.Pu ?? "");
                    var ge = row.Ge;
                    w.Write(ge?.Count ?? 0);
                    if (ge != null) foreach (var g in ge) w.Write(g ?? "");
                    w.Write(row.Mx.HasValue); if (row.Mx.HasValue) w.Write(row.Mx.Value);
                    w.Write(row.Cr.HasValue); if (row.Cr.HasValue) w.Write(row.Cr.Value);
                    w.Write(row.Yr.HasValue); if (row.Yr.HasValue) w.Write(row.Yr.Value);
                    w.Write(row.Ov);
                }
            }
            File.Move(tmp, SnapshotPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Raw rows → scored-pool candidates (normalise + tokenise), same for both cache paths.</summary>
    private static List<CandidateGame> Materialize(List<PoolRow> rows)
    {
        var list = new List<CandidateGame>(rows.Count);
        foreach (var w in rows)
        {
            var c = new CandidateGame
            {
                LbDbId      = w.Id,
                Id          = "db-" + w.Id,
                IsLocal     = false,
                Title       = w.Ti ?? "",
                Platform    = w.Pl ?? "",
                Rating      = w.Es ?? "",
                MaxPlayers  = w.Mx,
                ReleaseType = w.Rt ?? "",
                Developer   = w.De ?? "",
                Publisher   = w.Pu ?? "",
                CommunityStarRating = w.Cr,
                Year        = w.Yr,
                Series = "", PlayMode = "", Storefront = "",
                AlternateNames = new List<string>(),
                Genres = new List<string>(w.Ge ?? new List<string>()),   // copy: Normalise consumes it
            };
            c.Notes = w.Ov ? c.Title : "";
            Normalise(c);
            list.Add(c);
        }
        return list;
    }

    private static List<PoolRow> LoadDbOnlyRows(string releaseTypeEquals, int cap = 200000)
    {
        var rows = new List<PoolRow>();
        string dbPath = MetadataDb.ExtendedDbPath;
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return rows;

        try { SQLitePCL.Batteries.Init(); } catch { }

        var ovExpr = Data.OverviewCache.ReadExpression(dbPath);   // priority-resolved description (cache-aware)
        string sql =
            "SELECT DatabaseID, Name, Platform, Genres, ESRB, MaxPlayers, ReleaseType, " +
            "Developer, Publisher, CommunityRating, ReleaseYear, " +
            $"({ovExpr} IS NOT NULL AND {ovExpr} != '') AS HasOverview FROM Games";
        if (!string.IsNullOrEmpty(releaseTypeEquals)) sql += " WHERE ReleaseType = $rt";

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Cache = SqliteCacheMode.Private,
            };
            using var con = new SqliteConnection(csb.ToString());
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            if (!string.IsNullOrEmpty(releaseTypeEquals)) cmd.Parameters.AddWithValue("$rt", releaseTypeEquals);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new PoolRow
                {
                    Id = r.IsDBNull(0) ? 0 : r.GetInt32(0),
                    Ti = r.IsDBNull(1) ? "" : r.GetString(1),
                    Pl = r.IsDBNull(2) ? "" : r.GetString(2),
                    Ge = SplitList(r.IsDBNull(3) ? "" : r.GetString(3)),
                    Es = r.IsDBNull(4) ? "" : r.GetString(4),
                    Mx = r.IsDBNull(5) ? (int?)null : r.GetInt32(5),
                    Rt = r.IsDBNull(6) ? "" : r.GetString(6),
                    De = r.IsDBNull(7) ? "" : r.GetString(7),
                    Pu = r.IsDBNull(8) ? "" : r.GetString(8),
                    Cr = r.IsDBNull(9) ? (double?)null : r.GetDouble(9),
                    Yr = r.IsDBNull(10) ? (int?)null : r.GetInt32(10),
                    Ov = !r.IsDBNull(11) && r.GetInt64(11) != 0,
                });
                if (rows.Count >= cap) break;
            }
        }
        catch { /* Extended DB unreadable / older schema → whatever we managed to read */ }
        return rows;
    }

    // ── Normalise + tokenise ──

    private static readonly char[] _listSeps = { ';', ',' };

    private static void Normalise(CandidateGame c)
    {
        c.TitleNorm = Norm(c.Title); c.SeriesNorm = Norm(c.Series);
        c.DeveloperNorm = Norm(c.Developer); c.PublisherNorm = Norm(c.Publisher);
        c.PlatformNorm = Norm(c.Platform); c.PlayModeNorm = Norm(c.PlayMode);
        c.RatingNorm = Norm(c.Rating); c.ReleaseTypeNorm = Norm(c.ReleaseType);
        c.NotesNorm = Norm(c.Notes); c.StorefrontNorm = Norm(c.Storefront);

        var rawGenres = ExpandSeparated(c.Genres ?? new List<string>());
        c.PlayModes = ExpandSeparated(SplitOne(c.PlayMode));

        c.Genres = new List<string>(rawGenres.Count);
        c.VndbTags = new List<string>();
        foreach (var g in rawGenres)
        {
            if (!string.IsNullOrEmpty(g) && g.StartsWith("vndb", StringComparison.OrdinalIgnoreCase)) c.VndbTags.Add(g);
            else c.Genres.Add(g);
        }

        c.AlternateNamesNorm = new List<string>(c.AlternateNames.Count);
        foreach (var a in c.AlternateNames) c.AlternateNamesNorm.Add(Norm(a));
        c.GenresNorm = new List<string>(c.Genres.Count);
        foreach (var g in c.Genres) c.GenresNorm.Add(Norm(g));
        c.VndbTagsNorm = new List<string>(c.VndbTags.Count);
        foreach (var v in c.VndbTags) c.VndbTagsNorm.Add(Norm(v));
        c.PlayModesNorm = new List<string>(c.PlayModes.Count);
        foreach (var p in c.PlayModes) c.PlayModesNorm.Add(Norm(p));

        c.TitleTokens = Tokens(c.Title);
        c.SeriesTokens = Tokens(c.Series);
    }

    private static List<string> SplitOne(string s)
        => string.IsNullOrEmpty(s) ? new List<string>() : new List<string> { s };

    private static List<string> SplitList(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var p in s.Split(_listSeps, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    private static List<string> ExpandSeparated(List<string> src)
    {
        var result = new List<string>(src.Count);
        foreach (var item in src)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            if (item.IndexOfAny(_listSeps) < 0) { result.Add(item.Trim()); continue; }
            foreach (var p in item.Split(_listSeps, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                if (t.Length > 0) result.Add(t);
            }
        }
        return result;
    }

    public static string Norm(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();

    private static readonly HashSet<string> _stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the",
        "of", "in", "on", "at", "by", "for", "with", "from", "to",
        "into", "onto", "upon", "over", "under", "between", "through",
        "against", "before", "after", "during", "around", "about",
        "across", "behind", "below", "above", "near", "since", "while",
        "and", "or", "but", "nor", "so", "yet", "vs", "versus",
        "this", "that", "these", "those",
        "my", "your", "his", "her", "its", "our", "their",
        "me", "you", "him", "us", "we", "they", "them",
        "is", "are", "was", "were", "be", "been", "being", "am",
        "do", "does", "did", "has", "have", "had",
        "will", "would", "can", "could", "should",
        "le", "la", "les", "l", "un", "une", "des", "du", "de", "d",
        "à", "au", "aux", "en", "dans", "sur", "sous", "par", "pour",
        "sans", "avec", "contre", "vers", "entre", "chez",
        "devant", "derrière", "près", "après", "avant", "depuis",
        "et", "ou", "ni", "mais", "donc", "car",
        "ce", "cet", "cette", "ces",
        "mon", "ma", "mes", "ton", "ta", "tes", "son", "sa", "ses",
        "notre", "votre", "leur", "leurs", "nos", "vos",
        "je", "tu", "il", "elle", "nous", "vous", "ils", "elles",
        "est", "sont", "été", "être", "ont", "avait", "avaient",
        "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix",
        "x", "xi", "xii", "xiii", "xiv", "xv",
        "xvi", "xvii", "xviii", "xix", "xx",
        "edition", "editions", "complete", "deluxe", "special",
        "collector", "collectors", "anniversary", "definitive",
        "remastered", "remaster", "hd", "uhd", "4k",
        "gold", "platinum", "ultimate", "premium", "enhanced",
        "redux", "expanded", "director", "directors", "cut",
        "extended", "uncut", "uncensored", "goty", "rerelease",
        "édition", "intégrale", "complète", "spéciale", "ultime",
        "anniversaire", "version", "tome", "volume", "vol",
    };

    public static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(s)) return set;
        var buf = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (char.IsLetterOrDigit(ch)) buf.Append(char.ToLowerInvariant(ch));
            else if (buf.Length > 0) { Emit(set, buf); buf.Clear(); }
        }
        if (buf.Length > 0) Emit(set, buf);
        return set;
    }

    private static void Emit(HashSet<string> set, StringBuilder buf)
    {
        var t = buf.ToString();
        if (!_stopwords.Contains(t)) set.Add(t);
    }

    private static string MapStorefront(string source)
        => !string.IsNullOrEmpty(source) && StorefrontMap.TryGetValue(source, out var s) ? s : "";

    private static int? EffYear(IGame g)
    {
        try { var y = g.ReleaseYear; if (y.HasValue && y.Value > 1950 && y.Value < 2100) return y; } catch { }
        try { var d = g.ReleaseDate; if (d.HasValue && d.Value.Year > 1950 && d.Value.Year < 2100) return d.Value.Year; } catch { }
        return null;
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static int? PosInt(int? v) => v.HasValue && v.Value > 0 ? v : null;
    private static double? PosDouble(double? v) => v.HasValue && v.Value > 0 ? v : null;
}

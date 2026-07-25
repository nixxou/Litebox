// The advanced-search state for the game list — a WinForms port of BigBox-web's `advCrit`
// (web-assets/bigbox/engine/app.js). Same dimensions, defaults and matching semantics:
//   • year / rating ranges, sentinel-at-extreme = "no bound" (∞);
//   • release type (exact), favorite + installed flags;
//   • genres multi-select with OR ("any") / AND ("all"); publisher / developer substring;
//   • sortBy (alpha / year / rating / lastplayed).
// Matching is AND across dimensions; genre internally OR/AND; pub/dev/genre = case-insensitive
// Contains; releaseType = case-insensitive Equals; year needs a parseable year (missing → excluded
// when a year bound is set); rating uses the effective rating (CommunityOrLocalStarRating already
// falls back to community when the user rating is 0).
//
// "Active" = any FILTER dimension is non-default. Sort is NOT counted (the list has its own visible
// sort control), unlike the web where a non-alpha sort also lights the indicator.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Search;

internal sealed class FilterCriteria
{
    // Range bounds (mirror YEAR_B / RATING_B). A handle AT its extreme means "no bound".
    public const int YearLo = 1949;                       // <= this → no lower bound (real years start 1950)
    public static int YearHi => DateTime.Now.Year + 2;    // >= this → no upper bound (last real cran = +1)
    public const double RatingLo = -0.5, RatingHi = 5.5;  // step 0.5; extremes → no bound

    public int YearMin { get; set; } = YearLo;
    public int YearMax { get; set; } = YearHi;
    public double RatingMin { get; set; } = RatingLo;
    public double RatingMax { get; set; } = RatingHi;
    public string ReleaseType { get; set; } = "";
    public bool Fav { get; set; }
    public bool Installed { get; set; }
    public List<string> Genres { get; set; } = new();
    public string GenreMode { get; set; } = "or";         // "or" = ANY, "and" = ALL
    public string Publisher { get; set; } = "";
    public string Developer { get; set; } = "";
    public string SortBy { get; set; } = "alpha";         // alpha | year | rating | lastplayed

    [JsonIgnore] public bool HasYearMin => YearMin > YearLo;
    [JsonIgnore] public bool HasYearMax => YearMax < YearHi;
    [JsonIgnore] public bool HasRatingMin => RatingMin > RatingLo;
    [JsonIgnore] public bool HasRatingMax => RatingMax < RatingHi;

    /// <summary>True when at least one FILTER dimension is set (sort excluded — the toolbar shows sort).</summary>
    [JsonIgnore]
    public bool IsActive =>
        HasYearMin || HasYearMax || HasRatingMin || HasRatingMax
        || !string.IsNullOrEmpty(ReleaseType) || Fav || Installed
        || Genres.Count > 0 || !string.IsNullOrWhiteSpace(Publisher) || !string.IsNullOrWhiteSpace(Developer);

    public FilterCriteria Clone()
    {
        return new FilterCriteria
        {
            YearMin = YearMin, YearMax = YearMax, RatingMin = RatingMin, RatingMax = RatingMax,
            ReleaseType = ReleaseType, Fav = Fav, Installed = Installed,
            Genres = new List<string>(Genres), GenreMode = GenreMode,
            Publisher = Publisher, Developer = Developer, SortBy = SortBy,
        };
    }

    // ── Matching (self-contained + exception-tolerant, like the list's Safe(...) reads) ──
    public bool Matches(IGame g)
    {
        try
        {
            if (HasYearMin || HasYearMax)
            {
                int? y = Try<int?>(() => g.ReleaseYear);
                if (y == null) return false;                 // no year → excluded when a year bound is set
                if (HasYearMin && y.Value < YearMin) return false;
                if (HasYearMax && y.Value > YearMax) return false;
            }
            if (HasRatingMin || HasRatingMax)
            {
                double r = Try(() => (double)g.CommunityOrLocalStarRating, 0.0);
                if (HasRatingMin && r < RatingMin) return false;
                if (HasRatingMax && r > RatingMax) return false;
            }
            if (!string.IsNullOrEmpty(ReleaseType) &&
                !string.Equals(Str(() => g.ReleaseType), ReleaseType, StringComparison.OrdinalIgnoreCase))
                return false;
            if (Fav && !Try(() => g.Favorite, false)) return false;
            if (Installed && Try<bool?>(() => g.Installed) != true) return false;
            if (Genres.Count > 0)
            {
                string gg = Str(() => g.GenresString);
                bool ok = GenreMode == "and"
                    ? Genres.All(x => gg.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)
                    : Genres.Any(x => gg.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!ok) return false;
            }
            if (!string.IsNullOrWhiteSpace(Publisher) &&
                Str(() => g.Publisher).IndexOf(Publisher.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (!string.IsNullOrWhiteSpace(Developer) &&
                Str(() => g.Developer).IndexOf(Developer.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            return true;
        }
        catch { return false; }
    }

    private static T Try<T>(Func<T> f, T fallback = default) { try { return f(); } catch { return fallback; } }
    private static string Str(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    // ── Sort getter for the "Order by" tab (fixed directions, like the web) ──
    // Returns (getter, ascending). alpha → null getter (caller keeps its own sort).
    public (Func<IGame, object>? getter, bool asc) SortSpec()
    {
        switch (SortBy)
        {
            case "year":       return (g => (object)(Try<int?>(() => g.ReleaseYear) ?? int.MinValue), false);        // newest first
            case "rating":     return (g => (object)Try(() => (double)g.CommunityOrLocalStarRating, 0.0), false);     // highest first
            case "lastplayed": return (g => (object)(Try<DateTime?>(() => g.LastPlayedDate) ?? DateTime.MinValue), false); // most recent first
            default:           return (null, true);                                                                  // alpha → leave the list's sort
        }
    }

    // ── Human summary for the History tab (à la advHistoryLabel) ──
    public string Summary()
    {
        var parts = new List<string>();
        if (HasYearMin || HasYearMax)
            parts.Add($"Year {(HasYearMin ? YearMin.ToString() : "∞")}–{(HasYearMax ? YearMax.ToString() : "∞")}");
        if (HasRatingMin || HasRatingMax)
            parts.Add($"Rating {(HasRatingMin ? RatingMin.ToString("0.#") : "∞")}–{(HasRatingMax ? RatingMax.ToString("0.#") : "∞")}");
        if (!string.IsNullOrEmpty(ReleaseType)) parts.Add(ReleaseType);
        if (Fav) parts.Add("★ Favorite");
        if (Installed) parts.Add("Installed");
        if (Genres.Count > 0) parts.Add((GenreMode == "and" ? "& " : "") + string.Join(", ", Genres));
        if (!string.IsNullOrWhiteSpace(Publisher)) parts.Add("Pub: " + Publisher.Trim());
        if (!string.IsNullOrWhiteSpace(Developer)) parts.Add("Dev: " + Developer.Trim());
        if (SortBy != "alpha") parts.Add("↕ " + SortLabel(SortBy));
        return parts.Count == 0 ? "(all games)" : string.Join("  ·  ", parts);
    }

    public static string SortLabel(string s) => s switch
    {
        "year" => "Release date", "rating" => "Rating", "lastplayed" => "Recently played", _ => "Alphabetical",
    };

    // ── Value-equality key for history dedup (only the meaningful, non-default fields) ──
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    public string Key()
    {
        // Normalise: an inactive-bound field is written at its extreme, so two equivalent criteria match.
        var norm = Clone();
        norm.Genres = norm.Genres.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        norm.Publisher = (norm.Publisher ?? "").Trim();
        norm.Developer = (norm.Developer ?? "").Trim();
        return JsonSerializer.Serialize(norm, Json);
    }
}

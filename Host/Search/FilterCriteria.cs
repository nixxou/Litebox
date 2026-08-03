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
using LbApiHost;
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

    // ── Dimensions reprises du menu de filtre de LaunchBox ────────────────────
    // Multi-sélection : OU à l'intérieur d'une dimension, ET entre dimensions — la sémantique de
    // LaunchBox, et déjà celle des genres en mode "or".
    //
    // UNE DIFFÉRENCE ASSUMÉE AVEC LAUNCHBOX : il n'offre que les valeurs présentes dans la
    // plateforme/playlist affichée, parce que son filtre meurt en changeant de nœud. Le nôtre SURVIT au
    // changement de nœud (c'est tout son intérêt), donc restreindre les choix au nœud courant rendrait
    // impossible « les jeux japonais, partout ». Les listes viennent donc de TOUTE la bibliothèque.
    public List<string> Platforms { get; set; } = new();
    public List<string> Regions { get; set; } = new();
    public List<string> PlayModes { get; set; } = new();
    public List<string> Statuses { get; set; } = new();
    public List<string> Progresses { get; set; } = new();
    public List<string> Esrb { get; set; } = new();       // le champ « Rating » de LaunchBox (ESRB/PEGI)
    public List<string> Controllers { get; set; } = new();

    public bool HighScores { get; set; }                  // le jeu peut produire un high score (hiscore.dat)
    public string Achievements { get; set; } = "";        // "" | "yes" | "no"
    public string Saves { get; set; } = "";               // "" | "game" (sauvegarde) | "state" (save state)
    public int MaxPlayers { get; set; }                   // 0 = indifférent, sinon le nombre exact

    [JsonIgnore] public bool HasYearMin => YearMin > YearLo;
    [JsonIgnore] public bool HasYearMax => YearMax < YearHi;
    [JsonIgnore] public bool HasRatingMin => RatingMin > RatingLo;
    [JsonIgnore] public bool HasRatingMax => RatingMax < RatingHi;

    /// <summary>True when at least one FILTER dimension is set (sort excluded — the toolbar shows sort).</summary>
    [JsonIgnore]
    public bool IsActive =>
        HasYearMin || HasYearMax || HasRatingMin || HasRatingMax
        || !string.IsNullOrEmpty(ReleaseType) || Fav || Installed
        || Genres.Count > 0 || !string.IsNullOrWhiteSpace(Publisher) || !string.IsNullOrWhiteSpace(Developer)
        || Platforms.Count > 0 || Regions.Count > 0 || PlayModes.Count > 0 || Statuses.Count > 0
        || Progresses.Count > 0 || Esrb.Count > 0 || Controllers.Count > 0
        || HighScores || Achievements.Length > 0 || Saves.Length > 0 || MaxPlayers > 0;

    public FilterCriteria Clone()
    {
        return new FilterCriteria
        {
            YearMin = YearMin, YearMax = YearMax, RatingMin = RatingMin, RatingMax = RatingMax,
            ReleaseType = ReleaseType, Fav = Fav, Installed = Installed,
            Genres = new List<string>(Genres), GenreMode = GenreMode,
            Publisher = Publisher, Developer = Developer, SortBy = SortBy,
            Platforms = new List<string>(Platforms), Regions = new List<string>(Regions),
            PlayModes = new List<string>(PlayModes), Statuses = new List<string>(Statuses),
            Progresses = new List<string>(Progresses), Esrb = new List<string>(Esrb),
            Controllers = new List<string>(Controllers),
            HighScores = HighScores, Achievements = Achievements, Saves = Saves, MaxPlayers = MaxPlayers,
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
            // « Installed only » = la case Installed COCHÉE, strictement — décision produit : le
            // filtre dit ce que la case dit, ni plus ni moins. Une case jamais renseignée est donc
            // exclue (l'alternative « null = présent », sémantique du champ web `installed`, a été
            // considérée puis écartée). Les jeux de store restent justes : StoreInstallStateSync
            // coche/décoche la case d'après l'état réel des clients GOG/Steam/Epic/Uplay/EA.
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

            // ── Dimensions multi-valeurs. Chaque test est gardé par « la dimension est-elle active ? » :
            // le filtre passe sur chaque jeu à chaque frappe, et certaines lectures (sous-entités) allouent.
            if (Platforms.Count > 0 && !Platforms.Contains(Str(() => g.Platform), Ci)) return false;
            if (Statuses.Count > 0 && !Statuses.Contains(Str(() => g.Status), Ci)) return false;
            if (Progresses.Count > 0 && !Progresses.Contains(Str(() => g.Progress), Ci)) return false;
            if (Esrb.Count > 0 && !Esrb.Contains(Str(() => g.Rating), Ci)) return false;
            // Région et mode de jeu sont MULTI-VALUÉS chez LaunchBox (« Europe; France »), d'où le
            // découpage en jetons plutôt qu'une égalité sur la chaîne entière.
            if (Regions.Count > 0 && !AnyToken(Str(() => g.Region), Regions)) return false;
            if (PlayModes.Count > 0 && !AnyToken(Str(() => g.PlayMode), PlayModes)) return false;

            if (MaxPlayers > 0 && Try<int?>(() => g.MaxPlayers) != MaxPlayers) return false;

            if (Achievements.Length > 0)
            {
                // LiteBox ne connaît que RetroAchievements, via le hash que LaunchBox pose sur le jeu —
                // même source que le champ « Any Achievements » du filtre de playlist.
                bool has = g is Data.HostGame hg && !string.IsNullOrWhiteSpace(Try(() => hg.RetroAchievementsHash, ""));
                if (has != (Achievements == "yes")) return false;
            }

            // La version CACHÉE — Matches tourne sur chaque jeu à chaque frappe, et la question nue
            // referait le tour des émulateurs par jeu. Le cache est invalidé avec les hiscore.dat.
            if (HighScores && !Try(() => GameSortCatalog.MameHighScoresSupported(g), false)) return false;

            if (Saves.Length > 0 && !HasSave(g, wantState: Saves == "state")) return false;

            if (Controllers.Count > 0 && !UsesController(g)) return false;

            return true;
        }
        catch { return false; }
    }

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>Vrai quand l'un des jetons de <paramref name="value"/> (séparés par ';' ou ',') figure dans
    /// la sélection. Comparaison sur la valeur ENTIÈRE du jeton : « Europe » ne doit pas répondre pour
    /// « Eastern Europe ».</summary>
    private static bool AnyToken(string value, List<string> wanted)
    {
        if (value.Length == 0) return false;
        foreach (var tok in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (wanted.Contains(tok.Trim(), Ci)) return true;
        return false;
    }

    /// <summary>Une ligne &lt;GameSave&gt; est un SAVE STATE quand elle porte un Slot numérique — le
    /// discriminant du gestionnaire de sauvegardes (SaveManager.SlotOf), PAS le libellé du groupe :
    /// « My Save State » n'est qu'un nom par défaut, renommable, et un state rebaptisé « Quick
    /// Backup » doit rester un state.</summary>
    internal static bool RowIsState(IReadOnlyDictionary<string, string> row)
        => int.TryParse(row.TryGetValue("Slot", out var s) ? s : null, out _);

    private static bool HasSave(IGame g, bool wantState)
    {
        if (g is not ILiteBoxGame lb) return false;
        try
        {
            foreach (var row in lb.GetSubEntities("GameSave"))
                if (RowIsState(row) == wantState) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Une ligne &lt;GameControllerSupport&gt; VAUT support sauf si son SupportLevel est
    /// l'explicite « 0 = Not Supported » (contrat relevé sur LB 13.28 : absent = cellule vide,
    /// 1 = partiel, 2 = complet, 3 = requis). Sans ce test, un jeu déclaré incompatible avec une
    /// manette répondait présent pour elle.</summary>
    internal static bool RowSupportsController(IReadOnlyDictionary<string, string> row)
        => !(row.TryGetValue("SupportLevel", out var lvl) && lvl?.Trim() == "0");

    /// <summary>Manettes : le jeu porte des &lt;GameControllerSupport&gt; qui ne référencent qu'un
    /// ControllerId ; le nom lisible vient du catalogue Data\GameControllers.xml.</summary>
    private bool UsesController(IGame g)
    {
        if (g is not ILiteBoxGame lb) return false;
        try
        {
            var byId = ControllerNames;
            foreach (var row in lb.GetSubEntities("GameControllerSupport"))
            {
                if (!RowSupportsController(row)) continue;
                if (!row.TryGetValue("ControllerId", out var id) || string.IsNullOrEmpty(id)) continue;
                if (byId.TryGetValue(id, out var name) && Controllers.Contains(name, Ci)) return true;
            }
        }
        catch { }
        return false;
    }

    // Le catalogue de manettes change rarement et le filtre le lit par jeu : résolu une fois, remis à zéro
    // à l'ouverture du dialogue (ResetCaches) pour qu'un ajout de manette soit pris en compte.
    private static Dictionary<string, string>? _ctrlNames;
    private static Dictionary<string, string> ControllerNames
    {
        get
        {
            if (_ctrlNames != null) return _ctrlNames;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var r in Host.ControllerCatalogStore.All())
                    if (!string.IsNullOrEmpty(r.Id) && !string.IsNullOrEmpty(r.Name)) map[r.Id] = r.Name;
            }
            catch { }
            return _ctrlNames = map;
        }
    }

    /// <summary>À appeler quand le catalogue de manettes a pu changer (ouverture du dialogue de filtre).</summary>
    public static void ResetCaches() => _ctrlNames = null;

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
        if (Platforms.Count > 0) parts.Add(string.Join(", ", Platforms));
        if (Regions.Count > 0) parts.Add(string.Join(", ", Regions));
        if (PlayModes.Count > 0) parts.Add(string.Join(", ", PlayModes));
        if (Statuses.Count > 0) parts.Add("Status: " + string.Join(", ", Statuses));
        if (Progresses.Count > 0) parts.Add("Progress: " + string.Join(", ", Progresses));
        if (Esrb.Count > 0) parts.Add("ESRB: " + string.Join(", ", Esrb));
        if (Controllers.Count > 0) parts.Add("Controller: " + string.Join(", ", Controllers));
        if (MaxPlayers > 0) parts.Add(MaxPlayers + " player" + (MaxPlayers > 1 ? "s" : ""));
        if (HighScores) parts.Add("High scores");
        if (Achievements.Length > 0) parts.Add(Achievements == "yes" ? "Achievements" : "No achievements");
        if (Saves.Length > 0) parts.Add(Saves == "state" ? "Has save state" : "Has saved game");
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
        static List<string> Sorted(List<string> l) => l.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        norm.Genres = Sorted(norm.Genres);
        norm.Platforms = Sorted(norm.Platforms); norm.Regions = Sorted(norm.Regions);
        norm.PlayModes = Sorted(norm.PlayModes); norm.Statuses = Sorted(norm.Statuses);
        norm.Progresses = Sorted(norm.Progresses); norm.Esrb = Sorted(norm.Esrb);
        norm.Controllers = Sorted(norm.Controllers);
        norm.Publisher = (norm.Publisher ?? "").Trim();
        norm.Developer = (norm.Developer ?? "").Trim();
        return JsonSerializer.Serialize(norm, Json);
    }
}
